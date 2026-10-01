using System.Runtime.InteropServices;
using System.Windows.Threading;
using VrcVa.Core;

namespace VrcVa.Windows.Video;

/// <summary>Runs selection revalidation and a single write together on the WPF STA dispatcher.</summary>
public sealed class WpfVideoClipboardDispatcher : IVideoClipboardDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfVideoClipboardDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public async Task<VideoClipboardCopyStatus> InvokeAsync(Func<VideoClipboardCopyStatus> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { return VideoClipboardCopyStatus.Unavailable; }
        try
        {
            return await _dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                {
                    return VideoClipboardCopyStatus.Unavailable;
                }
                // Prevent nested dispatcher work from invalidating the selection mid-write.
                using (_dispatcher.DisableProcessing()) { return write(); }
            }, DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
        }
        catch (ExternalException) { return VideoClipboardCopyStatus.Busy; }
        catch (Exception exception) when (exception is InvalidOperationException or ThreadStateException)
        {
            return VideoClipboardCopyStatus.Unavailable;
        }
    }
}

/// <summary>Persistent Unicode clipboard data, with zero automatic retries and no history/settings changes.</summary>
public sealed class WindowsVideoClipboardTextWriter : IVideoClipboardTextWriter
{
    public void SetText(string text)
    {
        System.Windows.Forms.DataObject data = new();
        data.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
        // The ordinary WPF SetText overload retries internally. This overload performs one attempt.
        System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 0, retryDelay: 0);
    }
}
