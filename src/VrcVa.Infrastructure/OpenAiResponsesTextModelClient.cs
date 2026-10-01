using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

public sealed class OpenAiResponsesTextModelClient : ITextModelClient
{
    private const string ProviderName = "OpenAI Responses API";
    private const int HardMaximumOutputTokens = 1_200;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(1);

    private readonly HttpClient _httpClient;
    private readonly OpenAiTranslatorOptions _options;
    private readonly string? _apiKey;
    private readonly TextRequestQuota _requestQuota;
    private readonly bool _strictInterpretation;

    public OpenAiResponsesTextModelClient(
        HttpClient httpClient,
        OpenAiTranslatorOptions options,
        string? apiKey)
        : this(httpClient, options, apiKey, CreateRequestQuota(options))
    {
    }

    public OpenAiResponsesTextModelClient(
        HttpClient httpClient,
        OpenAiTranslatorOptions options,
        string? apiKey,
        TextRequestQuota requestQuota)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requestQuota);

        if (options.MaxInputUtf8Bytes is < 1 or > OpenAiTranslatorOptions.DefaultMaxInputUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxInputUtf8Bytes,
                $"OpenAI text input maximum must be between 1 and {OpenAiTranslatorOptions.DefaultMaxInputUtf8Bytes} UTF-8 bytes.");
        }

        if (options.Timeout <= MinimumTimeout
            || options.Timeout > TimeSpan.FromSeconds(OpenAiTranslatorOptions.MaxTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                $"OpenAI timeout must be greater than {MinimumTimeout.TotalMilliseconds:0} millisecond and at most {OpenAiTranslatorOptions.MaxTimeoutSeconds} seconds.");
        }

        if (options.MaxOutputTokens is < 1 or > HardMaximumOutputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxOutputTokens,
                $"OpenAI output token maximum must be between 1 and {HardMaximumOutputTokens}.");
        }

        if (options.MaxRequestsPerSession is < 1 or > TextRequestQuota.HardMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRequestsPerSession,
                $"OpenAI request maximum must be between 1 and {TextRequestQuota.HardMaximum}.");
        }

        _httpClient = httpClient;
        _options = options;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _requestQuota = requestQuota;
    }

    public OpenAiResponsesTextModelClient(
        HttpClient httpClient,
        OpenAiSearchInterpretationOptions options,
        string? apiKey,
        TextRequestQuota requestQuota)
        : this(httpClient, GetInterpretationTransport(options, requestQuota), apiKey, requestQuota)
    {
        _strictInterpretation = true;
    }

    private static OpenAiTranslatorOptions GetInterpretationTransport(
        OpenAiSearchInterpretationOptions options, TextRequestQuota requestQuota)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ToTransportOptions(requestQuota);
    }

    private ScanFailureCode FailureCode(ScanFailureCode translation, ScanFailureCode interpretation) =>
        _strictInterpretation ? interpretation : translation;

    private string PurposeName => _requestQuota.Purpose == TextRequestPurpose.Translation
        ? "翻訳"
        : "検索AI解釈";

    private ScanStage FailureStage => _requestQuota.Purpose == TextRequestPurpose.Translation
        ? ScanStage.Translation
        : _strictInterpretation ? ScanStage.SearchInterpretation : ScanStage.TextHandling;

    public TextRequestPurpose Purpose => _requestQuota.Purpose;

    public int MaxRequestsPerSession => _requestQuota.Maximum;

    public int RemainingRequests => _requestQuota.Remaining;

    public async Task<TextModelResponse> GenerateAsync(
        TextModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string model = ValidateModel(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        if (request.MaxOutputTokens <= 0 || request.MaxOutputTokens > _options.MaxOutputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.MaxOutputTokens,
                $"MaxOutputTokens must be between 1 and {_options.MaxOutputTokens}.");
        }

        if (_apiKey is null)
        {
            throw new ScanException(
                FailureCode(ScanFailureCode.TranslationNotConfigured, ScanFailureCode.SearchInterpretationNotConfigured),
                FailureStage,
                $"{PurposeName}APIキーが未設定です。専用APIキーの設定を確認してください。");
        }

        int inputUtf8Bytes;
        try
        {
            inputUtf8Bytes = (_strictInterpretation ? StrictUtf8 : Encoding.UTF8).GetByteCount(request.Input);
        }
        catch (EncoderFallbackException)
        {
            throw new ScanException(ScanFailureCode.SearchInterpretationInvalidInput,
                ScanStage.SearchInterpretation, "検索AI解釈の入力に不正な文字があるため、外部送信を停止しました。");
        }
        if (inputUtf8Bytes > _options.MaxInputUtf8Bytes)
        {
            throw new ScanException(
                FailureCode(ScanFailureCode.TranslationInputTooLarge, ScanFailureCode.SearchInterpretationInputTooLarge),
                FailureStage,
                $"{PurposeName}入力が上限（UTF-8で{_options.MaxInputUtf8Bytes:N0}バイト）を超えたため、外部送信を停止しました。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using HttpRequestMessage httpRequest = CreateRequest(request, model);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        if (!_requestQuota.TryReserve(out TextRequestQuota.TextRequestReservation? reservation))
        {
            throw new ScanException(
                _requestQuota.Purpose == TextRequestPurpose.Translation
                    ? ScanFailureCode.TranslationUsageLimitReached
                    : ScanFailureCode.SearchInterpretationUsageLimitReached,
                FailureStage,
                $"この起動中の{PurposeName}上限（{_requestQuota.Maximum}回）に達したため、外部送信を停止しました。必要ならアプリを再起動してください。");
        }

        using TextRequestQuota.TextRequestReservation reservedRequest = reservation;
        HttpResponseMessage response;
        try
        {
            // This invocation boundary is conservative: an attempted send counts even
            // when its transport fails before a provider confirms receipt.
            reservedRequest.MarkSendStarted(timeout.Token);
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            throw CreateConnectionFailure(exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string? requestId = response.Headers.TryGetValues(
                    "x-request-id",
                    out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null;
                throw CreateHttpFailure(response.StatusCode, requestId);
            }

            try
            {
                await using Stream responseStream = await response.Content
                    .ReadAsStreamAsync(timeout.Token)
                    .ConfigureAwait(false);
                using JsonDocument document = _strictInterpretation
                    ? await ReadBoundedInterpretationResponseAsync(responseStream, timeout.Token).ConfigureAwait(false)
                    : await JsonDocument.ParseAsync(responseStream, cancellationToken: timeout.Token).ConfigureAwait(false);

                string outputText = _strictInterpretation
                    ? ExtractInterpretationText(document.RootElement)
                    : ExtractOutputText(document.RootElement);
                if (string.IsNullOrWhiteSpace(outputText))
                {
                    throw new ScanException(
                        FailureCode(ScanFailureCode.TranslationFailed, ScanFailureCode.SearchInterpretationFailed),
                        FailureStage,
                        $"{PurposeName}サービスからテキスト結果が返りませんでした。");
                }

                return new TextModelResponse(
                    outputText.Trim(),
                    ProviderName,
                    model);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateTimeoutFailure(exception);
            }
            catch (HttpRequestException exception)
            {
                throw CreateConnectionFailure(exception);
            }
            catch (InvalidOperationException exception) when (_strictInterpretation)
            {
                throw new ScanException(ScanFailureCode.SearchInterpretationInvalidResponse,
                    ScanStage.SearchInterpretation, "検索AI解釈サービスの応答形式を解釈できませんでした。", exception);
            }
            catch (JsonException exception)
            {
                throw new ScanException(
                    FailureCode(ScanFailureCode.TranslationFailed, ScanFailureCode.SearchInterpretationInvalidResponse),
                    FailureStage,
                    $"{PurposeName}サービスの応答形式を解釈できませんでした。",
                    exception);
            }
        }
    }

    private static TextRequestQuota CreateRequestQuota(OpenAiTranslatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TextRequestQuota(options.MaxRequestsPerSession);
    }

    private string ValidateModel(string model)
    {
        string value = string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
        if (!(_strictInterpretation
            ? OpenAiSearchInterpretationOptions.IsSupportedModel(value)
            : OpenAiTranslatorOptions.IsSupportedModel(value)))
        {
            throw new ArgumentException(
                _strictInterpretation
                    ? $"Search interpretation model must be {OpenAiSearchInterpretationOptions.DefaultModel}."
                    : $"OpenAI model must be {OpenAiTranslatorOptions.QualityModel} or {OpenAiTranslatorOptions.BudgetModel}.",
                nameof(model));
        }

        return value;
    }

    private HttpRequestMessage CreateRequest(TextModelRequest request, string model)
    {
        var payload = new
        {
            model,
            store = false,
            reasoning = new
            {
                effort = "none",
            },
            instructions = request.Instructions,
            input = request.Input,
            max_output_tokens = request.MaxOutputTokens,
        };

        string json = JsonSerializer.Serialize(payload);
        HttpRequestMessage httpRequest = new(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("vrchat-visual-assistant/0.1");
        return httpRequest;
    }

    private ScanException CreateTimeoutFailure(OperationCanceledException exception) =>
        new(
            FailureCode(ScanFailureCode.TranslationTimedOut, ScanFailureCode.SearchInterpretationTimedOut),
            FailureStage,
            $"{PurposeName}が{_options.Timeout.TotalSeconds:0}秒以内に完了しませんでした。",
            exception);

    private ScanException CreateConnectionFailure(HttpRequestException exception) =>
        new(
            FailureCode(ScanFailureCode.TranslationFailed, ScanFailureCode.SearchInterpretationFailed),
            FailureStage,
            $"{PurposeName}サービスへ接続できませんでした。ネットワーク接続を確認してください。",
            exception);

    private static async Task<JsonDocument> ReadBoundedInterpretationResponseAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] bytes = new byte[4_096];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > OpenAiSearchInterpretationOptions.MaximumResponseBytes)
            {
                throw InvalidInterpretationResponse();
            }
            buffer.Write(bytes, 0, read);
        }
        return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
    }

    private static ScanException InvalidInterpretationResponse() => new(
        ScanFailureCode.SearchInterpretationInvalidResponse, ScanStage.SearchInterpretation,
        "検索AI解釈サービスから有効なテキスト応答が返りませんでした。");

    private static string ExtractInterpretationText(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String || status.GetString() != "completed"
            || (root.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
            || (root.TryGetProperty("incomplete_details", out JsonElement incomplete) && incomplete.ValueKind != JsonValueKind.Null)
            || !root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array || output.GetArrayLength() != 1)
        {
            throw InvalidInterpretationResponse();
        }
        JsonElement message = output[0];
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("type", out JsonElement type)
            || type.ValueKind != JsonValueKind.String || type.GetString() != "message"
            || (message.TryGetProperty("status", out status)
                && (status.ValueKind != JsonValueKind.String || status.GetString() != "completed"))
            || (message.TryGetProperty("role", out JsonElement role)
                && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"))
            || !message.TryGetProperty("content", out JsonElement content)
            || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
        {
            throw InvalidInterpretationResponse();
        }
        JsonElement part = content[0];
        if (part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out type)
            || type.ValueKind != JsonValueKind.String || type.GetString() != "output_text"
            || !part.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
        {
            throw InvalidInterpretationResponse();
        }
        // Validate before any downstream search. Tool/candidate output is never adopted.
        return new SearchQueryInterpretation(text.GetString()!).Query;
    }

    private static string ExtractOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement contentItem in content.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out JsonElement type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "output_text"
                    || !contentItem.TryGetProperty("text", out JsonElement text)
                    || text.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private ScanException CreateHttpFailure(
        HttpStatusCode statusCode,
        string? requestId)
    {
        string requestIdSuffix = string.IsNullOrWhiteSpace(requestId)
            ? string.Empty
            : $" (request ID: {requestId})";

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ScanException(
                FailureCode(ScanFailureCode.TranslationAuthenticationFailed, ScanFailureCode.SearchInterpretationAuthenticationFailed),
                FailureStage,
                $"{PurposeName}APIの認証に失敗しました。APIキーと利用権限を確認してください。{requestIdSuffix}"),
            HttpStatusCode.TooManyRequests => new ScanException(
                FailureCode(ScanFailureCode.TranslationRateLimited, ScanFailureCode.SearchInterpretationRateLimited),
                FailureStage,
                $"{PurposeName}APIの利用上限またはレート制限に達しました。少し待ってから再試行してください。{requestIdSuffix}"),
            _ => new ScanException(
                FailureCode(ScanFailureCode.TranslationFailed, ScanFailureCode.SearchInterpretationFailed),
                FailureStage,
                $"{PurposeName}サービスがHTTP {(int)statusCode}を返しました。{requestIdSuffix}"),
        };
    }
}
