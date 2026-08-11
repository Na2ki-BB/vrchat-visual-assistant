using System.Diagnostics;

namespace VrcVa.Core;

public sealed class OcrAnalyzer(IOcrEngine ocrEngine) : IAnalyzer
{
    public async Task<AnalysisResult> AnalyzeAsync(
        CapturedFrame frame,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(request);

        Stopwatch ocrTimer = Stopwatch.StartNew();
        progress?.Report(new ScanProgress(
            request.CorrelationId,
            ScanStage.Ocr,
            "画像から文字を認識しています…",
            ocrTimer.Elapsed));

        OcrOutput ocr = await ocrEngine
            .RecognizeAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        ocrTimer.Stop();

        string sourceText = TranslateAnalyzer.NormalizeOcrText(ocr.Text);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new ScanException(
                ScanFailureCode.NoTextDetected,
                ScanStage.Ocr,
                "英語テキストを検出できませんでした。文字を大きく表示するか、画像診断で切り分けてください。");
        }

        const string translationPending =
            "OCRは完了しました。翻訳サービスは未設定のため、日本語訳は生成していません。";
        string warning = string.IsNullOrWhiteSpace(ocr.Warning)
            ? translationPending
            : $"{ocr.Warning} {translationPending}";

        return new AnalysisResult(
            sourceText,
            string.Empty,
            ocr.RecognizerLanguage,
            "なし",
            "OCRのみ",
            ocrTimer.Elapsed,
            TimeSpan.Zero,
            warning);
    }
}
