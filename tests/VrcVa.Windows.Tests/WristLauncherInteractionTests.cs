using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class PointerActivationGateTests
{
    [Fact]
    public void TriggerAlreadyHeldWhenHoverStarts_RequiresReleaseBeforeActivation()
    {
        PointerActivationGate gate = new();

        Assert.Equal(0, gate.Update(1, selectActive: true, selectPressed: true, selectChanged: true));
        Assert.Equal(0, gate.Update(1, selectActive: true, selectPressed: false, selectChanged: true));
        Assert.Equal(1, gate.Update(1, selectActive: true, selectPressed: true, selectChanged: true));
    }

    [Fact]
    public void ChangingHoverTargetWhileHeld_DoesNotActivateNewTarget()
    {
        PointerActivationGate gate = new();

        Assert.Equal(0, gate.Update(1, selectActive: true, selectPressed: false, selectChanged: false));
        Assert.Equal(1, gate.Update(1, selectActive: true, selectPressed: true, selectChanged: true));
        Assert.Equal(0, gate.Update(2, selectActive: true, selectPressed: true, selectChanged: false));
        Assert.Equal(0, gate.Update(2, selectActive: true, selectPressed: false, selectChanged: true));
        Assert.Equal(2, gate.Update(2, selectActive: true, selectPressed: true, selectChanged: true));
    }
}

public sealed class WristLauncherStateMachineTests
{
    [Fact]
    public void LauncherPlacement_SitsAboveAndBehindTheTrackedControllerLikeAWristwatch()
    {
        WristLauncherPlacement placement = SteamVrResultPanel.LauncherPlacement;
        ResultPanelTransform transform = placement.CreateTransform();

        Assert.Equal(WristLauncherPlacement.Default, placement);
        Assert.Equal(-0.07, placement.X, precision: 4);
        Assert.Equal(0.22, placement.Z, precision: 4);
        Assert.Equal(
            WristLauncherRotation.FromEulerDegrees(180, 75, 0),
            placement.Rotation);
        Assert.True(-transform.M2 > 0.9);
        Assert.Equal(0, -transform.M6, precision: 4);
        Assert.True(-transform.M10 > 0.2);
    }

    [Fact]
    public void FacingForArmDelay_ArmsWithExitHysteresis()
    {
        WristLauncherStateMachine state = new();
        Assert.True(state.Start());

        Assert.False(state.UpdateFacing(true, true, TimeSpan.Zero));
        Assert.False(state.UpdateFacing(true, true, TimeSpan.FromMilliseconds(149)));
        Assert.True(state.UpdateFacing(true, true, TimeSpan.FromMilliseconds(150)));
        Assert.Equal(WristLauncherView.ArmedChip, state.View);

        Assert.False(state.UpdateFacing(false, true, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(WristLauncherView.ArmedChip, state.View);
        Assert.True(state.UpdateFacing(false, false, TimeSpan.FromMilliseconds(201)));
        Assert.Equal(WristLauncherView.DimChip, state.View);
    }

    [Fact]
    public void TranslationFlow_HidesLauncherUntilResultCloses()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        _ = state.UpdateFacing(true, true, TimeSpan.Zero);
        _ = state.UpdateFacing(true, true, WristLauncherStateMachine.ArmDelay);

        Assert.True(state.ExpandMenu());
        Assert.True(state.BeginScan());
        Assert.Equal(WristLauncherView.Scanning, state.View);
        state.ShowResult();
        Assert.Equal(WristLauncherView.Result, state.View);
        state.ReturnToChip();
        Assert.Equal(WristLauncherView.DimChip, state.View);
    }

    [Fact]
    public void DimChip_CanOpenMenuWhenFacingFeedbackDoesNotArm()
    {
        WristLauncherStateMachine state = new();
        Assert.True(state.Start());

        Assert.True(state.ExpandMenu());
        Assert.Equal(WristLauncherView.Menu, state.View);
    }

    [Fact]
    public void ExternalScan_ReplacesAnAlreadyVisibleResult()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        state.ShowResult();

        Assert.True(state.BeginScan());
        Assert.Equal(WristLauncherView.Scanning, state.View);
    }

    [Fact]
    public void PoseLoss_CollapsesMenuWithoutAHeadsetFallbackState()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        _ = state.UpdateFacing(true, true, TimeSpan.Zero);
        _ = state.UpdateFacing(true, true, WristLauncherStateMachine.ArmDelay);
        _ = state.ExpandMenu();

        Assert.True(state.HandlePoseLoss());
        Assert.Equal(WristLauncherView.DimChip, state.View);
    }

    [Fact]
    public void CalibrationFlow_ReturnsFromMenuToDimChip()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        _ = state.ExpandMenu();

        Assert.True(state.BeginCalibration());
        Assert.Equal(WristLauncherView.Calibrating, state.View);
        Assert.True(state.EndCalibration());
        Assert.Equal(WristLauncherView.DimChip, state.View);
    }

    [Fact]
    public void PoseLoss_DoesNotEndActiveCalibration()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        _ = state.ExpandMenu();
        _ = state.BeginCalibration();

        Assert.False(state.HandlePoseLoss());
        Assert.Equal(WristLauncherView.Calibrating, state.View);
    }

    [Fact]
    public void ScanCanReplaceActiveCalibration()
    {
        WristLauncherStateMachine state = new();
        _ = state.Start();
        _ = state.ExpandMenu();
        _ = state.BeginCalibration();

        Assert.True(state.BeginScan());
        Assert.Equal(WristLauncherView.Scanning, state.View);
    }
}

public sealed class WristLauncherTextureTests
{
    [Fact]
    public void VisibleControlsAndHitTestsShareExactRectangles()
    {
        WristLauncherTexture texture = new();

        Assert.Equal(
            WristLauncherAction.Expand,
            texture.HitTest(WristLauncherView.ArmedChip, 400, 200));
        Assert.Equal(
            WristLauncherAction.Expand,
            texture.HitTest(WristLauncherView.DimChip, 400, 200));
        Assert.Equal(
            WristLauncherAction.Translate,
            texture.HitTest(WristLauncherView.Menu, 229, 224));
        Assert.Equal(
            WristLauncherAction.Calibrate,
            texture.HitTest(WristLauncherView.Menu, 570, 224));
        Assert.Equal(
            WristLauncherAction.CloseMenu,
            texture.HitTest(WristLauncherView.Menu, 725, 58));
        Assert.Equal(
            WristLauncherAction.None,
            texture.HitTest(WristLauncherView.Menu, 20, 20));
    }

    [Fact]
    public void EveryVisibleControl_UsesItsSharedBoundsAtCornersAndOnePixelOutside()
    {
        WristLauncherTexture texture = new();

        AssertHitBounds(
            WristLauncherTexture.ChipBounds,
            (x, y) => texture.HitTest(WristLauncherView.DimChip, x, y),
            WristLauncherAction.Expand);
        AssertHitBounds(
            WristLauncherTexture.ChipBounds,
            (x, y) => texture.HitTest(WristLauncherView.ArmedChip, x, y),
            WristLauncherAction.Expand);
        AssertHitBounds(
            WristLauncherTexture.TranslationButtonBounds,
            (x, y) => texture.HitTest(WristLauncherView.Menu, x, y),
            WristLauncherAction.Translate);
        AssertHitBounds(
            WristLauncherTexture.CalibrationButtonBounds,
            (x, y) => texture.HitTest(WristLauncherView.Menu, x, y),
            WristLauncherAction.Calibrate);
        AssertHitBounds(
            WristLauncherTexture.CloseMenuButtonBounds,
            (x, y) => texture.HitTest(WristLauncherView.Menu, x, y),
            WristLauncherAction.CloseMenu);
    }

    [Fact]
    public void RenderRgba_CreatesFixedEightCellAtlas()
    {
        WristLauncherTexture texture = new();

        byte[] pixels = texture.RenderAtlasRgba();

        Assert.Equal(
            WristLauncherTexture.AtlasPixelWidth * WristLauncherTexture.AtlasPixelHeight * 4,
            pixels.Length);
    }

    [Theory]
    [InlineData((int)WristLauncherView.DimChip, (int)WristLauncherAction.None, 0)]
    [InlineData((int)WristLauncherView.DimChip, (int)WristLauncherAction.Expand, 2)]
    [InlineData((int)WristLauncherView.ArmedChip, (int)WristLauncherAction.None, 1)]
    [InlineData((int)WristLauncherView.ArmedChip, (int)WristLauncherAction.Expand, 2)]
    [InlineData((int)WristLauncherView.Menu, (int)WristLauncherAction.None, 3)]
    [InlineData((int)WristLauncherView.Menu, (int)WristLauncherAction.Translate, 4)]
    [InlineData((int)WristLauncherView.Menu, (int)WristLauncherAction.Calibrate, 5)]
    [InlineData((int)WristLauncherView.Menu, (int)WristLauncherAction.CloseMenu, 6)]
    public void AtlasCell_IsStableForEachVisualState(
        int view,
        int action,
        int expected) =>
        Assert.Equal(
            expected,
            new WristLauncherTexture().GetAtlasCell(
                (WristLauncherView)view,
                (WristLauncherAction)action));

    private static void AssertHitBounds(
        System.Windows.Rect bounds,
        Func<float, float, WristLauncherAction> hitTest,
        WristLauncherAction expected)
    {
        float left = (float)bounds.Left;
        float right = (float)bounds.Right;
        float top = (float)bounds.Top;
        float bottom = (float)bounds.Bottom;
        float centerX = (left + right) / 2;
        float centerY = (top + bottom) / 2;
        (float X, float Y)[] inside =
        [
            (centerX, centerY),
            (centerX, top),
            (right, centerY),
            (centerX, bottom),
            (left, centerY),
            (left, top),
            (right, top),
            (left, bottom),
            (right, bottom),
        ];
        foreach ((float x, float y) in inside)
        {
            Assert.Equal(expected, hitTest(x, y));
        }

        (float X, float Y)[] outside =
        [
            (centerX, top - 1),
            (right + 1, centerY),
            (centerX, bottom + 1),
            (left - 1, centerY),
            (left - 1, top - 1),
            (right + 1, top - 1),
            (left - 1, bottom + 1),
            (right + 1, bottom + 1),
        ];
        foreach ((float x, float y) in outside)
        {
            Assert.Equal(WristLauncherAction.None, hitTest(x, y));
        }
    }
}

public sealed class WristLauncherCalibrationTests
{
    [Fact]
    public void EveryAdjustmentAction_AppliesTheDocumentedLocalStep()
    {
        WristLauncherPlacement placement = new(
            X: 0,
            Y: 0,
            Z: 0,
            Rotation: WristLauncherRotation.FromEulerDegrees(0, 0, 0),
            MenuWidthMeters: 0.5);
        (WristLauncherCalibrationAction Action, WristLauncherPlacement Expected)[] cases =
        [
            (
                WristLauncherCalibrationAction.MoveTowardHandBack,
                placement with { X = -0.01 }),
            (
                WristLauncherCalibrationAction.MoveTowardPalm,
                placement with { X = 0.01 }),
            (
                WristLauncherCalibrationAction.MoveDown,
                placement with { Y = -0.01 }),
            (
                WristLauncherCalibrationAction.MoveUp,
                placement with { Y = 0.01 }),
            (
                WristLauncherCalibrationAction.MoveTowardFingertips,
                placement with { Z = -0.01 }),
            (
                WristLauncherCalibrationAction.MoveTowardElbow,
                placement with { Z = 0.01 }),
            (
                WristLauncherCalibrationAction.MakeSmaller,
                placement with { MenuWidthMeters = 0.475 }),
            (
                WristLauncherCalibrationAction.MakeLarger,
                placement with { MenuWidthMeters = 0.525 }),
        ];

        foreach ((WristLauncherCalibrationAction action, WristLauncherPlacement expected) in cases)
        {
            WristLauncherPlacement actual = WristLauncherCalibration.Apply(placement, action);

            AssertPlacement(expected, actual);
        }
    }

    [Fact]
    public void ControlActions_DoNotMutatePlacementUntilTheCallerCommitsThem()
    {
        WristLauncherPlacement placement = WristLauncherPlacement.Default with
        {
            X = 0.12,
            Rotation = WristLauncherRotation.FromEulerDegrees(12, -23, 35),
        };

        Assert.Same(
            placement,
            WristLauncherCalibration.Apply(
                placement,
                WristLauncherCalibrationAction.None));
        Assert.Same(
            placement,
            WristLauncherCalibration.Apply(
                placement,
                WristLauncherCalibrationAction.Cancel));
        Assert.Same(
            placement,
            WristLauncherCalibration.Apply(
                placement,
                WristLauncherCalibrationAction.Save));
    }

    [Fact]
    public void Reset_RestoresAllDefaultPlacementValues()
    {
        WristLauncherPlacement changed = new(
            X: 0.5,
            Y: -0.4,
            Z: 0.3,
            Rotation: WristLauncherRotation.FromEulerDegrees(-25, 110, -90),
            MenuWidthMeters: 0.75);

        Assert.Equal(
            WristLauncherPlacement.Default,
            WristLauncherCalibration.Apply(
                changed,
                WristLauncherCalibrationAction.Reset));
    }

    [Fact]
    public void PositionAndSizeAdjustments_StopAtValidatedBounds()
    {
        WristLauncherPlacement maximum = WristLauncherPlacement.Default with
        {
            X = WristLauncherPlacement.MaximumPosition,
            Y = WristLauncherPlacement.MaximumPosition,
            Z = WristLauncherPlacement.MaximumPosition,
            MenuWidthMeters = WristLauncherPlacement.MaximumMenuWidthMeters,
        };
        WristLauncherPlacement minimum = WristLauncherPlacement.Default with
        {
            X = WristLauncherPlacement.MinimumPosition,
            Y = WristLauncherPlacement.MinimumPosition,
            Z = WristLauncherPlacement.MinimumPosition,
            MenuWidthMeters = WristLauncherPlacement.MinimumMenuWidthMeters,
        };

        Assert.Equal(
            WristLauncherPlacement.MaximumPosition,
            WristLauncherCalibration.Apply(
                maximum,
                WristLauncherCalibrationAction.MoveTowardPalm).X);
        Assert.Equal(
            WristLauncherPlacement.MaximumPosition,
            WristLauncherCalibration.Apply(
                maximum,
                WristLauncherCalibrationAction.MoveUp).Y);
        Assert.Equal(
            WristLauncherPlacement.MaximumPosition,
            WristLauncherCalibration.Apply(
                maximum,
                WristLauncherCalibrationAction.MoveTowardElbow).Z);
        Assert.Equal(
            WristLauncherPlacement.MaximumMenuWidthMeters,
            WristLauncherCalibration.Apply(
                maximum,
                WristLauncherCalibrationAction.MakeLarger).MenuWidthMeters);
        Assert.Equal(
            WristLauncherPlacement.MinimumPosition,
            WristLauncherCalibration.Apply(
                minimum,
                WristLauncherCalibrationAction.MoveTowardHandBack).X);
        Assert.Equal(
            WristLauncherPlacement.MinimumPosition,
            WristLauncherCalibration.Apply(
                minimum,
                WristLauncherCalibrationAction.MoveDown).Y);
        Assert.Equal(
            WristLauncherPlacement.MinimumPosition,
            WristLauncherCalibration.Apply(
                minimum,
                WristLauncherCalibrationAction.MoveTowardFingertips).Z);
        Assert.Equal(
            WristLauncherPlacement.MinimumMenuWidthMeters,
            WristLauncherCalibration.Apply(
                minimum,
                WristLauncherCalibrationAction.MakeSmaller).MenuWidthMeters);
    }

    [Theory]
    [InlineData((int)WristLauncherCalibrationAction.DecreasePitch, 0, -5)]
    [InlineData((int)WristLauncherCalibrationAction.IncreasePitch, 0, 5)]
    [InlineData((int)WristLauncherCalibrationAction.DecreaseYaw, 1, -5)]
    [InlineData((int)WristLauncherCalibrationAction.IncreaseYaw, 1, 5)]
    [InlineData((int)WristLauncherCalibrationAction.DecreaseRoll, 2, -5)]
    [InlineData((int)WristLauncherCalibrationAction.IncreaseRoll, 2, 5)]
    public void RotationAdjustments_PostMultiplyTheRequestedLocalAxis(
        int actionValue,
        int axis,
        double degrees)
    {
        WristLauncherPlacement placement = new(
            X: 0,
            Y: 0,
            Z: 0,
            Rotation: WristLauncherRotation.FromEulerDegrees(-175, 100, -5),
            MenuWidthMeters: 0.5);
        ResultPanelTransform before = placement.CreateTransform();

        ResultPanelTransform actual =
            WristLauncherCalibration.Apply(
                placement,
                (WristLauncherCalibrationAction)actionValue).CreateTransform();
        float[,] expected = MultiplyRotation(
            RotationElements(before),
            CreateAxisRotation(axis, degrees));

        AssertRotationEqual(expected, RotationElements(actual));
    }

    [Theory]
    [InlineData((int)WristLauncherCalibrationAction.DecreaseRoll)]
    [InlineData((int)WristLauncherCalibrationAction.IncreaseRoll)]
    public void RollAdjustment_PreservesLauncherFrontDirection(int actionValue)
    {
        WristLauncherPlacement placement = new(
            X: 0,
            Y: 0,
            Z: 0,
            Rotation: WristLauncherRotation.FromEulerDegrees(-175, 100, -5),
            MenuWidthMeters: 0.5);
        ResultPanelTransform before = placement.CreateTransform();

        ResultPanelTransform after =
            WristLauncherCalibration.Apply(
                placement,
                (WristLauncherCalibrationAction)actionValue).CreateTransform();

        Assert.Equal(before.M2, after.M2, precision: 6);
        Assert.Equal(before.M6, after.M6, precision: 6);
        Assert.Equal(before.M10, after.M10, precision: 6);
    }

    [Theory]
    [InlineData(
        (int)WristLauncherCalibrationAction.IncreasePitch,
        (int)WristLauncherCalibrationAction.DecreasePitch)]
    [InlineData(
        (int)WristLauncherCalibrationAction.IncreaseYaw,
        (int)WristLauncherCalibrationAction.DecreaseYaw)]
    [InlineData(
        (int)WristLauncherCalibrationAction.IncreaseRoll,
        (int)WristLauncherCalibrationAction.DecreaseRoll)]
    public void OppositeRotationAdjustments_RestoreTheOriginalOrientation(
        int increaseValue,
        int decreaseValue)
    {
        WristLauncherCalibrationAction increase =
            (WristLauncherCalibrationAction)increaseValue;
        WristLauncherCalibrationAction decrease =
            (WristLauncherCalibrationAction)decreaseValue;
        WristLauncherPlacement original = new(
            X: 0,
            Y: 0,
            Z: 0,
            Rotation: WristLauncherRotation.FromEulerDegrees(-175, 100, -5),
            MenuWidthMeters: 0.5);

        WristLauncherPlacement increaseThenDecrease = WristLauncherCalibration.Apply(
            WristLauncherCalibration.Apply(original, increase),
            decrease);
        WristLauncherPlacement decreaseThenIncrease = WristLauncherCalibration.Apply(
            WristLauncherCalibration.Apply(original, decrease),
            increase);

        AssertRotationEqual(
            RotationElements(original.CreateTransform()),
            RotationElements(increaseThenDecrease.CreateTransform()));
        AssertRotationEqual(
            RotationElements(original.CreateTransform()),
            RotationElements(decreaseThenIncrease.CreateTransform()));
    }

    [Fact]
    public void RepeatedRotationAdjustments_RemainNormalized()
    {
        WristLauncherPlacement placement = new(
            X: 0,
            Y: 0,
            Z: 0,
            Rotation: WristLauncherRotation.FromEulerDegrees(-175, 100, -5),
            MenuWidthMeters: 0.5);
        WristLauncherCalibrationAction[] actions =
        [
            WristLauncherCalibrationAction.IncreasePitch,
            WristLauncherCalibrationAction.DecreaseYaw,
            WristLauncherCalibrationAction.IncreaseRoll,
        ];

        for (int index = 0; index < 10_000; index++)
        {
            placement = WristLauncherCalibration.Apply(
                placement,
                actions[index % actions.Length]);
        }

        WristLauncherRotation rotation = placement.Rotation;
        double norm = Math.Sqrt(
            (rotation.X * rotation.X)
            + (rotation.Y * rotation.Y)
            + (rotation.Z * rotation.Z)
            + (rotation.W * rotation.W));
        Assert.Equal(1, norm, precision: 12);
        placement.Validate();
    }

    [Fact]
    public void EveryVisibleCalibrationControl_UsesItsSharedBounds()
    {
        WristLauncherCalibrationAction[] expectedActions =
            Enum.GetValues<WristLauncherCalibrationAction>()
                .Where(action => action != WristLauncherCalibrationAction.None)
                .ToArray();
        Assert.Equal(
            expectedActions,
            ResultPanelTexture.WristLauncherCalibrationControls
                .Select(button => button.Action)
                .ToArray());

        foreach (WristLauncherCalibrationButton button in
            ResultPanelTexture.WristLauncherCalibrationControls)
        {
            float left = (float)button.Bounds.Left;
            float right = (float)button.Bounds.Right;
            float top = (float)button.Bounds.Top;
            float bottom = (float)button.Bounds.Bottom;
            float centerX = (left + right) / 2;
            float centerY = (top + bottom) / 2;
            (float X, float Y)[] inside =
            [
                (centerX, centerY),
                (centerX, top),
                (right, centerY),
                (centerX, bottom),
                (left, centerY),
                (left, top),
                (right, top),
                (left, bottom),
                (right, bottom),
            ];
            foreach ((float x, float y) in inside)
            {
                Assert.Equal(
                    button.Action,
                    ResultPanelTexture.HitTestWristLauncherCalibration(x, y));
            }

            (float X, float Y)[] outside =
            [
                (centerX, top - 1),
                (right + 1, centerY),
                (centerX, bottom + 1),
                (left - 1, centerY),
                (left - 1, top - 1),
                (right + 1, top - 1),
                (left - 1, bottom + 1),
                (right + 1, bottom + 1),
            ];
            foreach ((float x, float y) in outside)
            {
                Assert.Equal(
                    WristLauncherCalibrationAction.None,
                    ResultPanelTexture.HitTestWristLauncherCalibration(x, y));
            }
        }
    }

    [Fact]
    public void RenderWristLauncherCalibrationRgba_CreatesOneLogicalUiTexture()
    {
        byte[] pixels = new ResultPanelTexture().RenderWristLauncherCalibrationRgba();

        Assert.Equal(
            ResultPanelTexture.PixelWidth * ResultPanelTexture.PixelHeight * 4,
            pixels.Length);
    }

    private static void AssertPlacement(
        WristLauncherPlacement expected,
        WristLauncherPlacement actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 10);
        Assert.Equal(expected.Y, actual.Y, precision: 10);
        Assert.Equal(expected.Z, actual.Z, precision: 10);
        Assert.Equal(expected.Rotation.X, actual.Rotation.X, precision: 10);
        Assert.Equal(expected.Rotation.Y, actual.Rotation.Y, precision: 10);
        Assert.Equal(expected.Rotation.Z, actual.Rotation.Z, precision: 10);
        Assert.Equal(expected.Rotation.W, actual.Rotation.W, precision: 10);
        Assert.Equal(expected.MenuWidthMeters, actual.MenuWidthMeters, precision: 10);
    }

    private static float[,] RotationElements(ResultPanelTransform transform) => new[,]
    {
        { transform.M0, transform.M1, transform.M2 },
        { transform.M4, transform.M5, transform.M6 },
        { transform.M8, transform.M9, transform.M10 },
    };

    private static float[,] CreateAxisRotation(int axis, double degrees)
    {
        float cosine = (float)Math.Cos(degrees * Math.PI / 180);
        float sine = (float)Math.Sin(degrees * Math.PI / 180);
        return axis switch
        {
            0 => new[,]
            {
                { 1, 0, 0 },
                { 0, cosine, -sine },
                { 0, sine, cosine },
            },
            1 => new[,]
            {
                { cosine, 0, sine },
                { 0, 1, 0 },
                { -sine, 0, cosine },
            },
            2 => new[,]
            {
                { cosine, -sine, 0 },
                { sine, cosine, 0 },
                { 0, 0, 1 },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    private static float[,] MultiplyRotation(float[,] left, float[,] right)
    {
        float[,] result = new float[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                result[row, column] =
                    (left[row, 0] * right[0, column])
                    + (left[row, 1] * right[1, column])
                    + (left[row, 2] * right[2, column]);
            }
        }

        return result;
    }

    private static void AssertRotationEqual(float[,] expected, float[,] actual)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                Assert.Equal(
                    expected[row, column],
                    actual[row, column],
                    precision: 6);
            }
        }
    }
}

public sealed class WristFacingDetectorTests
{
    [Fact]
    public void Evaluate_UsesDifferentEnterAndExitCones()
    {
        OpenVrInputInterop.OpenVrPose left = IdentityAt(0, 0, 0);
        ResultPanelTransform launcher = new(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, -0.1f);
        OpenVrInputInterop.OpenVrPose hmd = IdentityAt(1.7f, 0, -1);

        WristFacingSample sample = WristFacingDetector.Evaluate(left, hmd, launcher);

        Assert.False(sample.EntersFacingCone);
        Assert.True(sample.RemainsInFacingCone);
    }

    [Fact]
    public void Evaluate_ArmsWhenLauncherFrontFacesHeadset()
    {
        WristFacingSample sample = WristFacingDetector.Evaluate(
            IdentityAt(0, 0, 0),
            IdentityAt(0, 0, -1),
            new ResultPanelTransform(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -0.1f));

        Assert.True(sample.EntersFacingCone);
        Assert.True(sample.RemainsInFacingCone);
        Assert.Equal(1, sample.Alignment, precision: 4);
    }

    [Fact]
    public void Evaluate_ArmsWhenVisibleBackFacePointsTowardHeadset()
    {
        WristFacingSample sample = WristFacingDetector.Evaluate(
            IdentityAt(0, 0, 0),
            IdentityAt(0, 0, 1),
            new ResultPanelTransform(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -0.1f));

        Assert.True(sample.EntersFacingCone);
        Assert.True(sample.RemainsInFacingCone);
        Assert.Equal(1, sample.Alignment, precision: 4);
    }

    [Fact]
    public void Evaluate_DoesNotArmWhenPanelPlaneIsPerpendicularToHeadset()
    {
        WristFacingSample sample = WristFacingDetector.Evaluate(
            IdentityAt(0, 0, 0),
            IdentityAt(1, 0, -0.1f),
            new ResultPanelTransform(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -0.1f));

        Assert.False(sample.EntersFacingCone);
        Assert.False(sample.RemainsInFacingCone);
        Assert.Equal(0, sample.Alignment, precision: 4);
    }

    [Fact]
    public void Evaluate_InvalidZeroDistanceFailsClosed()
    {
        WristFacingSample sample = WristFacingDetector.Evaluate(
            IdentityAt(0, 0, 0),
            IdentityAt(0, 0, -0.1f),
            new ResultPanelTransform(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -0.1f));

        Assert.False(sample.EntersFacingCone);
        Assert.False(sample.RemainsInFacingCone);
        Assert.Equal(-1, sample.Alignment, precision: 4);
    }

    [Fact]
    public void Evaluate_InvalidZeroNormalFailsClosed()
    {
        WristFacingSample sample = WristFacingDetector.Evaluate(
            IdentityAt(0, 0, 0),
            IdentityAt(0, 0, -1),
            new ResultPanelTransform(
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 0, 0));

        Assert.False(sample.EntersFacingCone);
        Assert.False(sample.RemainsInFacingCone);
        Assert.Equal(-1, sample.Alignment, precision: 4);
    }

    private static OpenVrInputInterop.OpenVrPose IdentityAt(float x, float y, float z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z);
}

public sealed class PointerCursorTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void LauncherConnection_RequiresInputLauncherAndCursor(
        bool inputConnected,
        bool launcherConnected,
        bool cursorConnected,
        bool expected) =>
        Assert.Equal(
            expected,
            SteamVrResultPanel.IsLauncherConnectionComplete(
                inputConnected,
                launcherConnected,
                cursorConnected));

    [Fact]
    public void CreateCursor_CentersOnIntersectionAndBuildsFiniteOrthonormalTransform()
    {
        OpenVrIntersection intersection = new(
            new OverlayLocalPoint(640, 360),
            new OpenVrVector3(1, 2, 3),
            new OpenVrVector3(0, 0, 2),
            new OpenVrVector3(0, 0, -1),
            1.25f);

        OpenVrAbsoluteTransform transform = OpenVrAbsoluteTransform.CreateCursor(intersection);

        Assert.Equal(1, transform.M3, 4);
        Assert.Equal(2, transform.M7, 4);
        Assert.Equal(3, transform.M11, 4);
        Assert.Equal(1, transform.M10, 4);
        Assert.All(
            new[]
            {
                transform.M0, transform.M1, transform.M2, transform.M3,
                transform.M4, transform.M5, transform.M6, transform.M7,
                transform.M8, transform.M9, transform.M10, transform.M11,
            },
            value => Assert.True(float.IsFinite(value)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public void CreateCursor_UsesTargetPlaneWithoutParallaxAtObliqueAngles(float normalZ)
    {
        OpenVrIntersection intersection = new(
            new OverlayLocalPoint(1100, 100),
            new OpenVrVector3(0.25f, -0.5f, 0.75f),
            new OpenVrVector3(0, 0, normalZ),
            new OpenVrVector3(0.8660254f, 0, -0.5f),
            1.25f);

        OpenVrAbsoluteTransform transform = OpenVrAbsoluteTransform.CreateCursor(intersection);

        Assert.Equal(0, transform.M2, 6);
        Assert.Equal(0, transform.M6, 6);
        Assert.Equal(1, transform.M10, 6);
        Assert.Equal(intersection.TrackingPoint.X, transform.M3, 6);
        Assert.Equal(intersection.TrackingPoint.Y, transform.M7, 6);
        Assert.Equal(intersection.TrackingPoint.Z, transform.M11, 6);

        float sourceFacing =
            (transform.M2 * -intersection.PointerDirection.X)
            + (transform.M6 * -intersection.PointerDirection.Y)
            + (transform.M10 * -intersection.PointerDirection.Z);
        Assert.True(sourceFacing > 0);
        float determinant =
            (transform.M0 * ((transform.M5 * transform.M10) - (transform.M6 * transform.M9)))
            - (transform.M1 * ((transform.M4 * transform.M10) - (transform.M6 * transform.M8)))
            + (transform.M2 * ((transform.M4 * transform.M9) - (transform.M5 * transform.M8)));
        Assert.Equal(1, determinant, 5);

        const float halfCursorWidth = 0.0125f;
        foreach ((float x, float y) in new[]
        {
            (-halfCursorWidth, -halfCursorWidth),
            (-halfCursorWidth, halfCursorWidth),
            (halfCursorWidth, -halfCursorWidth),
            (halfCursorWidth, halfCursorWidth),
        })
        {
            float cornerZ = transform.M11 + (transform.M8 * x) + (transform.M9 * y);
            Assert.Equal(intersection.TrackingPoint.Z, cornerZ, 6);
        }
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(float.NaN, 0, 1)]
    public void CreateCursor_RejectsInvalidSurfaceNormal(float x, float y, float z)
    {
        OpenVrIntersection intersection = new(
            new OverlayLocalPoint(640, 360),
            new OpenVrVector3(0, 0, 0),
            new OpenVrVector3(x, y, z),
            new OpenVrVector3(0, 0, -1),
            1);

        Assert.Throws<ArgumentException>(() => OpenVrAbsoluteTransform.CreateCursor(intersection));
    }

    [Fact]
    public void RenderRgba_CreatesTransparentCursorTexture()
    {
        byte[] pixels = PointerCursorTexture.RenderRgba();

        Assert.Equal(PointerCursorTexture.PixelSize * PointerCursorTexture.PixelSize * 4, pixels.Length);
        Assert.Equal(0, pixels[3]);
        int center = (((PointerCursorTexture.PixelSize / 2) * PointerCursorTexture.PixelSize)
            + (PointerCursorTexture.PixelSize / 2)) * 4;
        Assert.True(pixels[center + 3] > 0);
    }
}
