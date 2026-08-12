using System.Diagnostics;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Capture;
using VrcVa.Windows.Ocr;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Diagnostics;

internal static class CaptureBackendBenchmarkRunner
{
    private const string WaitForScanOption = "--wait-for-scan";

    public static async Task<int> RunAsync(
        Dispatcher dispatcher,
        string[] arguments)
    {
        if (arguments.Length is < 1 or > 2
            || (arguments.Length == 2
                && !arguments[1].Equals(
                    WaitForScanOption,
                    StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "使用方法: VrcVa.dll --capture-backend-benchmark [--wait-for-scan]");
            return 64;
        }

        OpenVrEyeCaptureOptions eyeOptions = OpenVrEyeCaptureOptions.FromEnvironment(
            out string? warning);
        if (warning is not null)
        {
            Console.WriteLine($"警告: {warning}");
        }

        SteamVrResultPanel panel = new(dispatcher);
        try
        {
            if (arguments.Length == 2)
            {
                await OpenVrEyeMirrorDiagnosticRunner.WaitForScanAsync().ConfigureAwait(true);
            }

            using CapturedFrame eyeFrame = await CaptureTimedAsync(
                new OpenVrEyeCaptureSource(dispatcher, panel, eyeOptions),
                "SteamVRアイミラー").ConfigureAwait(false);
            using CapturedFrame windowFrame = await CaptureTimedAsync(
                new VrChatWindowCaptureSource(),
                "VRChatウィンドウ").ConfigureAwait(false);

            await TrySendCaptureCompleteNotificationAsync().ConfigureAwait(false);
            Console.WriteLine("画像は保存せず、OCR本文も表示しません。");
            await BenchmarkFrameAsync("SteamVRアイミラー", eyeFrame).ConfigureAwait(false);
            await BenchmarkFrameAsync("VRChatウィンドウ", windowFrame).ConfigureAwait(false);
            Console.WriteLine("測定が完了しました。");
            return 0;
        }
        finally
        {
            await dispatcher.InvokeAsync(panel.Dispose, DispatcherPriority.Normal);
        }
    }

    public static async Task<int> RunRouteCheckAsync(Dispatcher dispatcher)
    {
        OpenVrEyeCaptureOptions eyeOptions = OpenVrEyeCaptureOptions.FromEnvironment(out _);
        SteamVrResultPanel panel = new(dispatcher);
        try
        {
            ICaptureSource source = new FallbackCaptureSource(
                new OpenVrEyeCaptureSource(dispatcher, panel, eyeOptions),
                new VrChatWindowCaptureSource(value => Console.WriteLine(
                    $"WGC寸法: item={value.ItemWidth}x{value.ItemHeight}, "
                    + $"content={value.ContentWidth}x{value.ContentHeight}, "
                    + $"bitmap={value.BitmapWidth}x{value.BitmapHeight}, "
                    + $"client={value.ClientWidth}x{value.ClientHeight}")));
            using CapturedFrame frame = await source.CaptureAsync(
                ScanRequest.Create("capture-route-check"),
                CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(
                $"取得経路: {CaptureSourceDisplayName.Get(frame.SourceKind)}; "
                + $"寸法: {frame.Width}x{frame.Height}");
            Console.WriteLine("画像は保存せず、OCRも実行していません。");
            return 0;
        }
        finally
        {
            await dispatcher.InvokeAsync(panel.Dispose, DispatcherPriority.Normal);
        }
    }

    private static async Task<CapturedFrame> CaptureTimedAsync(
        ICaptureSource source,
        string displayName)
    {
        Stopwatch timer = Stopwatch.StartNew();
        CapturedFrame frame = await source.CaptureAsync(
            ScanRequest.Create("capture-backend-benchmark"),
            CancellationToken.None).ConfigureAwait(false);
        timer.Stop();
        Console.WriteLine(
            $"{displayName}取得: {frame.Width}x{frame.Height}, "
            + $"{timer.Elapsed.TotalMilliseconds:F1} ms, source={frame.SourceKind}");
        return frame;
    }

    private static async Task BenchmarkFrameAsync(
        string sourceName,
        CapturedFrame frame)
    {
        Console.WriteLine($"[{sourceName}] OCR比較を開始します。");
        OcrBenchmark adaptive = await RunStepAsync(
            "適応 全体パイプライン",
            () => MeasureAdaptivePipelineAsync(
                frame,
                OcrBitmapScaleMode.Adaptive)).ConfigureAwait(false);
        OcrBenchmark legacy = await RunStepAsync(
            "従来2倍 全体パイプライン",
            () => MeasureAdaptivePipelineAsync(
                frame,
                OcrBitmapScaleMode.LegacyTwoTimes)).ConfigureAwait(false);
        OcrBenchmark adaptiveBands = await RunStepAsync(
            "適応 強制3帯",
            () => MeasureForcedBandsAsync(
                frame,
                OcrBitmapScaleMode.Adaptive)).ConfigureAwait(false);
        OcrBenchmark legacyBands = await RunStepAsync(
            "従来2倍 強制3帯",
            () => MeasureForcedBandsAsync(
                frame,
                OcrBitmapScaleMode.LegacyTwoTimes)).ConfigureAwait(false);

        PrintMeasurement("適応 全体パイプライン", adaptive);
        PrintMeasurement("従来2倍 全体パイプライン", legacy);
        PrintMeasurement("適応 強制3帯", adaptiveBands);
        PrintMeasurement("従来2倍 強制3帯", legacyBands);
        Console.WriteLine(
            $"結果一致: 全体={string.Equals(adaptive.Text, legacy.Text, StringComparison.Ordinal)}, "
            + $"3帯={string.Equals(adaptiveBands.Text, legacyBands.Text, StringComparison.Ordinal)}");
    }

    private static async Task<OcrBenchmark> RunStepAsync(
        string stepName,
        Func<Task<OcrBenchmark>> action)
    {
        Console.WriteLine($"  実行中: {stepName}");
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{stepName} に失敗しました ({exception.GetType().Name})。",
                exception);
        }
    }

    private static async Task<OcrBenchmark> MeasureAdaptivePipelineAsync(
        CapturedFrame frame,
        OcrBitmapScaleMode scaleMode)
    {
        IOcrEngine engine = new AdaptiveOcrEngine(
            new WindowsOcrEngine(scaleMode),
            new WindowsOcrRegionSource());
        Stopwatch timer = Stopwatch.StartNew();
        OcrOutput output = await engine
            .RecognizeAsync(frame, CancellationToken.None)
            .ConfigureAwait(false);
        timer.Stop();
        return CreateMeasurement(output.Text, timer.Elapsed);
    }

    private static async Task<OcrBenchmark> MeasureForcedBandsAsync(
        CapturedFrame frame,
        OcrBitmapScaleMode scaleMode)
    {
        Stopwatch timer = Stopwatch.StartNew();
        IReadOnlyList<CapturedFrame> regions = await new WindowsOcrRegionSource()
            .CreateRegionsAsync(frame, CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            WindowsOcrEngine engine = new(scaleMode);
            List<string> outputs = [];
            foreach (CapturedFrame region in regions)
            {
                OcrOutput output = await engine
                    .RecognizeAsync(region, CancellationToken.None)
                    .ConfigureAwait(false);
                outputs.Add(output.Text);
            }

            timer.Stop();
            return CreateMeasurement(
                string.Join(Environment.NewLine, outputs),
                timer.Elapsed);
        }
        finally
        {
            foreach (CapturedFrame region in regions)
            {
                region.Dispose();
            }
        }
    }

    private static OcrBenchmark CreateMeasurement(string text, TimeSpan duration)
    {
        int lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length;
        int asciiCharacters = text.Count(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9');
        return new OcrBenchmark(text, duration, lines, asciiCharacters);
    }

    private static void PrintMeasurement(string name, OcrBenchmark measurement) =>
        Console.WriteLine(
            $"  {name}: {measurement.Duration.TotalMilliseconds:F1} ms, "
            + $"行={measurement.LineCount}, ASCII英数字={measurement.AsciiCharacterCount}");

    private static async Task TrySendCaptureCompleteNotificationAsync()
    {
        try
        {
            await new XsOverlayUdpNotificationSink().SendAsync(
                "比較用キャプチャ完了",
                "両経路の取得が終わりました。ヘッドセットを外して構いません。OCR比較はPC内で継続します。",
                XsOverlayNotificationKind.Result,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // XSOverlay is optional; console measurement continues.
        }
    }

    private sealed record OcrBenchmark(
        string Text,
        TimeSpan Duration,
        int LineCount,
        int AsciiCharacterCount);
}
