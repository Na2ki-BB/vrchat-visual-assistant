using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VrcVa.Core;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows.Capture;

internal sealed class VrChatWindowCaptureSource : ICaptureSource
{
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestorePollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RenderSettleDelay = TimeSpan.FromMilliseconds(300);

    public async Task<CapturedFrame> CaptureAsync(
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr handle = IntPtr.Zero;
        bool restoreMinimizedState = false;

        try
        {
            handle = FindCaptureTargetHandle();
            restoreMinimizedState = NativeMethods.IsIconic(handle);
            if (restoreMinimizedState)
            {
                await RestoreForCaptureAsync(handle, cancellationToken).ConfigureAwait(false);
            }

            (int width, int height, int x, int y) = GetCaptureBounds(handle);

            using System.Drawing.Bitmap bitmap = new(
                width,
                height,
                PixelFormat.Format32bppArgb);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    x,
                    y,
                    0,
                    0,
                    new System.Drawing.Size(width, height),
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }

            using MemoryStream encoded = new();
            bitmap.Save(encoded, ImageFormat.Png);
            return new CapturedFrame(
                encoded.ToArray(),
                width,
                height,
                "image/png",
                restoreMinimizedState
                    ? "vrchat-window-auto-restored"
                    : "vrchat-window");
        }
        catch (ScanException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or ExternalException
                or ArgumentException)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "VRChat画面を取得できませんでした。VRChatが実行中で、PC画面に表示できる状態か確認してください。",
                exception);
        }
        finally
        {
            if (restoreMinimizedState && handle != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwShowMinNoActive);
            }
        }
    }

    private static IntPtr FindCaptureTargetHandle()
    {
        List<IntPtr> candidates = [];
        foreach (Process process in Process.GetProcessesByName("VRChat"))
        {
            using (process)
            {
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle))
                {
                    continue;
                }

                candidates.Add(handle);
            }
        }

        if (candidates.Count == 0)
        {
            throw new ScanException(
                ScanFailureCode.CaptureTargetNotFound,
                ScanStage.Capture,
                "VRChatの表示ウィンドウが見つかりません。Windows版VRChatを起動してください。");
        }

        return candidates
            .OrderBy(NativeMethods.IsIconic)
            .First();
    }

    private static async Task RestoreForCaptureAsync(
        IntPtr handle,
        CancellationToken cancellationToken)
    {
        _ = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwRestore);

        Stopwatch timer = Stopwatch.StartNew();
        while (NativeMethods.IsIconic(handle) && timer.Elapsed < RestoreTimeout)
        {
            await Task.Delay(RestorePollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (NativeMethods.IsIconic(handle))
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "最小化中のVRChatウィンドウを自動復元できませんでした。");
        }

        _ = NativeMethods.SetForegroundWindow(handle);
        await Task.Delay(RenderSettleDelay, cancellationToken).ConfigureAwait(false);
    }

    private static (int Width, int Height, int X, int Y) GetCaptureBounds(IntPtr handle)
    {
        NativeMethods.Rect bounds;
        int hresult = NativeMethods.DwmGetWindowAttribute(
            handle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out bounds,
            (uint)Marshal.SizeOf<NativeMethods.Rect>());
        if (hresult != 0)
        {
            bounds = GetClientBoundsFallback(handle);
        }

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "VRChatウィンドウの表示範囲を取得できませんでした。");
        }

        return (width, height, bounds.Left, bounds.Top);
    }

    private static NativeMethods.Rect GetClientBoundsFallback(IntPtr handle)
    {
        if (!NativeMethods.GetClientRect(handle, out NativeMethods.Rect client))
        {
            throw new Win32Exception();
        }

        NativeMethods.Point origin = new();
        if (!NativeMethods.ClientToScreen(handle, ref origin))
        {
            throw new Win32Exception();
        }

        return new NativeMethods.Rect
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Right - client.Left,
            Bottom = origin.Y + client.Bottom - client.Top,
        };
    }
}
