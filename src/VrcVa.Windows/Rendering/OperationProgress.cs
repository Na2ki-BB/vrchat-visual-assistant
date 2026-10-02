using VrcVa.Core;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Rendering;

internal enum OperationProgressState { Recording, Transcribing, Processing, Cancelling, Failed, Cancelled }
internal enum OperationProgressAction { None, Stop, Cancel, Retry, AdjustPlacement, Close }

internal sealed record OperationProgressSnapshot(
    Guid SessionId,
    Guid OperationId,
    OperationProgressState State,
    string Title,
    string Message,
    string Detail,
    bool CanStop = false,
    bool CanCancel = false,
    bool CanRetry = false,
    bool CanClose = false,
    string RetryLabel = "再試行",
    bool CanAdjustPlacement = false,
    bool UsesVoicePlacement = false)
{
    public bool Allows(OperationProgressAction action) => action switch
    {
        OperationProgressAction.Stop => CanStop,
        OperationProgressAction.Cancel => CanCancel,
        OperationProgressAction.Retry => CanRetry,
        OperationProgressAction.AdjustPlacement => CanAdjustPlacement,
        OperationProgressAction.Close => CanClose,
        _ => false,
    };
}

internal interface IOperationProgressView
{
    event EventHandler<OperationProgressActionEventArgs>? ProgressActionRequested;
    bool TryShowProgress(OperationProgressSnapshot snapshot);
    bool TryShowVoicePlacementCalibration(OperationProgressSnapshot snapshot) => false;
    void Hide();
    void ReturnToLauncher();
    void DismissProgress() { Hide(); ReturnToLauncher(); }
}

internal sealed class OperationProgressActionEventArgs(
    OperationProgressSnapshot snapshot, OperationProgressAction action) : EventArgs
{
    public OperationProgressSnapshot Snapshot { get; } = snapshot;
    public OperationProgressAction Action { get; } = action;
}

/// <summary>Dispatcher-owned presentation. Actions revalidate the live owner before touching shared execution.</summary>
internal sealed class OperationProgressController : IDisposable
{
    private readonly ExecutionCoordinator _execution;
    private readonly IOperationProgressView _view;
    private VoiceInputFlow? _voice;
    private ExecutionOperation? _scan;
    private Func<Task>? _retryScan;
    private ScanFailure? _scanFailure;
    private ScanProgress? _scanProgress;
    private bool _scanComplete;
    private bool _voiceView;
    private bool _disposed;
    private bool _suspended;
    private OperationProgressSnapshot? _shown;

    public OperationProgressController(ExecutionCoordinator execution, IOperationProgressView view)
    {
        _execution = execution;
        _view = view;
        _view.ProgressActionRequested += OnAction;
    }

    public void AttachVoice(VoiceInputFlow voice)
    {
        _voice = voice;
        voice.Changed += VoiceChanged;
    }

    public void BeginScan(ExecutionOperation operation, Func<Task> retry)
    {
        _scan = operation;
        _retryScan = retry;
        _scanFailure = null;
        _scanProgress = null;
        _scanComplete = false;
        _voiceView = false;
        _suspended = false;
        _shown = null;
    }

    public void RenderProgress(ScanProgress progress)
    {
        if (_scan?.OperationId != progress.CorrelationId || !_scan.IsCurrent) { return; }
        _scanProgress = progress;
        // Capture owns hide/boundary/discard/adopt. Keep the WPF cancel path alive
        // while no VR surface may be visible, including an in-flight texture upload.
        if (progress.Stage is ScanStage.Trigger or ScanStage.Capture)
        {
            _suspended = true;
            _shown = null;
            _view.Hide();
            return;
        }
        _suspended = false;
        Refresh();
    }

    public void ShowScanNotice(string message)
    {
        if (_scan is null || !_scan.IsCurrent) { return; }
        _scanProgress = new(_scan.OperationId, ScanStage.Trigger, message, TimeSpan.Zero);
        _suspended = false;
        Refresh();
    }

    public void RenderOutcome(ScanOutcome outcome)
    {
        if (_scan?.OperationId != outcome.CorrelationId || !_scan.IsCurrent) { return; }
        _scanComplete = true;
        _scanFailure = outcome.Failure;
        _suspended = false;
        if (outcome.IsSuccess)
        {
            // Keep ownership until the caller has drained every renderer and
            // upload scheduling. Cancel must still replace a pending result.
            _shown = null;
            return;
        }
        Refresh();
    }

    public void CompleteScan(ExecutionOperation operation)
    {
        if (!ReferenceEquals(_scan, operation)) { return; }
        if (_scanComplete && _scanFailure is null && operation.IsCurrent)
        {
            _scan = null;
            _retryScan = null;
            _shown = null;
            return;
        }
        _suspended = false;
        Refresh();
    }

    public bool CancelActive()
    {
        if (_disposed) { return false; }
        if (_voice?.IsCurrent == true && _voice.State is VoiceFlowState.Recording or VoiceFlowState.Transcribing or VoiceFlowState.Failed)
        {
            _voice.Cancel();
            Refresh();
            return true;
        }
        if (_scan is not null && _execution.IsActive(_scan))
        {
            _execution.CancelOperation(_scan);
            // Do not reveal the overlay inside an outstanding capture boundary.
            Refresh();
            return true;
        }
        return false;
    }

    public async Task ConnectionLostAsync()
    {
        _suspended = true;
        CancelActive();
        _shown = null;
        _scan = null;
        _retryScan = null;
        _voiceView = false;
        if (_voice is not null) { await _voice.CloseAsync(); }
    }

    public void Refresh()
    {
        if (_disposed) { return; }
        OperationProgressSnapshot? next = CreateSnapshot();
        if (next is null || _suspended) { return; }
        if (next == _shown) { return; }
        _shown = next;
        // A missing runtime is a desktop fallback, not an invitation to start SteamVR.
        try { _view.TryShowProgress(next); }
        catch (Exception) { /* The WPF flow remains usable after a display failure. */ }
    }

    private OperationProgressSnapshot? CreateSnapshot()
    {
        if (_scan is not null)
        {
            if (!_execution.IsSessionCurrent(_scan.SessionId)) { return null; }
            bool active = _execution.IsActive(_scan);
            if (!_scan.IsCurrent)
            {
                return new(_scan.SessionId, _scan.OperationId,
                    active ? OperationProgressState.Cancelling : OperationProgressState.Cancelled,
                    active ? "中止中" : "中止しました",
                    active ? "処理とリソースの回収を待っています。" : "処理とリソースを回収しました。",
                    "送信済みの要求の中止は、課金取消を保証しません。", CanClose: !active);
            }
            if (_scanFailure is { } failure)
            {
                return new(_scan.SessionId, _scan.OperationId, OperationProgressState.Failed,
                    "SCANに失敗しました", failure.Message,
                    $"段階: {failure.Stage} / 失敗: {failure.Code}\nやり直すと取得・認識・設定時の翻訳を再実行します。",
                    CanRetry: !active && CanRetryScan(failure.Code), CanClose: !active,
                    RetryLabel: "再SCAN");
            }
            if (!_scanComplete && active)
            {
                return new(_scan.SessionId, _scan.OperationId, OperationProgressState.Processing,
                    "SCAN処理中", _scanProgress?.Message ?? "処理を開始しています。",
                    $"段階: {_scanProgress?.Stage ?? ScanStage.Trigger}", CanCancel: true);
            }
            return null;
        }
        if (!_voiceView || _voice is null) { return null; }
        VoiceInputFlow voice = _voice;
        if (!voice.IsCurrent && !(voice.State == VoiceFlowState.Failed
            && (voice.SessionId == Guid.Empty || voice.RequiresRestart))) { return null; }
        return voice.State switch
        {
            VoiceFlowState.Recording => new(voice.SessionId, _execution.CurrentOperationId,
                OperationProgressState.Recording, voice.CanStop ? $"録音中 / 残り {voice.RemainingSeconds} 秒" : "録音を停止中",
                voice.Message, "停止するとOpenAIへ音声を送信し、文字起こしします。\n位置調整は録音停止後に利用できます。",
                CanStop: voice.CanStop, CanCancel: voice.IsCurrent,
                UsesVoicePlacement: true),
            VoiceFlowState.Transcribing => new(voice.SessionId, _execution.CurrentOperationId,
                OperationProgressState.Transcribing, "文字起こし中", voice.Message,
                "中止しても、送信済みの音声の利用料金は取り消されない場合があります。\n位置調整は処理完了後に利用できます。",
                CanCancel: voice.IsCurrent, UsesVoicePlacement: true),
            VoiceFlowState.Cancelling => new(voice.SessionId, Guid.Empty, OperationProgressState.Cancelling,
                "中止中", voice.Message, "音声と処理の回収後に次の操作が可能になります。",
                UsesVoicePlacement: true),
            VoiceFlowState.Cancelled => new(voice.SessionId, Guid.Empty, OperationProgressState.Cancelled,
                "中止しました", voice.Message, "音声は破棄済みです。", CanClose: !voice.IsBusy,
                CanAdjustPlacement: !voice.IsBusy && voice.SessionId != Guid.Empty,
                UsesVoicePlacement: true),
            VoiceFlowState.Failed => new(voice.SessionId, _execution.CurrentOperationId, OperationProgressState.Failed,
                "音声入力に失敗しました", voice.Message, $"失敗: {voice.FailureCode ?? "ConfigurationUnavailable"}\n再送は音声の秒数・回数枠を消費します。",
                CanRetry: voice.CanRetry || voice.CanRestartRecording, CanClose: !voice.IsBusy,
                RetryLabel: voice.CanRestartRecording ? "録り直す" : "音声を再送",
                CanAdjustPlacement: !voice.IsBusy && voice.SessionId != Guid.Empty,
                UsesVoicePlacement: true),
            _ => null,
        };
    }

    private void VoiceChanged(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _voice is null) { return; }
        if (_voice.State is VoiceFlowState.Ready or VoiceFlowState.Completed or VoiceFlowState.Closed)
        {
            if (_voiceView)
            {
                _voiceView = false;
                _shown = null;
                HideView();
            }
            return;
        }
        if (!_voice.IsCurrent && _voice.State != VoiceFlowState.Failed) { return; }
        // A delayed voice refresh cannot replace a newer scan/search session.
        if (_scan is not null && _execution.IsSessionCurrent(_scan.SessionId)) { return; }
        if (_voice.SessionId != Guid.Empty && !_voice.IsCurrent && !_voice.RequiresRestart) { return; }
        _scan = null;
        if (!_voiceView) { _suspended = false; }
        _voiceView = true;
        Refresh();
    }

    private async void OnAction(object? sender, OperationProgressActionEventArgs eventArgs)
    {
        if (_disposed || eventArgs.Snapshot != _shown || !_shown.Allows(eventArgs.Action)) { return; }
        OperationProgressSnapshot? live = CreateSnapshot();
        if (live is null || live != _shown || !live.Allows(eventArgs.Action)) { return; }
        switch (eventArgs.Action)
        {
            case OperationProgressAction.Stop: _voice?.StopRecording(); break;
            case OperationProgressAction.Cancel: CancelActive(); break;
            case OperationProgressAction.Retry:
                if (_voiceView && _voice is not null)
                {
                    if (_voice.CanRestartRecording) { _voice.TryStart(); }
                    else { _voice.TryRetry(); }
                }
                else if (_retryScan is { } retry && !_execution.IsRunning)
                {
                    _shown = null;
                    try { await retry(); }
                    catch (Exception)
                    {
                        if (_scan is not null && _execution.IsSessionCurrent(_scan.SessionId))
                        {
                            _scanFailure = new(ScanFailureCode.Unexpected, ScanStage.Trigger,
                                "SCANを再開できませんでした。PC側の状態を確認してください。");
                            _scanComplete = true;
                            _suspended = false;
                        }
                    }
                }
                break;
            case OperationProgressAction.AdjustPlacement:
                _view.TryShowVoicePlacementCalibration(_shown);
                break;
            case OperationProgressAction.Close:
                _shown = null;
                HideView();
                if (_voiceView)
                {
                    _voiceView = false;
                    if (_voice is not null) { await _voice.CloseAsync(); }
                }
                else if (_scan is not null)
                {
                    _execution.CloseSession(_scan.SessionId);
                    _scan = null;
                    _retryScan = null;
                }
                break;
        }
        Refresh();
    }

    private void HideView()
    {
        try { _view.DismissProgress(); }
        catch (Exception) { /* Display failure cannot change audio/execution ownership. */ }
    }

    internal static bool CanRetryScan(ScanFailureCode failure) => failure is
        ScanFailureCode.CaptureTargetNotFound or ScanFailureCode.CaptureUnavailable
        or ScanFailureCode.NoTextDetected or ScanFailureCode.TranslationRateLimited
        or ScanFailureCode.TranslationTimedOut or ScanFailureCode.TranslationFailed;

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _view.ProgressActionRequested -= OnAction;
        if (_voice is not null) { _voice.Changed -= VoiceChanged; }
        _shown = null;
        _scan = null;
        _retryScan = null;
        _view.Hide();
    }
}
