using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VrcVa.Windows.OpenVr;

internal sealed class OpenVrEyeMirrorCapture : IDisposable
{
    private const string SystemInterface = "FnTable:IVRSystem_026";
    private const string CompositorInterface = "FnTable:IVRCompositor_029";

    private readonly OpenVrRuntime _runtime;
    private readonly OpenVrD3D11Device _d3dDevice;
    private readonly GetMirrorTextureD3D11Delegate _getMirrorTexture;
    private readonly ReleaseMirrorTextureD3D11Delegate _releaseMirrorTexture;
    private bool _disposed;

    private OpenVrEyeMirrorCapture(
        OpenVrRuntime runtime,
        OpenVrD3D11Device d3dDevice,
        OpenVrDeviceActivityLevel hmdActivityLevel,
        GetMirrorTextureD3D11Delegate getMirrorTexture,
        ReleaseMirrorTextureD3D11Delegate releaseMirrorTexture)
    {
        _runtime = runtime;
        _d3dDevice = d3dDevice;
        HmdActivityLevel = hmdActivityLevel;
        _getMirrorTexture = getMirrorTexture;
        _releaseMirrorTexture = releaseMirrorTexture;
    }

    public ulong AdapterLuid => _d3dDevice.AdapterLuid;

    public OpenVrDeviceActivityLevel HmdActivityLevel { get; }

    public static bool TryCreate(out OpenVrEyeMirrorCapture? capture)
    {
        capture = null;
        if (!OpenVrRuntime.TryAcquire(out OpenVrRuntime? runtime))
        {
            return false;
        }

        OpenVrD3D11Device? d3dDevice = null;
        try
        {
            IntPtr systemTable = runtime!.GetInterface(SystemInterface);
            GetOutputDeviceDelegate getOutputDevice =
                OpenVrRuntime.GetFunction<GetOutputDeviceDelegate>(
                    systemTable,
                    SystemSlot.GetOutputDevice);
            ulong adapterLuid = 0;
            getOutputDevice(ref adapterLuid, OpenVrTextureType.DirectX, IntPtr.Zero);
            if (adapterLuid == 0)
            {
                throw new InvalidOperationException(
                    "IVRSystem.GetOutputDevice returned an empty DirectX adapter LUID.");
            }

            d3dDevice = OpenVrD3D11Device.Create(adapterLuid);

            GetTrackedDeviceActivityLevelDelegate getActivityLevel =
                OpenVrRuntime.GetFunction<GetTrackedDeviceActivityLevelDelegate>(
                    systemTable,
                    SystemSlot.GetTrackedDeviceActivityLevel);
            OpenVrDeviceActivityLevel hmdActivityLevel = getActivityLevel(0);

            IntPtr compositorTable = runtime.GetInterface(CompositorInterface);
            GetMirrorTextureD3D11Delegate getMirror =
                OpenVrRuntime.GetFunction<GetMirrorTextureD3D11Delegate>(
                    compositorTable,
                    CompositorSlot.GetMirrorTextureD3D11);
            ReleaseMirrorTextureD3D11Delegate releaseMirror =
                OpenVrRuntime.GetFunction<ReleaseMirrorTextureD3D11Delegate>(
                    compositorTable,
                    CompositorSlot.ReleaseMirrorTextureD3D11);

            capture = new OpenVrEyeMirrorCapture(
                runtime,
                d3dDevice,
                hmdActivityLevel,
                getMirror,
                releaseMirror);
            return true;
        }
        catch
        {
            d3dDevice?.Dispose();
            runtime?.Dispose();
            throw;
        }
    }

    public OpenVrEyeMirrorFrame CaptureAfterOverlayHidden(
        OpenVrInterop overlay,
        OpenVrEye eye,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        cancellationToken.ThrowIfCancellationRequested();

        overlay.HideAndConfirmInvisible(cancellationToken);
        // The compositor can have more than one queued eye image. Cross three
        // boundaries so a pre-hide composite cannot remain in the mirror view.
        overlay.WaitFrameSync();
        overlay.WaitFrameSync();
        overlay.WaitFrameSync();
        cancellationToken.ThrowIfCancellationRequested();
        using (CaptureForDiagnostic(eye, cancellationToken))
        {
            // GetMirrorTextureD3D11 can expose the previous composite on its first
            // acquisition after an overlay transition. Never analyze that frame.
        }

        overlay.WaitFrameSync();
        return CaptureForDiagnostic(eye, cancellationToken);
    }

    public OpenVrEyeMirrorFrame CaptureForDiagnostic(
        OpenVrEye eye,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        IntPtr mirrorView = IntPtr.Zero;
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            EvrCompositorError error = _getMirrorTexture(
                eye,
                _d3dDevice.DevicePointer,
                ref mirrorView);
            if (error != EvrCompositorError.None || mirrorView == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"GetMirrorTextureD3D11 failed with code {(int)error}.");
            }

            OpenVrEyeMirrorFrame frame = _d3dDevice.ReadMirrorView(
                mirrorView,
                eye);
            frame.SetElapsed(timer.Elapsed);
            cancellationToken.ThrowIfCancellationRequested();
            return frame;
        }
        finally
        {
            if (mirrorView != IntPtr.Zero)
            {
                // Valve owns this view. It must not be released through COM.
                _releaseMirrorTexture(mirrorView);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _d3dDevice.Dispose();
        _runtime.Dispose();
    }

    private static class SystemSlot
    {
        public const int GetOutputDevice = 9;
        public const int GetTrackedDeviceActivityLevel = 16;
    }

    private static class CompositorSlot
    {
        public const int GetMirrorTextureD3D11 = 35;
        public const int ReleaseMirrorTextureD3D11 = 36;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetOutputDeviceDelegate(
        ref ulong device,
        OpenVrTextureType textureType,
        IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate OpenVrDeviceActivityLevel GetTrackedDeviceActivityLevelDelegate(
        uint trackedDeviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrCompositorError GetMirrorTextureD3D11Delegate(
        OpenVrEye eye,
        IntPtr d3d11DeviceOrResource,
        ref IntPtr shaderResourceView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ReleaseMirrorTextureD3D11Delegate(IntPtr shaderResourceView);

    private enum OpenVrTextureType
    {
        DirectX = 0,
    }

    private enum EvrCompositorError
    {
        None = 0,
    }
}

internal enum OpenVrEye
{
    Left = 0,
    Right = 1,
}

internal enum OpenVrDeviceActivityLevel
{
    Unknown = -1,
    Idle = 0,
    UserInteraction = 1,
    UserInteractionTimeout = 2,
    Standby = 3,
    IdleTimeout = 4,
}
