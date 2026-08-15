using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrOverlayCaptureSequenceTests
{
    [Fact]
    public void CaptureAfterOverlayHidden_DiscardsStaleMarkerFrameAndAdoptsCleanFrame()
    {
        SyntheticOverlay overlay = new();
        List<SyntheticFrame> frames = [];

        SyntheticFrame adopted = OpenVrOverlayCaptureSequence.CaptureAfterOverlayHidden(
            overlay,
            () =>
            {
                overlay.RecordCapture();
                SyntheticFrame frame = new(containsMarker: frames.Count == 0);
                frames.Add(frame);
                return frame;
            },
            CancellationToken.None);

        Assert.Equal(
            ["hide-confirm", "wait", "wait", "wait", "capture", "wait", "capture", "suppress-end"],
            overlay.Events);
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].ContainsMarker);
        Assert.True(frames[0].IsDisposed);
        Assert.False(adopted.ContainsMarker);
        Assert.False(adopted.IsDisposed);
        adopted.Dispose();
    }

    [Fact]
    public void CaptureAfterOverlayHidden_ReleasesSuppressionWhenHideFails()
    {
        FailingHideOverlay overlay = new();

        Assert.Throws<InvalidOperationException>(() =>
            OpenVrOverlayCaptureSequence.CaptureAfterOverlayHidden(
                overlay,
                () => new SyntheticFrame(containsMarker: false),
                CancellationToken.None));
        Assert.True(overlay.SuppressionEnded);
    }

    private sealed class SyntheticOverlay : IOpenVrOverlayCaptureGate
    {
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events => _events;

        public void HideAndConfirmInvisible(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("hide-confirm");
        }

        public void EndCaptureSuppression() => _events.Add("suppress-end");

        public void WaitFrameSync(uint timeoutMilliseconds = 1000) => _events.Add("wait");

        public void RecordCapture() => _events.Add("capture");
    }

    private sealed class SyntheticFrame(bool containsMarker) : IDisposable
    {
        public bool ContainsMarker { get; } = containsMarker;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FailingHideOverlay : IOpenVrOverlayCaptureGate
    {
        public bool SuppressionEnded { get; private set; }

        public void HideAndConfirmInvisible(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic hide failure");

        public void EndCaptureSuppression() => SuppressionEnded = true;

        public void WaitFrameSync(uint timeoutMilliseconds = 1000) =>
            throw new InvalidOperationException("wait must not run");
    }
}

public sealed class ResultPanelTextureTests
{
    [Theory]
    [InlineData(173, 202, (int)ResultPanelCalibrationAction.MoveLeft)]
    [InlineData(484, 202, (int)ResultPanelCalibrationAction.MoveRight)]
    [InlineData(795, 202, (int)ResultPanelCalibrationAction.MoveUp)]
    [InlineData(1106, 202, (int)ResultPanelCalibrationAction.MoveDown)]
    [InlineData(173, 362, (int)ResultPanelCalibrationAction.MoveNear)]
    [InlineData(484, 362, (int)ResultPanelCalibrationAction.MoveFar)]
    [InlineData(795, 362, (int)ResultPanelCalibrationAction.MakeSmaller)]
    [InlineData(1106, 362, (int)ResultPanelCalibrationAction.MakeLarger)]
    [InlineData(188, 560, (int)ResultPanelCalibrationAction.Reset)]
    [InlineData(510, 560, (int)ResultPanelCalibrationAction.Cancel)]
    [InlineData(961, 560, (int)ResultPanelCalibrationAction.Save)]
    public void CalibrationHitTest_RecognizesLargeButtons(
        float x,
        float y,
        int expectedValue)
    {
        Assert.Equal(
            (ResultPanelCalibrationAction)expectedValue,
            ResultPanelTexture.HitTestCalibration(x, y));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(320, 200)]
    [InlineData(640, 400)]
    [InlineData(1250, 700)]
    public void CalibrationHitTest_RejectsGapsAndOutside(float x, float y)
    {
        Assert.Equal(
            ResultPanelCalibrationAction.None,
            ResultPanelTexture.HitTestCalibration(x, y));
    }

    [Fact]
    public void RenderCalibrationRgba_CreatesOneLogicalUiTexture()
    {
        ResultPanelTexture texture = new();

        byte[] pixels = texture.RenderCalibrationRgba();

        Assert.Equal(
            ResultPanelTexture.PixelWidth * ResultPanelTexture.PixelHeight * 4,
            pixels.Length);
    }

    [Theory]
    [InlineData(1020, 580)]
    [InlineData(1180, 660)]
    [InlineData(1100, 620)]
    public void IsCloseButton_AcceptsVisibleBottomRailButton(float x, float y)
    {
        ResultPanelTexture texture = new();

        Assert.True(texture.IsCloseButton(x, y));
    }

    [Theory]
    [InlineData(1019, 620)]
    [InlineData(1181, 620)]
    [InlineData(1100, 579)]
    [InlineData(1100, 661)]
    [InlineData(1100, 300)]
    public void IsCloseButton_RejectsPointsOutsideButton(float x, float y)
    {
        ResultPanelTexture texture = new();

        Assert.False(texture.IsCloseButton(x, y));
    }

    [Fact]
    public void VisiblePageButtons_UseTheSameBoundsForHitTesting()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.Equal(ResultPanelAction.None, texture.HitTestResult(816, 620));
        Assert.Equal(ResultPanelAction.NextPage, texture.HitTestResult(952, 620));
        Assert.Equal(ResultPanelAction.Close, texture.HitTestResult(1100, 620));
        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        Assert.Equal(1, texture.CurrentResultPage);
        Assert.Equal(ResultPanelAction.PreviousPage, texture.HitTestResult(816, 620));
        Assert.True(texture.Apply(ResultPanelAction.PreviousPage));
        Assert.Equal(0, texture.CurrentResultPage);
    }

    [Fact]
    public void ResultRailControls_UseSharedBoundsAtCornersAndOnePixelOutside()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        AssertHitBounds(
            texture,
            ResultPanelTexture.NextPageButtonBounds,
            ResultPanelAction.NextPage);
        AssertHitBounds(
            texture,
            ResultPanelTexture.CloseButtonBounds,
            ResultPanelAction.Close);

        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        AssertHitBounds(
            texture,
            ResultPanelTexture.PreviousPageButtonBounds,
            ResultPanelAction.PreviousPage);
    }

    [Fact]
    public void ResultRail_UsesFixedGeometryInsideTheBodyHitBandAndClearOfScrollbar()
    {
        Assert.Equal(
            new System.Windows.Rect(748, 580, 136, 80),
            ResultPanelTexture.PreviousPageButtonBounds);
        Assert.Equal(
            new System.Windows.Rect(884, 580, 136, 80),
            ResultPanelTexture.NextPageButtonBounds);
        Assert.Equal(
            new System.Windows.Rect(1020, 580, 160, 80),
            ResultPanelTexture.CloseButtonBounds);
        Assert.Equal(
            ResultPanelTexture.PreviousPageButtonBounds.Right,
            ResultPanelTexture.NextPageButtonBounds.Left);
        Assert.Equal(
            ResultPanelTexture.NextPageButtonBounds.Right,
            ResultPanelTexture.CloseButtonBounds.Left);

        System.Windows.Rect scrollbarBounds = new(
            ResultPanelTexture.ScrollTrackLeft,
            ResultPanelTexture.BodyTop,
            ResultPanelTexture.ScrollTrackRight - ResultPanelTexture.ScrollTrackLeft,
            ResultPanelTexture.BodyBottom - ResultPanelTexture.BodyTop);
        System.Windows.Rect[] controls =
        [
            ResultPanelTexture.PreviousPageButtonBounds,
            ResultPanelTexture.NextPageButtonBounds,
            ResultPanelTexture.CloseButtonBounds,
        ];
        foreach (System.Windows.Rect bounds in controls)
        {
            Assert.True(bounds.Left >= ResultPanelTexture.BodyLeft);
            Assert.True(bounds.Right <= ResultPanelTexture.BodyRight);
            Assert.True(bounds.Top > ResultPanelTexture.BodyTextBottom);
            Assert.True(bounds.Top >= ResultPanelTexture.BodyTop);
            Assert.True(bounds.Bottom <= ResultPanelTexture.BodyBottom);
            Assert.False(bounds.IntersectsWith(scrollbarBounds));
        }
    }

    [Fact]
    public void HeaderArea_DoesNotExposeResultActions()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        for (int y = 0; y <= ResultPanelTexture.HeaderHeight; y++)
        {
            for (int x = 0; x <= ResultPanelTexture.PixelWidth; x++)
            {
                Assert.Equal(ResultPanelAction.None, texture.HitTestResult(x, y));
            }
        }
    }

    [Fact]
    public void DisabledRailControls_ReturnNoneWithoutFallingThrough()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.Equal(ResultPanelAction.None, texture.HitTestResult(816, 620));
        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        Assert.True(texture.Apply(ResultPanelAction.NextPage));
        Assert.Equal(ResultPanelAction.None, texture.HitTestResult(952, 620));
    }

    [Fact]
    public void ScrollbarTrackClick_JumpsTowardClickedPosition()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.True(texture.BeginScrollbarInteraction(1235, 640));
        Assert.Equal(texture.ResultPageCount - 1, texture.CurrentResultPage);
        Assert.True(texture.Scroll(1));
        Assert.Equal(texture.ResultPageCount - 2, texture.CurrentResultPage);
    }

    [Fact]
    public void ScrollbarClicks_MoveBetweenTrackEnds()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.True(texture.BeginScrollbarInteraction(1235, 640));
        Assert.Equal(texture.ResultPageCount - 1, texture.CurrentResultPage);
        Assert.True(texture.BeginScrollbarInteraction(1235, 120));
        Assert.Equal(0, texture.CurrentResultPage);
    }

    [Fact]
    public void Scroll_AdvancesTheLegacyAtlasCellIndexWithTheCurrentPage()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.Equal(0, texture.CurrentResultPage);
        Assert.Equal(ResultPanelTexture.WaitingCell + 3, texture.CurrentResultCell);
        Assert.True(texture.Scroll(-1));
        Assert.Equal(1, texture.CurrentResultPage);
        Assert.Equal(ResultPanelTexture.WaitingCell + 4, texture.CurrentResultCell);
    }

    [Fact]
    public void RenderRgba_CreatesFixedSixCellAtlasAndMarksLongResultAsTruncated()
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.Equal(3, texture.ResultPageCount);
        Assert.True(texture.ResultTruncated);
        Assert.Equal(
            ResultPanelTexture.AtlasPixelWidth * ResultPanelTexture.AtlasPixelHeight * 4,
            texture.RenderRgba().Length);
    }

    [Fact]
    public void RenderCurrentResultRgba_RendersEachPageAsFullTextureAndMatchesAtlasCells()
    {
        ResultPanelTexture texture = new();
        texture.SetContent(
            "title",
            string.Join('\n', Enumerable.Range(1, 80).Select(index => $"result line {index:D2}")));

        byte[] firstPage = texture.RenderCurrentResultRgba();

        Assert.Equal(
            ResultPanelTexture.PixelWidth * ResultPanelTexture.PixelHeight * 4,
            firstPage.Length);
        Assert.Equal(3, texture.ResultPageCount);
        Assert.True(texture.ResultTruncated);
        Assert.Equal(0, texture.CurrentResultPage);

        byte[] atlas = texture.RenderRgba();
        byte[] previousPage = firstPage;
        for (int expectedPage = 0; expectedPage < texture.ResultPageCount; expectedPage++)
        {
            byte[] currentPage = expectedPage == 0
                ? firstPage
                : texture.RenderCurrentResultRgba();

            Assert.Equal(
                ResultPanelTexture.PixelWidth * ResultPanelTexture.PixelHeight * 4,
                currentPage.Length);
            Assert.Equal(3, texture.ResultPageCount);
            Assert.True(texture.ResultTruncated);
            Assert.Equal(expectedPage, texture.CurrentResultPage);
            Assert.True(FullTextureMatchesAtlasCell(
                atlas,
                currentPage,
                texture.CurrentResultCell));

            if (expectedPage > 0)
            {
                Assert.False(previousPage.AsSpan().SequenceEqual(currentPage));
            }

            previousPage = currentPage;
            if (expectedPage < texture.ResultPageCount - 1)
            {
                Assert.True(texture.Apply(ResultPanelAction.NextPage));
            }
        }
    }

    [Theory]
    [InlineData(1197, 300)]
    [InlineData(1269, 300)]
    [InlineData(1235, 115)]
    [InlineData(1235, 669)]
    public void ScrollbarInteraction_RejectsOutsideHitTarget(float x, float y)
    {
        ResultPanelTexture texture = CreateScrollableTexture();

        Assert.False(texture.BeginScrollbarInteraction(x, y));
    }

    private static ResultPanelTexture CreateScrollableTexture()
    {
        ResultPanelTexture texture = new();
        texture.SetContent("title", string.Join('\n', Enumerable.Repeat("long result line", 80)));
        _ = texture.RenderRgba();
        return texture;
    }

    private static bool FullTextureMatchesAtlasCell(
        byte[] atlas,
        byte[] fullTexture,
        int atlasCell)
    {
        int column = atlasCell % ResultPanelTexture.AtlasColumns;
        int row = atlasCell / ResultPanelTexture.AtlasColumns;
        int fullStride = ResultPanelTexture.PixelWidth * 4;
        int atlasStride = ResultPanelTexture.AtlasPixelWidth * 4;
        int cellLeftBytes = column * fullStride;
        int cellTop = row * ResultPanelTexture.PixelHeight;
        for (int y = 0; y < ResultPanelTexture.PixelHeight; y++)
        {
            int atlasOffset = ((cellTop + y) * atlasStride) + cellLeftBytes;
            int fullOffset = y * fullStride;
            if (!atlas.AsSpan(atlasOffset, fullStride)
                .SequenceEqual(fullTexture.AsSpan(fullOffset, fullStride)))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertHitBounds(
        ResultPanelTexture texture,
        System.Windows.Rect bounds,
        ResultPanelAction expected)
    {
        (float X, float Y)[] inside =
        [
            ((float)bounds.Left + 1, (float)bounds.Top + 1),
            ((float)bounds.Right - 1, (float)bounds.Top + 1),
            ((float)bounds.Left + 1, (float)bounds.Bottom - 1),
            ((float)bounds.Right - 1, (float)bounds.Bottom - 1),
            ((float)bounds.Left + (float)(bounds.Width / 2), (float)bounds.Top),
            ((float)bounds.Left + (float)(bounds.Width / 2), (float)bounds.Bottom),
            ((float)bounds.Left, (float)bounds.Top + (float)(bounds.Height / 2)),
            ((float)bounds.Right - 1, (float)bounds.Top + (float)(bounds.Height / 2)),
        ];
        foreach ((float x, float y) in inside)
        {
            Assert.Equal(expected, texture.HitTestResult(x, y));
        }

        (float X, float Y)[] outside =
        [
            ((float)bounds.Left - 1, (float)bounds.Top),
            ((float)bounds.Right + 1, (float)bounds.Top),
            ((float)bounds.Left, (float)bounds.Top - 1),
            ((float)bounds.Left, (float)bounds.Bottom + 1),
        ];
        foreach ((float x, float y) in outside)
        {
            Assert.NotEqual(expected, texture.HitTestResult(x, y));
        }
    }
}

public sealed class ResultPanelImageUploadTrackerTests
{
    [Theory]
    [InlineData((int)ResultPanelImageUploadKind.Calibration, true, false, true)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage, true, false, true)]
    [InlineData((int)ResultPanelImageUploadKind.Calibration, true, true, false)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage, true, true, false)]
    [InlineData((int)ResultPanelImageUploadKind.Calibration, false, false, false)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage, false, false, false)]
    [InlineData((int)ResultPanelImageUploadKind.Atlas, true, false, false)]
    public void FullTextureCompletion_ReloadsAtlasOnlyForPendingAtlasDisplay(
        int completedUploadValue,
        bool showAfterImageLoad,
        bool fullTextureStillActive,
        bool expected)
    {
        ResultPanelImageUploadKind completedUpload =
            (ResultPanelImageUploadKind)completedUploadValue;
        Assert.Equal(
            expected,
            SteamVrResultPanel.RequiresAtlasReloadAfterImageLoaded(
                completedUpload,
                fullTextureStillActive,
                showAfterImageLoad));
    }

    [Theory]
    [InlineData((int)ResultPanelImageUploadKind.Calibration)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage)]
    public void CompletedFullTextureUpload_DoesNotMarkAtlasAsLoaded(
        int uploadKindValue)
    {
        ResultPanelImageUploadKind uploadKind = (ResultPanelImageUploadKind)uploadKindValue;
        ResultPanelImageUploadTracker tracker = new();

        tracker.Begin(uploadKind);
        ResultPanelImageUploadKind completed = tracker.Complete();

        Assert.Equal(uploadKind, completed);
        Assert.False(tracker.InFlight);
        Assert.False(tracker.AtlasLoaded);
    }

    [Fact]
    public void CompletedAtlasUpload_MarksAtlasAsLoaded()
    {
        ResultPanelImageUploadTracker tracker = new();

        tracker.Begin(ResultPanelImageUploadKind.Atlas);
        ResultPanelImageUploadKind completed = tracker.Complete();

        Assert.Equal(ResultPanelImageUploadKind.Atlas, completed);
        Assert.False(tracker.InFlight);
        Assert.True(tracker.AtlasLoaded);
    }

    [Theory]
    [InlineData((int)ResultPanelImageUploadKind.Calibration)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage)]
    public void FullTextureUploadAfterAtlas_InvalidatesAtlasEvenAfterCompletion(
        int uploadKindValue)
    {
        ResultPanelImageUploadKind uploadKind = (ResultPanelImageUploadKind)uploadKindValue;
        ResultPanelImageUploadTracker tracker = new();
        tracker.Begin(ResultPanelImageUploadKind.Atlas);
        tracker.Complete();

        tracker.Begin(uploadKind);
        tracker.Complete();

        Assert.False(tracker.AtlasLoaded);
    }

    [Theory]
    [InlineData((int)ResultPanelImageUploadKind.Calibration)]
    [InlineData((int)ResultPanelImageUploadKind.ResultPage)]
    public void ResetDuringFullTextureUpload_LeavesNoLoadedImageState(
        int uploadKindValue)
    {
        ResultPanelImageUploadKind uploadKind = (ResultPanelImageUploadKind)uploadKindValue;
        ResultPanelImageUploadTracker tracker = new();
        tracker.Begin(uploadKind);

        tracker.Reset();

        Assert.False(tracker.InFlight);
        Assert.False(tracker.AtlasLoaded);
    }
}
