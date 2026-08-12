using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Rendering;

internal sealed class SteamVrOverlayResultRenderer(
    Dispatcher dispatcher,
    SteamVrResultPanel panel,
    IXsOverlayNotificationSink fallbackNotificationSink,
    IPrivacySafeLogger logger) : IResultRenderer
{
    private bool _fallbackReported;

    public async Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.Stage != ScanStage.Trigger)
        {
            return;
        }

        await dispatcher.InvokeAsync(panel.Hide, DispatcherPriority.Normal, cancellationToken);
    }

    public async Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!outcome.IsSuccess || outcome.Result is null)
        {
            await dispatcher.InvokeAsync(panel.Hide, DispatcherPriority.Normal, cancellationToken);
            return;
        }

        bool ocrOnly = string.IsNullOrWhiteSpace(outcome.Result.JapaneseText);
        string title = ocrOnly ? "OCR結果（翻訳未設定）" : "日本語訳";
        string body = ocrOnly
            ? outcome.Result.SourceText
            : outcome.Result.JapaneseText;

        try
        {
            bool displayed = await dispatcher.InvokeAsync(
                () => panel.TryShow(title, body),
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
            logger.Error(
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
            await fallbackNotificationSink.SendAsync(
                "VR結果パネルを表示できません",
                "SteamVRを確認してください。結果はPC側の画面に残っています。",
                XsOverlayNotificationKind.Error,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.Error(
                "rendering.steamvr_overlay_fallback_notification_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }
    }
}
