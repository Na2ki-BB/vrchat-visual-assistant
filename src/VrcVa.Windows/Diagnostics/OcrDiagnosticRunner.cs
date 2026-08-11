using System.IO;
using VrcVa.Core;
using VrcVa.Windows.Capture;
using VrcVa.Windows.Ocr;

namespace VrcVa.Windows.Diagnostics;

internal static class OcrDiagnosticRunner
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length == 1
            && arguments[0].Equals(
                "--capture-vrchat-check",
                StringComparison.OrdinalIgnoreCase))
        {
            using CapturedFrame captured = await new VrChatWindowCaptureSource().CaptureAsync(
                ScanRequest.Create("capture-diagnostic"),
                CancellationToken.None);
            Console.WriteLine(
                $"Capture OK: {captured.Width}x{captured.Height}, "
                + $"{captured.EncodedImage.Length} bytes, source: {captured.SourceKind}");
            Console.WriteLine("No image was saved and no OCR text was printed.");
            return 0;
        }

        ICaptureSource captureSource;
        string? explicitCapturePath = null;
        if (arguments.Length == 2
            && arguments[0].Equals("--ocr-file", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(arguments[1]))
        {
            captureSource = new ImageFileCaptureSource(arguments[1]);
        }
        else if ((arguments.Length == 1 || arguments.Length == 3)
            && arguments[0].Equals("--capture-vrchat-ocr", StringComparison.OrdinalIgnoreCase))
        {
            captureSource = new VrChatWindowCaptureSource();
            if (arguments.Length == 3)
            {
                if (!arguments[1].Equals("--save-capture", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(arguments[2]))
                {
                    Console.Error.WriteLine(
                        "The optional capture export syntax is --save-capture <png-path>.");
                    return 64;
                }

                explicitCapturePath = arguments[2];
            }
        }
        else
        {
            Console.Error.WriteLine(
                "Usage: VrcVa.exe --ocr-file <image-path> | "
                + "--capture-vrchat-ocr [--save-capture <png-path>] | "
                + "--capture-vrchat-check");
            return 64;
        }

        WindowsOcrEngine ocrEngine = new();
        IReadOnlyList<string> languageTags = WindowsOcrEngine.GetAvailableLanguageTags();
        Console.WriteLine($"Available OCR languages: {string.Join(", ", languageTags)}");

        using CapturedFrame frame = await captureSource.CaptureAsync(
            ScanRequest.Create("ocr-diagnostic"),
            CancellationToken.None);
        if (explicitCapturePath is not null)
        {
            await File.WriteAllBytesAsync(
                explicitCapturePath,
                frame.EncodedImage.ToArray(),
                CancellationToken.None);
            Console.WriteLine($"Explicit diagnostic capture saved to: {explicitCapturePath}");
        }

        OcrOutput output = await ocrEngine.RecognizeAsync(frame, CancellationToken.None);

        Console.WriteLine($"Frame: {frame.Width}x{frame.Height}, recognizer: {output.RecognizerLanguage}");
        if (!string.IsNullOrWhiteSpace(output.Warning))
        {
            Console.WriteLine($"Warning: {output.Warning}");
        }

        Console.WriteLine("OCR output:");
        Console.WriteLine(output.Text);
        return string.IsNullOrWhiteSpace(output.Text) ? 1 : 0;
    }
}
