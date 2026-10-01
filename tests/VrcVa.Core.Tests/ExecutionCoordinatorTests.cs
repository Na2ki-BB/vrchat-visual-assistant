namespace VrcVa.Core.Tests;

public sealed class ExecutionCoordinatorTests
{
    [Fact]
    public void OwnerScopedCancelAndCloseRejectStaleAndForeignIdentity()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? old));
        Guid oldSession = old!.SessionId;
        old.Dispose();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? next));
        using (next)
        {
            Assert.False(execution.CancelOperation(old));
            Assert.False(execution.CloseSession(oldSession));
            Assert.False(execution.CloseSession(Guid.Empty));
            Assert.True(next!.IsCurrent);
            Assert.False(next.CancellationToken.IsCancellationRequested);
            Assert.True(execution.IsRunning);
            Assert.True(execution.CancelOperation(next));
            Assert.False(next.IsCurrent);
            Assert.True(next.CancellationToken.IsCancellationRequested);
            Assert.True(execution.IsRunning);
        }

        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? current));
        using (current)
        {
            Assert.True(execution.CloseSession(current!.SessionId));
            Assert.False(current.IsCurrent);
            Assert.Equal(Guid.Empty, execution.CurrentSessionId);
            Assert.True(execution.IsRunning);
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedCancelAndCloseInvalidateK1CandidatesBeforeCancellationCallbacks(bool close)
    {
        ExecutionCoordinator execution = new();
        VideoSearchSession search = new(execution);
        DirectVideoSearchHandler handler = new(new ScopedCancellationSearchProvider(), search);
        TextInputSession transcript = TextInputSession.Create("synthetic transcript");
        ScanRequest request = ScanRequest.CreateText("fake-search", FeatureIds.DirectVideoSearch, transcript);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation, sessionId: transcript.SessionId));
        VideoCandidateAction selection;
        using (operation)
        {
            FeatureResult result = await handler.HandleAsync(transcript, request, null, default);
            selection = result.VideoSearch!.Candidates[0].CreateSelectionAction();
        }
        Assert.True(execution.TryBeginOperation(transcript.SessionId, Guid.NewGuid(), out ExecutionOperation? copy));
        using (copy)
        {
            Assert.False(execution.CancelOperation(operation!));
            Assert.True(search.TryResolveSelection(selection, out _));
            bool callbackRan = false;
            using CancellationTokenRegistration callback = copy!.CancellationToken.Register(() =>
            {
                callbackRan = true;
                Assert.False(search.TryResolveSelection(selection, out _));
            });
            Assert.True(close ? execution.CloseSession(transcript.SessionId) : execution.CancelOperation(copy));
            Assert.True(callbackRan);
            Assert.Null(copy.CancellationFailure);
            Assert.False(search.TryResolveSelection(selection, out _));
            Assert.True(execution.IsRunning);
        }
        Assert.False(search.TryResolveSelection(selection, out _));
    }

    private sealed class ScopedCancellationSearchProvider : IVideoSearchProvider
    {
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken token) =>
            Task.FromResult(new VideoSearchBatch([new VideoMetadata("sample00000", "synthetic title")]));
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordingThroughTranscription_RejectsEveryOtherEntryWithoutQueuing(bool duringTranscription)
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? recording));
        FakeVoiceFlow voice = new();
        Task recordingTask = voice.RunAsync(recording);
        await voice.RecordingEntered.Task.WaitAsync(TestTimeout);
        if (duringTranscription)
        {
            voice.StopRecording.SetResult();
            await voice.TranscriptionEntered.Task.WaitAsync(TestTimeout);
        }

        Guid sessionId = execution.CurrentSessionId;
        Guid operationId = execution.CurrentOperationId;
        CountingCapture capture = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, new FakeAnalyzer(), renderer, execution: execution);
        foreach (string trigger in new[] { "scan-button", "global-hotkey", "vrchat-osc", "wrist-launcher" })
        {
            ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.Create(trigger));
            Assert.Equal(ScanFailureCode.Busy, outcome.Failure?.Code);
        }

        int rejectedActions = 0;
        foreach (string action in new[] { "search", "copy" })
        {
            if (execution.TryBeginOperation(sessionId, Guid.NewGuid(), out ExecutionOperation? operation))
            {
                using (operation) { rejectedActions++; }
            }
            Assert.True(recording.IsCurrent, action);
        }

        Assert.Equal(sessionId, execution.CurrentSessionId);
        Assert.Equal(operationId, execution.CurrentOperationId);
        Assert.True(execution.IsActive(recording));
        Assert.Equal(0, capture.Calls);
        Assert.Equal(0, rejectedActions);
        Assert.Empty(renderer.Progress);
        Assert.Empty(renderer.Outcomes);

        voice.StopRecording.TrySetResult();
        await voice.TranscriptionEntered.Task.WaitAsync(TestTimeout);
        Assert.True(execution.IsRunning);
        voice.FinishTranscription.SetResult();
        await recordingTask.WaitAsync(TestTimeout);
        Assert.False(execution.IsRunning);
        Assert.Equal(1, voice.RecordingCalls);
        Assert.Equal(1, voice.TranscriptionCalls);
        Assert.Equal(0, capture.Calls);
        Assert.Equal(0, rejectedActions);
    }

    [Fact]
    public async Task Cancel_KeepsOwnershipAcrossUncooperativeWorkAndAwaitedCleanup()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        TaskCompletionSource workEntered = NewSignal();
        TaskCompletionSource releaseWork = NewSignal();
        TaskCompletionSource cleanupEntered = NewSignal();
        TaskCompletionSource releaseCleanup = NewSignal();
        int cancellationCalls = 0;
        using CancellationTokenRegistration registration = operation.CancellationToken.Register(
            () => Interlocked.Increment(ref cancellationCalls));
        Task owner = RunOwnerAsync();
        await workEntered.Task.WaitAsync(TestTimeout);
        Task idle = execution.WhenIdle;

        execution.CancelCurrentOperation();
        execution.CancelCurrentOperation();
        Assert.Equal(1, cancellationCalls);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.True(execution.IsActive(operation));
        Assert.False(idle.IsCompleted);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));

        releaseWork.SetResult();
        await cleanupEntered.Task.WaitAsync(TestTimeout);
        Assert.True(execution.IsRunning);
        Assert.False(idle.IsCompleted);
        Assert.False(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out _));
        releaseCleanup.SetResult();
        await owner.WaitAsync(TestTimeout);
        await idle.WaitAsync(TestTimeout);

        Assert.False(execution.IsRunning);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? next));
        next.Dispose();

        async Task RunOwnerAsync()
        {
            try
            {
                workEntered.SetResult();
                await releaseWork.Task;
            }
            finally
            {
                cleanupEntered.SetResult();
                await releaseCleanup.Task;
                operation.Dispose();
            }
        }
    }

    [Fact]
    public void CloseSession_InvalidatesWaitingInputAndRejectsStaleSearchAndCopy()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        operation.Dispose();
        Assert.True(operation.IsCurrent);
        execution.CloseSession();

        Assert.Equal(Guid.Empty, execution.CurrentSessionId);
        Assert.False(operation.IsCurrent);
        Assert.False(execution.IsCurrent(operation.OperationId));
        Assert.False(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out _));
        Assert.False(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out _));
    }

    [Fact]
    public void CloseAndReRecord_InvalidateOldCallbacksAndWaitForTheOldOwner()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? old));
        execution.CloseSession();
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.True(execution.IsActive(old));
        old.Dispose();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? replacement));

        int latePresentationCalls = 0;
        if (old.IsCurrent) { latePresentationCalls++; }
        Assert.Equal(0, latePresentationCalls);
        Assert.NotEqual(old.SessionId, replacement.SessionId);
        Assert.False(execution.TryBeginOperation(old.SessionId, Guid.NewGuid(), out _));
        replacement.Dispose();
    }

    [Fact]
    public void OldAndDoubleDispose_CannotReleaseAnotherOperation()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? old));
        old.Dispose();
        Assert.True(execution.TryBeginOperation(old.SessionId, Guid.NewGuid(), out ExecutionOperation? current));
        old.Dispose();

        Assert.True(execution.IsRunning);
        Assert.True(execution.IsActive(current));
        Assert.True(current.IsCurrent);
        Assert.False(old.IsCurrent);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        current.Dispose();
        current.Dispose();
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public void IdleSearchAndCopy_ReUseTheImmutableTranscriptAndReleaseIndependently()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? voice));
        TextInputSession transcript = new(voice.SessionId, "  Find a synthetic tutorial\nwith examples  ");
        voice.Dispose();
        Assert.False(execution.IsRunning);

        string? searchInput = null;
        string? copied = null;
        Assert.True(execution.TryBeginOperation(transcript.SessionId, Guid.NewGuid(), out ExecutionOperation? search));
        using (search) { searchInput = transcript.Transcript; }
        Assert.False(execution.IsRunning);
        Assert.True(execution.TryBeginOperation(transcript.SessionId, Guid.NewGuid(), out ExecutionOperation? copy));
        using (copy)
        {
            Assert.True(copy.IsCurrent);
            copied = "synthetic selected value";
        }

        Assert.Equal(transcript.Transcript, searchInput);
        Assert.Equal("  Find a synthetic tutorial\nwith examples  ", transcript.Transcript);
        Assert.Equal("synthetic selected value", copied);
        Assert.Equal(transcript.SessionId, execution.CurrentSessionId);
        Assert.False(search.IsCurrent);
        Assert.True(copy.IsCurrent);
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public void RejectedOperation_DoesNotInvalidateTheCurrentSessionOrResult()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? current));
        Guid sessionId = current.SessionId;
        Guid operationId = current.OperationId;
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? busy));
        Assert.Null(busy);
        current.Dispose();
        Assert.False(execution.TryBeginOperation(Guid.NewGuid(), Guid.NewGuid(), out ExecutionOperation? stale));
        Assert.Null(stale);
        Assert.Equal(sessionId, execution.CurrentSessionId);
        Assert.Equal(operationId, execution.CurrentOperationId);
        Assert.True(current.IsCurrent);
    }

    [Fact]
    public void ExternalCancellation_RemainsInvalidAfterReleaseAndPreservesSessionForRetry()
    {
        ExecutionCoordinator execution = new();
        using CancellationTokenSource cancellation = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation, cancellation.Token));
        cancellation.Cancel();

        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.False(execution.IsCurrent(operation.OperationId));
        Assert.True(execution.IsRunning);
        Assert.Throws<OperationCanceledException>(operation.ThrowIfNotCurrent);
        operation.Dispose();
        Assert.False(operation.IsCurrent);
        Assert.False(execution.IsCurrent(operation.OperationId));
        Assert.Equal(operation.SessionId, execution.CurrentSessionId);
        Assert.True(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out ExecutionOperation? retry));
        retry.Dispose();
    }

    [Fact]
    public void AlreadyCancelledAdmission_DoesNotReplaceWaitingSession()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? previous));
        previous.Dispose();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _, cancellation.Token));
        Assert.False(execution.TryBeginOperation(previous.SessionId, Guid.NewGuid(), out _, cancellation.Token));
        Assert.Equal(previous.SessionId, execution.CurrentSessionId);
        Assert.Equal(previous.OperationId, execution.CurrentOperationId);
        Assert.True(previous.IsCurrent);
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public async Task CancellationCallbacks_RunOutsideStateLockAndCompleteBeforeRelease()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        TaskCompletionSource callbackEntered = NewSignal();
        TaskCompletionSource releaseCallback = NewSignal();
        TaskCompletionSource disposeEntered = NewSignal();
        bool observedBusy = false;
        using CancellationTokenRegistration registration = operation.CancellationToken.Register(() =>
        {
            observedBusy = Task.Run(() => execution.IsRunning).WaitAsync(TestTimeout).GetAwaiter().GetResult();
            callbackEntered.SetResult();
            releaseCallback.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        });
        Task cancellation = Task.Run(execution.CancelCurrentOperation);
        await callbackEntered.Task.WaitAsync(TestTimeout);
        Task disposal = Task.Run(() =>
        {
            disposeEntered.SetResult();
            operation.Dispose();
        });
        await disposeEntered.Task.WaitAsync(TestTimeout);
        Assert.True(observedBusy);
        Assert.False(disposal.IsCompleted);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        releaseCallback.SetResult();
        await Task.WhenAll(cancellation, disposal).WaitAsync(TestTimeout);
        Assert.False(execution.IsRunning);
        Assert.False(operation.IsCurrent);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("stop")]
    public void ThrowingCancellationCallback_DoesNotEscapeOrReleaseOwnership(string action)
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        using CancellationTokenRegistration registration = operation.CancellationToken.Register(
            () => throw new InvalidOperationException("Synthetic adapter callback failure."));

        Exception? exception = Record.Exception(() =>
        {
            switch (action)
            {
                case "cancel": execution.CancelCurrentOperation(); break;
                case "close": execution.CloseSession(); break;
                default: execution.Stop(); break;
            }
        });
        Assert.Null(exception);
        Assert.NotNull(operation.CancellationFailure);
        Assert.True(operation.CancellationToken.IsCancellationRequested);
        Assert.False(operation.IsCurrent);
        Assert.True(execution.IsActive(operation));
        Assert.False(execution.WhenIdle.IsCompleted);
        operation.Dispose();
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public void AcceptedOperationIds_CannotBeReusedAcrossSessionOrOperationAdmission()
    {
        ExecutionCoordinator execution = new();
        Guid firstId = Guid.NewGuid();
        Assert.True(execution.TryBeginSession(firstId, out ExecutionOperation? first));
        first.Dispose();
        long generation = execution.CurrentGeneration;
        Assert.False(execution.TryBeginSession(firstId, out _));
        Assert.False(execution.TryBeginOperation(first.SessionId, firstId, out _));
        Assert.Equal(generation, execution.CurrentGeneration);
        Assert.True(first.IsCurrent);

        execution.CloseSession();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? replacement));
        replacement.Dispose();
        Assert.False(execution.TryBeginSession(firstId, out _));
        Assert.False(execution.TryBeginOperation(replacement.SessionId, firstId, out _));
        Assert.True(replacement.IsCurrent);
        Assert.False(execution.IsCurrent(firstId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationChanges_InvalidateEmptyAndAcceptedReceiptsThroughCancellationCleanup(bool closeSession)
    {
        ExecutionCoordinator execution = new();
        long emptyReceipt = execution.CurrentGeneration;
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        long acceptedReceipt = execution.CurrentGeneration;
        Assert.True(acceptedReceipt > emptyReceipt);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.Equal(acceptedReceipt, execution.CurrentGeneration);

        if (closeSession) { execution.CloseSession(); }
        else { execution.CancelCurrentOperation(); }
        long invalidatedReceipt = execution.CurrentGeneration;
        Assert.True(invalidatedReceipt > acceptedReceipt);
        Assert.True(execution.IsRunning);
        operation.Dispose();
        Assert.True(execution.CurrentGeneration > invalidatedReceipt);
        Assert.NotEqual(emptyReceipt, execution.CurrentGeneration);
        Assert.NotEqual(acceptedReceipt, execution.CurrentGeneration);
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public void RuntimeUpdates_RunOnlyWhileIdleAndNeverDuringCancellationOrAfterStop()
    {
        ExecutionCoordinator execution = new();
        int updates = 0;
        Assert.True(execution.TryUpdateWhenIdle(() => updates++));
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        Assert.False(execution.TryUpdateWhenIdle(() => updates++));
        execution.CancelCurrentOperation();
        Assert.False(execution.TryUpdateWhenIdle(() => updates++));
        Assert.Equal(1, updates);
        operation.Dispose();
        Assert.True(execution.TryUpdateWhenIdle(() => updates++));
        execution.Stop();
        Assert.False(execution.TryUpdateWhenIdle(() => updates++));
        Assert.Equal(2, updates);
    }

    [Fact]
    public async Task RuntimeUpdate_IsAtomicWithConcurrentAdmission()
    {
        ExecutionCoordinator execution = new();
        TaskCompletionSource updateEntered = NewSignal();
        TaskCompletionSource releaseUpdate = NewSignal();
        TaskCompletionSource admissionEntered = NewSignal();
        int runtimeVersion = 1;
        Task<bool> updating = Task.Run(() => execution.TryUpdateWhenIdle(() =>
        {
            updateEntered.SetResult();
            releaseUpdate.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
            runtimeVersion = 2;
        }));
        await updateEntered.Task.WaitAsync(TestTimeout);
        Task<ExecutionOperation?> admitting = Task.Run(() =>
        {
            admissionEntered.SetResult();
            return execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation) ? operation : null;
        });
        await admissionEntered.Task.WaitAsync(TestTimeout);
        Assert.False(admitting.IsCompleted);
        releaseUpdate.SetResult();
        Assert.True(await updating.WaitAsync(TestTimeout));
        ExecutionOperation? accepted = await admitting.WaitAsync(TestTimeout);
        Assert.NotNull(accepted);
        Assert.Equal(2, runtimeVersion);
        Assert.True(execution.IsActive(accepted));
        accepted.Dispose();
    }

    [Fact]
    public void FailedRuntimeUpdate_DoesNotHoldOwnershipOrPreventLaterAdmission()
    {
        ExecutionCoordinator execution = new();
        Assert.Throws<InvalidOperationException>(() => execution.TryUpdateWhenIdle(
            () => throw new InvalidOperationException("Synthetic runtime update failure.")));
        Assert.False(execution.IsRunning);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        operation.Dispose();
    }

    [Fact]
    public async Task Stop_WaitsForOwnerAndPermanentlyRejectsNewOperations()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? operation));
        Task idle = execution.WhenIdle;
        execution.Stop();
        Assert.True(execution.IsRunning);
        Assert.False(idle.IsCompleted);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        operation.Dispose();
        await idle.WaitAsync(TestTimeout);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.False(execution.TryBeginOperation(operation.SessionId, Guid.NewGuid(), out _));
        Assert.False(execution.IsCurrent(operation.OperationId));
    }

    [Fact]
    public async Task RebuiltPipeline_SharedCoordinatorCannotBypassAnActiveCapture()
    {
        ExecutionCoordinator execution = new();
        BlockingCapture firstCapture = new();
        RecordingRenderer firstRenderer = new();
        ScanPipeline first = new(firstCapture, new FakeAnalyzer(), firstRenderer, execution: execution);
        Task<ScanOutcome> firstRun = first.RunAsync(ScanRequest.Create("first-runtime"));
        await firstCapture.Entered.Task.WaitAsync(TestTimeout);

        CountingCapture rebuiltCapture = new();
        RecordingRenderer rebuiltRenderer = new();
        ScanPipeline rebuilt = new(rebuiltCapture, new FakeAnalyzer(), rebuiltRenderer, execution: execution);
        ScanOutcome busy = await rebuilt.RunAsync(ScanRequest.Create("rebuilt-runtime"));
        Assert.Equal(ScanFailureCode.Busy, busy.Failure?.Code);
        Assert.Equal(0, rebuiltCapture.Calls);
        Assert.Empty(rebuiltRenderer.Progress);
        Assert.Empty(rebuiltRenderer.Outcomes);

        firstCapture.Release.SetResult();
        Assert.True((await firstRun.WaitAsync(TestTimeout)).IsSuccess);
        Assert.True((await rebuilt.RunAsync(ScanRequest.Create("explicit-retry"))).IsSuccess);
        Assert.Equal(1, rebuiltCapture.Calls);
        Assert.Single(firstRenderer.Outcomes);
        Assert.Single(rebuiltRenderer.Outcomes);
    }

    [Fact]
    public async Task CallerOwnedPipeline_LeavesReleaseUntilTheCallersCleanup()
    {
        ExecutionCoordinator execution = new();
        ScanRequest request = ScanRequest.Create("caller-owned");
        Assert.True(execution.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation));
        CountingCapture capture = new();
        ScanPipeline pipeline = new(capture, new FakeAnalyzer(), new RecordingRenderer(), execution: execution);

        Assert.True((await pipeline.RunAsync(request, operation)).IsSuccess);
        Assert.True(execution.IsActive(operation));
        Assert.False(execution.WhenIdle.IsCompleted);
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _));
        Assert.All(capture.Bytes, value => Assert.Equal(0, value));
        operation.Dispose();
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public async Task CancelledPipeline_DropsLateProgressAndSuccessAndClearsTheFrame()
    {
        ExecutionCoordinator execution = new();
        CountingCapture capture = new();
        BlockingAnalyzer analyzer = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, analyzer, renderer, execution: execution);
        Task<ScanOutcome> running = pipeline.RunAsync(ScanRequest.Create("delayed-analysis"));
        await analyzer.Entered.Task.WaitAsync(TestTimeout);
        int originalProgressCount = renderer.Progress.Count;
        Task idle = execution.WhenIdle;
        execution.CloseSession();
        analyzer.Release.SetResult();

        ScanOutcome outcome = await running.WaitAsync(TestTimeout);
        await idle.WaitAsync(TestTimeout);
        Assert.Equal(ScanFailureCode.Cancelled, outcome.Failure?.Code);
        Assert.Equal(originalProgressCount, renderer.Progress.Count);
        Assert.Empty(renderer.Outcomes);
        Assert.All(capture.Bytes, value => Assert.Equal(0, value));
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public async Task RendererFailure_StillReleasesTheAcceptedOperation()
    {
        ExecutionCoordinator execution = new();
        ScanPipeline pipeline = new(new CountingCapture(), new FakeAnalyzer(), new ThrowingRenderer(), execution: execution);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(ScanRequest.Create("renderer-failure")));
        Assert.False(execution.IsRunning);
        Assert.True(execution.WhenIdle.IsCompleted);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? next));
        next.Dispose();
    }

    [Fact]
    public async Task ForeignOrMismatchedLease_IsRejectedWithoutReleasingItsOwner()
    {
        ExecutionCoordinator execution = new();
        ExecutionCoordinator foreignExecution = new();
        ScanRequest request = ScanRequest.Create("wrong-owner");
        Assert.True(foreignExecution.TryBeginSession(request.CorrelationId, out ExecutionOperation? foreign));
        ScanPipeline pipeline = new(new CountingCapture(), new FakeAnalyzer(), new RecordingRenderer(), execution: execution);
        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RunAsync(request, foreign));
        Assert.True(foreignExecution.IsActive(foreign));
        foreign.Dispose();

        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? mismatched));
        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RunAsync(request, mismatched));
        Assert.True(execution.IsActive(mismatched));
        mismatched.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static AnalysisResult CreateResult() => new(
        "Synthetic source", "Synthetic result", "en", "fake", "fake-model", TimeSpan.Zero, TimeSpan.Zero);

    private sealed class FakeVoiceFlow
    {
        public TaskCompletionSource RecordingEntered { get; } = NewSignal();
        public TaskCompletionSource StopRecording { get; } = NewSignal();
        public TaskCompletionSource TranscriptionEntered { get; } = NewSignal();
        public TaskCompletionSource FinishTranscription { get; } = NewSignal();
        public int RecordingCalls { get; private set; }
        public int TranscriptionCalls { get; private set; }

        public async Task RunAsync(ExecutionOperation operation)
        {
            using (operation)
            {
                RecordingCalls++;
                RecordingEntered.SetResult();
                await StopRecording.Task;
                operation.ThrowIfNotCurrent();
                TranscriptionCalls++;
                TranscriptionEntered.SetResult();
                await FinishTranscription.Task;
                operation.ThrowIfNotCurrent();
            }
        }
    }

    private sealed class CountingCapture : ICaptureSource
    {
        public int Calls { get; private set; }
        public byte[] Bytes { get; } = [1, 2, 3, 4];

        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CapturedFrame(Bytes, 1, 1, "image/png", "fake"));
        }
    }

    private sealed class BlockingCapture : ICaptureSource
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();

        public async Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task;
            return new CapturedFrame([1, 2, 3, 4], 1, 1, "image/png", "fake");
        }
    }

    private sealed class FakeAnalyzer : IAnalyzer
    {
        public Task<AnalysisResult> AnalyzeAsync(CapturedFrame frame, ScanRequest request,
            IProgress<ScanProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(CreateResult());
    }

    private sealed class BlockingAnalyzer : IAnalyzer
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();

        public async Task<AnalysisResult> AnalyzeAsync(CapturedFrame frame, ScanRequest request,
            IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task;
            progress?.Report(new ScanProgress(request.CorrelationId, ScanStage.Translation, "Synthetic delayed progress", TimeSpan.Zero));
            return CreateResult();
        }
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanProgress> Progress { get; } = [];
        public List<ScanOutcome> Outcomes { get; } = [];

        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRenderer : IResultRenderer
    {
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Synthetic rendering failure."));

        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Synthetic rendering failure."));
    }
}
