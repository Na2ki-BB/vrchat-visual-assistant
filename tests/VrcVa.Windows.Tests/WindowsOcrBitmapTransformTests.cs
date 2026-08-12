using VrcVa.Windows.Ocr;

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
}
