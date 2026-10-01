using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;

namespace VrcVa.Windows.Tests;

public sealed class WpfResultRendererTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(RenderKind.Progress, InvalidationKind.Close)]
    [InlineData(RenderKind.Success, InvalidationKind.Close)]
    [InlineData(RenderKind.Failure, InvalidationKind.Close)]
    [InlineData(RenderKind.Progress, InvalidationKind.Cancel)]
    [InlineData(RenderKind.Success, InvalidationKind.Cancel)]
    [InlineData(RenderKind.Failure, InvalidationKind.Cancel)]
    [InlineData(RenderKind.Progress, InvalidationKind.NewOperation)]
    [InlineData(RenderKind.Success, InvalidationKind.NewOperation)]
    [InlineData(RenderKind.Failure, InvalidationKind.NewOperation)]
    public async Task QueuedCallback_InvalidatedBeforeDispatch_DoesNotRender(
        RenderKind kind,
        InvalidationKind invalidation)
    {
        using DispatcherThread dispatcherThread = new();
        ExecutionCoordinator coordinator = new();
        Guid operationId = Guid.NewGuid();
        using ExecutionOperation operation = BeginSession(coordinator, operationId);
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered, coordinator.IsCurrent);

        Task pending = RenderAsync(renderer, kind, operationId, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        using ExecutionOperation? replacement = Invalidate(coordinator, operation, invalidation);
        dispatcherThread.Start();
        await pending.WaitAsync(TestTimeout);

        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(RenderKind.Progress)]
    [InlineData(RenderKind.Success)]
    [InlineData(RenderKind.Failure)]
    public async Task QueuedCallback_TokenCancelledBeforeDispatch_DoesNotRender(RenderKind kind)
    {
        using DispatcherThread dispatcherThread = new();
        using CancellationTokenSource cancellation = new();
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered);

        Task pending = RenderAsync(renderer, kind, Guid.NewGuid(), cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        dispatcherThread.Start();
        await pending.WaitAsync(TestTimeout);

        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(RenderKind.Progress)]
    [InlineData(RenderKind.Success)]
    [InlineData(RenderKind.Failure)]
    public async Task InlineCallback_CancelledToken_DoesNotRender(RenderKind kind)
    {
        using DispatcherThread dispatcherThread = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered);

        await dispatcherThread.InvokeAsync(() =>
            RenderAsync(renderer, kind, Guid.NewGuid(), cancellation.Token));

        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(RenderKind.Progress)]
    [InlineData(RenderKind.Success)]
    [InlineData(RenderKind.Failure)]
    public async Task InlineCallback_InvalidOperation_DoesNotRender(RenderKind kind)
    {
        using DispatcherThread dispatcherThread = new();
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered, _ => false);

        await dispatcherThread.InvokeAsync(() =>
            RenderAsync(renderer, kind, Guid.NewGuid(), CancellationToken.None));

        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(RenderKind.Progress, false)]
    [InlineData(RenderKind.Success, false)]
    [InlineData(RenderKind.Failure, false)]
    [InlineData(RenderKind.Progress, true)]
    [InlineData(RenderKind.Success, true)]
    [InlineData(RenderKind.Failure, true)]
    public async Task CurrentCallback_RendersOnDispatcher(RenderKind kind, bool inline)
    {
        using DispatcherThread dispatcherThread = new();
        ExecutionCoordinator coordinator = new();
        Guid operationId = Guid.NewGuid();
        using ExecutionOperation operation = BeginSession(coordinator, operationId);
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered, coordinator.IsCurrent);

        if (inline)
        {
            await dispatcherThread.InvokeAsync(() =>
                RenderAsync(renderer, kind, operationId, CancellationToken.None));
        }
        else
        {
            Task pending = RenderAsync(renderer, kind, operationId, CancellationToken.None);
            dispatcherThread.Start();
            await pending.WaitAsync(TestTimeout);
        }

        Assert.Equal([operationId], rendered);
    }

    [Fact]
    public async Task WithoutValidityGuard_CurrentCallbackStillRenders()
    {
        using DispatcherThread dispatcherThread = new();
        Guid operationId = Guid.NewGuid();
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered);

        Task pending = RenderAsync(renderer, RenderKind.Success, operationId, CancellationToken.None);
        dispatcherThread.Start();
        await pending.WaitAsync(TestTimeout);

        Assert.Equal([operationId], rendered);
    }

    [Fact]
    public async Task DispatcherShutdown_AbortsQueuedCallbackWithoutRendering()
    {
        using DispatcherThread dispatcherThread = new();
        List<Guid> rendered = [];
        WpfResultRenderer renderer = CreateRenderer(dispatcherThread.Dispatcher, rendered);

        Task pending = RenderAsync(renderer, RenderKind.Success, Guid.NewGuid(), CancellationToken.None);
        dispatcherThread.Shutdown();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
        Assert.Empty(rendered);
    }

    [Theory]
    [InlineData(RenderKind.Progress)]
    [InlineData(RenderKind.Success)]
    [InlineData(RenderKind.Failure)]
    public async Task SteamVrQueuedCallback_InvalidatedBeforeDispatch_DoesNotTouchPanelOrNotify(RenderKind kind)
    {
        using DispatcherThread dispatcherThread = new();
        SteamVrResultPanel panel = new(dispatcherThread.Dispatcher);
        panel.Dispose(); // A stale OCR/success callback would throw if it reached the panel.
        bool current = true;
        int validityChecks = 0;
        RecordingSink sink = new();
        RecordingLogger logger = new();
        SteamVrOverlayResultRenderer renderer = new(dispatcherThread.Dispatcher, panel, sink, logger, _ =>
        {
            validityChecks++;
            return current;
        });
        Guid operationId = Guid.NewGuid();
        Task pending = kind switch
        {
            RenderKind.Progress => renderer.RenderProgressAsync(new ScanProgress(operationId, ScanStage.Ocr, "fake", TimeSpan.Zero), CancellationToken.None),
            RenderKind.Success => renderer.RenderOutcomeAsync(ScanOutcome.Succeeded(operationId,
                new AnalysisResult("fake", "fake", "en", "fake", "fake", TimeSpan.Zero, TimeSpan.Zero), TimeSpan.Zero), CancellationToken.None),
            _ => renderer.RenderOutcomeAsync(ScanOutcome.Failed(operationId,
                new ScanFailure(ScanFailureCode.Unexpected, ScanStage.Ocr, "fake"), TimeSpan.Zero), CancellationToken.None),
        };
        current = false;
        dispatcherThread.Start();
        await pending.WaitAsync(TestTimeout);
        Assert.Equal(1, validityChecks);
        Assert.Equal(0, sink.Calls);
        Assert.Equal(0, logger.Errors);
    }

    [Fact]
    public async Task SteamVrInlineProgress_DoesNotQueueWorkBehindItsOwnDispatcher()
    {
        using DispatcherThread dispatcherThread = new();
        await dispatcherThread.InvokeAsync(() =>
        {
            SteamVrResultPanel panel = new(dispatcherThread.Dispatcher);
            panel.Dispose();
            SteamVrOverlayResultRenderer renderer = new(dispatcherThread.Dispatcher, panel, new RecordingSink(), new RecordingLogger());
            Task pending = renderer.RenderProgressAsync(new ScanProgress(Guid.NewGuid(), ScanStage.Trigger, "fake", TimeSpan.Zero), CancellationToken.None);
            Assert.True(pending.IsCompletedSuccessfully);
            return pending;
        });
    }

    private sealed class RecordingSink : IXsOverlayNotificationSink
    {
        public int Calls { get; private set; }
        public Task SendAsync(string title, string content, XsOverlayNotificationKind kind, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : IPrivacySafeLogger
    {
        public int Errors { get; private set; }
        public void Info(string eventName, Guid correlationId, ScanStage stage, TimeSpan? duration = null, IReadOnlyDictionary<string, long>? numericMetrics = null) { }
        public void Error(string eventName, Guid correlationId, ScanStage stage, ScanFailureCode failureCode, Exception exception) => Errors++;
    }

    private static WpfResultRenderer CreateRenderer(
        Dispatcher dispatcher,
        List<Guid> rendered,
        Func<Guid, bool>? canRender = null) =>
        new(
            dispatcher,
            progress =>
            {
                Assert.True(dispatcher.CheckAccess());
                rendered.Add(progress.CorrelationId);
            },
            outcome =>
            {
                Assert.True(dispatcher.CheckAccess());
                rendered.Add(outcome.CorrelationId);
            },
            canRender);

    private static Task RenderAsync(
        WpfResultRenderer renderer,
        RenderKind kind,
        Guid operationId,
        CancellationToken cancellationToken) =>
        kind switch
        {
            RenderKind.Progress => renderer.RenderProgressAsync(
                new ScanProgress(operationId, ScanStage.Ocr, "synthetic progress", TimeSpan.Zero),
                cancellationToken),
            RenderKind.Success => renderer.RenderOutcomeAsync(
                ScanOutcome.Succeeded(
                    operationId,
                    new AnalysisResult(
                        "synthetic source",
                        "synthetic result",
                        "en",
                        "fake",
                        "fake-model",
                        TimeSpan.Zero,
                        TimeSpan.Zero),
                    TimeSpan.Zero),
                cancellationToken),
            RenderKind.Failure => renderer.RenderOutcomeAsync(
                ScanOutcome.Failed(
                    operationId,
                    new ScanFailure(ScanFailureCode.Unexpected, ScanStage.Ocr, "synthetic failure"),
                    TimeSpan.Zero),
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static ExecutionOperation BeginSession(ExecutionCoordinator coordinator, Guid operationId)
    {
        Assert.True(coordinator.TryBeginSession(operationId, out ExecutionOperation? operation));
        return Assert.IsType<ExecutionOperation>(operation);
    }

    private static ExecutionOperation? Invalidate(
        ExecutionCoordinator coordinator,
        ExecutionOperation operation,
        InvalidationKind invalidation)
    {
        switch (invalidation)
        {
            case InvalidationKind.Close:
                coordinator.CloseSession();
                return null;
            case InvalidationKind.Cancel:
                coordinator.CancelCurrentOperation();
                return null;
            case InvalidationKind.NewOperation:
                operation.Dispose();
                return BeginSession(coordinator, Guid.NewGuid());
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation));
        }
    }

    public enum RenderKind
    {
        Progress,
        Success,
        Failure,
    }

    public enum InvalidationKind
    {
        Close,
        Cancel,
        NewOperation,
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

        public void Start() => _start.Set();

        public async Task InvokeAsync(Func<Task> action)
        {
            Start();
            await Dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TestTimeout);
        }

        public void Shutdown()
        {
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            Start();
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
                Shutdown();
            }

            if (!_thread.Join(TestTimeout))
            {
                throw new TimeoutException("The test dispatcher did not stop.");
            }

            _start.Dispose();
        }
    }
}
