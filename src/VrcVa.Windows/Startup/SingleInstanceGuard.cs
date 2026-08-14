using System.Runtime.ExceptionServices;

namespace VrcVa.Windows.Startup;

/// <summary>
/// Keeps the normal application launch single-instance within the current Windows session.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string ApplicationMutexName = @"Local\VrcVa.Application";

    private MutexLease? _lease;
    private int _disposed;

    private SingleInstanceGuard(MutexLease? lease, bool isPrimaryInstance, bool isBypassed)
    {
        _lease = lease;
        IsPrimaryInstance = isPrimaryInstance;
        IsBypassed = isBypassed;
    }

    public bool IsPrimaryInstance { get; }

    public bool IsBypassed { get; }

    /// <summary>
    /// Acquires the normal application mutex, or deliberately bypasses it for an isolated
    /// diagnostic command. The bypass decision is supplied by the application entry point so
    /// command-line values never become part of the mutex name.
    /// </summary>
    public static SingleInstanceGuard Acquire(bool bypassSingleInstance = false) =>
        AcquireNamed(ApplicationMutexName, bypassSingleInstance);

    internal static SingleInstanceGuard AcquireNamed(
        string mutexName,
        bool bypassSingleInstance = false)
    {
        ValidateMutexName(mutexName);

        if (bypassSingleInstance)
        {
            return new SingleInstanceGuard(
                lease: null,
                isPrimaryInstance: true,
                isBypassed: true);
        }

        MutexLease? lease = MutexLease.TryAcquire(mutexName);
        return new SingleInstanceGuard(
            lease,
            isPrimaryInstance: lease is not null,
            isBypassed: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        MutexLease? lease = Interlocked.Exchange(ref _lease, null);
        lease?.Dispose();
    }

    private static void ValidateMutexName(string mutexName)
    {
        ArgumentNullException.ThrowIfNull(mutexName);

        const string localPrefix = @"Local\";
        if (!mutexName.StartsWith(localPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The mutex name must use the Local namespace for the current Windows session.",
                nameof(mutexName));
        }

        ReadOnlySpan<char> objectName = mutexName.AsSpan(localPrefix.Length);
        if (objectName.IsEmpty || objectName.Length > 200)
        {
            throw new ArgumentException(
                "The mutex object name must contain between 1 and 200 characters.",
                nameof(mutexName));
        }

        foreach (char character in objectName)
        {
            bool isAllowed = char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_';
            if (!isAllowed)
            {
                throw new ArgumentException(
                    "The mutex object name may contain only ASCII letters, digits, '.', '-' or '_'.",
                    nameof(mutexName));
            }
        }
    }

    /// <summary>
    /// A mutex must be released by the thread that acquired it. A dedicated owner thread keeps
    /// that rule true even when the guard is disposed from a different synchronization context.
    /// </summary>
    private sealed class MutexLease : IDisposable
    {
        private readonly string _mutexName;
        private readonly ManualResetEventSlim _startupCompleted = new(initialState: false);
        private readonly ManualResetEventSlim _releaseRequested = new(initialState: false);
        private readonly ManualResetEventSlim _workerCompleted = new(initialState: false);
        private readonly Thread _ownerThread;

        private ExceptionDispatchInfo? _failure;
        private bool _acquired;
        private int _disposed;
        private int _waitHandlesDisposed;

        private MutexLease(string mutexName)
        {
            _mutexName = mutexName;
            _ownerThread = new Thread(Run)
            {
                IsBackground = true,
                Name = "VRCVA single-instance mutex owner",
            };
        }

        public static MutexLease? TryAcquire(string mutexName)
        {
            MutexLease lease = new(mutexName);
            try
            {
                lease._ownerThread.Start();
                lease._startupCompleted.Wait();

                if (!lease._acquired)
                {
                    lease._workerCompleted.Wait();
                    ExceptionDispatchInfo? failure = lease._failure;
                    lease.DisposeWaitHandles();
                    failure?.Throw();
                    return null;
                }

                return lease;
            }
            catch
            {
                if (lease._ownerThread.IsAlive)
                {
                    lease._releaseRequested.Set();
                    lease._workerCompleted.Wait();
                }

                lease.DisposeWaitHandles();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _releaseRequested.Set();
            _workerCompleted.Wait();
            ExceptionDispatchInfo? failure = _failure;
            DisposeWaitHandles();
            failure?.Throw();
        }

        private void Run()
        {
            Mutex? mutex = null;
            bool ownsMutex = false;
            bool startupWasPublished = false;

            try
            {
                mutex = new Mutex(initiallyOwned: false, _mutexName);
                try
                {
                    ownsMutex = mutex.WaitOne(millisecondsTimeout: 0);
                }
                catch (AbandonedMutexException)
                {
                    // WaitOne grants ownership when it reports an abandoned mutex.
                    ownsMutex = true;
                }

                _acquired = ownsMutex;
                startupWasPublished = true;
                _startupCompleted.Set();

                if (ownsMutex)
                {
                    _releaseRequested.Wait();
                }
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (ownsMutex)
                {
                    try
                    {
                        mutex!.ReleaseMutex();
                    }
                    catch (Exception exception) when (_failure is null)
                    {
                        _failure = ExceptionDispatchInfo.Capture(exception);
                    }
                }

                mutex?.Dispose();

                if (!startupWasPublished)
                {
                    _startupCompleted.Set();
                }

                _workerCompleted.Set();
            }
        }

        private void DisposeWaitHandles()
        {
            if (Interlocked.Exchange(ref _waitHandlesDisposed, 1) != 0)
            {
                return;
            }

            _startupCompleted.Dispose();
            _releaseRequested.Dispose();
            _workerCompleted.Dispose();
        }
    }
}
