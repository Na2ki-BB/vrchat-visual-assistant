using System.IO;
using VrcVa.Core;

namespace VrcVa.Windows.Capture;

internal sealed class ImageFileCaptureSource(string path) : ICaptureSource
{
    public async Task<CapturedFrame> CaptureAsync(
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new ScanException(
                ScanFailureCode.CaptureTargetNotFound,
                ScanStage.Capture,
                "選択した画像ファイルが見つかりません。");
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using MemoryStream stream = new(bytes, writable: false);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            return new CapturedFrame(
                bytes,
                image.Width,
                image.Height,
                GetMediaType(Path.GetExtension(path)),
                "explicit-image-file");
        }
        catch (ScanException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or OutOfMemoryException)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "選択した画像を読み込めませんでした。PNG、JPEG、BMPのいずれかを選択してください。",
                exception);
        }
    }

    private static string GetMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };
}
