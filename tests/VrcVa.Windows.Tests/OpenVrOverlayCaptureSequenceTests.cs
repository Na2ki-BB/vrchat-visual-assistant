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
