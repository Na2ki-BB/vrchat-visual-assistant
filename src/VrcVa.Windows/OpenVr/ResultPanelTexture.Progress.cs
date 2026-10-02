using System.Windows;
using System.Windows.Media;
using VrcVa.Windows.Rendering;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace VrcVa.Windows.OpenVr;

internal readonly record struct OperationProgressButton(OperationProgressAction Action, Rect Bounds, string Label, bool Enabled);

internal sealed partial class ResultPanelTexture
{
    // Full-size, fixed body rail. Rendering and hit testing enumerate this same
    // collection; every state retains visible but explicitly disabled controls.
    internal static readonly Rect StopProgressBounds = new(54, 580, 250, 80);
    internal static readonly Rect CancelProgressBounds = new(318, 580, 180, 80);
    internal static readonly Rect RetryProgressBounds = new(512, 580, 220, 80);
    internal static readonly Rect AdjustProgressPlacementBounds = new(746, 580, 220, 80);
    internal static readonly Rect CloseProgressBounds = new(980, 580, 200, 80);
    internal OperationProgressSnapshot? Progress { get; private set; }

    internal void SetProgress(OperationProgressSnapshot snapshot)
    {
        VoiceSearch = null;
        Progress = snapshot;
        _title = snapshot.Title;
        _resultPage = 0;
        _resultPageCount = 1;
        _resultTruncated = false;
    }

    internal static IReadOnlyList<OperationProgressButton> ProgressControls(OperationProgressSnapshot snapshot) =>
    [
        new(OperationProgressAction.Stop, StopProgressBounds, "マイク停止 → 認識", snapshot.CanStop),
        new(OperationProgressAction.Cancel, CancelProgressBounds, "中止", snapshot.CanCancel),
        new(OperationProgressAction.Retry, RetryProgressBounds, snapshot.RetryLabel, snapshot.CanRetry),
        new(OperationProgressAction.AdjustPlacement, AdjustProgressPlacementBounds, "位置調整", snapshot.CanAdjustPlacement),
        new(OperationProgressAction.Close, CloseProgressBounds, "閉じる", snapshot.CanClose),
    ];

    internal OperationProgressAction HitTestProgress(float x, float y)
    {
        if (Progress is null) { return OperationProgressAction.None; }
        foreach (OperationProgressButton button in ProgressControls(Progress))
        {
            if (button.Bounds.Contains(x, y))
            {
                return button.Enabled ? button.Action : OperationProgressAction.None;
            }
        }
        return OperationProgressAction.None;
    }

    private void DrawProgress(DrawingContext drawing)
    {
        OperationProgressSnapshot snapshot = Progress!;
        DrawBackground(drawing);
        DrawHeader(drawing);
        // Never let unusually long typed messages paint over the controls.
        drawing.PushClip(new RectangleGeometry(new Rect(BodyLeft, BodyTop, BodyRight - BodyLeft, BodyTextBottom - BodyTop)));
        FormattedText message = CreateText(snapshot.Message, 32, FontWeights.SemiBold, Brushes.White, BodyRight - BodyLeft);
        message.LineHeight = 46;
        drawing.DrawText(message, new Point(BodyLeft, 150));
        FormattedText detail = CreateText(snapshot.Detail, 26, FontWeights.Normal, Brushes.LightGray, BodyRight - BodyLeft);
        detail.LineHeight = 37;
        drawing.DrawText(detail, new Point(BodyLeft, 350));
        drawing.Pop();
        foreach (OperationProgressButton button in ProgressControls(snapshot))
        {
            DrawResultButton(drawing, button.Bounds, button.Label, button.Enabled);
        }
    }
}
