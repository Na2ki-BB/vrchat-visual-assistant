using System.IO;

namespace VrcVa.Windows.OpenVr;

internal enum ResultPanelAnchor
{
    LeftHand,
    RightHand,
    Headset,
}

internal sealed record ResultPanelPlacement(
    ResultPanelAnchor Anchor,
    double X,
    double Y,
    double Z,
    double PitchDegrees,
    double YawDegrees,
    double RollDegrees,
    double WidthMeters)
{
    public const double MinimumPosition = -1.5;
    public const double MaximumPosition = 1.5;
    public const double MinimumRotationDegrees = -180;
    public const double MaximumRotationDegrees = 180;
    public const double MinimumWidthMeters = 0.25;
    public const double MaximumWidthMeters = 1.5;

    public static ResultPanelPlacement Default => CreateDefault(ResultPanelAnchor.LeftHand);

    public static ResultPanelPlacement HeadsetFallback =>
        CreateDefault(ResultPanelAnchor.Headset);

    public static ResultPanelPlacement CreateDefault(ResultPanelAnchor anchor) => anchor switch
    {
        ResultPanelAnchor.LeftHand => new(
            anchor,
            X: 0.08,
            Y: 0.12,
            Z: -0.28,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            WidthMeters: 0.62),
        ResultPanelAnchor.RightHand => new(
            anchor,
            X: -0.08,
            Y: 0.12,
            Z: -0.28,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            WidthMeters: 0.62),
        ResultPanelAnchor.Headset => new(
            anchor,
            X: 0,
            Y: -0.08,
            Z: -1.2,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            WidthMeters: 1.15),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
    };

    public void Validate()
    {
        if (!Enum.IsDefined(Anchor))
        {
            throw new InvalidDataException("The result panel anchor is invalid.");
        }

        ValidateFiniteRange(X, MinimumPosition, MaximumPosition, nameof(X));
        ValidateFiniteRange(Y, MinimumPosition, MaximumPosition, nameof(Y));
        ValidateFiniteRange(Z, MinimumPosition, MaximumPosition, nameof(Z));
        ValidateFiniteRange(
            PitchDegrees,
            MinimumRotationDegrees,
            MaximumRotationDegrees,
            nameof(PitchDegrees));
        ValidateFiniteRange(
            YawDegrees,
            MinimumRotationDegrees,
            MaximumRotationDegrees,
            nameof(YawDegrees));
        ValidateFiniteRange(
            RollDegrees,
            MinimumRotationDegrees,
            MaximumRotationDegrees,
            nameof(RollDegrees));
        ValidateFiniteRange(
            WidthMeters,
            MinimumWidthMeters,
            MaximumWidthMeters,
            nameof(WidthMeters));
    }

    public ResultPanelTransform CreateTransform()
    {
        Validate();

        double pitch = DegreesToRadians(PitchDegrees);
        double yaw = DegreesToRadians(YawDegrees);
        double roll = DegreesToRadians(RollDegrees);
        double cx = Math.Cos(pitch);
        double sx = Math.Sin(pitch);
        double cy = Math.Cos(yaw);
        double sy = Math.Sin(yaw);
        double cz = Math.Cos(roll);
        double sz = Math.Sin(roll);

        // Intrinsic X (pitch), then Y (yaw), then Z (roll): Rz * Ry * Rx.
        return new ResultPanelTransform(
            M0: ToFloat(cz * cy),
            M1: ToFloat((cz * sy * sx) - (sz * cx)),
            M2: ToFloat((cz * sy * cx) + (sz * sx)),
            M3: ToFloat(X),
            M4: ToFloat(sz * cy),
            M5: ToFloat((sz * sy * sx) + (cz * cx)),
            M6: ToFloat((sz * sy * cx) - (cz * sx)),
            M7: ToFloat(Y),
            M8: ToFloat(-sy),
            M9: ToFloat(cy * sx),
            M10: ToFloat(cy * cx),
            M11: ToFloat(Z));
    }

    private static void ValidateFiniteRange(
        double value,
        double minimum,
        double maximum,
        string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Result panel {name} must be from {minimum} to {maximum}.");
        }
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static float ToFloat(double value) => checked((float)value);
}

internal readonly record struct ResultPanelTransform(
    float M0,
    float M1,
    float M2,
    float M3,
    float M4,
    float M5,
    float M6,
    float M7,
    float M8,
    float M9,
    float M10,
    float M11);

internal enum ResultPanelCalibrationAction
{
    None,
    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    MoveNear,
    MoveFar,
    MakeSmaller,
    MakeLarger,
    Reset,
    Cancel,
    Save,
}

internal static class ResultPanelCalibration
{
    private const double PositionStepMeters = 0.03;
    private const double WidthStepMeters = 0.06;

    public static ResultPanelPlacement Apply(
        ResultPanelPlacement placement,
        ResultPanelCalibrationAction action)
    {
        placement.Validate();
        ResultPanelPlacement updated = action switch
        {
            ResultPanelCalibrationAction.MoveLeft => placement with
            {
                X = ClampPosition(placement.X - PositionStepMeters),
            },
            ResultPanelCalibrationAction.MoveRight => placement with
            {
                X = ClampPosition(placement.X + PositionStepMeters),
            },
            ResultPanelCalibrationAction.MoveUp => placement with
            {
                Y = ClampPosition(placement.Y + PositionStepMeters),
            },
            ResultPanelCalibrationAction.MoveDown => placement with
            {
                Y = ClampPosition(placement.Y - PositionStepMeters),
            },
            ResultPanelCalibrationAction.MoveNear => placement with
            {
                Z = ClampPosition(placement.Z + PositionStepMeters),
            },
            ResultPanelCalibrationAction.MoveFar => placement with
            {
                Z = ClampPosition(placement.Z - PositionStepMeters),
            },
            ResultPanelCalibrationAction.MakeSmaller => placement with
            {
                WidthMeters = Math.Max(
                    ResultPanelPlacement.MinimumWidthMeters,
                    placement.WidthMeters - WidthStepMeters),
            },
            ResultPanelCalibrationAction.MakeLarger => placement with
            {
                WidthMeters = Math.Min(
                    ResultPanelPlacement.MaximumWidthMeters,
                    placement.WidthMeters + WidthStepMeters),
            },
            ResultPanelCalibrationAction.Reset =>
                ResultPanelPlacement.CreateDefault(placement.Anchor),
            _ => placement,
        };
        updated.Validate();
        return updated;
    }

    private static double ClampPosition(double value) => Math.Clamp(
        value,
        ResultPanelPlacement.MinimumPosition,
        ResultPanelPlacement.MaximumPosition);
}
