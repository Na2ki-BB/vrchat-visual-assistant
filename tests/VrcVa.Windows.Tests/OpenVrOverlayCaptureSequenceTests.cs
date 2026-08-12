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
