using System.Runtime.InteropServices;
using VrcVa.Core;

namespace VrcVa.Windows.Voice;

// Source: Windows SDK mmeapi.h/mmddk.h and mmdeviceapi.h (Microsoft win32metadata).
internal static class WinMmInterop
{
    internal const uint WaveMapper = uint.MaxValue;
    internal const uint WaveFormatQuery = 0x0001;
    internal const uint DefaultCommunicationDevice = 0x0010;
    internal const uint CallbackEvent = 0x00050000;
    internal const uint HeaderDone = 0x0001;
    internal const uint HeaderPrepared = 0x0002;
    internal const uint HeaderInQueue = 0x0010;
    internal const uint QueryFunctionInstanceId = 0x0811;
    internal const uint QueryFunctionInstanceIdSize = 0x0812;
    internal const uint NoError = 0;
    internal const uint BadDeviceId = 2;
    internal const uint NotEnabled = 3;
    internal const uint InvalidHandle = 5;
    internal const uint NoDriver = 6;
    internal const uint BadFormat = 32;
    internal const uint StillPlaying = 33;
    internal const uint AccessDenied = 0x80070005;
    internal const uint DeviceStateActive = 0x0001;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormat
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlignment;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        internal nint Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal nuint User;
        internal uint Flags;
        internal uint Loops;
        internal nint Next;
        internal nuint Reserved;
    }

    internal static WaveFormat CreateFormat() => new()
    {
        FormatTag = 1,
        Channels = VoiceAudioFormat.Channels,
        SamplesPerSecond = VoiceAudioFormat.SampleRate,
        AverageBytesPerSecond = VoiceAudioFormat.BytesPerSecond,
        BlockAlignment = VoiceAudioFormat.BlockAlignment,
        BitsPerSample = VoiceAudioFormat.BitsPerSample,
    };

    internal static VoiceInputFailureCode ClassifyError(uint error, bool recording) => error switch
    {
        BadFormat => VoiceInputFailureCode.FormatUnsupported,
        AccessDenied => VoiceInputFailureCode.MicrophoneAccessDenied,
        BadDeviceId or NotEnabled or NoDriver or InvalidHandle => recording
            ? VoiceInputFailureCode.DeviceLost
            : VoiceInputFailureCode.DeviceUnavailable,
        // WinMM does not document a distinct microphone-privacy-denied MMRESULT.
        // A generic MMSYSERR_ERROR is not evidence of access denial.
        _ => VoiceInputFailureCode.RecordingFailed,
    };

    internal static void Check(uint result, bool recording = false)
    {
        if (result != NoError)
        {
            throw new VoiceInputException(ClassifyError(result, recording));
        }
    }

    internal static unsafe string GetEndpointId(IWinMmApi api, nint handle)
    {
        Check(api.GetId(handle, out uint deviceId));
        nuint bytes = 0;
        Check(api.Message((nint)deviceId, QueryFunctionInstanceIdSize, (nuint)(&bytes), 0));
        // Endpoint IDs are opaque; only validate bounded UTF-16 storage, never parse them.
        if (bytes is < 2 or > 8192 || bytes % sizeof(char) != 0)
        {
            throw new VoiceInputException(VoiceInputFailureCode.DeviceUnavailable);
        }

        nint storage = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            new Span<byte>((void*)storage, (int)bytes).Clear();
            Check(api.Message((nint)deviceId, QueryFunctionInstanceId, (nuint)storage, bytes));
            ReadOnlySpan<char> characters = new((void*)storage, (int)bytes / sizeof(char));
            int end = characters.IndexOf('\0');
            if (end <= 0)
            {
                throw new VoiceInputException(VoiceInputFailureCode.DeviceUnavailable);
            }

            return characters[..end].ToString();
        }
        finally
        {
            new Span<byte>((void*)storage, (int)bytes).Clear();
            Marshal.FreeHGlobal(storage);
        }
    }
}

internal interface IWinMmApi
{
    uint Open(out nint handle, uint deviceId, ref WinMmInterop.WaveFormat format, nuint callback, uint flags);
    uint GetId(nint handle, out uint deviceId);
    uint Message(nint handleOrId, uint message, nuint parameter1, nuint parameter2);
    uint Prepare(nint handle, nint header, uint headerBytes);
    uint AddBuffer(nint handle, nint header, uint headerBytes);
    uint Start(nint handle);
    uint Stop(nint handle);
    uint Reset(nint handle);
    uint Unprepare(nint handle, nint header, uint headerBytes);
    uint Close(nint handle);
}

internal sealed class WinMmApi : IWinMmApi
{
    public uint Open(out nint handle, uint deviceId, ref WinMmInterop.WaveFormat format, nuint callback, uint flags) =>
        waveInOpen(out handle, deviceId, ref format, callback, 0, flags);
    public uint GetId(nint handle, out uint deviceId) => waveInGetID(handle, out deviceId);
    public uint Message(nint handleOrId, uint message, nuint parameter1, nuint parameter2) =>
        waveInMessage(handleOrId, message, parameter1, parameter2);
    public uint Prepare(nint handle, nint header, uint headerBytes) => waveInPrepareHeader(handle, header, headerBytes);
    public uint AddBuffer(nint handle, nint header, uint headerBytes) => waveInAddBuffer(handle, header, headerBytes);
    public uint Start(nint handle) => waveInStart(handle);
    public uint Stop(nint handle) => waveInStop(handle);
    public uint Reset(nint handle) => waveInReset(handle);
    public uint Unprepare(nint handle, nint header, uint headerBytes) => waveInUnprepareHeader(handle, header, headerBytes);
    public uint Close(nint handle) => waveInClose(handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInOpen(out nint handle, uint deviceId, ref WinMmInterop.WaveFormat format,
        nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInGetID(nint handle, out uint deviceId);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInMessage(nint handle, uint message, nuint parameter1, nuint parameter2);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInPrepareHeader(nint handle, nint header, uint headerBytes);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInAddBuffer(nint handle, nint header, uint headerBytes);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInStart(nint handle);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInStop(nint handle);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInReset(nint handle);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInUnprepareHeader(nint handle, nint header, uint headerBytes);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint waveInClose(nint handle);
}

internal interface IRecordingEndpoint : IDisposable
{
    bool IsAvailable { get; }
}

/// <summary>
/// Owned, queried, and released on the capture worker. Notifications only latch
/// invalidation; they never call WinMM or access audio buffers.
/// </summary>
internal sealed class CoreAudioRecordingEndpoint : IRecordingEndpoint
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private readonly WinMmEndpointNotifications _notifications;
    private nint _notificationPointer;
    private bool _registered;
    private bool _comInitialized;

    internal CoreAudioRecordingEndpoint(string endpointId)
    {
        _notifications = new WinMmEndpointNotifications(endpointId);
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        _comInitialized = true;
        try
        {
            Guid classId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Guid interfaceId = typeof(IMMDeviceEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 1, ref interfaceId, out _enumerator));
            Marshal.ThrowExceptionForHR(_enumerator.GetDevice(endpointId, out _device));
            // Register/Unregister do not AddRef/Release the callback. Own one
            // explicit CCW reference and pass that same pointer to both methods.
            _notificationPointer = Marshal.GetComInterfaceForObject<WinMmEndpointNotifications, IWinMmNotificationClient>(
                _notifications);
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_notificationPointer));
            _registered = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool IsAvailable
    {
        get
        {
            if (_notifications.Invalidated || _device is null || _enumerator is null)
            {
                return false;
            }

            if (_device.GetState(out uint state) < 0 || state != WinMmInterop.DeviceStateActive)
            {
                return false;
            }

            // Wave APIs can transparently reroute a default-device stream. Fail
            // rather than accept samples from a newly selected communication mic.
            int result = _enumerator.GetDefaultAudioEndpoint(1, 2, out IMMDevice current);
            if (result < 0)
            {
                return false;
            }

            nint id = 0;
            try
            {
                return current.GetId(out id) >= 0
                    && string.Equals(Marshal.PtrToStringUni(id), _notifications.EndpointId,
                        StringComparison.OrdinalIgnoreCase)
                    && !_notifications.Invalidated;
            }
            finally
            {
                Marshal.FreeCoTaskMem(id);
                Marshal.ReleaseComObject(current);
            }
        }
    }

    public void Dispose()
    {
        if (!_comInitialized)
        {
            return;
        }

        if (_registered)
        {
            if (_enumerator!.UnregisterEndpointNotificationCallback(_notificationPointer) < 0)
            {
                // Notifications may still arrive. Keep the explicit callback
                // reference and endpoint objects alive in the worker's quarantine.
                throw new VoiceInputException(VoiceInputFailureCode.MicrophoneCleanupFailed);
            }

            _registered = false;
        }

        if (_notificationPointer != 0)
        {
            Marshal.Release(_notificationPointer);
            _notificationPointer = 0;
        }

        if (_device is not null)
        {
            Marshal.ReleaseComObject(_device);
            _device = null;
        }

        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }

        CoUninitialize();
        _comInitialized = false;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, out nint value);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId(out nint id);
        [PreserveSig] int GetState(out uint state);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IMMDeviceEnumerator enumerator);
}

// CCW-exposed types must be top-level public; ComVisible(true) cannot expose
// internal types. The callback constructor remains internal and never opens audio.
[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWinMmNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, WinMmPropertyKey key);
}

[ComVisible(true), StructLayout(LayoutKind.Sequential)]
public struct WinMmPropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class WinMmEndpointNotifications : IWinMmNotificationClient
{
    private int _invalidated;
    internal string EndpointId { get; }
    internal WinMmEndpointNotifications(string endpointId) => EndpointId = endpointId;
    internal bool Invalidated => Volatile.Read(ref _invalidated) != 0;
    public int OnDeviceStateChanged(string id, uint state)
    {
        if (Matches(id) && state != WinMmInterop.DeviceStateActive)
        {
            Volatile.Write(ref _invalidated, 1);
        }

        return 0;
    }

    public int OnDeviceAdded(string id) => 0;
    public int OnDeviceRemoved(string id)
    {
        if (Matches(id))
        {
            Volatile.Write(ref _invalidated, 1);
        }

        return 0;
    }

    public int OnDefaultDeviceChanged(int flow, int role, string? id)
    {
        if (flow == 1 && role == 2 && !Matches(id))
        {
            Volatile.Write(ref _invalidated, 1);
        }

        return 0;
    }

    public int OnPropertyValueChanged(string id, WinMmPropertyKey key) => 0;
    private bool Matches(string? id) => string.Equals(id, EndpointId, StringComparison.OrdinalIgnoreCase);
}
