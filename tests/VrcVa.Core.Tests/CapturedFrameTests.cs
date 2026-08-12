namespace VrcVa.Core.Tests;

public sealed class CapturedFrameTests
{
    [Fact]
    public void Constructor_DefaultsOcrScaleReferenceToFrameDimensions()
    {
        using CapturedFrame frame = new([1], 1280, 720, "image/png", "test");

        Assert.Equal(1280, frame.OcrScaleReferenceWidth);
        Assert.Equal(720, frame.OcrScaleReferenceHeight);
    }

    [Fact]
    public void Constructor_PreservesParentDimensionsForDerivedRegion()
    {
        using CapturedFrame frame = new(
            [1],
            3072,
            1676,
            "image/png",
            "test:ocr-band-1",
            3072,
            3352);

        Assert.Equal(3072, frame.OcrScaleReferenceWidth);
        Assert.Equal(3352, frame.OcrScaleReferenceHeight);
    }
}
