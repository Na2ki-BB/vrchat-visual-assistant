using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VrcVa.Windows.Lifecycle;
using Xunit.Abstractions;

namespace VrcVa.Windows.Tests;

[Collection(NativeWpfWindowLifecycleCollection.Name)]
public sealed class WindowShutdownTests(ITestOutputHelper output)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompletedDrains_PostCloseWithoutReenteringTheInitialClosingEvent()
    {
        using DispatcherThread dispatcherThread = new(output);
        ShutdownWindow? window = null;
        await dispatcherThread.InvokeAsync(() =>
        {
            window = new ShutdownWindow(
                dispatcherThread.Dispatcher,
                Task.CompletedTask,
                Task.CompletedTask,
                dispatcherThread.Checkpoint);
            window.RequestClose();

            // This runs before the original Closing event can finish. Even completed
            // drains must leave the final Close queued rather than calling it inline.
            Assert.Equal(1, window.ClosingCalls);
            Assert.Equal(0, window.FinalCloseCalls);
            Assert.False(window.Closed.IsCompleted);
            return Task.CompletedTask;
        });

        ShutdownWindow completedWindow = Assert.IsType<ShutdownWindow>(window);
        await completedWindow.Closed.WaitAsync(TestTimeout);
        await dispatcherThread.InvokeAsync(() =>
        {
            Assert.Equal(2, completedWindow.ClosingCalls);
            Assert.Equal(1, completedWindow.FinalCloseCalls);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingDrains_WaitForBothOperationCleanupAndStartupBeforeClosing(bool operationCompletesFirst)
    {
        using DispatcherThread dispatcherThread = new(output);
        TaskCompletionSource operationCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShutdownWindow? window = null;
        await dispatcherThread.InvokeAsync(() =>
        {
            window = new ShutdownWindow(dispatcherThread.Dispatcher, operationCleanup.Task, startup.Task, dispatcherThread.Checkpoint);
            window.RequestClose();
            Assert.Equal(0, window.FinalCloseCalls);
            Assert.False(window.Closed.IsCompleted);
            return Task.CompletedTask;
        });

        ShutdownWindow pendingWindow = Assert.IsType<ShutdownWindow>(window);
        TaskCompletionSource first = operationCompletesFirst ? operationCleanup : startup;
        TaskCompletionSource last = operationCompletesFirst ? startup : operationCleanup;
        first.SetResult();
        await dispatcherThread.InvokeAsync(() =>
        {
            Assert.Equal(0, pendingWindow.FinalCloseCalls);
            Assert.False(pendingWindow.Closed.IsCompleted);
            // Repeated close attempts while draining must stay cancelled too.
            pendingWindow.RequestClose();
            Assert.Equal(2, pendingWindow.ClosingCalls);
            Assert.Equal(0, pendingWindow.FinalCloseCalls);
            return Task.CompletedTask;
        });

        last.SetResult();
        await pendingWindow.Closed.WaitAsync(TestTimeout);
        await dispatcherThread.InvokeAsync(() =>
        {
            Assert.Equal(3, pendingWindow.ClosingCalls);
            Assert.Equal(1, pendingWindow.FinalCloseCalls);
            return Task.CompletedTask;
        });
    }

    private sealed class ShutdownWindow
    {
        private readonly Dispatcher _dispatcher;
        private readonly Task _operations;
        private readonly Task _startup;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<string> _checkpoint;
        private bool _draining;
        private bool _drained;

        public ShutdownWindow(Dispatcher dispatcher, Task operations, Task startup, Action<string> checkpoint)
        {
            _dispatcher = dispatcher;
            _checkpoint = checkpoint;
            _operations = operations;
            _startup = startup;
            checkpoint("window-construction-start");
            Window = new Window { ShowInTaskbar = false };
            checkpoint("window-construction-complete");
            // Exercise actual HWND-backed Window.Closing without displaying a test UI.
            checkpoint("native-handle-start");
            _ = new WindowInteropHelper(Window).EnsureHandle();
            checkpoint("native-handle-ready");
            Window.Closing += OnClosing;
            Window.Closed += (_, _) => { checkpoint("window-closed"); _closed.TrySetResult(); };
        }

        public Window Window { get; }

        public Task Closed => _closed.Task;

        public int ClosingCalls { get; private set; }

        public int FinalCloseCalls { get; private set; }

        public void RequestClose()
        {
            _checkpoint("close-requested");
            Window.Close();
            _checkpoint("close-returned");
        }

        private async void OnClosing(object? sender, CancelEventArgs eventArgs)
        {
            ClosingCalls++;
            _checkpoint($"closing-event-{ClosingCalls}");
            if (_drained)
            {
                return;
            }

            eventArgs.Cancel = true;
            if (_draining)
            {
                return;
            }

            _draining = true;
            try
            {
                await WindowShutdown.DrainAndPostCloseAsync(_dispatcher, _operations, _startup, () =>
                {
                    Assert.True(_dispatcher.CheckAccess());
                    FinalCloseCalls++;
                    _drained = true;
                    _checkpoint("final-close-posted");
                    Window.Close();
                    _checkpoint("final-close-returned");
                });
            }
            catch (Exception exception)
            {
                // Surface async-void failures to the test instead of crashing its dispatcher.
                _closed.TrySetException(exception);
            }
        }
    }

    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ITestOutputHelper _output;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly ConcurrentQueue<string> _checkpoints = new();
        private string _stage = "not-started";
        private Exception? _threadFailure;
        private Dispatcher? _createdDispatcher;
        private int _stopRequested;
        private bool _disposed;

        public DispatcherThread(ITestOutputHelper output)
        {
            _output = output;
            TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Checkpoint("dispatcher-requested");
            _thread = new Thread(() =>
            {
                try
                {
                    Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                    Volatile.Write(ref _createdDispatcher, dispatcher);
                    Checkpoint("dispatcher-created");
                    if (Volatile.Read(ref _stopRequested) != 0)
                    {
                        dispatcher.InvokeShutdown();
                        return;
                    }
                    // Object construction alone is not proof that the dispatcher is pumping.
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Volatile.Read(ref _stopRequested) != 0)
                        {
                            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                            return;
                        }
                        Checkpoint("pump-ready");
                        ready.TrySetResult(dispatcher);
                    }));
                    Dispatcher.Run();
                    Checkpoint("pump-stopped");
                }
                catch (Exception exception)
                {
                    _threadFailure = exception;
                    Checkpoint("pump-failed");
                    ready.TrySetException(exception);
                }
            })
            {
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            try { Dispatcher = ready.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult(); }
            catch (Exception exception)
            {
                RequestStop();
                bool stopped = _thread.Join(TestTimeout);
                WriteCheckpoints();
                if (!stopped)
                {
                    throw new TimeoutException("The test dispatcher did not stop after startup failure.", exception);
                }
                throw;
            }
        }

        public Dispatcher Dispatcher { get; }

        public void Checkpoint(string stage)
        {
            Volatile.Write(ref _stage, stage);
            _checkpoints.Enqueue($"WPF fixture: {stage}, elapsed_ms={_elapsed.ElapsedMilliseconds}");
        }

        public async Task InvokeAsync(Func<Task> action)
        {
            Checkpoint("callback-queued");
            Task operation = Dispatcher.InvokeAsync(async () =>
            {
                Checkpoint("callback-entered");
                await action();
                Checkpoint("callback-completed");
            }).Task.Unwrap();
            try { await operation.WaitAsync(TestTimeout); }
            catch (TimeoutException exception)
            {
                // No window title or user content: distinguish a starved queue from native setup/Close.
                throw new TimeoutException($"WPF callback timeout: stage={Volatile.Read(ref _stage)}, "
                    + $"elapsed_ms={_elapsed.ElapsedMilliseconds}, thread={_thread.ThreadState}, "
                    + $"shutdown={Dispatcher.HasShutdownStarted}.", exception);
            }
        }

        private void RequestStop()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            Dispatcher? dispatcher = Volatile.Read(ref _createdDispatcher);
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }

        private void WriteCheckpoints()
        {
            foreach (string checkpoint in _checkpoints) { _output.WriteLine(checkpoint); }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            try
            {
                RequestStop();
                if (!_thread.Join(TestTimeout))
                {
                    throw new TimeoutException($"The test dispatcher did not stop: stage={Volatile.Read(ref _stage)}, "
                        + $"thread={_thread.ThreadState}.");
                }
                if (_threadFailure is not null)
                {
                    throw new InvalidOperationException("The test dispatcher failed.", _threadFailure);
                }
            }
            finally
            {
                WriteCheckpoints();
            }
        }
    }
}

// Only the three HWND lifecycle tests are isolated; the rest of the Windows suite stays parallel.
// Cold WPF/font/codec initialization elsewhere must not consume the shutdown assertion deadline.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeWpfWindowLifecycleCollection
{
    public const string Name = "Native WPF window lifecycle";
}
