using System.IO;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class ResultPanelPlacementTests
{
    [Fact]
    public void Default_UsesLauncherTransformAndPreservesOtherAnchorPresets()
    {
        ResultPanelPlacement left = ResultPanelPlacement.Default;
        ResultPanelPlacement right = ResultPanelPlacement.CreateDefault(ResultPanelAnchor.RightHand);
        ResultPanelPlacement headset = ResultPanelPlacement.CreateDefault(ResultPanelAnchor.Headset);

        Assert.Equal(ResultPanelAnchor.LeftHand, left.Anchor);
        Assert.Equal(0.62, left.WidthMeters);
        AssertTransformEqual(
            WristLauncherPlacement.Default.CreateTransform(),
            left.CreateTransform());

        Assert.Equal(
            new ResultPanelPlacement(
                ResultPanelAnchor.RightHand,
                X: -0.08,
                Y: 0.12,
                Z: -0.28,
                PitchDegrees: 0,
                YawDegrees: 0,
                RollDegrees: 0,
                WidthMeters: 0.62),
            right);
        Assert.Equal(
            new ResultPanelPlacement(
                ResultPanelAnchor.Headset,
                X: 0,
                Y: -0.08,
                Z: -1.2,
                PitchDegrees: 0,
                YawDegrees: 0,
                RollDegrees: 0,
                WidthMeters: 1.15),
            headset);
    }

    [Theory]
    [InlineData(23, -41, 67)]
    [InlineData(180, 75, 0)]
    [InlineData(35, 90, -22)]
    [InlineData(-76, -90, 31)]
    [InlineData(35, 89.999999, -22)]
    [InlineData(-76, -89.999999, 31)]
    public void CreateAlignedToWristLauncher_MatchesEulerLauncherTransform(
        double pitch,
        double yaw,
        double roll)
    {
        WristLauncherPlacement launcher = new(
            X: 0.12,
            Y: -0.34,
            Z: 0.56,
            Rotation: WristLauncherRotation.FromEulerDegrees(pitch, yaw, roll),
            MenuWidthMeters: 0.38);

        ResultPanelPlacement result =
            ResultPanelPlacement.CreateAlignedToWristLauncher(launcher, 0.92);

        Assert.Equal(ResultPanelAnchor.LeftHand, result.Anchor);
        Assert.Equal(0.92, result.WidthMeters);
        AssertTransformEqual(launcher.CreateTransform(), result.CreateTransform());
        Assert.True(result.IsAlignedToWristLauncher(launcher));
    }

    [Fact]
    public void CreateAlignedToWristLauncher_MatchesCurrentPersistedQuaternion()
    {
        WristLauncherPlacement launcher = new(
            X: -0.08,
            Y: 0.04,
            Z: 0.17,
            Rotation: new WristLauncherRotation(
                x: 0.620757676,
                y: -0.303684034,
                z: -0.650666498,
                w: 0.314752321),
            MenuWidthMeters: 0.38);

        ResultPanelPlacement result =
            ResultPanelPlacement.CreateAlignedToWristLauncher(launcher, 0.92);

        AssertTransformEqual(launcher.CreateTransform(), result.CreateTransform());
        Assert.True(result.IsAlignedToWristLauncher(launcher));
    }

    [Fact]
    public void IsAlignedToWristLauncher_IgnoresIndependentPanelWidth()
    {
        WristLauncherPlacement launcher = WristLauncherPlacement.Default with
        {
            MenuWidthMeters = WristLauncherPlacement.MinimumMenuWidthMeters,
        };
        ResultPanelPlacement placement =
            ResultPanelPlacement.CreateAlignedToWristLauncher(
                launcher,
                widthMeters: ResultPanelPlacement.MaximumWidthMeters);

        Assert.True(placement.IsAlignedToWristLauncher(launcher));
    }

    [Theory]
    [InlineData(0.03, false)]
    [InlineData(0.0001, false)]
    [InlineData(0.000001, true)]
    public void IsAlignedToWristLauncher_UsesTransformTolerance(
        double xOffset,
        bool expected)
    {
        WristLauncherPlacement launcher = WristLauncherPlacement.Default;
        ResultPanelPlacement placement =
            ResultPanelPlacement.CreateAlignedToWristLauncher(launcher, 0.92) with
            {
                X = launcher.X + xOffset,
            };

        Assert.Equal(expected, placement.IsAlignedToWristLauncher(launcher));
    }

    [Theory]
    [InlineData((int)ResultPanelAnchor.RightHand)]
    [InlineData((int)ResultPanelAnchor.Headset)]
    public void IsAlignedToWristLauncher_RejectsNonLeftHandAnchors(
        int anchorValue)
    {
        ResultPanelPlacement placement =
            ResultPanelPlacement.CreateDefault((ResultPanelAnchor)anchorValue);

        Assert.False(
            placement.IsAlignedToWristLauncher(WristLauncherPlacement.Default));
    }

    [Fact]
    public void IsAlignedToWristLauncher_ValidatesBothPlacements()
    {
        ResultPanelPlacement invalidPanel = ResultPanelPlacement.Default with
        {
            X = double.NaN,
        };
        WristLauncherPlacement invalidLauncher = WristLauncherPlacement.Default with
        {
            X = double.NaN,
        };

        Assert.Throws<InvalidDataException>(
            () => invalidPanel.IsAlignedToWristLauncher(
                WristLauncherPlacement.Default));
        Assert.Throws<InvalidDataException>(
            () => ResultPanelPlacement.Default.IsAlignedToWristLauncher(
                invalidLauncher));
        Assert.Throws<ArgumentNullException>(
            () => ResultPanelPlacement.Default.IsAlignedToWristLauncher(null!));
    }

    [Fact]
    public void FollowWristLauncherChange_WhenAligned_FollowsPoseAndPreservesWidth()
    {
        WristLauncherPlacement previousLauncher = WristLauncherPlacement.Default;
        WristLauncherPlacement updatedLauncher = previousLauncher with
        {
            X = previousLauncher.X + 0.21,
            Y = previousLauncher.Y - 0.13,
            Rotation = WristLauncherRotation.FromEulerDegrees(34, -27, 61),
            MenuWidthMeters = 0.74,
        };
        ResultPanelPlacement placement =
            ResultPanelPlacement.CreateAlignedToWristLauncher(
                previousLauncher,
                widthMeters: 1.23);

        ResultPanelPlacement result = placement.FollowWristLauncherChange(
            previousLauncher,
            updatedLauncher);

        Assert.NotSame(placement, result);
        Assert.Equal(placement.WidthMeters, result.WidthMeters);
        Assert.True(result.IsAlignedToWristLauncher(updatedLauncher));
        AssertTransformEqual(updatedLauncher.CreateTransform(), result.CreateTransform());
    }

    [Fact]
    public void FollowWristLauncherChange_AfterPreviousFollow_UsesCurrentLauncherPose()
    {
        WristLauncherPlacement firstLauncher = WristLauncherPlacement.Default;
        WristLauncherPlacement secondLauncher = firstLauncher with
        {
            X = firstLauncher.X + 0.12,
            Rotation = WristLauncherRotation.FromEulerDegrees(160, 68, -12),
        };
        WristLauncherPlacement thirdLauncher = secondLauncher with
        {
            Y = secondLauncher.Y + 0.09,
            Rotation = WristLauncherRotation.FromEulerDegrees(145, 54, -25),
        };
        ResultPanelPlacement initialResult =
            ResultPanelPlacement.CreateAlignedToWristLauncher(
                firstLauncher,
                widthMeters: 0.92);
        ResultPanelPlacement secondResult = initialResult.FollowWristLauncherChange(
            firstLauncher,
            secondLauncher);

        ResultPanelPlacement thirdResult = secondResult.FollowWristLauncherChange(
            secondLauncher,
            thirdLauncher);

        Assert.Equal(initialResult.WidthMeters, thirdResult.WidthMeters);
        Assert.True(thirdResult.IsAlignedToWristLauncher(thirdLauncher));
    }

    [Fact]
    public void FollowWristLauncherChange_AfterIndependentAdjustment_PreservesInstanceAndValue()
    {
        WristLauncherPlacement previousLauncher = WristLauncherPlacement.Default;
        WristLauncherPlacement updatedLauncher = previousLauncher with
        {
            X = previousLauncher.X + 0.2,
        };
        ResultPanelPlacement adjusted =
            ResultPanelPlacement.CreateAlignedToWristLauncher(
                previousLauncher,
                widthMeters: 0.92) with
            {
                X = previousLauncher.X + 0.03,
            };

        ResultPanelPlacement result = adjusted.FollowWristLauncherChange(
            previousLauncher,
            updatedLauncher);

        Assert.Same(adjusted, result);
        Assert.Equal(adjusted, result);
    }

    [Theory]
    [InlineData((int)ResultPanelAnchor.RightHand)]
    [InlineData((int)ResultPanelAnchor.Headset)]
    public void FollowWristLauncherChange_ForNonLeftHandAnchor_PreservesInstance(
        int anchorValue)
    {
        WristLauncherPlacement previousLauncher = WristLauncherPlacement.Default;
        WristLauncherPlacement updatedLauncher = previousLauncher with
        {
            Z = previousLauncher.Z - 0.2,
        };
        ResultPanelPlacement placement = ResultPanelPlacement.CreateDefault(
            (ResultPanelAnchor)anchorValue);

        ResultPanelPlacement result = placement.FollowWristLauncherChange(
            previousLauncher,
            updatedLauncher);

        Assert.Same(placement, result);
        Assert.Equal(placement, result);
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
            PitchDegrees = 0,
            YawDegrees = 90,
            RollDegrees = 0,
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
