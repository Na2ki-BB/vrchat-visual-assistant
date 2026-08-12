namespace VrcVa.Core;

public sealed class FallbackCaptureSource(
    ICaptureSource primary,
    ICaptureSource fallback) : ICaptureSource
{
    public async Task<CapturedFrame> CaptureAsync(
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await primary
                .CaptureAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ScanException exception) when (CanFallback(exception, cancellationToken))
        {
            return await fallback
                .CaptureAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool CanFallback(
        ScanException exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception.Stage == ScanStage.Capture
        && exception.FailureCode is ScanFailureCode.CaptureUnavailable
            or ScanFailureCode.CaptureTargetNotFound;
}
