using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VrcVa.Windows.Lifecycle;

namespace VrcVa.Windows.Tests;

public sealed class WindowShutdownTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompletedDrains_PostCloseWithoutReenteringTheInitialClosingEvent()
    {
        using DispatcherThread dispatcherThread = new();
        ShutdownWindow? window = null;
        await dispatcherThread.InvokeAsync(() =>
        {
            window = new ShutdownWindow(
                dispatcherThread.Dispatcher,
                Task.CompletedTask,
                Task.CompletedTask);
            window.Window.Close();

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
        using DispatcherThread dispatcherThread = new();
        TaskCompletionSource operationCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShutdownWindow? window = null;
        await dispatcherThread.InvokeAsync(() =>
        {
            window = new ShutdownWindow(dispatcherThread.Dispatcher, operationCleanup.Task, startup.Task);
            window.Window.Close();
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
            pendingWindow.Window.Close();
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
        private bool _draining;
        private bool _drained;

        public ShutdownWindow(Dispatcher dispatcher, Task operations, Task startup)
        {
            _dispatcher = dispatcher;
            _operations = operations;
            _startup = startup;
            Window = new Window { ShowInTaskbar = false };
            // Exercise actual HWND-backed Window.Closing without displaying a test UI.
            _ = new WindowInteropHelper(Window).EnsureHandle();
            Window.Closing += OnClosing;
            Window.Closed += (_, _) => _closed.TrySetResult();
        }

        public Window Window { get; }

        public Task Closed => _closed.Task;

        public int ClosingCalls { get; private set; }

        public int FinalCloseCalls { get; private set; }

        private async void OnClosing(object? sender, CancelEventArgs eventArgs)
        {
            ClosingCalls++;
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
                    Window.Close();
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
        private readonly ManualResetEventSlim _start = new();
        private readonly Thread _thread;
        private bool _disposed;

        public DispatcherThread()
        {
            TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                _start.Wait();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Dispatcher = ready.Task.WaitAsync(TestTimeout).GetAwaiter().GetResult();
        }

        public Dispatcher Dispatcher { get; }

        public async Task InvokeAsync(Func<Task> action)
        {
            _start.Set();
            await Dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TestTimeout);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            _start.Set();
            if (!_thread.Join(TestTimeout))
            {
                throw new TimeoutException("The test dispatcher did not stop.");
            }

            _start.Dispose();
        }
    }
}
