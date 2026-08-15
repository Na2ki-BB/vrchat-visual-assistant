using System.Net.Http;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Security;

namespace VrcVa.Windows.Translation;

internal enum TranslationKeySource
{
    None,
    Environment,
    CredentialManager,
}

internal sealed record TranslationRuntime(
    IAnalyzer Analyzer,
    OpenAiTextTranslator? OpenAiTranslator,
    string ProviderId,
    TranslationKeySource KeySource,
    bool HasEffectiveApiKey,
    bool HasEnvironmentApiKey,
    bool HasStoredApiKey,
    string PrivacyNotice,
    string Status,
    string? Warning = null);

internal sealed class TranslationRuntimeFactory
{
    private static readonly Uri OfficialOpenAiEndpoint = new(
        OpenAiTranslatorOptions.DefaultEndpoint,
        UriKind.Absolute);

    private readonly HttpClient _httpClient;
    private readonly IOcrEngine _ocrEngine;
    private readonly TranslationRequestQuota _requestQuota;

    public TranslationRuntimeFactory(
        HttpClient httpClient,
        IOcrEngine ocrEngine,
        TranslationRequestQuota requestQuota)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(ocrEngine);
        ArgumentNullException.ThrowIfNull(requestQuota);

        _httpClient = httpClient;
        _ocrEngine = ocrEngine;
        _requestQuota = requestQuota;
    }

    public TranslationRuntime Create(
        string? configuredProvider,
        string? environmentApiKey,
        string? storedApiKey,
        Func<OpenAiTranslatorOptions> openAiOptionsFactory,
        string? preferredModel = null)
    {
        ArgumentNullException.ThrowIfNull(openAiOptionsFactory);

        bool hasEnvironmentApiKey = !string.IsNullOrWhiteSpace(environmentApiKey);
        bool hasStoredApiKey = !string.IsNullOrWhiteSpace(storedApiKey);
        string providerId = TranslationProviderSelection.Resolve(
            configuredProvider,
            hasStoredApiKey);

        if (string.Equals(providerId, "none", StringComparison.Ordinal))
        {
            return CreateOcrOnly(hasEnvironmentApiKey, hasStoredApiKey);
        }

        if (!string.Equals(providerId, "openai", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "VRCVA_TRANSLATION_PROVIDER must be either none or openai.");
        }

        OpenAiTranslatorOptions options = openAiOptionsFactory();
        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            if (!OpenAiTranslatorOptions.IsSupportedModel(preferredModel))
            {
                throw new InvalidOperationException("The selected OpenAI model is unsupported.");
            }

            options = options with { Model = preferredModel.Trim() };
        }

        string? apiKey;
        TranslationKeySource keySource;
        if (hasEnvironmentApiKey)
        {
            apiKey = environmentApiKey;
            keySource = TranslationKeySource.Environment;
        }
        else if (hasStoredApiKey)
        {
            apiKey = storedApiKey;
            keySource = TranslationKeySource.CredentialManager;
        }
        else
        {
            apiKey = null;
            keySource = TranslationKeySource.None;
        }

        if (keySource == TranslationKeySource.CredentialManager
            && !IsOfficialOpenAiEndpoint(options.Endpoint))
        {
            throw new InvalidOperationException(
                "A Credential Manager API key can only be sent to the official OpenAI endpoint.");
        }

        OpenAiTextTranslator translator = new(
            _httpClient,
            options,
            apiKey,
            _requestQuota);
        bool hasEffectiveApiKey = apiKey is not null;
        return new TranslationRuntime(
            new TranslateAnalyzer(_ocrEngine, translator),
            translator,
            providerId,
            keySource,
            hasEffectiveApiKey,
            hasEnvironmentApiKey,
            hasStoredApiKey,
            hasEffectiveApiKey
                ? "画像はWindows内でOCRし、保存しません。翻訳時はOCRテキストだけをOpenAIへ送信します（API従量課金）。"
                : "画像はWindows内でOCRし、保存しません。OpenAIが選択されていますが、専用APIキー未設定のため外部送信しません。",
            hasEffectiveApiKey
                ? CreateOpenAiStatus(translator)
                : "翻訳API: OpenAI / 専用キー未設定");
    }

    public TranslationRuntime CreateConfigurationFailure(
        bool hasEnvironmentApiKey,
        bool hasStoredApiKey,
        string warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(warning);

        return new TranslationRuntime(
            Analyzer: new TranslateAnalyzer(
                _ocrEngine,
                new ConfigurationFailureTranslator(warning)),
            OpenAiTranslator: null,
            ProviderId: "error",
            KeySource: TranslationKeySource.None,
            HasEffectiveApiKey: false,
            HasEnvironmentApiKey: hasEnvironmentApiKey,
            HasStoredApiKey: hasStoredApiKey,
            PrivacyNotice: "翻訳設定が不正なため、外部送信は行われません。",
            Status: "翻訳: 設定エラー",
            Warning: warning);
    }

    private TranslationRuntime CreateOcrOnly(
        bool hasEnvironmentApiKey,
        bool hasStoredApiKey) =>
        new(
            Analyzer: new OcrAnalyzer(_ocrEngine),
            OpenAiTranslator: null,
            ProviderId: "none",
            KeySource: TranslationKeySource.None,
            HasEffectiveApiKey: false,
            HasEnvironmentApiKey: hasEnvironmentApiKey,
            HasStoredApiKey: hasStoredApiKey,
            PrivacyNotice: "画像とOCRはWindows内で処理し、保存しません。OCR結果は表示しますが、翻訳未設定のため外部送信しません。",
            Status: "OCRのみ / OpenAI未設定");

    private static bool IsOfficialOpenAiEndpoint(Uri endpoint) =>
        string.Equals(endpoint.Scheme, OfficialOpenAiEndpoint.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(endpoint.Host, OfficialOpenAiEndpoint.Host, StringComparison.OrdinalIgnoreCase)
        && endpoint.Port == OfficialOpenAiEndpoint.Port
        && string.Equals(
            endpoint.AbsolutePath,
            OfficialOpenAiEndpoint.AbsolutePath,
            StringComparison.Ordinal)
        && string.IsNullOrEmpty(endpoint.Query)
        && string.IsNullOrEmpty(endpoint.Fragment)
        && string.IsNullOrEmpty(endpoint.UserInfo);

    private static string CreateOpenAiStatus(OpenAiTextTranslator translator) =>
        $"翻訳API: OpenAI / {translator.Model}（従量課金・残り{translator.RemainingRequests}"
        + $"/{translator.MaxRequestsPerSession}回）";

    private sealed class ConfigurationFailureTranslator(string message) : ITextTranslator
    {
        public Task<TranslationOutput> TranslateToJapaneseAsync(
            string sourceText,
            CancellationToken cancellationToken) =>
            Task.FromException<TranslationOutput>(new ScanException(
                ScanFailureCode.TranslationNotConfigured,
                ScanStage.Translation,
                message));
    }
}
