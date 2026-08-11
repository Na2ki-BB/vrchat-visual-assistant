using System.Diagnostics;
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

            return await WindowsGraphicsCapture
                .CaptureWindowAsync(handle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ScanException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ExternalException
                or ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            throw new ScanException(
                ScanFailureCode.CaptureUnavailable,
                ScanStage.Capture,
                "VRChatウィンドウを直接取得できませんでした。VRChatが応答しているか確認してください。",
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

        await Task.Delay(RenderSettleDelay, cancellationToken).ConfigureAwait(false);
    }

}
