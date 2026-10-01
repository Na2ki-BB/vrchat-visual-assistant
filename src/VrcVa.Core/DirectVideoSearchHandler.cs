using System.Diagnostics;

namespace VrcVa.Core;

/// <summary>Passes the immutable transcript verbatim to one metadata search, with no AI or image dependency.</summary>
public sealed class DirectVideoSearchHandler : ITextFeatureHandler
{
    private readonly IVideoSearchProvider _provider;
    private readonly VideoSearchSession _session;

    public DirectVideoSearchHandler(IVideoSearchProvider provider, VideoSearchSession session)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(session);
        _provider = provider;
        _session = session;
    }

    public async Task<FeatureResult> HandleAsync(
        TextInputSession input,
        ScanRequest request,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        if (request.FeatureId != FeatureIds.DirectVideoSearch || !ReferenceEquals(request.TextInput, input)
            || request.RetrySearchOperationId is not null)
        {
            throw new ArgumentException("The direct search request must match its transcript and feature.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        VideoSearchRequest search = new(input.SessionId, request.CorrelationId, input.Transcript);
        ExecutionOperation operation = _session.BeginSearch(search);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken, cancellationToken);
        operation.ThrowIfNotCurrent();
        Stopwatch timer = Stopwatch.StartNew();
        VideoSearchBatch batch = await _provider.SearchAsync(search, cancellation.Token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(batch);
        timer.Stop();
        VideoSearchResult result = _session.CompleteSearch(search, batch, timer.Elapsed, cancellation.Token);
        return new FeatureResult(FeatureIds.DirectVideoSearch,
        [
            new ResultSection(TranslationResultSectionIds.SourceText, "認識文", input.Transcript),
            new ResultSection("search-query", "検索語", search.Query, ResultSectionRole.Primary),
        ], videoSearch: result)
        {
            CaptureSourceKind = "none",
        };
    }
}
