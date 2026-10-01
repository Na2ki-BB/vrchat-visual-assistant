namespace VrcVa.Core.Tests;

public sealed class SharedExecutionPipelineTests
{
    [Fact]
    public async Task CallerOwnsAdmissionThroughPreCaptureWaitAndUiCleanup()
    {
        ExecutionCoordinator execution = new();
        CountingCapture capture = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, new ImmediateAnalyzer(), renderer, execution: execution);
        ScanRequest request = ScanRequest.Create("osc");
        Assert.True(execution.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation));
        using (operation)
        {
            ScanOutcome busy = await pipeline.RunAsync(ScanRequest.Create("hotkey"));
            Assert.Equal(ScanFailureCode.Busy, busy.Failure?.Code);
            Assert.Equal(0, capture.Calls);
            Assert.Empty(renderer.Outcomes);

            ScanOutcome success = await pipeline.RunAsync(request, operation);
            Assert.True(success.IsSuccess);
            Assert.True(execution.IsRunning);
            Assert.False(execution.WhenIdle.IsCompleted);
            Assert.All(capture.Bytes, value => Assert.Equal(0, value));
        }

        Assert.False(execution.IsRunning);
        Assert.True(execution.WhenIdle.IsCompleted);
    }

    [Fact]
    public async Task CancelledAdapterReturningLateFailure_CannotRepaintOrReleaseBeforeCleanup()
    {
        ExecutionCoordinator execution = new();
        LateFailureAnalyzer analyzer = new();
        CountingCapture capture = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, analyzer, renderer, execution: execution);
        Task<ScanOutcome> pending = pipeline.RunAsync(ScanRequest.Create("scan"));
        await analyzer.Started.Task;
        execution.CloseSession();
        Assert.True(execution.IsRunning);
        Assert.False(execution.WhenIdle.IsCompleted);
        Assert.Equal(ScanFailureCode.Busy, (await pipeline.RunAsync(ScanRequest.Create("next"))).Failure?.Code);
        Assert.Empty(renderer.Outcomes);

        analyzer.Finish.SetResult();
        ScanOutcome cancelled = await pending;
        Assert.Equal(ScanFailureCode.Cancelled, cancelled.Failure?.Code);
        Assert.Empty(renderer.Outcomes);
        Assert.All(capture.Bytes, value => Assert.Equal(0, value));
        Assert.False(execution.IsRunning);
    }

    [Fact]
    public async Task BorrowedOperationFromAnotherController_IsRejectedBeforeCapture()
    {
        ExecutionCoordinator owner = new();
        CountingCapture capture = new();
        ScanPipeline pipeline = new(capture, new ImmediateAnalyzer(), new RecordingRenderer());
        ScanRequest request = ScanRequest.Create("test");
        Assert.True(owner.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation));
        using (operation)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RunAsync(request, operation));
            Assert.Equal(0, capture.Calls);
            Assert.True(owner.IsRunning);
        }
    }

    [Fact]
    public async Task RendererFailure_ReleasesOwnedOperationForNextRequest()
    {
        ExecutionCoordinator execution = new();
        ScanPipeline failing = new(new CountingCapture(), new ImmediateAnalyzer(), new ThrowingRenderer(), execution: execution);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RunAsync(ScanRequest.Create("first")));
        Assert.False(execution.IsRunning);

        ScanPipeline next = new(new CountingCapture(), new ImmediateAnalyzer(), new RecordingRenderer(), execution: execution);
        Assert.True((await next.RunAsync(ScanRequest.Create("next"))).IsSuccess);
    }

    [Fact]
    public async Task SameBorrowedOperation_CannotRunTwiceAcrossPipelines()
    {
        ExecutionCoordinator execution = new();
        BlockingCapture capture = new();
        RecordingRenderer renderer = new();
        ScanPipeline first = new(capture, new ImmediateAnalyzer(), renderer, execution: execution);
        ScanPipeline rebuilt = new(capture, new ImmediateAnalyzer(), renderer, execution: execution);
        ScanRequest request = ScanRequest.Create("test");
        Assert.True(execution.TryBeginSession(request.CorrelationId, out ExecutionOperation? operation));
        using (operation)
        {
            Task<ScanOutcome> pending = first.RunAsync(request, operation);
            await capture.Entered.Task;
            Assert.Equal(ScanFailureCode.Busy, (await first.RunAsync(request, operation)).Failure?.Code);
            Assert.Equal(ScanFailureCode.Busy, (await rebuilt.RunAsync(request, operation)).Failure?.Code);
            Assert.Equal(1, capture.Calls);
            Assert.Empty(renderer.Outcomes);
            capture.Release.SetResult();
            Assert.True((await pending).IsSuccess);
            Assert.Equal(ScanFailureCode.Busy, (await rebuilt.RunAsync(request, operation)).Failure?.Code);
            Assert.Equal(1, capture.Calls);
        }
    }

    [Fact]
    public async Task ConfigurationOwnership_KeepsCredentialMutationAndRuntimeReplacementTogether()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        scan.Dispose();
        Assert.True(execution.TryBeginConfiguration(out ExecutionOperation? configuration));
        using (configuration)
        {
            string? storedKey = "synthetic-key";
            bool runtimeUsesKey = true;
            storedKey = null;
            ScanPipeline pipeline = new(new CountingCapture(), new ImmediateAnalyzer(), new RecordingRenderer(), execution: execution);
            Assert.Equal(ScanFailureCode.Busy, (await pipeline.RunAsync(ScanRequest.Create("osc"))).Failure?.Code);
            Assert.False(execution.TryBeginConfiguration(out _));
            runtimeUsesKey = storedKey is not null;
            Assert.False(runtimeUsesKey);
            Assert.Equal(scan.SessionId, configuration.SessionId);
        }

        Assert.False(execution.IsRunning);
        Assert.True(execution.TryBeginConfiguration(out ExecutionOperation? next));
        next.Dispose();
    }

    [Fact]
    public void ClosedOrReplacedInputSession_CannotBeRevivedByNewAdmission()
    {
        ExecutionCoordinator execution = new();
        Guid oldSessionId = Guid.NewGuid();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? old, sessionId: oldSessionId));
        old.Dispose();
        execution.CloseSession();
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _, sessionId: oldSessionId));
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? current));
        current.Dispose();
        Assert.False(execution.TryBeginSession(Guid.NewGuid(), out _, sessionId: oldSessionId));
        Assert.True(execution.IsSessionCurrent(current.SessionId));
        Assert.False(execution.IsSessionCurrent(oldSessionId));
    }

    [Fact]
    public void DisplayedSession_RemainsCurrentAfterCopyOrSettingsButEndsOnNewInputOrClose()
    {
        ExecutionCoordinator execution = new();
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? scan));
        scan.Dispose();
        Assert.True(execution.TryBeginOperation(scan.SessionId, Guid.NewGuid(), out ExecutionOperation? copy));
        copy.Dispose();
        Assert.False(execution.IsCurrent(scan.OperationId));
        Assert.True(execution.IsSessionCurrent(scan.SessionId));
        Assert.True(execution.TryBeginConfiguration(out ExecutionOperation? settings));
        settings.Dispose();
        Assert.True(execution.IsSessionCurrent(scan.SessionId));
        Assert.True(execution.TryBeginSession(Guid.NewGuid(), out ExecutionOperation? nextScan));
        Assert.False(execution.IsSessionCurrent(scan.SessionId));
        nextScan.Dispose();
        execution.CloseSession();
        Assert.False(execution.IsSessionCurrent(nextScan.SessionId));
    }

    private sealed class BlockingCapture : ICaptureSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.SetResult();
            await Release.Task;
            return new CapturedFrame([1], 1, 1, "image/png", "test");
        }
    }

    private sealed class CountingCapture : ICaptureSource
    {
        public byte[] Bytes { get; } = [1, 2, 3];
        public int Calls { get; private set; }
        public Task<CapturedFrame> CaptureAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CapturedFrame(Bytes, 1, 1, "image/png", "test"));
        }
    }

    private sealed class ImmediateAnalyzer : IAnalyzer
    {
        public Task<AnalysisResult> AnalyzeAsync(CapturedFrame frame, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new AnalysisResult("source", "結果", "en", "fake", "fake", TimeSpan.Zero, TimeSpan.Zero));
    }

    private sealed class LateFailureAnalyzer : IAnalyzer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AnalysisResult> AnalyzeAsync(CapturedFrame frame, ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Finish.Task;
            throw new ScanException(ScanFailureCode.TranslationFailed, ScanStage.Translation, "fake failure");
        }
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanOutcome> Outcomes { get; } = [];
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRenderer : IResultRenderer
    {
        public Task RenderProgressAsync(ScanProgress progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenderOutcomeAsync(ScanOutcome outcome, CancellationToken cancellationToken) => throw new InvalidOperationException("fake render failure");
    }
}
