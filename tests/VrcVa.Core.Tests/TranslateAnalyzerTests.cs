namespace VrcVa.Core.Tests;

public sealed class TranslateAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_NormalizesOcrAndTranslates()
    {
        RecordingTranslator translator = new();
        TranslateAnalyzer analyzer = new(
            new FixedOcrEngine("  KEEP   OUT\r\n\r\n Emergency  exit  "),
            translator);
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        AnalysisResult result = await analyzer.AnalyzeAsync(
            frame,
            ScanRequest.Create("unit-test"),
            progress: null,
            CancellationToken.None);

        Assert.Equal($"KEEP OUT{Environment.NewLine}Emergency exit", translator.SourceText);
        Assert.Equal("立入禁止。非常口。", result.JapaneseText);
        Assert.Equal(FeatureIds.Translation, result.FeatureResult.FeatureId);
        Assert.Equal(
            new[]
            {
                TranslationResultSectionIds.SourceText,
                TranslationResultSectionIds.JapaneseText,
            },
            result.FeatureResult.Sections.Select(section => section.Id));
        Assert.Equal("立入禁止。非常口。", result.FeatureResult.PrimarySection.Text);
        Assert.NotNull(result.FeatureResult.TextModelMetadata);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsEmptyOcrBeforeTranslation()
    {
        RecordingTranslator translator = new();
        TranslateAnalyzer analyzer = new(new FixedOcrEngine(" \r\n "), translator);
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() => analyzer.AnalyzeAsync(
            frame,
            ScanRequest.Create("unit-test"),
            progress: null,
            CancellationToken.None));

        Assert.Equal(ScanFailureCode.NoTextDetected, exception.FailureCode);
        Assert.Null(translator.SourceText);
    }

    private sealed class FixedOcrEngine(string text) : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput(text, "en"));
    }

    private sealed class RecordingTranslator : ITextTranslator
    {
        public string? SourceText { get; private set; }

        public Task<TranslationOutput> TranslateToJapaneseAsync(
            string sourceText,
            CancellationToken cancellationToken)
        {
            SourceText = sourceText;
            return Task.FromResult(new TranslationOutput(
                "立入禁止。非常口。",
                "fake",
                "fake-model"));
        }
    }
}
