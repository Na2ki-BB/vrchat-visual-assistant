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
    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(1);

    private readonly HttpClient _httpClient;
    private readonly OpenAiTranslatorOptions _options;
    private readonly string? _apiKey;
    private readonly TranslationRequestQuota _requestQuota;

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
        TranslationRequestQuota requestQuota)
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

        _httpClient = httpClient;
        _options = options;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _requestQuota = requestQuota;
    }

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
                ScanFailureCode.TranslationNotConfigured,
                ScanStage.Translation,
                "翻訳APIキーが未設定です。VRCVA画面のOpenAI APIキー欄から登録してください。");
        }

        int inputUtf8Bytes = Encoding.UTF8.GetByteCount(request.Input);
        if (inputUtf8Bytes > _options.MaxInputUtf8Bytes)
        {
            throw new ScanException(
                ScanFailureCode.TranslationInputTooLarge,
                ScanStage.Translation,
                $"OCRテキストが翻訳上限（UTF-8で{_options.MaxInputUtf8Bytes:N0}バイト）を超えたため、外部送信を停止しました。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using HttpRequestMessage httpRequest = CreateRequest(request, model);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        if (!_requestQuota.TryReserve())
        {
            throw new ScanException(
                ScanFailureCode.TranslationUsageLimitReached,
                ScanStage.Translation,
                $"この起動中の翻訳上限（{_requestQuota.Maximum}回）に達したため、外部送信を停止しました。必要ならアプリを再起動してください。");
        }

        HttpResponseMessage response;
        try
        {
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
                using JsonDocument document = await JsonDocument
                    .ParseAsync(responseStream, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);

                string outputText = ExtractOutputText(document.RootElement);
                if (string.IsNullOrWhiteSpace(outputText))
                {
                    throw new ScanException(
                        ScanFailureCode.TranslationFailed,
                        ScanStage.Translation,
                        "翻訳サービスからテキスト結果が返りませんでした。");
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
            catch (JsonException exception)
            {
                throw new ScanException(
                    ScanFailureCode.TranslationFailed,
                    ScanStage.Translation,
                    "翻訳サービスの応答形式を解釈できませんでした。",
                    exception);
            }
        }
    }

    private static TranslationRequestQuota CreateRequestQuota(OpenAiTranslatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TranslationRequestQuota(options.MaxRequestsPerSession);
    }

    private static string ValidateModel(string model)
    {
        string value = string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
        if (!OpenAiTranslatorOptions.IsSupportedModel(value))
        {
            throw new ArgumentException(
                $"OpenAI model must be {OpenAiTranslatorOptions.QualityModel} or {OpenAiTranslatorOptions.BudgetModel}.",
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
            ScanFailureCode.TranslationTimedOut,
            ScanStage.Translation,
            $"翻訳が{_options.Timeout.TotalSeconds:0}秒以内に完了しませんでした。",
            exception);

    private static ScanException CreateConnectionFailure(HttpRequestException exception) =>
        new(
            ScanFailureCode.TranslationFailed,
            ScanStage.Translation,
            "翻訳サービスへ接続できませんでした。ネットワーク接続を確認してください。",
            exception);

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

    private static ScanException CreateHttpFailure(
        HttpStatusCode statusCode,
        string? requestId)
    {
        string requestIdSuffix = string.IsNullOrWhiteSpace(requestId)
            ? string.Empty
            : $" (request ID: {requestId})";

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ScanException(
                ScanFailureCode.TranslationAuthenticationFailed,
                ScanStage.Translation,
                $"翻訳APIの認証に失敗しました。APIキーと利用権限を確認してください。{requestIdSuffix}"),
            HttpStatusCode.TooManyRequests => new ScanException(
                ScanFailureCode.TranslationRateLimited,
                ScanStage.Translation,
                $"翻訳APIの利用上限またはレート制限に達しました。少し待ってから再試行してください。{requestIdSuffix}"),
            _ => new ScanException(
                ScanFailureCode.TranslationFailed,
                ScanStage.Translation,
                $"翻訳サービスがHTTP {(int)statusCode}を返しました。{requestIdSuffix}"),
        };
    }
}
