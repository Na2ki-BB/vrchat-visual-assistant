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
    public Task<CapturedFrame> CaptureAsync(
        ScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            (IntPtr handle, int width, int height, int x, int y) = FindCaptureTarget();
            _ = handle;

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
            return Task.FromResult(new CapturedFrame(
                encoded.ToArray(),
                width,
                height,
                "image/png",
                "vrchat-window"));
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
                "VRChat画面を取得できませんでした。ミラーを表示し、最小化されていないことを確認してください。",
                exception);
        }
    }

    private static (IntPtr Handle, int Width, int Height, int X, int Y) FindCaptureTarget()
    {
        List<(IntPtr Handle, int Width, int Height, int X, int Y)> candidates = [];
        foreach (Process process in Process.GetProcessesByName("VRChat"))
        {
            using (process)
            {
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle))
                {
                    continue;
                }

                if (NativeMethods.IsIconic(handle))
                {
                    throw new ScanException(
                        ScanFailureCode.CaptureUnavailable,
                        ScanStage.Capture,
                        "VRChatウィンドウが最小化されています。復元してからSCANしてください。");
                }

                NativeMethods.Rect bounds;
                int hresult = NativeMethods.DwmGetWindowAttribute(
                    handle,
                    NativeMethods.DwmwaExtendedFrameBounds,
                    out bounds,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>());
                if (hresult != 0)
                {
                    bounds = GetClientBoundsFallback(handle);
                }

                int width = bounds.Right - bounds.Left;
                int height = bounds.Bottom - bounds.Top;
                if (width > 0 && height > 0)
                {
                    candidates.Add((handle, width, height, bounds.Left, bounds.Top));
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new ScanException(
                ScanFailureCode.CaptureTargetNotFound,
                ScanStage.Capture,
                "VRChatの表示ウィンドウが見つかりません。Windows版VRChatを起動してください。");
        }

        return candidates.MaxBy(candidate => (long)candidate.Width * candidate.Height);
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
