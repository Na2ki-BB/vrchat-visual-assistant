namespace VrcVa.Core.Tests;

public sealed class DirectVideoSearchHandlerTests
{
    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(5, 1, 5, 0)]
    [InlineData(6, 2, 5, 1)]
    [InlineData(10, 2, 5, 5)]
    public async Task DirectSearch_UsesOneVerbatimQueryAndMemoryPages(int count, int pages, int firstCount, int secondCount)
    {
        const string transcript = "  架空の作品\r\n2026年のライブを探して\t--sample  ";
        Harness harness = new(count);
        TextInputSession input = TextInputSession.Create(transcript);
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);

        ScanOutcome outcome = await harness.Pipeline.RunAsync(request);

        Assert.True(outcome.IsSuccess);
        VideoSearchResult result = Assert.IsType<VideoSearchResult>(outcome.Result!.FeatureResult.VideoSearch);
        Assert.Same(result, harness.Session.CurrentResult);
        Assert.Equal(transcript, input.Transcript);
        Assert.Equal(transcript, harness.Provider.Request!.Query);
        Assert.Equal(transcript, result.Query);
        Assert.Equal(transcript, outcome.Result.SourceText);
        Assert.Equal(transcript, outcome.Result.PrimarySection.Text);
        Assert.Equal(input.SessionId, result.SessionId);
        Assert.Equal(request.CorrelationId, result.OperationId);
        Assert.Equal(10, harness.Provider.Request.CandidateLimit);
        Assert.Equal(pages, result.PageCount);
        Assert.Equal(count, result.Candidates.Count);
        Assert.Equal(firstCount, result.GetPage(0).Count);
        Assert.Equal("none", outcome.Result.CaptureSourceKind);
        Assert.Null(outcome.Result.TextModelMetadata);
        Assert.Equal(TimeSpan.Zero, outcome.Result.OcrDuration);
        Assert.Equal(TimeSpan.Zero, outcome.Result.TranslationDuration);
        Assert.Equal(0, harness.Capture.Calls);
        Assert.Equal(0, harness.Ocr.Calls);
        Assert.Equal(0, harness.Ai.Calls);
        Assert.Equal(FeatureDataBoundary.SearchTextToYouTube, BuiltInFeatures.DirectVideoSearch.DataBoundary);
        Assert.False(harness.Execution.IsRunning);
        Assert.True(harness.Session.TryGetPage(result.SessionId, result.OperationId, 0, out var first));
        Assert.Equal(firstCount, first!.Count);
        if (pages == 2)
        {
            Assert.Equal(secondCount, result.GetPage(1).Count);
            Assert.True(harness.Session.TryGetPage(result.SessionId, result.OperationId, 1, out var second));
            Assert.Equal(secondCount, second!.Count);
            Assert.Equal(result.Candidates.Skip(5), second);
            Assert.Equal(first, result.GetPage(0));
        }
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(count, result.Candidates.Select(candidate => candidate.CandidateId).Distinct().Count());
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Equal(input.SessionId, candidate.SessionId);
            Assert.Equal(request.CorrelationId, candidate.SearchOperationId);
            Assert.NotEqual(Guid.Empty, candidate.CandidateId);
            Assert.Equal($"https://www.youtube.com/watch?v={candidate.VideoId}", candidate.WatchUrl.AbsoluteUri);
            Assert.True(harness.Session.TryResolveSelection(candidate.CreateSelectionAction(), out var selected));
            Assert.Same(candidate, selected);
        });
        Assert.DoesNotContain(harness.Renderer.Progress, value => value.Stage is ScanStage.Capture or ScanStage.Ocr);
    }

    [Theory]
    [InlineData("猫。", "猫")]
    [InlineData("猫。犬。", "猫。犬")]
    [InlineData("猫。。", "猫。")]
    [InlineData("cat.", "cat.")]
    [InlineData("猫。 \r\n", "猫 \r\n")]
    [InlineData("猫. \r\n", "猫. \r\n")]
    [InlineData("猫！", "猫！")]
    public async Task DirectSearch_StripsOnlyTerminalJapaneseFullStopsFromProviderQuery(
        string transcript, string expectedProviderQuery)
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create(transcript);
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);

        ScanOutcome outcome = await harness.Pipeline.RunAsync(request);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(expectedProviderQuery, harness.Provider.Request!.Query);
        Assert.Equal(transcript, input.Transcript);
        Assert.Equal(transcript, outcome.Result!.SourceText);
        Assert.Equal(transcript, outcome.Result.PrimarySection.Text);
        Assert.Equal(transcript, outcome.Result.FeatureResult.VideoSearch!.Query);
        Assert.Equal(0, harness.Ai.Calls);
    }

    [Theory]
    [InlineData("。")]
    [InlineData("  。")]
    [InlineData("。 \r\n")]
    public async Task DirectSearch_TerminalFullStopRemovalCannotCreateEmptyProviderQuery(string transcript)
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create(transcript);
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);

        ScanOutcome outcome = await harness.Pipeline.RunAsync(request);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ScanFailureCode.Unexpected, outcome.Failure?.Code);
        Assert.Equal(0, harness.Provider.Calls);
        Assert.Null(harness.Session.CurrentResult);
        Assert.Equal(transcript, input.Transcript);
    }

    [Fact]
    public async Task PartialProviderBatch_PropagatesThroughTypedSnapshotAndCompatibilityProjection()
    {
        Harness harness = new(1);
        harness.Provider.Batch = new VideoSearchBatch(CreateBatch(1).Videos, isPartial: true);
        VideoSearchResult result = await harness.Search();
        Assert.True(result.IsPartial);
        FeatureResult feature = new(FeatureIds.DirectVideoSearch,
            [new ResultSection("query", "Query", "synthetic", ResultSectionRole.Primary)], videoSearch: result);
        Assert.True(new AnalysisResult(feature).FeatureResult.VideoSearch!.IsPartial);
        Assert.Single(result.GetPage(0));
    }

    [Fact]
    public async Task Selection_RejectsUnknownActionAndAllStaleOrForgedIdentities()
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        VideoCandidateAction action = result.Candidates[0].CreateSelectionAction();
        foreach (VideoCandidateAction invalid in new[]
        {
            action with { Kind = (VideoCandidateActionKind)0 },
            action with { Kind = (VideoCandidateActionKind)99 },
            action with { SessionId = Guid.Empty },
            action with { SessionId = Guid.NewGuid() },
            action with { SearchOperationId = Guid.Empty },
            action with { SearchOperationId = Guid.NewGuid() },
            action with { CandidateId = Guid.Empty },
            action with { CandidateId = Guid.NewGuid() },
        })
        {
            Assert.False(harness.Session.TryResolveSelection(invalid, out var selected));
            Assert.Null(selected);
        }
        Assert.Throws<ArgumentNullException>(() => harness.Session.TryResolveSelection(null!, out _));
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task Pages_RejectUnknownSessionSearchAndOutsideBatchWithoutFetching()
    {
        Harness harness = new(6);
        VideoSearchResult result = await harness.Search();
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => result.GetPage(2));
        Assert.False(harness.Session.TryGetPage(result.SessionId, result.OperationId, -1, out _));
        Assert.False(harness.Session.TryGetPage(result.SessionId, result.OperationId, 2, out _));
        Assert.False(harness.Session.TryGetPage(Guid.NewGuid(), result.OperationId, 0, out _));
        Assert.False(harness.Session.TryGetPage(result.SessionId, Guid.NewGuid(), 0, out _));
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task NewSearch_InvalidatesOldCandidatesBeforeAwaitAndRecreatesIdentities()
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create("same transcript");
        VideoSearchResult oldResult = await harness.Search(input);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Provider.Operation = (_, _) => { entered.SetResult(); return finish.Task; };
        ScanRequest nextRequest = ScanRequest.CreateText("again", FeatureIds.DirectVideoSearch, input);
        Assert.True(harness.Execution.TryBeginOperation(input.SessionId, nextRequest.CorrelationId, out var operation));
        using (operation)
        {
            Task<ScanOutcome> pending = harness.Pipeline.RunAsync(nextRequest, operation);
            await entered.Task;
            Assert.Null(harness.Session.CurrentResult);
            Assert.False(harness.Session.TryResolveSelection(oldResult.Candidates[0].CreateSelectionAction(), out _));
            Assert.False(harness.Session.TryGetPage(oldResult.SessionId, oldResult.OperationId, 0, out _));
            finish.SetResult(CreateBatch(1));
            ScanOutcome outcome = await pending;
            VideoSearchResult result = outcome.Result!.FeatureResult.VideoSearch!;
            Assert.NotEqual(oldResult.OperationId, result.OperationId);
            Assert.NotEqual(oldResult.Candidates[0].CandidateId, result.Candidates[0].CandidateId);
            Assert.Equal(oldResult.Candidates[0].VideoId, result.Candidates[0].VideoId);
            Assert.False(harness.Session.TryResolveSelection(oldResult.Candidates[0].CreateSelectionAction(), out _));
            Assert.True(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
        }
        Assert.Equal("same transcript", input.Transcript);
        Assert.Equal(2, harness.Provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RerecordOrClose_RejectsOldCandidates(bool close)
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        if (close) { harness.Execution.CloseSession(); }
        else
        {
            Assert.True(harness.Execution.TryBeginSession(Guid.NewGuid(), out var rerecord));
            rerecord.Dispose();
        }
        Assert.Null(harness.Session.CurrentResult);
        Assert.False(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
        Assert.False(harness.Session.TryGetPage(result.SessionId, result.OperationId, 0, out _));
    }

    [Fact]
    public async Task CopyAdmission_DoesNotInvalidateCandidatesAndResolutionCanBeRechecked()
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        VideoCandidateAction action = result.Candidates[0].CreateSelectionAction();
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var copy));
        using (copy)
        {
            Assert.True(harness.Session.TryResolveSelection(action, out var candidate));
            Assert.Same(result.Candidates[0], candidate);
        }
        Assert.Same(result, harness.Session.CurrentResult);
        Assert.True(harness.Session.TryResolveSelection(action, out _));
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task CoordinatorInvalidation_RejectsSelectionBeforeCancellationTokenCatchesUp()
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create("transcript");
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
        Assert.True(harness.Execution.TryBeginSession(request.CorrelationId, out var operation, sessionId: input.SessionId));
        using (operation)
        {
            ScanOutcome outcome = await harness.Pipeline.RunAsync(request, operation);
            VideoSearchResult result = outcome.Result!.FeatureResult.VideoSearch!;
            Assert.True(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
            // Pause only the private cancellation lock to exercise the deliberate gap between
            // coordinator invalidation and adapter callbacks, without changing production APIs.
            object cancellationSync = typeof(ExecutionOperation).GetField("_cancellationSync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(operation)!;
            Task cancel;
            lock (cancellationSync)
            {
                long generation = harness.Execution.CurrentGeneration;
                cancel = Task.Run(harness.Execution.CancelCurrentOperation);
                Assert.True(SpinWait.SpinUntil(() => harness.Execution.CurrentGeneration != generation, TimeSpan.FromSeconds(5)));
                Assert.False(operation.IsCurrent);
                Assert.False(operation.CancellationToken.IsCancellationRequested);
                Assert.False(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
                Assert.Null(harness.Session.CurrentResult);
            }
            await cancel;
        }
    }

    [Fact]
    public async Task InvalidationAfterReleasedSearch_IsImmediateAndCannotResurrectOnLaterOperation()
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        VideoCandidateAction action = result.Candidates[0].CreateSelectionAction();
        Assert.False(harness.Execution.IsRunning);
        harness.Execution.CancelCurrentOperation();
        // Begin another ordinary operation before observing state: old snapshots still stay invalid.
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var later));
        using (later)
        {
            Assert.Null(harness.Session.CurrentResult);
            Assert.False(harness.Session.TryResolveSelection(action, out _));
            Assert.False(harness.Session.TryGetPage(result.SessionId, result.OperationId, 0, out _));
        }
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task CancelledCopy_InvalidatesOldCandidatesEvenWithoutIntermediateRead()
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var copy));
        harness.Execution.CancelCurrentOperation();
        copy.Dispose();
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var later));
        using (later)
        {
            Assert.False(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
            Assert.Null(harness.Session.CurrentResult);
        }
    }

    [Fact]
    public async Task LinkedCopyCancellation_InvalidatesCandidatesImmediatelyBeforeOwnerRelease()
    {
        Harness harness = new(1);
        VideoSearchResult result = await harness.Search();
        using CancellationTokenSource cancellation = new();
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var copy, cancellation.Token));
        using (copy)
        {
            cancellation.Cancel();
            Assert.True(harness.Execution.IsRunning);
            Assert.False(copy.IsCurrent);
            Assert.False(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
            Assert.Null(harness.Session.CurrentResult);
        }
        Assert.True(harness.Execution.TryBeginOperation(result.SessionId, Guid.NewGuid(), out var later));
        using (later)
        {
            Assert.False(harness.Session.TryResolveSelection(result.Candidates[0].CreateSelectionAction(), out _));
        }
    }

    [Fact]
    public async Task CancelledSearch_DiscardsLateProviderResultAndKeepsGateUntilReturn()
    {
        Harness harness = new(1);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Provider.Operation = (_, _) => { entered.SetResult(); return finish.Task; };
        Task<ScanOutcome> pending = harness.Pipeline.RunAsync(ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, TextInputSession.Create("transcript")));
        await entered.Task;
        harness.Execution.CancelCurrentOperation();
        Assert.True(harness.Execution.IsRunning);
        ScanOutcome busy = await harness.Pipeline.RunAsync(ScanRequest.Create("image"));
        Assert.Equal(ScanFailureCode.Busy, busy.Failure!.Code);
        finish.SetResult(CreateBatch(1));
        ScanOutcome cancelled = await pending;
        Assert.Equal(ScanFailureCode.Cancelled, cancelled.Failure!.Code);
        Assert.Null(harness.Session.CurrentResult);
        Assert.Empty(harness.Renderer.Outcomes);
        Assert.False(harness.Execution.IsRunning);
    }

    [Fact]
    public async Task CancellationAfterHandlerBeforePresentation_InvalidatesPublishedCandidates()
    {
        Harness harness = new(1);
        harness.Renderer.OnOutcome = () => harness.Execution.CancelCurrentOperation();
        ScanOutcome outcome = await harness.Pipeline.RunAsync(ScanRequest.CreateText(
            "test", FeatureIds.DirectVideoSearch, TextInputSession.Create("transcript")));
        Assert.Equal(ScanFailureCode.Cancelled, outcome.Failure!.Code);
        Assert.Null(harness.Session.CurrentResult);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.False(harness.Execution.IsRunning);
    }

    [Fact]
    public async Task ProviderFailure_DoesNotRestoreOldCandidatesOrRetry()
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create("transcript");
        VideoSearchResult old = await harness.Search(input);
        harness.Provider.Operation = (_, _) => throw new InvalidOperationException("synthetic provider failure");
        ScanRequest request = ScanRequest.CreateText("again", FeatureIds.DirectVideoSearch, input);
        Assert.True(harness.Execution.TryBeginOperation(input.SessionId, request.CorrelationId, out var operation));
        using (operation)
        {
            ScanOutcome failed = await harness.Pipeline.RunAsync(request, operation);
            Assert.Equal(ScanFailureCode.Unexpected, failed.Failure!.Code);
        }
        Assert.Null(harness.Session.CurrentResult);
        Assert.False(harness.Session.TryResolveSelection(old.Candidates[0].CreateSelectionAction(), out _));
        Assert.Equal(2, harness.Provider.Calls);
        Assert.Equal("transcript", input.Transcript);
    }

    [Fact]
    public async Task Handler_RejectsInactiveStaleMismatchedAndRepeatedOperationBeforeSearch()
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create("transcript");
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Handler.HandleAsync(input, request, null, default));
        Assert.True(harness.Execution.TryBeginSession(request.CorrelationId, out var operation, sessionId: input.SessionId));
        using (operation)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Handler.HandleAsync(input, request with { FeatureId = FeatureIds.Translation }, null, default));
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Handler.HandleAsync(new TextInputSession(input.SessionId, "different"), request, null, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Handler.HandleAsync(input, request with { CorrelationId = Guid.NewGuid() }, null, default));
            await harness.Handler.HandleAsync(input, request, null, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Handler.HandleAsync(input, request, null, default));
        }
        Assert.Equal(1, harness.Provider.Calls);
        harness.Execution.CloseSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Handler.HandleAsync(input, request, null, default));
        Assert.Equal(1, harness.Provider.Calls);
    }

    [Fact]
    public async Task PreCancelledSearch_DoesNotCallProvider()
    {
        Harness harness = new(1);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ScanOutcome cancelled = await harness.Pipeline.RunAsync(ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, TextInputSession.Create("transcript")), cancellation.Token);
        Assert.Equal(ScanFailureCode.Cancelled, cancelled.Failure!.Code);
        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task TypedResult_SurvivesCompatibilityProjectionAndPreservesSearchIdentityInEquality()
    {
        Harness harness = new(1);
        TextInputSession input = TextInputSession.Create("transcript");
        VideoSearchResult first = await harness.Search(input);
        FeatureResult feature = new(FeatureIds.DirectVideoSearch,
        [new ResultSection("query", "Query", "same", ResultSectionRole.Primary)], videoSearch: first);
        AnalysisResult legacy = new(feature);
        Assert.Same(first, legacy.FeatureResult.VideoSearch);
        Assert.Same(first, (legacy with { Warning = "warning" }).FeatureResult.VideoSearch);
        Assert.Equal(legacy, new AnalysisResult(feature));
        Assert.Equal(legacy.GetHashCode(), new AnalysisResult(feature).GetHashCode());
        VideoSearchResult second = await harness.Search(input);
        AnalysisResult different = new(new FeatureResult(FeatureIds.DirectVideoSearch, feature.Sections, videoSearch: second));
        Assert.NotEqual(legacy, different);
    }

    private static VideoSearchBatch CreateBatch(int count) => new(Enumerable.Range(0, count)
        .Select(index => new VideoMetadata($"sample{index:00000}", $"Synthetic video {index}")));

    private sealed class Harness
    {
        public Harness(int count)
        {
            Provider.Batch = CreateBatch(count);
            Handler = new DirectVideoSearchHandler(Provider, Session);
            Pipeline = new ScanPipeline(Capture, new FeatureCatalog(
                new FeatureEntry(BuiltInFeatures.Translation, new SummarizeAnalyzer(Ocr, Ai, "fake-model")),
                new FeatureEntry(BuiltInFeatures.DirectVideoSearch, Handler)), Renderer, execution: Execution);
        }
        public ExecutionCoordinator Execution { get; } = new();
        public VideoSearchSession Session => _session ??= new VideoSearchSession(Execution);
        private VideoSearchSession? _session;
        public CountingCapture Capture { get; } = new();
        public CountingOcr Ocr { get; } = new();
        public CountingAi Ai { get; } = new();
        public FakeProvider Provider { get; } = new();
        public RecordingRenderer Renderer { get; } = new();
        public DirectVideoSearchHandler Handler { get; }
        public ScanPipeline Pipeline { get; }
        public async Task<VideoSearchResult> Search(TextInputSession? input = null)
        {
            input ??= TextInputSession.Create("transcript");
            ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, input);
            ScanOutcome outcome;
            if (Execution.IsSessionCurrent(input.SessionId))
            {
                Assert.True(Execution.TryBeginOperation(input.SessionId, request.CorrelationId, out var operation));
                using (operation) { outcome = await Pipeline.RunAsync(request, operation); }
            }
            else { outcome = await Pipeline.RunAsync(request); }
            Assert.True(outcome.IsSuccess);
            return outcome.Result!.FeatureResult.VideoSearch!;
        }
    }
    private sealed class FakeProvider : IVideoSearchProvider
    {
        public int Calls { get; private set; }
        public VideoSearchRequest? Request { get; private set; }
        public VideoSearchBatch Batch { get; set; } = new([]);
        public Func<VideoSearchRequest, CancellationToken, Task<VideoSearchBatch>>? Operation { get; set; }
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Operation?.Invoke(request, cancellationToken) ?? Task.FromResult(Batch);
        }
    }
    private sealed class CountingCapture : ICaptureSource
    {
        public int Calls { get; private set; }
        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Capture must not run."); }
    }
    private sealed class CountingOcr : IOcrEngine
    {
        public int Calls { get; private set; }
        public Task<OcrOutput> RecognizeAsync(CapturedFrame frame, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("OCR must not run."); }
    }
    private sealed class CountingAi : ITextModelClient
    {
        public int Calls { get; private set; }
        public Task<TextModelResponse> GenerateAsync(TextModelRequest request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("AI must not run."); }
    }
    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanProgress> Progress { get; } = [];
        public List<ScanOutcome> Outcomes { get; } = [];
        public Action? OnOutcome { get; set; }
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken)
        { Progress.Add(progress); return Task.CompletedTask; }
        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken)
        { Outcomes.Add(outcome); OnOutcome?.Invoke(); return Task.CompletedTask; }
    }
}
