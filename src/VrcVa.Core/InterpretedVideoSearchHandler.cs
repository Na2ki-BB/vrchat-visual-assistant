using System.Diagnostics;

namespace VrcVa.Core;

/// <summary>One interpretation then one metadata search. Explicit search retries reuse the accepted query.</summary>
public sealed class InterpretedVideoSearchHandler : ITextFeatureHandler
{
    private readonly ISearchQueryInterpreter _interpreter;
    private readonly IVideoSearchProvider _provider;
    private readonly VideoSearchSession _session;

    public InterpretedVideoSearchHandler(ISearchQueryInterpreter interpreter, IVideoSearchProvider provider,
        VideoSearchSession session)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(session);
        _interpreter = interpreter;
        _provider = provider;
        _session = session;
    }

    public async Task<FeatureResult> HandleAsync(TextInputSession input, ScanRequest request,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        if (request.FeatureId != FeatureIds.InterpretedVideoSearch || !ReferenceEquals(request.TextInput, input))
        {
            throw new ArgumentException("The interpreted search request must match its transcript and feature.", nameof(request));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionOperation operation = _session.BeginInterpretedSearch(input, request.CorrelationId,
            request.RetrySearchOperationId, out SearchQueryInterpretation? query);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken, cancellationToken);
        using CancellationTokenRegistration cancellationRegistration = cancellation.Token.Register(
            () => _session.CancelSearch(operation));
        operation.ThrowIfNotCurrent();
        if (query is null)
        {
            progress?.Report(new ScanProgress(request.CorrelationId, ScanStage.SearchInterpretation, "検索語を解釈しています。", TimeSpan.Zero));
            query = await _interpreter.InterpretAsync(input, cancellation.Token).ConfigureAwait(false);
            _session.AcceptInterpretation(input, operation, query, cancellation.Token);
        }
        cancellation.Token.ThrowIfCancellationRequested();
        operation.ThrowIfNotCurrent();
        VideoSearchRequest search = new(input.SessionId, request.CorrelationId, query.Query);
        _session.AcceptSearchRequest(operation, search, cancellation.Token);
        progress?.Report(new ScanProgress(request.CorrelationId, ScanStage.TextHandling, "動画を検索しています。", TimeSpan.Zero));
        Stopwatch timer = Stopwatch.StartNew();
        VideoSearchBatch batch;
        try
        {
            batch = await _provider.SearchAsync(search, cancellation.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(batch);
        }
        catch
        {
            _session.MarkSearchFailed(operation, cancellation.Token);
            throw;
        }
        timer.Stop();
        VideoSearchResult result = _session.CompleteSearch(search, batch, timer.Elapsed, cancellation.Token);
        return new FeatureResult(FeatureIds.InterpretedVideoSearch,
        [
            new ResultSection(TranslationResultSectionIds.SourceText, "認識文", input.Transcript),
            new ResultSection("search-query", "検索語", search.Query, ResultSectionRole.Primary),
        ], videoSearch: result)
        { CaptureSourceKind = "none" };
    }
}
