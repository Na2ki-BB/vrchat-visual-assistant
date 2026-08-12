using System.Windows.Threading;

namespace VrcVa.Windows.OpenVr;

internal sealed class SteamVrResultPanel : IDisposable
{
    private readonly ResultPanelTexture _texture = new();
    private readonly DispatcherTimer _eventTimer;
    private OpenVrInterop? _interop;
    private bool _visible;
    private bool _interactive;
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
            SetInteractive(false);
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
            try
            {
                SetInteractive(false);
            }
            finally
            {
                _interop?.Hide();
            }
        }
        finally
        {
            _visible = false;
            _interactive = false;
            _eventTimer.Stop();
            Hidden?.Invoke(this, EventArgs.Empty);
        }
    }

    public SteamVrPanelInteractionChange ToggleInteraction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_visible || _interop is null)
        {
            return SteamVrPanelInteractionChange.NoVisiblePanel;
        }

        try
        {
            SetInteractive(!_interactive);
            return _interactive
                ? SteamVrPanelInteractionChange.Enabled
                : SteamVrPanelInteractionChange.Disabled;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    internal bool TryGetCaptureOverlay(out IOpenVrOverlayCaptureGate? overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureConnected())
        {
            overlay = null;
            return false;
        }

        overlay = _interop;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventTimer.Stop();
        _visible = false;
        _interactive = false;
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

    private void SetInteractive(bool enabled)
    {
        _interop?.SetInteractive(enabled);
        _interactive = enabled;
    }

    private void Disconnect()
    {
        _eventTimer.Stop();
        _visible = false;
        _interactive = false;
        _interop?.Dispose();
        _interop = null;
    }
}

internal enum SteamVrPanelInteractionChange
{
    NoVisiblePanel,
    Enabled,
    Disabled,
}
