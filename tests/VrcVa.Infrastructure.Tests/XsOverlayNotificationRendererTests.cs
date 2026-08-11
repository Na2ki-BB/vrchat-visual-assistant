using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class XsOverlayNotificationRendererTests
{
    [Fact]
    public async Task RenderProgressAsync_NotifiesOnlyWhenScanStarts()
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

        Notification notification = Assert.Single(sink.Notifications);
        Assert.Equal("SCAN開始", notification.Title);
        Assert.False(notification.IsError);
    }

    [Fact]
    public async Task RenderOutcomeAsync_UsesOcrTextWhenTranslationIsUnconfigured()
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

        Notification notification = Assert.Single(sink.Notifications);
        Assert.Equal("OCR結果（翻訳未設定）", notification.Title);
        Assert.Equal("EMERGENCY EXIT", notification.Content);
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
        Assert.True(notification.IsError);
    }

    private sealed class RecordingSink : IXsOverlayNotificationSink
    {
        public List<Notification> Notifications { get; } = [];

        public Task SendAsync(
            string title,
            string content,
            bool isError,
            CancellationToken cancellationToken)
        {
            Notifications.Add(new Notification(title, content, isError));
            return Task.CompletedTask;
        }
    }

    private sealed record Notification(string Title, string Content, bool IsError);
}
