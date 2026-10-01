using System.Runtime.InteropServices;
using VrcVa.Core;

namespace VrcVa.Windows.Voice;

internal sealed class WinMmMicrophoneFactory : IMicrophoneFactory
{
    private static readonly WinMmCaptureSafetyGate ProductionSafetyGate = new();
    private readonly IWinMmApi _api;
    private readonly Func<string, IRecordingEndpoint> _createEndpoint;
    private readonly WinMmCaptureSafetyGate _safetyGate;

    internal WinMmMicrophoneFactory()
        : this(new WinMmApi(), id => new CoreAudioRecordingEndpoint(id), ProductionSafetyGate)
    {
    }

    internal WinMmMicrophoneFactory(IWinMmApi api, Func<string, IRecordingEndpoint> createEndpoint,
        WinMmCaptureSafetyGate? safetyGate = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _createEndpoint = createEndpoint ?? throw new ArgumentNullException(nameof(createEndpoint));
        _safetyGate = safetyGate ?? new WinMmCaptureSafetyGate();
    }

    public async Task<IMicrophone> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _safetyGate.Acquire();
        WinMmMicrophone? microphone = null;
        try
        {
            microphone = new WinMmMicrophone(_api, _createEndpoint, _safetyGate, cancellationToken);
            await microphone.Opened.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return microphone;
        }
        catch
        {
            // Even cancellation during a native open must reclaim the worker before returning.
            if (microphone is not null)
            {
                await microphone.DisposeAsync().ConfigureAwait(false);
                if (microphone.CleanupUnconfirmed)
                {
                    // Caller cancellation must not hide a still-live native
                    // capture handle and permit the shared session gate to reopen.
                    throw new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed);
                }
            }
            else
            {
                _safetyGate.Release();
            }
            throw;
        }
    }
}

/// <summary>
/// A single recording owns one dedicated worker and three 100 ms native buffers.
/// Every WinMM operation and CoreAudio lifetime operation runs on that worker.
/// CALLBACK_EVENT does not execute application code on a driver callback thread.
/// </summary>
internal sealed class WinMmMicrophone : IMicrophone
{
    internal const int NativeBufferCount = 3;
    internal const int NativeBufferBytes = VoiceAudioFormat.BytesPerSecond / 10;
    internal const int DeviceCheckMilliseconds = 100;
    internal const int CleanupAttempts = 3;
    private static readonly uint HeaderBytes = (uint)Marshal.SizeOf<WinMmInterop.WaveHeader>();

    private readonly IWinMmApi _api;
    private readonly Func<string, IRecordingEndpoint> _createEndpoint;
    private readonly WinMmCaptureSafetyGate _safetyGate;
    private readonly CancellationToken _openToken;
    private readonly object _stateLock = new();
    private readonly AutoResetEvent _driverEvent = new(false);
    private readonly ManualResetEvent _startOrDispose = new(false);
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<NativeBuffer> _buffers = [];
    private readonly Queue<NativeBuffer> _queued = new();
    private readonly byte[] _scratch = new byte[NativeBufferBytes];
    private Action<ReadOnlyMemory<byte>>? _receiveSamples;
    private CancellationToken _stopToken;
    private bool _recordRequested;
    private bool _disposeRequested;
    private bool _cleanupUnconfirmed;
    private nint _handle;

    internal WinMmMicrophone(IWinMmApi api, Func<string, IRecordingEndpoint> createEndpoint,
        WinMmCaptureSafetyGate safetyGate, CancellationToken openToken)
    {
        _api = api;
        _createEndpoint = createEndpoint;
        _safetyGate = safetyGate;
        _openToken = openToken;
        var worker = new Thread(Run) { IsBackground = true, Name = "VrcVa microphone" };
        try
        {
            worker.Start();
        }
        catch
        {
            _driverEvent.Dispose();
            _startOrDispose.Dispose();
            _disposeStop.Dispose();
            throw;
        }
    }

    internal Task Opened => _opened.Task;
    internal bool CleanupUnconfirmed
    {
        get { lock (_stateLock) { return _cleanupUnconfirmed; } }
    }

    public Task RecordAsync(Action<ReadOnlyMemory<byte>> receiveSamples, CancellationToken stopToken)
    {
        ArgumentNullException.ThrowIfNull(receiveSamples);
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_recordRequested || !_opened.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("A microphone supports one recording after opening.");
            }

            _receiveSamples = receiveSamples;
            _stopToken = stopToken;
            _recordRequested = true;
            _startOrDispose.Set();
            return _completed.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (!_disposeRequested)
            {
                _disposeRequested = true;
                // The worker can already have completed and disposed these handles.
                if (!_completed.Task.IsCompleted)
                {
                    _disposeStop.Cancel();
                    _startOrDispose.Set();
                }
            }
        }

        // Recording/opening owns reporting its typed failure. Disposal still waits
        // for all cleanup, but does not overwrite that original failure.
        try
        {
            await _completed.Task.ConfigureAwait(false);
        }
        catch (VoiceInputException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Run()
    {
        IRecordingEndpoint? endpoint = null;
        Exception? failure = null;
        bool opened = false;
        bool safeToRelease = true;
        try
        {
            _openToken.ThrowIfCancellationRequested();
            WinMmInterop.WaveFormat format = WinMmInterop.CreateFormat();
            WinMmInterop.Check(_api.Open(out _, WinMmInterop.WaveMapper, ref format, 0,
                WinMmInterop.WaveFormatQuery | WinMmInterop.DefaultCommunicationDevice));
            _openToken.ThrowIfCancellationRequested();
            WinMmInterop.Check(_api.Open(out _handle, WinMmInterop.WaveMapper, ref format,
                (nuint)_driverEvent.SafeWaitHandle.DangerousGetHandle(),
                WinMmInterop.CallbackEvent | WinMmInterop.DefaultCommunicationDevice));
            if (_handle == 0)
            {
                throw new VoiceInputException(VoiceInputFailureCode.DeviceUnavailable);
            }

            endpoint = _createEndpoint(WinMmInterop.GetEndpointId(_api, _handle));
            EnsureEndpoint(endpoint, recording: false);
            _openToken.ThrowIfCancellationRequested();
            opened = true;
            _opened.TrySetResult();
            _startOrDispose.WaitOne();
            lock (_stateLock)
            {
                if (!_recordRequested || _disposeRequested || _stopToken.IsCancellationRequested)
                {
                    return;
                }
            }

            EnsureEndpoint(endpoint, recording: true);
            for (int index = 0; index < NativeBufferCount; index++)
            {
                var buffer = new NativeBuffer();
                _buffers.Add(buffer);
                WinMmInterop.Check(_api.Prepare(_handle, buffer.Header, HeaderBytes), recording: true);
                buffer.Prepared = true;
                QueueBuffer(buffer);
            }

            if (_stopToken.IsCancellationRequested || _disposeStop.IsCancellationRequested)
            {
                return;
            }

            WinMmInterop.Check(_api.Start(_handle), recording: true);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stopToken, _disposeStop.Token);
            WaitHandle[] waits = [_driverEvent, stop.Token.WaitHandle];
            while (!stop.IsCancellationRequested)
            {
                EnsureEndpoint(endpoint, recording: true);
                DrainCompleted(endpoint, requeue: true);
                WaitHandle.WaitAny(waits, DeviceCheckMilliseconds);
            }
        }
        catch (Exception error)
        {
            failure = !opened && error is OperationCanceledException ? error : Sanitize(error);
        }
        finally
        {
            try
            {
                safeToRelease = Cleanup(endpoint, ref failure);
            }
            catch (Exception)
            {
                // Unexpected cleanup exceptions also cannot justify freeing
                // possibly driver-owned pointers or closing its callback event.
                safeToRelease = false;
                failure = new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed);
            }

            try
            {
                endpoint?.Dispose();
            }
            catch (Exception)
            {
                failure ??= new VoiceInputException(VoiceInputFailureCode.RecordingFailed);
            }

            Array.Clear(_scratch);
            lock (_stateLock)
            {
                _receiveSamples = null;
                _cleanupUnconfirmed = !safeToRelease;
                // Complete while holding the same lock used by DisposeAsync;
                // it then cannot race a Set/Cancel against disposed wait handles.
                if (safeToRelease)
                {
                    _driverEvent.Dispose();
                    _safetyGate.Release();
                }
                else
                {
                    _safetyGate.Quarantine(this);
                }

                _startOrDispose.Dispose();
                _disposeStop.Dispose();
                if (failure is null)
                {
                    _completed.TrySetResult();
                }
                else
                {
                    _completed.TrySetException(failure);
                }

                if (!opened)
                {
                    _opened.TrySetException(failure
                        ?? new VoiceInputException(VoiceInputFailureCode.RecordingFailed));
                }
            }
        }
    }

    private void QueueBuffer(NativeBuffer buffer)
    {
        WinMmInterop.Check(_api.AddBuffer(_handle, buffer.Header, HeaderBytes), recording: true);
        _queued.Enqueue(buffer);
    }

    private void DrainCompleted(IRecordingEndpoint endpoint, bool requeue)
    {
        // FIFO matters after ring wrap: scanning headers by array index can emit
        // a newly completed first buffer before the older second/third buffers.
        while (_queued.TryPeek(out NativeBuffer? buffer)
            && (buffer.ReadHeader().Flags & WinMmInterop.HeaderDone) != 0)
        {
            EnsureEndpoint(endpoint, recording: true);
            WinMmInterop.WaveHeader header = buffer.ReadHeader();
            if (header.BytesRecorded > NativeBufferBytes
                || header.BytesRecorded % VoiceAudioFormat.BlockAlignment != 0)
            {
                throw new VoiceInputException(VoiceInputFailureCode.RecordingFailed);
            }

            _queued.Dequeue();
            int bytes = (int)header.BytesRecorded;
            try
            {
                if (bytes != 0)
                {
                    Marshal.Copy(buffer.Data, _scratch, 0, bytes);
                    EnsureEndpoint(endpoint, recording: true);
                    _receiveSamples!(_scratch.AsMemory(0, bytes));
                }
            }
            finally
            {
                Array.Clear(_scratch);
                buffer.ClearSamples();
            }

            if (requeue && !_stopToken.IsCancellationRequested && !_disposeStop.IsCancellationRequested)
            {
                header.BytesRecorded = 0;
                header.Flags &= ~WinMmInterop.HeaderDone;
                Marshal.StructureToPtr(header, buffer.Header, false);
                QueueBuffer(buffer);
            }
        }
    }

    private bool Cleanup(IRecordingEndpoint? endpoint, ref Exception? failure)
    {
        if (_handle == 0)
        {
            return true;
        }

        uint stopResult = _api.Stop(_handle);
        if (stopResult != WinMmInterop.NoError)
        {
            failure ??= new VoiceInputException(WinMmInterop.ClassifyError(stopResult, recording: true));
        }

        bool closed = false;
        for (int attempt = 0; attempt < CleanupAttempts; attempt++)
        {
            uint resetResult = _api.Reset(_handle);
            if (resetResult != WinMmInterop.NoError)
            {
                failure ??= new VoiceInputException(WinMmInterop.ClassifyError(resetResult, recording: true));
            }

            // Stop returns a partial buffer; Reset returns all pending buffers.
            // Drain once, only on a healthy endpoint and without earlier failure.
            if (failure is null && endpoint is not null)
            {
                try
                {
                    EnsureEndpoint(endpoint, recording: true);
                    DrainCompleted(endpoint, requeue: false);
                }
                catch (Exception error)
                {
                    failure = Sanitize(error);
                }
            }

            foreach (NativeBuffer buffer in _buffers.Where(buffer => buffer.Prepared))
            {
                if (_api.Unprepare(_handle, buffer.Header, HeaderBytes) == WinMmInterop.NoError)
                {
                    buffer.Prepared = false;
                }
            }

            // Unpreparing all headers is mandatory before close/free. If a
            // driver still owns one, retry reset instead of invalidating memory.
            if (_buffers.All(buffer => !buffer.Prepared)
                && _api.Close(_handle) == WinMmInterop.NoError)
            {
                _handle = 0;
                closed = true;
                break;
            }

            _driverEvent.WaitOne(10);
        }

        foreach (NativeBuffer buffer in _buffers.Where(buffer => !buffer.Prepared))
        {
            buffer.Dispose();
        }

        if (!closed)
        {
            failure = new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed);
            // No UAF: retain only the bounded still-prepared buffers and event.
            // Best-effort zeroing cannot force an uncooperative driver to stop.
            foreach (NativeBuffer buffer in _buffers.Where(buffer => buffer.Prepared))
            {
                buffer.ClearSamples();
            }
        }

        _queued.Clear();
        return closed;
    }

    private static void EnsureEndpoint(IRecordingEndpoint endpoint, bool recording)
    {
        if (!endpoint.IsAvailable)
        {
            throw new VoiceInputException(recording
                ? VoiceInputFailureCode.DeviceLost
                : VoiceInputFailureCode.DeviceUnavailable);
        }
    }

    private static Exception Sanitize(Exception error) => error switch
    {
        VoiceInputException => error,
        UnauthorizedAccessException => new VoiceInputException(VoiceInputFailureCode.MicrophoneAccessDenied),
        COMException { HResult: unchecked((int)WinMmInterop.AccessDenied) } =>
            new VoiceInputException(VoiceInputFailureCode.MicrophoneAccessDenied),
        _ => new VoiceInputException(VoiceInputFailureCode.RecordingFailed),
    };

    private sealed class NativeBuffer : IDisposable
    {
        internal nint Data { get; private set; }
        internal nint Header { get; private set; }
        internal bool Prepared { get; set; }

        internal NativeBuffer()
        {
            Data = Marshal.AllocHGlobal(NativeBufferBytes);
            try
            {
                ClearSamples();
                Header = Marshal.AllocHGlobal((int)HeaderBytes);
                Marshal.StructureToPtr(new WinMmInterop.WaveHeader
                {
                    Data = Data,
                    BufferLength = NativeBufferBytes,
                }, Header, false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal WinMmInterop.WaveHeader ReadHeader() => Marshal.PtrToStructure<WinMmInterop.WaveHeader>(Header);
        internal unsafe void ClearSamples() => new Span<byte>((void*)Data, NativeBufferBytes).Clear();

        public void Dispose()
        {
            if (Data != 0)
            {
                ClearSamples();
                Marshal.FreeHGlobal(Data);
                Data = 0;
            }

            if (Header != 0)
            {
                Marshal.FreeHGlobal(Header);
                Header = 0;
            }
        }
    }
}

/// <summary>
/// One process-wide production owner keeps native quarantine bounded. After an
/// unsafe driver cleanup, restarting the application is required before reopen.
/// Tests inject an isolated gate and never open a physical microphone.
/// </summary>
internal sealed class WinMmCaptureSafetyGate
{
    private readonly object _lock = new();
    private bool _occupied;
    private WinMmMicrophone? _quarantined;

    internal void Acquire()
    {
        lock (_lock)
        {
            if (_quarantined is not null)
            {
                throw new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed);
            }

            if (_occupied)
            {
                throw new VoiceInputException(VoiceInputFailureCode.Busy);
            }

            _occupied = true;
        }
    }

    internal void Release()
    {
        lock (_lock)
        {
            _occupied = false;
        }
    }

    internal void Quarantine(WinMmMicrophone microphone)
    {
        lock (_lock)
        {
            _quarantined = microphone;
        }
    }
}
