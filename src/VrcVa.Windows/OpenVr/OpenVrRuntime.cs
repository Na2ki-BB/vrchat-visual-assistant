using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VrcVa.Windows.OpenVr;

/// <summary>
/// Owns the process-wide OpenVR initialization. Every consumer receives a lease so
/// one adapter cannot call VR_ShutdownInternal while another is still using it.
/// </summary>
internal sealed class OpenVrRuntime : IDisposable
{
    private static readonly object Sync = new();
    private static RuntimeState? _state;
    private static int _leaseCount;

    private RuntimeState? _leasedState;

    private OpenVrRuntime(RuntimeState state)
    {
        _leasedState = state;
    }

    public static bool TryAcquire(out OpenVrRuntime? runtime)
    {
        runtime = null;
        lock (Sync)
        {
            if (_state is null)
            {
                string? libraryPath = FindRunningSteamVrLibrary();
                if (libraryPath is null)
                {
                    return false;
                }

                _state = RuntimeState.Create(libraryPath);
            }

            checked
            {
                _leaseCount++;
            }

            runtime = new OpenVrRuntime(_state);
            return true;
        }
    }

    public IntPtr GetInterface(string version)
    {
        RuntimeState state = _leasedState
            ?? throw new ObjectDisposedException(nameof(OpenVrRuntime));
        return state.GetInterface(version);
    }

    public void Dispose()
    {
        RuntimeState? state = Interlocked.Exchange(ref _leasedState, null);
        if (state is null)
        {
            return;
        }

        lock (Sync)
        {
            if (!ReferenceEquals(state, _state) || _leaseCount <= 0)
            {
                throw new InvalidOperationException("The shared OpenVR lifetime is inconsistent.");
            }

            _leaseCount--;
            if (_leaseCount == 0)
            {
                _state = null;
                state.Dispose();
            }
        }
    }

    internal static T GetFunction<T>(IntPtr functionTable, int slot)
        where T : Delegate
    {
        IntPtr function = Marshal.ReadIntPtr(functionTable, slot * IntPtr.Size);
        if (function == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"OpenVR function table slot {slot} is unavailable.");
        }

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
                    // Another running vrserver candidate may still be readable.
                }
            }
        }

        return null;
    }

    private sealed class RuntimeState : IDisposable
    {
        private readonly IntPtr _libraryHandle;
        private readonly ShutdownDelegate _shutdown;
        private readonly GetGenericInterfaceDelegate _getInterface;
        private bool _disposed;

        private RuntimeState(
            IntPtr libraryHandle,
            ShutdownDelegate shutdown,
            GetGenericInterfaceDelegate getInterface)
        {
            _libraryHandle = libraryHandle;
            _shutdown = shutdown;
            _getInterface = getInterface;
        }

        public static RuntimeState Create(string libraryPath)
        {
            IntPtr libraryHandle = IntPtr.Zero;
            ShutdownDelegate? shutdown = null;
            try
            {
                libraryHandle = NativeLibrary.Load(libraryPath);
                InitDelegate init = LoadExport<InitDelegate>(libraryHandle, "VR_InitInternal2");
                shutdown = LoadExport<ShutdownDelegate>(libraryHandle, "VR_ShutdownInternal");
                GetGenericInterfaceDelegate getInterface =
                    LoadExport<GetGenericInterfaceDelegate>(libraryHandle, "VR_GetGenericInterface");

                EvrInitError error = EvrInitError.None;
                _ = init(ref error, EvrApplicationType.Overlay, string.Empty);
                if (error != EvrInitError.None)
                {
                    throw new InvalidOperationException(
                        $"OpenVR initialization failed with code {(int)error}.");
                }

                return new RuntimeState(libraryHandle, shutdown, getInterface);
            }
            catch
            {
                shutdown?.Invoke();
                if (libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(libraryHandle);
                }

                throw;
            }
        }

        public IntPtr GetInterface(string version)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EvrInitError error = EvrInitError.None;
            IntPtr table = _getInterface(version, ref error);
            if (table == IntPtr.Zero || error != EvrInitError.None)
            {
                throw new InvalidOperationException(
                    $"OpenVR interface {version} failed with code {(int)error}.");
            }

            return table;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown();
            NativeLibrary.Free(_libraryHandle);
        }

        private static T LoadExport<T>(IntPtr libraryHandle, string name)
            where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libraryHandle, name));
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

    private enum EvrApplicationType
    {
        Overlay = 2,
    }

    private enum EvrInitError
    {
        None = 0,
    }
}
