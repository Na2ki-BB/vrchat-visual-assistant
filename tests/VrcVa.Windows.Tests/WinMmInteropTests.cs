using System.Runtime.InteropServices;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class WinMmInteropTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Abi_HasWindowsPointerWidthsAndPcm16Format()
    {
        Assert.Equal(18, Marshal.SizeOf<WinMmInterop.WaveFormat>());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf<WinMmInterop.WaveHeader>());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<WinMmInterop.WaveHeader>("BufferLength").ToInt32());
        Assert.Equal(IntPtr.Size + 4, Marshal.OffsetOf<WinMmInterop.WaveHeader>("BytesRecorded").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16,
            Marshal.OffsetOf<WinMmInterop.WaveHeader>("Flags").ToInt32());
        WinMmInterop.WaveFormat format = WinMmInterop.CreateFormat();
        Assert.Equal(1, format.FormatTag);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16_000u, format.SamplesPerSecond);
        Assert.Equal(32_000u, format.AverageBytesPerSecond);
        Assert.Equal(2, format.BlockAlignment);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(0, format.ExtraSize);
        Assert.Equal(0x00050000u, WinMmInterop.CallbackEvent);
        Assert.Equal(0x0010u, WinMmInterop.DefaultCommunicationDevice);
        Assert.Equal(0x0811u, WinMmInterop.QueryFunctionInstanceId);
        Assert.Equal(0x0812u, WinMmInterop.QueryFunctionInstanceIdSize);
    }

    [Theory]
    [InlineData(2u, false, (int)VoiceInputFailureCode.DeviceUnavailable)]
    [InlineData(6u, true, (int)VoiceInputFailureCode.DeviceLost)]
    [InlineData(5u, true, (int)VoiceInputFailureCode.DeviceLost)]
    [InlineData(32u, false, (int)VoiceInputFailureCode.FormatUnsupported)]
    [InlineData(0x80070005u, false, (int)VoiceInputFailureCode.MicrophoneAccessDenied)]
    [InlineData(1u, false, (int)VoiceInputFailureCode.RecordingFailed)]
    public void FailureMapping_DoesNotInferPrivacyDenialFromGenericError(
        uint result, bool recording, int expected) =>
        Assert.Equal((VoiceInputFailureCode)expected, WinMmInterop.ClassifyError(result, recording));

    [Fact]
    public void EndpointNotifications_LatchLossAndDefaultChangesWithoutCallingNativeApis()
    {
        var notifications = new WinMmEndpointNotifications(FakeApi.EndpointId);
        notifications.OnDeviceRemoved("different-endpoint");
        notifications.OnDeviceStateChanged(FakeApi.EndpointId, WinMmInterop.DeviceStateActive);
        notifications.OnDefaultDeviceChanged(0, 2, "different-render-endpoint");
        notifications.OnDefaultDeviceChanged(1, 0, "different-console-endpoint");
        Assert.False(notifications.Invalidated);
        notifications.OnDefaultDeviceChanged(1, 2, "different-communication-endpoint");
        notifications.OnDefaultDeviceChanged(1, 2, FakeApi.EndpointId);
        Assert.True(notifications.Invalidated); // Switching back never makes old audio valid again.

        var unplugged = new WinMmEndpointNotifications(FakeApi.EndpointId);
        unplugged.OnDeviceStateChanged(FakeApi.EndpointId, 8);
        unplugged.OnDeviceStateChanged(FakeApi.EndpointId, WinMmInterop.DeviceStateActive);
        Assert.True(unplugged.Invalidated);
        var removed = new WinMmEndpointNotifications(FakeApi.EndpointId);
        removed.OnDeviceRemoved(FakeApi.EndpointId);
        Assert.True(removed.Invalidated);
        var noDefault = new WinMmEndpointNotifications(FakeApi.EndpointId);
        noDefault.OnDefaultDeviceChanged(1, 2, null);
        Assert.True(noDefault.Invalidated);
    }

#if WINDOWS
    [Fact]
    public void EndpointNotifications_CcwExposesOfficialInterfaceAndDispatchesAllFiveNativeSlots()
    {
        var notifications = new WinMmEndpointNotifications(FakeApi.EndpointId);
        Guid notificationId = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
        Assert.Equal(notificationId, typeof(IWinMmNotificationClient).GUID);
        Assert.Equal(5, typeof(IWinMmNotificationClient).GetMethods().Length);
        Assert.Equal(20, Marshal.SizeOf<WinMmPropertyKey>());
        Assert.Equal(16, Marshal.OffsetOf<WinMmPropertyKey>(nameof(WinMmPropertyKey.PropertyId)).ToInt32());
        nint unknown = 0;
        nint client = 0;
        nint ownedCallback = 0;
        nint sameEndpoint = 0;
        nint otherEndpoint = 0;
        try
        {
            // This exercises only the managed COM callable wrapper. No endpoint
            // enumerator, WinMM device, or physical microphone is instantiated.
            unknown = Marshal.GetIUnknownForObject(notifications);
            Assert.Equal(0, Marshal.QueryInterface(unknown, ref notificationId, out client));
            Assert.NotEqual((nint)0, client);
            ownedCallback = Marshal.GetComInterfaceForObject<WinMmEndpointNotifications, IWinMmNotificationClient>(
                notifications);
            Assert.NotEqual((nint)0, ownedCallback);
            sameEndpoint = Marshal.StringToCoTaskMemUni(FakeApi.EndpointId);
            otherEndpoint = Marshal.StringToCoTaskMemUni("different-synthetic-endpoint");
            nint table = Marshal.ReadIntPtr(ownedCallback);
            var state = Marshal.GetDelegateForFunctionPointer<DeviceStateChangedCallback>(
                Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
            var added = Marshal.GetDelegateForFunctionPointer<DeviceChangedCallback>(
                Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
            var removed = Marshal.GetDelegateForFunctionPointer<DeviceChangedCallback>(
                Marshal.ReadIntPtr(table, 5 * IntPtr.Size));
            var changed = Marshal.GetDelegateForFunctionPointer<DefaultDeviceChangedCallback>(
                Marshal.ReadIntPtr(table, 6 * IntPtr.Size));
            var property = Marshal.GetDelegateForFunctionPointer<PropertyValueChangedCallback>(
                Marshal.ReadIntPtr(table, 7 * IntPtr.Size));
            Assert.Equal(0, state(ownedCallback, sameEndpoint, WinMmInterop.DeviceStateActive));
            Assert.Equal(0, added(ownedCallback, otherEndpoint));
            Assert.Equal(0, removed(ownedCallback, otherEndpoint));
            Assert.Equal(0, property(ownedCallback, sameEndpoint, new WinMmPropertyKey
            {
                FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                PropertyId = 14,
            }));
            Assert.False(notifications.Invalidated);
            Assert.Equal(0, changed(ownedCallback, 1, 2, otherEndpoint));
            Assert.True(notifications.Invalidated);
            Assert.Equal(0, changed(ownedCallback, 1, 2, sameEndpoint));
            Assert.True(notifications.Invalidated);
        }
        finally
        {
            Marshal.FreeCoTaskMem(sameEndpoint);
            Marshal.FreeCoTaskMem(otherEndpoint);
            if (ownedCallback != 0) { Marshal.Release(ownedCallback); }
            if (client != 0) { Marshal.Release(client); }
            if (unknown != 0) { Marshal.Release(unknown); }
            GC.KeepAlive(notifications);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceStateChangedCallback(nint client, nint endpoint, uint state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeviceChangedCallback(nint client, nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DefaultDeviceChangedCallback(nint client, int flow, int role, nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyValueChangedCallback(nint client, nint endpoint, WinMmPropertyKey key);
#endif

    [Fact]
    public async Task Open_QueriesCommunicationMapperAndSnapshotsActualEndpointWithoutRecording()
    {
        var api = new FakeApi();
        var endpoint = new FakeEndpoint();
        string? selected = null;
        var factory = new WinMmMicrophoneFactory(api, id =>
        {
            selected = id;
            return endpoint;
        });
        IMicrophone microphone = await factory.OpenAsync(CancellationToken.None).WaitAsync(TestTimeout);
        Assert.Equal(FakeApi.EndpointId, selected);
        Assert.Equal(["query", "open", "get-id", "id-size", "id"], api.Calls);
        Assert.All(api.OpenDevices, device => Assert.Equal(uint.MaxValue, device));
        Assert.Equal(0x11u, api.OpenFlags[0]);
        Assert.Equal(0x50010u, api.OpenFlags[1]);
        Assert.NotEqual((nuint)0, api.EventHandle);
        await microphone.DisposeAsync();
        await microphone.DisposeAsync();
        Assert.DoesNotContain("start", api.Calls);
        Assert.Equal(["stop", "reset", "close"], api.Calls.TakeLast(3));
        Assert.True(endpoint.Disposed);
        Assert.Single(api.Threads.Distinct());
        Assert.All(endpoint.Threads, thread => Assert.Equal(api.Threads[0], thread));
    }

    [Theory]
    [InlineData(2u, (int)VoiceInputFailureCode.DeviceUnavailable)]
    [InlineData(32u, (int)VoiceInputFailureCode.FormatUnsupported)]
    [InlineData(1u, (int)VoiceInputFailureCode.RecordingFailed)]
    public async Task QueryFailure_DoesNotOpenOrPrepare(uint result, int expected)
    {
        var api = new FakeApi { QueryResult = result };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(
            () => factory.OpenAsync(CancellationToken.None));
        Assert.Equal((VoiceInputFailureCode)expected, error.Code);
        Assert.Equal(["query"], api.Calls);
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("add")]
    [InlineData("start")]
    public async Task RecordingSetupFailure_ReclaimsEveryPreviouslyPreparedHeader(string stage)
    {
        var api = new FakeApi { FailStage = stage };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(
            () => microphone.RecordAsync(_ => { }, CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(VoiceInputFailureCode.RecordingFailed, error.Code);
        Assert.Contains("reset", api.Calls);
        Assert.Contains("close", api.Calls);
        Assert.True(api.AllBuffersWereZeroAtClose);
    }

    [Fact]
    public async Task CancelDuringUncooperativeNativeOpen_WaitsForCleanupBeforeReturning()
    {
        using var release = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi { OnOpen = () => { opening.TrySetResult(); release.Wait(); } };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        using var cancel = new CancellationTokenSource();
        Task<IMicrophone> pending = factory.OpenAsync(cancel.Token);
        await opening.Task.WaitAsync(TestTimeout);
        cancel.Cancel();
        try
        {
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
        Assert.Contains("close", api.Calls);
        Assert.DoesNotContain("start", api.Calls);
    }

    [Fact]
    public async Task CancelDuringNativeOpen_UnsafeLateCleanupOverridesCancellationAndRefusesReopen()
    {
        using var release = new ManualResetEventSlim();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeApi
        {
            OnOpen = () => { opening.TrySetResult(); release.Wait(); },
            CloseResult = 1,
        };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        using var cancel = new CancellationTokenSource();
        Task<IMicrophone> pending = factory.OpenAsync(cancel.Token);
        await opening.Task.WaitAsync(TestTimeout);
        cancel.Cancel();
        try
        {
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        VoiceInputException failure = await Assert.ThrowsAsync<VoiceInputException>(
            () => pending.WaitAsync(TestTimeout));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, failure.Code);
        Assert.Equal(WinMmMicrophone.CleanupAttempts, api.Calls.Count(call => call == "close"));
        Assert.DoesNotContain("start", api.Calls);
        VoiceInputException reopen = await Assert.ThrowsAsync<VoiceInputException>(
            () => factory.OpenAsync(CancellationToken.None));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, reopen.Code);
        Assert.Equal(1, api.Calls.Count(call => call == "open"));
    }

    [Fact]
    public async Task Recording_DrainsFinalPartialBeforeUnprepareCloseAndClearsBorrowedSlice()
    {
        var api = new FakeApi { FinalSamples = [1, 0, 2, 0] };
        var endpoint = new FakeEndpoint();
        var factory = new WinMmMicrophoneFactory(api, _ => endpoint);
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        byte[]? received = null;
        ReadOnlyMemory<byte> borrowed = default;
        Task recording = microphone.RecordAsync(samples =>
        {
            Assert.DoesNotContain("unprepare", api.Calls);
            received = samples.ToArray();
            borrowed = samples;
        }, stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        await recording.WaitAsync(TestTimeout);
        Assert.Equal(api.FinalSamples, received);
        Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(WinMmMicrophone.NativeBufferCount, api.MaximumQueued);
        Assert.All(api.HeaderLengths, length => Assert.Equal(3_200u, length));
        Assert.Equal(["stop", "reset", "unprepare", "unprepare", "unprepare", "close"], api.Calls.TakeLast(6));
        Assert.True(api.AllBuffersWereZeroAtClose);
        Assert.Single(api.Threads.Distinct());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await microphone.RecordAsync(_ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task Recording_RingWrapPreservesFifoSampleOrder()
    {
        var api = new FakeApi();
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<byte>();
        Task recording = microphone.RecordAsync(samples =>
        {
            received.Add(samples.Span[0]);
            if (received.Count == 1)
            {
                first.SetResult();
            }
            if (received.Count == 4)
            {
                all.SetResult();
            }
        }, stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        api.CompleteOldest([1, 0]);
        await first.Task.WaitAsync(TestTimeout);
        await api.FirstRequeue.Task.WaitAsync(TestTimeout);
        // Queue now consists of buffers 2, 3, 1, so an array-index scan is wrong.
        api.CompleteOldest([2, 0]);
        api.CompleteOldest([3, 0]);
        api.CompleteOldest([4, 0]);
        await all.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        await recording.WaitAsync(TestTimeout);
        Assert.Equal([1, 2, 3, 4], received);
        Assert.Equal(3, api.MaximumQueued);
    }

    [Fact]
    public async Task DeviceLossWithoutData_EndsRecordingAndDoesNotEmitFinalSamples()
    {
        var api = new FakeApi { FinalSamples = [7, 0] };
        var endpoint = new FakeEndpoint();
        var factory = new WinMmMicrophoneFactory(api, _ => endpoint);
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        int emitted = 0;
        Task recording = microphone.RecordAsync(_ => emitted++, CancellationToken.None);
        await api.Started.Task.WaitAsync(TestTimeout);
        endpoint.Lose();
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(
            () => recording.WaitAsync(TestTimeout));
        Assert.Equal(VoiceInputFailureCode.DeviceLost, error.Code);
        Assert.Equal(0, emitted);
        Assert.Contains("close", api.Calls);
        Assert.True(endpoint.Disposed);
    }

    [Fact]
    public async Task DeviceLossAtStop_DoesNotTreatNewDefaultSamplesAsValidFinalAudio()
    {
        var endpoint = new FakeEndpoint();
        var api = new FakeApi { FinalSamples = [7, 0], OnStop = endpoint.Lose };
        var factory = new WinMmMicrophoneFactory(api, _ => endpoint);
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        int emitted = 0;
        Task recording = microphone.RecordAsync(_ => emitted++, stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(() => recording);
        Assert.Equal(VoiceInputFailureCode.DeviceLost, error.Code);
        Assert.Equal(0, emitted);
    }

    [Fact]
    public async Task CallbackFailure_IsSanitizedAndCleanupStillCompletes()
    {
        var api = new FakeApi { FinalSamples = [1, 0] };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task recording = microphone.RecordAsync(_ => throw new Exception("private callback content"), stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(() => recording);
        Assert.Equal(VoiceInputFailureCode.RecordingFailed, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.Message);
        Assert.True(api.AllBuffersWereZeroAtClose);
    }

    [Fact]
    public async Task UnsafeCleanup_RetainsDriverOwnedMemoryAndRefusesReopenOnSameGate()
    {
        var api = new FakeApi { UnsafeCleanup = true };
        var gate = new WinMmCaptureSafetyGate();
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint(), gate);
        IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task recording = microphone.RecordAsync(_ => { }, stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        VoiceInputException error = await Assert.ThrowsAsync<VoiceInputException>(() => recording);
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, error.Code);
        Assert.Equal(WinMmMicrophone.CleanupAttempts, api.Calls.Count(call => call == "reset"));
        Assert.DoesNotContain("close", api.Calls);
        Assert.All(api.Headers, header =>
        {
            // Reading is safe because the allocation was deliberately retained.
            WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
            var samples = new byte[value.BufferLength];
            Marshal.Copy(value.Data, samples, 0, samples.Length);
            Assert.All(samples, sample => Assert.Equal(0, sample));
        });
        VoiceInputException reopen = await Assert.ThrowsAsync<VoiceInputException>(
            () => factory.OpenAsync(CancellationToken.None));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, reopen.Code);
        Assert.Equal(1, api.Calls.Count(call => call == "open"));
        VoiceInputException disposal = await Assert.ThrowsAsync<VoiceInputException>(
            () => microphone.DisposeAsync().AsTask());
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, disposal.Code);
    }

    [Fact]
    public async Task EndpointDisposalFailure_QuarantinesAfterNativeCloseAndRefusesReopen()
    {
        var api = new FakeApi();
        var endpoint = new FakeEndpoint { ThrowOnDispose = true };
        var factory = new WinMmMicrophoneFactory(api, _ => endpoint);
        IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        Task recording = microphone.RecordAsync(_ => { }, stop.Token);
        await api.Started.Task.WaitAsync(TestTimeout);
        stop.Cancel();
        VoiceInputException failure = await Assert.ThrowsAsync<VoiceInputException>(
            () => recording.WaitAsync(TestTimeout));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, failure.Code);
        Assert.True(((WinMmMicrophone)microphone).CleanupUnconfirmed);
        Assert.Equal(1, api.Calls.Count(call => call == "close"));
        Assert.True(api.AllBuffersWereZeroAtClose);
        Assert.True(endpoint.DisposeAttempted);
        Assert.False(endpoint.Disposed);
        VoiceInputException reopen = await Assert.ThrowsAsync<VoiceInputException>(
            () => factory.OpenAsync(CancellationToken.None));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, reopen.Code);
        Assert.Equal(1, api.Calls.Count(call => call == "open"));
        VoiceInputException disposal = await Assert.ThrowsAsync<VoiceInputException>(
            () => microphone.DisposeAsync().AsTask());
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, disposal.Code);
    }

    [Fact]
    public async Task DisposeWithoutRecording_ReportsUnsafeCloseAndRefusesReopen()
    {
        var api = new FakeApi { CloseResult = 1 };
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        VoiceInputException failure = await Assert.ThrowsAsync<VoiceInputException>(
            () => microphone.DisposeAsync().AsTask().WaitAsync(TestTimeout));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, failure.Code);
        Assert.True(((WinMmMicrophone)microphone).CleanupUnconfirmed);
        Assert.DoesNotContain("start", api.Calls);
        Assert.Equal(WinMmMicrophone.CleanupAttempts, api.Calls.Count(call => call == "close"));
        VoiceInputException reopen = await Assert.ThrowsAsync<VoiceInputException>(
            () => factory.OpenAsync(CancellationToken.None));
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, reopen.Code);
    }

    [Fact]
    public async Task AlreadyStoppedRecording_DoesNotPrepareStartOrThrowCancellation()
    {
        var api = new FakeApi();
        var factory = new WinMmMicrophoneFactory(api, _ => new FakeEndpoint());
        await using IMicrophone microphone = await factory.OpenAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await microphone.RecordAsync(_ => throw new Exception(), stop.Token).WaitAsync(TestTimeout);
        Assert.DoesNotContain("prepare", api.Calls);
        Assert.DoesNotContain("start", api.Calls);
        Assert.Contains("close", api.Calls);
    }

    private sealed class FakeEndpoint : IRecordingEndpoint
    {
        private int _available = 1;
        internal bool Disposed { get; private set; }
        internal bool ThrowOnDispose { get; init; }
        internal bool DisposeAttempted { get; private set; }
        internal List<int> Threads { get; } = [];
        public bool IsAvailable
        {
            get
            {
                Threads.Add(Environment.CurrentManagedThreadId);
                return Volatile.Read(ref _available) != 0;
            }
        }

        internal void Lose() => Volatile.Write(ref _available, 0);
        public void Dispose()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            DisposeAttempted = true;
            if (ThrowOnDispose) { throw new InvalidOperationException("synthetic unregister failure"); }
            Disposed = true;
        }
    }

    private sealed class FakeApi : IWinMmApi
    {
        internal const string EndpointId = "synthetic-communication-endpoint";
        private readonly object _queueLock = new();
        private readonly Queue<nint> _queued = new();
        private int _adds;
        internal List<string> Calls { get; } = [];
        internal List<int> Threads { get; } = [];
        internal List<uint> OpenDevices { get; } = [];
        internal List<uint> OpenFlags { get; } = [];
        internal List<uint> HeaderLengths { get; } = [];
        internal List<nint> Headers { get; } = [];
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstRequeue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal uint QueryResult { get; init; }
        internal uint CloseResult { get; init; }
        internal byte[] FinalSamples { get; init; } = [];
        internal Action? OnStop { get; init; }
        internal Action? OnOpen { get; init; }
        internal string? FailStage { get; init; }
        internal bool UnsafeCleanup { get; init; }
        internal nuint EventHandle { get; private set; }
        internal int MaximumQueued { get; private set; }
        internal bool AllBuffersWereZeroAtClose { get; private set; }

        public uint Open(out nint handle, uint deviceId, ref WinMmInterop.WaveFormat format, nuint callback, uint flags)
        {
            bool query = (flags & WinMmInterop.WaveFormatQuery) != 0;
            Log(query ? "query" : "open");
            OpenDevices.Add(deviceId);
            OpenFlags.Add(flags);
            EventHandle = callback;
            handle = query ? 0 : 123;
            if (!query) { OnOpen?.Invoke(); }
            return query ? QueryResult : 0;
        }

        public uint GetId(nint handle, out uint deviceId)
        {
            Log("get-id");
            deviceId = 7;
            return 0;
        }

        public uint Message(nint handleOrId, uint message, nuint parameter1, nuint parameter2)
        {
            Assert.Equal((nint)7, handleOrId);
            if (message == WinMmInterop.QueryFunctionInstanceIdSize)
            {
                Log("id-size");
                Marshal.WriteIntPtr((nint)parameter1, (nint)((EndpointId.Length + 1) * sizeof(char)));
            }
            else
            {
                Assert.Equal(WinMmInterop.QueryFunctionInstanceId, message);
                Log("id");
                char[] chars = (EndpointId + '\0').ToCharArray();
                Marshal.Copy(chars, 0, (nint)parameter1, chars.Length);
            }

            return 0;
        }

        public uint Prepare(nint handle, nint header, uint headerBytes)
        {
            Log("prepare");
            Assert.Equal((uint)Marshal.SizeOf<WinMmInterop.WaveHeader>(), headerBytes);
            if (FailStage == "prepare" && Headers.Count == 1) { return 1; }
            WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
            value.Flags |= WinMmInterop.HeaderPrepared;
            Marshal.StructureToPtr(value, header, false);
            HeaderLengths.Add(value.BufferLength);
            Headers.Add(header);
            return 0;
        }

        public uint AddBuffer(nint handle, nint header, uint headerBytes)
        {
            Log("add");
            if (FailStage == "add" && _adds == 1) { return 1; }
            lock (_queueLock)
            {
                WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
                value.Flags = (value.Flags | WinMmInterop.HeaderInQueue) & ~WinMmInterop.HeaderDone;
                Marshal.StructureToPtr(value, header, false);
                _queued.Enqueue(header);
                MaximumQueued = Math.Max(MaximumQueued, _queued.Count);
                if (++_adds == 4)
                {
                    FirstRequeue.TrySetResult();
                }
            }

            return 0;
        }

        public uint Start(nint handle)
        {
            Log("start");
            Started.TrySetResult();
            return FailStage == "start" ? 1u : 0;
        }

        public uint Stop(nint handle)
        {
            Log("stop");
            OnStop?.Invoke();
            if (FinalSamples.Length != 0)
            {
                CompleteOldest(FinalSamples);
            }

            return 0;
        }

        public uint Reset(nint handle)
        {
            Log("reset");
            if (UnsafeCleanup)
            {
                return 1;
            }

            lock (_queueLock)
            {
                while (_queued.Count != 0)
                {
                    CompleteOldest([]);
                }
            }

            return 0;
        }

        public uint Unprepare(nint handle, nint header, uint headerBytes)
        {
            Log("unprepare");
            if (UnsafeCleanup)
            {
                return WinMmInterop.StillPlaying;
            }

            WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
            Assert.Equal(0u, value.Flags & WinMmInterop.HeaderInQueue);
            value.Flags &= ~WinMmInterop.HeaderPrepared;
            Marshal.StructureToPtr(value, header, false);
            return 0;
        }

        public uint Close(nint handle)
        {
            Log("close");
            Assert.Empty(_queued);
            AllBuffersWereZeroAtClose = Headers.All(header =>
            {
                WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
                var samples = new byte[value.BufferLength];
                Marshal.Copy(value.Data, samples, 0, samples.Length);
                return samples.All(sample => sample == 0);
            });
            return CloseResult;
        }

        internal void CompleteOldest(byte[] samples)
        {
            lock (_queueLock)
            {
                nint header = _queued.Dequeue();
                WinMmInterop.WaveHeader value = Marshal.PtrToStructure<WinMmInterop.WaveHeader>(header);
                Marshal.Copy(samples, 0, value.Data, samples.Length);
                value.BytesRecorded = (uint)samples.Length;
                value.Flags = (value.Flags | WinMmInterop.HeaderDone) & ~WinMmInterop.HeaderInQueue;
                Marshal.StructureToPtr(value, header, false);
            }
        }

        private void Log(string call)
        {
            Calls.Add(call);
            Threads.Add(Environment.CurrentManagedThreadId);
        }
    }
}
