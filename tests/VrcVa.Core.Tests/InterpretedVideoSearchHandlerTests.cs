namespace VrcVa.Core.Tests;

public sealed class InterpretedVideoSearchHandlerTests
{
    [Fact]
    public async Task Search_RetainsOriginalAndSeparatelyValidatedQuery()
    {
        Harness h = new();
        FeatureResult result = await h.Run();
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(1, h.Provider.Calls);
        Assert.Equal(h.Input.Transcript, result.Sections[0].Text);
        Assert.Equal(h.Input.Transcript, h.Interpreter.Input!.Transcript);
        Assert.Equal("架空曲 2024 ライブ", result.VideoSearch!.Query);
        Assert.Equal("架空曲 2024 ライブ", h.Session.CurrentInterpretedQuery!.Query);
        Assert.Null(h.Session.RetryableSearchOperationId);
        Assert.Equal(FeatureIds.InterpretedVideoSearch, result.FeatureId);
        Assert.Equal(FeatureDataBoundary.InputTextToOpenAi | FeatureDataBoundary.SearchTextToYouTube,
            BuiltInFeatures.InterpretedVideoSearch.DataBoundary);
    }

    [Fact]
    public async Task FailedSearch_OnlyExplicitRetryReusesQueryIncludingRepeatedFailure()
    {
        Harness h = new();
        h.Provider.Fail = true;
        ScanRequest first = h.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(first));
        Assert.Equal(first.CorrelationId, h.Session.RetryableSearchOperationId);
        Assert.NotNull(h.Session.CurrentInterpretedQuery);
        ScanRequest retry = h.Request() with { RetrySearchOperationId = first.CorrelationId };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(retry));
        Assert.Equal(retry.CorrelationId, h.Session.RetryableSearchOperationId);
        h.Provider.Fail = false;
        FeatureResult result = await h.Run(h.Request() with { RetrySearchOperationId = retry.CorrelationId });
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(3, h.Provider.Calls);
        Assert.All(h.Provider.Requests, search => Assert.Equal("架空曲 2024 ライブ", search.Query));
        Assert.Equal(h.Input.Transcript, result.Sections[0].Text);
        Assert.Null(h.Session.RetryableSearchOperationId);
    }

    [Fact]
    public async Task FreshSearch_DoesNotImplicitlyReuseInterpretationAndInvalidatesCandidates()
    {
        Harness h = new();
        FeatureResult first = await h.Run();
        VideoCandidateAction selection = first.VideoSearch!.Candidates[0].CreateSelectionAction();
        h.Interpreter.Callback = () => Assert.False(h.Session.TryResolveSelection(selection, out _));
        await h.Run();
        Assert.Equal(2, h.Interpreter.Calls);
        Assert.Equal(2, h.Provider.Calls);
    }

    [Fact]
    public async Task Retry_RejectsForgedIdChangedTranscriptAndSuccessWithoutAnyCalls()
    {
        Harness h = new();
        h.Provider.Fail = true;
        ScanRequest first = h.Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(first));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(h.Request() with { RetrySearchOperationId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(h.Request() with { RetrySearchOperationId = Guid.Empty }));
        TextInputSession forged = new(h.Input.SessionId, "changed transcript");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(h.Request() with
        {
            TextInput = forged,
            RetrySearchOperationId = first.CorrelationId,
        }));
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(1, h.Provider.Calls);
        h.Provider.Fail = false;
        await h.Run(h.Request() with { RetrySearchOperationId = first.CorrelationId });
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(h.Request() with { RetrySearchOperationId = first.CorrelationId }));
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(2, h.Provider.Calls);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("stop")]
    [InlineData("replace")]
    public async Task InvalidatedSession_DiscardsQueryAndRetryIdentity(string action)
    {
        Harness h = new();
        h.Provider.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        switch (action)
        {
            case "cancel": h.Execution.CancelCurrentOperation(); break;
            case "close": h.Execution.CloseSession(); break;
            case "stop": h.Execution.Stop(); break;
            case "replace":
                Assert.True(h.Execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? replacement));
                replacement.Dispose();
                break;
        }
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
        Assert.Null(h.Session.CurrentResult);
    }

    [Fact]
    public async Task InterpretationFailure_DoesNotSearchOrOfferSearchRetryOrFallback()
    {
        Harness h = new();
        h.Interpreter.Fail = true;
        await Assert.ThrowsAsync<ScanException>(() => h.Run());
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(0, h.Provider.Calls);
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelDuringInterpretationOrSearch_RejectsLateOutput(bool interpretation)
    {
        Harness h = new();
        if (interpretation) { h.Interpreter.Callback = h.Execution.CancelCurrentOperation; }
        else { h.Provider.Callback = h.Execution.CancelCurrentOperation; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run());
        Assert.Equal(interpretation ? 0 : 1, h.Provider.Calls);
        Assert.Null(h.Session.CurrentResult);
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
        Assert.False(h.Execution.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateHandlerCancellation_DiscardsQueryForInitialSearchAndRetry(bool retry)
    {
        Harness h = new();
        ScanRequest request = h.Request();
        if (retry)
        {
            h.Provider.Fail = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(request));
            h.Provider.Fail = false;
            request = h.Request() with { RetrySearchOperationId = request.CorrelationId };
        }
        using CancellationTokenSource cancellation = new();
        h.Provider.Callback = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(request, cancellation.Token));
        Assert.Null(h.Session.CurrentResult);
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Equal(retry ? 2 : 1, h.Provider.Calls);
        Assert.False(h.Execution.IsRunning);
    }

    [Fact]
    public async Task DirectSearch_ClearsRetainedInterpretationAndDoesNotCallAi()
    {
        Harness h = new();
        h.Provider.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        h.Provider.Fail = false;
        DirectVideoSearchHandler direct = new(h.Provider, h.Session);
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.DirectVideoSearch, h.Input);
        Assert.True(h.Execution.TryBeginOperation(h.Input.SessionId, request.CorrelationId, out ExecutionOperation? operation));
        using (operation)
        {
            FeatureResult result = await direct.HandleAsync(h.Input, request, null, CancellationToken.None);
            Assert.Equal(h.Input.Transcript, result.VideoSearch!.Query);
        }
        Assert.Equal(1, h.Interpreter.Calls);
        Assert.Null(h.Session.CurrentInterpretedQuery);
        Assert.Null(h.Session.RetryableSearchOperationId);
    }

    private sealed class Harness
    {
        public ExecutionCoordinator Execution { get; } = new();
        public VideoSearchSession Session { get; }
        public FakeInterpreter Interpreter { get; } = new();
        public FakeProvider Provider { get; } = new();
        public TextInputSession Input { get; } = TextInputSession.Create("  架空曲はカタカナで\r\n2024年のライブを探して  ");
        private readonly InterpretedVideoSearchHandler _handler;
        private bool _started;
        public Harness() { Session = new(Execution); _handler = new(Interpreter, Provider, Session); }
        public ScanRequest Request() => ScanRequest.CreateText("test", FeatureIds.InterpretedVideoSearch, Input);
        public async Task<FeatureResult> Run(ScanRequest? request = null, CancellationToken cancellationToken = default)
        {
            request ??= Request();
            ExecutionOperation? operation;
            bool accepted = _started
                ? Execution.TryBeginOperation(Input.SessionId, request.CorrelationId, out operation)
                : Execution.TryBeginSession(request.CorrelationId, out operation, sessionId: Input.SessionId);
            Assert.True(accepted);
            _started = true;
            using (operation) { return await _handler.HandleAsync(request.TextInput!, request, null, cancellationToken); }
        }
    }

    private sealed class FakeInterpreter : ISearchQueryInterpreter
    {
        public int Calls { get; private set; }
        public TextInputSession? Input { get; private set; }
        public Action? Callback { get; set; }
        public bool Fail { get; set; }
        public Task<SearchQueryInterpretation> InterpretAsync(TextInputSession input, CancellationToken cancellationToken)
        {
            Calls++; Input = input; Callback?.Invoke();
            if (Fail) { throw new ScanException(ScanFailureCode.SearchInterpretationFailed, ScanStage.SearchInterpretation, "fake failure"); }
            return Task.FromResult(new SearchQueryInterpretation("架空曲 2024 ライブ"));
        }
    }

    private sealed class FakeProvider : IVideoSearchProvider
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Action? Callback { get; set; }
        public List<VideoSearchRequest> Requests { get; } = [];
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken)
        {
            Calls++; Requests.Add(request); Callback?.Invoke();
            if (Fail) { throw new InvalidOperationException("fake search failure"); }
            return Task.FromResult(new VideoSearchBatch([new VideoMetadata("AAAAAAAAAAA", "self-authored title")]));
        }
    }
}
