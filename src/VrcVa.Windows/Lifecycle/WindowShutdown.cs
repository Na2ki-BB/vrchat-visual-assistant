using System.Windows.Threading;

namespace VrcVa.Windows.Lifecycle;

internal static class WindowShutdown
{
    public static async Task DrainAndPostCloseAsync(
        Dispatcher dispatcher,
        Task operations,
        Task startup,
        Action close)
    {
        await Task.WhenAll(operations, startup);
        // Always post, even when both drains completed inline. Close cannot reenter Closing.
        await dispatcher.InvokeAsync(close, DispatcherPriority.Normal);
    }
}
