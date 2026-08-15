using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcVa.Windows.OpenVr;

/// <summary>
/// Minimal IVRApplications binding used only to register VRCVA for SteamVR auto-launch.
/// </summary>
internal sealed class OpenVrApplicationsInterop : IOpenVrApplications
{
    internal const string ApplicationsInterface = "FnTable:IVRApplications_007";

    private readonly IDisposable _runtimeLease;
    private readonly AddApplicationManifestDelegate _addApplicationManifest;
    private readonly IsApplicationInstalledDelegate _isApplicationInstalled;
    private readonly SetApplicationAutoLaunchDelegate _setApplicationAutoLaunch;
    private readonly GetApplicationAutoLaunchDelegate _getApplicationAutoLaunch;
    private bool _disposed;

    internal OpenVrApplicationsInterop(IDisposable runtimeLease, IntPtr applicationsFunctionTable)
    {
        ArgumentNullException.ThrowIfNull(runtimeLease);
        if (applicationsFunctionTable == IntPtr.Zero)
        {
            throw new ArgumentException(
                "The OpenVR applications function table is unavailable.",
                nameof(applicationsFunctionTable));
        }

        _runtimeLease = runtimeLease;
        _addApplicationManifest = OpenVrRuntime.GetFunction<AddApplicationManifestDelegate>(
            applicationsFunctionTable,
            ApplicationsSlot.AddApplicationManifest);
        _isApplicationInstalled = OpenVrRuntime.GetFunction<IsApplicationInstalledDelegate>(
            applicationsFunctionTable,
            ApplicationsSlot.IsApplicationInstalled);
        _setApplicationAutoLaunch = OpenVrRuntime.GetFunction<SetApplicationAutoLaunchDelegate>(
            applicationsFunctionTable,
            ApplicationsSlot.SetApplicationAutoLaunch);
        _getApplicationAutoLaunch = OpenVrRuntime.GetFunction<GetApplicationAutoLaunchDelegate>(
            applicationsFunctionTable,
            ApplicationsSlot.GetApplicationAutoLaunch);
    }

    public OpenVrApplicationError AddApplicationManifest(string manifestPath, bool temporary)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        return _addApplicationManifest(manifestPath, temporary);
    }

    public bool IsApplicationInstalled(string applicationKey)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationKey);
        return _isApplicationInstalled(applicationKey);
    }

    public OpenVrApplicationError SetApplicationAutoLaunch(string applicationKey, bool enabled)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationKey);
        return _setApplicationAutoLaunch(applicationKey, enabled);
    }

    public bool GetApplicationAutoLaunch(string applicationKey)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationKey);
        return _getApplicationAutoLaunch(applicationKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtimeLease.Dispose();
    }

    internal static OpenVrApplicationsConnectionResult TryCreate(
        out IOpenVrApplications? applications)
    {
        applications = null;
        OpenVrRuntime? runtime = null;
        try
        {
            if (!OpenVrRuntime.TryAcquire(out runtime))
            {
                return OpenVrApplicationsConnectionResult.Unavailable;
            }
        }
        catch (Exception exception) when (IsRuntimeUnavailable(exception))
        {
            return OpenVrApplicationsConnectionResult.Unavailable;
        }

        try
        {
            IntPtr table = runtime!.GetInterface(ApplicationsInterface);
            applications = new OpenVrApplicationsInterop(runtime, table);
            return OpenVrApplicationsConnectionResult.Connected;
        }
        catch
        {
            runtime?.Dispose();
            return OpenVrApplicationsConnectionResult.Failed;
        }
    }

    private static bool IsRuntimeUnavailable(Exception exception) =>
        exception is InvalidOperationException
            or FileNotFoundException
            or DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException
            or Win32Exception
            or NotSupportedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // Valve OpenVR SDK v1.26.7, generated openvr_capi.h,
    // VR_IVRApplications_FnTable. Slot 16 is GetApplicationPropertyUint64;
    // Set/GetApplicationAutoLaunch follow it at 17/18. Published OpenVR
    // interfaces remain available for backwards compatibility.
    internal static class ApplicationsSlot
    {
        public const int AddApplicationManifest = 0;
        public const int IsApplicationInstalled = 2;
        public const int SetApplicationAutoLaunch = 17;
        public const int GetApplicationAutoLaunch = 18;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate OpenVrApplicationError AddApplicationManifestDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string manifestPath,
        [MarshalAs(UnmanagedType.I1)] bool temporary);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsApplicationInstalledDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationKey);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate OpenVrApplicationError SetApplicationAutoLaunchDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationKey,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool GetApplicationAutoLaunchDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string applicationKey);
}

internal interface IOpenVrApplications : IDisposable
{
    OpenVrApplicationError AddApplicationManifest(string manifestPath, bool temporary);

    bool IsApplicationInstalled(string applicationKey);

    OpenVrApplicationError SetApplicationAutoLaunch(string applicationKey, bool enabled);

    bool GetApplicationAutoLaunch(string applicationKey);
}

internal interface IOpenVrApplicationsFactory
{
    OpenVrApplicationsConnectionResult TryCreate(out IOpenVrApplications? applications);
}

internal sealed class OpenVrApplicationsFactory : IOpenVrApplicationsFactory
{
    public OpenVrApplicationsConnectionResult TryCreate(out IOpenVrApplications? applications) =>
        OpenVrApplicationsInterop.TryCreate(out applications);
}

internal enum OpenVrApplicationsConnectionResult
{
    Connected,
    Unavailable,
    Failed,
}

internal enum OpenVrApplicationError
{
    None = 0,
    AppKeyAlreadyExists = 100,
    NoManifest = 101,
    NoApplication = 102,
    InvalidIndex = 103,
    UnknownApplication = 104,
    IpcFailed = 105,
    ApplicationAlreadyRunning = 106,
    InvalidManifest = 107,
    InvalidApplication = 108,
    LaunchFailed = 109,
    ApplicationAlreadyStarting = 110,
    LaunchInProgress = 111,
    OldApplicationQuitting = 112,
    TransitionAborted = 113,
    IsTemplate = 114,
    SteamVrIsExiting = 115,
    BufferTooSmall = 200,
    PropertyNotSet = 201,
    UnknownProperty = 202,
    InvalidParameter = 203,
    NotImplemented = 300,
}
