namespace VrcVa.Core.Tests;

public sealed class VideoCandidateClipboardTests
{
    [Fact]
    public async Task Copy_WritesTheExactCurrentCandidateOnlyAfterDispatchAndShowsSuccess()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        VideoCandidate chosen = result.Candidates[6];
        Task<VideoClipboardCopyResult> pending = harness.Copy.CopyAsync(chosen.CreateSelectionAction(), Guid.NewGuid());
        Assert.Empty(harness.Writer.Writes);
        Assert.True(harness.Execution.IsRunning);
        harness.Dispatcher.Run();
        VideoClipboardCopyResult copied = await pending;
        Assert.Equal(VideoClipboardCopyStatus.Copied, copied.Status);
        Assert.Equal("URLをコピーしました", copied.Message);
        Assert.True(harness.Copy.IsFeedbackCurrent(copied));
        Assert.Equal([chosen.WatchUrl.AbsoluteUri], harness.Writer.Writes);
        Assert.Same(result, harness.Session.CurrentResult);
        Assert.True(harness.Session.TryGetPage(result.SessionId, result.OperationId, 1, out var page));
        Assert.Contains(chosen, page);
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task ConcurrentClicks_AreNotQueuedAndAReplayedOperationCannotWriteTwice()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        Guid copyId = Guid.NewGuid();
        Task<VideoClipboardCopyResult> first = harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), copyId);
        VideoClipboardCopyResult repeated = await harness.Copy.CopyAsync(result.Candidates[1].CreateSelectionAction(), Guid.NewGuid());
        Assert.Equal(VideoClipboardCopyStatus.Busy, repeated.Status);
        Assert.DoesNotContain("コピーしました", repeated.Message);
        Assert.True(harness.Copy.IsFeedbackCurrent(repeated));
        Assert.Equal(1, harness.Dispatcher.Calls);
        harness.Dispatcher.Run();
        Assert.Equal(VideoClipboardCopyStatus.Copied, (await first).Status);
        Assert.Equal(VideoClipboardCopyStatus.StaleSelection, harness.Dispatcher.RepeatWrite());
        Assert.False(harness.Copy.IsFeedbackCurrent(repeated));
        VideoClipboardCopyResult replay = await harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), copyId);
        Assert.Equal(VideoClipboardCopyStatus.StaleSelection, replay.Status);
        Assert.Single(harness.Writer.Writes);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("stop")]
    public async Task InvalidationBetweenAdmissionAndDispatch_PreventsWriting(string action)
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        Task<VideoClipboardCopyResult> pending = harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), Guid.NewGuid());
        if (action == "close") { harness.Execution.CloseSession(); }
        else if (action == "cancel") { harness.Execution.CancelCurrentOperation(); }
        else { harness.Execution.Stop(); }
        Assert.True(harness.Execution.IsRunning);
        Assert.False(harness.Execution.TryBeginSession(Guid.NewGuid(), out _));
        harness.Dispatcher.Run(); // Deliberately ignore cancellation to test the last identity check too.
        VideoClipboardCopyResult stale = await pending;
        Assert.NotEqual(VideoClipboardCopyStatus.Copied, stale.Status);
        Assert.False(harness.Copy.IsFeedbackCurrent(stale));
        Assert.Empty(harness.Writer.Writes);
        Assert.Null(harness.Session.CurrentResult);
        Assert.False(harness.Execution.IsRunning);
    }

    [Fact]
    public async Task FrozenOldCandidate_ReplacedSearchAndResetSessionCannotCopy()
    {
        Harness harness = new();
        TextInputSession input = TextInputSession.Create("synthetic transcript");
        VideoSearchResult old = await harness.Search(input);
        VideoCandidateAction frozen = old.Candidates[0].CreateSelectionAction();
        VideoSearchResult current = await harness.Search(input);
        Assert.Equal(VideoClipboardCopyStatus.StaleSelection, (await harness.Copy.CopyAsync(frozen, Guid.NewGuid())).Status);
        Assert.Empty(harness.Writer.Writes);
        Assert.Same(current, harness.Session.CurrentResult);
        harness.Execution.CloseSession();
        VideoSearchResult reset = await harness.Search();
        Assert.Equal(VideoClipboardCopyStatus.StaleSelection,
            (await harness.Copy.CopyAsync(current.Candidates[0].CreateSelectionAction(), Guid.NewGuid())).Status);
        Assert.Same(reset, harness.Session.CurrentResult);
        Assert.Equal(0, harness.Dispatcher.Calls);
    }

    [Fact]
    public async Task BusyClipboard_PreservesCandidatesAndPageForAnExplicitRetryWithoutSearch()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        VideoCandidateAction selection = result.Candidates[8].CreateSelectionAction();
        Task<VideoClipboardCopyResult> first = harness.Copy.CopyAsync(selection, Guid.NewGuid());
        harness.Dispatcher.Run(VideoClipboardCopyStatus.Busy);
        VideoClipboardCopyResult failed = await first;
        Assert.Equal(VideoClipboardCopyStatus.Busy, failed.Status);
        Assert.True(failed.CanRetry);
        Assert.DoesNotContain("コピーしました", failed.Message);
        Assert.Empty(harness.Writer.Writes);
        Assert.Same(result, harness.Session.CurrentResult);
        Assert.True(harness.Session.TryGetPage(result.SessionId, result.OperationId, 1, out var page));
        Assert.Same(result.Candidates[8], page[3]);
        Assert.Equal(1, harness.Dispatcher.Calls);
        Task<VideoClipboardCopyResult> retry = harness.Copy.CopyAsync(selection, Guid.NewGuid());
        harness.Dispatcher.Run();
        Assert.Equal(VideoClipboardCopyStatus.Copied, (await retry).Status);
        Assert.Single(harness.Writer.Writes);
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task SuccessFeedback_FromAnOlderOperationCannotReplaceNewerFeedback()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        Task<VideoClipboardCopyResult> first = harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), Guid.NewGuid());
        harness.Dispatcher.Run();
        VideoClipboardCopyResult copied = await first;
        Assert.True(harness.Copy.IsFeedbackCurrent(copied));
        Task<VideoClipboardCopyResult> second = harness.Copy.CopyAsync(result.Candidates[1].CreateSelectionAction(), Guid.NewGuid());
        Assert.False(harness.Copy.IsFeedbackCurrent(copied));
        harness.Dispatcher.Run();
        Assert.True(harness.Copy.IsFeedbackCurrent(await second));
    }

    [Fact]
    public async Task AnotherSharedOperation_BlocksCopyWithoutChangingTheSearch()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var other));
        using (other)
        {
            VideoClipboardCopyResult blocked = await harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), Guid.NewGuid());
            Assert.Equal(VideoClipboardCopyStatus.Busy, blocked.Status);
            Assert.Equal(0, harness.Dispatcher.Calls);
            Assert.True(harness.Copy.IsFeedbackCurrent(blocked));
            Assert.Same(result, harness.Session.CurrentResult);
        }
        Assert.Empty(harness.Writer.Writes);
    }

    [Fact]
    public async Task CloseAndTheSynchronousWrite_CannotInterleaveAfterFinalRevalidation()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Writer.OnWrite = () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<VideoClipboardCopyResult> copy = harness.Copy.CopyAsync(result.Candidates[0].CreateSelectionAction(), Guid.NewGuid());
        Task dispatch = Task.Run(() => harness.Dispatcher.Run());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        TaskCompletionSource attemptingClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task close = Task.Run(() =>
        {
            attemptingClose.SetResult();
            harness.Execution.CloseSession();
        });
        try
        {
            await attemptingClose.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(close.IsCompleted);
        }
        finally { release.Set(); }
        await Task.WhenAll(dispatch, close, copy).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(harness.Writer.Writes);
        Assert.Equal(result.Candidates[0].WatchUrl.AbsoluteUri, harness.Writer.Writes[0]);
        Assert.False(harness.Copy.IsFeedbackCurrent(await copy));
        Assert.Null(harness.Session.CurrentResult);
    }

    [Fact]
    public async Task UnknownCandidateOrAction_AndPreCancelledClicksNeverReachDispatcher()
    {
        Harness harness = new();
        VideoSearchResult result = await harness.Search();
        VideoCandidateAction valid = result.Candidates[0].CreateSelectionAction();
        foreach (VideoCandidateAction action in new[]
        {
            valid with { CandidateId = Guid.NewGuid() }, valid with { SearchOperationId = Guid.NewGuid() },
            valid with { SessionId = Guid.NewGuid() }, valid with { Kind = (VideoCandidateActionKind)999 },
        })
        {
            Assert.Equal(VideoClipboardCopyStatus.StaleSelection, (await harness.Copy.CopyAsync(action, Guid.NewGuid())).Status);
        }
        Assert.Equal(VideoClipboardCopyStatus.Cancelled,
            (await harness.Copy.CopyAsync(valid, Guid.NewGuid(), new CancellationToken(true))).Status);
        Assert.Empty(harness.Writer.Writes);
        Assert.Equal(0, harness.Dispatcher.Calls);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Session = new VideoSearchSession(Execution);
            Copy = new VideoCandidateClipboard(Execution, Session, Dispatcher, Writer);
        }
        public ExecutionCoordinator Execution { get; } = new();
        public VideoSearchSession Session { get; }
        public DelayedDispatcher Dispatcher { get; } = new();
        public RecordingWriter Writer { get; } = new();
        public FakeProvider Provider { get; } = new();
        public VideoCandidateClipboard Copy { get; }
        public async Task<VideoSearchResult> Search(TextInputSession? input = null)
        {
            input ??= TextInputSession.Create("synthetic transcript");
            ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
            ExecutionOperation? operation;
            bool accepted = Execution.IsSessionCurrent(input.SessionId)
                ? Execution.TryBeginOperation(input.SessionId, request.CorrelationId, out operation)
                : Execution.TryBeginSession(request.CorrelationId, out operation, sessionId: input.SessionId);
            Assert.True(accepted);
            using (operation)
            {
                FeatureResult feature = await new DirectVideoSearchHandler(Provider, Session)
                    .HandleAsync(input, request, null, CancellationToken.None);
                return feature.VideoSearch!;
            }
        }
    }
    private sealed class FakeProvider : IVideoSearchProvider
    {
        public int Calls { get; private set; }
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new VideoSearchBatch(Enumerable.Range(0, 10)
                .Select(index => new VideoMetadata($"sample{index:00000}", $"Synthetic title {index}"))));
        }
    }
    private sealed class DelayedDispatcher : IVideoClipboardDispatcher
    {
        private Func<VideoClipboardCopyStatus>? _write;
        private TaskCompletionSource<VideoClipboardCopyStatus>? _pending;
        public int Calls { get; private set; }
        public Task<VideoClipboardCopyStatus> InvokeAsync(Func<VideoClipboardCopyStatus> write, CancellationToken cancellationToken)
        {
            Calls++;
            _write = write;
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }
        public VideoClipboardCopyStatus RepeatWrite() => _write!();
        public void Run(VideoClipboardCopyStatus? failure = null)
        {
            try { _pending!.SetResult(failure ?? _write!()); }
            catch (Exception exception) { _pending!.SetException(exception); }
        }
    }
    private sealed class RecordingWriter : IVideoClipboardTextWriter
    {
        public List<string> Writes { get; } = [];
        public Action? OnWrite { get; set; }
        public void SetText(string text) { OnWrite?.Invoke(); Writes.Add(text); }
    }
}
