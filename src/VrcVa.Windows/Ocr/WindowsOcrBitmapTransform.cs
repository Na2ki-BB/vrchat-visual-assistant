using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace VrcVa.Windows.Ocr;

internal static class WindowsOcrBitmapTransform
{
    private const double MaximumScale = 2d;

    public static BitmapTransform Create(
        uint sourceWidth,
        uint sourceHeight,
        BitmapBounds? bounds = null)
    {
        uint inputWidth = bounds?.Width ?? sourceWidth;
        uint inputHeight = bounds?.Height ?? sourceHeight;
        if (inputWidth == 0 || inputHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "OCR image dimensions must be positive.");
        }

        double scale = Math.Min(
            MaximumScale,
            Math.Min(
                OcrEngine.MaxImageDimension / (double)inputWidth,
                OcrEngine.MaxImageDimension / (double)inputHeight));
        BitmapTransform transform = new()
        {
            ScaledWidth = Math.Max(1u, checked((uint)Math.Floor(inputWidth * scale))),
            ScaledHeight = Math.Max(1u, checked((uint)Math.Floor(inputHeight * scale))),
            InterpolationMode = BitmapInterpolationMode.Cubic,
        };
        if (bounds is BitmapBounds selectedBounds)
        {
            transform.Bounds = selectedBounds;
        }

        return transform;
    }
}
