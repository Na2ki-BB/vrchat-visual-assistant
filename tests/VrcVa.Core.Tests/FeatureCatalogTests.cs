namespace VrcVa.Core.Tests;

public sealed class FeatureCatalogTests
{
    [Fact]
    public async Task Pipeline_RoutesASecondFeatureAndPreservesGenericSections()
    {
        FeatureId secondFeatureId = new("test.summary");
        RecordingAnalyzer translationAnalyzer = new(CreateGenericResult(
            FeatureIds.Translation,
            "translation-primary"));
        RecordingAnalyzer summaryAnalyzer = new(CreateGenericResult(
            secondFeatureId,
            "summary-primary"));
        CountingCaptureSource capture = new();
        RecordingRenderer renderer = new();
        FeatureCatalog catalog = new(
            new FeatureEntry(BuiltInFeatures.Translation, translationAnalyzer),
            new FeatureEntry(
                new FeatureDescriptor(
                    secondFeatureId,
                    "要約",
                    FeatureInputKind.CapturedFrame,
                    FeatureDataBoundary.LocalOnly),
                summaryAnalyzer));
        ScanPipeline pipeline = new(capture, catalog, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(
            ScanRequest.Create("unit-test", secondFeatureId));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(1, capture.CaptureCount);
        Assert.Equal(0, translationAnalyzer.CallCount);
        Assert.Equal(1, summaryAnalyzer.CallCount);
        Assert.Equal(secondFeatureId, outcome.Result?.FeatureId);
        Assert.Equal("test", outcome.Result?.CaptureSourceKind);
        Assert.Equal(
            new[] { "context", "answer" },
            outcome.Result?.Sections.Select(section => section.Id));
        Assert.Equal("summary-primary", outcome.Result?.PrimarySection.Text);
        Assert.Equal("summary-primary", outcome.Result?.JapaneseText);
    }

    [Fact]
    public void Constructor_RejectsDuplicateFeatureIds()
    {
        FeatureDescriptor first = new(
            new FeatureId("duplicate"),
            "First",
            FeatureInputKind.CapturedFrame,
            FeatureDataBoundary.LocalOnly);
        FeatureDescriptor second = new(
            new FeatureId("duplicate"),
            "Second",
            FeatureInputKind.CapturedFrame,
            FeatureDataBoundary.CapturedImageMayLeaveDevice);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new FeatureCatalog(
                new FeatureEntry(first, new RecordingAnalyzer(CreateGenericResult(first.Id, "a"))),
                new FeatureEntry(second, new RecordingAnalyzer(CreateGenericResult(second.Id, "b")))));

        Assert.Equal("entries", exception.ParamName);
    }

    [Fact]
    public async Task Pipeline_RejectsUnknownFeatureBeforeCapture()
    {
        CountingCaptureSource capture = new();
        RecordingRenderer renderer = new();
        FeatureCatalog catalog = new(new FeatureEntry(
            BuiltInFeatures.Translation,
            new RecordingAnalyzer(CreateGenericResult(
                FeatureIds.Translation,
                "translation-primary"))));
        ScanPipeline pipeline = new(capture, catalog, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(
            ScanRequest.Create("unit-test", new FeatureId("unknown")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ScanFailureCode.UnknownFeature, outcome.Failure?.Code);
        Assert.Equal(ScanStage.Trigger, outcome.Failure?.Stage);
        Assert.Equal(0, capture.CaptureCount);
        Assert.Single(renderer.Outcomes);
    }

    [Fact]
    public async Task Pipeline_DoesNotSucceedWhenAnalyzerReturnsDifferentFeatureId()
    {
        FeatureId registeredId = new("test.registered");
        CountingCaptureSource capture = new();
        RecordingRenderer renderer = new();
        FeatureCatalog catalog = new(new FeatureEntry(
            new FeatureDescriptor(
                registeredId,
                "Registered",
                FeatureInputKind.CapturedFrame,
                FeatureDataBoundary.LocalOnly),
            new RecordingAnalyzer(CreateGenericResult(
                new FeatureId("test.wrong-result"),
                "wrong"))));
        ScanPipeline pipeline = new(capture, catalog, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(
            ScanRequest.Create("unit-test", registeredId));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ScanFailureCode.Unexpected, outcome.Failure?.Code);
        Assert.Equal(1, capture.CaptureCount);
        Assert.Single(renderer.Outcomes);
    }

    [Theory]
    [InlineData("UPPERCASE")]
    [InlineData(" leading-space")]
    [InlineData("non/ascii")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void FeatureId_RejectsUnstableValues(string value)
    {
        Assert.Throws<ArgumentException>(() => new FeatureId(value));
    }

    [Fact]
    public void FeatureResult_RequiresExactlyOnePrimarySection()
    {
        FeatureId featureId = new("test.validation");

        Assert.Throws<ArgumentException>(() => new FeatureResult(
            featureId,
            [new ResultSection("only", "Only", "content")]));

        Assert.Throws<ArgumentException>(() => new FeatureResult(
            featureId,
            [
                new ResultSection("first", "First", "a", ResultSectionRole.Primary),
                new ResultSection("second", "Second", "b", ResultSectionRole.Primary),
            ]));

    }

    [Fact]
    public void AnalysisResult_PreservesLegacyConstructorPropertiesAndDeconstruction()
    {
        AnalysisResult result = new(
            SourceText: "source",
            JapaneseText: "日本語",
            OcrLanguage: "en",
            TranslationProvider: "fake",
            TranslationModel: "fake-model",
            OcrDuration: TimeSpan.FromMilliseconds(1),
            TranslationDuration: TimeSpan.FromMilliseconds(2),
            Warning: "warning",
            CaptureSourceKind: "capture");

        (
            string sourceText,
            string japaneseText,
            string ocrLanguage,
            string provider,
            string model,
            TimeSpan ocrDuration,
            TimeSpan translationDuration,
            string? warning,
            string captureSourceKind) = result;
        FeatureResult canonical = result;
        AnalysisResult changed = result with { Warning = "updated" };
        AnalysisResult equalLegacyResult = new(
            SourceText: "source",
            JapaneseText: "日本語",
            OcrLanguage: "en",
            TranslationProvider: "fake",
            TranslationModel: "fake-model",
            OcrDuration: TimeSpan.FromMilliseconds(1),
            TranslationDuration: TimeSpan.FromMilliseconds(2),
            Warning: "warning",
            CaptureSourceKind: "capture");
        ScanOutcome outcome = ScanOutcome.Succeeded(
            Guid.NewGuid(),
            result,
            TimeSpan.Zero);
        AnalysisResult? assignedFromOutcome = outcome.Result;

        Assert.Equal("source", sourceText);
        Assert.Equal("日本語", japaneseText);
        Assert.Equal("en", ocrLanguage);
        Assert.Equal("fake", provider);
        Assert.Equal("fake-model", model);
        Assert.Equal(TimeSpan.FromMilliseconds(1), ocrDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(2), translationDuration);
        Assert.Equal("warning", warning);
        Assert.Equal("capture", captureSourceKind);
        Assert.Equal("updated", changed.Warning);
        Assert.Equal("updated", changed.FeatureResult.Warning);
        Assert.Equal(result, equalLegacyResult);
        Assert.Same(result, assignedFromOutcome);
        Assert.Equal(canonical.PrimarySection.Text, result.FeatureResult.PrimarySection.Text);
    }

    [Fact]
    public void AnalysisResult_WithProjectsChangedLegacyValuesIntoCanonicalResult()
    {
        AnalysisResult original = CreateGenericResult(
            new FeatureId("test.summary"),
            "original primary");

        AnalysisResult changed = original with
        {
            SourceText = "changed source",
            JapaneseText = "changed primary",
            OcrLanguage = "ja",
            TranslationProvider = "changed-provider",
            TranslationModel = "changed-model",
            Warning = "changed warning",
            CaptureSourceKind = "changed-capture",
        };
        FeatureResult projected = changed.FeatureResult;

        Assert.Equal(new FeatureId("test.summary"), projected.FeatureId);
        Assert.Equal("changed source", projected.Sections[0].Text);
        Assert.Equal("changed primary", projected.PrimarySection.Text);
        Assert.Equal("ja", projected.OcrLanguage);
        Assert.Equal("changed-provider", projected.TranslationProvider);
        Assert.Equal("changed-model", projected.TranslationModel);
        Assert.Equal("changed warning", projected.Warning);
        Assert.Equal("changed-capture", projected.CaptureSourceKind);
    }

    [Fact]
    public void AnalysisResult_EqualityIncludesCanonicalFeatureIdentityAndSections()
    {
        AnalysisResult first = CreateGenericResult(new FeatureId("test.first"), "same");
        AnalysisResult equivalent = CreateGenericResult(new FeatureId("test.first"), "same");
        AnalysisResult differentFeature = CreateGenericResult(
            new FeatureId("test.second"),
            "same");
        AnalysisResult differentSection = CreateGenericResult(
            new FeatureId("test.first"),
            "different");

        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.NotEqual(first, differentFeature);
        Assert.NotEqual(first, differentSection);
    }

    [Fact]
    public void AnalysisResult_CanonicalTranslationRetainsLegacyValueEquality()
    {
        FeatureResult featureResult = new(
            FeatureIds.Translation,
            [
                new ResultSection("source-text", "OCR結果（英語）", "source"),
                new ResultSection(
                    "japanese-text",
                    "日本語訳",
                    "日本語",
                    ResultSectionRole.Primary),
            ],
            new TextModelMetadata("en", "fake", "fake-model"))
        {
            OcrDuration = TimeSpan.FromMilliseconds(1),
            TranslationDuration = TimeSpan.FromMilliseconds(2),
            Warning = "warning",
            CaptureSourceKind = "capture",
        };
        AnalysisResult canonical = new(featureResult);
        AnalysisResult legacy = new(
            SourceText: "source",
            JapaneseText: "日本語",
            OcrLanguage: "en",
            TranslationProvider: "fake",
            TranslationModel: "fake-model",
            OcrDuration: TimeSpan.FromMilliseconds(1),
            TranslationDuration: TimeSpan.FromMilliseconds(2),
            Warning: "warning",
            CaptureSourceKind: "capture");

        Assert.True(canonical == legacy);
        Assert.True(legacy == canonical);
        Assert.Equal(canonical.GetHashCode(), legacy.GetHashCode());
    }

    private static AnalysisResult CreateGenericResult(FeatureId featureId, string primaryText) =>
        new(new FeatureResult(
            featureId,
            [
                new ResultSection("context", "Context", "supporting"),
                new ResultSection(
                    "answer",
                    "Answer",
                    primaryText,
                    ResultSectionRole.Primary),
            ]));

    private sealed class CountingCaptureSource : ICaptureSource
    {
        public int CaptureCount { get; private set; }

        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult(new CapturedFrame([1], 1, 1, "image/png", "test"));
        }
    }

    private sealed class RecordingAnalyzer(AnalysisResult result) : IAnalyzer
    {
        public int CallCount { get; private set; }

        public Task<AnalysisResult> AnalyzeAsync(
            CapturedFrame frame,
            ScanRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanOutcome> Outcomes { get; } = [];

        public Task RenderProgressAsync(
            ScanProgress progress,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenderOutcomeAsync(
            ScanOutcome outcome,
            CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
}
