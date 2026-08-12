using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;
using VrcVa.Core;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows.Capture;

internal static partial class WindowsGraphicsCapture
{
    private static readonly Guid GraphicsCaptureItemId =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);
    private static readonly Lazy<IDirect3DDevice> CaptureDevice = new(
        CreateDirect3DDevice,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private const int D3dDriverTypeHardware = 1;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;

    internal static async Task<CapturedFrame> CaptureWindowAsync(
        IntPtr windowHandle,
        CancellationToken cancellationToken)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "このWindows環境ではウィンドウ単体キャプチャを利用できません。");
        }

        GraphicsCaptureItem item = CreateItemForWindow(windowHandle);
        using Direct3D11CaptureFramePool framePool =
            Direct3D11CaptureFramePool.CreateFreeThreaded(
                CaptureDevice.Value,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);
        using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);
        TaskCompletionSource<Direct3D11CaptureFrame> frameReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TypedEventHandler<Direct3D11CaptureFramePool, object> frameHandler =
            (sender, _) =>
            {
                Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is not null && !frameReady.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            };

        framePool.FrameArrived += frameHandler;
        try
        {
            session.StartCapture();
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FrameTimeout);
            using CancellationTokenRegistration registration = timeout.Token.Register(
                () => frameReady.TrySetCanceled(timeout.Token));

            Direct3D11CaptureFrame frame;
            try
            {
                frame = await frameReady.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ScanException(
                    ScanFailureCode.CaptureUnavailable,
                    ScanStage.Capture,
                    "VRChatウィンドウからキャプチャフレームを取得できませんでした。");
            }

            using (frame)
            using (SoftwareBitmap bitmap = await SoftwareBitmap
                .CreateCopyFromSurfaceAsync(frame.Surface)
                .AsTask(cancellationToken)
                .ConfigureAwait(false))
            {
                BitmapBounds clientBounds = GetClientCaptureBounds(
                    windowHandle,
                    bitmap.PixelWidth,
                    bitmap.PixelHeight);
                byte[] encoded = await EncodePngAsync(
                        bitmap,
                        clientBounds,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new CapturedFrame(
                    encoded,
                    checked((int)clientBounds.Width),
                    checked((int)clientBounds.Height),
                    "image/png",
                    "windows-graphics-capture-client-area");
            }
        }
        finally
        {
            framePool.FrameArrived -= frameHandler;
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        IGraphicsCaptureItemInterop interop =
            GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        IntPtr itemPointer = interop.CreateForWindow(
            windowHandle,
            GraphicsCaptureItemId);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        IntPtr nativeDevice = IntPtr.Zero;
        IntPtr nativeContext = IntPtr.Zero;
        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr inspectableDevice = IntPtr.Zero;
        try
        {
            int hresult = D3D11CreateDevice(
                IntPtr.Zero,
                D3dDriverTypeHardware,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out nativeDevice,
                out _,
                out nativeContext);
            Marshal.ThrowExceptionForHR(hresult);

            hresult = Marshal.QueryInterface(nativeDevice, in DxgiDeviceId, out dxgiDevice);
            Marshal.ThrowExceptionForHR(hresult);

            hresult = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice,
                out inspectableDevice);
            Marshal.ThrowExceptionForHR(hresult);

            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectableDevice);
        }
        finally
        {
            ReleaseIfPresent(inspectableDevice);
            ReleaseIfPresent(dxgiDevice);
            ReleaseIfPresent(nativeContext);
            ReleaseIfPresent(nativeDevice);
        }
    }

    private static BitmapBounds GetClientCaptureBounds(
        IntPtr windowHandle,
        int captureWidth,
        int captureHeight)
    {
        if (!NativeMethods.GetClientRect(windowHandle, out NativeMethods.Rect clientRect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeMethods.Point clientOrigin = new()
        {
            X = clientRect.Left,
            Y = clientRect.Top,
        };
        if (!NativeMethods.ClientToScreen(windowHandle, ref clientOrigin))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        int hresult = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out NativeMethods.Rect windowRect,
            (uint)Marshal.SizeOf<NativeMethods.Rect>());
        if (hresult != 0 && !NativeMethods.GetWindowRect(windowHandle, out windowRect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        int windowWidth = windowRect.Right - windowRect.Left;
        int windowHeight = windowRect.Bottom - windowRect.Top;
        int clientWidth = clientRect.Right - clientRect.Left;
        int clientHeight = clientRect.Bottom - clientRect.Top;
        if (windowWidth <= 0
            || windowHeight <= 0
            || clientWidth <= 0
            || clientHeight <= 0
            || captureWidth <= 0
            || captureHeight <= 0)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "VRChatの描画領域を特定できませんでした。");
        }

        double scaleX = captureWidth / (double)windowWidth;
        double scaleY = captureHeight / (double)windowHeight;
        int left = ScaleAndClamp(
            clientOrigin.X - windowRect.Left,
            scaleX,
            0,
            captureWidth - 1);
        int top = ScaleAndClamp(
            clientOrigin.Y - windowRect.Top,
            scaleY,
            0,
            captureHeight - 1);
        int right = ScaleAndClamp(
            clientOrigin.X + clientWidth - windowRect.Left,
            scaleX,
            left + 1,
            captureWidth);
        int bottom = ScaleAndClamp(
            clientOrigin.Y + clientHeight - windowRect.Top,
            scaleY,
            top + 1,
            captureHeight);

        return new BitmapBounds
        {
            X = checked((uint)left),
            Y = checked((uint)top),
            Width = checked((uint)(right - left)),
            Height = checked((uint)(bottom - top)),
        };
    }

    private static int ScaleAndClamp(
        int value,
        double scale,
        int minimum,
        int maximum) =>
        Math.Clamp(
            checked((int)Math.Round(value * scale, MidpointRounding.AwayFromZero)),
            minimum,
            maximum);

    private static async Task<byte[]> EncodePngAsync(
        SoftwareBitmap bitmap,
        BitmapBounds bounds,
        CancellationToken cancellationToken)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.BitmapTransform.Bounds = bounds;
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync()
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        int length = checked((int)stream.Size);
        byte[] encoded = new byte[length];
        using IInputStream input = stream.GetInputStreamAt(0);
        using DataReader reader = new(input);
        uint loaded = await reader.LoadAsync((uint)length)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (loaded != length)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "キャプチャ画像をメモリへ読み込めませんでした。");
        }

        reader.ReadBytes(encoded);
        return encoded;
    }

    private static void ReleaseIfPresent(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.Release(value);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint selectedFeatureLevel,
        out IntPtr immediateContext);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid interfaceId);

        IntPtr CreateForMonitor(IntPtr monitor, in Guid interfaceId);
    }
}
