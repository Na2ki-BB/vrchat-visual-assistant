namespace VrcVa.Core.Tests;

public sealed class OcrAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ReturnsNormalizedOcrWithoutTranslation()
    {
        OcrAnalyzer analyzer = new(new FixedOcrEngine(
            "  KEEP   OUT\r\n\r\n Emergency  exit  "));
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        AnalysisResult result = await analyzer.AnalyzeAsync(
            frame,
            ScanRequest.Create("unit-test"),
            progress: null,
            CancellationToken.None);

        Assert.Equal($"KEEP OUT{Environment.NewLine}Emergency exit", result.SourceText);
        Assert.Empty(result.JapaneseText);
        Assert.Equal("OCRのみ", result.TranslationModel);
        Assert.Contains("翻訳サービスは未設定", result.Warning, StringComparison.Ordinal);
        Assert.Equal(FeatureIds.Translation, result.FeatureResult.FeatureId);
        Assert.Equal(
            TranslationResultSectionIds.SourceText,
            Assert.Single(result.FeatureResult.Sections).Id);
        Assert.Equal(result.SourceText, result.FeatureResult.PrimarySection.Text);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsEmptyOcr()
    {
        OcrAnalyzer analyzer = new(new FixedOcrEngine(" \r\n "));
        using CapturedFrame frame = new([1], 1, 1, "image/png", "test");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            analyzer.AnalyzeAsync(
                frame,
                ScanRequest.Create("unit-test"),
                progress: null,
                CancellationToken.None));

        Assert.Equal(ScanFailureCode.NoTextDetected, exception.FailureCode);
    }

    private sealed class FixedOcrEngine(string text) : IOcrEngine
    {
        public Task<OcrOutput> RecognizeAsync(
            CapturedFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OcrOutput(text, "en"));
    }
}
