using System.Net;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class OpenAiSearchQueryInterpreterTests
{
    [Fact]
    public async Task InterpretThenSearch_IsOneHttpOneSearchAndOriginalIsImmutable()
    {
        using Harness h = new(Response("  ルミナ 2024 ライブ  "));
        ScanOutcome outcome = await h.Search();
        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, h.Http.SendCount);
        Assert.Equal(1, h.Provider.Calls);
        Assert.Equal(h.Input.Transcript, outcome.Result!.SourceText);
        Assert.Equal("ルミナ 2024 ライブ", outcome.Result.FeatureResult.VideoSearch!.Query);
        Assert.Equal("  ルミナという曲、カタカナで\r\n2024年のライブを探して  ", h.Input.Transcript);
        Assert.Equal(1, h.Quotas.SearchInterpretation.Consumed);
        Assert.Equal(0, h.Quotas.Translation.Consumed);
        Assert.Equal(0, h.Quotas.Voice.ConsumedRequests);
        Assert.Equal(0, h.Quotas.Voice.ConsumedSeconds);
        Assert.Equal(0, h.Capture.Calls);
        Assert.Equal(0, h.Ocr.Calls);
        using JsonDocument request = JsonDocument.Parse(h.Http.RequestBody!);
        JsonElement root = request.RootElement;
        Assert.Equal("https://api.openai.com/v1/responses", h.Http.Endpoint!.AbsoluteUri);
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(400, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(h.Input.Transcript, root.GetProperty("input").GetString());
        Assert.Equal(OpenAiSearchQueryInterpreter.Instructions, root.GetProperty("instructions").GetString());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("previous_response_id", out _));
        Assert.Equal("Bearer", h.Http.AuthorizationScheme);
        Assert.Equal("fake-api-key", h.Http.AuthorizationParameter);
        Assert.Contains(h.Renderer.Progress, progress => progress.Stage == ScanStage.SearchInterpretation);
        Assert.DoesNotContain(h.Renderer.Progress, progress => progress.Stage is ScanStage.Ocr or ScanStage.Capture);
    }

    [Fact]
    public async Task SearchOnlyRetry_AfterExhaustingInterpretationQuotaDoesNotResendOrTranscribe()
    {
        using Harness h = new(Response("ルミナ 2024 ライブ"), maximumInterpretations: 1);
        h.Provider.Fail = true;
        ScanRequest first = h.Request();
        ScanOutcome failed = await h.Search(first);
        Assert.False(failed.IsSuccess);
        Assert.NotNull(h.Session.CurrentInterpretedQuery);
        Assert.Equal(0, h.Quotas.SearchInterpretation.Remaining);
        h.Provider.Fail = false;
        ScanOutcome retry = await h.Search(h.Request() with { RetrySearchOperationId = first.CorrelationId });
        Assert.True(retry.IsSuccess);
        Assert.Equal(1, h.Http.SendCount);
        Assert.Equal(2, h.Provider.Calls);
        Assert.Equal(0, h.Capture.Calls);
        Assert.Equal(0, h.Ocr.Calls);
        Assert.Equal(1, h.Quotas.SearchInterpretation.Consumed);
        Assert.Equal("ルミナ 2024 ライブ", h.Provider.Query);
        // Both calls accept the same already completed input. No transcriber is in this handler's dependency graph.
        Assert.Same(h.Input, first.TextInput);
        Assert.Equal(h.Input.Transcript, retry.Result!.SourceText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("query\nother")]
    [InlineData("query\tother")]
    [InlineData("https://youtu.be/AAAAAAAAAAA")]
    [InlineData("[\"candidate\"]")]
    [InlineData("```query```")]
    public async Task InvalidQuery_FailsBeforeSearchWithoutFallbackOrRetry(string output)
    {
        using Harness h = new(Response(output));
        ScanOutcome outcome = await h.Search();
        AssertFailure(h, outcome, ScanFailureCode.SearchInterpretationInvalidResponse);
    }

    [Fact]
    public async Task OversizedQueryAndResponse_FailBeforeSearch()
    {
        using Harness query = new(Response(new string('界', 334)));
        AssertFailure(query, await query.Search(), ScanFailureCode.SearchInterpretationInvalidResponse);
        using Harness response = new(new string(' ', OpenAiSearchInterpretationOptions.MaximumResponseBytes + 1));
        AssertFailure(response, await response.Search(), ScanFailureCode.SearchInterpretationInvalidResponse);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"output\":null}")]
    [InlineData("{\"output\":[null]}")]
    [InlineData("{\"output\":[{\"type\":\"function_call\",\"name\":\"search\"}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":42}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"one\"},{\"type\":\"output_text\",\"text\":\"two\"}]}]}")]
    [InlineData("{\"status\":\"incomplete\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"query\"}]}]}")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"query\"}]},{\"type\":\"web_search_call\"}]}")]
    public async Task MalformedToolOrIncompleteOutput_IsRejected(string body)
    {
        using Harness h = new(body);
        AssertFailure(h, await h.Search(), ScanFailureCode.SearchInterpretationInvalidResponse);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ScanFailureCode.SearchInterpretationAuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, ScanFailureCode.SearchInterpretationAuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ScanFailureCode.SearchInterpretationRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ScanFailureCode.SearchInterpretationFailed)]
    public async Task HttpFailure_IsPurposeTypedCountedOnceAndDoesNotEchoBody(HttpStatusCode status, ScanFailureCode code)
    {
        using Harness h = new("private fake body", status: status);
        ScanOutcome outcome = await h.Search();
        AssertFailure(h, outcome, code);
        Assert.DoesNotContain("private fake body", outcome.Failure!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingKeyAndCancelledBeforeSend_UseNoQuotaOrNetwork()
    {
        using Harness missing = new(Response("query"), apiKey: null);
        ScanOutcome outcome = await missing.Search();
        Assert.Equal(ScanFailureCode.SearchInterpretationNotConfigured, outcome.Failure!.Code);
        Assert.Equal(0, missing.Http.SendCount);
        Assert.Equal(0, missing.Quotas.SearchInterpretation.Consumed);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OpenAiSearchQueryInterpreter interpreter = new(missing.Client, new(), null, missing.Quotas.SearchInterpretation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interpreter.InterpretAsync(missing.Input, cancellation.Token));
        Assert.Equal(0, missing.Http.SendCount);
    }

    [Fact]
    public async Task InvalidInputUnicode_IsRejectedWithoutRewritingQuotaOrNetwork()
    {
        using RecordingHttp handler = new(Response("query"));
        using HttpClient http = new(handler);
        FeatureUsageQuotas quotas = new();
        OpenAiSearchQueryInterpreter interpreter = new(http, new(), "fake-api-key", quotas.SearchInterpretation);
        foreach (string input in new[] { "before\ud800after", "before\udc00after" })
        {
            ScanException failure = await Assert.ThrowsAsync<ScanException>(() =>
                interpreter.InterpretAsync(TextInputSession.Create(input), CancellationToken.None));
            Assert.Equal(ScanFailureCode.SearchInterpretationInvalidInput, failure.FailureCode);
        }
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(0, quotas.SearchInterpretation.Consumed);
    }

    [Fact]
    public async Task InvalidOutputUnicode_IsTypedFailureBeforeSearch()
    {
        using Harness h = new("""
            {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"before\uD800after"}]}]}
            """);
        AssertFailure(h, await h.Search(), ScanFailureCode.SearchInterpretationInvalidResponse);
    }

    [Fact]
    public async Task RebuiltClients_KeepProcessQuotaAndTranslationRemainsAvailable()
    {
        FeatureUsageQuotas quotas = new(new FeatureUsageLimits { SearchInterpretationRequests = 1 });
        using RecordingHttp handler = new(Response("query"));
        using HttpClient http = new(handler);
        TextInputSession input = TextInputSession.Create("self-authored input");
        await new OpenAiSearchQueryInterpreter(http, new(), "fake-api-key", quotas.SearchInterpretation)
            .InterpretAsync(input, CancellationToken.None);
        ScanException limit = await Assert.ThrowsAsync<ScanException>(() =>
            new OpenAiSearchQueryInterpreter(http, new(), "fake-api-key", quotas.SearchInterpretation)
                .InterpretAsync(input, CancellationToken.None));
        Assert.Equal(ScanFailureCode.SearchInterpretationUsageLimitReached, limit.FailureCode);
        Assert.Equal(ScanStage.SearchInterpretation, limit.Stage);
        OpenAiTextTranslator translator = new(http, new(), "fake-api-key", quotas.Translation);
        Assert.Equal(OpenAiTranslatorOptions.QualityModel, translator.Model);
        translator.SelectModel(OpenAiTranslatorOptions.BudgetModel);
        Assert.Equal(OpenAiTranslatorOptions.BudgetModel, translator.Model);
        Assert.Throws<ArgumentException>(() => translator.SelectModel(OpenAiSearchInterpretationOptions.DefaultModel));
        await translator.TranslateToJapaneseAsync("Self-authored text", CancellationToken.None);
        Assert.Equal(1, quotas.SearchInterpretation.Consumed);
        Assert.Equal(1, quotas.Translation.Consumed);
        Assert.Equal(2, handler.SendCount);
    }

    [Theory]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.4-nano")]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-6-luna ")]
    [InlineData("")]
    public void SearchOnlyAllowlist_RejectsOtherModelsWithoutBroadeningTranslation(string model)
    {
        using RecordingHttp handler = new(Response("query"));
        using HttpClient http = new(handler);
        FeatureUsageQuotas quotas = new();
        Assert.Throws<ArgumentException>(() => new OpenAiSearchQueryInterpreter(http,
            new OpenAiSearchInterpretationOptions { Model = model }, "fake-api-key", quotas.SearchInterpretation));
        Assert.False(OpenAiTranslatorOptions.IsSupportedModel(OpenAiSearchInterpretationOptions.DefaultModel));
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task ExhaustedTranslationQuota_DoesNotBlockInterpretation()
    {
        using Harness h = new(Response("self-authored query"));
        while (h.Quotas.Translation.TryReserve(out TextRequestQuota.TextRequestReservation? reservation))
        {
            using (reservation) { reservation.MarkSendStarted(CancellationToken.None); }
        }
        Assert.True((await h.Search()).IsSuccess);
        Assert.Equal(1, h.Http.SendCount);
        Assert.Equal(1, h.Quotas.SearchInterpretation.Consumed);
        Assert.Equal(h.Quotas.Translation.Maximum, h.Quotas.Translation.Consumed);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("http")]
    [InlineData("interpretation")]
    [InlineData("search")]
    public async Task Logs_ContainNoTranscriptQueryKeyResponseOrExceptionContent(string mode)
    {
        const string query = "private-query-marker";
        const string key = "private-fake-key-marker";
        const string bodyMarker = "private-response-body-marker";
        string directory = Path.Combine(Path.GetTempPath(), $"vrcva-k3-privacy-{Guid.NewGuid():N}");
        try
        {
            string body = mode == "http" ? bodyMarker : Response(mode == "interpretation" ? $"`{query}`" : query);
            using Harness h = new(body, status: mode == "http" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
                apiKey: key, logger: new PrivacySafeFileLogger(directory));
            h.Provider.Fail = mode == "search";
            ScanOutcome outcome = await h.Search();
            Assert.Equal(mode == "success", outcome.IsSuccess);
            string logs = string.Concat(Directory.GetFiles(directory).Select(File.ReadAllText));
            foreach (string content in new[] { query, key, bodyMarker, h.Input.Transcript, "self-authored search failure" })
            {
                Assert.DoesNotContain(content, logs, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("capture.completed", logs, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Fact]
    public void SearchClient_RejectsTranslationQuotaAndInterpreterRejectsTranslationClient()
    {
        using RecordingHttp handler = new(Response("query"));
        using HttpClient http = new(handler);
        FeatureUsageQuotas quotas = new();
        Assert.Throws<ArgumentException>(() => new OpenAiResponsesTextModelClient(http,
            new OpenAiSearchInterpretationOptions(), "fake-api-key", quotas.Translation));
        OpenAiResponsesTextModelClient translation = new(http, new OpenAiTranslatorOptions(), "fake-api-key", quotas.Translation);
        Assert.Throws<ArgumentException>(() => new OpenAiSearchQueryInterpreter(translation, new()));
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutOrTransportFailure_ConsumesInterpretationOnlyWithoutRetry(bool timeout)
    {
        using RecordingHttp handler = new(Response("query"));
        handler.OnSend = async token =>
        {
            if (timeout) { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            throw new HttpRequestException("private fake exception");
        };
        using HttpClient http = new(handler);
        FeatureUsageQuotas quotas = new();
        OpenAiSearchQueryInterpreter interpreter = new(http, new() { Timeout = TimeSpan.FromMilliseconds(30) },
            "fake-api-key", quotas.SearchInterpretation);
        ScanException failure = await Assert.ThrowsAsync<ScanException>(() =>
            interpreter.InterpretAsync(TextInputSession.Create("self-authored input"), CancellationToken.None));
        Assert.Equal(timeout ? ScanFailureCode.SearchInterpretationTimedOut : ScanFailureCode.SearchInterpretationFailed,
            failure.FailureCode);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, quotas.SearchInterpretation.Consumed);
        Assert.Equal(0, quotas.Translation.Consumed);
    }

    private static string Response(string output) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = output } } } },
    });

    private static void AssertFailure(Harness h, ScanOutcome outcome, ScanFailureCode code)
    {
        Assert.False(outcome.IsSuccess);
        Assert.Equal(code, outcome.Failure!.Code);
        Assert.Equal(ScanStage.SearchInterpretation, outcome.Failure.Stage);
        Assert.Equal(1, h.Http.SendCount);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(1, h.Quotas.SearchInterpretation.Consumed);
        Assert.Equal(0, h.Quotas.Translation.Consumed);
        Assert.Equal(0, h.Quotas.Voice.ConsumedRequests);
        Assert.Equal(0, h.Quotas.Voice.ConsumedSeconds);
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
    }

    private sealed class Harness : IDisposable
    {
        public RecordingHttp Http { get; }
        public HttpClient Client { get; }
        public FeatureUsageQuotas Quotas { get; }
        public ExecutionCoordinator Execution { get; } = new();
        public VideoSearchSession Session { get; }
        public TextInputSession Input { get; } = TextInputSession.Create("  ルミナという曲、カタカナで\r\n2024年のライブを探して  ");
        public FakeProvider Provider { get; } = new();
        public CountingCapture Capture { get; } = new();
        public CountingOcr Ocr { get; } = new();
        public RecordingRenderer Renderer { get; } = new();
        public ScanPipeline Pipeline { get; }
        private bool _started;
        public Harness(string body, int maximumInterpretations = 10, HttpStatusCode status = HttpStatusCode.OK,
            string? apiKey = "fake-api-key", IPrivacySafeLogger? logger = null)
        {
            Http = new(body, status); Client = new(Http);
            Quotas = new(new FeatureUsageLimits { SearchInterpretationRequests = maximumInterpretations });
            Session = new(Execution);
            OpenAiSearchQueryInterpreter interpreter = new(Client, new(), apiKey, Quotas.SearchInterpretation);
            FeatureCatalog catalog = new(new FeatureEntry(BuiltInFeatures.InterpretedVideoSearch,
                new InterpretedVideoSearchHandler(interpreter, Provider, Session)));
            Pipeline = new(Capture, catalog, Renderer, logger, Execution);
        }
        public ScanRequest Request() => ScanRequest.CreateText("test", FeatureIds.InterpretedVideoSearch, Input);
        public async Task<ScanOutcome> Search(ScanRequest? request = null)
        {
            request ??= Request();
            ExecutionOperation? operation;
            bool accepted = _started ? Execution.TryBeginOperation(Input.SessionId, request.CorrelationId, out operation)
                : Execution.TryBeginSession(request.CorrelationId, out operation, sessionId: Input.SessionId);
            Assert.True(accepted); _started = true;
            using (operation) { return await Pipeline.RunAsync(request, operation!); }
        }
        public void Dispose() => Client.Dispose();
    }

    private sealed class RecordingHttp(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public string? RequestBody { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public Func<CancellationToken, Task>? OnSend { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            SendCount++; Endpoint = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestBody = await request.Content!.ReadAsStringAsync(token);
            if (OnSend is not null) { await OnSend(token); }
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FakeProvider : IVideoSearchProvider
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public string? Query { get; private set; }
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++; Query = request.Query;
            if (Fail) { throw new ScanException(ScanFailureCode.Unexpected, ScanStage.TextHandling, "self-authored search failure"); }
            return Task.FromResult(new VideoSearchBatch([]));
        }
    }
    private sealed class CountingCapture : ICaptureSource
    {
        public int Calls { get; private set; }
        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Capture must not run."); }
    }
    private sealed class CountingOcr : IOcrEngine
    {
        public int Calls { get; private set; }
        public Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("OCR must not run."); }
    }
    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanProgress> Progress { get; } = [];
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken)
        { Progress.Add(progress); return Task.CompletedTask; }
        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
