using System.Net;
using System.Net.Http;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Translation;

namespace VrcVa.Windows.Tests;

public sealed class TranslationRuntimeTests
{
    [Fact]
    public void Create_WithStoredKey_UsesOfficialEndpointAndSharedQuota()
    {
        using HttpClient client = new(new CountingHandler());
        TranslationRequestQuota quota = new();
        TranslationRuntimeFactory factory = CreateFactory(client, quota);

        TranslationRuntime runtime = factory.Create(
            configuredProvider: null,
            environmentApiKey: null,
            storedApiKey: "stored-test-key",
            openAiOptionsFactory: () => CreateOptions());

        Assert.Equal("openai", runtime.ProviderId);
        Assert.Equal(TranslationKeySource.CredentialManager, runtime.KeySource);
        Assert.True(runtime.HasEffectiveApiKey);
        Assert.True(runtime.HasStoredApiKey);
        Assert.False(runtime.HasEnvironmentApiKey);
        Assert.NotNull(runtime.OpenAiTranslator);
        Assert.Equal(quota.Maximum, runtime.OpenAiTranslator.RemainingRequests);
    }

    [Fact]
    public void Create_WithStoredKeyAndCustomEndpoint_FailsClosed()
    {
        CountingHandler handler = new();
        using HttpClient client = new(handler);
        TranslationRuntimeFactory factory = CreateFactory(client, new TranslationRequestQuota());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            factory.Create(
                configuredProvider: null,
                environmentApiKey: null,
                storedApiKey: "stored-test-key",
                openAiOptionsFactory: () =>
                    CreateOptions(new Uri("https://example.test/v1/responses"))));

        Assert.Contains("official OpenAI endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void Create_WithEnvironmentKeyAndExplicitProvider_AllowsCustomEndpoint()
    {
        using HttpClient client = new(new CountingHandler());
        TranslationRuntimeFactory factory = CreateFactory(client, new TranslationRequestQuota());

        TranslationRuntime runtime = factory.Create(
            configuredProvider: "openai",
            environmentApiKey: "environment-test-key",
            storedApiKey: "stored-test-key",
            openAiOptionsFactory: () =>
                CreateOptions(new Uri("https://example.test/v1/responses")));

        Assert.Equal(TranslationKeySource.Environment, runtime.KeySource);
        Assert.True(runtime.HasEnvironmentApiKey);
        Assert.True(runtime.HasStoredApiKey);
        Assert.True(runtime.HasEffectiveApiKey);
    }

    [Fact]
    public void Create_WithExplicitNone_DoesNotReadOpenAiOptionsOrUseAvailableKeys()
    {
        using HttpClient client = new(new CountingHandler());
        TranslationRuntimeFactory factory = CreateFactory(client, new TranslationRequestQuota());
        bool optionsRead = false;

        TranslationRuntime runtime = factory.Create(
            configuredProvider: "none",
            environmentApiKey: "environment-test-key",
            storedApiKey: "stored-test-key",
            openAiOptionsFactory: () =>
            {
                optionsRead = true;
                return CreateOptions();
            });

        Assert.False(optionsRead);
        Assert.Equal("none", runtime.ProviderId);
        Assert.Equal(TranslationKeySource.None, runtime.KeySource);
        Assert.False(runtime.HasEffectiveApiKey);
        Assert.IsType<OcrAnalyzer>(runtime.Analyzer);
    }

    [Fact]
    public void Create_PreservesPreferredSupportedModelAcrossRuntimeReplacement()
    {
        using HttpClient client = new(new CountingHandler());
        TranslationRuntimeFactory factory = CreateFactory(client, new TranslationRequestQuota());

        TranslationRuntime runtime = factory.Create(
            configuredProvider: null,
            environmentApiKey: null,
            storedApiKey: "stored-test-key",
            openAiOptionsFactory: () => CreateOptions(),
            preferredModel: OpenAiTranslatorOptions.BudgetModel);

        Assert.Equal(OpenAiTranslatorOptions.BudgetModel, runtime.OpenAiTranslator?.Model);
    }

    [Fact]
    public async Task ReloadableAnalyzer_UsesOneRuntimeSnapshotPerAnalysis()
    {
        BlockingAnalyzer first = new("first");
        ImmediateAnalyzer second = new("second");
        ReloadableAnalyzer reloadable = new(CreateRuntime(first));
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");
        ScanRequest request = ScanRequest.Create("test");

        Task<AnalysisResult> running = reloadable.AnalyzeAsync(
            frame,
            request,
            progress: null,
            CancellationToken.None);
        await first.Started;

        reloadable.Swap(CreateRuntime(second));
        AnalysisResult replacementResult = await reloadable.AnalyzeAsync(
            frame,
            request,
            progress: null,
            CancellationToken.None);
        first.Release();
        AnalysisResult originalResult = await running;

        Assert.Equal("first", originalResult.SourceText);
        Assert.Equal("second", replacementResult.SourceText);
    }

    [Fact]
    public async Task ConfigurationFailureRuntime_PerformsNoHttpRequest()
    {
        CountingHandler handler = new();
        using HttpClient client = new(handler);
        TranslationRuntimeFactory factory = CreateFactory(client, new TranslationRequestQuota());
        TranslationRuntime runtime = factory.CreateConfigurationFailure(
            hasEnvironmentApiKey: false,
            hasStoredApiKey: true,
            warning: "翻訳設定を確認してください。");
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            runtime.Analyzer.AnalyzeAsync(
                frame,
                ScanRequest.Create("test"),
                progress: null,
                CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationNotConfigured, exception.FailureCode);
        Assert.Equal(0, handler.SendCount);
        Assert.False(runtime.HasEffectiveApiKey);
    }

    private static TranslationRuntimeFactory CreateFactory(
        HttpClient client,
        TranslationRequestQuota quota) =>
        new(client, new FixedOcrEngine(), quota);

    private static OpenAiTranslatorOptions CreateOptions(Uri? endpoint = null) => new()
    {
        Endpoint = endpoint ?? new Uri(OpenAiTranslatorOptions.DefaultEndpoint),
        Model = OpenAiTranslatorOptions.QualityModel,
        Timeout = TimeSpan.FromSeconds(2),
    };

    private static TranslationRuntime CreateRuntime(IAnalyzer analyzer) => new(
        Analyzer: analyzer,
        OpenAiTranslator: null,
        ProviderId: "test",
        KeySource: TranslationKeySource.None,
        HasEffectiveApiKey: false,
        HasEnvironmentApiKey: false,
        HasStoredApiKey: false,
        PrivacyNotice: "test",
        Status: "test");

    private static AnalysisResult CreateResult(string text) => new(
        text,
        string.Empty,
        "en",
        "test",
        "test",
        TimeSpan.Zero,
        TimeSpan.Zero);

    private sealed class FixedOcrEngine : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput("Hello", "en-US"));
    }

    private sealed class ImmediateAnalyzer(string text) : IAnalyzer
    {
        public Task<AnalysisResult> AnalyzeAsync(
            CapturedFrame frame,
            ScanRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(CreateResult(text));
    }

    private sealed class BlockingAnalyzer(string text) : IAnalyzer
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult();

        public async Task<AnalysisResult> AnalyzeAsync(
            CapturedFrame frame,
            ScanRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateResult(text);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
