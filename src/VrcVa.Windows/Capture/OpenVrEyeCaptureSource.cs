using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Capture;

internal sealed class OpenVrEyeCaptureSource(
    Dispatcher dispatcher,
    SteamVrResultPanel resultPanel,
    OpenVrEyeCaptureOptions options) : ICaptureSource
{
    public async Task<CapturedFrame> CaptureAsync(
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            IOpenVrOverlayCaptureGate? overlay = await dispatcher.InvokeAsync(
                () => resultPanel.TryGetCaptureOverlay(out IOpenVrOverlayCaptureGate? value)
                    ? value
                    : null,
                DispatcherPriority.Normal,
                cancellationToken);
            if (overlay is null
                || !OpenVrEyeMirrorCapture.TryCreate(out OpenVrEyeMirrorCapture? capture))
            {
                throw CreateUnavailableException();
            }

            using (capture)
            using (OpenVrEyeMirrorFrame frame = capture!.CaptureAfterOverlayHidden(
                overlay,
                options.Eye,
                cancellationToken))
            {
                byte[] encoded = EncodePng(frame);
                return new CapturedFrame(
                    encoded,
                    frame.Width,
                    frame.Height,
                    "image/png",
                    options.Eye == OpenVrEye.Left
                        ? "openvr-eye-mirror-left"
                        : "openvr-eye-mirror-right");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScanException)
        {
            throw;
        }
        catch (Exception exception) when (IsAcquisitionFailure(exception))
        {
            throw CreateUnavailableException(exception);
        }
    }

    private static byte[] EncodePng(OpenVrEyeMirrorFrame frame)
    {
        byte[] pixels = frame.BgraPixels.ToArray();
        try
        {
            BitmapSource bitmap = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                checked(frame.Width * 4));
            bitmap.Freeze();
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using MemoryStream stream = new();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private static ScanException CreateUnavailableException(Exception? innerException = null) =>
        innerException is null
        ? new ScanException(
            ScanFailureCode.CaptureUnavailable,
            ScanStage.Capture,
            "SteamVRのアイミラーを取得できなかったため、VRChatウィンドウ取得を試します。")
        : new ScanException(
            ScanFailureCode.CaptureUnavailable,
            ScanStage.Capture,
            "SteamVRのアイミラーを取得できなかったため、VRChatウィンドウ取得を試します。",
            innerException);

    private static bool IsAcquisitionFailure(Exception exception) =>
        exception is InvalidOperationException
            or ExternalException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or NotSupportedException
            or ArgumentException
            or System.ComponentModel.Win32Exception;
}
