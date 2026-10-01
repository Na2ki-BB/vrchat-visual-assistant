using VrcVa.Core;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Video;
using VrcVa.Windows.Voice;

namespace VrcVa.Windows.Tests;

public sealed class VrVoiceSearchControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MicrophoneToBothSearchModesAndEveryCard_UsesSharedOwners_ProgressCannotEraseTranscript(bool interpreted, bool reverseSubscriptions)
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using OperationProgressController progress = new(fixture.Voice.Execution, view);
            if (!reverseSubscriptions) { progress.AttachVoice(fixture.Voice.Flow); }
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            if (reverseSubscriptions) { progress.AttachVoice(fixture.Voice.Flow); }
            view.Microphone();
            await fixture.Voice.Microphone.Started.Task;
            Assert.Equal(OperationProgressState.Recording, view.Progress!.State);
            view.ActivateProgress(OperationProgressAction.Stop); // reachable microphone-stop control on the recording surface
            await fixture.Voice.Flow.WhenIdle;
            Assert.NotNull(view.Current);
            Assert.True(view.Current.IsInput);
            Assert.True(view.Current.Allows(VrVoiceSearchAction.DirectSearch));
            Assert.True(view.Current.Allows(VrVoiceSearchAction.InterpretedSearch));
            Assert.Equal(1, fixture.Voice.Opens);
            Assert.All(fixture.Voice.BorrowedAudio.ToArray(), value => Assert.Equal(0, value));
            VrVoiceSearchSnapshot transcript = view.Current;
            view.Activate(interpreted ? VrVoiceSearchAction.InterpretedSearch : VrVoiceSearchAction.DirectSearch);
            view.Activate(VrVoiceSearchAction.DirectSearch, transcript); // stale button cannot double-run
            await fixture.Flow.WhenIdle;
            await fixture.Flow.WhenImagesIdle;
            Assert.Equal(1, fixture.Searches);
            Assert.Equal(interpreted ? 1 : 0, fixture.Interpretations);
            Assert.Equal(1, fixture.Voice.Requests);
            Assert.Same(transcript.Input, view.Current!.Input);
            Assert.Equal(interpreted ? "synthetic interpreted query" : transcript.Input!.Transcript, view.Current.Query);
            for (int page = 0; page < 2; page++)
            {
                Assert.Equal(page, view.Current!.PageIndex);
                Assert.Equal(5, view.Current.Cards.Count);
                Assert.Equal(page > 0, view.Current.CanPrevious);
                Assert.Equal(page == 0, view.Current.CanNext);
                for (int card = 0; card < 5; card++)
                {
                    string expected = view.Current.Cards[card].Candidate.WatchUrl.AbsoluteUri;
                    VrVoiceSearchSnapshot before = view.Current;
                    view.Activate(VrVoiceSearchAction.Candidate1 + card);
                    view.Activate(VrVoiceSearchAction.Candidate1 + card, before);
                    await fixture.Flow.WhenIdle;
                    Assert.Equal(expected, fixture.Writes[^1]);
                    Assert.Contains("コピーしました", view.Current!.Message, StringComparison.Ordinal);
                }
                if (page == 0) { view.Activate(VrVoiceSearchAction.Next); }
            }
            Assert.Equal(10, fixture.Writes.Count);
            Assert.Equal(1, fixture.Searches);
            view.Activate(VrVoiceSearchAction.Previous);
            view.Activate(VrVoiceSearchAction.Back);
            Assert.True(view.Current!.IsInput);
            Assert.Same(transcript.Input, view.Current.Input);
            Assert.Empty(view.Current.Cards);
            view.Activate(VrVoiceSearchAction.Close);
            await fixture.Flow.WhenIdle;
            Assert.Null(view.Current);
            Assert.Null(fixture.Voice.Flow.CurrentInput);

        });
    }

    [Fact]
    public async Task TranscriptPagingAndPeriodicRefresh_PreserveSnapshotUntilSomethingChanges()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new() { TranscriptPages = 3 };
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            VrVoiceSearchSnapshot first = view.Current!;
            for (int i = 0; i < 10; i++) { fixture.Flow.Refresh(); controller.Refresh(); }
            Assert.Same(first, view.Current);
            view.Activate(VrVoiceSearchAction.Previous);
            Assert.Same(first, view.Current);
            view.Activate(VrVoiceSearchAction.Next);
            Assert.Equal(1, view.Current!.PageIndex);
            view.Activate(VrVoiceSearchAction.Next, first);
            Assert.Equal(1, view.Current.PageIndex);
            view.Activate(VrVoiceSearchAction.Next);
            Assert.Equal(2, view.Current.PageIndex);
            Assert.False(view.Current.CanNext);
            view.Activate(VrVoiceSearchAction.Previous);
            Assert.Equal(1, view.Current.PageIndex);
            Assert.Same(first.Input, view.Current.Input);
            Assert.Equal(0, fixture.Searches);

        });
    }

    [Fact]
    public async Task FailedSearchRetry_ReusesInterpretedQuery_AndBackKeepsInput()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            fixture.SearchResponse = (_, _) => throw new ScanException(ScanFailureCode.VideoSearchTimedOut, ScanStage.TextHandling, "private failure");
            await fixture.Voice.RecordAsync();
            view.Activate(VrVoiceSearchAction.InterpretedSearch);
            await fixture.Flow.WhenIdle;
            Assert.True(view.Current!.CanRetry);
            Assert.True(view.Current.CanBack);
            Assert.DoesNotContain("private", view.Current.Message, StringComparison.Ordinal);
            fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(0));
            view.Activate(VrVoiceSearchAction.Retry);
            await fixture.Flow.WhenIdle;
            Assert.Equal(1, fixture.Interpretations);
            Assert.Equal(1, fixture.Voice.Requests);
            Assert.Equal(2, fixture.Searches);
            Assert.Empty(view.Current!.Cards);
            Assert.Contains("0件", view.Current.Message, StringComparison.Ordinal);
            view.Activate(VrVoiceSearchAction.Back);
            Assert.True(view.Current!.IsInput);

        });
    }

    [Fact]
    public async Task CancelWaitsForProviderCleanup_ThenReturnsToTranscriptWithoutLateCandidates()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<VideoSearchBatch> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.SearchResponse = (_, _) => { entered.TrySetResult(); return response.Task; };
            await fixture.Voice.RecordAsync();
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await entered.Task;
            view.Activate(VrVoiceSearchAction.Cancel);
            Assert.Equal(VideoSearchFlowState.Cancelling, view.Current!.State);
            Assert.False(view.Current.CanRerecord);
            Assert.False(view.Current.CanSearch);
            Assert.False(view.Current.CanCancel);
            view.Microphone();
            Assert.Equal(1, fixture.Voice.Opens);
            response.SetResult(SearchFlowFixture.Batch());
            await fixture.Flow.WhenIdle;
            Assert.True(view.Current!.IsInput);
            Assert.Empty(view.Current.Cards);

        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseOrRerecord_DropsOldImagesAndSelections_AsyncCloseCannotCancelNewRecording(bool close)
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            TaskCompletionSource<VideoThumbnailResult> images = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.ImageResponse = (_, _) => images.Task;
            await fixture.Voice.RecordAsync();
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            VrVoiceSearchSnapshot old = view.Current!;
            fixture.Voice.NextMicrophone();
            Task closing = Task.CompletedTask;
            if (close) { closing = controller.CloseAsync(); view.Microphone(); }
            else { view.Activate(VrVoiceSearchAction.Rerecord); }
            await fixture.Voice.Microphone.Started.Task;
            view.Activate(VrVoiceSearchAction.Candidate1, old);
            images.SetResult(new(VideoThumbnailStatus.Available, new VideoThumbnailImage(1, 1, [0, 0, 0, 255])));
            await closing;
            await fixture.Flow.WhenImagesIdle;
            Assert.Equal(VoiceFlowState.Recording, fixture.Voice.Flow.State);
            Assert.Null(fixture.Flow.Result);
            Assert.Empty(fixture.Writes);
            view.Microphone();
            await fixture.Voice.Flow.WhenIdle;
            Assert.True(view.Current!.IsInput);
            Assert.NotEqual(old.Input!.SessionId, view.Current.Input!.SessionId);
            Assert.Empty(view.Current.Cards);

        });
    }

    [Fact]
    public async Task DisconnectDuringCopy_InvalidatesBeforeQueuedDispatcherWriteAndDrains()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.DispatchCopy = async (write, _) => { entered.TrySetResult(); await release.Task; return write(); };
            await fixture.Voice.RecordAsync();
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            view.Activate(VrVoiceSearchAction.Candidate1);
            await entered.Task;
            Task disconnect = controller.CloseAsync();
            Assert.Null(view.Current);
            Assert.Null(fixture.Voice.Flow.CurrentInput);
            Assert.False(disconnect.IsCompleted);
            release.SetResult();
            await disconnect;
            Assert.Empty(fixture.Writes);
            Assert.False(fixture.Voice.Execution.IsRunning);
            Assert.Null(view.Current);

        });
    }

    [Fact]
    public async Task TerminalCleanupFailureRemainsInFlowButClosedVrWarningNeverReopens()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            fixture.SearchResponse = (_, _) =>
            {
                fixture.Voice.Execution.Stop();
                throw new ScanException(ScanFailureCode.VideoSearchCleanupFailed, ScanStage.TextHandling, "private cleanup");
            };
            await fixture.Voice.RecordAsync();
            view.Activate(VrVoiceSearchAction.DirectSearch);
            await fixture.Flow.WhenIdle;
            Assert.True(fixture.Flow.RequiresRestart);
            Assert.NotNull(view.Current);
            Assert.False(view.Current.CanRetry);
            await controller.CloseAsync();
            fixture.Voice.Flow.Refresh();
            fixture.Flow.Refresh();
            controller.Refresh();
            Assert.True(fixture.Flow.RequiresRestart);
            Assert.Null(view.Current);
            view.Microphone();
            Assert.Equal(1, fixture.Voice.Opens);

        });
    }

    [Fact]
    public async Task NewScanInvalidatesOldVoicePresentation_WithoutHidingItsReplacementProgress()
    {
        await VrFlowTestThread.RunAsync(async () =>
        {
            await using SearchFlowFixture fixture = new();
            VrSearchView view = new();
            using VrVoiceSearchController controller = new(fixture.Voice.Execution, fixture.Voice.Flow, fixture.Flow, view);
            await fixture.Voice.RecordAsync();
            VrVoiceSearchSnapshot old = view.Current!;
            Assert.True(fixture.Voice.Execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
            OperationProgressSnapshot next = new(scan!.SessionId, scan.OperationId, OperationProgressState.Processing, "new scan", "capture", "");
            view.TryShowProgress(next);
            controller.Refresh();
            view.Activate(VrVoiceSearchAction.DirectSearch, old);
            Assert.Same(next, view.Progress);
            Assert.Null(view.Current);
            Assert.Equal(0, fixture.Searches);
            scan.Dispose();

        });
    }
}

internal sealed class VrSearchView : IVrVoiceSearchView, IOperationProgressView
{
    public event EventHandler? MicrophoneRequested;
    public event EventHandler<VrVoiceSearchActionEventArgs>? VoiceSearchActionRequested;
    public event EventHandler<OperationProgressActionEventArgs>? ProgressActionRequested;
    public VrVoiceSearchSnapshot? Current { get; private set; }
    public OperationProgressSnapshot? Progress { get; private set; }
    public int TranscriptPages { get; init; } = 1;
    public int MeasureTranscriptPages(string transcript) => TranscriptPages;
    public bool TryShowVoiceSearch(VrVoiceSearchSnapshot snapshot) { Current = snapshot; Progress = null; return true; }
    public void DismissVoiceSearch(VrVoiceSearchSnapshot snapshot) { if (ReferenceEquals(Current, snapshot)) { Current = null; } }
    public void DismissProgress() { if (Current is null) { Hide(); } }
    public bool TryShowProgress(OperationProgressSnapshot snapshot) { Progress = snapshot; Current = null; return true; }
    public void Hide() { Progress = null; Current = null; }
    public void ReturnToLauncher() { }
    public void ActivateProgress(OperationProgressAction action) => ProgressActionRequested?.Invoke(this, new(Progress!, action));
    public void Microphone() => MicrophoneRequested?.Invoke(this, EventArgs.Empty);
    public void Activate(VrVoiceSearchAction action, VrVoiceSearchSnapshot? snapshot = null) =>
        VoiceSearchActionRequested?.Invoke(this, new(snapshot ?? Current!, action));
}

internal static class VrFlowTestThread
{
    // These presentation flows are dispatcher-owned. Serialize all test continuations
    // like that dispatcher, without opening a Window or depending on a Windows UI loop.
    public static async Task RunAsync(Func<Task> body)
    {
        ConcurrentExclusiveSchedulerPair scheduler = new(TaskScheduler.Default, maxConcurrencyLevel: 1);
        try
        {
            await Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.None,
                scheduler.ExclusiveScheduler).Unwrap();
        }
        finally { scheduler.Complete(); }
    }
}
