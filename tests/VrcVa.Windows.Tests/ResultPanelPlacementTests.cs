using System.IO;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class ResultPanelPlacementTests
{
    [Fact]
    public void Default_UsesLeftHandAndMirrorsRightHandPreset()
    {
        ResultPanelPlacement left = ResultPanelPlacement.Default;
        ResultPanelPlacement right = ResultPanelPlacement.CreateDefault(ResultPanelAnchor.RightHand);

        Assert.Equal(ResultPanelAnchor.LeftHand, left.Anchor);
        Assert.Equal(-left.X, right.X);
        Assert.Equal(left.Y, right.Y);
        Assert.Equal(left.Z, right.Z);
        Assert.Equal(left.WidthMeters, right.WidthMeters);
    }

    [Fact]
    public void CreateTransform_WithNoRotation_ProducesIdentityAtPosition()
    {
        ResultPanelPlacement placement = new(
            ResultPanelAnchor.LeftHand,
            X: 0.1,
            Y: 0.2,
            Z: -0.3,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            WidthMeters: 0.6);

        ResultPanelTransform transform = placement.CreateTransform();

        Assert.Equal(1, transform.M0);
        Assert.Equal(1, transform.M5);
        Assert.Equal(1, transform.M10);
        Assert.Equal(0.1f, transform.M3);
        Assert.Equal(0.2f, transform.M7);
        Assert.Equal(-0.3f, transform.M11);
    }

    [Fact]
    public void CreateTransform_WithNinetyDegreeYaw_RotatesAroundY()
    {
        ResultPanelPlacement placement = ResultPanelPlacement.Default with
        {
            X = 0,
            Y = 0,
            Z = 0,
            YawDegrees = 90,
        };

        ResultPanelTransform transform = placement.CreateTransform();

        Assert.InRange(Math.Abs(transform.M0), 0, 0.00001);
        Assert.InRange(transform.M2, 0.99999, 1.00001);
        Assert.InRange(transform.M8, -1.00001, -0.99999);
        Assert.InRange(Math.Abs(transform.M10), 0, 0.00001);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1.51)]
    public void Validate_RejectsInvalidPosition(double x)
    {
        ResultPanelPlacement placement = ResultPanelPlacement.Default with { X = x };

        Assert.Throws<InvalidDataException>(placement.Validate);
    }

    [Theory]
    [InlineData((int)ResultPanelCalibrationAction.MoveLeft, -0.03, 0, 0, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MoveRight, 0.03, 0, 0, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MoveUp, 0, 0.03, 0, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MoveDown, 0, -0.03, 0, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MoveNear, 0, 0, 0.03, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MoveFar, 0, 0, -0.03, 0)]
    [InlineData((int)ResultPanelCalibrationAction.MakeSmaller, 0, 0, 0, -0.06)]
    [InlineData((int)ResultPanelCalibrationAction.MakeLarger, 0, 0, 0, 0.06)]
    public void CalibrationApply_UsesVisibleThreeCentimeterSteps(
        int actionValue,
        double xChange,
        double yChange,
        double zChange,
        double widthChange)
    {
        ResultPanelPlacement original = new(
            ResultPanelAnchor.LeftHand,
            X: 0,
            Y: 0,
            Z: -0.5,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            WidthMeters: 0.7);

        ResultPanelPlacement updated = ResultPanelCalibration.Apply(
            original,
            (ResultPanelCalibrationAction)actionValue);

        Assert.Equal(original.X + xChange, updated.X, precision: 5);
        Assert.Equal(original.Y + yChange, updated.Y, precision: 5);
        Assert.Equal(original.Z + zChange, updated.Z, precision: 5);
        Assert.Equal(original.WidthMeters + widthChange, updated.WidthMeters, precision: 5);
    }

    [Fact]
    public void CalibrationApply_ClampsAtSafetyBounds()
    {
        ResultPanelPlacement placement = ResultPanelPlacement.Default with
        {
            X = ResultPanelPlacement.MinimumPosition,
            WidthMeters = ResultPanelPlacement.MinimumWidthMeters,
        };

        ResultPanelPlacement updated = ResultPanelCalibration.Apply(
            ResultPanelCalibration.Apply(
                placement,
                ResultPanelCalibrationAction.MoveLeft),
            ResultPanelCalibrationAction.MakeSmaller);

        Assert.Equal(ResultPanelPlacement.MinimumPosition, updated.X);
        Assert.Equal(ResultPanelPlacement.MinimumWidthMeters, updated.WidthMeters);
    }

    [Fact]
    public void CalibrationApply_ResetUsesSelectedAnchorPreset()
    {
        ResultPanelPlacement changed = ResultPanelPlacement.CreateDefault(
            ResultPanelAnchor.RightHand) with
        {
            X = 0.5,
            Y = -0.5,
            WidthMeters = 1.2,
        };

        ResultPanelPlacement reset = ResultPanelCalibration.Apply(
            changed,
            ResultPanelCalibrationAction.Reset);

        Assert.Equal(
            ResultPanelPlacement.CreateDefault(ResultPanelAnchor.RightHand),
            reset);
    }

    [Fact]
    public void Store_WhenFileIsAbsent_ReturnsDefault()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            ResultPanelPlacementStore store = new(Path.Combine(directory, "settings.json"));

            Assert.Equal(ResultPanelPlacement.Default, store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Store_SaveAndLoad_RoundTripsPlacementWithoutTemporaryFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            ResultPanelPlacementStore store = new(path);
            ResultPanelPlacement expected = new(
                ResultPanelAnchor.RightHand,
                X: -0.12,
                Y: 0.2,
                Z: -0.35,
                PitchDegrees: -15,
                YawDegrees: 20,
                RollDegrees: 5,
                WidthMeters: 0.7);

            store.Save(expected);

            Assert.Equal(expected, store.Load());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Store_LoadRejectsUnsupportedVersion()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"Version\":2,\"ResultPanel\":null}");
            ResultPanelPlacementStore store = new(path);

            Assert.Throws<InvalidDataException>(store.Load);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "VrcVa.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
