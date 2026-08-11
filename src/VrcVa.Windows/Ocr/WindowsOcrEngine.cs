using VrcVa.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VrcVa.Windows.Ocr;

internal sealed class WindowsOcrEngine : IOcrEngine
{
    public async Task<OcrOutput> RecognizeAsync(
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Width > OcrEngine.MaxImageDimension || frame.Height > OcrEngine.MaxImageDimension)
        {
            throw new ScanException(
                ScanFailureCode.OcrUnavailable,
                ScanStage.Ocr,
                $"画像がWindows OCRの上限 {OcrEngine.MaxImageDimension}px を超えています。");
        }

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
            using SoftwareBitmap bitmap = await decoder
                .GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            OcrResult result = await engine
                .RecognizeAsync(bitmap)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return new OcrOutput(
                result.Text,
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

    private static (OcrEngine Engine, string? Warning) CreateEngine()
    {
        Language? english = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(
            language => language.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (english is not null)
        {
            OcrEngine? englishEngine = OcrEngine.TryCreateFromLanguage(english);
            if (englishEngine is not null)
            {
                return (englishEngine, null);
            }
        }

        OcrEngine? fallback = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fallback is not null)
        {
            return (
                fallback,
                $"英語OCR言語が未導入のため {fallback.RecognizerLanguage.LanguageTag} を使用しました。精度が低い場合はWindowsの英語OCR機能を追加してください。");
        }

        throw new ScanException(
            ScanFailureCode.OcrUnavailable,
            ScanStage.Ocr,
            "利用可能なWindows OCR言語がありません。Windowsの言語オプションでOCR機能を追加してください。");
    }
}

