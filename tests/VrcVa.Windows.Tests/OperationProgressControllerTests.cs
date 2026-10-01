using System.Net;
using System.Net.Http;
using VrcVa.Core;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class OperationProgressControllerTests
{
    [Fact]
    public async Task RecordingCountdown_StopAndTranscription_UseSameFlowAndReleaseAudioBeforeHiding()
    {
        await using VoiceFlowFixture fixture = new();
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { entered.TrySetResult(); return response.Task; };
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        controller.Refresh();
        Assert.Equal(OperationProgressState.Recording, view.Current!.State);
        Assert.Contains("30", view.Current.Title, StringComparison.Ordinal);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        controller.Refresh();
        Assert.Contains("28", view.Current!.Title, StringComparison.Ordinal);
        view.Activate(OperationProgressAction.Stop);
        await entered.Task;
        Assert.Equal(OperationProgressState.Transcribing, view.Current!.State);
        Assert.False(view.Current.CanStop);
        Assert.True(view.Current.CanCancel);
        Assert.False(fixture.Execution.TryBeginSession(Guid.NewGuid(), out _));
        response.SetResult(VoiceFlowFixture.Success("private transcript never sent to VR in L1"));
        await fixture.Flow.WhenIdle;
        Assert.Null(view.Current);
        Assert.Equal(1, view.Returns);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
        Assert.DoesNotContain(view.History, s => s.Message.Contains("private transcript", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelRecording_WaitsForNativeCleanup_NoTranscriptionOrAdmissionDuringCleanup()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Microphone.DelayCleanup = true;
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        controller.Refresh();
        view.Activate(OperationProgressAction.Cancel);
        await fixture.Microphone.CleanupStarted.Task;
        Assert.Equal(OperationProgressState.Cancelling, view.Current!.State);
        Assert.False(view.Current.CanClose);
        Assert.False(view.Current.CanCancel);
        view.Activate(OperationProgressAction.Close);
        Assert.False(fixture.Execution.TryBeginSession(Guid.NewGuid(), out _));
        fixture.Microphone.Cleanup.SetResult();
        await fixture.Flow.WhenIdle;
        Assert.Equal(OperationProgressState.Cancelled, view.Current!.State);
        Assert.True(view.Current.CanClose);
        Assert.Equal(0, fixture.Requests);
        view.Activate(OperationProgressAction.Close);
        await fixture.Flow.WhenIdle;
        Assert.Null(view.Current);
    }

    [Fact]
    public async Task CancelTranscription_DrainsLateHttpWithoutTextOrDoubleRetry()
    {
        await using VoiceFlowFixture fixture = new();
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (_, _) => { entered.TrySetResult(); return late.Task; };
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        fixture.Flow.StopRecording();
        await entered.Task;
        OperationProgressSnapshot old = view.Current!;
        view.Activate(OperationProgressAction.Cancel);
        Assert.Equal(OperationProgressState.Cancelling, view.Current!.State);
        Assert.True(fixture.Execution.IsRunning);
        view.Activate(OperationProgressAction.Cancel, old);
        view.Activate(OperationProgressAction.Retry, old);
        Assert.Equal(1, fixture.Requests);
        late.SetResult(VoiceFlowFixture.Success("late private response"));
        await fixture.Flow.WhenIdle;
        Assert.Equal(OperationProgressState.Cancelled, view.Current!.State);
        Assert.Null(fixture.Flow.CurrentInput);
        Assert.False(fixture.Execution.IsRunning);
        Assert.Equal(1, fixture.Quotas.Voice.ConsumedRequests);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task TypedVoiceFailure_ManualRetryUsesSameAudio_ExpiryDisablesStaleAction(HttpStatusCode status, bool retryable)
    {
        await using VoiceFlowFixture fixture = new();
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(status));
        await fixture.RecordAsync();
        controller.Refresh();
        Assert.Equal(OperationProgressState.Failed, view.Current!.State);
        Assert.Contains(fixture.Flow.FailureCode!, view.Current.Detail, StringComparison.Ordinal);
        Assert.Equal(retryable, view.Current.CanRetry);
        Assert.Equal(1, fixture.Requests);
        OperationProgressSnapshot old = view.Current;
        view.Activate(OperationProgressAction.Retry);
        await fixture.Flow.WhenIdle;
        Assert.Equal(retryable ? 2 : 1, fixture.Requests);
        Assert.Equal(1, fixture.Opens);
        fixture.Time.Advance(TimeSpan.FromSeconds(15));
        view.Activate(OperationProgressAction.Retry, old);
        controller.Refresh();
        Assert.False(view.Current!.CanRetry);
        Assert.Equal(retryable ? 2 : 1, fixture.Requests);
        Assert.All(fixture.BorrowedAudio.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task HiddenCapture_WpfCancelNeverRevealsOverlayUntilOwnerCleanup_RejectsLateRenderingAndStaleActions()
    {
        await using VoiceFlowFixture fixture = new();
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        Assert.True(fixture.Execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        controller.BeginScan(scan!, () => Task.CompletedTask);
        controller.ShowScanNotice("notice");
        OperationProgressSnapshot previous = view.Current!;
        controller.RenderProgress(new(scan!.OperationId, ScanStage.Capture, "capture", TimeSpan.Zero));
        Assert.Null(view.Current);
        Assert.True(controller.CancelActive());
        controller.Refresh();
        Assert.Null(view.Current);
        Assert.True(fixture.Execution.IsRunning);
        controller.RenderProgress(new(scan.OperationId, ScanStage.Ocr, "late private content", TimeSpan.Zero));
        Assert.Null(view.Current);
        scan.Dispose();
        controller.CompleteScan(scan);
        Assert.Equal(OperationProgressState.Cancelled, view.Current!.State);
        Assert.True(view.Current.CanClose);
        Assert.True(fixture.Execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? replacement));
        using (replacement)
        {
            view.Activate(OperationProgressAction.Cancel, previous);
            Assert.True(replacement!.IsCurrent);
            Assert.False(controller.CancelActive());
        }
    }

    [Theory]
    [InlineData(ScanFailureCode.TranslationTimedOut, true)]
    [InlineData(ScanFailureCode.CaptureUnavailable, true)]
    [InlineData(ScanFailureCode.NoTextDetected, true)]
    [InlineData(ScanFailureCode.TranslationUsageLimitReached, false)]
    [InlineData(ScanFailureCode.TranslationAuthenticationFailed, false)]
    [InlineData(ScanFailureCode.TranslationNotConfigured, false)]
    public void TranslationFailure_RetryIsExplicitTypedAndDisabledUntilCleanup(ScanFailureCode failure, bool retryable)
    {
        ExecutionCoordinator execution = new();
        ProgressView view = new();
        using OperationProgressController controller = new(execution, view);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        int retries = 0;
        controller.BeginScan(scan!, () => { retries++; return Task.CompletedTask; });
        controller.RenderProgress(new(scan!.OperationId, ScanStage.Translation, "translation", TimeSpan.Zero));
        Assert.True(view.Current!.CanCancel);
        controller.RenderOutcome(ScanOutcome.Failed(scan.OperationId,
            new(failure, ScanStage.Translation, "typed failure"), TimeSpan.Zero));
        Assert.Equal(OperationProgressState.Failed, view.Current!.State);
        Assert.False(view.Current.CanRetry);
        Assert.False(view.Current.CanClose);
        view.Activate(OperationProgressAction.Retry);
        Assert.Equal(0, retries);
        scan.Dispose();
        controller.CompleteScan(scan);
        Assert.Equal(retryable, view.Current!.CanRetry);
        Assert.True(view.Current.CanClose);
        Assert.Contains(failure.ToString(), view.Current.Detail, StringComparison.Ordinal);
        view.Activate(OperationProgressAction.Retry);
        Assert.Equal(retryable ? 1 : 0, retries);
    }

    [Fact]
    public void SuccessAwaitingRendererCleanup_CanStillCancelPendingResultAndRejectStaleResultActions()
    {
        ExecutionCoordinator execution = new();
        ProgressView view = new();
        using OperationProgressController controller = new(execution, view);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        controller.BeginScan(scan!, () => Task.CompletedTask);
        controller.ShowScanNotice("notice");
        // Success reaches the progress renderer before all other renderers and
        // the full result's pending ImageLoaded have drained.
        controller.RenderOutcome(ScanOutcome.Succeeded(scan!.OperationId,
            new AnalysisResult("fake", "fake", "en", "fake", "fake", TimeSpan.Zero, TimeSpan.Zero), TimeSpan.Zero));
        Assert.True(controller.CancelActive());
        Assert.True(execution.IsActive(scan));
        Assert.Equal(OperationProgressState.Cancelling, view.Current!.State);
        Assert.False(view.Current.CanClose);
        controller.RenderProgress(new(scan.OperationId, ScanStage.Rendering, "late result", TimeSpan.Zero));
        Assert.Equal(OperationProgressState.Cancelling, view.Current.State);
        scan.Dispose();
        controller.CompleteScan(scan);
        Assert.Equal(OperationProgressState.Cancelled, view.Current!.State);
        Assert.True(view.Current.CanClose);
    }

    [Fact]
    public async Task ConnectionLoss_CancelsAudioAndDrainsWithoutRecreatingVrOrSending()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Microphone.DelayCleanup = true;
        ProgressView view = new();
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        int shows = view.History.Count;
        Task lost = controller.ConnectionLostAsync();
        await fixture.Microphone.CleanupStarted.Task;
        Assert.False(lost.IsCompleted);
        Assert.True(fixture.Execution.IsRunning);
        controller.Refresh();
        Assert.Equal(shows, view.History.Count);
        fixture.Microphone.Cleanup.SetResult();
        await lost;
        Assert.False(fixture.Execution.IsRunning);
        Assert.True(fixture.Microphone.Disposed);
        Assert.Equal(0, fixture.Requests);
        Assert.Equal(VoiceFlowState.Closed, fixture.Flow.State);
    }

    [Fact]
    public async Task Shutdown_DisconnectsPresentationWhileCancellationStillOwnsCleanup()
    {
        await using VoiceFlowFixture fixture = new();
        fixture.Microphone.DelayCleanup = true;
        ProgressView view = new();
        OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        Assert.True(fixture.Flow.TryStart());
        await fixture.Microphone.Started.Task;
        OperationProgressSnapshot old = view.Current!;
        controller.Dispose();
        fixture.Execution.Stop();
        Task shutdown = fixture.Flow.DisposeAsync().AsTask();
        await fixture.Microphone.CleanupStarted.Task;
        Assert.False(shutdown.IsCompleted);
        Assert.Null(view.Current);
        int shows = view.History.Count;
        view.Activate(OperationProgressAction.Stop, old);
        controller.Refresh();
        fixture.Microphone.Cleanup.SetResult();
        await shutdown;
        Assert.Equal(shows, view.History.Count);
        Assert.Equal(0, fixture.Requests);
        Assert.False(fixture.Execution.IsRunning);
    }

    [Fact]
    public async Task MissingVr_DoesNotPreventDesktopVoiceOrRetry()
    {
        await using VoiceFlowFixture fixture = new();
        ProgressView view = new() { Available = false };
        using OperationProgressController controller = new(fixture.Execution, view);
        controller.AttachVoice(fixture.Flow);
        await fixture.RecordAsync();
        Assert.Equal(VoiceFlowState.Completed, fixture.Flow.State);
        Assert.Equal(1, fixture.Requests);
        Assert.False(fixture.Execution.IsRunning);
    }

    [Fact]
    public async Task RecordingFailure_RetryReopensMicrophoneExplicitly_WithoutTranscription()
    {
        ExecutionCoordinator execution = new();
        RetryRecordingFactory microphones = new();
        NeverTranscriber transcriber = new();
        await using VoiceInputFlow flow = new(execution, microphones,
            () => new(new VoiceInputOptions { IsEnabled = true }, transcriber));
        ProgressView view = new();
        using OperationProgressController controller = new(execution, view);
        controller.AttachVoice(flow);
        Assert.True(flow.TryStart());
        await flow.WhenIdle;
        controller.Refresh();
        Assert.Equal(VoiceInputFailureCode.DeviceLost.ToString(), flow.FailureCode);
        Assert.True(view.Current!.CanRetry);
        Assert.Equal("録り直す", view.Current.RetryLabel);
        Guid failedSession = flow.SessionId;
        view.Activate(OperationProgressAction.Retry);
        await microphones.Second.Started.Task;
        Assert.NotEqual(failedSession, flow.SessionId);
        Assert.Equal(2, microphones.Opens);
        Assert.Equal(OperationProgressState.Recording, view.Current!.State);
        view.Activate(OperationProgressAction.Cancel);
        await flow.WhenIdle;
        Assert.Equal(0, transcriber.Requests);
    }

    [Fact]
    public void ScanDisconnect_DrainsOriginalOwnerWithoutRestartingDisplay()
    {
        ExecutionCoordinator execution = new();
        ProgressView view = new();
        using OperationProgressController controller = new(execution, view);
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        controller.BeginScan(scan!, () => Task.CompletedTask);
        controller.ShowScanNotice("notice");
        int shown = view.History.Count;
        Assert.True(controller.ConnectionLostAsync().IsCompletedSuccessfully);
        Assert.True(execution.IsActive(scan!));
        Assert.False(scan!.IsCurrent);
        controller.Refresh();
        Assert.Equal(shown, view.History.Count);
        scan.Dispose();
        controller.CompleteScan(scan);
        Assert.False(execution.IsRunning);
        Assert.Equal(shown, view.History.Count);
    }

    private sealed class RetryRecordingFactory : IMicrophoneFactory
    {
        public int Opens { get; private set; }
        public FakeVoiceMicrophone Second { get; } = new();
        public Task<IMicrophone> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IMicrophone>(++Opens == 1 ? new FailedMicrophone() : Second);
    }
    private sealed class FailedMicrophone : IMicrophone
    {
        public Task RecordAsync(Action<ReadOnlyMemory<byte>> receiveSamples, CancellationToken stopToken) =>
            Task.FromException(new VoiceInputException(VoiceInputFailureCode.DeviceLost));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NeverTranscriber : IVoiceTranscriber
    {
        public int Requests { get; private set; }
        public Task<VoiceTranscription> TranscribeAsync(ReadOnlyMemory<byte> waveBytes, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("No transmission expected.");
        }
    }

    private sealed class ProgressView : IOperationProgressView
    {
        public event EventHandler<OperationProgressActionEventArgs>? ProgressActionRequested;
        public OperationProgressSnapshot? Current { get; private set; }
        public List<OperationProgressSnapshot> History { get; } = [];
        public int Returns { get; private set; }
        public bool Available { get; set; } = true;
        public bool TryShowProgress(OperationProgressSnapshot snapshot) { Current = snapshot; History.Add(snapshot); return Available; }
        public void Hide() => Current = null;
        public void ReturnToLauncher() => Returns++;
        public void Activate(OperationProgressAction action, OperationProgressSnapshot? snapshot = null) =>
            ProgressActionRequested?.Invoke(this, new(snapshot ?? Current!, action));
    }
}
