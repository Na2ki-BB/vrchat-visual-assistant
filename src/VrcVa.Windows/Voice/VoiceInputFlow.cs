using VrcVa.Core;

namespace VrcVa.Windows.Voice;

internal enum VoiceFlowState { Ready, Recording, Transcribing, Completed, Failed, Cancelling, Cancelled, Closed }

/// <summary>
/// WPF-thread-owned shared input flow. Recorder and retry leases own cleanup; the app owns the gate and quotas.
/// No logging/persistence dependencies and no feature is automatically run after transcription.
/// </summary>
internal sealed class VoiceInputFlow : IAsyncDisposable
{
    private readonly ExecutionCoordinator _execution;
    private readonly VoiceInputRecorder _recorder;
    private readonly Func<VoiceInputRuntime> _createRuntime;
    private VoiceRecordingSession? _recording;
    private ExecutionOperation? _retry;
    private Task _pending = Task.CompletedTask;
    private bool _disposed;
    private TextInputSession? _text;
    private Guid _sessionId;
    private ScanFailureCode? _transcriptionFailure;
    private VoiceInputFailureCode? _recordingFailure;

    public VoiceInputFlow(ExecutionCoordinator execution, IMicrophoneFactory microphone,
        Func<VoiceInputRuntime> createRuntime, TimeProvider? timeProvider = null)
    {
        _execution = execution;
        _recorder = new(execution, microphone, timeProvider);
        _createRuntime = createRuntime;
    }

    public event EventHandler? Changed;
    public VoiceFlowState State { get; private set; }
    public string Message { get; private set; } = "音声入力を明示的に有効化し、専用キーを保存してから録音してください。";
    public Task WhenIdle => _pending;
    public Guid SessionId => _sessionId;
    public bool IsCurrent => _execution.IsSessionCurrent(_sessionId);
    public bool IsBusy => _execution.IsRunning;
    public bool RequiresRestart => _recordingFailure == VoiceInputFailureCode.MicrophoneCleanupFailed;
    public int RemainingSeconds => _recording?.RemainingSeconds ?? 0;
    public bool CanStop => IsCurrent && _retry is null && _recording?.State is
        VoiceRecordingState.Opening or VoiceRecordingState.Recording;
    public bool CanRetry => IsCurrent && !_execution.IsRunning && State == VoiceFlowState.Failed
        && _recording?.Audio?.IsAvailable == true
        && _transcriptionFailure is not (ScanFailureCode.VoiceAuthenticationFailed
            or ScanFailureCode.VoiceUsageLimitReached or ScanFailureCode.VoiceTranscriptionNotConfigured
            or ScanFailureCode.VoiceInputDisabled or ScanFailureCode.VoiceAudioInvalid or ScanFailureCode.VoiceAudioTooLarge);
    internal bool CanRestartRecording => State == VoiceFlowState.Failed && !_execution.IsRunning
        && !RequiresRestart && _recordingFailure is VoiceInputFailureCode.EmptyAudio
            or VoiceInputFailureCode.SilentAudio or VoiceInputFailureCode.DeviceLost
            or VoiceInputFailureCode.RecordingFailed;
    public TextInputSession? CurrentInput => IsCurrent ? _text : null;
    public string? FailureCode => _transcriptionFailure?.ToString() ?? _recordingFailure?.ToString();

    public bool TryStart()
    {
        if (_disposed || _execution.IsRunning) { return false; }
        VoiceInputRuntime? runtime = CreateRuntime();
        if (runtime is null) { return false; }
        if (!_recorder.TryStart(runtime.Options, true,
            (audio, operation) => TranscribeAsync(audio, operation, runtime),
            out VoiceRecordingSession? recording, out VoiceInputFailureCode? failure))
        {
            runtime.Dispose();
            if (failure != VoiceInputFailureCode.Busy) { FailRecording(failure); }
            return false;
        }

        _recording = recording!;
        _sessionId = recording!.SessionId;
        _text = null;
        _transcriptionFailure = null;
        _recordingFailure = null;
        SetState(VoiceFlowState.Recording, "マイクを準備しています。再押しで停止し、文字起こしへ進みます。");
        _pending = ObserveRecordingAsync(recording, runtime);
        return true;
    }

    private VoiceInputRuntime? CreateRuntime()
    {
        if (!_execution.TryBeginConfiguration(out ExecutionOperation? configuration)) { return null; }
        using (configuration)
        {
            try { return _createRuntime(); }
            catch (VoiceInputException exception) { FailRecording(exception.Code); }
            catch (Exception) { SetState(VoiceFlowState.Failed, "音声設定または専用キーを読み込めませんでした。設定を確認してください。"); }
        }
        return null;
    }

    private async Task ObserveRecordingAsync(VoiceRecordingSession recording, VoiceInputRuntime runtime)
    {
        try
        {
            VoiceRecordingOutcome outcome = await recording.Completion;
            if (!ReferenceEquals(_recording, recording)) { return; }
            // Native cleanup failure deliberately stops the coordinator, so IsCurrent is
            // false. Still surface this owned terminal safety failure without any text.
            if (outcome.Failure == VoiceInputFailureCode.MicrophoneCleanupFailed)
            {
                _text = null;
                FailRecording(outcome.Failure);
                return;
            }
            if (!IsCurrent) { return; }
            if (outcome.State == VoiceRecordingState.Completed)
            {
                SetState(VoiceFlowState.Completed, "文字起こしが完了しました。音声を破棄しました。");
            }
            else if (outcome.State == VoiceRecordingState.Cancelled)
            {
                SetState(VoiceFlowState.Cancelled, "中止しました。音声を破棄しました。");
            }
            else if (_transcriptionFailure is null) { FailRecording(outcome.Failure); }
        }
        finally { runtime.Dispose(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    private async Task TranscribeAsync(VoiceAudioLease audio, ExecutionOperation operation, VoiceInputRuntime runtime)
    {
        try
        {
            operation.ThrowIfNotCurrent();
            SetState(VoiceFlowState.Transcribing, "OpenAIへ音声を送信し、文字起こししています。");
            VoiceTranscription result = await runtime.Transcriber.TranscribeAsync(audio.WaveBytes, operation.CancellationToken);
            operation.ThrowIfNotCurrent();
            TextInputSession input;
            try { input = new(operation.SessionId, result.Text); }
            catch (ArgumentException) { throw new ScanException(ScanFailureCode.VoiceTranscriptionInvalidResponse, ScanStage.Transcription, "Invalid transcript."); }
            operation.ThrowIfNotCurrent();
            _text = input;
        }
        catch (OperationCanceledException) when (!operation.IsCurrent) { throw; }
        catch (Exception exception)
        {
            if (operation.IsCurrent)
            {
                _transcriptionFailure = exception is ScanException typed
                    ? typed.FailureCode : ScanFailureCode.VoiceTranscriptionFailed;
                SetState(VoiceFlowState.Failed, TranscriptionMessage(_transcriptionFailure.Value));
            }
            throw;
        }
        finally { runtime.Dispose(); }
    }

    public void StopRecording() { if (CanStop) { _recording!.Stop(); Changed?.Invoke(this, EventArgs.Empty); } }

    public bool TryRetry()
    {
        if (_disposed || !CanRetry) { return false; }
        VoiceInputRuntime? runtime = CreateRuntime();
        if (runtime is null) { return false; }
        if (!_execution.TryBeginOperation(_sessionId, Guid.NewGuid(), out ExecutionOperation? operation))
        {
            runtime.Dispose();
            return false;
        }
        VoiceAudioSession? audio = _recording?.Audio;
        if (audio is null || !audio.TryAcquire(_execution, operation, out VoiceAudioLease? lease) || lease is null)
        {
            runtime.Dispose();
            operation.Dispose();
            SetState(VoiceFlowState.Failed, "失敗音声の保持期限が切れました。録り直してください。");
            return false;
        }
        _retry = operation;
        _transcriptionFailure = null;
        _pending = RetryAsync(audio, lease, operation, runtime);
        return true;
    }

    private async Task RetryAsync(VoiceAudioSession audio, VoiceAudioLease lease, ExecutionOperation operation, VoiceInputRuntime runtime)
    {
        // Publish _pending before a fake/fast adapter can complete inline.
        await Task.Yield();
        using CancellationTokenRegistration cancelled = operation.CancellationToken.Register(audio.Dispose);
        bool succeeded = false;
        bool wasCancelled = false;
        try
        {
            await TranscribeAsync(lease, operation, runtime);
            operation.ThrowIfNotCurrent();
            audio.Dispose();
            succeeded = true;
        }
        catch (OperationCanceledException) when (!operation.IsCurrent)
        {
            audio.Dispose();
            wasCancelled = true;
        }
        catch (Exception)
        {
            if (operation.IsCurrent) { audio.MarkTransmissionFailed(); }
            else { audio.Dispose(); wasCancelled = true; }
        }
        finally
        {
            runtime.Dispose();
            lease.Dispose();
            operation.Dispose();
            _retry = null;
            // Audio lease and runtime are gone before successful text becomes visible.
            if (IsCurrent && succeeded) { SetState(VoiceFlowState.Completed, "文字起こしが完了しました。音声を破棄しました。"); }
            else if (IsCurrent && wasCancelled) { SetState(VoiceFlowState.Cancelled, "中止しました。音声を破棄しました。"); }
            else { Changed?.Invoke(this, EventArgs.Empty); }
        }
    }

    public void Cancel()
    {
        if (!IsCurrent) { return; }
        _text = null;
        if (_retry is not null) { _execution.CancelOperation(_retry); }
        else { _recording?.Cancel(); }
        _recording?.DiscardAudio();
        SetState(_execution.IsRunning ? VoiceFlowState.Cancelling : VoiceFlowState.Cancelled,
            _execution.IsRunning ? "中止中です。処理と音声の回収を待っています。" : "中止しました。音声を破棄しました。");
    }

    public async Task CloseAsync()
    {
        _execution.CloseSession(_sessionId);
        _text = null;
        _sessionId = Guid.Empty;
        SetState(VoiceFlowState.Closed, "音声入力を閉じ、内容を破棄しました。");
        await _recorder.CloseAsync();
        await _pending;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh()
    {
        if (_sessionId != Guid.Empty && !IsCurrent)
        {
            _text = null;
            _recording?.DiscardAudio();
        }
        if (State == VoiceFlowState.Failed && _recording?.Audio is { IsAvailable: false }
            && !_execution.IsRunning && IsCurrent)
        {
            Message = "失敗音声は破棄済みです。録り直してください。";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void FailRecording(VoiceInputFailureCode? failure)
    {
        _recordingFailure = failure;
        _transcriptionFailure = null;
        SetState(VoiceFlowState.Failed, failure switch
        {
            VoiceInputFailureCode.Disabled => "音声入力への明示的な同意が必要です。録音・送信は行いません。",
            VoiceInputFailureCode.VoiceKeyUnavailable => "音声専用APIキーを保存してください。翻訳用キーは使用しません。",
            VoiceInputFailureCode.DeviceUnavailable => "Windowsの既定の通信入力デバイスがありません。マイク設定を確認してください。",
            VoiceInputFailureCode.MicrophoneAccessDenied => "Windowsのマイクへのアクセスが拒否されました。プライバシー設定を確認してください。",
            VoiceInputFailureCode.FormatUnsupported => "既定の通信マイクが対応する音声形式を確認できません。",
            VoiceInputFailureCode.DeviceLost => "録音中にマイクが切断されました。接続を確認して録り直してください。",
            VoiceInputFailureCode.EmptyAudio or VoiceInputFailureCode.SilentAudio => "音声が空またはほぼ無音です。OS・物理マイクのミュートを確認して録り直してください。",
            VoiceInputFailureCode.MicrophoneCleanupFailed => "マイクの解放を確認できません。安全のため処理を停止しました。アプリを終了してください。",
            _ => "録音に失敗しました。マイクを確認して録り直してください。",
        });
    }

    private static string TranscriptionMessage(ScanFailureCode failure) => failure switch
    {
        ScanFailureCode.VoiceAuthenticationFailed => "文字起こしの認証に失敗しました。音声専用キーを確認して録り直してください。",
        ScanFailureCode.VoiceUsageLimitReached => "この起動中の音声上限に達しました。設定の上限と消費量を確認してください。",
        ScanFailureCode.VoiceRateLimited => "音声サービスが混み合っているか、サービス側の利用制限です。期限内に本人の操作で再試行できます。",
        ScanFailureCode.VoiceTranscriptionTimedOut => "文字起こしが時間内に完了しませんでした。期限内の音声だけ手動で再試行できます。",
        ScanFailureCode.VoiceTranscriptionEmpty => "文字起こし結果が空でした。録り直すか、期限内の音声を手動で再試行してください。",
        ScanFailureCode.VoiceTranscriptionInvalidResponse => "文字起こしの応答が不正または長すぎます。録り直すか、期限内の音声を手動で再試行してください。",
        _ => "文字起こしに失敗しました。期限内に保持した音声だけ、本人の操作で再試行できます。",
    };

    private void SetState(VoiceFlowState state, string message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await CloseAsync();
        await _recorder.DisposeAsync();
    }
}
