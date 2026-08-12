using VrcVa.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VrcVa.Windows.Ocr;

internal sealed class WindowsOcrEngine(
    OcrBitmapScaleMode scaleMode = OcrBitmapScaleMode.Adaptive) : IOcrEngine
{
    internal const string EnglishRecognizerInstallationSteps =
        "設定 → 時刻と言語 → 言語と地域 → Englishを追加 → 言語のオプション → "
        + "オプション機能 → 文字認識 (OCR)。Windowsの表示言語を英語に変更する必要はありません。";

    internal const string EnglishRecognizerMissingWarning =
        "英語OCR言語が未導入です。英語を日本語認識器で読むため、結果に存在しない漢字や全角記号が混ざり、"
        + "ほぼ読めない場合があります。"
        + EnglishRecognizerInstallationSteps;

    public async Task<OcrOutput> RecognizeAsync(
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        (OcrEngine engine, string? warning) = CreateEngine();

        try
        {
            using InMemoryRandomAccessStream randomAccessStream = new();
            using (DataWriter writer = new(randomAccessStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(frame.EncodedImage.ToArray());
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
                writer.DetachStream();
            }

            randomAccessStream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder
                .CreateAsync(randomAccessStream)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            BitmapTransform transform = WindowsOcrBitmapTransform.Create(
                decoder.PixelWidth,
                decoder.PixelHeight,
                scaleMode: scaleMode,
                scaleReferenceWidth: checked((uint)frame.OcrScaleReferenceWidth),
                scaleReferenceHeight: checked((uint)frame.OcrScaleReferenceHeight));
            using SoftwareBitmap bitmap = await decoder
                .GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            OcrResult result = await engine
                .RecognizeAsync(bitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            string text = string.Join(
                Environment.NewLine,
                result.Lines.Select(line => line.Text));

            return new OcrOutput(
                text,
                engine.RecognizerLanguage.LanguageTag,
                warning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ScanException(
                ScanFailureCode.OcrUnavailable,
                ScanStage.Ocr,
                "Windows OCRで画像を処理できませんでした。画像形式と言語機能を確認してください。",
                exception);
        }
    }

    internal static IReadOnlyList<string> GetAvailableLanguageTags() =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static bool HasEnglishRecognizer(IEnumerable<string> languageTags) =>
        languageTags.Any(IsEnglishLanguageTag);

    internal static string? GetSelectedRecognizerLanguageTag()
    {
        OcrEngine? engine = TryCreateEnglishEngine()
            ?? OcrEngine.TryCreateFromUserProfileLanguages();
        return engine?.RecognizerLanguage.LanguageTag;
    }

    private static (OcrEngine Engine, string? Warning) CreateEngine()
    {
        OcrEngine? englishEngine = TryCreateEnglishEngine();
        if (englishEngine is not null)
        {
            return (englishEngine, null);
        }

        OcrEngine? fallback = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fallback is not null)
        {
            return (
                fallback,
                $"{fallback.RecognizerLanguage.LanguageTag} 認識器を代わりに使用しました。"
                + EnglishRecognizerMissingWarning);
        }

        throw new ScanException(
            ScanFailureCode.OcrUnavailable,
            ScanStage.Ocr,
            "利用可能なWindows OCR言語がありません。Windowsの言語オプションでOCR機能を追加してください。");
    }

    private static OcrEngine? TryCreateEnglishEngine()
    {
        Language? english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(
            language => IsEnglishLanguageTag(language.LanguageTag));
        return english is null
            ? null
            : OcrEngine.TryCreateFromLanguage(english);
    }

    private static bool IsEnglishLanguageTag(string languageTag) =>
        languageTag.Equals("en", StringComparison.OrdinalIgnoreCase)
        || languageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
}
