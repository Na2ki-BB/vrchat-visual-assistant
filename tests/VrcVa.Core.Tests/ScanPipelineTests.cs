namespace VrcVa.Core.Tests;

public sealed class ScanPipelineTests
{
    [Fact]
    public async Task RunAsync_CompletesVerticalSliceAndClearsFrameBytes()
    {
        byte[] sensitiveFrame = [10, 20, 30, 40];
        FakeCaptureSource capture = new(() =>
            new CapturedFrame(sensitiveFrame, 2, 2, "image/png", "test"));
        FakeAnalyzer analyzer = new(new AnalysisResult(
            "Safety first",
            "安全第一",
            "en",
            "fake",
            "fake-model",
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20)));
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, analyzer, renderer);

        ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.Create("unit-test"));

        Assert.True(outcome.IsSuccess);
        Assert.Equal("安全第一", outcome.Result?.JapaneseText);
        Assert.Equal("test", outcome.Result?.CaptureSourceKind);
        Assert.All(sensitiveFrame, value => Assert.Equal(0, value));
        Assert.Contains(renderer.Progress, item => item.Stage == ScanStage.Capture);
        Assert.Single(renderer.Outcomes);
    }

    [Fact]
    public async Task RunAsync_ClassifiesKnownFailure()
    {
        FakeCaptureSource capture = new(() => throw new ScanException(
            ScanFailureCode.CaptureTargetNotFound,
            ScanStage.Capture,
            "VRChatが見つかりません。"));
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(
            capture,
            new FakeAnalyzer(CreateResult()),
            renderer);

        ScanOutcome outcome = await pipeline.RunAsync(ScanRequest.Create("unit-test"));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ScanFailureCode.CaptureTargetNotFound, outcome.Failure?.Code);
        Assert.Equal(ScanStage.Capture, outcome.Failure?.Stage);
    }

    [Fact]
    public async Task RunAsync_RejectsOverlappingScan()
    {
        TaskCompletionSource captureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingCaptureSource capture = new(captureEntered, releaseCapture);
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, new FakeAnalyzer(CreateResult()), renderer);

        Task<ScanOutcome> first = pipeline.RunAsync(ScanRequest.Create("first"));
        await captureEntered.Task;

        ScanOutcome second = await pipeline.RunAsync(ScanRequest.Create("second"));
        releaseCapture.SetResult();
        ScanOutcome firstOutcome = await first;

        Assert.Equal(ScanFailureCode.Busy, second.Failure?.Code);
        Assert.True(firstOutcome.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_ReturnsCancelledOutcome()
    {
        CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellingCaptureSource capture = new();
        RecordingRenderer renderer = new();
        ScanPipeline pipeline = new(capture, new FakeAnalyzer(CreateResult()), renderer);

        ScanOutcome outcome = await pipeline.RunAsync(
            ScanRequest.Create("cancel-test"),
            cancellation.Token);

        Assert.Equal(ScanFailureCode.Cancelled, outcome.Failure?.Code);
    }

    private static AnalysisResult CreateResult() => new(
        "Exit",
        "出口",
        "en",
        "fake",
        "fake-model",
        TimeSpan.Zero,
        TimeSpan.Zero);

    private sealed class FakeCaptureSource(Func<CapturedFrame> factory) : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken) => Task.FromResult(factory());
    }

    private sealed class BlockingCaptureSource(
        TaskCompletionSource entered,
        TaskCompletionSource release) : ICaptureSource
    {
        public async Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken)
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new CapturedFrame([1], 1, 1, "image/png", "test");
        }
    }

    private sealed class CancellingCaptureSource : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<CapturedFrame>(cancellationToken);
    }

    private sealed class FakeAnalyzer(AnalysisResult result) : IAnalyzer
    {
        public Task<AnalysisResult> AnalyzeAsync(
            CapturedFrame frame,
            ScanRequest request,
            IProgress<ScanProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingRenderer : IResultRenderer
    {
        public List<ScanProgress> Progress { get; } = [];

        public List<ScanOutcome> Outcomes { get; } = [];

        public Task RenderProgressAsync(
            ScanProgress progress,
            CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }

        public Task RenderOutcomeAsync(
            ScanOutcome outcome,
            CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
}
