using System.Runtime.InteropServices;
using VrcVa.Windows.OpenVr;
using Rect = System.Windows.Rect;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrOverlayIntersectionTests
{
    [Fact]
    public void ValidateAbi_AcceptsPublishedOpenVr027Layouts() =>
        OpenVrInterop.ValidateAbi();

    [Fact]
    public void InteractionConfiguration_UsesPublishedOpenVr027SlotsAndPrimitiveSize()
    {
        Assert.Equal(50, OpenVrInterop.SetOverlayMouseScaleFunctionSlot);
        Assert.Equal(53, OpenVrInterop.SetOverlayIntersectionMaskFunctionSlot);
        Assert.Equal(20u, OpenVrInterop.IntersectionMaskPrimitiveAbiSize);

        OpenVrIntersectionMaskRectangleSpec rectangle = new(0, 0, 1280.5f, 720.25f);
        OpenVrInterop.VrOverlayIntersectionMaskPrimitive primitive =
            OpenVrInterop.VrOverlayIntersectionMaskPrimitive.CreateRectangle(rectangle);
        Assert.Equal(20, Marshal.SizeOf<OpenVrInterop.VrOverlayIntersectionMaskPrimitive>());
        Assert.Equal(0, Marshal.OffsetOf<OpenVrInterop.VrOverlayIntersectionMaskPrimitive>(
            nameof(OpenVrInterop.VrOverlayIntersectionMaskPrimitive.PrimitiveType)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<OpenVrInterop.VrOverlayIntersectionMaskPrimitive>(
            nameof(OpenVrInterop.VrOverlayIntersectionMaskPrimitive.Primitive)).ToInt32());
        Assert.Equal(OpenVrInterop.VrOverlayIntersectionMaskPrimitiveType.Rectangle, primitive.PrimitiveType);
        Assert.Equal(rectangle.X, primitive.Primitive.Rectangle.TopLeftX);
        Assert.Equal(rectangle.Y, primitive.Primitive.Rectangle.TopLeftY);
        Assert.Equal(rectangle.Width, primitive.Primitive.Rectangle.Width);
        Assert.Equal(rectangle.Height, primitive.Primitive.Rectangle.Height);
    }

    [Fact]
    public void InteractionConfiguration_UsesOneFullLogicalSurfaceForMouseScaleAndMask()
    {
        OverlaySurfaceSpec surface = new(1280, 720);

        OpenVrOverlayInteractionConfiguration configuration =
            OpenVrInterop.CreateInteractionConfiguration(surface);

        Assert.Equal(surface.LogicalWidth, configuration.MouseScaleWidth);
        Assert.Equal(surface.LogicalHeight, configuration.MouseScaleHeight);
        Assert.Equal(
            new OpenVrIntersectionMaskRectangleSpec(0, 0, 1280, 720),
            configuration.IntersectionMask);
    }

    [Fact]
    public void InteractionConfiguration_AppliesMouseScaleBeforeOneFullSurfaceMask()
    {
        List<string> calls = [];
        OpenVrOverlayMouseScaleCall mouseScale = default;
        OpenVrOverlayIntersectionMaskCall intersectionMask = default;

        OpenVrInterop.ApplyInteractionConfiguration(
            new OverlaySurfaceSpec(1280, 720),
            call =>
            {
                calls.Add("mouse-scale");
                mouseScale = call;
            },
            call =>
            {
                calls.Add("intersection-mask");
                intersectionMask = call;
            });

        Assert.Equal(new[] { "mouse-scale", "intersection-mask" }, calls);
        Assert.Equal(new OpenVrOverlayMouseScaleCall(1280, 720), mouseScale);
        Assert.Equal(
            new OpenVrOverlayIntersectionMaskCall(
                new OpenVrIntersectionMaskRectangleSpec(0, 0, 1280, 720),
                PrimitiveCount: 1,
                PrimitiveSize: 20),
            intersectionMask);
    }

    [Fact]
    public void InteractionMask_ExplicitlyIncludesTheResultHeaderAndBodyRailControls()
    {
        OpenVrOverlayInteractionConfiguration configuration =
            OpenVrInterop.CreateInteractionConfiguration(
                new OverlaySurfaceSpec(ResultPanelTexture.PixelWidth, ResultPanelTexture.PixelHeight));
        OpenVrIntersectionMaskRectangleSpec mask = configuration.IntersectionMask;

        Assert.True(mask.ContainsRectangle(
            0,
            0,
            ResultPanelTexture.PixelWidth,
            (float)ResultPanelTexture.HeaderHeight));
        Assert.True(mask.ContainsRectangle(
            (float)ResultPanelTexture.PreviousPageButtonBounds.X,
            (float)ResultPanelTexture.PreviousPageButtonBounds.Y,
            (float)ResultPanelTexture.PreviousPageButtonBounds.Width,
            (float)ResultPanelTexture.PreviousPageButtonBounds.Height));
        Assert.True(mask.ContainsRectangle(
            (float)ResultPanelTexture.NextPageButtonBounds.X,
            (float)ResultPanelTexture.NextPageButtonBounds.Y,
            (float)ResultPanelTexture.NextPageButtonBounds.Width,
            (float)ResultPanelTexture.NextPageButtonBounds.Height));
        Assert.True(mask.ContainsRectangle(
            (float)ResultPanelTexture.CloseButtonBounds.X,
            (float)ResultPanelTexture.CloseButtonBounds.Y,
            (float)ResultPanelTexture.CloseButtonBounds.Width,
            (float)ResultPanelTexture.CloseButtonBounds.Height));
    }

    [Fact]
    public void InteractionConfiguration_PreservesFloatDimensionsUsedByTheNativeRectangleAbi()
    {
        OpenVrOverlayInteractionConfiguration configuration =
            OpenVrInterop.CreateInteractionConfiguration(new OverlaySurfaceSpec(1280.5f, 720.25f));

        Assert.Equal(1280.5f, configuration.MouseScaleWidth);
        Assert.Equal(720.25f, configuration.MouseScaleHeight);
        Assert.Equal(
            new OpenVrIntersectionMaskRectangleSpec(0, 0, 1280.5f, 720.25f),
            configuration.IntersectionMask);
    }

    [Theory]
    [InlineData((int)OpenVrIntersectionOutcome.OverlayHidden, false)]
    [InlineData((int)OpenVrIntersectionOutcome.NativeMiss, false)]
    [InlineData((int)OpenVrIntersectionOutcome.MappingRejected, true)]
    [InlineData((int)OpenVrIntersectionOutcome.Hit, true)]
    public void IntersectionAttempt_ExposesRawUvWhenOpenVrReturnedOne(
        int outcomeValue,
        bool expectedHasRawPoint)
    {
        OpenVrIntersectionOutcome outcome = (OpenVrIntersectionOutcome)outcomeValue;
        OpenVrIntersectionAttempt attempt = new(outcome, new OverlayLocalPoint(0.25f, 0.75f));

        Assert.Equal(expectedHasRawPoint, attempt.HasRawPoint);
    }

    [Fact]
    public void NativeIntersectionMapping_PreservesRejectedRawUvAndReason()
    {
        OverlayLocalPoint rejectedRawPoint = new(1.01f, 0.75f);
        OpenVrNativeIntersection nativeIntersection = new(
            rejectedRawPoint,
            new OpenVrVector3(1, 2, 3),
            new OpenVrVector3(0, 0, 1),
            0.5f);

        bool hit = OpenVrInterop.TryMapNativeIntersection(
            new OpenVrRay(new OpenVrVector3(0, 0, 0), new OpenVrVector3(0, 0, -1)),
            OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720)),
            nativeIntersection,
            out OpenVrIntersection intersection,
            out OpenVrIntersectionAttempt attempt);

        Assert.False(hit);
        Assert.Equal(default, intersection);
        Assert.Equal(OpenVrIntersectionOutcome.MappingRejected, attempt.Outcome);
        Assert.True(attempt.HasRawPoint);
        Assert.Equal(rejectedRawPoint, attempt.RawPoint);
    }

    [Fact]
    public void NativeIntersectionMapping_ReportsHitAndBuildsLogicalIntersection()
    {
        OverlayLocalPoint rawPoint = new(0.25f, 0.75f);
        OpenVrVector3 direction = new(0, 0, -1);
        OpenVrNativeIntersection nativeIntersection = new(
            rawPoint,
            new OpenVrVector3(1, 2, 3),
            new OpenVrVector3(0, 0, 1),
            0.5f);

        bool hit = OpenVrInterop.TryMapNativeIntersection(
            new OpenVrRay(new OpenVrVector3(0, 0, 0), direction),
            OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720)),
            nativeIntersection,
            out OpenVrIntersection intersection,
            out OpenVrIntersectionAttempt attempt);

        Assert.True(hit);
        Assert.Equal(OpenVrIntersectionOutcome.Hit, attempt.Outcome);
        Assert.True(attempt.HasRawPoint);
        Assert.Equal(rawPoint, attempt.RawPoint);
        Assert.Equal(new OverlayLocalPoint(320, 180), intersection.LocalPoint);
        Assert.Equal(nativeIntersection.Point, intersection.TrackingPoint);
        Assert.Equal(nativeIntersection.Normal, intersection.TrackingNormal);
        Assert.Equal(direction, intersection.PointerDirection);
        Assert.Equal(nativeIntersection.Distance, intersection.DistanceMeters);
        Assert.Equal(rawPoint, intersection.RawPoint);
    }

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

    [Fact]
    public void FullTextureView_UsesTheSameCoordinateContractWithoutAnInset()
    {
        OverlaySurfaceSpec surface = new(1280, 720);
        OverlayTextureView view = OverlayTextureView.CreateFull(surface);

        Assert.Equal(surface, view.Surface);
        Assert.Equal(0, view.Cell);
        Assert.Equal(1, view.Columns);
        Assert.Equal(1, view.Rows);
        Assert.Equal(new OverlayTextureBounds(0, 0, 1, 1), view.UpperLeftTextureBounds);

        AssertMapped(view, new OverlayLocalPoint(0, 0));
        AssertMapped(view, new OverlayLocalPoint(640, 360));
        AssertMapped(view, new OverlayLocalPoint(1280, 720));
        AssertNotMapped(view, new OverlayLocalPoint(-1, 360));
        AssertNotMapped(view, new OverlayLocalPoint(1281, 360));
        AssertNotMapped(view, new OverlayLocalPoint(640, -1));
        AssertNotMapped(view, new OverlayLocalPoint(640, 721));
    }

    [Fact]
    public void AtlasTextureView_UsesExactHalfTexelBoundsForEveryResultGridCell()
    {
        OverlaySurfaceSpec surface = new(1280, 720);
        const int columns = 2;
        const int rows = 3;
        float atlasWidth = surface.LogicalWidth * columns;
        float atlasHeight = surface.LogicalHeight * rows;

        for (int cell = 0; cell < columns * rows; cell++)
        {
            OverlayTextureView view = OverlayTextureView.CreateAtlasCell(
                surface,
                cell,
                columns,
                rows);
            int column = cell % columns;
            int row = cell / columns;
            OverlayTextureBounds bounds = view.UpperLeftTextureBounds;

            Assert.Equal(surface, view.Surface);
            Assert.Equal(cell, view.Cell);
            Assert.Equal(columns, view.Columns);
            Assert.Equal(rows, view.Rows);
            AssertClose(((column * surface.LogicalWidth) + 0.5f) / atlasWidth, bounds.UMin);
            AssertClose((((column + 1) * surface.LogicalWidth) - 0.5f) / atlasWidth, bounds.UMax);
            AssertClose(((row * surface.LogicalHeight) + 0.5f) / atlasHeight, bounds.VMin);
            AssertClose((((row + 1) * surface.LogicalHeight) - 0.5f) / atlasHeight, bounds.VMax);
        }
    }

    [Fact]
    public void AtlasTextureView_MapsCenterEdgesCornersAndOnePixelBoundariesInEveryResultGridCell()
    {
        OverlaySurfaceSpec surface = new(1280, 720);
        const int columns = 2;
        const int rows = 3;
        OverlayLocalPoint[] mappedPoints =
        [
            new(0, 0),
            new(surface.LogicalWidth / 2, 0),
            new(surface.LogicalWidth, 0),
            new(0, surface.LogicalHeight / 2),
            new(surface.LogicalWidth / 2, surface.LogicalHeight / 2),
            new(surface.LogicalWidth, surface.LogicalHeight / 2),
            new(0, surface.LogicalHeight),
            new(surface.LogicalWidth / 2, surface.LogicalHeight),
            new(surface.LogicalWidth, surface.LogicalHeight),
            new(1, 1),
            new(surface.LogicalWidth - 1, 1),
            new(1, surface.LogicalHeight - 1),
            new(surface.LogicalWidth - 1, surface.LogicalHeight - 1),
        ];
        OverlayLocalPoint[] rejectedPoints =
        [
            new(-1, surface.LogicalHeight / 2),
            new(surface.LogicalWidth + 1, surface.LogicalHeight / 2),
            new(surface.LogicalWidth / 2, -1),
            new(surface.LogicalWidth / 2, surface.LogicalHeight + 1),
        ];

        for (int cell = 0; cell < columns * rows; cell++)
        {
            OverlayTextureView view = OverlayTextureView.CreateAtlasCell(
                surface,
                cell,
                columns,
                rows);
            foreach (OverlayLocalPoint expected in mappedPoints)
            {
                AssertMapped(view, expected);
            }

            foreach (OverlayLocalPoint outside in rejectedPoints)
            {
                AssertNotMapped(view, outside);
            }
        }
    }

    [Fact]
    public void AtlasTextureView_AcceptsIdealCellEdgesAndFloatRoundingAtThePerimeter()
    {
        OverlaySurfaceSpec surface = new(1280, 720);
        const int columns = 2;
        const int rows = 3;

        for (int cell = 0; cell < columns * rows; cell++)
        {
            OverlayTextureView view = OverlayTextureView.CreateAtlasCell(
                surface,
                cell,
                columns,
                rows);
            int column = cell % columns;
            int row = cell / columns;
            float idealUMin = (float)column / columns;
            float idealUMax = (float)(column + 1) / columns;
            float idealLowerVMin = 1f - ((float)(row + 1) / rows);
            float idealLowerVMax = 1f - ((float)row / rows);
            float middleU = (idealUMin + idealUMax) / 2;
            float middleV = (idealLowerVMin + idealLowerVMax) / 2;

            AssertRawMapped(view, idealUMin, idealLowerVMax, new OverlayLocalPoint(0, 0));
            AssertRawMapped(
                view,
                idealUMax,
                idealLowerVMax,
                new OverlayLocalPoint(surface.LogicalWidth, 0));
            AssertRawMapped(
                view,
                idealUMin,
                idealLowerVMin,
                new OverlayLocalPoint(0, surface.LogicalHeight));
            AssertRawMapped(
                view,
                idealUMax,
                idealLowerVMin,
                new OverlayLocalPoint(surface.LogicalWidth, surface.LogicalHeight));
            AssertRawMapped(view, MathF.BitDecrement(idealUMin), middleV, new OverlayLocalPoint(0, 360));
            AssertRawMapped(
                view,
                MathF.BitIncrement(idealUMax),
                middleV,
                new OverlayLocalPoint(surface.LogicalWidth, 360));
            AssertRawMapped(view, middleU, MathF.BitIncrement(idealLowerVMax), new OverlayLocalPoint(640, 0));
            AssertRawMapped(
                view,
                middleU,
                MathF.BitDecrement(idealLowerVMin),
                new OverlayLocalPoint(640, surface.LogicalHeight));
        }
    }

    [Fact]
    public void AtlasTextureView_CellSwitchMovesRawCoordinatesButPreservesLogicalPoint()
    {
        OverlaySurfaceSpec surface = new(1280, 720);
        OverlayLocalPoint expected = new(377, 219);
        OverlayLocalPoint? previousRaw = null;

        for (int cell = 0; cell < 6; cell++)
        {
            OverlayTextureView view = OverlayTextureView.CreateAtlasCell(surface, cell, 2, 3);
            OverlayLocalPoint raw = ToAtlasGlobalLowerOrigin(view, expected);

            Assert.True(view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
                raw.X,
                raw.Y,
                out OverlayLocalPoint actual));
            AssertPoint(expected, actual);
            if (previousRaw is OverlayLocalPoint previous)
            {
                Assert.NotEqual(previous, raw);
            }

            previousRaw = raw;
        }
    }

    [Fact]
    public void ResultFullTextureView_MapsEveryRailControlBoundaryAcrossAllPages()
    {
        ResultPanelTexture texture = CreateScrollableTexture();
        OverlayTextureView view = OverlayTextureView.CreateFull(
            new OverlaySurfaceSpec(ResultPanelTexture.PixelWidth, ResultPanelTexture.PixelHeight));

        Rect[] controls =
        [
            ResultPanelTexture.PreviousPageButtonBounds,
            ResultPanelTexture.NextPageButtonBounds,
            ResultPanelTexture.CloseButtonBounds,
        ];
        for (int page = 0; page < 3; page++)
        {
            Assert.Equal(page, texture.CurrentResultPage);
            foreach (Rect bounds in controls)
            {
                AssertControlThroughView(view, texture, bounds, page);
            }

            if (page < 2)
            {
                Assert.True(texture.Apply(ResultPanelAction.NextPage));
            }
        }
    }

    [Fact]
    public void ResultFullTextureView_SharedRailBoundariesFollowVisualDrawOrder()
    {
        ResultPanelTexture texture = CreateScrollableTexture();
        OverlayTextureView fullView = OverlayTextureView.CreateFull(
            new OverlaySurfaceSpec(ResultPanelTexture.PixelWidth, ResultPanelTexture.PixelHeight));

        Assert.Equal(
            ResultPanelAction.NextPage,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(884, 620)));
        Assert.Equal(
            ResultPanelAction.Close,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(1020, 620)));

        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        Assert.Equal(
            ResultPanelAction.NextPage,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(884, 620)));
        Assert.Equal(
            ResultPanelAction.Close,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(1020, 620)));

        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        Assert.Equal(
            ResultPanelAction.None,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(884, 620)));
        Assert.Equal(
            ResultPanelAction.Close,
            HitResultThroughView(fullView, texture, new OverlayLocalPoint(1020, 620)));
    }

    [Theory]
    [InlineData(float.NaN, 0.5f)]
    [InlineData(0.5f, float.NaN)]
    [InlineData(float.PositiveInfinity, 0.5f)]
    [InlineData(0.5f, float.NegativeInfinity)]
    [InlineData(-0.01f, 0.5f)]
    [InlineData(1.01f, 0.5f)]
    [InlineData(0.5f, -0.01f)]
    [InlineData(0.5f, 1.01f)]
    public void TextureView_FailsClosedForNonFiniteOrOutOfViewCoordinates(
        float atlasU,
        float atlasV)
    {
        OverlayTextureView view = OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720));

        bool mapped = view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
            atlasU,
            atlasV,
            out OverlayLocalPoint point);

        Assert.False(mapped);
        Assert.Equal(default, point);
    }

    [Fact]
    public void TextureView_DefaultValueFailsClosed()
    {
        bool mapped = default(OverlayTextureView).TryMapAtlasGlobalLowerOriginToTopLeftLocal(
            0.5f,
            0.5f,
            out OverlayLocalPoint point);

        Assert.False(mapped);
        Assert.Equal(default, point);
    }

    [Fact]
    public void AtlasTextureView_RejectsInvalidGridSelection()
    {
        OverlaySurfaceSpec surface = new(1280, 720);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayTextureView.CreateAtlasCell(surface, 0, 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayTextureView.CreateAtlasCell(surface, 0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayTextureView.CreateAtlasCell(surface, -1, 2, 3));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OverlayTextureView.CreateAtlasCell(surface, 6, 2, 3));
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

    private static void AssertMapped(OverlayTextureView view, OverlayLocalPoint expected)
    {
        OverlayLocalPoint raw = ToAtlasGlobalLowerOrigin(view, expected);

        bool mapped = view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
            raw.X,
            raw.Y,
            out OverlayLocalPoint actual);

        Assert.True(mapped);
        AssertPoint(expected, actual);
    }

    private static ResultPanelTexture CreateScrollableTexture()
    {
        ResultPanelTexture texture = new();
        texture.SetContent("title", string.Join('\n', Enumerable.Repeat("long result line", 80)));
        _ = texture.RenderRgba();
        Assert.Equal(3, texture.ResultPageCount);
        return texture;
    }

    private static void AssertControlThroughView(
        OverlayTextureView view,
        ResultPanelTexture texture,
        Rect bounds,
        int page)
    {
        float left = (float)bounds.Left;
        float top = (float)bounds.Top;
        float right = (float)bounds.Right;
        float bottom = (float)bounds.Bottom;
        float centerX = (left + right) / 2;
        float centerY = (top + bottom) / 2;
        float[] xCoordinates =
        [
            left - 1,
            left,
            left + 1,
            centerX,
            right - 1,
            right,
            right + 1,
        ];
        float[] yCoordinates =
        [
            top - 1,
            top,
            top + 1,
            centerY,
            bottom - 1,
            bottom,
            bottom + 1,
        ];
        foreach (float x in xCoordinates)
        {
            foreach (float y in yCoordinates)
            {
                OverlayLocalPoint local = new(x, y);
                OverlayLocalPoint raw = ToAtlasGlobalLowerOrigin(view, local);
                Assert.True(view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
                    raw.X,
                    raw.Y,
                    out OverlayLocalPoint mapped));
                AssertPoint(local, mapped);

                ResultPanelAction expected = ExpectedRailAction(page, local);
                ResultPanelAction actual = texture.HitTestResult(mapped.X, mapped.Y);
                Assert.True(
                    actual == expected,
                    $"Expected {expected} at logical ({local.X}, {local.Y}), but got {actual} in cell {view.Cell}.");
            }
        }
    }

    private static ResultPanelAction ExpectedRailAction(int page, OverlayLocalPoint point)
    {
        if (ResultPanelTexture.CloseButtonBounds.Contains(point.X, point.Y))
        {
            return ResultPanelAction.Close;
        }

        if (ResultPanelTexture.NextPageButtonBounds.Contains(point.X, point.Y))
        {
            return page < 2
                ? ResultPanelAction.NextPage
                : ResultPanelAction.None;
        }

        if (ResultPanelTexture.PreviousPageButtonBounds.Contains(point.X, point.Y))
        {
            return page > 0
                ? ResultPanelAction.PreviousPage
                : ResultPanelAction.None;
        }

        return ResultPanelAction.None;
    }

    private static ResultPanelAction HitResultThroughView(
        OverlayTextureView view,
        ResultPanelTexture texture,
        OverlayLocalPoint local)
    {
        OverlayLocalPoint raw = ToAtlasGlobalLowerOrigin(view, local);
        if (!view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
                raw.X,
                raw.Y,
                out OverlayLocalPoint mapped))
        {
            return ResultPanelAction.None;
        }

        return texture.HitTestResult(mapped.X, mapped.Y);
    }

    private static void AssertRawMapped(
        OverlayTextureView view,
        float atlasU,
        float atlasV,
        OverlayLocalPoint expected)
    {
        bool mapped = view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
            atlasU,
            atlasV,
            out OverlayLocalPoint actual);

        Assert.True(mapped);
        AssertPoint(expected, actual);
    }

    private static void AssertNotMapped(OverlayTextureView view, OverlayLocalPoint outside)
    {
        OverlayLocalPoint raw = ToAtlasGlobalLowerOrigin(view, outside);

        Assert.False(view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
            raw.X,
            raw.Y,
            out _));
    }

    private static OverlayLocalPoint ToAtlasGlobalLowerOrigin(
        OverlayTextureView view,
        OverlayLocalPoint local)
    {
        OverlayTextureBounds bounds = view.UpperLeftTextureBounds;
        float normalizedX = local.X / view.Surface.LogicalWidth;
        float normalizedY = local.Y / view.Surface.LogicalHeight;
        float atlasU = bounds.UMin + (normalizedX * (bounds.UMax - bounds.UMin));
        float lowerLeftVMin = 1f - bounds.VMax;
        float lowerLeftVMax = 1f - bounds.VMin;
        float atlasV = lowerLeftVMax - (normalizedY * (lowerLeftVMax - lowerLeftVMin));
        return new OverlayLocalPoint(atlasU, atlasV);
    }

    private static void AssertPoint(OverlayLocalPoint expected, OverlayLocalPoint actual)
    {
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(MathF.Abs(expected - actual), 0, 0.001f);
}
