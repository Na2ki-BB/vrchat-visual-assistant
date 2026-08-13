using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Capture;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Rendering;

internal sealed class SteamVrOverlayResultRenderer : IResultRenderer
{
    private readonly Dispatcher _dispatcher;
    private readonly SteamVrResultPanel _panel;
    private readonly IXsOverlayNotificationSink _fallbackNotificationSink;
    private readonly IPrivacySafeLogger _logger;
    private bool _fallbackReported;

    public SteamVrOverlayResultRenderer(
        Dispatcher dispatcher,
        SteamVrResultPanel panel,
        IXsOverlayNotificationSink fallbackNotificationSink,
        IPrivacySafeLogger logger)
    {
        _dispatcher = dispatcher;
        _panel = panel;
        _fallbackNotificationSink = fallbackNotificationSink;
        _logger = logger;
        _panel.DisplayFailed += Panel_DisplayFailed;
    }

    public async Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.Stage == ScanStage.Trigger || progress.Stage == ScanStage.Capture)
        {
            await _dispatcher.InvokeAsync(_panel.Hide, DispatcherPriority.Normal, cancellationToken);
            return;
        }

        if (progress.Stage == ScanStage.Ocr)
        {
            await _dispatcher.InvokeAsync(
                () => _panel.TryShowStatus(ResultPanelTexture.ProcessingCell),
                DispatcherPriority.Normal,
                cancellationToken);
        }
    }

    public async Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!outcome.IsSuccess || outcome.Result is null)
        {
            await _dispatcher.InvokeAsync(_panel.Hide, DispatcherPriority.Normal, cancellationToken);
            return;
        }

        bool ocrOnly = string.IsNullOrWhiteSpace(outcome.Result.JapaneseText);
        string resultTitle = ocrOnly ? "OCR結果（翻訳未設定）" : "日本語訳";
        string title = $"{resultTitle} — {CaptureSourceDisplayName.Get(outcome.Result.CaptureSourceKind)}";
        string body = ocrOnly
            ? outcome.Result.SourceText
            : outcome.Result.JapaneseText;

        try
        {
            bool displayed = await _dispatcher.InvokeAsync(
                () => _panel.TryShow(title, body),
                DispatcherPriority.Normal,
                cancellationToken);
            if (displayed)
            {
                _fallbackReported = false;
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error(
                "rendering.steamvr_overlay_failed",
                outcome.CorrelationId,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }

        await ReportFallbackOnceAsync(cancellationToken);
    }

    private async Task ReportFallbackOnceAsync(CancellationToken cancellationToken)
    {
        if (_fallbackReported)
        {
            return;
        }

        _fallbackReported = true;
        try
        {
            await _fallbackNotificationSink.SendAsync(
                "VR結果パネルを表示できません",
                "SteamVRを確認してください。結果はPC側の画面に残っています。",
                XsOverlayNotificationKind.Error,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error(
                "rendering.steamvr_overlay_fallback_notification_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private async void Panel_DisplayFailed(object? sender, EventArgs eventArgs)
    {
        try
        {
            await ReportFallbackOnceAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "rendering.steamvr_overlay_async_failure_notification_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }
    }
}
