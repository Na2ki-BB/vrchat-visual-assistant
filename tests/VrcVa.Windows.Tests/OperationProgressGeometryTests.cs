using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;

namespace VrcVa.Windows.Tests;

public sealed class OperationProgressGeometryTests
{
    [Theory]
    [InlineData((int)OperationProgressState.Recording, true, true, false, false)]
    [InlineData((int)OperationProgressState.Recording, false, true, false, false)]
    [InlineData((int)OperationProgressState.Transcribing, false, true, false, false)]
    [InlineData((int)OperationProgressState.Processing, false, true, false, false)]
    [InlineData((int)OperationProgressState.Cancelling, false, false, false, false)]
    [InlineData((int)OperationProgressState.Failed, false, false, false, false)]
    [InlineData((int)OperationProgressState.Failed, false, false, true, true)]
    [InlineData((int)OperationProgressState.Failed, false, false, false, true)]
    [InlineData((int)OperationProgressState.Cancelled, false, false, false, true)]
    public void FullView_AllStateControls_CentersEdgesCornersAndOnePixelNeighborsMatchRenderedGeometry(
        int state, bool stop, bool cancel, bool retry, bool close)
    {
        ResultPanelTexture texture = new();
        OperationProgressSnapshot snapshot = new(Guid.NewGuid(), Guid.NewGuid(), (OperationProgressState)state,
            "title", "message", "detail", stop, cancel, retry, close);
        texture.SetProgress(snapshot);
        OverlayTextureView view = OverlayTextureView.CreateFull(new OverlaySurfaceSpec(1280, 720));
        Assert.Equal(0, view.UpperLeftTextureBounds.UMin);
        Assert.Equal(0, view.UpperLeftTextureBounds.VMin);
        Assert.Equal(1, view.UpperLeftTextureBounds.UMax);
        Assert.Equal(1, view.UpperLeftTextureBounds.VMax);
        IReadOnlyList<OperationProgressButton> buttons = ResultPanelTexture.ProgressControls(snapshot);
        Assert.Equal(5, buttons.Count);
        foreach (OperationProgressButton button in buttons)
        {
            Assert.True(button.Bounds.Top > ResultPanelTexture.HeaderHeight);
            float[] xs = Coordinates(button.Bounds.Left, button.Bounds.Right);
            float[] ys = Coordinates(button.Bounds.Top, button.Bounds.Bottom);
            foreach (float x in xs)
            {
                foreach (float y in ys)
                {
                    // The exact selected full-view bounds are also the only source
                    // of the reverse mapping: no idealized atlas cell/alternate rect.
                    float rawX = view.UpperLeftTextureBounds.UMin + x / 1280 * (view.UpperLeftTextureBounds.UMax - view.UpperLeftTextureBounds.UMin);
                    float rawY = 1 - (view.UpperLeftTextureBounds.VMin + y / 720 * (view.UpperLeftTextureBounds.VMax - view.UpperLeftTextureBounds.VMin));
                    Assert.True(view.TryMapAtlasGlobalLowerOriginToTopLeftLocal(rawX, rawY, out OverlayLocalPoint local));
                    Assert.InRange(Math.Abs(local.X - x), 0, 0.001f);
                    Assert.InRange(Math.Abs(local.Y - y), 0, 0.001f);
                    OperationProgressAction expected = button.Enabled && button.Bounds.Contains(x, y)
                        ? button.Action : OperationProgressAction.None;
                    OperationProgressAction actual = texture.HitTestProgress(local.X, local.Y);
                    Assert.Equal(expected, actual);
                    Assert.Equal(ResultPanelAction.None, texture.HitTestResult(local.X, local.Y));
                    Assert.False(texture.IsScrollbar(local.X, local.Y));
                    int target = actual == OperationProgressAction.None ? 0 : 500 + (int)actual;
                    PointerActivationGate gate = new();
                    Assert.Equal(0, gate.Update(target, true, true, true)); // enter while held
                    Assert.Equal(0, gate.Update(target, true, true, false));
                    Assert.Equal(0, gate.Update(target, true, false, true));
                    Assert.Equal(target, gate.Update(target, true, true, true));
                    Assert.Equal(0, gate.Update(target, true, true, true)); // no repeated press
                }
            }
        }
        for (int y = 0; y <= ResultPanelTexture.HeaderHeight; y += 18)
        {
            for (int x = 0; x <= 1280; x += 32)
            {
                Assert.Equal(OperationProgressAction.None, texture.HitTestProgress(x, y));
            }
        }
        Assert.False(texture.IsScrollbar(1230, 600));
    }

    [Fact]
    public void ProgressStateChangeWhileHeld_NeverActivatesNewlyEnabledControl()
    {
        ResultPanelTexture texture = new();
        OperationProgressSnapshot disabled = new(Guid.NewGuid(), Guid.NewGuid(), OperationProgressState.Cancelling,
            "title", "message", "detail");
        PointerActivationGate gate = new();
        foreach (OperationProgressButton button in ResultPanelTexture.ProgressControls(disabled))
        {
            texture.SetProgress(disabled);
            float x = (float)(button.Bounds.Left + button.Bounds.Width / 2);
            float y = (float)(button.Bounds.Top + button.Bounds.Height / 2);
            Assert.Equal(OperationProgressAction.None, texture.HitTestProgress(x, y));
            Assert.Equal(0, gate.Update(0, true, true, true));
            texture.SetProgress(disabled with
            {
                CanStop = true,
                CanCancel = true,
                CanRetry = true,
                CanAdjustPlacement = true,
                CanClose = true,
            });
            int target = 500 + (int)texture.HitTestProgress(x, y);
            Assert.Equal(0, gate.Update(target, true, true, false));
            Assert.Equal(0, gate.Update(target, true, true, true));
            gate.Reset(); // upload/input transition follows production reset discipline
            Assert.Equal(0, gate.Update(target, true, true, true));
            Assert.Equal(0, gate.Update(target, true, false, true));
            Assert.Equal(target, gate.Update(target, true, true, true));
        }
    }

    [Fact]
    public void ProgressRasterIsFullSize_AndReplacingWithResultClearsProgressActions()
    {
        ResultPanelTexture texture = new();
        texture.SetProgress(new(Guid.NewGuid(), Guid.NewGuid(), OperationProgressState.Failed,
            "文字起こしに失敗しました", "期限内に本人の操作で再試行できます。", "失敗: VoiceTranscriptionTimedOut", CanRetry: true, CanClose: true));
        Assert.Equal(1280 * 720 * 4, texture.RenderCurrentResultRgba().Length);
        Assert.Equal(1, texture.ResultPageCount);
        Assert.False(texture.ResultTruncated);
        texture.SetContent("result", "full result");
        Assert.Null(texture.Progress);
        Assert.Equal(OperationProgressAction.None, texture.HitTestProgress(1080, 620));
        Assert.Equal(ResultPanelAction.Close, texture.HitTestResult(1080, 620));
    }

    private static float[] Coordinates(double minimum, double maximum) =>
    [
        (float)minimum - 1, (float)minimum, (float)minimum + 1,
        (float)((minimum + maximum) / 2),
        (float)maximum - 1, (float)maximum, (float)maximum + 1,
    ];
}
