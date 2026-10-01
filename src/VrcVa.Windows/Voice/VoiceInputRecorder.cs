using VrcVa.Core;

namespace VrcVa.Windows.Voice;

internal enum VoiceRecordingState { Opening, Recording, Stopping, Processing, Completed, Failed, Cancelled }
internal sealed record VoiceRecordingOutcome(VoiceRecordingState State, VoiceInputFailureCode? Failure = null);
internal delegate Task VoiceInputContinuation(VoiceAudioLease audio, ExecutionOperation operation);

/// <summary>
/// Unconnected Windows adapter. J3 supplies the explicit UI/key snapshot and continuation.
/// The shared gate covers opening, recording, processing, AND awaited native/continuation cleanup.
/// </summary>
internal sealed class VoiceInputRecorder(
    ExecutionCoordinator execution,
    IMicrophoneFactory microphoneFactory,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private VoiceRecordingSession? _current;
    private bool _shutdown;

    public bool TryStart(
        VoiceInputOptions options,
        bool hasVoiceApiKey,
        VoiceInputContinuation continuation,
        out VoiceRecordingSession? session,
        out VoiceInputFailureCode? failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(continuation);
        options.Validate();
        session = null;
        failure = null;
        // Availability is a non-secret snapshot. No credential value reaches this adapter.
        if (!options.IsEnabled) { failure = VoiceInputFailureCode.Disabled; return false; }
        if (!hasVoiceApiKey) { failure = VoiceInputFailureCode.VoiceKeyUnavailable; return false; }
        lock (_sync)
        {
            if (_shutdown || !execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation, cancellationToken))
            {
                failure = VoiceInputFailureCode.Busy;
                return false;
            }

            // Only accepted new input replaces the old session; Busy preserves it.
            _current?.DiscardAudio();
            try
            {
                session = new VoiceRecordingSession(execution, operation, microphoneFactory, options, _time, continuation);
            }
            catch
            {
                operation.Dispose();
                throw;
            }

            _current = session;
            return true;
        }
    }

    public async Task CloseAsync()
    {
        VoiceRecordingSession? current;
        lock (_sync)
        {
            current = _current;
            if (current is not null) { execution.CloseSession(current.SessionId); }
        }
        if (current is not null) { await current.DisposeAsync(); }
    }

    public Task OnSteamVrLostAsync() => CloseAsync();

    public async ValueTask DisposeAsync()
    {
        VoiceRecordingSession? current;
        lock (_sync)
        {
            _shutdown = true;
            execution.Stop();
            current = _current;
        }

        if (current is not null) { await current.DisposeAsync(); }
    }
}

internal sealed class VoiceRecordingSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ExecutionCoordinator _execution;
    private readonly ExecutionOperation _operation;
    private readonly VoiceInputOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly VoiceAudioBuffer _buffer;
    private ITimer? _timer;
    private long? _startedAt;
    private VoiceRecordingState _state = VoiceRecordingState.Opening;
    private VoiceAudioSession? _audio;
    private VoiceInputFailureCode? _sampleFailure;
    private bool _disposed;

    internal VoiceRecordingSession(
        ExecutionCoordinator execution,
        ExecutionOperation operation,
        IMicrophoneFactory factory,
        VoiceInputOptions options,
        TimeProvider time,
        VoiceInputContinuation continuation)
    {
        _execution = execution;
        _operation = operation;
        _options = options;
        _time = time;
        _buffer = new VoiceAudioBuffer(options.MaximumRecordingSeconds);
        Completion = RunAsync(factory, continuation);
    }

    public Guid SessionId => _operation.SessionId;
    public Task<VoiceRecordingOutcome> Completion { get; }
    public VoiceAudioSession? Audio { get { lock (_sync) { return _audio; } } }
    public VoiceRecordingState State { get { lock (_sync) { return _state; } } }
    public int RemainingSeconds
    {
        get
        {
            lock (_sync)
            {
                double elapsed = _startedAt is long startedAt ? _time.GetElapsedTime(startedAt).TotalSeconds : 0;
                return Math.Clamp((int)Math.Ceiling(_options.MaximumRecordingSeconds - elapsed), 0, _options.MaximumRecordingSeconds);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_state is not (VoiceRecordingState.Opening or VoiceRecordingState.Recording or VoiceRecordingState.Stopping)) { return; }
            _state = VoiceRecordingState.Stopping;
            try { _stop.Cancel(); }
            catch (AggregateException) { _sampleFailure = VoiceInputFailureCode.RecordingFailed; }
        }
    }

    public void Cancel()
    {
        if (!_execution.CancelOperation(_operation)) { DiscardAudio(); }
    }

    private void ReceiveSamples(ReadOnlyMemory<byte> pcm)
    {
        lock (_sync)
        {
            if (!_operation.IsCurrent || _sampleFailure is not null
                || _state is not (VoiceRecordingState.Recording or VoiceRecordingState.Stopping)) { return; }
            try
            {
                if (_buffer.Append(pcm.Span)) { Stop(); }
            }
            catch (VoiceInputException exception)
            {
                _sampleFailure = exception.Code;
                Stop();
            }
        }
    }

    private async Task<VoiceRecordingOutcome> RunAsync(IMicrophoneFactory factory, VoiceInputContinuation continuation)
    {
        await Task.Yield(); // Publish the session before any adapter or continuation can finish inline.
        VoiceRecordingOutcome outcome = new(VoiceRecordingState.Failed, VoiceInputFailureCode.RecordingFailed);
        IMicrophone? microphone = null;
        bool processing = false;
        bool cleanupUnconfirmed = false;
        using CancellationTokenRegistration cancelled = _operation.CancellationToken.Register(() =>
        {
            Stop();
            DiscardAudio();
        });
        try
        {
            using CancellationTokenSource opening = CancellationTokenSource.CreateLinkedTokenSource(_operation.CancellationToken, _stop.Token);
            _operation.ThrowIfNotCurrent();
            opening.Token.ThrowIfCancellationRequested();
            microphone = await factory.OpenAsync(opening.Token);
            _operation.ThrowIfNotCurrent();
            opening.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _startedAt = _time.GetTimestamp();
                if (!_stop.IsCancellationRequested) { _state = VoiceRecordingState.Recording; }
                _timer = _time.CreateTimer(_ => Stop(), null,
                    TimeSpan.FromSeconds(_options.MaximumRecordingSeconds), Timeout.InfiniteTimeSpan);
            }

            await microphone.RecordAsync(ReceiveSamples, _stop.Token);
            // A stopped recording is never handed onward before native cleanup finishes.
            await microphone.DisposeAsync();
            microphone = null;
            _operation.ThrowIfNotCurrent();
            lock (_sync)
            {
                if (_sampleFailure is VoiceInputFailureCode sampleFailure) { throw new VoiceInputException(sampleFailure); }
                _buffer.Seal();
                _timer?.Dispose();
                _timer = null;
                _audio = new VoiceAudioSession(SessionId, _buffer, _options, _time);
                _state = VoiceRecordingState.Processing;
            }

            _operation.ThrowIfNotCurrent();
            if (!_audio.TryAcquire(_execution, _operation, out VoiceAudioLease? lease) || lease is null)
            {
                throw new OperationCanceledException(_operation.CancellationToken);
            }

            using (lease)
            {
                processing = true;
                _operation.ThrowIfNotCurrent();
                await continuation(lease, _operation);
                _operation.ThrowIfNotCurrent();
            }

            DiscardAudio();
            outcome = new(VoiceRecordingState.Completed);
        }
        catch (OperationCanceledException) when (!_operation.IsCurrent)
        {
            DiscardAudio();
            outcome = new(VoiceRecordingState.Cancelled);
        }
        catch (Exception exception)
        {
            if (exception is VoiceInputException { Code: VoiceInputFailureCode.MicrophoneCleanupFailed })
            {
                cleanupUnconfirmed = true;
                DiscardAudio();
                _execution.Stop();
                outcome = new(VoiceRecordingState.Failed, VoiceInputFailureCode.MicrophoneCleanupFailed);
            }
            else if (!_operation.IsCurrent)
            {
                DiscardAudio();
                outcome = new(VoiceRecordingState.Cancelled);
            }
            else
            {
                VoiceInputFailureCode code = exception is VoiceInputException typed ? typed.Code
                    : processing ? VoiceInputFailureCode.InputProcessingFailed
                    : exception is OperationCanceledException ? VoiceInputFailureCode.EmptyAudio
                    : VoiceInputFailureCode.RecordingFailed;
                if (processing) { _audio?.MarkTransmissionFailed(); }
                else { DiscardAudio(); }
                outcome = new(VoiceRecordingState.Failed, code);
            }
        }
        finally
        {
            try
            {
                if (microphone is not null) { await microphone.DisposeAsync(); }
            }
            catch
            {
                cleanupUnconfirmed = true;
                DiscardAudio();
                _execution.Stop();
                outcome = new(VoiceRecordingState.Failed, VoiceInputFailureCode.MicrophoneCleanupFailed);
            }
            finally
            {
                lock (_sync) { _timer?.Dispose(); _timer = null; }
                if (!cleanupUnconfirmed && !_operation.IsCurrent)
                {
                    DiscardAudio();
                    outcome = new(VoiceRecordingState.Cancelled);
                }

                _operation.Dispose();
            }
        }

        lock (_sync) { _state = outcome.State; }
        return outcome;
    }

    internal void DiscardAudio()
    {
        lock (_sync)
        {
            if (_audio is not null) { _audio.Dispose(); }
            else { _buffer.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        try { await Completion; }
        finally
        {
            DiscardAudio();
            lock (_sync)
            {
                if (!_disposed) { _disposed = true; _stop.Dispose(); }
            }
        }
    }
}
