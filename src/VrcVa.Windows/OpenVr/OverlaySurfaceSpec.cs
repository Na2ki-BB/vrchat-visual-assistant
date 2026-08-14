namespace VrcVa.Windows.OpenVr;

/// <summary>
/// Defines the logical UI coordinate system shared by OpenVR's mouse scale and
/// ray-intersection results. Local coordinates use a top-left origin.
/// </summary>
internal readonly record struct OverlaySurfaceSpec
{
    public OverlaySurfaceSpec(float logicalWidth, float logicalHeight)
    {
        if (!float.IsFinite(logicalWidth) || logicalWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalWidth));
        }

        if (!float.IsFinite(logicalHeight) || logicalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalHeight));
        }

        LogicalWidth = logicalWidth;
        LogicalHeight = logicalHeight;
    }

    public float LogicalWidth { get; }

    public float LogicalHeight { get; }

    public void Validate()
    {
        if (!float.IsFinite(LogicalWidth) || LogicalWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LogicalWidth));
        }

        if (!float.IsFinite(LogicalHeight) || LogicalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LogicalHeight));
        }
    }

    /// <summary>
    /// Converts OpenVR's normalized overlay texture coordinates to the
    /// top-left-origin logical coordinates used by VRCVA's UI and hit tests.
    /// </summary>
    public OverlayLocalPoint ToTopLeftLocal(float normalizedU, float normalizedV) =>
        new(
            normalizedU * LogicalWidth,
            (1 - normalizedV) * LogicalHeight);

    public OverlayLocalPoint ToAtlasCellLocal(
        float normalizedU,
        float normalizedV,
        int cell,
        int columns,
        int rows)
    {
        if (columns <= 0 || rows <= 0 || cell < 0 || cell >= columns * rows)
        {
            throw new ArgumentOutOfRangeException(nameof(cell));
        }

        int column = cell % columns;
        int row = cell / columns;
        return new OverlayLocalPoint(
            (normalizedU * columns * LogicalWidth) - (column * LogicalWidth),
            ((1 - normalizedV) * rows * LogicalHeight) - (row * LogicalHeight));
    }
}

internal readonly record struct OverlayLocalPoint(float X, float Y);

internal readonly record struct OpenVrVector3(float X, float Y, float Z);

internal readonly record struct OpenVrRay(OpenVrVector3 Source, OpenVrVector3 Direction)
{
    /// <summary>
    /// Creates a standing-space pointer ray from an OpenVR device pose. OpenVR
    /// uses -Z as the controller's forward direction.
    /// </summary>
    public static OpenVrRay FromPose(OpenVrInputInterop.OpenVrPose pose)
    {
        OpenVrVector3 source = new(pose.M3, pose.M7, pose.M11);
        float directionX = -pose.M2;
        float directionY = -pose.M6;
        float directionZ = -pose.M10;
        float lengthSquared =
            (directionX * directionX)
            + (directionY * directionY)
            + (directionZ * directionZ);
        if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
        {
            throw new ArgumentException("The OpenVR pose has no finite forward direction.", nameof(pose));
        }

        float inverseLength = 1f / MathF.Sqrt(lengthSquared);
        OpenVrVector3 direction = new(
            directionX * inverseLength,
            directionY * inverseLength,
            directionZ * inverseLength);
        return new OpenVrRay(source, direction);
    }
}

internal readonly record struct OpenVrIntersection(
    OverlayLocalPoint LocalPoint,
    OpenVrVector3 TrackingPoint,
    OpenVrVector3 TrackingNormal,
    OpenVrVector3 PointerDirection,
    float DistanceMeters,
    OverlayLocalPoint RawPoint = default);

internal readonly record struct OpenVrAbsoluteTransform(
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
    float M11)
{
    public static OpenVrAbsoluteTransform CreateCursor(OpenVrIntersection intersection)
    {
        OpenVrVector3 surfaceNormal = Normalize(intersection.TrackingNormal);
        OpenVrVector3 pointerDirection = Normalize(intersection.PointerDirection);
        if (Dot(surfaceNormal, pointerDirection) > 0)
        {
            surfaceNormal = Negate(surfaceNormal);
        }

        // OpenVR renders the texture-facing side along local +Z. Keep that
        // side pointed back toward the controller while the cursor remains
        // parallel to the target. A ray-facing billboard intersects the
        // target plane at oblique angles and becomes occluded near the edges.
        OpenVrVector3 zAxis = surfaceNormal;
        OpenVrVector3 reference = MathF.Abs(zAxis.Y) < 0.95f
            ? new OpenVrVector3(0, 1, 0)
            : new OpenVrVector3(1, 0, 0);
        OpenVrVector3 xAxis = Normalize(Cross(reference, zAxis));
        OpenVrVector3 yAxis = Normalize(Cross(zAxis, xAxis));
        const float surfaceOffsetMeters = 0.003f;
        OpenVrVector3 position = new(
            intersection.TrackingPoint.X + (surfaceNormal.X * surfaceOffsetMeters),
            intersection.TrackingPoint.Y + (surfaceNormal.Y * surfaceOffsetMeters),
            intersection.TrackingPoint.Z + (surfaceNormal.Z * surfaceOffsetMeters));
        return new OpenVrAbsoluteTransform(
            xAxis.X, yAxis.X, zAxis.X, position.X,
            xAxis.Y, yAxis.Y, zAxis.Y, position.Y,
            xAxis.Z, yAxis.Z, zAxis.Z, position.Z);
    }

    private static float Dot(OpenVrVector3 left, OpenVrVector3 right) =>
        (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

    private static OpenVrVector3 Negate(OpenVrVector3 value) =>
        new(-value.X, -value.Y, -value.Z);

    private static OpenVrVector3 Cross(OpenVrVector3 left, OpenVrVector3 right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    private static OpenVrVector3 Normalize(OpenVrVector3 value)
    {
        float length = MathF.Sqrt(
            (value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));
        if (!float.IsFinite(length) || length <= float.Epsilon)
        {
            throw new ArgumentException("The OpenVR direction must be finite and non-zero.", nameof(value));
        }

        return new OpenVrVector3(value.X / length, value.Y / length, value.Z / length);
    }
}
