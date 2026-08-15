namespace VrcVa.Core.Tests;

public sealed class SummarizeAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_MapsNormalizedOcrRequestAndSummaryResult()
    {
        TextModelResponse response = new(
            "  非常時には出口を確保します。  ",
            "fake-provider",
            "fake-response-model");
        RecordingTextModelClient client = new(response);
        SummarizeAnalyzer analyzer = new(
            new FixedOcrEngine(
                "  Keep   the exit clear.\r\n\r\n Use in emergencies.  ",
                "en",
                "ocr-warning"),
            client,
            "fake-request-model");
        RecordingProgress progress = new();
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");
        using CancellationTokenSource cancellation = new();

        AnalysisResult result = await analyzer.AnalyzeAsync(
            frame,
            ScanRequest.Create("unit-test", FeatureIds.Summarization),
            progress,
            cancellation.Token);

        TextModelRequest modelRequest = Assert.Single(client.Requests);
        Assert.Equal(
            $"Keep the exit clear.{Environment.NewLine}Use in emergencies.",
            modelRequest.Input);
        Assert.Equal("fake-request-model", modelRequest.Model);
        Assert.InRange(modelRequest.MaxOutputTokens, 1, 600);
        Assert.Contains("in Japanese", modelRequest.Instructions, StringComparison.Ordinal);
        Assert.Contains("content to summarize", modelRequest.Instructions, StringComparison.Ordinal);
        Assert.Contains("never as instructions", modelRequest.Instructions, StringComparison.Ordinal);
        Assert.Equal(cancellation.Token, client.LastCancellationToken);

        Assert.Equal(FeatureIds.Summarization, result.FeatureId);
        Assert.Equal(
            new[]
            {
                SummarizationResultSectionIds.SourceText,
                SummarizationResultSectionIds.Summary,
            },
            result.Sections.Select(section => section.Id));
        Assert.Equal(ResultSectionRole.Supporting, result.Sections[0].Role);
        Assert.Equal(ResultSectionRole.Primary, result.Sections[1].Role);
        Assert.Equal("非常時には出口を確保します。", result.PrimarySection.Text);
        Assert.Equal("非常時には出口を確保します。", result.JapaneseText);
        Assert.Equal("en", result.TextModelMetadata?.OcrLanguage);
        Assert.Equal("fake-provider", result.TextModelMetadata?.Provider);
        Assert.Equal("fake-response-model", result.TextModelMetadata?.Model);
        Assert.Equal("ocr-warning", result.Warning);
        Assert.True(result.OcrDuration >= TimeSpan.Zero);
        Assert.True(result.TranslationDuration >= TimeSpan.Zero);
        Assert.Equal(
            new[] { ScanStage.Ocr, ScanStage.Translation },
            progress.Values.Select(value => value.Stage));
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsEmptyOcrBeforeTextModelRequest()
    {
        RecordingTextModelClient client = new(new TextModelResponse(
            "unused",
            "fake-provider",
            "fake-model"));
        SummarizeAnalyzer analyzer = new(
            new FixedOcrEngine(" \r\n\t ", "en"),
            client,
            "fake-request-model");
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            analyzer.AnalyzeAsync(
                frame,
                ScanRequest.Create("unit-test", FeatureIds.Summarization),
                progress: null,
                CancellationToken.None));

        Assert.Equal(ScanFailureCode.NoTextDetected, exception.FailureCode);
        Assert.Equal(ScanStage.Ocr, exception.Stage);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsEmptyTextModelResponse()
    {
        RecordingTextModelClient client = new(new TextModelResponse(
            "  ",
            "fake-provider",
            "fake-model"));
        SummarizeAnalyzer analyzer = new(
            new FixedOcrEngine("Visible text", "en"),
            client,
            "fake-request-model");
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            analyzer.AnalyzeAsync(
                frame,
                ScanRequest.Create("unit-test", FeatureIds.Summarization),
                progress: null,
                CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationFailed, exception.FailureCode);
        Assert.Equal(ScanStage.Translation, exception.Stage);
        Assert.Single(client.Requests);
    }

    [Fact]
    public void BuiltInFeature_DeclaresExtractedTextBoundaryWithoutRegistration()
    {
        FeatureDescriptor descriptor = BuiltInFeatures.Summarization;

        Assert.Equal(new FeatureId("summarization"), descriptor.Id);
        Assert.Equal("要約", descriptor.DisplayName);
        Assert.Equal(FeatureInputKind.CapturedFrame, descriptor.InputKind);
        Assert.Equal(
            FeatureDataBoundary.ExtractedTextMayLeaveDevice,
            descriptor.DataBoundary);
    }

    private sealed class FixedOcrEngine(
        string text,
        string recognizerLanguage,
        string? warning = null) : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput(text, recognizerLanguage, warning));
    }

    private sealed class RecordingTextModelClient(TextModelResponse response) : ITextModelClient
    {
        public List<TextModelRequest> Requests { get; } = [];

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<TextModelResponse> GenerateAsync(
            TextModelRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastCancellationToken = cancellationToken;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Values { get; } = [];

        public void Report(ScanProgress value) => Values.Add(value);
    }
}
