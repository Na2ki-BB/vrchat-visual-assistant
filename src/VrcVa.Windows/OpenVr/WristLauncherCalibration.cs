namespace VrcVa.Windows.OpenVr;

internal enum WristLauncherCalibrationAction
{
    None,
    MoveTowardHandBack,
    MoveTowardPalm,
    MoveDown,
    MoveUp,
    MoveTowardFingertips,
    MoveTowardElbow,
    DecreasePitch,
    IncreasePitch,
    DecreaseYaw,
    IncreaseYaw,
    DecreaseRoll,
    IncreaseRoll,
    MakeSmaller,
    MakeLarger,
    Reset,
    Cancel,
    Save,
}

internal static class WristLauncherCalibration
{
    internal const double PositionStepMeters = 0.01;
    internal const double RotationStepDegrees = 5;
    internal const double ScaleStep = 0.05;

    public static WristLauncherPlacement Apply(
        WristLauncherPlacement placement,
        WristLauncherCalibrationAction action)
    {
        placement.Validate();
        WristLauncherPlacement updated = action switch
        {
            WristLauncherCalibrationAction.MoveTowardHandBack => placement with
            {
                X = ClampPosition(placement.X - PositionStepMeters),
            },
            WristLauncherCalibrationAction.MoveTowardPalm => placement with
            {
                X = ClampPosition(placement.X + PositionStepMeters),
            },
            WristLauncherCalibrationAction.MoveDown => placement with
            {
                Y = ClampPosition(placement.Y - PositionStepMeters),
            },
            WristLauncherCalibrationAction.MoveUp => placement with
            {
                Y = ClampPosition(placement.Y + PositionStepMeters),
            },
            WristLauncherCalibrationAction.MoveTowardFingertips => placement with
            {
                Z = ClampPosition(placement.Z - PositionStepMeters),
            },
            WristLauncherCalibrationAction.MoveTowardElbow => placement with
            {
                Z = ClampPosition(placement.Z + PositionStepMeters),
            },
            WristLauncherCalibrationAction.DecreasePitch => placement with
            {
                Rotation = placement.Rotation.RotateLocalX(-RotationStepDegrees),
            },
            WristLauncherCalibrationAction.IncreasePitch => placement with
            {
                Rotation = placement.Rotation.RotateLocalX(RotationStepDegrees),
            },
            WristLauncherCalibrationAction.DecreaseYaw => placement with
            {
                Rotation = placement.Rotation.RotateLocalY(-RotationStepDegrees),
            },
            WristLauncherCalibrationAction.IncreaseYaw => placement with
            {
                Rotation = placement.Rotation.RotateLocalY(RotationStepDegrees),
            },
            WristLauncherCalibrationAction.DecreaseRoll => placement with
            {
                Rotation = placement.Rotation.RotateLocalZ(-RotationStepDegrees),
            },
            WristLauncherCalibrationAction.IncreaseRoll => placement with
            {
                Rotation = placement.Rotation.RotateLocalZ(RotationStepDegrees),
            },
            WristLauncherCalibrationAction.MakeSmaller => placement with
            {
                MenuWidthMeters = Math.Max(
                    WristLauncherPlacement.MinimumMenuWidthMeters,
                    placement.MenuWidthMeters * (1 - ScaleStep)),
            },
            WristLauncherCalibrationAction.MakeLarger => placement with
            {
                MenuWidthMeters = Math.Min(
                    WristLauncherPlacement.MaximumMenuWidthMeters,
                    placement.MenuWidthMeters * (1 + ScaleStep)),
            },
            WristLauncherCalibrationAction.Reset => WristLauncherPlacement.Default,
            _ => placement,
        };
        updated.Validate();
        return updated;
    }

    private static double ClampPosition(double value) => Math.Clamp(
        value,
        WristLauncherPlacement.MinimumPosition,
        WristLauncherPlacement.MaximumPosition);
}
