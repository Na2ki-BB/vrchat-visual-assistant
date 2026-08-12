using System.Windows.Threading;

namespace VrcVa.Windows.OpenVr;

internal sealed class SteamVrResultPanel : IDisposable
{
    private readonly ResultPanelTexture _texture = new();
    private readonly DispatcherTimer _eventTimer;
    private OpenVrInterop? _interop;
    private bool _visible;
    private bool _disposed;

    public event EventHandler? Hidden;

    public SteamVrResultPanel(Dispatcher dispatcher)
    {
        _eventTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            PollEvents,
            dispatcher);
        _eventTimer.Stop();
    }

    public bool TryShow(string title, string body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            _texture.SetContent(title, body);
            UploadTexture();
            _interop!.Show();
            _visible = true;
            _eventTimer.Start();
            return true;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public void Hide()
    {
        if (_disposed || !_visible)
        {
            return;
        }

        try
        {
            _interop?.Hide();
        }
        finally
        {
            _visible = false;
            _eventTimer.Stop();
            Hidden?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventTimer.Stop();
        _interop?.Dispose();
        _interop = null;
    }

    private bool EnsureConnected()
    {
        if (_interop is not null)
        {
            return true;
        }

        return OpenVrInterop.TryCreate(out _interop);
    }

    private void PollEvents(object? sender, EventArgs eventArgs)
    {
        if (!_visible || _interop is null)
        {
            return;
        }

        try
        {
            while (_interop.TryPollEvent(out OpenVrEvent overlayEvent))
            {
                switch (overlayEvent.EventType)
                {
                    case OpenVrEvent.OverlayClosed:
                        Hide();
                        return;
                    case OpenVrEvent.MouseButtonDown
                        when overlayEvent.MouseButton == OpenVrEvent.LeftMouseButton
                            && _texture.IsCloseButton(
                                overlayEvent.MouseX,
                                overlayEvent.MouseY):
                        Hide();
                        return;
                    case OpenVrEvent.ScrollDiscrete:
                    case OpenVrEvent.ScrollSmooth:
                        if (_texture.Scroll(overlayEvent.ScrollY))
                        {
                            UploadTexture();
                        }

                        break;
                }
            }
        }
        catch
        {
            Disconnect();
        }
    }

    private void UploadTexture()
    {
        byte[] pixels = _texture.RenderRgba();
        _interop!.SetImage(
            pixels,
            ResultPanelTexture.PixelWidth,
            ResultPanelTexture.PixelHeight);
    }

    private void Disconnect()
    {
        _eventTimer.Stop();
        _visible = false;
        _interop?.Dispose();
        _interop = null;
    }
}
