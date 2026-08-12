using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace VrcVa.Windows.Ocr;

internal static class WindowsOcrBitmapTransform
{
    private const double MaximumScale = 2d;
    internal const long HighResolutionSourcePixelThreshold = 8_000_000;
    internal const long MaximumOutputPixelCount = 16_777_216;

    public static BitmapTransform Create(
        uint sourceWidth,
        uint sourceHeight,
        BitmapBounds? bounds = null,
        OcrBitmapScaleMode scaleMode = OcrBitmapScaleMode.Adaptive)
    {
        uint inputWidth = bounds?.Width ?? sourceWidth;
        uint inputHeight = bounds?.Height ?? sourceHeight;
        if (inputWidth == 0 || inputHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "OCR image dimensions must be positive.");
        }

        double scale = CalculateScale(
            sourceWidth,
            sourceHeight,
            inputWidth,
            inputHeight,
            OcrEngine.MaxImageDimension,
            scaleMode);
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

    internal static double CalculateScale(
        uint sourceWidth,
        uint sourceHeight,
        uint inputWidth,
        uint inputHeight,
        uint maximumImageDimension,
        OcrBitmapScaleMode scaleMode = OcrBitmapScaleMode.Adaptive)
    {
        if (sourceWidth == 0
            || sourceHeight == 0
            || inputWidth == 0
            || inputHeight == 0
            || maximumImageDimension == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputWidth),
                "OCR image dimensions must be positive.");
        }

        long sourcePixels = checked((long)sourceWidth * sourceHeight);
        long inputPixels = checked((long)inputWidth * inputHeight);
        double desiredScale = scaleMode switch
        {
            OcrBitmapScaleMode.LegacyTwoTimes => MaximumScale,
            OcrBitmapScaleMode.Unscaled => 1d,
            _ when sourcePixels >= HighResolutionSourcePixelThreshold => 1d,
            _ => MaximumScale,
        };
        double pixelBudgetScale = scaleMode == OcrBitmapScaleMode.LegacyTwoTimes
            ? MaximumScale
            : Math.Sqrt(MaximumOutputPixelCount / (double)inputPixels);
        double dimensionScale = Math.Min(
            maximumImageDimension / (double)inputWidth,
            maximumImageDimension / (double)inputHeight);

        return Math.Min(desiredScale, Math.Min(pixelBudgetScale, dimensionScale));
    }
}
