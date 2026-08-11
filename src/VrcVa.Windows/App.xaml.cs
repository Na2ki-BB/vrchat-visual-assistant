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
            && (eventArgs.Args[0].Equals("--ocr-file", StringComparison.OrdinalIgnoreCase)
                || eventArgs.Args[0].Equals(
                    "--capture-vrchat-ocr",
                    StringComparison.OrdinalIgnoreCase)))
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
}
