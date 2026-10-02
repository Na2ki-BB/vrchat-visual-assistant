using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class VideoSearchFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VoiceToSearchToClipboard_UsesCatalogAndKeepsOriginalAndCandidates(bool interpreted) =>
        VrFlowTestThread.RunAsync(() =>
            VoiceToSearchToClipboard_UsesCatalogAndKeepsOriginalAndCandidatesCore(interpreted));

    private static async Task VoiceToSearchToClipboard_UsesCatalogAndKeepsOriginalAndCandidatesCore(bool interpreted)
    {
        await using SearchFlowFixture fixture = new();
        await fixture.Voice.RecordAsync();
        TextInputSession input = fixture.Voice.Flow.CurrentInput!;
        Assert.Equal(3, fixture.Catalog.Entries.Count);
        Assert.False(fixture.Catalog.TryResolve(FeatureIds.Summarization, out _));
        Assert.True(fixture.Flow.TrySearch(interpreted));
        Assert.False(fixture.Flow.TrySearch(!interpreted));
        await fixture.Flow.WhenIdle;
        await fixture.Flow.WhenImagesIdle;
        Assert.Equal(VideoSearchFlowState.Candidates, fixture.Flow.State);
        Assert.Equal(interpreted ? "synthetic interpreted query" : input.Transcript, fixture.Flow.Query);
        Assert.Same(input, fixture.Voice.Flow.CurrentInput);
        Assert.Equal(interpreted ? 1 : 0, fixture.Interpretations);
        Assert.Equal(1, fixture.Voice.Requests);
        Assert.Equal(1, fixture.Searches);
        Assert.Equal(5, fixture.Flow.Candidates.Count);
        VideoCandidate oldPageCandidate = fixture.Flow.Candidates[0];
        Assert.True(fixture.Flow.MovePage(1));
        Assert.False(fixture.Flow.MovePage(1));
        Assert.False(fixture.Flow.TryCopy(oldPageCandidate.CreateSelectionAction()));
        Assert.Equal(1, fixture.Searches);
        Assert.Equal(10, fixture.ImageRequests);
        VideoCandidate selected = fixture.Flow.Candidates[4];
        Assert.True(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        Assert.False(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        await fixture.Flow.WhenIdle;
        Assert.Equal([selected.WatchUrl.AbsoluteUri], fixture.Writes);
        Assert.Equal(VideoSearchFlowState.Candidates, fixture.Flow.State);
        Assert.Contains("コピーしました", fixture.Flow.Message, StringComparison.Ordinal);
        Assert.Equal(5, fixture.Flow.Candidates.Count);
        Assert.True(fixture.Flow.MovePage(-1));
        fixture.Flow.BackToInput();
        Assert.Empty(fixture.Flow.Candidates);
        Assert.Same(input, fixture.Voice.Flow.CurrentInput);
        Assert.False(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        Assert.Equal(1, fixture.Voice.Requests);
    }

    [Fact]
    public Task DirectSearchAndRetry_OnlyStripTerminalJapaneseFullStopFromProviderQuery() =>
        VrFlowTestThread.RunAsync(DirectSearchAndRetry_OnlyStripTerminalJapaneseFullStopFromProviderQueryCore);

    private static async Task DirectSearchAndRetry_OnlyStripTerminalJapaneseFullStopFromProviderQueryCore()
    {
        const string transcript = "内部。句点。 \r\n";
        await using SearchFlowFixture fixture = new();
        fixture.Voice.Response = (_, _) => Task.FromResult(VoiceFlowFixture.Success(transcript));
        fixture.SearchResponse = (_, _) => throw new ScanException(
            ScanFailureCode.VideoSearchTimedOut, ScanStage.TextHandling, "private provider data");
        await fixture.Voice.RecordAsync();

        Assert.True(fixture.Flow.TrySearch(false));
        await fixture.Flow.WhenIdle;
        Assert.Equal(VideoSearchFlowState.Failed, fixture.Flow.State);
        Assert.Equal(transcript, fixture.Flow.Query);
        Assert.Equal(["内部。句点 \r\n"], fixture.Queries);
        Assert.Equal(0, fixture.Interpretations);

        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(1));
        Assert.True(fixture.Flow.TrySearch(false, retry: true));
        await fixture.Flow.WhenIdle;
        Assert.Equal(VideoSearchFlowState.Candidates, fixture.Flow.State);
        Assert.Equal(transcript, fixture.Flow.Query);
        Assert.Equal(["内部。句点 \r\n", "内部。句点 \r\n"], fixture.Queries);
        Assert.Equal(transcript, fixture.Voice.Flow.CurrentInput!.Transcript);
        Assert.Equal(0, fixture.Interpretations);
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, true, 1)]
    [InlineData(5, false, 1)]
    [InlineData(6, true, 2)]
    [InlineData(10, false, 2)]
    public Task BatchSizesAndPartialMetadataArePreserved(int count, bool partial, int pages) =>
        VrFlowTestThread.RunAsync(() =>
            BatchSizesAndPartialMetadataArePreservedCore(count, partial, pages));

    private static async Task BatchSizesAndPartialMetadataArePreservedCore(int count, bool partial, int pages)
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(count, partial));
        await fixture.Voice.RecordAsync();
        Assert.True(fixture.Flow.TrySearch(false));
        await fixture.Flow.WhenIdle;
        Assert.Equal(count, fixture.Flow.Result!.Candidates.Count);
        Assert.Equal(pages, fixture.Flow.Result.PageCount);
        Assert.Equal(partial, fixture.Flow.Result.IsPartial);
        if (count == 0) { Assert.Contains("0件", fixture.Flow.Message, StringComparison.Ordinal); }
        if (partial) { Assert.Contains("一部", fixture.Flow.Message, StringComparison.Ordinal); }
        Assert.Equal(Math.Min(count, 5), fixture.Flow.Candidates.Count);
        Assert.Equal(pages > 1, fixture.Flow.CanNext);
    }

    [Fact]
    public Task FailedSearchRetry_ReusesPaidInterpretationAndTranscript() =>
        VrFlowTestThread.RunAsync(FailedSearchRetry_ReusesPaidInterpretationAndTranscriptCore);

    private static async Task FailedSearchRetry_ReusesPaidInterpretationAndTranscriptCore()
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) => throw new ScanException(ScanFailureCode.VideoSearchTimedOut, ScanStage.TextHandling, "private provider data");
        await fixture.Voice.RecordAsync();
        Assert.True(fixture.Flow.TrySearch(true));
        await fixture.Flow.WhenIdle;
        Assert.Equal(VideoSearchFlowState.Failed, fixture.Flow.State);
        Assert.True(fixture.Flow.CanRetry);
        Assert.Equal("synthetic interpreted query", fixture.Flow.Query);
        Assert.DoesNotContain("private", fixture.Flow.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Searches);
        Assert.Equal(1, fixture.Interpretations);
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch());
        Assert.True(fixture.Flow.TrySearch(false, retry: true));
        await fixture.Flow.WhenIdle;
        Assert.Equal(VideoSearchFlowState.Candidates, fixture.Flow.State);
        Assert.Equal(1, fixture.Interpretations);
        Assert.Equal(1, fixture.Voice.Requests);
        Assert.Equal(2, fixture.Searches);
        Assert.All(fixture.Queries, query => Assert.Equal("synthetic interpreted query", query));
    }

    [Theory]
    [InlineData(ScanFailureCode.SearchInterpretationNotConfigured)]
    [InlineData(ScanFailureCode.SearchInterpretationAuthenticationFailed)]
    [InlineData(ScanFailureCode.SearchInterpretationUsageLimitReached)]
    [InlineData(ScanFailureCode.SearchInterpretationRateLimited)]
    [InlineData(ScanFailureCode.SearchInterpretationTimedOut)]
    [InlineData(ScanFailureCode.SearchInterpretationInvalidResponse)]
    public Task InterpretationFailuresHaveNoImplicitFallbackOrAutomaticRetry(ScanFailureCode code) =>
        VrFlowTestThread.RunAsync(() =>
            InterpretationFailuresHaveNoImplicitFallbackOrAutomaticRetryCore(code));

    private static async Task InterpretationFailuresHaveNoImplicitFallbackOrAutomaticRetryCore(ScanFailureCode code)
    {
        await using SearchFlowFixture fixture = new();
        fixture.InterpretResponse = (_, _) => throw new ScanException(code, ScanStage.SearchInterpretation, "private response");
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(true);
        await fixture.Flow.WhenIdle;
        Assert.Equal(code, fixture.Flow.FailureCode);
        Assert.Equal(ScanStage.SearchInterpretation, fixture.Flow.FailureStage);
        Assert.Equal(0, fixture.Searches);
        Assert.Equal(1, fixture.Interpretations);
        Assert.DoesNotContain("private", fixture.Flow.Message, StringComparison.Ordinal);
        fixture.InterpretResponse = (_, _) => Task.FromResult(new SearchQueryInterpretation("recovered query"));
        Assert.True(fixture.Flow.TrySearch(false, retry: true));
        await fixture.Flow.WhenIdle;
        Assert.Equal(2, fixture.Interpretations);
        Assert.Equal(1, fixture.Searches);
        Assert.Equal("recovered query", fixture.Flow.Query);
    }

    [Fact]
    public Task CancelKeepsGateUntilCleanup_ThenAllowsRawTranscriptRecovery() =>
        VrFlowTestThread.RunAsync(CancelKeepsGateUntilCleanup_ThenAllowsRawTranscriptRecoveryCore);

    private static async Task CancelKeepsGateUntilCleanup_ThenAllowsRawTranscriptRecoveryCore()
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SearchResponse = (_, _) => { started.TrySetResult(); return cleanup.Task; };
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(true);
        await started.Task;
        fixture.Flow.Cancel();
        Assert.Equal(VideoSearchFlowState.Cancelling, fixture.Flow.State);
        Assert.True(fixture.Voice.Execution.IsRunning);
        Assert.False(fixture.Flow.TrySearch(false));
        Assert.False(fixture.Voice.Flow.TryStart());
        Assert.False(fixture.Voice.Execution.TryBeginSession(Guid.NewGuid(), out _));
        cleanup.SetResult(SearchFlowFixture.Batch()); // late success cannot publish
        await fixture.Flow.WhenIdle;
        Assert.False(fixture.Voice.Execution.IsRunning);
        Assert.Equal(VideoSearchFlowState.Input, fixture.Flow.State);
        Assert.Null(fixture.Flow.Result);
        Assert.NotNull(fixture.Voice.Flow.CurrentInput);
        fixture.SearchResponse = (_, _) => Task.FromResult(SearchFlowFixture.Batch(1));
        Assert.True(fixture.Flow.TrySearch(false));
        await fixture.Flow.WhenIdle;
        Assert.Single(fixture.Flow.Candidates);
        Assert.Equal(1, fixture.Voice.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task CloseOrRerecordRejectsLateImagesAndOldSelections(bool close) =>
        VrFlowTestThread.RunAsync(() =>
            CloseOrRerecordRejectsLateImagesAndOldSelectionsCore(close));

    private static async Task CloseOrRerecordRejectsLateImagesAndOldSelectionsCore(bool close)
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource<VideoThumbnailResult> images = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ImageResponse = (_, _) => images.Task;
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await fixture.Flow.WhenIdle;
        VideoCandidate old = fixture.Flow.Candidates[0];
        Task closing = Task.CompletedTask;
        if (close) { closing = fixture.Flow.CloseAsync(); }
        else
        {
            fixture.Voice.NextMicrophone();
            await fixture.Voice.RecordAsync();
            fixture.Flow.Refresh();
        }
        Assert.False(fixture.Flow.TryCopy(old.CreateSelectionAction()));
        Assert.Null(fixture.Flow.ImageFor(old));
        images.SetResult(new(VideoThumbnailStatus.Available, new VideoThumbnailImage(1, 1, new byte[] { 0, 0, 0, 255 })));
        await fixture.Flow.WhenImagesIdle;
        await closing;
        Assert.Null(fixture.Flow.ImageFor(old));
        Assert.Null(fixture.Flow.Result);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public Task CloseDuringSearchDrainsOwnerAndNeverReopensCandidateView() =>
        VrFlowTestThread.RunAsync(CloseDuringSearchDrainsOwnerAndNeverReopensCandidateViewCore);

    private static async Task CloseDuringSearchDrainsOwnerAndNeverReopensCandidateViewCore()
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SearchResponse = (_, _) => { started.SetResult(); return response.Task; };
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await started.Task;
        Task closing = fixture.Flow.CloseAsync();
        Assert.False(closing.IsCompleted);
        Assert.True(fixture.Voice.Execution.IsRunning);
        response.SetResult(SearchFlowFixture.Batch());
        await closing;
        Assert.Equal(VideoSearchFlowState.Closed, fixture.Flow.State);
        Assert.Null(fixture.Flow.Result);
        Assert.Equal(0, fixture.ImageRequests);
    }

    [Fact]
    public Task CopyFailureIsExplicitlyRetryableAndKeepsCandidates_NoDuplicateWrites() =>
        VrFlowTestThread.RunAsync(CopyFailureIsExplicitlyRetryableAndKeepsCandidates_NoDuplicateWritesCore);

    private static async Task CopyFailureIsExplicitlyRetryableAndKeepsCandidates_NoDuplicateWritesCore()
    {
        await using SearchFlowFixture fixture = new();
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await fixture.Flow.WhenIdle;
        VideoCandidate selected = fixture.Flow.Candidates[0];
        fixture.ClipboardStatus = VideoClipboardCopyStatus.Busy;
        Assert.True(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        Assert.False(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        await fixture.Flow.WhenIdle;
        Assert.Empty(fixture.Writes);
        Assert.Equal(VideoSearchFlowState.Candidates, fixture.Flow.State);
        Assert.Contains("もう一度", fixture.Flow.Message, StringComparison.Ordinal);
        fixture.ClipboardStatus = VideoClipboardCopyStatus.Copied;
        Assert.True(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        Assert.False(fixture.Flow.TryCopy(selected.CreateSelectionAction()));
        await fixture.Flow.WhenIdle;
        Assert.Single(fixture.Writes);
        Assert.Equal(1, fixture.Searches);
    }

    [Fact]
    public Task CloseBeforeQueuedClipboardCallbackPreventsWriteAndDrainsCopy() =>
        VrFlowTestThread.RunAsync(CloseBeforeQueuedClipboardCallbackPreventsWriteAndDrainsCopyCore);

    private static async Task CloseBeforeQueuedClipboardCallbackPreventsWriteAndDrainsCopyCore()
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource dispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DispatchCopy = async (write, cancellation) =>
        {
            await dispatch.Task;
            cancellation.ThrowIfCancellationRequested();
            return write();
        };
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await fixture.Flow.WhenIdle;
        Assert.True(fixture.Flow.TryCopy(fixture.Flow.Candidates[0].CreateSelectionAction()));
        Assert.True(fixture.Voice.Execution.IsRunning);
        Assert.False(fixture.Flow.TryCopy(fixture.Flow.Candidates[0].CreateSelectionAction()));
        Task close = fixture.Flow.CloseAsync();
        Assert.False(close.IsCompleted);
        dispatch.SetResult();
        await close;
        Assert.False(fixture.Voice.Execution.IsRunning);
        Assert.Empty(fixture.Writes);
        Assert.Equal(VideoSearchFlowState.Closed, fixture.Flow.State);
    }

    [Fact]
    public Task TerminalCleanupFailureSurvivesSessionInvalidationAndDisablesAllWork() =>
        VrFlowTestThread.RunAsync(TerminalCleanupFailureSurvivesSessionInvalidationAndDisablesAllWorkCore);

    private static async Task TerminalCleanupFailureSurvivesSessionInvalidationAndDisablesAllWorkCore()
    {
        await using SearchFlowFixture fixture = new();
        fixture.SearchResponse = (_, _) =>
        {
            fixture.Voice.Execution.Stop();
            throw new ScanException(ScanFailureCode.VideoSearchCleanupFailed, ScanStage.TextHandling, "private diagnostic");
        };
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await fixture.Flow.WhenIdle;
        Assert.True(fixture.Flow.RequiresRestart);
        Assert.Contains("再起動", fixture.Flow.Message, StringComparison.Ordinal);
        Assert.False(fixture.Flow.CanSearch);
        Assert.False(fixture.Flow.CanRetry);
        Assert.Null(fixture.Flow.Result);
        fixture.Flow.Refresh();
        Assert.True(fixture.Flow.RequiresRestart);
    }

    [Fact]
    public Task TerminalCleanupFailureAfterUserCloseStillRequiresAppRestart() =>
        VrFlowTestThread.RunAsync(TerminalCleanupFailureAfterUserCloseStillRequiresAppRestartCore);

    private static async Task TerminalCleanupFailureAfterUserCloseStillRequiresAppRestartCore()
    {
        await using SearchFlowFixture fixture = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<VideoSearchBatch> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SearchResponse = (_, _) => { started.SetResult(); return response.Task; };
        await fixture.Voice.RecordAsync();
        fixture.Flow.TrySearch(false);
        await started.Task;
        Task closing = fixture.Flow.CloseAsync();
        fixture.Voice.Execution.Stop();
        response.SetException(new ScanException(ScanFailureCode.VideoSearchCleanupFailed, ScanStage.TextHandling, "private cleanup"));
        await closing;
        Assert.True(fixture.Flow.RequiresRestart);
        Assert.Equal(VideoSearchFlowState.Failed, fixture.Flow.State);
        Assert.Contains("再起動", fixture.Flow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public Task InvalidSettingsPreventsSearchAndPaidInterpretation() =>
        VrFlowTestThread.RunAsync(InvalidSettingsPreventsSearchAndPaidInterpretationCore);

    private static async Task InvalidSettingsPreventsSearchAndPaidInterpretationCore()
    {
        await using SearchFlowFixture fixture = new();
        await fixture.Voice.RecordAsync();
        fixture.FailPrepare = true;
        fixture.Flow.TrySearch(true);
        await fixture.Flow.WhenIdle;
        Assert.Equal(ScanStage.Trigger, fixture.Flow.FailureStage);
        Assert.Equal(0, fixture.Searches);
        Assert.Equal(0, fixture.Interpretations);
        fixture.FailPrepare = false;
        Assert.True(fixture.Flow.TrySearch(false, retry: true));
        await fixture.Flow.WhenIdle;
        Assert.Equal(1, fixture.Interpretations);
    }

    [Fact]
    public async Task CredentialWrapperUsesFreshExplicitStoreAndIndependentQuotaOnly()
    {
        FeatureUsageQuotas quotas = new();
        int reads = 0;
        string? secret = null;
        SearchHttpHandler transport = new();
        using HttpClient http = new(transport);
        VideoSearchRuntime.CurrentCredentialInterpreter interpreter = new(http, quotas.SearchInterpretation, () => { reads++; return secret; });
        TextInputSession input = new(Guid.NewGuid(), "synthetic input");
        ScanException missing = await Assert.ThrowsAsync<ScanException>(() => interpreter.InterpretAsync(input, CancellationToken.None));
        Assert.Equal(ScanFailureCode.SearchInterpretationNotConfigured, missing.FailureCode);
        Assert.Equal(0, transport.Requests);
        secret = "synthetic-text-key";
        SearchQueryInterpretation result = await interpreter.InterpretAsync(input, CancellationToken.None);
        Assert.Equal("synthetic query", result.Query);
        Assert.Equal(2, reads);
        Assert.Equal(secret, transport.Key);
        Assert.Equal(1, quotas.SearchInterpretation.Consumed);
        Assert.Equal(0, quotas.Translation.Consumed);
        Assert.Equal(0, quotas.Voice.ConsumedRequests);
    }

    private sealed class SearchHttpHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Key { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Key = request.Headers.Authorization?.Parameter;
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"synthetic query\"}]}]}", Encoding.UTF8, "application/json"),
            });
        }
    }
}

internal sealed class SearchFlowFixture : IVideoSearchProvider, ISearchQueryInterpreter, IVideoThumbnailProvider,
    IVideoClipboardDispatcher, IVideoClipboardTextWriter, IAsyncDisposable
{
    public SearchFlowFixture()
    {
        Session = new(Voice.Execution);
        Catalog = VideoSearchRuntime.CreateCatalog(new RejectAnalyzer(), this, this, Session);
        Clipboard = new(Voice.Execution, Session, this, this);
        Flow = new(Voice.Execution, Session, Catalog, () => Voice.Flow.CurrentInput,
            () => { if (FailPrepare) { throw new IOException("private settings"); } }, this, Clipboard);
    }
    public VoiceFlowFixture Voice { get; } = new();
    public VideoSearchSession Session { get; }
    public FeatureCatalog Catalog { get; }
    public VideoCandidateClipboard Clipboard { get; }
    public VideoSearchFlow Flow { get; }
    public bool FailPrepare { get; set; }
    public int Searches { get; private set; }
    public int Interpretations { get; private set; }
    public int ImageRequests { get; private set; }
    public List<string> Queries { get; } = [];
    public List<string> Writes { get; } = [];
    public VideoClipboardCopyStatus ClipboardStatus { get; set; } = VideoClipboardCopyStatus.Copied;
    public Func<VideoSearchRequest, CancellationToken, Task<VideoSearchBatch>> SearchResponse { get; set; } = (_, _) => Task.FromResult(Batch());
    public Func<TextInputSession, CancellationToken, Task<SearchQueryInterpretation>> InterpretResponse { get; set; } =
        (_, _) => Task.FromResult(new SearchQueryInterpretation("synthetic interpreted query"));
    public Func<Uri?, CancellationToken, Task<VideoThumbnailResult>> ImageResponse { get; set; } =
        (_, _) => Task.FromResult(new VideoThumbnailResult(VideoThumbnailStatus.Missing));
    public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
    { Searches++; Queries.Add(request.Query); return SearchResponse(request, cancellationToken); }
    public Task<SearchQueryInterpretation> InterpretAsync(TextInputSession input, CancellationToken cancellationToken)
    { Interpretations++; return InterpretResponse(input, cancellationToken); }
    public Task<VideoThumbnailResult> LoadAsync(Uri? uri, CancellationToken cancellationToken)
    { ImageRequests++; return ImageResponse(uri, cancellationToken); }
    public Func<Func<VideoClipboardCopyStatus>, CancellationToken, Task<VideoClipboardCopyStatus>>? DispatchCopy { get; set; }
    public Task<VideoClipboardCopyStatus> InvokeAsync(Func<VideoClipboardCopyStatus> write, CancellationToken cancellationToken) =>
        DispatchCopy?.Invoke(write, cancellationToken)
        ?? Task.FromResult(ClipboardStatus == VideoClipboardCopyStatus.Copied ? write() : ClipboardStatus);
    public void SetText(string text) => Writes.Add(text);
    public static VideoSearchBatch Batch(int count = 10, bool partial = false, string? title = null) => new(
        Enumerable.Range(0, count).Select(index => new VideoMetadata($"video{index:000000}", title ?? $"synthetic title {index}")), partial);
    public async ValueTask DisposeAsync() { await Flow.DisposeAsync(); await Voice.DisposeAsync(); }
    private sealed class RejectAnalyzer : IAnalyzer
    {
        public Task<AnalysisResult> AnalyzeAsync(CapturedFrame frame, ScanRequest request, IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Search must not capture or OCR.");
    }
}
