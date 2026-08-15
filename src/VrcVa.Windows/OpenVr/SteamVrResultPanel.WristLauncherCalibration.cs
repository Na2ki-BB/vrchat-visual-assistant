namespace VrcVa.Windows.OpenVr;

internal sealed partial class SteamVrResultPanel
{
    private WristLauncherPlacement _launcherPlacement = WristLauncherPlacement.Default;
    private WristLauncherPlacement? _launcherCalibrationOriginalPlacement;
    private bool _launcherCalibrationActive;

    public event EventHandler? WristLauncherPlacementCalibrationStarted;

    public event EventHandler<WristLauncherPlacementCalibrationEventArgs>?
        WristLauncherPlacementCalibrationFinished;

    private bool TryStartWristLauncherCalibration()
    {
        if (_disposed
            || _launcherCalibrationActive
            || _calibrationActive
            || _input is null
            || _launcherInterop is null
            || !_launcherPoseAvailable
            || !EnsureConnected()
            || _imageUpload.InFlight)
        {
            return false;
        }

        if (!_launcherState.BeginCalibration())
        {
            return false;
        }

        _launcherCalibrationOriginalPlacement = _launcherPlacement;
        _launcherCalibrationActive = true;

        try
        {
            SetPointerEnabled(false);
            _interop!.Hide();
            _visible = false;
            _ = _interop.SetPlacement(ResultPanelPlacement.HeadsetFallback);
            WristLauncherPlacementCalibrationStarted?.Invoke(this, EventArgs.Empty);
            _launcherHover = WristLauncherAction.None;
            BeginWristLauncherCalibrationUpload();
            _showAfterImageLoad = true;
            _enableInteractionAfterImageLoad = true;
            ShowLauncherView();
            _eventTimer.Start();
            return true;
        }
        catch
        {
            AbandonWristLauncherCalibration();
            Disconnect();
            throw;
        }
    }

    private void HandleWristLauncherCalibrationClick(float x, float y)
    {
        WristLauncherCalibrationAction action =
            ResultPanelTexture.HitTestWristLauncherCalibration(x, y);
        switch (action)
        {
            case WristLauncherCalibrationAction.None:
                return;
            case WristLauncherCalibrationAction.Save:
                FinishWristLauncherCalibration(save: true);
                return;
            case WristLauncherCalibrationAction.Cancel:
                FinishWristLauncherCalibration(save: false);
                return;
            default:
                WristLauncherPlacement updated = WristLauncherCalibration.Apply(
                    _launcherPlacement,
                    action);
                if (updated == _launcherPlacement)
                {
                    return;
                }

                _launcherPlacement = updated;
                _ = _launcherInterop!.SetPlacement(updated);
                ShowLauncherView();
                return;
        }
    }

    private void FinishWristLauncherCalibration(
        bool save,
        bool returnToLauncher = true)
    {
        if (!_launcherCalibrationActive)
        {
            return;
        }

        WristLauncherPlacement original =
            _launcherCalibrationOriginalPlacement ?? _launcherPlacement;
        WristLauncherPlacement completed = save ? _launcherPlacement : original;
        _launcherCalibrationActive = false;
        _launcherCalibrationOriginalPlacement = null;
        _launcherPlacement = completed;
        _ = _launcherState.EndCalibration();
        _launcherHover = WristLauncherAction.None;

        SetPointerEnabled(false);
        _interop?.Hide();
        _visible = false;
        _showAfterImageLoad = false;
        _enableInteractionAfterImageLoad = false;
        Exception? nativeFailure = null;
        try
        {
            _ = _interop?.SetPlacement(_placement);
            _ = _launcherInterop?.SetPlacement(completed);
        }
        catch (Exception exception)
        {
            nativeFailure = exception;
        }
        finally
        {
            // Re-enable desktop controls and persist/revert the chosen value
            // even when the OpenVR runtime disappears during the final click.
            WristLauncherPlacementCalibrationFinished?.Invoke(
                this,
                new WristLauncherPlacementCalibrationEventArgs(completed, save));
        }

        if (nativeFailure is not null)
        {
            TryLogLauncherError(
                "openvr.launcher.calibration_finish_failed",
                nativeFailure);
            Disconnect();
            DisconnectLauncher();
            return;
        }

        if (returnToLauncher && _launcherState.View != WristLauncherView.Scanning)
        {
            ShowLauncherView();
        }
    }

    private void AbandonWristLauncherCalibration()
    {
        if (!_launcherCalibrationActive)
        {
            return;
        }

        WristLauncherPlacement original =
            _launcherCalibrationOriginalPlacement ?? _launcherPlacement;
        _launcherPlacement = original;
        _launcherCalibrationActive = false;
        _launcherCalibrationOriginalPlacement = null;
        _ = _launcherState.EndCalibration();
        _launcherHover = WristLauncherAction.None;
        WristLauncherPlacementCalibrationFinished?.Invoke(
            this,
            new WristLauncherPlacementCalibrationEventArgs(original, SaveRequested: false));
    }

    private void BeginWristLauncherCalibrationUpload()
    {
        OpenVrInterop interop = _interop
            ?? throw new InvalidOperationException("The SteamVR overlay is not connected.");
        while (interop.TryPollEvent(out _))
        {
            // Associate the next image completion event with this upload.
        }

        _imageUpload.Begin(ResultPanelImageUploadKind.Calibration);
        byte[] pixels = _texture.RenderWristLauncherCalibrationRgba();
        interop.SetImage(
            pixels,
            ResultPanelTexture.PixelWidth,
            ResultPanelTexture.PixelHeight);
    }
}

internal sealed record WristLauncherPlacementCalibrationEventArgs(
    WristLauncherPlacement Placement,
    bool SaveRequested);
