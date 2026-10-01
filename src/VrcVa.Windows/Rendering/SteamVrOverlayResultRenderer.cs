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
    private readonly Func<Guid, bool>? _canRender;
    private Guid _displayedOperationId;
    private Guid _displayedSessionId;
    private readonly ExecutionCoordinator? _execution;

    public SteamVrOverlayResultRenderer(
        Dispatcher dispatcher,
        SteamVrResultPanel panel,
        IXsOverlayNotificationSink fallbackNotificationSink,
        IPrivacySafeLogger logger,
        Func<Guid, bool>? canRender = null,
        ExecutionCoordinator? execution = null)
    {
        _dispatcher = dispatcher;
        _panel = panel;
        _fallbackNotificationSink = fallbackNotificationSink;
        _logger = logger;
        _canRender = canRender;
        _execution = execution;
        _panel.DisplayFailed += Panel_DisplayFailed;
    }

    public async Task RenderProgressAsync(
        ScanProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.Stage == ScanStage.Trigger || progress.Stage == ScanStage.Capture)
        {
            await InvokeAsync(() =>
            {
                if (CanRender(progress.CorrelationId, cancellationToken)) { _panel.Hide(); }
            }, DispatcherPriority.Normal);
            return;
        }

        if (progress.Stage == ScanStage.Ocr)
        {
            await InvokeAsync(
                () =>
                {
                    if (CanRender(progress.CorrelationId, cancellationToken))
                    {
                        _ = _panel.TryShowStatus(ResultPanelTexture.ProcessingCell);
                    }
                },
                DispatcherPriority.Normal);
        }
    }

    public async Task RenderOutcomeAsync(
        ScanOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (!outcome.IsSuccess || outcome.Result is null)
        {
            await InvokeAsync(
                () =>
                {
                    if (CanRender(outcome.CorrelationId, cancellationToken))
                    {
                        _panel.Hide();
                        _panel.ReturnToLauncher();
                    }
                },
                DispatcherPriority.Normal);
            return;
        }

        ResultSection primary = outcome.Result.PrimarySection;
        string title = $"{primary.Title} — {CaptureSourceDisplayName.Get(outcome.Result.CaptureSourceKind)}";
        string body = primary.Text;

        try
        {
            bool? displayed = await InvokeAsync(
                () =>
                {
                    if (!CanRender(outcome.CorrelationId, cancellationToken)) { return (bool?)null; }
                    _displayedOperationId = outcome.CorrelationId;
                    _displayedSessionId = _execution?.CurrentSessionId ?? Guid.Empty;
                    return _panel.TryShow(title, body);
                },
                DispatcherPriority.Normal);
            if (displayed is null) { return; }
            if (displayed.Value)
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

        await InvokeAsync(
            () =>
            {
                if (CanRender(outcome.CorrelationId, cancellationToken)) { _panel.ReturnToLauncher(); }
            },
            DispatcherPriority.Normal);
        await ReportFallbackOnceAsync(outcome.CorrelationId, cancellationToken);
    }

    private Task InvokeAsync(Action action, DispatcherPriority priority)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, priority).Task;
    }

    private Task<T> InvokeAsync<T>(Func<T> action, DispatcherPriority priority)
    {
        if (_dispatcher.CheckAccess()) { return Task.FromResult(action()); }
        return _dispatcher.InvokeAsync(action, priority).Task;
    }

    private bool CanRender(Guid operationId, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && _canRender?.Invoke(operationId) != false;

    private async Task ReportFallbackOnceAsync(
        Guid operationId,
        CancellationToken cancellationToken,
        bool displayedSession = false)
    {
        bool current = displayedSession && _execution is not null
            ? _execution.IsSessionCurrent(_displayedSessionId)
            : CanRender(operationId, cancellationToken);
        if (_fallbackReported || !current || cancellationToken.IsCancellationRequested)
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
            await ReportFallbackOnceAsync(_displayedOperationId, CancellationToken.None, displayedSession: true);
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
