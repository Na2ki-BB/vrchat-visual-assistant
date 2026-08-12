using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcVa.Core;
using VrcVa.Windows.Capture;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Diagnostics;

internal static class OpenVrEyeMirrorDiagnosticRunner
{
    private const string SaveOption = "--save-eye-mirror";
    private const int MarkerWidth = 256;
    private const int MarkerHeight = 256;

    public static async Task<int> RunAsync(string[] arguments)
    {
        string? saveDirectory = ParseSaveDirectory(arguments);
        if (saveDirectory is not null)
        {
            Directory.CreateDirectory(saveDirectory);
            Console.WriteLine($"明示された保存先へ診断画像を書き出します: {saveDirectory}");
        }
        else
        {
            Console.WriteLine("画像は保存しません。メモリ上で診断します。");
        }

        if (!OpenVrInterop.TryCreate(out OpenVrInterop? overlay))
        {
            Console.Error.WriteLine(
                "SteamVRが起動していないため、アイミラー診断を開始できません。SteamVRは自動起動しません。");
            return 2;
        }

        using OpenVrInterop activeOverlay = overlay!;
        {
            if (!OpenVrEyeMirrorCapture.TryCreate(out OpenVrEyeMirrorCapture? capture))
            {
                Console.Error.WriteLine("SteamVRのOpenVRコンポジタを取得できませんでした。");
                return 2;
            }

            using OpenVrEyeMirrorCapture activeCapture = capture!;
        {
                Console.WriteLine($"OpenVR compositor adapter LUID: 0x{activeCapture.AdapterLuid:X16}");
                Console.WriteLine($"HMD activity: {activeCapture.HmdActivityLevel}");
                OverlayMarkerCounts baselineLeftMarker;
                OverlayMarkerCounts baselineRightMarker;
                OverlayMarkerCounts visibleLeftMarker;
                OverlayMarkerCounts visibleRightMarker;
                OverlayMarkerCounts discardedLeftMarker;
                OverlayMarkerCounts discardedRightMarker;

                activeOverlay.HideAndConfirmInvisible(CancellationToken.None);
                activeOverlay.WaitFrameSync();
                activeOverlay.WaitFrameSync();
                activeOverlay.WaitFrameSync();
                using (OpenVrEyeMirrorFrame baselineLeft = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Left,
                    CancellationToken.None))
                using (OpenVrEyeMirrorFrame baselineRight = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Right,
                    CancellationToken.None))
                {
                    baselineLeftMarker = CountMarkerPixels(baselineLeft);
                    baselineRightMarker = CountMarkerPixels(baselineRight);
                }

                byte[] marker = CreateMarkerPixels();
                try
                {
                    activeOverlay.SetImage(marker, MarkerWidth, MarkerHeight);
                    activeOverlay.Show();
                    activeOverlay.WaitFrameSync();
                    activeOverlay.WaitFrameSync();

                    using OpenVrEyeMirrorFrame visibleLeftPrime = activeCapture.CaptureForDiagnostic(
                        OpenVrEye.Left,
                        CancellationToken.None);
                    using OpenVrEyeMirrorFrame visibleRightPrime = activeCapture.CaptureForDiagnostic(
                        OpenVrEye.Right,
                        CancellationToken.None);
                    activeOverlay.WaitFrameSync();
                    using OpenVrEyeMirrorFrame visibleLeft = activeCapture.CaptureForDiagnostic(
                        OpenVrEye.Left,
                        CancellationToken.None);
                    using OpenVrEyeMirrorFrame visibleRight = activeCapture.CaptureForDiagnostic(
                        OpenVrEye.Right,
                        CancellationToken.None);
                    visibleLeftMarker = CountMarkerPixels(visibleLeft);
                    visibleRightMarker = CountMarkerPixels(visibleRight);
                }
                finally
                {
                    Array.Clear(marker);
                }

                activeOverlay.HideAndConfirmInvisible(CancellationToken.None);
                activeOverlay.WaitFrameSync();
                activeOverlay.WaitFrameSync();
                activeOverlay.WaitFrameSync();
                using (OpenVrEyeMirrorFrame discardedLeft = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Left,
                    CancellationToken.None))
                using (OpenVrEyeMirrorFrame discardedRight = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Right,
                    CancellationToken.None))
                {
                    discardedLeftMarker = CountMarkerPixels(discardedLeft);
                    discardedRightMarker = CountMarkerPixels(discardedRight);
                }

                activeOverlay.WaitFrameSync();
                using OpenVrEyeMirrorFrame left = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Left,
                    CancellationToken.None);
                using OpenVrEyeMirrorFrame right = activeCapture.CaptureForDiagnostic(
                    OpenVrEye.Right,
                    CancellationToken.None);

                OverlayMarkerCounts hiddenLeftMarker = CountMarkerPixels(left);
                OverlayMarkerCounts hiddenRightMarker = CountMarkerPixels(right);
                bool overlayExcluded = IsOverlayExcluded(
                    baselineLeftMarker,
                    visibleLeftMarker,
                    hiddenLeftMarker)
                    && IsOverlayExcluded(
                        baselineRightMarker,
                        visibleRightMarker,
                        hiddenRightMarker);

                PrintFrame(left);
                PrintFrame(right);
                PrintOverlayCheck(
                    baselineLeftMarker,
                    visibleLeftMarker,
                    discardedLeftMarker,
                    hiddenLeftMarker,
                    baselineRightMarker,
                    visibleRightMarker,
                    discardedRightMarker,
                    hiddenRightMarker,
                    overlayExcluded);

                WindowCaptureMeasurement? windowMeasurement =
                    await MeasureWindowCaptureAsync(saveDirectory).ConfigureAwait(false);
                if (windowMeasurement is not null)
                {
                    Console.WriteLine(
                        $"現行ウィンドウ: {windowMeasurement.Width}x{windowMeasurement.Height}, "
                        + $"PNG, {windowMeasurement.Elapsed.TotalMilliseconds:F1} ms");
                    Console.WriteLine(
                        $"解像度比（左目/現行）: "
                        + $"{left.Width / (double)windowMeasurement.Width:F2}x × "
                        + $"{left.Height / (double)windowMeasurement.Height:F2}x");
                }

                if (saveDirectory is not null)
                {
                    SavePng(left, Path.Combine(saveDirectory, "eye-left.png"));
                    SavePng(right, Path.Combine(saveDirectory, "eye-right.png"));
                    Console.WriteLine("eye-left.png と eye-right.png を保存しました。");
                }

                if (!overlayExcluded)
                {
                    Console.Error.WriteLine(
                        "失敗: 非表示後のアイミラーに診断オーバーレイの色パターンが残っています。");
                    return 5;
                }

                Console.WriteLine(
                    "第1段階の自動取得は成功しました。左右画像の視野比較とGPU負荷は実機で確認してください。");
                return 0;
            }
        }
    }

    private static string? ParseSaveDirectory(string[] arguments)
    {
        if (arguments.Length == 1)
        {
            return null;
        }

        if (arguments.Length != 3
            || !arguments[1].Equals(SaveOption, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(arguments[2]))
        {
            throw new ArgumentException(
                $"使用法: --openvr-eye-mirror-check [{SaveOption} <保存先フォルダー>]");
        }

        return Path.GetFullPath(arguments[2]);
    }

    private static async Task<WindowCaptureMeasurement?> MeasureWindowCaptureAsync(
        string? saveDirectory)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            using CapturedFrame frame = await new VrChatWindowCaptureSource().CaptureAsync(
                ScanRequest.Create("openvr-eye-mirror-diagnostic"),
                CancellationToken.None).ConfigureAwait(false);
            timer.Stop();

            if (saveDirectory is not null)
            {
                string path = Path.Combine(saveDirectory, "window-current.png");
                byte[] encoded = frame.EncodedImage.ToArray();
                try
                {
                    await using FileStream stream = new(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);
                    await stream.WriteAsync(encoded).ConfigureAwait(false);
                    Console.WriteLine("window-current.png を保存しました。");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            return new WindowCaptureMeasurement(frame.Width, frame.Height, timer.Elapsed);
        }
        catch (ScanException exception)
        {
            Console.WriteLine(
                $"現行ウィンドウとの比較は取得できませんでした [{exception.FailureCode}]。"
                + "アイミラー単体の成否には影響しません。");
            return null;
        }
    }

    private static void PrintFrame(OpenVrEyeMirrorFrame frame)
    {
        string eye = frame.Eye == OpenVrEye.Left ? "左目" : "右目";
        Console.WriteLine(
            $"{eye}: {frame.Width}x{frame.Height}, "
            + $"DXGI {frame.Format} ({(int)frame.Format}), "
            + $"SRV {frame.ViewDimension} slice={frame.ArraySlice}, "
            + $"取得+GPU読戻し {frame.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static void PrintOverlayCheck(
        OverlayMarkerCounts baselineLeft,
        OverlayMarkerCounts visibleLeft,
        OverlayMarkerCounts discardedLeft,
        OverlayMarkerCounts hiddenLeft,
        OverlayMarkerCounts baselineRight,
        OverlayMarkerCounts visibleRight,
        OverlayMarkerCounts discardedRight,
        OverlayMarkerCounts hiddenRight,
        bool excluded)
    {
        Console.WriteLine(
            "オーバーレイ色パターン候補 "
            + $"左目 baseline={baselineLeft.MinimumChannelCount}, "
            + $"visible={visibleLeft.MinimumChannelCount}, "
            + $"discarded={discardedLeft.MinimumChannelCount}, "
            + $"hidden={hiddenLeft.MinimumChannelCount}; "
            + $"右目 baseline={baselineRight.MinimumChannelCount}, "
            + $"visible={visibleRight.MinimumChannelCount}, "
            + $"discarded={discardedRight.MinimumChannelCount}, "
            + $"hidden={hiddenRight.MinimumChannelCount}");
        Console.WriteLine(excluded
            ? "オーバーレイ写り込み検査: PASS（非表示後の自己ループ要因なし）"
            : "オーバーレイ写り込み検査: FAIL");
    }

    private static byte[] CreateMarkerPixels()
    {
        byte[] pixels = new byte[MarkerWidth * MarkerHeight * 4];
        for (int y = 0; y < MarkerHeight; y++)
        {
            for (int x = 0; x < MarkerWidth; x++)
            {
                int tile = ((x / 32) + (y / 32)) % 4;
                int offset = ((y * MarkerWidth) + x) * 4;
                (pixels[offset], pixels[offset + 1], pixels[offset + 2]) = tile switch
                {
                    0 => ((byte)255, (byte)0, (byte)255),
                    1 => ((byte)0, (byte)255, (byte)255),
                    2 => ((byte)255, (byte)255, (byte)0),
                    _ => ((byte)0, (byte)255, (byte)0),
                };
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static OverlayMarkerCounts CountMarkerPixels(OpenVrEyeMirrorFrame frame)
    {
        ReadOnlySpan<byte> pixels = frame.BgraPixels.Span;
        int magenta = 0;
        int cyan = 0;
        int yellow = 0;
        int green = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte blue = pixels[offset];
            byte greenValue = pixels[offset + 1];
            byte red = pixels[offset + 2];
            if (red >= 210 && blue >= 210 && greenValue <= 60)
            {
                magenta++;
            }
            else if (greenValue >= 210 && blue >= 210 && red <= 60)
            {
                cyan++;
            }
            else if (red >= 210 && greenValue >= 210 && blue <= 60)
            {
                yellow++;
            }
            else if (greenValue >= 210 && red <= 60 && blue <= 60)
            {
                green++;
            }
        }

        return new OverlayMarkerCounts(magenta, cyan, yellow, green);
    }

    private static bool IsOverlayExcluded(
        OverlayMarkerCounts baseline,
        OverlayMarkerCounts visible,
        OverlayMarkerCounts hidden)
    {
        long visibleDifference = visible.DifferenceFrom(baseline);
        long hiddenDifference = hidden.DifferenceFrom(baseline);
        if (visibleDifference < 512)
        {
            // This eye did not receive the HMD-relative diagnostic panel. Its hidden
            // frame must still remain close to the pre-panel baseline.
            return hiddenDifference < 512;
        }

        return hiddenDifference <= Math.Max(512, visibleDifference / 10);
    }

    private static void SavePng(OpenVrEyeMirrorFrame frame, string path)
    {
        byte[] pixels = frame.BgraPixels.ToArray();
        try
        {
            BitmapSource bitmap = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                checked(frame.Width * 4));
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            encoder.Save(stream);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private sealed record WindowCaptureMeasurement(
        int Width,
        int Height,
        TimeSpan Elapsed);

    private readonly record struct OverlayMarkerCounts(
        int Magenta,
        int Cyan,
        int Yellow,
        int Green)
    {
        public int MinimumChannelCount => Math.Min(
            Math.Min(Magenta, Cyan),
            Math.Min(Yellow, Green));

        public long DifferenceFrom(OverlayMarkerCounts other) =>
            Math.Abs((long)Magenta - other.Magenta)
            + Math.Abs((long)Cyan - other.Cyan)
            + Math.Abs((long)Yellow - other.Yellow)
            + Math.Abs((long)Green - other.Green);
    }
}
