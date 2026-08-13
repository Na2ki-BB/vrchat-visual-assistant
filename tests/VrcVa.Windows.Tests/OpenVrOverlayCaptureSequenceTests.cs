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
            ["hide-confirm", "wait", "wait", "wait", "capture", "wait", "capture"],
            overlay.Events);
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].ContainsMarker);
        Assert.True(frames[0].IsDisposed);
        Assert.False(adopted.ContainsMarker);
        Assert.False(adopted.IsDisposed);
        adopted.Dispose();
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

        public void WaitFrameSync(uint timeoutMilliseconds = 1000) => _events.Add("wait");

        public void RecordCapture() => _events.Add("capture");
    }

    private sealed class SyntheticFrame(bool containsMarker) : IDisposable
    {
        public bool ContainsMarker { get; } = containsMarker;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
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
    [InlineData(173, 517.5, 173, 202.5)]
    [InlineData(795, 357.5, 795, 362.5)]
    [InlineData(188, 160, 188, 560)]
    public void MapFullTexturePointer_MapsOpenVrBottomOriginToLogicalUi(
        float rawX,
        float rawY,
        float expectedX,
        float expectedY)
    {
        (float actualX, float actualY) = ResultPanelTexture.MapFullTexturePointer(rawX, rawY);

        Assert.Equal(expectedX, actualX, precision: 2);
        Assert.Equal(expectedY, actualY, precision: 2);
    }

    [Theory]
    [InlineData(1020, 0)]
    [InlineData(1188, 719)]
    [InlineData(1100, 300)]
    public void IsCloseButton_AcceptsLargeButton(float x, float y)
    {
        ResultPanelTexture texture = new();

        Assert.True(texture.IsCloseButton(x, y));
    }

    [Theory]
    [InlineData(1019, 180)]
    [InlineData(1189, 60)]
    [InlineData(1235, 640)]
    public void IsCloseButton_RejectsPointsOutsideButton(float x, float y)
    {
        ResultPanelTexture texture = new();

        Assert.False(texture.IsCloseButton(x, y));
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

    [Theory]
    [InlineData(3, 1173, 460, 1066, 60)]
    [InlineData(3, 1257, 419, 1234, 183)]
    [InlineData(5, 1161, 207, 1042, 99)]
    [InlineData(5, 1260, 32, 1240, 624)]
    public void MapOpenVrPointer_MapsAtlasCoordinatesToVisiblePage(
        int cell,
        float openVrX,
        float openVrY,
        float expectedX,
        float expectedY)
    {
        (float actualX, float actualY) = ResultPanelTexture.MapOpenVrPointer(
            openVrX,
            openVrY,
            cell);

        Assert.Equal(expectedX, actualX, precision: 0);
        Assert.Equal(expectedY, actualY, precision: 0);
    }

    [Fact]
    public void Scroll_SelectsPreRenderedResultCellsWithoutRenderingAgain()
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
}

public sealed class ResultPanelImageUploadTrackerTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void CalibrationCompletion_ReloadsAtlasOnlyForPendingPostCalibrationDisplay(
        bool showAfterImageLoad,
        bool calibrationActive,
        bool expected)
    {
        Assert.Equal(
            expected,
            SteamVrResultPanel.RequiresAtlasReloadAfterImageLoaded(
                ResultPanelImageUploadKind.Calibration,
                calibrationActive,
                showAfterImageLoad));
        Assert.False(
            SteamVrResultPanel.RequiresAtlasReloadAfterImageLoaded(
                ResultPanelImageUploadKind.Atlas,
                calibrationActive,
                showAfterImageLoad));
    }

    [Fact]
    public void CompletedCalibrationUpload_DoesNotMarkAtlasAsLoaded()
    {
        ResultPanelImageUploadTracker tracker = new();

        tracker.Begin(ResultPanelImageUploadKind.Calibration);
        ResultPanelImageUploadKind completed = tracker.Complete();

        Assert.Equal(ResultPanelImageUploadKind.Calibration, completed);
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

    [Fact]
    public void CalibrationUploadAfterAtlas_InvalidatesAtlasEvenAfterCompletion()
    {
        ResultPanelImageUploadTracker tracker = new();
        tracker.Begin(ResultPanelImageUploadKind.Atlas);
        tracker.Complete();

        tracker.Begin(ResultPanelImageUploadKind.Calibration);
        tracker.Complete();

        Assert.False(tracker.AtlasLoaded);
    }

    [Fact]
    public void ResetDuringCalibrationUpload_LeavesNoLoadedImageState()
    {
        ResultPanelImageUploadTracker tracker = new();
        tracker.Begin(ResultPanelImageUploadKind.Calibration);

        tracker.Reset();

        Assert.False(tracker.InFlight);
        Assert.False(tracker.AtlasLoaded);
    }
}
