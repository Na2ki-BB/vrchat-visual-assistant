using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>
/// Unconnected audio adapter. Explicit opt-in and an audio-only key snapshot are
/// mandatory; quota ownership belongs to the app, not this replaceable client.
/// </summary>
public sealed class OpenAiVoiceTranscriber : IVoiceTranscriber, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiVoiceTranscriberOptions _options;
    private readonly VoiceInputOptions _voiceInput;
    private readonly OpenAiVoiceCredential? _credential;
    private readonly VoiceRequestQuota _quota;

    public OpenAiVoiceTranscriber(
        OpenAiVoiceTranscriberOptions options,
        VoiceInputOptions voiceInput,
        OpenAiVoiceCredential? credential,
        VoiceRequestQuota quota)
        : this(options, voiceInput, credential, quota, null)
    {
    }

    /// <summary>Handler injection supports secret-free tests; production uses the default constructor.</summary>
    public OpenAiVoiceTranscriber(
        OpenAiVoiceTranscriberOptions options,
        VoiceInputOptions voiceInput,
        OpenAiVoiceCredential? credential,
        VoiceRequestQuota quota,
        HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(voiceInput);
        ArgumentNullException.ThrowIfNull(quota);
        options.Validate();
        voiceInput.Validate();
        _options = options;
        _voiceInput = voiceInput;
        _credential = credential;
        _quota = quota;
        _httpClient = new HttpClient(handler ?? CreateProductionHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };

    public async Task<VoiceTranscription> TranscribeAsync(
        ReadOnlyMemory<byte> waveBytes,
        CancellationToken cancellationToken)
    {
        if (!_voiceInput.IsEnabled)
        {
            throw Failure(ScanFailureCode.VoiceInputDisabled, "音声入力への同意が未設定のため、音声を送信しませんでした。");
        }
        if (_credential?.IsAvailable != true)
        {
            throw Failure(ScanFailureCode.VoiceTranscriptionNotConfigured, "音声専用APIキーが未設定のため、音声を送信しませんでした。");
        }

        int pcmBytes = ValidateWave(waveBytes.Span);
        cancellationToken.ThrowIfCancellationRequested();
        using HttpRequestMessage request = CreateRequest(waveBytes);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        if (!_quota.TryReserve(pcmBytes, out VoiceRequestQuota.VoiceRequestReservation? reservation))
        {
            throw Failure(ScanFailureCode.VoiceUsageLimitReached,
                $"この起動中の音声上限（{_quota.MaximumSeconds}秒・{_quota.MaximumRequests}送信）を超えるため、音声を送信しませんでした。");
        }

        using VoiceRequestQuota.VoiceRequestReservation reserved = reservation;
        try
        {
            // Conservative invocation boundary: transport/auth/timeout/cancel failures
            // after this point count, regardless of the provider's receipt or billing.
            reserved.MarkSendStarted(timeout.Token);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) { throw CreateHttpFailure(response); }
            byte[] body = await ReadBoundedBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            try
            {
                using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
                string text = ExtractText(document.RootElement);
                timeout.Token.ThrowIfCancellationRequested();
                return new VoiceTranscription(text, "OpenAI Audio Transcriptions API", _options.Model);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(ScanFailureCode.VoiceTranscriptionTimedOut,
                $"文字起こしが{_options.Timeout.TotalSeconds:0.###}秒以内に完了しませんでした。");
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
        catch (HttpRequestException)
        {
            throw Failure(ScanFailureCode.VoiceTranscriptionFailed, "文字起こしサービスへ接続できませんでした。");
        }
        catch (IOException)
        {
            throw Failure(ScanFailureCode.VoiceTranscriptionFailed, "文字起こしサービスの応答を受信できませんでした。");
        }
    }

    private int ValidateWave(ReadOnlySpan<byte> wave)
    {
        if (wave.Length > VoiceAudioFormat.WaveHeaderBytes + VoiceAudioFormat.GetPcmByteLimit(_voiceInput.MaximumRecordingSeconds))
        {
            throw Failure(ScanFailureCode.VoiceAudioTooLarge, "録音上限を超えたため、音声を送信しませんでした。");
        }
        if (wave.Length <= VoiceAudioFormat.WaveHeaderBytes
            || !wave[..4].SequenceEqual("RIFF"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(wave[4..]) != wave.Length - 8
            || !wave.Slice(8, 8).SequenceEqual("WAVEfmt "u8)
            || BinaryPrimitives.ReadInt32LittleEndian(wave[16..]) != 16
            || BinaryPrimitives.ReadInt16LittleEndian(wave[20..]) != 1
            || BinaryPrimitives.ReadInt16LittleEndian(wave[22..]) != VoiceAudioFormat.Channels
            || BinaryPrimitives.ReadInt32LittleEndian(wave[24..]) != VoiceAudioFormat.SampleRate
            || BinaryPrimitives.ReadInt32LittleEndian(wave[28..]) != VoiceAudioFormat.BytesPerSecond
            || BinaryPrimitives.ReadInt16LittleEndian(wave[32..]) != VoiceAudioFormat.BlockAlignment
            || BinaryPrimitives.ReadInt16LittleEndian(wave[34..]) != VoiceAudioFormat.BitsPerSample
            || !wave.Slice(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(wave[40..]) != wave.Length - VoiceAudioFormat.WaveHeaderBytes
            || (wave.Length - VoiceAudioFormat.WaveHeaderBytes) % VoiceAudioFormat.BlockAlignment != 0)
        {
            throw Failure(ScanFailureCode.VoiceAudioInvalid, "音声が空または対応するWAV形式ではないため、送信しませんでした。");
        }
        return wave.Length - VoiceAudioFormat.WaveHeaderBytes;
    }

    private HttpRequestMessage CreateRequest(ReadOnlyMemory<byte> waveBytes)
    {
        MultipartFormDataContent multipart = new();
        multipart.Add(new StringContent(_options.Model), "model");
        multipart.Add(new StringContent("json"), "response_format");
        multipart.Add(new StringContent("false"), "stream");
        ReadOnlyMemoryContent audio = new(waveBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audio, "file", "recording.wav");
        HttpRequestMessage request = new(HttpMethod.Post, _options.Endpoint) { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential!.ApiKey);
        request.Headers.UserAgent.ParseAdd("vrchat-visual-assistant/0.1");
        return request;
    }

    private async Task<byte[]> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > _options.ResponseByteLimit)
        {
            throw InvalidResponse();
        }
        byte[] buffer = new byte[_options.ResponseByteLimit + 1];
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int total = 0;
            while (total < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) { return buffer.AsSpan(0, total).ToArray(); }
                total += count;
                if (total > _options.ResponseByteLimit) { throw InvalidResponse(); }
            }
            throw InvalidResponse();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string ExtractText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("text", out JsonElement textElement)
            || textElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }
        string text;
        try { text = textElement.GetString()!; }
        catch (InvalidOperationException) { throw InvalidResponse(); }
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure(ScanFailureCode.VoiceTranscriptionEmpty, "認識できる文章が返りませんでした。録り直してください。");
        }
        if (Encoding.UTF8.GetByteCount(text) > TextInputSession.MaximumTranscriptUtf8Bytes)
        {
            throw InvalidResponse();
        }
        // Preserve the transcript; search-specific transformations belong to a feature.
        return text;
    }

    private static ScanException CreateHttpFailure(HttpResponseMessage response)
    {
        string suffix = string.Empty;
        if (response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? values))
        {
            string? id = values.FirstOrDefault();
            if (id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                suffix = $" (request ID: {id})";
            }
        }
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(
                ScanFailureCode.VoiceAuthenticationFailed, "音声APIの認証に失敗しました。専用キーと利用権限を確認してください。" + suffix),
            HttpStatusCode.TooManyRequests => Failure(
                ScanFailureCode.VoiceRateLimited, "音声APIの利用上限またはレート制限に達しました。" + suffix),
            _ => Failure(ScanFailureCode.VoiceTranscriptionFailed,
                $"文字起こしサービスがHTTP {(int)response.StatusCode}を返しました。" + suffix),
        };
    }

    private static ScanException InvalidResponse() => Failure(
        ScanFailureCode.VoiceTranscriptionInvalidResponse, "文字起こしサービスの応答が不正または上限を超えました。");

    private static ScanException Failure(ScanFailureCode code, string message) => new(code, ScanStage.Transcription, message);

    public void Dispose() => _httpClient.Dispose();
}
