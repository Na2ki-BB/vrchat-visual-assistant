using System.Windows.Threading;

namespace VrcVa.Windows.OpenVr;

internal sealed class SteamVrResultPanel : IDisposable
{
    private readonly ResultPanelTexture _texture = new();
    private readonly DispatcherTimer _eventTimer;
    private OpenVrInterop? _interop;
    private bool _visible;
    private bool _interactive;
    private bool _atlasLoaded;
    private bool _imageUploadInFlight;
    private bool _showAfterImageLoad;
    private bool _enableInteractionAfterImageLoad;
    private int _pendingCell;
    private string? _queuedResultTitle;
    private string? _queuedResultBody;
    private ResultPanelPlacement _placement;
    private ResultPanelPlacement? _calibrationOriginalPlacement;
    private bool _calibrationActive;
    private bool _disposed;

    public event EventHandler? Hidden;
    public event EventHandler? DisplayFailed;
    public event EventHandler? PlacementFallback;
    public event EventHandler<ResultPanelPlacementCalibrationEventArgs>? PlacementCalibrationFinished;

    public bool LastPlacementUsedFallback =>
        _interop?.LastPlacementUsedFallback == true;

    public SteamVrResultPanel(
        Dispatcher dispatcher,
        ResultPanelPlacement? placement = null)
    {
        _placement = placement ?? ResultPanelPlacement.HeadsetFallback;
        _placement.Validate();
        _eventTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            PollEvents,
            dispatcher);
        _eventTimer.Stop();
    }

    public bool UpdatePlacement(ResultPanelPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        placement.Validate();
        _placement = placement;
        if (_interop is null)
        {
            return false;
        }

        try
        {
            bool usedFallback = _interop.SetPlacement(placement);
            if (usedFallback)
            {
                PlacementFallback?.Invoke(this, EventArgs.Empty);
            }

            return usedFallback;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public bool TryShowPlacementCalibration(ResultPanelPlacement placement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        placement.Validate();
        if (_calibrationActive || !EnsureConnected() || _imageUploadInFlight)
        {
            return false;
        }

        try
        {
            SetInteractive(false);
            _interop!.Hide();
            _visible = false;
            _placement = placement;
            if (_interop.SetPlacement(placement))
            {
                PlacementFallback?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _calibrationOriginalPlacement = placement;
            _calibrationActive = true;
            _texture.SetCalibration();
            BeginAtlasUpload();
            _pendingCell = ResultPanelTexture.CalibrationCell;
            _showAfterImageLoad = true;
            _enableInteractionAfterImageLoad = true;
            _eventTimer.Start();
            return true;
        }
        catch
        {
            AbandonPlacementCalibration();
            Disconnect();
            throw;
        }
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
            _interop!.Hide();
            _visible = false;
            if (_imageUploadInFlight)
            {
                _queuedResultTitle = title;
                _queuedResultBody = body;
                _showAfterImageLoad = false;
                _enableInteractionAfterImageLoad = false;
                return true;
            }

            _texture.SetContent(title, body);
            BeginAtlasUpload();
            _pendingCell = _texture.CurrentResultCell;
            _showAfterImageLoad = true;
            _enableInteractionAfterImageLoad = true;
            _eventTimer.Start();
            return true;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public bool PreloadStatusAtlas()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            SetInteractive(false);
            _interop!.Hide();
            _visible = false;
            _texture.SetContent(string.Empty, string.Empty);
            BeginAtlasUpload();
            _pendingCell = ResultPanelTexture.WaitingCell;
            _showAfterImageLoad = false;
            _enableInteractionAfterImageLoad = false;
            _eventTimer.Start();
            return true;
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public bool TryShowStatus(int atlasCell)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            SetInteractive(false);
            if (!_atlasLoaded)
            {
                if (!_imageUploadInFlight)
                {
                    _texture.SetContent(string.Empty, string.Empty);
                    BeginAtlasUpload();
                }

                _pendingCell = atlasCell;
                _showAfterImageLoad = true;
                _enableInteractionAfterImageLoad = false;
                _eventTimer.Start();
                return true;
            }

            SelectAtlasCell(atlasCell);
            ShowOverlay();
            _visible = true;
            _eventTimer.Start();
            return _interop!.IsVisible();
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public void ShowStatus(int atlasCell)
    {
        if (_disposed || _interop is null)
        {
            return;
        }

        SetInteractive(false);
        if (_imageUploadInFlight)
        {
            _pendingCell = atlasCell;
            _showAfterImageLoad = true;
            _enableInteractionAfterImageLoad = false;
            return;
        }

        if (!_visible)
        {
            return;
        }

        SelectAtlasCell(atlasCell);
    }

    public void Hide()
    {
        if (_calibrationActive)
        {
            FinishPlacementCalibration(save: false);
            return;
        }

        if (_disposed
            || (!_visible && !_showAfterImageLoad && !_imageUploadInFlight))
        {
            return;
        }

        bool notifyHidden = _visible;
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
        catch
        {
            bool resultFailed = _enableInteractionAfterImageLoad
                || _queuedResultTitle is not null;
            Disconnect();
            if (resultFailed)
            {
                DisplayFailed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _visible = false;
            _interactive = false;
            _showAfterImageLoad = false;
            _enableInteractionAfterImageLoad = false;
            _queuedResultTitle = null;
            _queuedResultBody = null;
            if (!_imageUploadInFlight)
            {
                _eventTimer.Stop();
            }
            if (notifyHidden)
            {
                Hidden?.Invoke(this, EventArgs.Empty);
            }
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

        return OpenVrInterop.TryCreate(_placement, out _interop);
    }

    private void PollEvents(object? sender, EventArgs eventArgs)
    {
        if ((!_visible && !_imageUploadInFlight) || _interop is null)
        {
            return;
        }

        try
        {
            bool textureChanged = false;
            while (_interop.TryPollEvent(out OpenVrEvent overlayEvent))
            {
                (float localX, float localY) = ResultPanelTexture.MapOpenVrPointer(
                    overlayEvent.MouseX,
                    overlayEvent.MouseY,
                    _texture.CurrentResultCell);
                switch (overlayEvent.EventType)
                {
                    case OpenVrEvent.OverlayClosed:
                        Hide();
                        return;
                    case OpenVrEvent.ImageLoaded when _imageUploadInFlight:
                        _imageUploadInFlight = false;
                        _atlasLoaded = true;
                        if (_queuedResultTitle is not null && _queuedResultBody is not null)
                        {
                            string title = _queuedResultTitle;
                            string body = _queuedResultBody;
                            _queuedResultTitle = null;
                            _queuedResultBody = null;
                            _texture.SetContent(title, body);
                            BeginAtlasUpload(drainEvents: false);
                            _pendingCell = _texture.CurrentResultCell;
                            _showAfterImageLoad = true;
                            _enableInteractionAfterImageLoad = true;
                        }
                        else if (_showAfterImageLoad)
                        {
                            SelectAtlasCell(_pendingCell);
                            ShowOverlay();
                            _visible = _interop.IsVisible();
                            _showAfterImageLoad = false;
                            if (_visible && _enableInteractionAfterImageLoad)
                            {
                                SetInteractive(true);
                            }

                            if (!_visible && _enableInteractionAfterImageLoad)
                            {
                                DisplayFailed?.Invoke(this, EventArgs.Empty);
                            }
                        }

                        break;
                    case OpenVrEvent.ImageFailed:
                        bool resultFailed = _enableInteractionAfterImageLoad
                            || _queuedResultTitle is not null;
                        Disconnect();
                        if (resultFailed)
                        {
                            DisplayFailed?.Invoke(this, EventArgs.Empty);
                        }
                        return;
                    case OpenVrEvent.MouseButtonDown
                        when _calibrationActive
                            && overlayEvent.MouseButton == OpenVrEvent.LeftMouseButton:
                        HandlePlacementCalibrationClick(localX, localY);
                        if (!_calibrationActive)
                        {
                            return;
                        }
                        break;
                    case OpenVrEvent.MouseButtonDown
                        when overlayEvent.MouseButton == OpenVrEvent.LeftMouseButton
                            && _texture.IsCloseButton(
                                localX,
                                localY):
                        Hide();
                        return;
                    case OpenVrEvent.MouseButtonDown
                        when overlayEvent.MouseButton == OpenVrEvent.LeftMouseButton:
                        textureChanged |= _texture.BeginScrollbarInteraction(
                            localX,
                            localY);
                        break;
                    case OpenVrEvent.ScrollDiscrete:
                    case OpenVrEvent.ScrollSmooth:
                        textureChanged |= _texture.Scroll(overlayEvent.ScrollY);
                        break;
                }
            }

            if (textureChanged)
            {
                SelectAtlasCell(_texture.CurrentResultCell);
            }

            if (!_visible && !_imageUploadInFlight)
            {
                _eventTimer.Stop();
            }
        }
        catch
        {
            Disconnect();
        }
    }

    private void BeginAtlasUpload(bool drainEvents = true)
    {
        OpenVrInterop interop = _interop
            ?? throw new InvalidOperationException("The SteamVR overlay is not connected.");
        while (drainEvents && interop.TryPollEvent(out _))
        {
            // Associate the next image completion event with this upload.
        }

        _atlasLoaded = false;
        _imageUploadInFlight = true;
        byte[] pixels = _texture.RenderRgba();
        interop.SetImage(
            pixels,
            ResultPanelTexture.AtlasPixelWidth,
            ResultPanelTexture.AtlasPixelHeight);
    }

    private void SelectAtlasCell(int cell) => _interop!.SelectAtlasCell(
        cell,
        ResultPanelTexture.AtlasColumns,
        ResultPanelTexture.AtlasRows);

    private void SetInteractive(bool enabled)
    {
        _interop?.SetInteractive(enabled);
        _interactive = enabled;
    }

    private void ShowOverlay()
    {
        OpenVrInterop interop = _interop
            ?? throw new InvalidOperationException("The SteamVR overlay is not connected.");
        interop.Show();
        if (interop.LastPlacementUsedFallback)
        {
            PlacementFallback?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandlePlacementCalibrationClick(float x, float y)
    {
        ResultPanelCalibrationAction action = ResultPanelTexture.HitTestCalibration(x, y);
        switch (action)
        {
            case ResultPanelCalibrationAction.None:
                return;
            case ResultPanelCalibrationAction.Save:
                FinishPlacementCalibration(save: true);
                return;
            case ResultPanelCalibrationAction.Cancel:
                FinishPlacementCalibration(save: false);
                return;
            default:
                ResultPanelPlacement updated = ResultPanelCalibration.Apply(_placement, action);
                if (updated == _placement)
                {
                    return;
                }

                _placement = updated;
                bool usedFallback = _interop!.SetPlacement(updated);
                if (usedFallback)
                {
                    PlacementFallback?.Invoke(this, EventArgs.Empty);
                }
                return;
        }
    }

    private void FinishPlacementCalibration(bool save)
    {
        ResultPanelPlacement original = _calibrationOriginalPlacement ?? _placement;
        ResultPanelPlacement completed = save ? _placement : original;
        _calibrationActive = false;
        _calibrationOriginalPlacement = null;
        if (!save)
        {
            _placement = original;
            _ = _interop?.SetPlacement(original);
        }

        PlacementCalibrationFinished?.Invoke(
            this,
            new ResultPanelPlacementCalibrationEventArgs(completed, save));
        Hide();
    }

    private void AbandonPlacementCalibration()
    {
        if (!_calibrationActive)
        {
            return;
        }

        ResultPanelPlacement original = _calibrationOriginalPlacement ?? _placement;
        _placement = original;
        _calibrationActive = false;
        _calibrationOriginalPlacement = null;
        PlacementCalibrationFinished?.Invoke(
            this,
            new ResultPanelPlacementCalibrationEventArgs(original, SaveRequested: false));
    }

    private void Disconnect()
    {
        AbandonPlacementCalibration();
        _eventTimer.Stop();
        _visible = false;
        _interactive = false;
        _atlasLoaded = false;
        _imageUploadInFlight = false;
        _showAfterImageLoad = false;
        _enableInteractionAfterImageLoad = false;
        _queuedResultTitle = null;
        _queuedResultBody = null;
        _interop?.Dispose();
        _interop = null;
    }
}

internal sealed record ResultPanelPlacementCalibrationEventArgs(
    ResultPanelPlacement Placement,
    bool SaveRequested);
