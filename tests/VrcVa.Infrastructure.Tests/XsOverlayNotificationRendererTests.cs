using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class XsOverlayNotificationRendererTests
{
    [Fact]
    public async Task RenderProgressAsync_DoesNotQueueDelayedXsOverlayNotifications()
    {
        RecordingSink sink = new();
        XsOverlayNotificationRenderer renderer = new(sink);
        Guid correlationId = Guid.NewGuid();

        await renderer.RenderProgressAsync(
            new ScanProgress(correlationId, ScanStage.Trigger, "start", TimeSpan.Zero),
            CancellationToken.None);
        await renderer.RenderProgressAsync(
            new ScanProgress(correlationId, ScanStage.Capture, "capture", TimeSpan.Zero),
            CancellationToken.None);
        await renderer.RenderProgressAsync(
            new ScanProgress(correlationId, ScanStage.Ocr, "ocr", TimeSpan.Zero),
            CancellationToken.None);

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public async Task RenderOutcomeAsync_DoesNotSendSuccessfulResultAsFixedNotification()
    {
        RecordingSink sink = new();
        XsOverlayNotificationRenderer renderer = new(sink);
        AnalysisResult result = new(
            "EMERGENCY EXIT",
            string.Empty,
            "en",
            "なし",
            "OCRのみ",
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero);

        await renderer.RenderOutcomeAsync(
            ScanOutcome.Succeeded(Guid.NewGuid(), result, TimeSpan.FromMilliseconds(40)),
            CancellationToken.None);

        Assert.Empty(sink.Notifications);
    }

    [Fact]
    public async Task RenderOutcomeAsync_NotifiesFailureStageAndMessage()
    {
        RecordingSink sink = new();
        XsOverlayNotificationRenderer renderer = new(sink);
        ScanOutcome outcome = ScanOutcome.Failed(
            Guid.NewGuid(),
            new ScanFailure(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "画面を取得できませんでした。"),
            TimeSpan.FromMilliseconds(10));

        await renderer.RenderOutcomeAsync(outcome, CancellationToken.None);

        Notification notification = Assert.Single(sink.Notifications);
        Assert.Equal("SCAN失敗 — Capture", notification.Title);
        Assert.Equal("画面を取得できませんでした。", notification.Content);
        Assert.Equal(XsOverlayNotificationKind.Error, notification.Kind);
    }

    private sealed class RecordingSink : IXsOverlayNotificationSink
    {
        public List<Notification> Notifications { get; } = [];

        public Task SendAsync(
            string title,
            string content,
            XsOverlayNotificationKind kind,
            CancellationToken cancellationToken)
        {
            Notifications.Add(new Notification(title, content, kind));
            return Task.CompletedTask;
        }
    }

    private sealed record Notification(
        string Title,
        string Content,
        XsOverlayNotificationKind Kind);
}
