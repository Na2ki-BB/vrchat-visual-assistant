using System.IO;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class WristLauncherPlacementTests
{
    [Fact]
    public void Default_MatchesCurrentLeftHandLauncherPlacement()
    {
        WristLauncherPlacement placement = WristLauncherPlacement.Default;

        Assert.Equal(-0.07, placement.X);
        Assert.Equal(0, placement.Y);
        Assert.Equal(0.22, placement.Z);
        Assert.Equal(
            WristLauncherRotation.FromEulerDegrees(180, 75, 0),
            placement.Rotation);
        Assert.Equal(0.38, placement.MenuWidthMeters);
        Assert.Equal(0.14, placement.ChipWidthMeters, precision: 12);
        AssertTransformEqual(
            CreateLegacyEulerTransform(-0.07, 0, 0.22, 180, 75, 0),
            placement.CreateTransform());
    }

    [Theory]
    [InlineData(180, 75, 0)]
    [InlineData(-175, 100, -5)]
    [InlineData(0, 90, 0)]
    [InlineData(0, -90, 0)]
    public void FromEulerDegrees_MatchesLegacyRzRyRxMatrix(
        double pitch,
        double yaw,
        double roll)
    {
        WristLauncherPlacement placement = new(
            X: 0.12,
            Y: -0.34,
            Z: 0.56,
            Rotation: WristLauncherRotation.FromEulerDegrees(pitch, yaw, roll),
            MenuWidthMeters: 0.5);

        AssertTransformEqual(
            CreateLegacyEulerTransform(0.12, -0.34, 0.56, pitch, yaw, roll),
            placement.CreateTransform());
    }

    [Fact]
    public void QuaternionConstructor_NormalizesAndCanonicalizesEquivalentSigns()
    {
        WristLauncherRotation positive = new(1, -2, 3, 4);
        WristLauncherRotation negative = new(-1, 2, -3, -4);

        Assert.Equal(positive, negative);
        Assert.Equal(1, QuaternionNorm(positive), precision: 12);
        Assert.True(positive.W > 0);

        WristLauncherRotation halfTurnPositive = new(1, 0, 0, 0);
        WristLauncherRotation halfTurnNegative = new(-1, 0, 0, 0);
        Assert.Equal(halfTurnPositive, halfTurnNegative);
        Assert.True(halfTurnPositive.X > 0);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 1)]
    [InlineData(0, double.PositiveInfinity, 0, 1)]
    [InlineData(0, 0, double.NegativeInfinity, 1)]
    [InlineData(0, 0, 0, double.NaN)]
    [InlineData(0, 0, 0, 0)]
    public void QuaternionConstructor_RejectsInvalidComponents(
        double x,
        double y,
        double z,
        double w)
    {
        Assert.Throws<InvalidDataException>(
            () => new WristLauncherRotation(x, y, z, w));
    }

    [Fact]
    public void Validate_RejectsDefaultUninitializedQuaternion()
    {
        WristLauncherPlacement placement = WristLauncherPlacement.Default with
        {
            Rotation = default,
        };

        Assert.Throws<InvalidDataException>(placement.Validate);
        Assert.Throws<InvalidDataException>(() => placement.CreateTransform());
    }

    [Theory]
    [InlineData(-1.5, -1.5, -1.5, 0.25)]
    [InlineData(1.5, 1.5, 1.5, 1.0)]
    public void Validate_AcceptsSafetyBoundariesAndProducesValidTransform(
        double x,
        double y,
        double z,
        double menuWidth)
    {
        WristLauncherPlacement placement = new(
            x,
            y,
            z,
            WristLauncherRotation.FromEulerDegrees(-175, 100, -5),
            menuWidth);

        placement.Validate();
        ResultPanelTransform transform = placement.CreateTransform();
        Assert.Equal((float)x, transform.M3);
        Assert.Equal((float)y, transform.M7);
        Assert.Equal((float)z, transform.M11);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, 0.38)]
    [InlineData(0, 1.51, 0, 0.38)]
    [InlineData(0, 0, -1.51, 0.38)]
    [InlineData(0, 0, 0, 0.24)]
    [InlineData(0, 0, 0, 1.01)]
    public void Validate_RejectsUnsafeOrNonFinitePlacementValues(
        double x,
        double y,
        double z,
        double menuWidth)
    {
        WristLauncherPlacement placement = new(
            x,
            y,
            z,
            WristLauncherRotation.FromEulerDegrees(0, 0, 0),
            menuWidth);

        Assert.Throws<InvalidDataException>(placement.Validate);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(0, 0, double.NegativeInfinity)]
    public void FromEulerDegrees_RejectsNonFiniteAngles(
        double pitch,
        double yaw,
        double roll)
    {
        Assert.Throws<InvalidDataException>(
            () => WristLauncherRotation.FromEulerDegrees(pitch, yaw, roll));
    }

    private static double QuaternionNorm(WristLauncherRotation rotation) => Math.Sqrt(
        (rotation.X * rotation.X)
        + (rotation.Y * rotation.Y)
        + (rotation.Z * rotation.Z)
        + (rotation.W * rotation.W));

    private static ResultPanelTransform CreateLegacyEulerTransform(
        double x,
        double y,
        double z,
        double pitchDegrees,
        double yawDegrees,
        double rollDegrees)
    {
        double pitch = pitchDegrees * Math.PI / 180;
        double yaw = yawDegrees * Math.PI / 180;
        double roll = rollDegrees * Math.PI / 180;
        double cx = Math.Cos(pitch);
        double sx = Math.Sin(pitch);
        double cy = Math.Cos(yaw);
        double sy = Math.Sin(yaw);
        double cz = Math.Cos(roll);
        double sz = Math.Sin(roll);

        return new ResultPanelTransform(
            M0: (float)(cz * cy),
            M1: (float)((cz * sy * sx) - (sz * cx)),
            M2: (float)((cz * sy * cx) + (sz * sx)),
            M3: (float)x,
            M4: (float)(sz * cy),
            M5: (float)((sz * sy * sx) + (cz * cx)),
            M6: (float)((sz * sy * cx) - (cz * sx)),
            M7: (float)y,
            M8: (float)-sy,
            M9: (float)(cy * sx),
            M10: (float)(cy * cx),
            M11: (float)z);
    }

    private static void AssertTransformEqual(
        ResultPanelTransform expected,
        ResultPanelTransform actual)
    {
        Assert.Equal(expected.M0, actual.M0, precision: 6);
        Assert.Equal(expected.M1, actual.M1, precision: 6);
        Assert.Equal(expected.M2, actual.M2, precision: 6);
        Assert.Equal(expected.M3, actual.M3, precision: 6);
        Assert.Equal(expected.M4, actual.M4, precision: 6);
        Assert.Equal(expected.M5, actual.M5, precision: 6);
        Assert.Equal(expected.M6, actual.M6, precision: 6);
        Assert.Equal(expected.M7, actual.M7, precision: 6);
        Assert.Equal(expected.M8, actual.M8, precision: 6);
        Assert.Equal(expected.M9, actual.M9, precision: 6);
        Assert.Equal(expected.M10, actual.M10, precision: 6);
        Assert.Equal(expected.M11, actual.M11, precision: 6);
    }
}
