using System.IO;
using System.Text;
using System.Windows;
using VrcVa.Core;
using VrcVa.Windows.Diagnostics;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        if (eventArgs.Args.Length > 0
            && eventArgs.Args[0].Equals(
                "--capture-route-check",
                StringComparison.OrdinalIgnoreCase))
        {
            AttachDiagnosticConsole();
            int exitCode;
            try
            {
                exitCode = await CaptureBackendBenchmarkRunner.RunRouteCheckAsync(Dispatcher);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"キャプチャ経路診断に失敗しました: {exception.GetType().Name}: {exception.Message}");
                exitCode = 8;
            }

            Shutdown(exitCode);
            return;
        }

        if (eventArgs.Args.Length > 0
            && eventArgs.Args[0].Equals(
                "--capture-backend-benchmark",
                StringComparison.OrdinalIgnoreCase))
        {
            AttachDiagnosticConsole();
            int exitCode;
            try
            {
                exitCode = await CaptureBackendBenchmarkRunner.RunAsync(
                    Dispatcher,
                    eventArgs.Args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"キャプチャ経路比較に失敗しました: {exception.GetType().Name}: {exception.Message}");
                exitCode = 7;
            }

            Shutdown(exitCode);
            return;
        }

        if (eventArgs.Args.Length > 0
            && eventArgs.Args[0].Equals(
                "--openvr-eye-mirror-check",
                StringComparison.OrdinalIgnoreCase))
        {
            AttachDiagnosticConsole();
            int exitCode;
            try
            {
                exitCode = await OpenVrEyeMirrorDiagnosticRunner.RunAsync(eventArgs.Args);
            }
            catch (Exception exception)
            {
                await OpenVrEyeMirrorDiagnosticRunner.TrySendFailureNotificationAsync();
                Console.Error.WriteLine(
                    $"OpenVRアイミラー診断に失敗しました: {exception.GetType().Name}: {exception.Message}");
                exitCode = 6;
            }

            Shutdown(exitCode);
            return;
        }

        if (eventArgs.Args.Length > 0
            && eventArgs.Args[0].Equals(
                "--steamvr-overlay-check",
                StringComparison.OrdinalIgnoreCase))
        {
            AttachDiagnosticConsole();
            int exitCode;
            try
            {
                exitCode = await OpenVrDiagnosticRunner.RunAsync(Dispatcher);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"SteamVR overlay diagnostic failed: {exception.GetType().Name}");
                exitCode = 4;
            }

            Shutdown(exitCode);
            return;
        }

        if (eventArgs.Args.Length > 0
            && (eventArgs.Args[0].Equals("--ocr-file", StringComparison.OrdinalIgnoreCase)
                || eventArgs.Args[0].Equals(
                    "--capture-vrchat-ocr",
                    StringComparison.OrdinalIgnoreCase)
                || eventArgs.Args[0].Equals(
                    "--capture-vrchat-check",
                    StringComparison.OrdinalIgnoreCase)))
        {
            AttachDiagnosticConsole();
            int exitCode;
            try
            {
                exitCode = await OcrDiagnosticRunner.RunAsync(eventArgs.Args);
            }
            catch (ScanException exception)
            {
                Console.Error.WriteLine($"OCR diagnostic failed [{exception.FailureCode}]: {exception.UserMessage}");
                exitCode = 2;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"OCR diagnostic failed unexpectedly: {exception.GetType().Name}");
                exitCode = 3;
            }

            Shutdown(exitCode);
            return;
        }

        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }

    private static void AttachDiagnosticConsole()
    {
        NativeMethods.AttachConsole(unchecked((uint)-1));
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // A GUI-subsystem launch may not have an attachable console.
            // `dotnet VrcVa.dll ...` is the documented diagnostic path.
        }
    }
}
