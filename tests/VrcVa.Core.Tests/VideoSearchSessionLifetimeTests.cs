using System.Runtime.CompilerServices;

namespace VrcVa.Core.Tests;

public sealed class VideoSearchSessionLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPurgeReleasesClosedOrReplacedContentWithoutWaitingForARead(bool replace)
    {
        (VideoSearchSession session, WeakReference input, WeakReference query, WeakReference result) = ClosedSession(replace);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(input.IsAlive);
        Assert.False(query.IsAlive);
        Assert.False(result.IsAlive);
        GC.KeepAlive(session); // the app itself still owns this session
    }

    [Fact]
    public async Task PurgePreservesANewerLiveResultAndItsSelection()
    {
        ExecutionCoordinator execution = new();
        VideoSearchSession session = new(execution);
        TextInputSession input = TextInputSession.Create("synthetic input");
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.InterpretedVideoSearch, input);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out var operation, sessionId: input.SessionId));
        FeatureResult response;
        using (operation)
        {
            response = await new InterpretedVideoSearchHandler(new Interpreter(), new Provider(), session)
                .HandleAsync(input, request, null, CancellationToken.None);
        }
        session.DiscardInvalidatedState();
        Assert.Same(response.VideoSearch, session.CurrentResult);
        Assert.True(session.TryResolveSelection(response.VideoSearch!.Candidates[0].CreateSelectionAction(), out _));
        Assert.NotNull(session.CurrentInterpretedQuery);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VideoSearchSession, WeakReference, WeakReference, WeakReference) ClosedSession(bool replace)
    {
        ExecutionCoordinator execution = new();
        VideoSearchSession session = new(execution);
        TextInputSession input = TextInputSession.Create(new string('a', 100));
        ScanRequest request = ScanRequest.CreateText("test", FeatureIds.InterpretedVideoSearch, input);
        Assert.True(execution.TryBeginSession(request.CorrelationId, out var operation, sessionId: input.SessionId));
        FeatureResult response;
        using (operation)
        {
            response = new InterpretedVideoSearchHandler(new Interpreter(), new Provider(), session)
                .HandleAsync(input, request, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        WeakReference retainedInput = new(input);
        WeakReference retainedQuery = new(session.CurrentInterpretedQuery!);
        WeakReference retainedResult = new(response.VideoSearch!);
        if (replace)
        {
            Assert.True(execution.TryBeginSession(Guid.NewGuid(), out var newer));
            newer.Dispose();
        }
        else { execution.CloseSession(input.SessionId); }
        session.DiscardInvalidatedState();
        return (session, retainedInput, retainedQuery, retainedResult);
    }

    private sealed class Interpreter : ISearchQueryInterpreter
    {
        public Task<SearchQueryInterpretation> InterpretAsync(TextInputSession input, CancellationToken cancellationToken) =>
            Task.FromResult(new SearchQueryInterpretation(new string('q', 50)));
    }
    private sealed class Provider : IVideoSearchProvider
    {
        public Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new VideoSearchBatch([new VideoMetadata("AAAAAAAAAAA", "synthetic title")]));
    }
}
