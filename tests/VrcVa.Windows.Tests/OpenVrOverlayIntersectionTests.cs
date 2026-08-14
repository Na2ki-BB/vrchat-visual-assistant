using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrOverlayIntersectionTests
{
    [Fact]
    public void ValidateAbi_AcceptsPublishedOpenVr027Layouts() =>
        OpenVrInterop.ValidateAbi();

    [Theory]
    [InlineData(0, 720)]
    [InlineData(-1, 720)]
    [InlineData(1280, 0)]
    [InlineData(1280, -1)]
    [InlineData(float.NaN, 720)]
    [InlineData(1280, float.PositiveInfinity)]
    public void SurfaceSpec_RejectsNonPositiveOrNonFiniteSize(float width, float height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlaySurfaceSpec(width, height));

    [Fact]
    public void SurfaceSpec_RejectsDefaultValueWhenValidated() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => default(OverlaySurfaceSpec).Validate());

    [Theory]
    [InlineData(0, 0, 0, 720)]
    [InlineData(0.5, 0.5, 640, 360)]
    [InlineData(1, 1, 1280, 0)]
    public void SurfaceSpec_MapsNormalizedOpenVrCoordinatesToTopLeftUi(
        float openVrX,
        float openVrY,
        float expectedX,
        float expectedY)
    {
        OverlaySurfaceSpec surface = new(1280, 720);

        OverlayLocalPoint point = surface.ToTopLeftLocal(openVrX, openVrY);

        Assert.Equal(expectedX, point.X);
        Assert.Equal(expectedY, point.Y);
    }

    [Theory]
    [InlineData(800, 400, 0, 3, 2, 0.16666667, 0.75, 400, 200)]
    [InlineData(800, 400, 3, 3, 2, 0.16666667, 0.25, 400, 200)]
    [InlineData(800, 400, 5, 4, 2, 0.375, 0.25, 400, 200)]
    [InlineData(1280, 720, 3, 2, 3, 0.75, 0.5, 640, 360)]
    [InlineData(1280, 720, 5, 2, 3, 0.75, 0.16666667, 640, 360)]
    public void SurfaceSpec_MapsNormalizedAtlasCoordinatesToVisibleCell(
        float logicalWidth,
        float logicalHeight,
        int cell,
        int columns,
        int rows,
        float openVrX,
        float openVrY,
        float expectedX,
        float expectedY)
    {
        OverlayLocalPoint point = new OverlaySurfaceSpec(
            logicalWidth,
            logicalHeight).ToAtlasCellLocal(
            openVrX,
            openVrY,
            cell,
            columns,
            rows);

        Assert.Equal(expectedX, point.X, precision: 0);
        Assert.Equal(expectedY, point.Y, precision: 0);
    }

    [Fact]
    public void FromPose_UsesTranslationAndLocalNegativeZ()
    {
        OpenVrInputInterop.OpenVrPose pose = new(
            1, 0, 0, 1.25f,
            0, 1, 0, -2.5f,
            0, 0, 1, 3.75f);

        OpenVrRay ray = OpenVrRay.FromPose(pose);

        Assert.Equal(new OpenVrVector3(1.25f, -2.5f, 3.75f), ray.Source);
        Assert.Equal(new OpenVrVector3(0, 0, -1), ray.Direction);
    }

    [Fact]
    public void FromPose_NormalizesTransformedDirection()
    {
        OpenVrInputInterop.OpenVrPose pose = new(
            0, 0, -3, 10,
            0, 1, 0, 20,
            1, 0, 0, 30);

        OpenVrRay ray = OpenVrRay.FromPose(pose);

        Assert.Equal(new OpenVrVector3(10, 20, 30), ray.Source);
        Assert.Equal(1f, ray.Direction.X, precision: 6);
        Assert.Equal(0f, ray.Direction.Y, precision: 6);
        Assert.Equal(0f, ray.Direction.Z, precision: 6);
    }

    [Fact]
    public void FromPose_RejectsPoseWithoutForwardDirection()
    {
        OpenVrInputInterop.OpenVrPose pose = new(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 0, 0);

        Assert.Throws<ArgumentException>(() => OpenVrRay.FromPose(pose));
    }
}
