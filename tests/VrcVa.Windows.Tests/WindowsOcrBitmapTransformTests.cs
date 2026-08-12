using VrcVa.Windows.Ocr;
using Windows.Graphics.Imaging;

namespace VrcVa.Windows.Tests;

public sealed class WindowsOcrBitmapTransformTests
{
    private const uint MaximumDimension = 10_000;

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1600)]
    public void CalculateScale_PreservesTwoTimesForMeasuredLowerResolutionInputs(
        uint width,
        uint height)
    {
        double scale = WindowsOcrBitmapTransform.CalculateScale(
            width,
            height,
            width,
            height,
            MaximumDimension);

        Assert.Equal(2d, scale, precision: 6);
    }

    [Fact]
    public void CalculateScale_DoesNotUpscaleMeasuredEyeMirror()
    {
        double scale = WindowsOcrBitmapTransform.CalculateScale(
            3072,
            3352,
            3072,
            3352,
            MaximumDimension);

        Assert.Equal(1d, scale, precision: 6);
    }

    [Fact]
    public void CalculateScale_DoesNotUpscaleBandsFromHighResolutionSource()
    {
        double scale = WindowsOcrBitmapTransform.CalculateScale(
            3072,
            3352,
            3072,
            1676,
            MaximumDimension);

        Assert.Equal(1d, scale, precision: 6);
    }

    [Fact]
    public void CalculateScale_LimitsOutputByTotalPixelCount()
    {
        double scale = WindowsOcrBitmapTransform.CalculateScale(
            3000,
            2000,
            3000,
            2000,
            MaximumDimension);

        long outputPixels = checked(
            (long)Math.Floor(3000 * scale)
            * (long)Math.Floor(2000 * scale));
        Assert.True(outputPixels <= WindowsOcrBitmapTransform.MaximumOutputPixelCount);
        Assert.InRange(scale, 1d, 2d);
    }

    [Fact]
    public void CalculateScale_LegacyModeReproducesPreviousTwoTimesBehavior()
    {
        double scale = WindowsOcrBitmapTransform.CalculateScale(
            3072,
            3352,
            3072,
            3352,
            MaximumDimension,
            OcrBitmapScaleMode.LegacyTwoTimes);

        Assert.Equal(2d, scale, precision: 6);
    }

    [Fact]
    public void Create_ScalesFullSourceBeforeConvertingCropBounds()
    {
        BitmapTransform transform = WindowsOcrBitmapTransform.Create(
            2560,
            1600,
            new BitmapBounds
            {
                X = 0,
                Y = 800,
                Width = 2560,
                Height = 800,
            });

        Assert.Equal(5120u, transform.ScaledWidth);
        Assert.Equal(3200u, transform.ScaledHeight);
        Assert.Equal(0u, transform.Bounds.X);
        Assert.Equal(1600u, transform.Bounds.Y);
        Assert.Equal(5120u, transform.Bounds.Width);
        Assert.Equal(1600u, transform.Bounds.Height);
    }

    [Fact]
    public void Create_UnscaledCropKeepsBoundsInsideFullSource()
    {
        BitmapTransform transform = WindowsOcrBitmapTransform.Create(
            3072,
            3352,
            new BitmapBounds
            {
                X = 0,
                Y = 1676,
                Width = 3072,
                Height = 1676,
            },
            OcrBitmapScaleMode.Unscaled);

        Assert.Equal(3072u, transform.ScaledWidth);
        Assert.Equal(3352u, transform.ScaledHeight);
        Assert.Equal(1676u, transform.Bounds.Y);
        Assert.Equal(1676u, transform.Bounds.Height);
    }
}
