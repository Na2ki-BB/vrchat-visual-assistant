namespace VrcVa.Core.Tests;

public sealed class FallbackCaptureSourceTests
{
    [Fact]
    public async Task CaptureAsync_ReturnsPrimaryWithoutCallingFallback()
    {
        RecordingCaptureSource primary = new("primary");
        RecordingCaptureSource fallback = new("fallback");
        FallbackCaptureSource source = new(primary, fallback);

        using CapturedFrame frame = await source.CaptureAsync(
            ScanRequest.Create("test"),
            CancellationToken.None);

        Assert.Equal("primary", frame.SourceKind);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task CaptureAsync_UsesFallbackWhenPrimaryCaptureIsUnavailable()
    {
        ThrowingCaptureSource primary = new(new ScanException(
            ScanFailureCode.CaptureUnavailable,
            ScanStage.Capture,
            "primary unavailable"));
        RecordingCaptureSource fallback = new("fallback");
        FallbackCaptureSource source = new(primary, fallback);

        using CapturedFrame frame = await source.CaptureAsync(
            ScanRequest.Create("test"),
            CancellationToken.None);

        Assert.Equal("fallback", frame.SourceKind);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotFallbackAfterCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellingCaptureSource primary = new();
        RecordingCaptureSource fallback = new("fallback");
        FallbackCaptureSource source = new(primary, fallback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CaptureAsync(
            ScanRequest.Create("test"),
            cancellation.Token));

        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotHideUnexpectedPrimaryFailure()
    {
        ThrowingCaptureSource primary = new(new InvalidOperationException("bug"));
        RecordingCaptureSource fallback = new("fallback");
        FallbackCaptureSource source = new(primary, fallback);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CaptureAsync(
            ScanRequest.Create("test"),
            CancellationToken.None));

        Assert.Equal(0, fallback.CallCount);
    }

    private sealed class RecordingCaptureSource(string sourceKind) : ICaptureSource
    {
        public int CallCount { get; private set; }

        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new CapturedFrame([1], 1, 1, "image/png", sourceKind));
        }
    }

    private sealed class ThrowingCaptureSource(Exception exception) : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken) => Task.FromException<CapturedFrame>(exception);
    }

    private sealed class CancellingCaptureSource : ICaptureSource
    {
        public Task<CapturedFrame> CaptureAsync(
            ScanRequest request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<CapturedFrame>(cancellationToken);
    }
}
