using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcVa.Windows.OpenVr;

internal sealed class OpenVrInterop : IDisposable
{
    private const string OverlayInterface = "FnTable:IVROverlay_027";
    private const ulong InvalidOverlayHandle = 0;
    private const uint HmdTrackedDeviceIndex = 0;
    private const int VrEventSize = 64;

    private readonly IntPtr _libraryHandle;
    private readonly ShutdownDelegate _shutdown;
    private readonly CreateOverlayDelegate _createOverlay;
    private readonly DestroyOverlayDelegate _destroyOverlay;
    private readonly HideOverlayDelegate _hideOverlay;
    private readonly SetOverlayWidthInMetersDelegate _setOverlayWidthInMeters;
    private readonly SetOverlayTransformTrackedDeviceRelativeDelegate _setOverlayTransform;
    private readonly SetOverlayInputMethodDelegate _setOverlayInputMethod;
    private readonly SetOverlayMouseScaleDelegate _setOverlayMouseScale;
    private readonly SetOverlayFlagDelegate _setOverlayFlag;
    private readonly SetOverlayRawDelegate _setOverlayRaw;
    private readonly ShowOverlayDelegate _showOverlay;
    private readonly PollNextOverlayEventDelegate _pollNextOverlayEvent;
    private ulong _overlayHandle;
    private bool _disposed;

    private OpenVrInterop(
        IntPtr libraryHandle,
        ShutdownDelegate shutdown,
        IntPtr overlayFunctionTable)
    {
        if (Marshal.SizeOf<VrEvent>() != VrEventSize)
        {
            throw new InvalidOperationException("The OpenVR event ABI layout is invalid.");
        }

        _libraryHandle = libraryHandle;
        _shutdown = shutdown;
        _createOverlay = GetFunction<CreateOverlayDelegate>(overlayFunctionTable, OverlaySlot.CreateOverlay);
        _destroyOverlay = GetFunction<DestroyOverlayDelegate>(overlayFunctionTable, OverlaySlot.DestroyOverlay);
        _setOverlayFlag = GetFunction<SetOverlayFlagDelegate>(overlayFunctionTable, OverlaySlot.SetOverlayFlag);
        _setOverlayWidthInMeters = GetFunction<SetOverlayWidthInMetersDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayWidthInMeters);
        _setOverlayTransform = GetFunction<SetOverlayTransformTrackedDeviceRelativeDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayTransformTrackedDeviceRelative);
        _showOverlay = GetFunction<ShowOverlayDelegate>(overlayFunctionTable, OverlaySlot.ShowOverlay);
        _hideOverlay = GetFunction<HideOverlayDelegate>(overlayFunctionTable, OverlaySlot.HideOverlay);
        _pollNextOverlayEvent = GetFunction<PollNextOverlayEventDelegate>(
            overlayFunctionTable,
            OverlaySlot.PollNextOverlayEvent);
        _setOverlayInputMethod = GetFunction<SetOverlayInputMethodDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayInputMethod);
        _setOverlayMouseScale = GetFunction<SetOverlayMouseScaleDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayMouseScale);
        _setOverlayRaw = GetFunction<SetOverlayRawDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayRaw);
    }

    public static bool TryCreate(out OpenVrInterop? interop)
    {
        interop = null;
        string? libraryPath = FindRunningSteamVrLibrary();
        if (libraryPath is null)
        {
            return false;
        }

        IntPtr libraryHandle = IntPtr.Zero;
        ShutdownDelegate? shutdown = null;
        OpenVrInterop? candidate = null;
        try
        {
            libraryHandle = NativeLibrary.Load(libraryPath);
            InitDelegate init = LoadExport<InitDelegate>(libraryHandle, "VR_InitInternal2");
            shutdown = LoadExport<ShutdownDelegate>(libraryHandle, "VR_ShutdownInternal");
            GetGenericInterfaceDelegate getInterface =
                LoadExport<GetGenericInterfaceDelegate>(libraryHandle, "VR_GetGenericInterface");

            EvrInitError initError = EvrInitError.None;
            _ = init(ref initError, EvrApplicationType.Overlay, string.Empty);
            if (initError != EvrInitError.None)
            {
                throw new InvalidOperationException($"OpenVR initialization failed with code {(int)initError}.");
            }

            EvrInitError interfaceError = EvrInitError.None;
            IntPtr table = getInterface(OverlayInterface, ref interfaceError);
            if (table == IntPtr.Zero || interfaceError != EvrInitError.None)
            {
                throw new InvalidOperationException(
                    $"OpenVR overlay interface failed with code {(int)interfaceError}.");
            }

            candidate = new OpenVrInterop(libraryHandle, shutdown, table);
            candidate.CreateAndConfigureOverlay();
            interop = candidate;
            return true;
        }
        catch
        {
            if (candidate is not null)
            {
                candidate.Dispose();
            }
            else
            {
                shutdown?.Invoke();
                if (libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(libraryHandle);
                }
            }

            throw;
        }
    }

    public void SetImage(ReadOnlySpan<byte> rgbaPixels, uint width, uint height)
    {
        ThrowIfDisposed();
        if (rgbaPixels.Length != checked((int)(width * height * 4)))
        {
            throw new ArgumentException("The RGBA buffer size does not match its dimensions.", nameof(rgbaPixels));
        }

        unsafe
        {
            fixed (byte* buffer = rgbaPixels)
            {
                EnsureSuccess(_setOverlayRaw(
                    _overlayHandle,
                    (IntPtr)buffer,
                    width,
                    height,
                    4));
            }
        }
    }

    public void Show()
    {
        ThrowIfDisposed();
        EnsureSuccess(_showOverlay(_overlayHandle));
    }

    public void Hide()
    {
        if (_disposed || _overlayHandle == InvalidOverlayHandle)
        {
            return;
        }

        _ = _hideOverlay(_overlayHandle);
    }

    public bool TryPollEvent(out OpenVrEvent overlayEvent)
    {
        ThrowIfDisposed();
        VrEvent nativeEvent = default;
        if (!_pollNextOverlayEvent(
                _overlayHandle,
                ref nativeEvent,
                VrEventSize))
        {
            overlayEvent = default;
            return false;
        }

        overlayEvent = new OpenVrEvent(
            nativeEvent.EventType,
            nativeEvent.Data.Mouse.X,
            nativeEvent.Data.Mouse.Y,
            nativeEvent.Data.Mouse.Button,
            nativeEvent.Data.Scroll.YDelta);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_overlayHandle != InvalidOverlayHandle)
        {
            _ = _hideOverlay(_overlayHandle);
            _ = _destroyOverlay(_overlayHandle);
            _overlayHandle = InvalidOverlayHandle;
        }

        _shutdown();
        NativeLibrary.Free(_libraryHandle);
    }

    private void CreateAndConfigureOverlay()
    {
        IntPtr key = Marshal.StringToCoTaskMemUTF8("com.na2kibb.vrcva.result");
        IntPtr name = Marshal.StringToCoTaskMemUTF8("VRChat Visual Assistant Result");
        try
        {
            EnsureSuccess(_createOverlay(key, name, ref _overlayHandle));
        }
        finally
        {
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(name);
        }

        EnsureSuccess(_setOverlayWidthInMeters(_overlayHandle, 1.15f));
        HmdMatrix34 transform = HmdMatrix34.IdentityAt(0, -0.08f, -1.2f);
        EnsureSuccess(_setOverlayTransform(
            _overlayHandle,
            HmdTrackedDeviceIndex,
            ref transform));

        EnsureSuccess(_setOverlayInputMethod(_overlayHandle, VrOverlayInputMethod.Mouse));
        HmdVector2 mouseScale = new(1280, 720);
        EnsureSuccess(_setOverlayMouseScale(_overlayHandle, ref mouseScale));
        SetFlag(VrOverlayFlag.MakeOverlaysInteractiveIfVisible);
        SetFlag(VrOverlayFlag.SendVrDiscreteScrollEvents);
        SetFlag(VrOverlayFlag.SendVrSmoothScrollEvents);
        SetFlag(VrOverlayFlag.EnableControlBar);
        SetFlag(VrOverlayFlag.EnableControlBarClose);
        SetFlag(VrOverlayFlag.EnableClickStabilization);
    }

    private void SetFlag(VrOverlayFlag flag) =>
        EnsureSuccess(_setOverlayFlag(_overlayHandle, flag, true));

    private static T LoadExport<T>(IntPtr libraryHandle, string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libraryHandle, name));

    private static T GetFunction<T>(IntPtr functionTable, int slot)
        where T : Delegate
    {
        IntPtr function = Marshal.ReadIntPtr(functionTable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static string? FindRunningSteamVrLibrary()
    {
        foreach (Process process in Process.GetProcessesByName("vrserver"))
        {
            using (process)
            {
                try
                {
                    string? executable = process.MainModule?.FileName;
                    if (executable is null)
                    {
                        continue;
                    }

                    string candidate = Path.Combine(
                        Path.GetDirectoryName(executable)!,
                        "openvr_api.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception exception) when (
                    exception is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or NotSupportedException)
                {
                    // Another candidate may still be readable. No process details are logged.
                }
            }
        }

        return null;
    }

    private static void EnsureSuccess(EvrOverlayError error)
    {
        if (error != EvrOverlayError.None)
        {
            throw new InvalidOperationException($"OpenVR overlay call failed with code {(int)error}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // Slots are from Valve's generated IVROverlay_027 function table. Published
    // OpenVR interface versions remain available for backwards compatibility.
    private static class OverlaySlot
    {
        public const int CreateOverlay = 1;
        public const int DestroyOverlay = 2;
        public const int SetOverlayFlag = 10;
        public const int SetOverlayWidthInMeters = 21;
        public const int SetOverlayTransformTrackedDeviceRelative = 34;
        public const int ShowOverlay = 41;
        public const int HideOverlay = 42;
        public const int PollNextOverlayEvent = 46;
        public const int SetOverlayInputMethod = 48;
        public const int SetOverlayMouseScale = 50;
        public const int SetOverlayRaw = 60;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr InitDelegate(
        ref EvrInitError error,
        EvrApplicationType applicationType,
        [MarshalAs(UnmanagedType.LPStr)] string startupInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate IntPtr GetGenericInterfaceDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string version,
        ref EvrInitError error);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError CreateOverlayDelegate(
        IntPtr key,
        IntPtr name,
        ref ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError DestroyOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayFlagDelegate(
        ulong overlayHandle,
        VrOverlayFlag flag,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayWidthInMetersDelegate(
        ulong overlayHandle,
        float widthInMeters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayTransformTrackedDeviceRelativeDelegate(
        ulong overlayHandle,
        uint trackedDevice,
        ref HmdMatrix34 transform);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError ShowOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError HideOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool PollNextOverlayEventDelegate(
        ulong overlayHandle,
        ref VrEvent overlayEvent,
        uint eventSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayInputMethodDelegate(
        ulong overlayHandle,
        VrOverlayInputMethod inputMethod);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayMouseScaleDelegate(
        ulong overlayHandle,
        ref HmdVector2 mouseScale);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayRawDelegate(
        ulong overlayHandle,
        IntPtr buffer,
        uint width,
        uint height,
        uint bytesPerPixel);

    private enum EvrApplicationType
    {
        Overlay = 2,
    }

    private enum EvrInitError
    {
        None = 0,
    }

    private enum EvrOverlayError
    {
        None = 0,
    }

    private enum VrOverlayInputMethod
    {
        Mouse = 1,
    }

    [Flags]
    private enum VrOverlayFlag
    {
        SendVrDiscreteScrollEvents = 64,
        MakeOverlaysInteractiveIfVisible = 65536,
        SendVrSmoothScrollEvents = 131072,
        EnableControlBar = 8388608,
        EnableControlBarClose = 33554432,
        EnableClickStabilization = 134217728,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdMatrix34
    {
        public float M0;
        public float M1;
        public float M2;
        public float M3;
        public float M4;
        public float M5;
        public float M6;
        public float M7;
        public float M8;
        public float M9;
        public float M10;
        public float M11;

        public static HmdMatrix34 IdentityAt(float x, float y, float z) => new()
        {
            M0 = 1,
            M3 = x,
            M5 = 1,
            M7 = y,
            M10 = 1,
            M11 = z,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector2(float x, float y)
    {
        public float X = x;
        public float Y = y;
    }

    // On 64-bit Windows, VREvent_Data_t is 8-byte aligned because the native
    // union includes uint64 members. The payload therefore starts at byte 16,
    // and the complete VREvent_t is 64 bytes. Using a sequential managed union
    // containing only the mouse/scroll members incorrectly produces 60 bytes;
    // SteamVR then rejects PollNextOverlayEvent without returning any events.
    [StructLayout(LayoutKind.Explicit, Size = VrEventSize)]
    private struct VrEvent
    {
        [FieldOffset(0)]
        public uint EventType;

        [FieldOffset(4)]
        public uint TrackedDeviceIndex;

        [FieldOffset(8)]
        public float EventAgeSeconds;

        [FieldOffset(16)]
        public VrEventData Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct VrEventData
    {
        [FieldOffset(0)]
        public VrEventMouse Mouse;

        [FieldOffset(0)]
        public VrEventScroll Scroll;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrEventMouse
    {
        public float X;
        public float Y;
        public uint Button;
        public uint CursorIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrEventScroll
    {
        public float XDelta;
        public float YDelta;
        public uint Unused;
        public float ViewportScale;
        public uint CursorIndex;
    }
}

internal readonly record struct OpenVrEvent(
    uint EventType,
    float MouseX,
    float MouseY,
    uint MouseButton,
    float ScrollY)
{
    public const uint MouseButtonDown = 301;
    public const uint ScrollDiscrete = 305;
    public const uint ScrollSmooth = 309;
    public const uint OverlayClosed = 534;
}
