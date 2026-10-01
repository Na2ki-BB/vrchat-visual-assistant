namespace VrcVa.Core.Tests;

public sealed class TextInputPipelineTests
{
    private static readonly FeatureId TextFeatureId = new("test.text");

    [Fact]
    public async Task TextRoute_PreservesTranscriptWithoutCaptureOrOcr()
    {
        const string transcript = "  作品名\r\n2026 年のライブを検索して  ";
        TextInputSession session = TextInputSession.Create(transcript);
        CountingCapture capture = new();
        CountingOcr ocr = new();
        CountingTranslator translator = new();
        RecordingTextHandler handler = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = CreatePipeline(capture, new TranslateAnalyzer(ocr, translator), handler, renderer);
        ScanRequest request = ScanRequest.CreateText("test", TextFeatureId, session);

        ScanOutcome outcome = await pipeline.RunAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Same(session, handler.Input);
        Assert.Equal(transcript, session.Transcript);
        Assert.NotEqual(session.SessionId, request.CorrelationId);
        Assert.Equal(FeatureInputKind.Text, request.InputKind);
        Assert.Equal(0, capture.Count);
        Assert.Equal(0, ocr.Count);
        Assert.Equal(0, translator.Count);
        Assert.Equal(1, handler.Count);
        Assert.Equal("feature-owned query", outcome.Result?.PrimarySection.Text);
        Assert.Equal(transcript, outcome.Result?.SourceText);
        Assert.Equal("none", outcome.Result?.CaptureSourceKind);
        Assert.Equal(TimeSpan.Zero, outcome.Result?.OcrDuration);
        Assert.Null(outcome.Result?.TextModelMetadata);
        Assert.DoesNotContain(renderer.Progress, value => value.Stage is ScanStage.Capture or ScanStage.Ocr);
        Assert.Contains(renderer.Progress, value => value.Stage == ScanStage.TextHandling);
        Assert.Single(renderer.Outcomes);
    }

    [Fact]
    public async Task TextRoute_PreservesSupportingSectionsBeforeExplicitSourceSection()
    {
        RecordingTextHandler handler = new() { IncludeContext = true };
        ScanPipeline pipeline = CreatePipeline(new CountingCapture(), new OcrAnalyzer(new CountingOcr()), handler, new RecordingRenderer());

        ScanOutcome outcome = await pipeline.RunAsync(CreateTextRequest());

        Assert.True(outcome.IsSuccess);
        Assert.Equal(new[] { "context", TranslationResultSectionIds.SourceText, "query" }, outcome.Result?.Sections.Select(section => section.Id));
        Assert.Equal(new[] { "supporting context", "transcript", "feature-owned query" }, outcome.Result?.Sections.Select(section => section.Text));
        AnalysisResult changed = outcome.Result! with { SourceText = "updated transcript" };
        Assert.Equal("supporting context", changed.Sections[0].Text);
        Assert.Equal("updated transcript", changed.Sections[1].Text);
        Assert.Equal("feature-owned query", changed.PrimarySection.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageRoute_PreservesTranslationAndOcrOnlyPrimarySections(bool translate)
    {
        CountingCapture capture = new();
        CountingOcr ocr = new();
        CountingTranslator translator = new();
        RecordingTextHandler handler = new();
        RecordingRenderer renderer = new();
        IAnalyzer analyzer = translate ? new TranslateAnalyzer(ocr, translator) : new OcrAnalyzer(ocr);
        ScanPipeline pipeline = CreatePipeline(capture, analyzer, handler, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.Create("test"));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, capture.Count);
        Assert.Equal(1, ocr.Count);
        Assert.Equal(translate ? 1 : 0, translator.Count);
        Assert.Equal(0, handler.Count);
        Assert.Equal(FeatureIds.Translation, outcome.Result?.FeatureId);
        Assert.Equal("image-source", outcome.Result?.CaptureSourceKind);
        Assert.Equal("source", outcome.Result?.SourceText);
        Assert.Equal(translate ? "翻訳" : string.Empty, outcome.Result?.JapaneseText);
        Assert.Equal(
            translate ? TranslationResultSectionIds.JapaneseText : TranslationResultSectionIds.SourceText,
            outcome.Result?.PrimarySection.Id);
        Assert.All(capture.Bytes, value => Assert.Equal(0, value));
        Assert.DoesNotContain(renderer.Progress, value => value.Stage == ScanStage.TextHandling);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InputMismatch_IsRejectedBeforeAnyProcessing(bool textRequest)
    {
        CountingCapture capture = new();
        CountingOcr ocr = new();
        RecordingTextHandler handler = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = CreatePipeline(capture, new OcrAnalyzer(ocr), handler, renderer);
        ScanRequest request = textRequest
            ? ScanRequest.CreateText("test", FeatureIds.Translation, TextInputSession.Create("transcript"))
            : ScanRequest.Create("test", TextFeatureId);

        ScanOutcome outcome = await pipeline.RunAsync(request);

        Assert.Equal(ScanFailureCode.InputKindMismatch, outcome.Failure?.Code);
        Assert.Equal(ScanStage.Trigger, outcome.Failure?.Stage);
        Assert.Equal(0, capture.Count);
        Assert.Equal(0, ocr.Count);
        Assert.Equal(0, handler.Count);
        Assert.Empty(renderer.Progress);
        Assert.Single(renderer.Outcomes);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("summarization")]
    public async Task UnknownTextFeature_IsRejectedBeforeAnyProcessing(string featureId)
    {
        CountingCapture capture = new();
        CountingOcr ocr = new();
        RecordingTextHandler handler = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = CreatePipeline(capture, new OcrAnalyzer(ocr), handler, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.CreateText(
            "test", new FeatureId(featureId), TextInputSession.Create("transcript")));

        Assert.Equal(ScanFailureCode.UnknownFeature, outcome.Failure?.Code);
        Assert.Equal(0, capture.Count);
        Assert.Equal(0, ocr.Count);
        Assert.Equal(0, handler.Count);
        Assert.Empty(renderer.Progress);
    }

    [Fact]
    public async Task TextHandlerWrongFeature_IsNotReportedAsSuccess()
    {
        RecordingTextHandler handler = new() { ResultFeatureId = new FeatureId("test.wrong") };
        ScanPipeline pipeline = CreatePipeline(new CountingCapture(), new OcrAnalyzer(new CountingOcr()), handler, new RecordingRenderer());

        ScanOutcome outcome = await pipeline.RunAsync(CreateTextRequest());

        Assert.Equal(ScanFailureCode.Unexpected, outcome.Failure?.Code);
        Assert.Equal(ScanStage.TextHandling, outcome.Failure?.Stage);
    }

    [Fact]
    public async Task PreCancelledTextRequest_DoesNotInvokeHandler()
    {
        RecordingTextHandler handler = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = CreatePipeline(new CountingCapture(), new OcrAnalyzer(new CountingOcr()), handler, renderer);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        ScanOutcome outcome = await pipeline.RunAsync(CreateTextRequest(), cancellation.Token);

        Assert.Equal(ScanFailureCode.Cancelled, outcome.Failure?.Code);
        Assert.Equal(0, handler.Count);
        Assert.Empty(renderer.Progress);
    }

    [Fact]
    public async Task TextAndImageRoutes_ShareExistingPipelineSingleFlightAndReleaseAfterCancellation()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingTextHandler handler = new()
        {
            Operation = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        CountingCapture capture = new();
        ScanPipeline pipeline = CreatePipeline(capture, new OcrAnalyzer(new CountingOcr()), handler, new RecordingRenderer());
        using CancellationTokenSource cancellation = new();
        Task<ScanOutcome> first = pipeline.RunAsync(CreateTextRequest(), cancellation.Token);
        await entered.Task;

        ScanOutcome busy = await pipeline.RunAsync(ScanRequest.Create("image"));
        cancellation.Cancel();
        ScanOutcome cancelled = await first;
        ScanOutcome next = await pipeline.RunAsync(ScanRequest.Create("next image"));

        Assert.Equal(ScanFailureCode.Busy, busy.Failure?.Code);
        Assert.Equal(ScanFailureCode.Cancelled, cancelled.Failure?.Code);
        Assert.Equal(ScanStage.TextHandling, cancelled.Failure?.Stage);
        Assert.Equal(1, handler.Count);
        Assert.True(next.IsSuccess);
        Assert.Equal(1, capture.Count);
    }

    [Fact]
    public async Task TextFailure_IsClassifiedAndDoesNotBlockNextRequest()
    {
        RecordingTextHandler handler = new()
        {
            Operation = _ => throw new ScanException(
                ScanFailureCode.NoTextDetected, ScanStage.TextHandling, "テキスト処理に失敗しました。"),
        };
        ScanPipeline pipeline = CreatePipeline(new CountingCapture(), new OcrAnalyzer(new CountingOcr()), handler, new RecordingRenderer());

        ScanOutcome failure = await pipeline.RunAsync(CreateTextRequest());
        handler.Operation = null;
        ScanOutcome next = await pipeline.RunAsync(CreateTextRequest());

        Assert.Equal(ScanFailureCode.NoTextDetected, failure.Failure?.Code);
        Assert.Equal(ScanStage.TextHandling, failure.Failure?.Stage);
        Assert.True(next.IsSuccess);
    }

    [Fact]
    public async Task HandlerIgnoringCancellation_CannotRenderLateSuccess()
    {
        using CancellationTokenSource cancellation = new();
        RecordingTextHandler handler = new()
        {
            Operation = _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = CreatePipeline(new CountingCapture(), new OcrAnalyzer(new CountingOcr()), handler, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(CreateTextRequest(), cancellation.Token);

        Assert.Equal(ScanFailureCode.Cancelled, outcome.Failure?.Code);
        Assert.Empty(renderer.Outcomes);
    }

    private static ScanRequest CreateTextRequest() =>
        ScanRequest.CreateText("test", TextFeatureId, TextInputSession.Create("transcript"));

    private static ScanPipeline CreatePipeline(
        CountingCapture capture,
        IAnalyzer analyzer,
        RecordingTextHandler handler,
        RecordingRenderer renderer) => new(
            capture,
            new FeatureCatalog(
                new FeatureEntry(BuiltInFeatures.Translation, analyzer),
                new FeatureEntry(new FeatureDescriptor(TextFeatureId, "Text", FeatureInputKind.Text, FeatureDataBoundary.LocalOnly), handler)),
            renderer);

    private sealed class CountingCapture : ICaptureSource
    {
        public int Count { get; private set; }
        public byte[] Bytes { get; } = [1, 2];

        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new CapturedFrame(Bytes, 1, 1, "image/png", "image-source"));
        }
    }

    private sealed class CountingOcr : IOcrEngine
    {
        public int Count { get; private set; }

        public Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new OcrOutput("source", "en"));
        }
    }

    private sealed class CountingTranslator : ITextTranslator
    {
        public int Count { get; private set; }

        public Task<TranslationOutput> TranslateToJapaneseAsync(string sourceText, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new TranslationOutput("翻訳", "fake", "fake-model"));
        }
    }

    private sealed class RecordingTextHandler : ITextFeatureHandler
    {
        public int Count { get; private set; }
        public TextInputSession? Input { get; private set; }
        public FeatureId ResultFeatureId { get; init; } = TextFeatureId;
        public bool IncludeContext { get; init; }
        public Func<CancellationToken, Task>? Operation { get; set; }

        public async Task<FeatureResult> HandleAsync(TextInputSession input, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Count++;
            Input = input;
            if (Operation is not null)
            {
                await Operation(cancellationToken);
            }

            List<ResultSection> sections = [];
            if (IncludeContext)
            {
                sections.Add(new ResultSection("context", "Context", "supporting context"));
            }

            sections.AddRange(
            [
                new ResultSection(TranslationResultSectionIds.SourceText, "認識文", input.Transcript),
                new ResultSection("query", "用途の結果", "feature-owned query", ResultSectionRole.Primary),
            ]);
            return new FeatureResult(ResultFeatureId, sections);
        }
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanProgress> Progress { get; } = [];
        public List<ScanOutcome> Outcomes { get; } = [];

        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
}
