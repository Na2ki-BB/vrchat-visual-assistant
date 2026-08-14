namespace VrcVa.Windows.OpenVr;

internal interface IOpenVrOverlayCaptureGate
{
    void HideAndConfirmInvisible(CancellationToken cancellationToken);

    void EndCaptureSuppression();

    void WaitFrameSync(uint timeoutMilliseconds = 1000);
}

internal static class OpenVrOverlayCaptureSequence
{
    private const int PreDiscardFrameBoundaries = 3;

    internal static T CaptureAfterOverlayHidden<T>(
        IOpenVrOverlayCaptureGate overlay,
        Func<T> captureFrame,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(captureFrame);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            overlay.HideAndConfirmInvisible(cancellationToken);
            for (int index = 0; index < PreDiscardFrameBoundaries; index++)
            {
                overlay.WaitFrameSync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (captureFrame())
            {
                // Quest 3S testing proved the first acquisition can still expose the
                // old overlay composite even after multiple compositor boundaries.
                // This throwaway acquisition is a privacy and self-OCR invariant.
            }

            overlay.WaitFrameSync();
            cancellationToken.ThrowIfCancellationRequested();
            return captureFrame();
        }
        finally
        {
            overlay.EndCaptureSuppression();
        }
    }
}
