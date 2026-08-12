using System.Runtime.CompilerServices;

namespace VrcVa.Windows.Win32;

internal static class DpiAwarenessInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // `dotnet VrcVa.dll` does not inherit VrcVa.exe's manifest. Set the same
        // process context before WPF or a diagnostic creates any window; false is
        // expected when the executable manifest already selected an awareness mode.
        _ = NativeMethods.SetProcessDpiAwarenessContext(
            NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
    }
}
