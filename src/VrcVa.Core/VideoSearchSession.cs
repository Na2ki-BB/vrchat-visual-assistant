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

    internal ExecutionOperation BeginSearch(VideoSearchRequest request)
    {
        lock (_sync)
        {
            ExecutionOperation? operation = _execution.FindActiveOperation(request.SessionId, request.OperationId);
            if (operation is null || _searchOperation?.OperationId == request.OperationId)
            {
                throw new InvalidOperationException("The search must belong to a new active operation in the current session.");
            }

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

    private void DiscardStaleSession()
    {
        if (_result is not null && !_execution.IsResultCurrent(_searchOperation!))
        {
            _result = null;
        }
    }
}
