namespace VrcVa.Windows.OpenVr;

/// <summary>
/// Defines the logical UI coordinate system shared by OpenVR's mouse scale and
/// VRCVA's rendering and hit tests. Local coordinates use a top-left origin.
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
}

/// <summary>
/// Texture bounds in the upper-left-origin coordinate system required by
/// OpenVR's SetOverlayTextureBounds API.
/// </summary>
internal readonly record struct OverlayTextureBounds
{
    public OverlayTextureBounds(float uMin, float vMin, float uMax, float vMax)
    {
        if (!IsFiniteUnitRange(uMin)
            || !IsFiniteUnitRange(vMin)
            || !IsFiniteUnitRange(uMax)
            || !IsFiniteUnitRange(vMax)
            || uMin >= uMax
            || vMin >= vMax)
        {
            throw new ArgumentOutOfRangeException(nameof(uMin));
        }

        UMin = uMin;
        VMin = vMin;
        UMax = uMax;
        VMax = vMax;
    }

    public float UMin { get; }

    public float VMin { get; }

    public float UMax { get; }

    public float VMax { get; }

    public bool IsValid =>
        IsFiniteUnitRange(UMin)
        && IsFiniteUnitRange(VMin)
        && IsFiniteUnitRange(UMax)
        && IsFiniteUnitRange(VMax)
        && UMin < UMax
        && VMin < VMax;

    private static bool IsFiniteUnitRange(float value) =>
        float.IsFinite(value) && value >= 0 && value <= 1;
}

/// <summary>
/// The complete coordinate contract for one visible OpenVR texture view.
/// OpenVR reports atlas-global normalized intersection coordinates with a
/// lower-left origin, while texture bounds use an upper-left origin. Keeping
/// the exact submitted bounds here makes their inverse mapping authoritative.
/// </summary>
internal readonly record struct OverlayTextureView
{
    private const float NormalizedBoundaryTolerance = 0.000001f;

    private OverlayTextureView(
        OverlaySurfaceSpec surface,
        int cell,
        int columns,
        int rows,
        OverlayTextureBounds upperLeftTextureBounds)
    {
        Surface = surface;
        Cell = cell;
        Columns = columns;
        Rows = rows;
        UpperLeftTextureBounds = upperLeftTextureBounds;
    }

    public OverlaySurfaceSpec Surface { get; }

    public int Cell { get; }

    public int Columns { get; }

    public int Rows { get; }

    public OverlayTextureBounds UpperLeftTextureBounds { get; }

    public static OverlayTextureView CreateFull(OverlaySurfaceSpec surface)
    {
        surface.Validate();
        return new OverlayTextureView(
            surface,
            cell: 0,
            columns: 1,
            rows: 1,
            new OverlayTextureBounds(0, 0, 1, 1));
    }

    public static OverlayTextureView CreateAtlasCell(
        OverlaySurfaceSpec surface,
        int cell,
        int columns,
        int rows)
    {
        surface.Validate();
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        long cellCount = (long)columns * rows;
        if (cell < 0 || cell >= cellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(cell));
        }

        float cellWidth = 1f / columns;
        float cellHeight = 1f / rows;
        int column = cell % columns;
        int row = cell / columns;

        // The crop starts and ends at texel centres to prevent neighbouring
        // atlas cells from bleeding into the selected view. These are the
        // exact values submitted to SetOverlayTextureBounds and inverted by
        // TryMapAtlasGlobalLowerOriginToTopLeftLocal below.
        float halfTexelU = 0.5f / surface.LogicalWidth / columns;
        float halfTexelV = 0.5f / surface.LogicalHeight / rows;
        OverlayTextureBounds bounds = new(
            (column * cellWidth) + halfTexelU,
            (row * cellHeight) + halfTexelV,
            ((column + 1) * cellWidth) - halfTexelU,
            ((row + 1) * cellHeight) - halfTexelV);
        return new OverlayTextureView(surface, cell, columns, rows, bounds);
    }

    public bool TryMapAtlasGlobalLowerOriginToTopLeftLocal(
        float atlasU,
        float atlasV,
        out OverlayLocalPoint localPoint)
    {
        localPoint = default;
        if (!float.IsFinite(atlasU)
            || !float.IsFinite(atlasV)
            || !UpperLeftTextureBounds.IsValid
            || Columns <= 0
            || Rows <= 0
            || Cell < 0
            || Cell >= (long)Columns * Rows)
        {
            return false;
        }

        int column = Cell % Columns;
        int row = Cell / Columns;
        float idealUMin = (float)column / Columns;
        float idealUMax = (float)(column + 1) / Columns;
        float idealLowerLeftVMin = 1f - ((float)(row + 1) / Rows);
        float idealLowerLeftVMax = 1f - ((float)row / Rows);
        if (atlasU < idealUMin - NormalizedBoundaryTolerance
            || atlasU > idealUMax + NormalizedBoundaryTolerance
            || atlasV < idealLowerLeftVMin - NormalizedBoundaryTolerance
            || atlasV > idealLowerLeftVMax + NormalizedBoundaryTolerance)
        {
            return false;
        }

        // Some OpenVR runtimes report the mathematical cell edge while the
        // submitted crop is inset to texel centres. Clamp only that half-texel
        // fringe (plus a tiny float tolerance) to the nearest logical edge.
        // Coordinates beyond the selected cell still fail closed above.
        float lowerLeftVMin = 1f - UpperLeftTextureBounds.VMax;
        float lowerLeftVMax = 1f - UpperLeftTextureBounds.VMin;
        float boundedU = Math.Clamp(
            atlasU,
            UpperLeftTextureBounds.UMin,
            UpperLeftTextureBounds.UMax);
        float boundedV = Math.Clamp(atlasV, lowerLeftVMin, lowerLeftVMax);
        float normalizedLocalX =
            (boundedU - UpperLeftTextureBounds.UMin)
            / (UpperLeftTextureBounds.UMax - UpperLeftTextureBounds.UMin);
        float normalizedLocalY =
            (lowerLeftVMax - boundedV)
            / (lowerLeftVMax - lowerLeftVMin);
        float localX = SnapNearLogicalPixel(normalizedLocalX * Surface.LogicalWidth);
        float localY = SnapNearLogicalPixel(normalizedLocalY * Surface.LogicalHeight);
        if (!float.IsFinite(localX)
            || !float.IsFinite(localY)
            || localX < 0
            || localX > Surface.LogicalWidth
            || localY < 0
            || localY > Surface.LogicalHeight)
        {
            return false;
        }

        localPoint = new OverlayLocalPoint(localX, localY);
        return true;
    }

    private static float SnapNearLogicalPixel(float value)
    {
        // A float atlas coordinate can round an exact integer UI boundary a
        // fraction past its rendered Rect. Normalize only sub-millipixel
        // noise so render and hit-test edges retain the same contract.
        float nearestPixel = MathF.Round(value);
        return MathF.Abs(value - nearestPixel) <= 0.001f
            ? nearestPixel
            : value;
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
        // The visual cursor center and the hit-test point must be the same
        // tracking-space point. Moving the cursor toward the controller creates
        // view-dependent parallax when the HMD observes the panel obliquely.
        // Overlay sort order, not a physical offset, keeps the cursor on top.
        OpenVrVector3 position = intersection.TrackingPoint;
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
