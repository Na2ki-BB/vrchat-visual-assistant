using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VrcVa.Core;

public sealed partial class TranslateAnalyzer : IAnalyzer
{
    private readonly IOcrEngine _ocrEngine;
    private readonly ITextTranslator _translator;

    public TranslateAnalyzer(IOcrEngine ocrEngine, ITextTranslator translator)
    {
        _ocrEngine = ocrEngine;
        _translator = translator;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        CapturedFrame frame,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(request);

        Stopwatch total = Stopwatch.StartNew();
        progress?.Report(new ScanProgress(
            request.CorrelationId,
            ScanStage.Ocr,
            "画像から文字を認識しています…",
            total.Elapsed));

        Stopwatch ocrTimer = Stopwatch.StartNew();
        OcrOutput ocr = await _ocrEngine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
        ocrTimer.Stop();

        string sourceText = NormalizeOcrText(ocr.Text);
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new ScanException(
                ScanFailureCode.NoTextDetected,
                ScanStage.Ocr,
                "英語テキストを検出できませんでした。文字を大きく表示するか、画像診断で切り分けてください。");
        }

        progress?.Report(new ScanProgress(
            request.CorrelationId,
            ScanStage.Translation,
            "認識したテキストを日本語へ翻訳しています…",
            total.Elapsed));

        Stopwatch translationTimer = Stopwatch.StartNew();
        TranslationOutput translation = await _translator
            .TranslateToJapaneseAsync(sourceText, cancellationToken)
            .ConfigureAwait(false);
        translationTimer.Stop();

        return new AnalysisResult(
            sourceText,
            translation.Text.Trim(),
            ocr.RecognizerLanguage,
            translation.Provider,
            translation.Model,
            ocrTimer.Elapsed,
            translationTimer.Elapsed,
            ocr.Warning);
    }

    internal static string NormalizeOcrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        IEnumerable<string> normalizedLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => HorizontalWhitespaceRegex().Replace(line.Trim(), " "))
            .Where(line => line.Length > 0);

        return string.Join(Environment.NewLine, normalizedLines);
    }

    [GeneratedRegex("[\\t ]+")]
    private static partial Regex HorizontalWhitespaceRegex();
}

