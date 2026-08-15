using System.IO;
using System.Text.Json.Serialization;

namespace VrcVa.Windows.OpenVr;

/// <summary>
/// A normalized, canonical quaternion describing the launcher's local orientation.
/// </summary>
internal readonly record struct WristLauncherRotation
{
    private const double EulerSingularityThreshold = 1e-12;

    [JsonConstructor]
    public WristLauncherRotation(double x, double y, double z, double w)
    {
        if (!double.IsFinite(x)
            || !double.IsFinite(y)
            || !double.IsFinite(z)
            || !double.IsFinite(w))
        {
            throw new InvalidDataException(
                "Wrist launcher rotation components must be finite.");
        }

        // Scaling first avoids overflow and underflow while finding the norm.
        double scale = Math.Max(
            Math.Max(Math.Abs(x), Math.Abs(y)),
            Math.Max(Math.Abs(z), Math.Abs(w)));
        if (scale == 0)
        {
            throw new InvalidDataException(
                "Wrist launcher rotation must be a non-zero quaternion.");
        }

        double scaledX = x / scale;
        double scaledY = y / scale;
        double scaledZ = z / scale;
        double scaledW = w / scale;
        double scaledNorm = Math.Sqrt(
            (scaledX * scaledX)
            + (scaledY * scaledY)
            + (scaledZ * scaledZ)
            + (scaledW * scaledW));
        double normalizedX = scaledX / scaledNorm;
        double normalizedY = scaledY / scaledNorm;
        double normalizedZ = scaledZ / scaledNorm;
        double normalizedW = scaledW / scaledNorm;

        // q and -q are the same rotation. Keep one deterministic representation
        // so equality and persisted settings do not change sign unpredictably.
        if (ShouldNegate(
            normalizedX,
            normalizedY,
            normalizedZ,
            normalizedW))
        {
            normalizedX = -normalizedX;
            normalizedY = -normalizedY;
            normalizedZ = -normalizedZ;
            normalizedW = -normalizedW;
        }

        X = CleanZero(normalizedX);
        Y = CleanZero(normalizedY);
        Z = CleanZero(normalizedZ);
        W = CleanZero(normalizedW);
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }

    public double W { get; }

    public static WristLauncherRotation FromEulerDegrees(
        double pitchDegrees,
        double yawDegrees,
        double rollDegrees)
    {
        ValidateFiniteAngle(pitchDegrees, nameof(pitchDegrees));
        ValidateFiniteAngle(yawDegrees, nameof(yawDegrees));
        ValidateFiniteAngle(rollDegrees, nameof(rollDegrees));

        double halfPitch = DegreesToRadians(pitchDegrees) / 2;
        double halfYaw = DegreesToRadians(yawDegrees) / 2;
        double halfRoll = DegreesToRadians(rollDegrees) / 2;
        double sx = Math.Sin(halfPitch);
        double cx = Math.Cos(halfPitch);
        double sy = Math.Sin(halfYaw);
        double cy = Math.Cos(halfYaw);
        double sz = Math.Sin(halfRoll);
        double cz = Math.Cos(halfRoll);

        // Intrinsic X, then Y, then Z: q = qz * qy * qx.
        return new WristLauncherRotation(
            x: (cz * cy * sx) - (sz * sy * cx),
            y: (cz * sy * cx) + (sz * cy * sx),
            z: (sz * cy * cx) - (cz * sy * sx),
            w: (cz * cy * cx) + (sz * sy * sx));
    }

    public void Validate()
    {
        if (!double.IsFinite(X)
            || !double.IsFinite(Y)
            || !double.IsFinite(Z)
            || !double.IsFinite(W))
        {
            throw new InvalidDataException(
                "Wrist launcher rotation components must be finite.");
        }

        double normSquared = (X * X) + (Y * Y) + (Z * Z) + (W * W);
        if (!double.IsFinite(normSquared)
            || Math.Abs(normSquared - 1) > 1e-12
            || ShouldNegate(X, Y, Z, W))
        {
            throw new InvalidDataException(
                "Wrist launcher rotation must be normalized and canonical.");
        }
    }

    public WristLauncherRotation RotateLocalX(double degrees) =>
        Multiply(this, FromAxisAngle(degrees, x: 1, y: 0, z: 0));

    public WristLauncherRotation RotateLocalY(double degrees) =>
        Multiply(this, FromAxisAngle(degrees, x: 0, y: 1, z: 0));

    public WristLauncherRotation RotateLocalZ(double degrees) =>
        Multiply(this, FromAxisAngle(degrees, x: 0, y: 0, z: 1));

    public (double PitchDegrees, double YawDegrees, double RollDegrees)
        ToRzRyRxEulerDegrees()
    {
        Validate();

        double xx = X * X;
        double yy = Y * Y;
        double zz = Z * Z;
        double xy = X * Y;
        double xz = X * Z;
        double yz = Y * Z;
        double xw = X * W;
        double yw = Y * W;
        double zw = Z * W;

        double m00 = 1 - (2 * (yy + zz));
        double m10 = 2 * (xy + zw);
        double m20 = 2 * (xz - yw);
        double m11 = 1 - (2 * (xx + zz));
        double m12 = 2 * (yz - xw);
        double m21 = 2 * (yz + xw);
        double m22 = 1 - (2 * (xx + yy));

        // R = Rz(roll) * Ry(yaw) * Rx(pitch). Use atan2 with the
        // non-negative horizontal length so yaw stays in [-90, 90].
        double horizontalLength = Math.Sqrt((m00 * m00) + (m10 * m10));
        double yaw = Math.Atan2(-m20, horizontalLength);
        double pitch;
        double roll;
        if (horizontalLength > EulerSingularityThreshold)
        {
            pitch = Math.Atan2(m21, m22);
            roll = Math.Atan2(m10, m00);
        }
        else
        {
            // At yaw +/-90 degrees, pitch and roll are not independently
            // observable. Choosing roll=0 preserves the complete matrix.
            pitch = Math.Atan2(-m12, m11);
            roll = 0;
        }

        return (
            RadiansToDegrees(pitch),
            RadiansToDegrees(yaw),
            RadiansToDegrees(roll));
    }

    private static WristLauncherRotation FromAxisAngle(
        double degrees,
        double x,
        double y,
        double z)
    {
        ValidateFiniteAngle(degrees, nameof(degrees));
        double halfAngle = DegreesToRadians(degrees) / 2;
        double sine = Math.Sin(halfAngle);
        return new WristLauncherRotation(
            x * sine,
            y * sine,
            z * sine,
            Math.Cos(halfAngle));
    }

    private static WristLauncherRotation Multiply(
        WristLauncherRotation left,
        WristLauncherRotation right) => new(
            x: (left.W * right.X)
                + (left.X * right.W)
                + (left.Y * right.Z)
                - (left.Z * right.Y),
            y: (left.W * right.Y)
                - (left.X * right.Z)
                + (left.Y * right.W)
                + (left.Z * right.X),
            z: (left.W * right.Z)
                + (left.X * right.Y)
                - (left.Y * right.X)
                + (left.Z * right.W),
            w: (left.W * right.W)
                - (left.X * right.X)
                - (left.Y * right.Y)
                - (left.Z * right.Z));

    private static bool ShouldNegate(double x, double y, double z, double w) =>
        w < 0
        || (w == 0
            && (x < 0
                || (x == 0
                    && (y < 0 || (y == 0 && z < 0)))));

    private static double CleanZero(double value) => value == 0 ? 0 : value;

    private static void ValidateFiniteAngle(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException(
                $"Wrist launcher {name} must be finite.");
        }
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static double RadiansToDegrees(double value) => value * 180 / Math.PI;
}

/// <summary>
/// Non-secret placement preferences for the launcher attached to the left-hand pose.
/// </summary>
internal sealed record WristLauncherPlacement(
    double X,
    double Y,
    double Z,
    WristLauncherRotation Rotation,
    double MenuWidthMeters)
{
    public const double MinimumPosition = -1.5;
    public const double MaximumPosition = 1.5;
    public const double MinimumMenuWidthMeters = ResultPanelPlacement.MinimumWidthMeters;
    public const double MaximumMenuWidthMeters = 1.0;
    public const double DefaultChipToMenuWidthRatio = 0.14 / 0.38;

    public static WristLauncherPlacement Default => new(
        X: -0.07,
        Y: 0,
        Z: 0.22,
        Rotation: WristLauncherRotation.FromEulerDegrees(
            pitchDegrees: 180,
            yawDegrees: 75,
            rollDegrees: 0),
        MenuWidthMeters: 0.38);

    [JsonIgnore]
    public double ChipWidthMeters => MenuWidthMeters * DefaultChipToMenuWidthRatio;

    public void Validate()
    {
        ValidateFiniteRange(X, MinimumPosition, MaximumPosition, nameof(X));
        ValidateFiniteRange(Y, MinimumPosition, MaximumPosition, nameof(Y));
        ValidateFiniteRange(Z, MinimumPosition, MaximumPosition, nameof(Z));
        Rotation.Validate();
        ValidateFiniteRange(
            MenuWidthMeters,
            MinimumMenuWidthMeters,
            MaximumMenuWidthMeters,
            nameof(MenuWidthMeters));
    }

    public ResultPanelTransform CreateTransform()
    {
        Validate();

        double xx = Rotation.X * Rotation.X;
        double yy = Rotation.Y * Rotation.Y;
        double zz = Rotation.Z * Rotation.Z;
        double xy = Rotation.X * Rotation.Y;
        double xz = Rotation.X * Rotation.Z;
        double yz = Rotation.Y * Rotation.Z;
        double xw = Rotation.X * Rotation.W;
        double yw = Rotation.Y * Rotation.W;
        double zw = Rotation.Z * Rotation.W;

        return new ResultPanelTransform(
            M0: ToFloat(1 - (2 * (yy + zz))),
            M1: ToFloat(2 * (xy - zw)),
            M2: ToFloat(2 * (xz + yw)),
            M3: ToFloat(X),
            M4: ToFloat(2 * (xy + zw)),
            M5: ToFloat(1 - (2 * (xx + zz))),
            M6: ToFloat(2 * (yz - xw)),
            M7: ToFloat(Y),
            M8: ToFloat(2 * (xz - yw)),
            M9: ToFloat(2 * (yz + xw)),
            M10: ToFloat(1 - (2 * (xx + yy))),
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
                $"Wrist launcher {name} must be from {minimum} to {maximum}.");
        }
    }

    private static float ToFloat(double value) => checked((float)value);
}
