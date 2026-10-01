using System.Diagnostics.CodeAnalysis;

namespace VrcVa.Core;

/// <summary>
/// Keeps only the currently accepted search for a shared transcript session.
/// Consumers must resolve the action again immediately before a later clipboard write.
/// This class authorizes identities only; it performs no clipboard or network work.
/// </summary>
public sealed class VideoSearchSession
{
    private readonly object _sync = new();
    private readonly ExecutionCoordinator _execution;
    private ExecutionOperation? _searchOperation;
    private VideoSearchResult? _result;
    private SearchQueryInterpretation? _interpretedQuery;
    private TextInputSession? _interpretedInput;
    private ExecutionOperation? _interpretationOperation;
    private Guid? _retryableSearchOperationId;

    public VideoSearchSession(ExecutionCoordinator execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _execution = execution;
    }

    public VideoSearchResult? CurrentResult
    {
        get
        {
            lock (_sync)
            {
                DiscardStaleSession();
                return _result;
            }
        }
    }

    /// <summary>Feature-owned query retained in memory, including after a retryable search failure.</summary>
    public SearchQueryInterpretation? CurrentInterpretedQuery
    {
        get { lock (_sync) { DiscardStaleSession(); return _interpretedQuery; } }
    }

    public Guid? RetryableSearchOperationId
    {
        get { lock (_sync) { DiscardStaleSession(); return _retryableSearchOperationId; } }
    }

    internal ExecutionOperation BeginInterpretedSearch(TextInputSession input, Guid operationId,
        Guid? retrySearchOperationId, out SearchQueryInterpretation? retainedQuery)
    {
        lock (_sync)
        {
            DiscardStaleSession();
            retainedQuery = null;
            if (retrySearchOperationId is Guid retry
                && (retry == Guid.Empty || _retryableSearchOperationId != retry
                    || _interpretedQuery is null || !ReferenceEquals(_interpretedInput, input)))
            {
                throw new InvalidOperationException("Only the current failed search may be explicitly retried.");
            }
            ExecutionOperation operation = FindNewOperation(input.SessionId, operationId);
            if (retrySearchOperationId is not null) { retainedQuery = _interpretedQuery; }
            else { ClearInterpretation(); }
            _searchOperation = operation;
            _result = null;
            _retryableSearchOperationId = null;
            return operation;
        }
    }

    internal void AcceptInterpretation(TextInputSession input, ExecutionOperation operation,
        SearchQueryInterpretation query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.ThrowIfNotCurrent();
            if (!ReferenceEquals(_searchOperation, operation) || !_execution.IsActive(operation))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            _interpretedInput = input;
            _interpretedQuery = query;
            _interpretationOperation = operation;
        }
    }

    internal void CancelSearch(ExecutionOperation operation)
    {
        lock (_sync)
        {
            // A late cancellation from an old handler cannot clear a newer search.
            if (!ReferenceEquals(_searchOperation, operation)) { return; }
            _result = null;
            ClearInterpretation();
        }
        if (!operation.CancellationToken.IsCancellationRequested)
        {
            _execution.CancelOperation(operation);
        }
    }

    internal void MarkSearchFailed(ExecutionOperation operation, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!cancellationToken.IsCancellationRequested && ReferenceEquals(_searchOperation, operation)
                && _execution.IsActive(operation) && operation.IsCurrent && _interpretedQuery is not null)
            {
                _retryableSearchOperationId = operation.OperationId;
            }
        }
    }

    private ExecutionOperation FindNewOperation(Guid sessionId, Guid operationId)
    {
        ExecutionOperation? operation = _execution.FindActiveOperation(sessionId, operationId);
        if (operation is null || _searchOperation?.OperationId == operationId)
        {
            throw new InvalidOperationException("The search must belong to a new active operation in the current session.");
        }
        return operation;
    }

    private void ClearInterpretation()
    {
        _interpretedQuery = null;
        _interpretedInput = null;
        _interpretationOperation = null;
        _retryableSearchOperationId = null;
    }

    internal ExecutionOperation BeginSearch(VideoSearchRequest request)
    {
        lock (_sync)
        {
            ExecutionOperation operation = FindNewOperation(request.SessionId, request.OperationId);
            ClearInterpretation();
            _searchOperation = operation;
            _result = null;
            return operation;
        }
    }

    internal VideoSearchResult CompleteSearch(
        VideoSearchRequest request,
        VideoSearchBatch batch,
        TimeSpan searchDuration,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_searchOperation is null || _searchOperation.SessionId != request.SessionId
                || _searchOperation.OperationId != request.OperationId || !_execution.IsActive(_searchOperation)
                || !_searchOperation.IsCurrent)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _retryableSearchOperationId = null;
            _result = new VideoSearchResult(request, batch, searchDuration);
            return _result;
        }
    }

    public bool TryGetPage(
        Guid sessionId,
        Guid searchOperationId,
        int pageIndex,
        [NotNullWhen(true)] out IReadOnlyList<VideoCandidate>? candidates)
    {
        lock (_sync)
        {
            DiscardStaleSession();
            candidates = null;
            if (_result is null || _result.SessionId != sessionId || _result.OperationId != searchOperationId
                || pageIndex < 0 || pageIndex >= _result.PageCount)
            {
                return false;
            }

            candidates = _result.GetPage(pageIndex);
            return true;
        }
    }

    public bool TryResolveSelection(
        VideoCandidateAction action,
        [NotNullWhen(true)] out VideoCandidate? candidate)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            DiscardStaleSession();
            candidate = null;
            if (action.Kind != VideoCandidateActionKind.CopyWatchUrl || _result is null
                || action.SessionId != _result.SessionId || action.SearchOperationId != _result.OperationId)
            {
                return false;
            }

            candidate = _result.Candidates.FirstOrDefault(value => value.CandidateId == action.CandidateId);
            return candidate is not null;
        }
    }

    internal bool TryWriteSelection(VideoCandidateAction action, ExecutionOperation copyOperation, Action<string> write)
    {
        lock (_sync)
        {
            // Lock order is always session then coordinator. No await can split this boundary.
            return _execution.TryExecuteCurrentSideEffect(copyOperation, () =>
            {
                if (copyOperation.SessionId != action.SessionId || !TryResolveSelection(action, out var candidate))
                {
                    throw new OperationCanceledException(copyOperation.CancellationToken);
                }
                write(candidate.WatchUrl.AbsoluteUri);
            });
        }
    }

    private void DiscardStaleSession()
    {
        if (_interpretationOperation is not null && !_execution.IsResultCurrent(_interpretationOperation))
        {
            ClearInterpretation();
        }
        if (_result is not null && !_execution.IsResultCurrent(_searchOperation!))
        {
            _result = null;
        }
    }
}
