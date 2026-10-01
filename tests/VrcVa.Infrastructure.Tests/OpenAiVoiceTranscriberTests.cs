using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class OpenAiVoiceTranscriberTests
{
    [Fact]
    public void ProductionTransportCannotRedirectAudioOrSendCookiesAndBoundsAreExplicit()
    {
        using SocketsHttpHandler handler = OpenAiVoiceTranscriber.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        OpenAiVoiceTranscriberOptions defaults = new();
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.Timeout);
        Assert.Equal(65536, defaults.ResponseByteLimit);
        Assert.Equal("gpt-transcribe", defaults.Model);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", defaults.Endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AudioLimitBeforeExactAndOverBlocksHttpWithoutSpendingIndependentTextBudgets(bool secondsBound)
    {
        FeatureUsageQuotas quotas = new();
        RecordingHandler handler = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quotas.Voice, voice: new()
        {
            IsEnabled = true,
            MaximumRecordingSeconds = 120,
        });
        if (secondsBound)
        {
            foreach (int seconds in new[] { 120, 120, 59 })
            {
                await client.TranscribeAsync(CreateWave(seconds * 32000), default);
            }
            Assert.Equal(1, quotas.Voice.RemainingSeconds);
            ScanException oversized = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(32002), default));
            AssertFailure(oversized, ScanFailureCode.VoiceUsageLimitReached);
            Assert.Equal(3, handler.SendCount);
            await client.TranscribeAsync(CreateWave(32000), default);
            Assert.Equal(300, quotas.Voice.ConsumedSeconds);
            Assert.Equal(26, quotas.Voice.RemainingRequests);
        }
        else
        {
            for (int index = 0; index < 29; index++) { await client.TranscribeAsync(CreateWave(2), default); }
            Assert.Equal(1, quotas.Voice.RemainingRequests);
            await client.TranscribeAsync(CreateWave(2), default);
            Assert.Equal(30, quotas.Voice.ConsumedRequests);
            Assert.Equal(270, quotas.Voice.RemainingSeconds);
        }
        int sendsAtLimit = handler.SendCount;
        ScanException excess = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        AssertFailure(excess, ScanFailureCode.VoiceUsageLimitReached);
        Assert.Equal(sendsAtLimit, handler.SendCount);
        Assert.Equal(10, quotas.Translation.Remaining);
        Assert.Equal(10, quotas.SearchInterpretation.Remaining);
    }

    [Fact]
    public async Task Request_IsOfficialMultipartBorrowedCanonicalWaveAndPreservesTranscript()
    {
        byte[] wave = CreateWave(32_002);
        RecordingHandler handler = new("{\"text\":\"  synthetic transcript\\n続き  \",\"languages\":[]}");
        FeatureUsageQuotas quotas = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quotas.Voice);
        VoiceTranscription result = await client.TranscribeAsync(wave, CancellationToken.None);

        Assert.Equal("  synthetic transcript\n続き  ", result.Text);
        Assert.Equal("gpt-transcribe", result.Model);
        Assert.Equal("OpenAI Audio Transcriptions API", result.Provider);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(OpenAiVoiceTranscriberOptions.OfficialEndpoint, handler.Uri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("synthetic-audio-only-key", handler.AuthValue);
        Assert.Equal("multipart/form-data", handler.ContentType);
        Assert.Equal(4, handler.Parts.Count);
        Assert.Equal("gpt-transcribe", Encoding.UTF8.GetString(handler.Parts["model"].Bytes));
        Assert.Equal("json", Encoding.UTF8.GetString(handler.Parts["response_format"].Bytes));
        Assert.Equal("false", Encoding.UTF8.GetString(handler.Parts["stream"].Bytes));
        Assert.Equal("recording.wav", handler.Parts["file"].FileName);
        Assert.Equal("audio/wav", handler.Parts["file"].MediaType);
        Assert.Equal(wave, handler.Parts["file"].Bytes);
        Assert.False(handler.Parts.ContainsKey("store"));
        Assert.Equal(2, quotas.Voice.ConsumedSeconds);
        Assert.Equal(1, quotas.Voice.ConsumedRequests);
        Assert.Equal(0, quotas.Translation.Consumed);
        Assert.Equal(0, quotas.SearchInterpretation.Consumed);
    }

    [Theory]
    [InlineData(false, "synthetic-audio-only-key", ScanFailureCode.VoiceInputDisabled)]
    [InlineData(false, null, ScanFailureCode.VoiceInputDisabled)]
    [InlineData(true, null, ScanFailureCode.VoiceTranscriptionNotConfigured)]
    [InlineData(true, " ", ScanFailureCode.VoiceTranscriptionNotConfigured)]
    public async Task OptInAndDedicatedKeyAreBothRequiredBeforeAnySend(bool enabled, string? key, ScanFailureCode code)
    {
        RecordingHandler handler = new();
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = new(new(), new() { IsEnabled = enabled },
            key is null ? null : new(key), quota, handler);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, code);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, quota.ConsumedRequests);
        Assert.Equal(300, quota.RemainingSeconds);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("headerOnly")]
    [InlineData("truncated")]
    [InlineData("riff")]
    [InlineData("riffSize")]
    [InlineData("wave")]
    [InlineData("fmt")]
    [InlineData("fmtSize")]
    [InlineData("encoding")]
    [InlineData("channels")]
    [InlineData("sampleRate")]
    [InlineData("byteRate")]
    [InlineData("align")]
    [InlineData("bits")]
    [InlineData("data")]
    [InlineData("dataSize")]
    [InlineData("partialSample")]
    [InlineData("extraChunk")]
    public async Task InvalidCanonicalWaveIsRejectedWithoutReservationOrSend(string variant)
    {
        byte[] wave = CreateWave(32_000);
        Dictionary<string, int> offsets = new()
        {
            ["riff"] = 0,
            ["riffSize"] = 4,
            ["wave"] = 8,
            ["fmt"] = 12,
            ["fmtSize"] = 16,
            ["encoding"] = 20,
            ["channels"] = 22,
            ["sampleRate"] = 24,
            ["byteRate"] = 28,
            ["align"] = 32,
            ["bits"] = 34,
            ["data"] = 36,
            ["dataSize"] = 40,
        };
        if (offsets.TryGetValue(variant, out int offset)) { wave[offset] ^= 1; }
        else
        {
            wave = variant switch
            {
                "empty" => [],
                "headerOnly" => CreateWave(0),
                "truncated" => wave[..20],
                "partialSample" => CreateWave(3),
                "extraChunk" => [.. wave, 0, 0],
                _ => throw new InvalidOperationException(),
            };
        }
        RecordingHandler handler = new();
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(wave, default));
        AssertFailure(failure, ScanFailureCode.VoiceAudioInvalid);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, quota.ConsumedSeconds);
        Assert.Equal(30, quota.RemainingRequests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    public async Task WaveSizeUsesConfiguredRecordingLimitAndAllowsExactBoundary(int maximumSeconds)
    {
        VoiceRequestQuota quota = new(3600, 300);
        RecordingHandler handler = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota, voice: new()
        {
            IsEnabled = true,
            MaximumRecordingSeconds = maximumSeconds,
        });
        await client.TranscribeAsync(CreateWave(maximumSeconds * 32_000), default);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() =>
            client.TranscribeAsync(CreateWave(maximumSeconds * 32_000 + 2), default));
        AssertFailure(failure, ScanFailureCode.VoiceAudioTooLarge);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(maximumSeconds, quota.ConsumedSeconds);
    }

    [Theory]
    [InlineData("{", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("null", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("[]", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("{}", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("{\"text\":null}", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("{\"text\":42}", ScanFailureCode.VoiceTranscriptionInvalidResponse)]
    [InlineData("{\"text\":\"\"}", ScanFailureCode.VoiceTranscriptionEmpty)]
    [InlineData("{\"text\":\" \\n\\t\"}", ScanFailureCode.VoiceTranscriptionEmpty)]
    public async Task InvalidOrEmptyJsonCountsAnAttemptAndDoesNotRetry(string body, ScanFailureCode code)
    {
        RecordingHandler handler = new(body);
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, code);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData("highSurrogate")]
    [InlineData("lowSurrogate")]
    [InlineData("invalidUtf8")]
    public async Task MalformedUnicodeIsATypedInvalidResponseWithoutContentLeakage(string variant)
    {
        byte[] body = variant switch
        {
            "highSurrogate" => Encoding.UTF8.GetBytes("{\"text\":\"private-synthetic-\\uD800\"}"),
            "lowSurrogate" => Encoding.UTF8.GetBytes("{\"text\":\"private-synthetic-\\uDC00\"}"),
            "invalidUtf8" => [.. "{\"text\":\"private-synthetic-"u8.ToArray(), 0xff, .. "\"}"u8.ToArray()],
            _ => throw new InvalidOperationException(),
        };
        RawBodyHandler handler = new(body);
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, ScanFailureCode.VoiceTranscriptionInvalidResponse);
        Assert.DoesNotContain("private-synthetic", failure.ToString());
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseBodyLimitAllowsExactAndRejectsOneOverIncludingUnknownLength(bool unknownLength)
    {
        string body = "{\"text\":\"synthetic\"}";
        int bytes = Encoding.UTF8.GetByteCount(body);
        RecordingHandler accepted = new(body, unknownLength: unknownLength);
        RecordingHandler rejected = new(body + " ", unknownLength: unknownLength);
        VoiceRequestQuota quota = new();
        OpenAiVoiceTranscriberOptions options = new() { ResponseByteLimit = bytes };
        using OpenAiVoiceTranscriber first = CreateClient(accepted, quota, options);
        using OpenAiVoiceTranscriber second = CreateClient(rejected, quota, options);
        Assert.Equal("synthetic", (await first.TranscribeAsync(CreateWave(2), default)).Text);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => second.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, ScanFailureCode.VoiceTranscriptionInvalidResponse);
        Assert.Equal(2, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData("a", 4000, true)]
    [InlineData("a", 4001, false)]
    [InlineData("あ", 1333, true)]
    [InlineData("あ", 1334, false)]
    public async Task TranscriptUtf8LimitIsExactAndNeverTruncated(string unit, int count, bool accepted)
    {
        string transcript = string.Concat(Enumerable.Repeat(unit, count));
        RecordingHandler handler = new(JsonSerializer.Serialize(new { text = transcript }));
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        if (accepted) { Assert.Equal(transcript, (await client.TranscribeAsync(CreateWave(2), default)).Text); }
        else
        {
            ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
            AssertFailure(failure, ScanFailureCode.VoiceTranscriptionInvalidResponse);
        }
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData(401, ScanFailureCode.VoiceAuthenticationFailed)]
    [InlineData(403, ScanFailureCode.VoiceAuthenticationFailed)]
    [InlineData(429, ScanFailureCode.VoiceRateLimited)]
    [InlineData(500, ScanFailureCode.VoiceTranscriptionFailed)]
    [InlineData(302, ScanFailureCode.VoiceTranscriptionFailed)]
    [InlineData(307, ScanFailureCode.VoiceTranscriptionFailed)]
    [InlineData(308, ScanFailureCode.VoiceTranscriptionFailed)]
    public async Task HttpFailuresNeverReadEchoedBodyOrRetryAndExplicitManualRetryCountsAgain(int status, ScanFailureCode code)
    {
        RecordingHandler handler = new("private-synthetic-echo", (HttpStatusCode)status, requestId: "req_synthetic-1");
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(32_002), default));
            AssertFailure(failure, code);
            Assert.Contains("req_synthetic-1", failure.UserMessage);
            Assert.DoesNotContain("private-synthetic-echo", failure.ToString());
            Assert.DoesNotContain("synthetic-audio-only-key", failure.ToString());
            Assert.Equal(attempt, handler.SendCount);
            Assert.Equal(attempt * 2, quota.ConsumedSeconds);
            Assert.Equal(attempt, quota.ConsumedRequests);
        }
    }

    [Fact]
    public async Task UntrustedRequestIdAndTransportExceptionContentAreNotExposed()
    {
        RecordingHandler handler = new(status: HttpStatusCode.BadRequest, requestId: "private synthetic content");
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        Assert.DoesNotContain("private synthetic content", failure.ToString());
        using OpenAiVoiceTranscriber broken = CreateClient(new ThrowingHandler(), quota);
        ScanException connection = await Assert.ThrowsAsync<ScanException>(() => broken.TranscribeAsync(CreateWave(2), default));
        AssertFailure(connection, ScanFailureCode.VoiceTranscriptionFailed);
        Assert.DoesNotContain("private synthetic transport content", connection.ToString());
        Assert.Equal(2, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutIncludesSendAndResponseBodyAndKeepsConsumption(bool duringBody)
    {
        WaitingHandler handler = new(duringBody);
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota, new() { Timeout = TimeSpan.FromMilliseconds(100) });
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(32_002), default));
        AssertFailure(failure, ScanFailureCode.VoiceTranscriptionTimedOut);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(2, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationIsPropagatedAfterSendAndBodyCleanup(bool duringBody)
    {
        WaitingHandler handler = new(duringBody);
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        using CancellationTokenSource cancellation = new();
        Task<VoiceTranscription> pending = client.TranscribeAsync(CreateWave(32_002), cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(2, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
        if (duringBody) { Assert.True(handler.Stream!.Disposed); }
    }

    [Fact]
    public async Task LateSuccessAfterCallerCancellationCannotReturnAStaleTranscript()
    {
        LateSuccessHandler handler = new();
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        using CancellationTokenSource cancellation = new();
        Task<VoiceTranscription> pending = client.TranscribeAsync(CreateWave(2), cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, quota.ConsumedSeconds);
        Assert.Equal(1, quota.ConsumedRequests);
    }

    [Fact]
    public async Task ErrorStatusNeverOpensOrReadsItsResponseBody()
    {
        ErrorBodyHandler handler = new();
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => client.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, ScanFailureCode.VoiceAuthenticationFailed);
        Assert.False(handler.Body.ReadStarted);
        Assert.True(handler.Body.Disposed);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotReserveOrSend()
    {
        RecordingHandler handler = new();
        VoiceRequestQuota quota = new();
        using OpenAiVoiceTranscriber client = CreateClient(handler, quota);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TranscribeAsync(CreateWave(2), cancellation.Token));
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, quota.ConsumedRequests);
        Assert.Equal(300, quota.RemainingSeconds);
    }

    [Fact]
    public async Task RebuiltClientAndSettingsReloadCannotResetAudioOrTextCounters()
    {
        FeatureUsageQuotas quotas = new(new() { VoiceSeconds = 2, VoiceRequests = 2 });
        using OpenAiVoiceTranscriber first = CreateClient(new RecordingHandler(), quotas.Voice);
        await first.TranscribeAsync(CreateWave(2), default);
        VoiceRequestQuota original = quotas.Voice;
        quotas.ApplyLimits(quotas.Limits with { VoiceSeconds = 1, VoiceRequests = 1 });
        RecordingHandler handler = new();
        using OpenAiVoiceTranscriber second = CreateClient(handler, quotas.Voice);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() => second.TranscribeAsync(CreateWave(2), default));
        AssertFailure(failure, ScanFailureCode.VoiceUsageLimitReached);
        Assert.Equal(0, handler.SendCount);
        quotas.ApplyLimits(quotas.Limits with { VoiceSeconds = 2, VoiceRequests = 2 });
        await second.TranscribeAsync(CreateWave(2), default);
        Assert.Same(original, quotas.Voice);
        Assert.Equal(2, quotas.Voice.ConsumedSeconds);
        Assert.Equal(2, quotas.Voice.ConsumedRequests);
        Assert.Equal(10, quotas.Translation.Remaining);
        Assert.Equal(10, quotas.SearchInterpretation.Remaining);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("host")]
    [InlineData("http")]
    [InlineData("fragment")]
    [InlineData("userinfo")]
    [InlineData("query")]
    [InlineData("relative")]
    [InlineData("nullEndpoint")]
    [InlineData("timeoutZero")]
    [InlineData("timeoutOneMs")]
    [InlineData("timeoutOver")]
    [InlineData("bodyZero")]
    [InlineData("bodyOver")]
    public void OptionsRejectUnapprovedModelEndpointAndUnboundedLimits(string variant)
    {
        OpenAiVoiceTranscriberOptions options = variant switch
        {
            "model" => new() { Model = "gpt-4o-transcribe" },
            "host" => new() { Endpoint = new("https://example.test/v1/audio/transcriptions") },
            "http" => new() { Endpoint = new("http://api.openai.com/v1/audio/transcriptions") },
            "fragment" => new() { Endpoint = new("https://api.openai.com/v1/audio/transcriptions#extra") },
            "userinfo" => new() { Endpoint = new("https://someone@api.openai.com/v1/audio/transcriptions") },
            "query" => new() { Endpoint = new("https://api.openai.com/v1/audio/transcriptions?extra=1") },
            "relative" => new() { Endpoint = new("/audio", UriKind.Relative) },
            "nullEndpoint" => new() { Endpoint = null! },
            "timeoutZero" => new() { Timeout = TimeSpan.Zero },
            "timeoutOneMs" => new() { Timeout = TimeSpan.FromMilliseconds(1) },
            "timeoutOver" => new() { Timeout = TimeSpan.FromSeconds(60.001) },
            "bodyZero" => new() { ResponseByteLimit = 0 },
            "bodyOver" => new() { ResponseByteLimit = 65537 },
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void DedicatedCredentialRedactsAndCannotEnableAudio()
    {
        OpenAiVoiceCredential credential = new("  synthetic-audio-only-key  ");
        Assert.True(credential.IsAvailable);
        Assert.DoesNotContain("synthetic-audio-only-key", credential.ToString());
        Assert.Equal("VrcVa/OpenAI/Voice", OpenAiVoiceCredential.CredentialTarget);
        Assert.False(new VoiceInputOptions().IsEnabled);
        Assert.False(new OpenAiVoiceCredential(null).IsAvailable);
        Assert.Throws<ArgumentException>(() => new OpenAiVoiceCredential("bad\r\nsynthetic"));
    }

    private static OpenAiVoiceTranscriber CreateClient(HttpMessageHandler handler, VoiceRequestQuota quota,
        OpenAiVoiceTranscriberOptions? options = null, VoiceInputOptions? voice = null) =>
        new(options ?? new(), voice ?? new() { IsEnabled = true }, new("synthetic-audio-only-key"), quota, handler);

    private static void AssertFailure(ScanException failure, ScanFailureCode code)
    {
        Assert.Equal(code, failure.FailureCode);
        Assert.Equal(ScanStage.Transcription, failure.Stage);
    }

    internal static byte[] CreateWave(int pcmBytes)
    {
        byte[] wave = new byte[44 + pcmBytes];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), pcmBytes + 36);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), pcmBytes);
        wave.AsSpan(44).Fill(42);
        return wave;
    }

    private sealed record Part(byte[] Bytes, string? FileName, string? MediaType);
    private sealed class RecordingHandler(string body = "{\"text\":\"synthetic transcript\"}",
        HttpStatusCode status = HttpStatusCode.OK, bool unknownLength = false, string? requestId = null) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthValue { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, Part> Parts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            Method = request.Method;
            Uri = request.RequestUri;
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthValue = request.Headers.Authorization?.Parameter;
            ContentType = request.Content!.Headers.ContentType?.MediaType;
            Parts.Clear();
            foreach (HttpContent part in Assert.IsType<MultipartFormDataContent>(request.Content))
            {
                ContentDispositionHeaderValue disposition = part.Headers.ContentDisposition!;
                Parts.Add(disposition.Name!.Trim('"'), new(await part.ReadAsByteArrayAsync(cancellationToken),
                    disposition.FileName?.Trim('"'), part.Headers.ContentType?.MediaType));
            }
            HttpResponseMessage response = new(status)
            {
                Content = unknownLength ? new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(body))) : new StringContent(body),
            };
            if (unknownLength) { response.Content.Headers.ContentLength = null; }
            if (requestId is not null) { response.Headers.TryAddWithoutValidation("x-request-id", requestId); }
            if ((int)status is >= 300 and < 400) { response.Headers.Location = new("https://example.test/redirect"); }
            return response;
        }
    }

    private sealed class RawBodyHandler(byte[] body) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("private synthetic transport content");
    }

    private sealed class LateSuccessHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"synthetic late success\"}") };
        }
    }

    private sealed class ErrorBodyHandler : HttpMessageHandler
    {
        public UnreadableContent Body { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = Body });
    }

    private sealed class UnreadableContent : HttpContent
    {
        public bool ReadStarted { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadStarted = true;
            throw new InvalidOperationException("Error response content must not be read.");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class WaitingHandler(bool duringBody) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WaitingStream? Stream { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            if (duringBody)
            {
                Stream = new WaitingStream(Entered);
                return new(HttpStatusCode.OK) { Content = new StreamContent(Stream) };
            }
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class WaitingStream(TaskCompletionSource entered) : Stream
    {
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
