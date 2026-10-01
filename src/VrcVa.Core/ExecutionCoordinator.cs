using System.Diagnostics.CodeAnalysis;

namespace VrcVa.Core;

/// <summary>
/// Application-lifetime, non-queuing execution ownership. Quotas are separate owners.
/// Cancellation invalidates presentation immediately; only the owner releases after cleanup.
/// </summary>
public sealed class ExecutionCoordinator
{
    private readonly object _sync = new();
    private ExecutionOperation? _active;
    private readonly HashSet<Guid> _acceptedOperationIds = [];
    private readonly HashSet<Guid> _acceptedSessionIds = [];
    private Guid _sessionId;
    private Guid _operationId;
    private long _generation;
    private long _invalidatedThroughGeneration;
    private bool _stopped;

    public bool IsRunning
    {
        get { lock (_sync) { return _active is not null; } }
    }

    public Guid CurrentSessionId
    {
        get { lock (_sync) { return _sessionId; } }
    }

    public Guid CurrentOperationId
    {
        get { lock (_sync) { return _operationId; } }
    }

    public long CurrentGeneration
    {
        get { lock (_sync) { return _generation; } }
    }

    /// <summary>Atomically updates a short in-memory configuration only while idle.</summary>
    public bool TryUpdateWhenIdle(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            if (_stopped || _active is not null) { return false; }
            update();
            return true;
        }
    }

    /// <summary>Completes only when the accepted owner has released after resource cleanup.</summary>
    public Task WhenIdle
    {
        get { lock (_sync) { return _active?.Completion ?? Task.CompletedTask; } }
    }

    /// <summary>Holds exclusion through synchronous credential/runtime replacement, preserving the session.</summary>
    public bool TryBeginConfiguration([NotNullWhen(true)] out ExecutionOperation? operation)
    {
        lock (_sync)
        {
            return TryBegin(Guid.NewGuid(), _sessionId == Guid.Empty ? Guid.NewGuid() : _sessionId,
                true, CancellationToken.None, out operation);
        }
    }

    public bool TryBeginSession(
        Guid operationId,
        [NotNullWhen(true)] out ExecutionOperation? operation,
        CancellationToken cancellationToken = default,
        Guid? sessionId = null) =>
        TryBegin(operationId, sessionId ?? Guid.NewGuid(), true, cancellationToken, out operation);

    public bool TryBeginOperation(
        Guid sessionId,
        Guid operationId,
        [NotNullWhen(true)] out ExecutionOperation? operation,
        CancellationToken cancellationToken = default) =>
        TryBegin(operationId, sessionId, false, cancellationToken, out operation);

    public bool IsSessionCurrent(Guid sessionId)
    {
        lock (_sync) { return !_stopped && sessionId != Guid.Empty && _sessionId == sessionId; }
    }

    public bool IsCurrent(Guid operationId)
    {
        lock (_sync)
        {
            return !_stopped && operationId != Guid.Empty && _operationId == operationId
                && (_active is null || !_active.CancellationToken.IsCancellationRequested);
        }
    }

    public bool IsActive(ExecutionOperation operation)
    {
        lock (_sync) { return ReferenceEquals(_active, operation); }
    }

    // Borrow identity/token for a feature handler; ownership and release remain with its caller.
    internal ExecutionOperation? FindActiveOperation(Guid sessionId, Guid operationId)
    {
        lock (_sync)
        {
            return _active is not null && _active.SessionId == sessionId && _active.OperationId == operationId
                && IsCurrent(_active) ? _active : null;
        }
    }

    // Result snapshots survive later ordinary operations, but never any subsequent invalidation.
    internal bool IsResultCurrent(ExecutionOperation source)
    {
        lock (_sync)
        {
            return !_stopped && _sessionId == source.SessionId
                && source.Generation > _invalidatedThroughGeneration
                && !source.CancellationToken.IsCancellationRequested
                && (_active is null || !_active.CancellationToken.IsCancellationRequested);
        }
    }

    public void CancelCurrentOperation() => Invalidate(endSession: false, stop: false);

    /// <summary>Invalidates only the expected owner, so stale adapter actions cannot cancel newer work.</summary>
    public bool CancelOperation(ExecutionOperation expectedOperation)
    {
        ArgumentNullException.ThrowIfNull(expectedOperation);
        return Invalidate(endSession: false, stop: false, expectedOperation: expectedOperation);
    }

    public void CloseSession() => Invalidate(endSession: true, stop: false);

    /// <summary>Closes only the displayed session; an old Close cannot invalidate its replacement.</summary>
    public bool CloseSession(Guid expectedSessionId) =>
        Invalidate(endSession: true, stop: false, expectedSessionId: expectedSessionId);

    /// <summary>Rejects all later admissions, while the current owner still drains.</summary>
    public void Stop() => Invalidate(endSession: true, stop: true);

    private bool TryBegin(
        Guid operationId,
        Guid sessionId,
        bool newSession,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out ExecutionOperation? operation)
    {
        if (operationId == Guid.Empty || sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session and operation IDs must be nonempty.");
        }

        lock (_sync)
        {
            operation = null;
            if (_stopped || _active is not null || cancellationToken.IsCancellationRequested
                || _acceptedOperationIds.Contains(operationId)
                || (newSession && _sessionId != sessionId && _acceptedSessionIds.Contains(sessionId))
                || (!newSession && _sessionId != sessionId))
            {
                return false;
            }

            _acceptedOperationIds.Add(operationId);
            _acceptedSessionIds.Add(sessionId);
            _sessionId = sessionId;
            _operationId = operationId;
            _generation++;
            operation = new ExecutionOperation(this, sessionId, operationId, _generation, cancellationToken);
            _active = operation;
            return true;
        }
    }

    internal bool IsCurrent(ExecutionOperation operation)
    {
        lock (_sync)
        {
            return !_stopped && _sessionId == operation.SessionId
                && _operationId == operation.OperationId && _generation == operation.Generation
                && !operation.CancellationToken.IsCancellationRequested;
        }
    }

    internal void Complete(ExecutionOperation operation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, operation))
            {
                // A directly linked cancellation must not become current again after release.
                if (operation.CancellationToken.IsCancellationRequested)
                {
                    _operationId = Guid.Empty;
                    _invalidatedThroughGeneration = ++_generation;
                }

                _active = null;
            }
        }

        operation.SignalCompletion();
    }

    private bool Invalidate(
        bool endSession,
        bool stop,
        ExecutionOperation? expectedOperation = null,
        Guid? expectedSessionId = null)
    {
        ExecutionOperation? operation;
        lock (_sync)
        {
            if ((expectedOperation is not null && !ReferenceEquals(_active, expectedOperation))
                || (expectedSessionId is Guid sessionId && (sessionId == Guid.Empty || _sessionId != sessionId)))
            {
                return false;
            }

            _invalidatedThroughGeneration = ++_generation;
            _operationId = Guid.Empty;
            if (endSession) { _sessionId = Guid.Empty; }
            _stopped |= stop;
            operation = _active;
        }

        // Adapter callbacks must never run under the coordinator lock.
        operation?.Cancel();
        return true;
    }
}

/// <summary>
/// One accepted operation, spanning adapter work AND awaited cleanup. Dispose only after
/// the owner's async finally has drained; cancellation never disposes or releases it.
/// </summary>
public sealed class ExecutionOperation : IDisposable
{
    private readonly ExecutionCoordinator _owner;
    private readonly object _cancellationSync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private int _executionClaimed;

    internal ExecutionOperation(
        ExecutionCoordinator owner,
        Guid sessionId,
        Guid operationId,
        long generation,
        CancellationToken cancellationToken)
    {
        _owner = owner;
        SessionId = sessionId;
        OperationId = operationId;
        Generation = generation;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken = _cancellation.Token;
    }

    public Guid SessionId { get; }

    public Guid OperationId { get; }

    public CancellationToken CancellationToken { get; }

    public bool IsCurrent => _owner.IsCurrent(this);

    /// <summary>Observable adapter callback failure; cancellation still invalidates and drains.</summary>
    public AggregateException? CancellationFailure { get; private set; }

    internal bool TryClaimExecution() => Interlocked.CompareExchange(ref _executionClaimed, 1, 0) == 0;

    internal long Generation { get; }

    internal Task Completion => _completion.Task;

    public void ThrowIfNotCurrent()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent) { throw new OperationCanceledException(CancellationToken); }
    }

    internal void Cancel()
    {
        lock (_cancellationSync)
        {
            if (!_disposed)
            {
                try { _cancellation.Cancel(); }
                catch (AggregateException exception) { CancellationFailure = exception; }
            }
        }
    }

    public void Dispose()
    {
        lock (_cancellationSync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _cancellation.Dispose();
        }

        _owner.Complete(this);
    }

    internal void SignalCompletion() => _completion.TrySetResult();
}
