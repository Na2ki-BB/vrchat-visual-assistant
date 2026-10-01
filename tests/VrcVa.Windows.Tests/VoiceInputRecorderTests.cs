using System.Buffers.Binary;
using VrcVa.Core;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VoiceInputRecorderTests
{
    private static VoiceInputOptions Enabled(int seconds = 30) => new() { IsEnabled = true, MaximumRecordingSeconds = seconds };
    private static readonly byte[] Speech = [0, 64, 0, 192];

    [Theory]
    [InlineData(false, true, (int)VoiceInputFailureCode.Disabled)]
    [InlineData(true, false, (int)VoiceInputFailureCode.VoiceKeyUnavailable)]
    [InlineData(false, false, (int)VoiceInputFailureCode.Disabled)]
    public async Task DisabledOrNoDedicatedVoiceKey_NeverOpensMicrophone(bool enabled, bool key, int failureCode)
    {
        VoiceInputFailureCode code = (VoiceInputFailureCode)failureCode;
        FakeMicrophoneFactory factory = new();
        ExecutionCoordinator execution = new();
        await using VoiceInputRecorder recorder = new(execution, factory);
        Assert.False(recorder.TryStart(Enabled() with { IsEnabled = enabled }, key, (_, _) => Task.CompletedTask,
            out _, out VoiceInputFailureCode? failure));
        Assert.Equal(code, failure);
        Assert.Equal(0, factory.Opens);
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public async Task ManualStopAndDefaultLimitRace_OneContinuationAfterCleanup()
    {
        ManualTimeProvider time = new();
        FakeMicrophoneFactory factory = new();
        ExecutionCoordinator execution = new();
        int continuations = 0;
        ReadOnlyMemory<byte> observed = default;
        await using VoiceInputRecorder recorder = new(execution, factory, time);
        Assert.True(recorder.TryStart(Enabled(), true, (audio, operation) =>
        {
            Assert.True(factory.Microphone.Disposed);
            Assert.True(execution.IsRunning);
            Assert.True(operation.IsCurrent);
            observed = audio.WaveBytes;
            Assert.Equal(1, audio.QuotaSeconds);
            Interlocked.Increment(ref continuations);
            return Task.CompletedTask;
        }, out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        Assert.Equal(30, session!.RemainingSeconds);
        time.Advance(TimeSpan.FromSeconds(29.25));
        Assert.Equal(1, session.RemainingSeconds);
        await Task.WhenAll(Task.Run(session.Stop), Task.Run(() => time.Advance(TimeSpan.FromSeconds(0.75))));
        VoiceRecordingOutcome outcome = await session.Completion;
        Assert.Equal(VoiceRecordingState.Completed, outcome.State);
        Assert.Equal(1, continuations);
        Assert.Equal(1, factory.Microphone.Records);
        Assert.True(factory.Microphone.Disposed);
        Assert.False(execution.IsRunning);
        Assert.All(observed.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(120)]
    public async Task ConfiguredTimerStopsAtLimitAndNeverStopsEarlyForSilence(int seconds)
    {
        ManualTimeProvider time = new();
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(new(), factory, time);
        int processed = 0;
        Assert.True(recorder.TryStart(Enabled(seconds), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(new byte[320]);
        time.Advance(TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1));
        Assert.False(session!.Completion.IsCompleted);
        Assert.Equal(1, session.RemainingSeconds);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(VoiceInputFailureCode.SilentAudio, (await session.Completion).Failure);
        Assert.Equal(0, processed);
        Assert.True(factory.Microphone.Disposed);
    }

    [Fact]
    public async Task SampleByteCapStopsAndTruncatesOnlyAtWholeSamples()
    {
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(new(), factory);
        int bytes = 0;
        Assert.True(recorder.TryStart(Enabled(1), true, (audio, _) =>
        {
            bytes = audio.PcmBytes;
            Assert.Equal(VoiceAudioFormat.BytesPerSecond + 44, audio.WaveBytes.Length);
            Assert.Equal(VoiceAudioFormat.BytesPerSecond, BinaryPrimitives.ReadInt32LittleEndian(audio.WaveBytes.Span[40..]));
            return Task.CompletedTask;
        }, out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        byte[] large = Enumerable.Repeat(Speech, 20_000).SelectMany(x => x).ToArray();
        factory.Microphone.Send(large);
        Assert.Equal(VoiceRecordingState.Completed, (await session!.Completion).State);
        Assert.Equal(VoiceAudioFormat.BytesPerSecond, bytes);
    }

    [Theory]
    [InlineData(0, (int)VoiceInputFailureCode.EmptyAudio)]
    [InlineData(4, (int)VoiceInputFailureCode.SilentAudio)]
    [InlineData(3, (int)VoiceInputFailureCode.InvalidAudio)]
    public async Task EmptyMutedOrPartialSampleRejectsWithoutProcessing(int count, int failureCode)
    {
        VoiceInputFailureCode code = (VoiceInputFailureCode)failureCode;
        FakeMicrophoneFactory factory = new();
        int processed = 0;
        await using VoiceInputRecorder recorder = new(new(), factory);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(new byte[count]);
        if (count % 2 == 0) { Assert.False(session!.Completion.IsCompleted); }
        session!.Stop();
        VoiceRecordingOutcome outcome = await session.Completion;
        Assert.Equal(code, outcome.Failure);
        Assert.Equal(0, processed);
        Assert.True(factory.Microphone.Disposed);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("vr-loss")]
    [InlineData("shutdown")]
    public async Task LifecycleCancellation_DrainsWithoutProcessing(string action)
    {
        FakeMicrophoneFactory factory = new() { Microphone = new() { DelayCleanup = true } };
        ExecutionCoordinator execution = new();
        VoiceInputRecorder recorder = new(execution, factory);
        int processed = 0;
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        Task closing = action switch
        {
            "close" => recorder.CloseAsync(),
            "vr-loss" => recorder.OnSteamVrLostAsync(),
            "shutdown" => recorder.DisposeAsync().AsTask(),
            _ => Cancel(session!),
        };
        await factory.Microphone.CleanupStarted.Task;
        Assert.True(execution.IsRunning);
        Assert.False(execution.WhenIdle.IsCompleted);
        Assert.False(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out _, out _));
        factory.Microphone.Send(Speech); // late data after cancellation is ignored
        factory.Microphone.AllowCleanup.TrySetResult();
        await closing;
        Assert.Equal(VoiceRecordingState.Cancelled, (await session!.Completion).State);
        Assert.Equal(0, processed);
        Assert.True(factory.Microphone.Disposed);
        Assert.False(execution.IsRunning);
        await recorder.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringProcessing_HoldsGateThroughLateSuccessOrFailureAndZerosAudio(bool lateFailure)
    {
        FakeMicrophoneFactory factory = new();
        ExecutionCoordinator execution = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadOnlyMemory<byte> observed = default;
        await using VoiceInputRecorder recorder = new(execution, factory);
        Assert.True(recorder.TryStart(Enabled(), true, async (audio, operation) =>
        {
            observed = audio.WaveBytes;
            started.SetResult();
            await drain.Task;
            if (lateFailure) { throw new InvalidOperationException("private late content"); }
            operation.ThrowIfNotCurrent();
        }, out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        await started.Task;
        session.Cancel();
        Assert.True(execution.IsRunning);
        Assert.False(session.Completion.IsCompleted);
        drain.SetResult();
        Assert.Equal(VoiceRecordingState.Cancelled, (await session.Completion).State);
        Assert.All(observed.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData((int)VoiceInputFailureCode.DeviceLost)]
    [InlineData((int)VoiceInputFailureCode.DeviceUnavailable)]
    [InlineData((int)VoiceInputFailureCode.FormatUnsupported)]
    [InlineData((int)VoiceInputFailureCode.MicrophoneAccessDenied)]
    public async Task DeviceFailureHasTypedSafeCodeAndNoProcessing(int failureCode)
    {
        VoiceInputFailureCode code = (VoiceInputFailureCode)failureCode;
        FakeMicrophoneFactory factory = new() { Microphone = new() { Failure = code } };
        int processed = 0;
        await using VoiceInputRecorder recorder = new(new(), factory);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        Assert.Equal(code, (await session.Completion).Failure);
        Assert.Equal(0, processed);
        Assert.True(factory.Microphone.Disposed);
    }

    [Fact]
    public async Task FailedProcessingRetainsOnlyBoundedCurrentAudioAndRerecordInvalidatesIt()
    {
        ManualTimeProvider time = new();
        FakeMicrophoneFactory factory = new();
        ExecutionCoordinator execution = new();
        await using VoiceInputRecorder recorder = new(execution, factory, time);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => throw new InvalidOperationException("private fake content"),
            out VoiceRecordingSession? first, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        first!.Stop();
        Assert.Equal(VoiceInputFailureCode.InputProcessingFailed, (await first.Completion).Failure);
        Assert.True(first.Audio!.IsAvailable);
        Assert.False(execution.IsRunning);
        Guid previous = first.SessionId;
        factory.Microphone = new();
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out VoiceRecordingSession? second, out _));
        Assert.NotEqual(previous, second!.SessionId);
        Assert.False(execution.IsSessionCurrent(previous));
        Assert.False(first.Audio.IsAvailable);
        await recorder.CloseAsync();
    }

    [Fact]
    public async Task UnconfirmedNativeCleanupFailsClosedForEveryFeatureButShutdownCanComplete()
    {
        ExecutionCoordinator execution = new();
        FakeMicrophoneFactory factory = new() { Microphone = new() { Failure = VoiceInputFailureCode.MicrophoneCleanupFailed } };
        VoiceInputRecorder recorder = new(execution, factory);
        int processed = 0;
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        VoiceRecordingOutcome failure = await session.Completion;
        Assert.Equal(VoiceRecordingState.Failed, failure.State);
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, failure.Failure);
        Assert.Equal(0, processed);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.False(execution.TryBeginConfiguration(out _));
        Assert.False(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out _, out _));
        Assert.True(execution.WhenIdle.IsCompleted);
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task CleanupFailureBeforeRecordingStillFailsClosedAndDoesNotClaimDeviceDisposed()
    {
        ExecutionCoordinator execution = new();
        FakeMicrophoneFactory factory = new() { DelayOpen = true, Microphone = new() { FailCleanup = true } };
        VoiceInputRecorder recorder = new(execution, factory);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask,
            out VoiceRecordingSession? session, out _));
        await factory.OpenStarted.Task;
        session!.Stop();
        factory.AllowOpen.SetResult();
        VoiceRecordingOutcome failure = await session.Completion;
        Assert.Equal(VoiceInputFailureCode.MicrophoneCleanupFailed, failure.Failure);
        Assert.Equal(VoiceRecordingState.Failed, failure.State);
        Assert.Equal(0, factory.Microphone.Records);
        Assert.False(factory.Microphone.Disposed);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.True(execution.WhenIdle.IsCompleted);
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task AcceptedRerecordImmediatelyInvalidatesK1TranscriptCandidates()
    {
        ExecutionCoordinator execution = new();
        VideoSearchSession search = new(execution);
        DirectVideoSearchHandler handler = new(new SyntheticSearchProvider(), search);
        TextInputSession transcript = TextInputSession.Create("synthetic transcript");
        ScanRequest request = ScanRequest.CreateText("fake-search", FeatureIds.DirectVideoSearch, transcript);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation, sessionId: transcript.SessionId));
        VideoCandidateAction selection;
        using (operation)
        {
            FeatureResult result = await handler.HandleAsync(transcript, request, null, default);
            selection = result.VideoSearch!.Candidates[0].CreateSelectionAction();
        }
        Assert.True(search.TryResolveSelection(selection, out _));
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(execution, factory);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out _, out _));
        Assert.False(execution.IsSessionCurrent(transcript.SessionId));
        Assert.False(search.TryResolveSelection(selection, out _));
        Assert.Equal("synthetic transcript", transcript.Transcript);
        await recorder.CloseAsync();
    }

    [Fact]
    public async Task StaleVoiceCancelAndCloseCannotInvalidateNewScanSession()
    {
        ExecutionCoordinator execution = new();
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(execution, factory);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => throw new InvalidOperationException(),
            out VoiceRecordingSession? voice, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        voice!.Stop();
        await voice.Completion;
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        using (scan)
        {
            voice.Cancel();
            await recorder.CloseAsync();
            Assert.True(scan!.IsCurrent);
            Assert.False(scan.CancellationToken.IsCancellationRequested);
            Assert.True(execution.IsRunning);
            Assert.False(voice.Audio!.IsAvailable);
        }
    }

    [Fact]
    public async Task BusyDoesNotReplaceSessionOrOpenAnotherDevice()
    {
        ExecutionCoordinator execution = new();
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(execution, factory);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        using (scan)
        {
            Guid original = execution.CurrentSessionId;
            Assert.False(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out _, out VoiceInputFailureCode? failure));
            Assert.Equal(VoiceInputFailureCode.Busy, failure);
            Assert.Equal(original, execution.CurrentSessionId);
            Assert.Equal(0, factory.Opens);
        }
    }

    [Fact]
    public async Task PrecancelledDoesNotAcquireAndStopDuringOpenDoesNotRecord()
    {
        FakeMicrophoneFactory factory = new() { DelayOpen = true };
        await using VoiceInputRecorder recorder = new(new(), factory);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.False(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out _, out _, cancelled.Token));
        Assert.Equal(0, factory.Opens);
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => Task.CompletedTask, out VoiceRecordingSession? session, out _));
        await factory.OpenStarted.Task;
        session!.Stop();
        factory.AllowOpen.SetResult();
        Assert.Equal(VoiceInputFailureCode.EmptyAudio, (await session.Completion).Failure);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(0, factory.Microphone.Records);
        Assert.True(factory.Microphone.Disposed);
    }

    [Fact]
    public async Task CancellationDuringOpenWaitsForLateDeviceAndCleansItWithoutRecording()
    {
        FakeMicrophoneFactory factory = new() { DelayOpen = true };
        ExecutionCoordinator execution = new();
        await using VoiceInputRecorder recorder = new(execution, factory);
        int processed = 0;
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.OpenStarted.Task;
        session!.Cancel();
        Assert.True(execution.IsRunning);
        Assert.False(session.Completion.IsCompleted);
        factory.AllowOpen.SetResult();
        Assert.Equal(VoiceRecordingState.Cancelled, (await session.Completion).State);
        Assert.True(factory.Microphone.Disposed);
        Assert.Equal(0, factory.Microphone.Records);
        Assert.Equal(0, processed);
    }

    [Fact]
    public async Task ThrowingStopCallbackStillDrainsAndRejectsAudio()
    {
        FakeMicrophoneFactory factory = new() { Microphone = new() { ThrowStopCallback = true } };
        await using VoiceInputRecorder recorder = new(new(), factory);
        int processed = 0;
        Assert.True(recorder.TryStart(Enabled(), true, (_, _) => { processed++; return Task.CompletedTask; },
            out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        Assert.Equal(VoiceInputFailureCode.RecordingFailed, (await session.Completion).Failure);
        Assert.True(factory.Microphone.Disposed);
        Assert.Equal(0, processed);
    }

    [Fact]
    public async Task CancelAfterFailedProcessingDiscardsRetainedAudioImmediately()
    {
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(new(), factory);
        ReadOnlyMemory<byte> view = default;
        Assert.True(recorder.TryStart(Enabled(), true, (audio, _) =>
        {
            view = audio.WaveBytes;
            throw new InvalidOperationException("private fake content");
        }, out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        await session.Completion;
        Assert.True(session.Audio!.IsAvailable);
        session.Cancel();
        Assert.False(session.Audio.IsAvailable);
        Assert.All(view.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CloseAfterFailedProcessingDiscardsRetainedAudio()
    {
        FakeMicrophoneFactory factory = new();
        await using VoiceInputRecorder recorder = new(new(), factory);
        ReadOnlyMemory<byte> view = default;
        Assert.True(recorder.TryStart(Enabled(), true, (audio, _) =>
        {
            view = audio.WaveBytes;
            throw new InvalidOperationException("private fake content");
        }, out VoiceRecordingSession? session, out _));
        await factory.Microphone.Started.Task;
        factory.Microphone.Send(Speech);
        session!.Stop();
        await session.Completion;
        Assert.True(session.Audio!.IsAvailable);
        await recorder.CloseAsync();
        Assert.False(session.Audio.IsAvailable);
        Assert.All(view.ToArray(), value => Assert.Equal(0, value));
    }

    private static async Task Cancel(VoiceRecordingSession session) { session.Cancel(); await session.Completion; }

    private sealed class SyntheticSearchProvider : IVideoSearchProvider
    {
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken token) =>
            Task.FromResult(new VideoSearchBatch([new VideoMetadata("sample00000", "synthetic title")]));
    }

    private sealed class FakeMicrophoneFactory : IMicrophoneFactory
    {
        public int Opens { get; private set; }
        public bool DelayOpen { get; init; }
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeMicrophone Microphone { get; set; } = new();
        public async Task<IMicrophone> OpenAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Opens++;
            OpenStarted.TrySetResult();
            if (DelayOpen) { await AllowOpen.Task; } // deliberately uncooperative native open
            return Microphone;
        }
    }

    private sealed class FakeMicrophone : IMicrophone
    {
        private Action<ReadOnlyMemory<byte>>? _receive;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayCleanup { get; init; }
        public bool ThrowStopCallback { get; init; }
        public bool FailCleanup { get; init; }
        public VoiceInputFailureCode? Failure { get; init; }
        public bool Disposed { get; private set; }
        public int Records { get; private set; }
        public void Send(byte[] bytes) => _receive?.Invoke(bytes);
        public async Task RecordAsync(Action<ReadOnlyMemory<byte>> receiveSamples, CancellationToken stopToken)
        {
            Records++;
            _receive = receiveSamples;
            Task stopped = Task.Delay(Timeout.Infinite, stopToken);
            using CancellationTokenRegistration throwing = stopToken.Register(() =>
            {
                if (ThrowStopCallback) { throw new InvalidOperationException("private callback failure"); }
            });
            Started.TrySetResult();
            try { await stopped; }
            catch (OperationCanceledException) { }
            if (Failure is VoiceInputFailureCode failure) { throw new VoiceInputException(failure); }
        }
        public async ValueTask DisposeAsync()
        {
            CleanupStarted.TrySetResult();
            if (DelayCleanup) { await AllowCleanup.Task; }
            if (FailCleanup) { throw new InvalidOperationException("private cleanup failure"); }
            Disposed = true;
        }
    }
}
