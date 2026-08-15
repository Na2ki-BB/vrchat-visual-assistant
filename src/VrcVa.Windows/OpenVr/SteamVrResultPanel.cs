using System.Diagnostics;
using System.Windows.Threading;
using VrcVa.Core;

namespace VrcVa.Windows.OpenVr;

internal sealed partial class SteamVrResultPanel : IOpenVrOverlayCaptureGate, IDisposable
{
    private static readonly TimeSpan LauncherRetryDelay = TimeSpan.FromSeconds(2);
    private readonly ResultPanelTexture _texture = new();
    private readonly WristLauncherTexture _launcherTexture = new();
    private readonly WristLauncherStateMachine _launcherState = new();
    private readonly PointerActivationGate _activationGate = new();
    private readonly Stopwatch _interactionClock = Stopwatch.StartNew();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _eventTimer;
    private readonly IPrivacySafeLogger? _logger;
    private OpenVrInterop? _interop;
    private OpenVrInterop? _launcherInterop;
    private OpenVrInterop? _cursorInterop;
    private OpenVrInputInterop? _input;
    private bool _visible;
    private bool _interactive;
    private bool _launcherImageLoading;
    private bool _launcherAtlasLoaded;
    private bool _launcherPoseAvailable;
    private bool _cursorImageLoading;
    private bool _cursorReady;
    private bool _cursorShown;
    private OpenVrIntersection? _pendingCursorIntersection;
    private WristLauncherAction _launcherHover;
    private bool _captureSuppressed;
    private readonly ResultPanelImageUploadTracker _imageUpload = new();
    private bool _showAfterImageLoad;
    private bool _enableInteractionAfterImageLoad;
    private int _pendingCell;
    private string? _queuedResultTitle;
    private string? _queuedResultBody;
    private ResultPanelPlacement _placement;
    private ResultPanelPlacement? _calibrationOriginalPlacement;
    private bool _calibrationActive;
    private PointerDiagnosticKey? _lastPointerDiagnostic;
    private TimeSpan _nextPointerDiagnosticAt;
    private int _pointerDiagnosticCount;
    private bool _pointerDiagnosticsDisabled;
    private bool _launcherRecoveryEnabled;
    private TimeSpan _nextLauncherStartAttemptAt;
    private int _launcherRetryFailureLogCount;
    private bool _scanSessionActive;
    private bool _resultDesired;
    private bool _disposed;

    public event EventHandler? Hidden;
    public event EventHandler? ScanRequested;
    public event EventHandler? DisplayFailed;
    public event EventHandler? PlacementFallback;
    public event EventHandler<ResultPanelPlacementCalibrationEventArgs>? PlacementCalibrationFinished;

    public bool LastPlacementUsedFallback =>
        _interop?.LastPlacementUsedFallback == true;

    internal static WristLauncherPlacement LauncherPlacement =>
        WristLauncherPlacement.Default;

    public SteamVrResultPanel(
        Dispatcher dispatcher,
        ResultPanelPlacement? placement = null,
        IPrivacySafeLogger? logger = null,
        WristLauncherPlacement? launcherPlacement = null)
    {
        _dispatcher = dispatcher;
        _placement = placement ?? ResultPanelPlacement.HeadsetFallback;
        _placement.Validate();
        _launcherPlacement = launcherPlacement ?? WristLauncherPlacement.Default;
        _launcherPlacement.Validate();
        _logger = logger;
        _eventTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Background,
            PollEvents,
            dispatcher);
        _eventTimer.Stop();
    }

    public bool TryStartWristLauncher()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _launcherRecoveryEnabled = true;
        try
        {
            bool started = TryStartWristLauncherCore();
            if (started)
            {
                _nextLauncherStartAttemptAt = TimeSpan.Zero;
                _launcherRetryFailureLogCount = 0;
            }
            else
            {
                ScheduleLauncherRetry();
            }

            return started;
        }
        catch
        {
            ScheduleLauncherRetry();
            throw;
        }
    }

    private bool TryStartWristLauncherCore()
    {
        if (IsLauncherConnectionComplete(
            _input is not null,
            _launcherInterop is not null,
            _cursorInterop is not null))
        {
            return true;
        }

        if (_input is not null || _launcherInterop is not null || _cursorInterop is not null)
        {
            // A launcher without its pointer is not usable. Tear down partial
            // ownership before rebuilding all three OpenVR resources together.
            DisconnectLauncher();
        }

        try
        {
            if (!OpenVrInputInterop.TryCreate(out OpenVrInputInterop? input))
            {
                return false;
            }

            _input = input;

            if (!OpenVrInterop.TryCreate(
                _launcherPlacement,
                new OverlaySurfaceSpec(
                    WristLauncherTexture.PixelWidth,
                    WristLauncherTexture.PixelHeight),
                "com.na2kibb.vrcva.launcher",
                "VRChat Visual Assistant Wrist Launcher",
                out OpenVrInterop? launcher))
            {
                DisconnectLauncher();
                return false;
            }

            _launcherInterop = launcher!;

            if (!OpenVrInterop.TryCreate(
                ResultPanelPlacement.HeadsetFallback,
                new OverlaySurfaceSpec(PointerCursorTexture.PixelSize, PointerCursorTexture.PixelSize),
                "com.na2kibb.vrcva.pointer",
                "VRChat Visual Assistant Pointer",
                out OpenVrInterop? cursor))
            {
                DisconnectLauncher();
                return false;
            }

            _cursorInterop = cursor!;
            _cursorInterop.SetSortOrder(1);
            _cursorInterop.SetWidth(0.025f);
            _cursorImageLoading = true;
            _cursorReady = false;
            _cursorInterop.SetImage(
                PointerCursorTexture.RenderRgba(),
                PointerCursorTexture.PixelSize,
                PointerCursorTexture.PixelSize);
            _launcherState.Start();
            _launcherImageLoading = true;
            _launcherAtlasLoaded = false;
            _launcherInterop.SetImage(
                _launcherTexture.RenderAtlasRgba(),
                WristLauncherTexture.AtlasPixelWidth,
                WristLauncherTexture.AtlasPixelHeight);
            _eventTimer.Start();
            return true;
        }
        catch
        {
            DisconnectLauncher();
            throw;
        }
    }

    public void BeginScan()
    {
        if (_disposed)
        {
            return;
        }

        _scanSessionActive = true;
        _resultDesired = false;
        _ = TryRecoverWristLauncher();
        if (_launcherCalibrationActive)
        {
            FinishWristLauncherCalibration(save: false, returnToLauncher: false);
        }

        _ = _launcherState.BeginScan();

        _launcherInterop?.Hide();
        _cursorInterop?.Hide();
        _cursorShown = false;
        _pendingCursorIntersection = null;
        _activationGate.Reset();
    }

    public void ReturnToLauncher()
    {
        if (_disposed)
        {
            return;
        }

        _scanSessionActive = false;
        _resultDesired = false;
        if (_launcherInterop is null)
        {
            _ = TryRecoverWristLauncher();
        }

        if (_launcherInterop is null || _captureSuppressed)
        {
            return;
        }

        _launcherState.ReturnToChip();
        _launcherHover = WristLauncherAction.None;
        ShowLauncherView();
        _eventTimer.Start();
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
        _ = TryRecoverWristLauncher();
        if (_calibrationActive
            || _launcherCalibrationActive
            || _input is null
            || !EnsureConnected()
            || _imageUpload.InFlight)
        {
            return false;
        }

        try
        {
            SetPointerEnabled(false);
            _interop!.Hide();
            _visible = false;
            _placement = placement;
            if (_interop.SetPlacement(placement))
            {
                PlacementFallback?.Invoke(this, EventArgs.Empty);
                return false;
            }

            _launcherInterop?.Hide();
            _cursorInterop?.Hide();
            _cursorShown = false;
            _calibrationOriginalPlacement = placement;
            _calibrationActive = true;
            BeginCalibrationUpload();
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
        _ = TryRecoverWristLauncher();
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            // A long launcher-only session can exhaust the bounded diagnostic
            // budget before a result is shown. Start a fresh bounded window so
            // the result surface itself is always observable during device
            // acceptance and future regressions.
            _pointerDiagnosticCount = 0;
            _lastPointerDiagnostic = null;
            _nextPointerDiagnosticAt = _interactionClock.Elapsed;
            _scanSessionActive = true;
            _resultDesired = true;
            _launcherState.ShowResult();
            _launcherInterop?.Hide();
            SetPointerEnabled(false);
            _interop!.Hide();
            _visible = false;
            if (_imageUpload.InFlight)
            {
                _queuedResultTitle = title;
                _queuedResultBody = body;
                _showAfterImageLoad = false;
                _enableInteractionAfterImageLoad = false;
                return true;
            }

            _texture.SetContent(title, body);
            BeginResultPageUpload();
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
            SetPointerEnabled(false);
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
        _ = TryRecoverWristLauncher();
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            SetPointerEnabled(false);
            if (!_imageUpload.AtlasLoaded)
            {
                if (!_imageUpload.InFlight)
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

        SetPointerEnabled(false);
        if (!_imageUpload.AtlasLoaded)
        {
            if (!_imageUpload.InFlight)
            {
                _texture.SetContent(string.Empty, string.Empty);
                BeginAtlasUpload();
            }

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
        if (_launcherCalibrationActive)
        {
            FinishWristLauncherCalibration(save: false);
            return;
        }

        if (_calibrationActive)
        {
            FinishPlacementCalibration(save: false);
            return;
        }

        if (_disposed
            || (!_visible && !_showAfterImageLoad && !_imageUpload.InFlight))
        {
            return;
        }

        bool notifyHidden = _visible;
        try
        {
            try
            {
                SetPointerEnabled(false);
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
            if (!_imageUpload.InFlight)
            {
                _eventTimer.Stop();
            }
            if (notifyHidden)
            {
                Hidden?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void HideAndConfirmInvisible(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        InvokeOnOverlayThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _captureSuppressed = true;
            _eventTimer.Stop();
            _activationGate.Reset();
            _interop?.HideAndConfirmInvisible(cancellationToken);
            _launcherInterop?.HideAndConfirmInvisible(cancellationToken);
            _cursorInterop?.HideAndConfirmInvisible(cancellationToken);
            _cursorShown = false;
        });
    }

    public void EndCaptureSuppression()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        InvokeOnOverlayThread(() =>
        {
            _captureSuppressed = false;
            if (_disposed)
            {
                return;
            }

            if (_launcherState.View is WristLauncherView.DimChip
                or WristLauncherView.ArmedChip
                or WristLauncherView.Menu
                or WristLauncherView.Calibrating)
            {
                ShowLauncherView();
            }

            if (_interop is not null || _launcherInterop is not null)
            {
                _eventTimer.Start();
            }
        });
    }

    public void WaitFrameSync(uint timeoutMilliseconds = 1000)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvokeOnOverlayThread(() =>
        {
            if (_interop is not null)
            {
                _interop.WaitFrameSync(timeoutMilliseconds);
                return;
            }

            _launcherInterop?.WaitFrameSync(timeoutMilliseconds);
        });
    }

    private void InvokeOnOverlayThread(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action, DispatcherPriority.Send);
    }

    internal bool TryGetCaptureOverlay(out IOpenVrOverlayCaptureGate? overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!EnsureConnected())
        {
            overlay = null;
            return false;
        }

        overlay = this;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcherRecoveryEnabled = false;
        _eventTimer.Stop();
        _visible = false;
        _interactive = false;
        _interop?.Dispose();
        _interop = null;
        DisconnectLauncher();
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
        if (_launcherRecoveryEnabled
            && !IsLauncherConnectionComplete(
                _input is not null,
                _launcherInterop is not null,
                _cursorInterop is not null))
        {
            _ = TryRecoverWristLauncher();
        }

        if (_interop is null && _launcherInterop is null)
        {
            return;
        }

        try
        {
            bool textureChanged = false;
            while (_interop is not null && _interop.TryPollEvent(out OpenVrEvent overlayEvent))
            {
                switch (overlayEvent.EventType)
                {
                    case OpenVrEvent.OverlayClosed:
                        HandleUserResultClose();
                        return;
                    case OpenVrEvent.ImageLoaded when _imageUpload.InFlight:
                        ResultPanelImageUploadKind completedUpload = _imageUpload.Complete();
                        if (_queuedResultTitle is not null && _queuedResultBody is not null)
                        {
                            string title = _queuedResultTitle;
                            string body = _queuedResultBody;
                            _queuedResultTitle = null;
                            _queuedResultBody = null;
                            _texture.SetContent(title, body);
                            BeginResultPageUpload(drainEvents: false);
                            _showAfterImageLoad = true;
                            _enableInteractionAfterImageLoad = true;
                        }
                        else if (!_enableInteractionAfterImageLoad
                            && RequiresAtlasReloadAfterImageLoaded(
                                completedUpload,
                                _calibrationActive || _launcherCalibrationActive,
                                _showAfterImageLoad))
                        {
                            // A SCAN status can replace a full-texture result or calibration
                            // while its image upload is still completing. Restore the status
                            // atlas before applying atlas bounds.
                            _texture.SetContent(string.Empty, string.Empty);
                            BeginAtlasUpload(drainEvents: false);
                        }
                        else if (_showAfterImageLoad)
                        {
                            if (_calibrationActive
                                || _launcherCalibrationActive
                                || completedUpload == ResultPanelImageUploadKind.ResultPage)
                            {
                                _interop.SelectFullTexture();
                            }
                            else
                            {
                                SelectAtlasCell(_pendingCell);
                            }
                            ShowOverlay();
                            _visible = _interop.IsVisible();
                            _showAfterImageLoad = false;
                            if (_visible && _enableInteractionAfterImageLoad)
                            {
                                SetPointerEnabled(true);
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
                }
            }

            PollLauncherEvents();
            textureChanged |= PollPointerInput();

            if (textureChanged)
            {
                SetPointerEnabled(false);
                UpdateCursor(null);
                _activationGate.Reset();
                BeginResultPageUpload();
                _showAfterImageLoad = true;
                _enableInteractionAfterImageLoad = true;
            }

            if (!_visible
                && !_imageUpload.InFlight
                && _launcherInterop is null
                && !_launcherRecoveryEnabled)
            {
                _eventTimer.Stop();
            }
        }
        catch
        {
            Disconnect();
            DisconnectLauncher();
        }
    }

    private void PollLauncherEvents()
    {
        while (_launcherInterop is not null
            && _launcherInterop.TryPollEvent(out OpenVrEvent launcherEvent))
        {
            if (launcherEvent.EventType == OpenVrEvent.ImageLoaded && _launcherImageLoading)
            {
                _launcherImageLoading = false;
                _launcherAtlasLoaded = true;
                ShowLauncherView();
            }
            else if (launcherEvent.EventType == OpenVrEvent.ImageFailed)
            {
                DisconnectLauncher();
                return;
            }
        }


        while (_cursorInterop is not null
            && _cursorInterop.TryPollEvent(out OpenVrEvent cursorEvent))
        {
            if (cursorEvent.EventType == OpenVrEvent.ImageLoaded && _cursorImageLoading)
            {
                _cursorImageLoading = false;
                _cursorReady = true;
                UpdateCursor(_pendingCursorIntersection);
            }
            else if (cursorEvent.EventType == OpenVrEvent.ImageFailed)
            {
                // Input, launcher, and pointer form one usable unit. Reconnect
                // the whole unit so a transient cursor upload failure cannot
                // leave an invisible pointer until the application restarts.
                DisconnectLauncher();
                return;
            }
        }
    }

    private bool PollPointerInput()
    {
        if (_input is null || _captureSuppressed)
        {
            return false;
        }

        try
        {
            OpenVrPointerInputSample sample = _input.Poll();
            bool posesAvailable = sample.HmdPoseValid && sample.LeftPoseValid;
            bool poseBecameAvailable = posesAvailable && !_launcherPoseAvailable;
            _launcherPoseAvailable = posesAvailable;
            WristFacingSample facing = new(false, false, -1);
            bool launcherViewChanged;
            if (posesAvailable)
            {
                launcherViewChanged = UpdateLauncherFacing(sample, out facing);
            }
            else
            {
                launcherViewChanged = _launcherState.HandlePoseLoss();
                _launcherInterop?.Hide();
                UpdateCursor(null);
            }

            if (posesAvailable && (launcherViewChanged || poseBecameAvailable))
            {
                ShowLauncherView();
            }

            int target = 0;
            int intersectionKind = 0;
            bool intersectionAttempted = false;
            bool resultIntersectionAttempted = false;
            OpenVrIntersectionAttempt resultIntersectionAttempt = default;
            OverlayLocalPoint local = default;
            OverlayLocalPoint raw = default;
            WristLauncherAction launcherAction = WristLauncherAction.None;
            ResultPanelAction resultAction = ResultPanelAction.None;
            ResultPanelCalibrationAction calibrationAction = ResultPanelCalibrationAction.None;
            bool scrollbar = false;
            OpenVrIntersection? pointerIntersection = null;
            if (sample.PoseActive && sample.PoseValid && sample.DeviceConnected)
            {
                intersectionAttempted = _launcherInterop is not null
                    || (_visible && _interactive && _interop is not null);
                OpenVrRay ray = OpenVrRay.FromPose(sample.Pose);
                OpenVrIntersection resultHit = default;
                bool resultHitFound = false;
                if (_visible && _interactive && _interop is not null)
                {
                    resultIntersectionAttempted = true;
                    resultHitFound = _interop.TryComputeIntersection(
                        ray,
                        out resultHit,
                        out resultIntersectionAttempt);
                }

                if (resultHitFound)
                {
                    intersectionKind = 1;
                    pointerIntersection = resultHit;
                    local = resultHit.LocalPoint;
                    raw = resultHit.RawPoint;
                    if (_launcherCalibrationActive)
                    {
                        WristLauncherCalibrationAction action =
                            ResultPanelTexture.HitTestWristLauncherCalibration(local.X, local.Y);
                        target = action == WristLauncherCalibrationAction.None
                            ? 0
                            : 400 + (int)action;
                    }
                    else if (_calibrationActive)
                    {
                        calibrationAction = ResultPanelTexture.HitTestCalibration(local.X, local.Y);
                        target = calibrationAction == ResultPanelCalibrationAction.None
                            ? 0
                            : 200 + (int)calibrationAction;
                    }
                    else
                    {
                        resultAction = _texture.HitTestResult(local.X, local.Y);
                        scrollbar = resultAction == ResultPanelAction.None
                            && _texture.IsScrollbar(local.X, local.Y);
                        target = resultAction != ResultPanelAction.None
                            ? 100 + (int)resultAction
                            : scrollbar ? 150 : 0;
                    }
                }
                else if (_launcherInterop?.TryComputeIntersection(
                    ray,
                    out OpenVrIntersection launcherHit) == true)
                {
                    intersectionKind = 2;
                    pointerIntersection = launcherHit;
                    local = launcherHit.LocalPoint;
                    raw = launcherHit.RawPoint;
                    launcherAction = _launcherTexture.HitTest(
                        _launcherState.View,
                        local.X,
                        local.Y);
                    target = launcherAction == WristLauncherAction.None
                        ? 0
                        : 300 + (int)launcherAction;
                }
            }

            UpdateCursor(pointerIntersection);

            UpdateLauncherHover(launcherAction);
            int activated = _activationGate.Update(
                target,
                sample.SelectActive,
                sample.SelectPressed,
                sample.SelectChanged);
            LogPointerDiagnostic(
                sample,
                intersectionAttempted,
                intersectionKind,
                resultIntersectionAttempted,
                resultIntersectionAttempt,
                raw,
                local,
                launcherAction,
                target,
                activated,
                facing);
            if (activated == 0)
            {
                return false;
            }

            if (activated >= 400)
            {
                HandleWristLauncherCalibrationClick(local.X, local.Y);
                return false;
            }

            if (activated >= 300)
            {
                HandleLauncherAction(launcherAction);
                return false;
            }

            if (activated >= 200)
            {
                HandlePlacementCalibrationClick(local.X, local.Y);
                return false;
            }

            if (activated == 150)
            {
                return _texture.BeginScrollbarInteraction(local.X, local.Y);
            }

            if (resultAction == ResultPanelAction.Close)
            {
                HandleUserResultClose();
                return false;
            }

            return _texture.Apply(resultAction);
        }
        catch (Exception exception)
        {
            // Input failure is fail-open: never enable SteamVR's global laser.
            TryLogPointerError(exception);
            DisconnectLauncher();
            return false;
        }
    }

    private void LogPointerDiagnostic(
        OpenVrPointerInputSample sample,
        bool intersectionAttempted,
        int intersectionKind,
        bool resultIntersectionAttempted,
        OpenVrIntersectionAttempt resultIntersectionAttempt,
        OverlayLocalPoint raw,
        OverlayLocalPoint local,
        WristLauncherAction launcherAction,
        int target,
        int activated,
        WristFacingSample facing)
    {
        if (_logger is null || _pointerDiagnosticsDisabled || _pointerDiagnosticCount >= 512)
        {
            return;
        }

        bool resultViewVisible = _visible
            && !_calibrationActive
            && !_launcherCalibrationActive
            && _launcherState.View == WristLauncherView.Result;
        // Interactive results are uploaded as one full texture per page. Keep
        // the legacy atlas-cell field explicitly unset so device diagnostics
        // cannot be mistaken for the superseded cells 3/4/5 path.
        int resultCell = -1;
        int resultPage = resultViewVisible ? _texture.CurrentResultPage : -1;
        int resultPageCount = resultViewVisible ? _texture.ResultPageCount : 0;
        PointerDiagnosticKey key = new(
            sample.SelectActive,
            sample.SelectPressed,
            sample.SelectChanged,
            sample.PoseActive,
            sample.PoseValid,
            sample.DeviceConnected,
            sample.HmdPoseValid,
            sample.LeftPoseValid,
            _launcherState.View,
            intersectionAttempted,
            intersectionKind,
            resultIntersectionAttempted
                ? resultIntersectionAttempt.Outcome
                : null,
            _visible,
            _interactive,
            _interop is not null,
            _cursorShown,
            launcherAction,
            target,
            activated,
            resultCell,
            resultPage,
            resultPageCount,
            facing.EntersFacingCone,
            facing.RemainsInFacingCone);
        if (_lastPointerDiagnostic == key)
        {
            return;
        }

        TimeSpan now = _interactionClock.Elapsed;
        if (!sample.SelectChanged && now < _nextPointerDiagnosticAt)
        {
            return;
        }

        try
        {
            _logger.Info(
                "openvr.pointer.diagnostic",
                Guid.Empty,
                ScanStage.Trigger,
                numericMetrics: new Dictionary<string, long>
                {
                    ["activated"] = activated,
                    ["connected"] = sample.DeviceConnected ? 1 : 0,
                    ["cursorReady"] = _cursorReady ? 1 : 0,
                    ["cursorShown"] = _cursorShown ? 1 : 0,
                    ["facingEnters"] = facing.EntersFacingCone ? 1 : 0,
                    ["facingMilli"] = (long)MathF.Round(facing.Alignment * 1000),
                    ["facingRemains"] = facing.RemainsInFacingCone ? 1 : 0,
                    ["hmdPose"] = sample.HmdPoseValid ? 1 : 0,
                    ["intersectionAttempted"] = intersectionAttempted ? 1 : 0,
                    ["intersection"] = intersectionKind,
                    ["launcherAtlas"] = _launcherAtlasLoaded ? 1 : 0,
                    ["launcherAction"] = (int)launcherAction,
                    ["launcherView"] = (int)_launcherState.View,
                    ["launcherVisible"] = _launcherInterop?.IsVisible() == true ? 1 : 0,
                    ["leftPose"] = sample.LeftPoseValid ? 1 : 0,
                    ["localXMilli"] = (long)MathF.Round(local.X * 1000),
                    ["localYMilli"] = (long)MathF.Round(local.Y * 1000),
                    ["poseActive"] = sample.PoseActive ? 1 : 0,
                    ["poseValid"] = sample.PoseValid ? 1 : 0,
                    ["rawXMilli"] = (long)MathF.Round(raw.X * 1000),
                    ["rawYMilli"] = (long)MathF.Round(raw.Y * 1000),
                    ["resultCell"] = resultCell,
                    ["resultFullTexture"] = resultViewVisible ? 1 : 0,
                    ["resultIntersectionAttempted"] = resultIntersectionAttempted ? 1 : 0,
                    ["resultIntersectionOutcome"] = resultIntersectionAttempted
                        ? (int)resultIntersectionAttempt.Outcome
                        : -1,
                    ["resultInteropReady"] = _interop is not null ? 1 : 0,
                    ["resultMappingAccepted"] = resultIntersectionAttempt.Outcome
                        == OpenVrIntersectionOutcome.Hit
                        ? 1
                        : 0,
                    ["resultNativeHit"] = resultIntersectionAttempt.Outcome is
                        OpenVrIntersectionOutcome.MappingRejected or
                        OpenVrIntersectionOutcome.Hit
                        ? 1
                        : 0,
                    ["resultPage"] = resultPage,
                    ["resultPageCount"] = resultPageCount,
                    ["resultPointerEnabled"] = _interactive ? 1 : 0,
                    ["resultRawXMilli"] = resultIntersectionAttempt.HasRawPoint
                        ? (long)MathF.Round(resultIntersectionAttempt.RawPoint.X * 1000)
                        : 0,
                    ["resultRawYMilli"] = resultIntersectionAttempt.HasRawPoint
                        ? (long)MathF.Round(resultIntersectionAttempt.RawPoint.Y * 1000)
                        : 0,
                    ["resultVisible"] = _visible ? 1 : 0,
                    ["selectActive"] = sample.SelectActive ? 1 : 0,
                    ["selectChanged"] = sample.SelectChanged ? 1 : 0,
                    ["selectPressed"] = sample.SelectPressed ? 1 : 0,
                    ["target"] = target,
                });
            _lastPointerDiagnostic = key;
            _nextPointerDiagnosticAt = now + TimeSpan.FromMilliseconds(250);
            _pointerDiagnosticCount++;
        }
        catch
        {
            // Optional diagnostics must never affect VR input or overlay life cycle.
            _pointerDiagnosticsDisabled = true;
        }
    }

    private void TryLogPointerError(Exception exception)
    {
        if (_logger is null || _pointerDiagnosticsDisabled)
        {
            return;
        }

        try
        {
            _logger.Error(
                "openvr.pointer.poll_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }
        catch
        {
            _pointerDiagnosticsDisabled = true;
        }
    }

    private bool UpdateLauncherFacing(
        OpenVrPointerInputSample sample,
        out WristFacingSample facing)
    {
        facing = WristFacingDetector.Evaluate(
            sample.LeftPose,
            sample.HmdPose,
            _launcherPlacement.CreateTransform());
        return _launcherState.UpdateFacing(
            facing.EntersFacingCone,
            facing.RemainsInFacingCone,
            _interactionClock.Elapsed);
    }

    private void UpdateLauncherHover(WristLauncherAction action)
    {
        if (_launcherHover == action)
        {
            return;
        }

        _launcherHover = action;
        ShowLauncherView();
    }

    private void UpdateCursor(OpenVrIntersection? intersection)
    {
        _pendingCursorIntersection = intersection;
        if (_cursorInterop is null || !_cursorReady || _captureSuppressed || intersection is null)
        {
            _cursorInterop?.Hide();
            _cursorShown = false;
            return;
        }

        _cursorInterop.SetAbsoluteTransform(
            OpenVrAbsoluteTransform.CreateCursor(intersection.Value));
        _cursorInterop.ShowCurrentTransform();
        _cursorShown = true;
    }

    private void HandleLauncherAction(WristLauncherAction action)
    {
        switch (action)
        {
            case WristLauncherAction.Expand:
                if (_launcherState.ExpandMenu())
                {
                    _launcherHover = WristLauncherAction.None;
                    ShowLauncherView();
                }

                break;
            case WristLauncherAction.CloseMenu:
                if (_launcherState.CollapseMenu())
                {
                    _launcherHover = WristLauncherAction.None;
                    ShowLauncherView();
                }

                break;
            case WristLauncherAction.Translate:
                if (_launcherState.BeginScan())
                {
                    _scanSessionActive = true;
                    _resultDesired = false;
                    _launcherInterop?.Hide();
                    _activationGate.Reset();
                    ScanRequested?.Invoke(this, EventArgs.Empty);
                }

                break;
            case WristLauncherAction.Calibrate:
                _ = TryStartWristLauncherCalibration();
                break;
        }
    }

    private void HandleUserResultClose()
    {
        Hide();
        ReturnToLauncher();
    }

    private void ShowLauncherView()
    {
        if (_captureSuppressed
            || _calibrationActive
            || !_launcherAtlasLoaded
            || !_launcherPoseAvailable
            || _launcherInterop is null
            || _launcherState.View is not (
                WristLauncherView.DimChip
                or WristLauncherView.ArmedChip
                or WristLauncherView.Menu
                or WristLauncherView.Calibrating))
        {
            return;
        }

        WristLauncherView textureView = _launcherState.View == WristLauncherView.Calibrating
            ? WristLauncherView.ArmedChip
            : _launcherState.View;
        int cell = _launcherTexture.GetAtlasCell(textureView, _launcherHover);
        _launcherInterop.SelectAtlasCell(
            cell,
            WristLauncherTexture.AtlasColumns,
            WristLauncherTexture.AtlasRows);
        float width = _launcherState.View == WristLauncherView.Menu
            ? checked((float)_launcherPlacement.MenuWidthMeters)
            : checked((float)_launcherPlacement.ChipWidthMeters);
        _ = _launcherInterop.TryShowTrackedDeviceOnly(width);
    }

    private void DisconnectLauncher()
    {
        if (_launcherCalibrationActive)
        {
            AbandonWristLauncherCalibration();
            Disconnect();
        }

        _activationGate.Reset();
        _launcherState.Stop();
        _launcherHover = WristLauncherAction.None;
        _launcherImageLoading = false;
        _launcherAtlasLoaded = false;
        _launcherPoseAvailable = false;
        _cursorImageLoading = false;
        _cursorReady = false;
        _cursorShown = false;
        _pendingCursorIntersection = null;
        _lastPointerDiagnostic = null;
        _input?.Dispose();
        _input = null;
        _launcherInterop?.Dispose();
        _launcherInterop = null;
        _cursorInterop?.Dispose();
        _cursorInterop = null;
        ScheduleLauncherRetry();
    }

    private bool TryRecoverWristLauncher()
    {
        bool connectionComplete = IsLauncherConnectionComplete(
            _input is not null,
            _launcherInterop is not null,
            _cursorInterop is not null);
        if (_disposed
            || !_launcherRecoveryEnabled
            || connectionComplete
            || _interactionClock.Elapsed < _nextLauncherStartAttemptAt)
        {
            return connectionComplete;
        }

        try
        {
            bool started = TryStartWristLauncherCore();
            if (!started)
            {
                ScheduleLauncherRetry();
                return false;
            }

            _nextLauncherStartAttemptAt = TimeSpan.Zero;
            _launcherRetryFailureLogCount = 0;
            if (_resultDesired)
            {
                _launcherState.ShowResult();
                _launcherInterop?.Hide();
            }
            else if (_scanSessionActive)
            {
                _ = _launcherState.BeginScan();
                _launcherInterop?.Hide();
            }
            else if (_visible)
            {
                _launcherState.ShowResult();
                _launcherInterop?.Hide();
            }

            return true;
        }
        catch (Exception exception)
        {
            if (_launcherRetryFailureLogCount < 5)
            {
                TryLogLauncherError("openvr.launcher.retry_failed", exception);
                _launcherRetryFailureLogCount++;
            }

            ScheduleLauncherRetry();
            return false;
        }
    }

    internal static bool IsLauncherConnectionComplete(
        bool inputConnected,
        bool launcherConnected,
        bool cursorConnected) =>
        inputConnected && launcherConnected && cursorConnected;

    private void ScheduleLauncherRetry()
    {
        if (_disposed || !_launcherRecoveryEnabled)
        {
            return;
        }

        _nextLauncherStartAttemptAt = _interactionClock.Elapsed + LauncherRetryDelay;
        _eventTimer.Start();
    }

    private void TryLogLauncherError(string eventName, Exception exception)
    {
        if (_logger is null || _pointerDiagnosticsDisabled)
        {
            return;
        }

        try
        {
            _logger.Error(
                eventName,
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }
        catch
        {
            _pointerDiagnosticsDisabled = true;
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

        _imageUpload.Begin(ResultPanelImageUploadKind.Atlas);
        byte[] pixels = _texture.RenderRgba();
        interop.SetImage(
            pixels,
            ResultPanelTexture.AtlasPixelWidth,
            ResultPanelTexture.AtlasPixelHeight);
    }

    private void BeginResultPageUpload(bool drainEvents = true)
    {
        OpenVrInterop interop = _interop
            ?? throw new InvalidOperationException("The SteamVR overlay is not connected.");
        while (drainEvents && interop.TryPollEvent(out _))
        {
            // Associate the next image completion event with this upload.
        }

        _imageUpload.Begin(ResultPanelImageUploadKind.ResultPage);
        byte[] pixels = _texture.RenderCurrentResultRgba();
        interop.SetImage(
            pixels,
            ResultPanelTexture.PixelWidth,
            ResultPanelTexture.PixelHeight);
    }

    internal static bool RequiresAtlasReloadAfterImageLoaded(
        ResultPanelImageUploadKind completedUpload,
        bool fullTextureStillActive,
        bool showAfterImageLoad) =>
        (completedUpload is ResultPanelImageUploadKind.ResultPage
            or ResultPanelImageUploadKind.Calibration)
        && !fullTextureStillActive
        && showAfterImageLoad;

    private void BeginCalibrationUpload()
    {
        OpenVrInterop interop = _interop
            ?? throw new InvalidOperationException("The SteamVR overlay is not connected.");
        while (interop.TryPollEvent(out _))
        {
            // Associate the next image completion event with this upload.
        }

        _imageUpload.Begin(ResultPanelImageUploadKind.Calibration);
        byte[] pixels = _texture.RenderCalibrationRgba();
        interop.SetImage(
            pixels,
            ResultPanelTexture.PixelWidth,
            ResultPanelTexture.PixelHeight);
    }

    private void SelectAtlasCell(int cell) => _interop!.SelectAtlasCell(
        cell,
        ResultPanelTexture.AtlasColumns,
        ResultPanelTexture.AtlasRows);

    private void SetPointerEnabled(bool enabled) => _interactive = enabled;

    private void ShowOverlay()
    {
        if (_captureSuppressed)
        {
            return;
        }

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
                ResultPanelPlacement updated = action == ResultPanelCalibrationAction.Reset
                    && _placement.Anchor == ResultPanelAnchor.LeftHand
                        ? ResultPanelPlacement.CreateAlignedToWristLauncher(
                            _launcherPlacement,
                            ResultPanelPlacement.Default.WidthMeters)
                        : ResultPanelCalibration.Apply(_placement, action);
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
        Exception? nativeFailure = null;
        if (!save)
        {
            _placement = original;
            try
            {
                _ = _interop?.SetPlacement(original);
            }
            catch (Exception exception)
            {
                nativeFailure = exception;
            }
        }

        try
        {
            PlacementCalibrationFinished?.Invoke(
                this,
                new ResultPanelPlacementCalibrationEventArgs(completed, save));
        }
        finally
        {
            if (nativeFailure is not null)
            {
                TryLogLauncherError(
                    "openvr.result_panel.calibration_finish_failed",
                    nativeFailure);
                Disconnect();
                ReturnToLauncher();
            }
        }

        if (nativeFailure is not null)
        {
            return;
        }

        Hide();
        if (_launcherState.View != WristLauncherView.Scanning)
        {
            ReturnToLauncher();
        }
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
        bool launcherConnected = _launcherInterop is not null;
        bool keepLauncherHiddenForActiveScan = _scanSessionActive && !_resultDesired;
        bool restoreLauncherPlacement = _launcherCalibrationActive;
        bool restoreLauncher = launcherConnected
            && !keepLauncherHiddenForActiveScan
            && (_calibrationActive
                || _launcherCalibrationActive
                || _launcherState.View is WristLauncherView.Result or WristLauncherView.Scanning);
        AbandonWristLauncherCalibration();
        AbandonPlacementCalibration();
        _eventTimer.Stop();
        _visible = false;
        _interactive = false;
        _imageUpload.Reset();
        _showAfterImageLoad = false;
        _enableInteractionAfterImageLoad = false;
        _queuedResultTitle = null;
        _queuedResultBody = null;
        _interop?.Dispose();
        _interop = null;
        if (keepLauncherHiddenForActiveScan)
        {
            // Losing only the result overlay while OCR/translation is still running
            // must not end the scan session or reveal an actionable launcher early.
            // A later result failure/completion explicitly returns to the chip.
            if (launcherConnected && !_disposed)
            {
                _eventTimer.Start();
            }
        }
        else if (restoreLauncher)
        {
            if (restoreLauncherPlacement && _launcherInterop is not null)
            {
                try
                {
                    _ = _launcherInterop.SetPlacement(_launcherPlacement);
                }
                catch
                {
                    DisconnectLauncher();
                    return;
                }
            }

            ReturnToLauncher();
        }
        else if (launcherConnected && !_disposed)
        {
            _eventTimer.Start();
        }
    }
}

internal readonly record struct PointerDiagnosticKey(
    bool SelectActive,
    bool SelectPressed,
    bool SelectChanged,
    bool PoseActive,
    bool PoseValid,
    bool DeviceConnected,
    bool HmdPoseValid,
    bool LeftPoseValid,
    WristLauncherView LauncherView,
    bool IntersectionAttempted,
    int IntersectionKind,
    OpenVrIntersectionOutcome? ResultIntersectionOutcome,
    bool ResultVisible,
    bool ResultPointerEnabled,
    bool ResultInteropReady,
    bool CursorShown,
    WristLauncherAction LauncherAction,
    int Target,
    int Activated,
    int ResultCell,
    int ResultPage,
    int ResultPageCount,
    bool FacingEnters,
    bool FacingRemains);

internal sealed record ResultPanelPlacementCalibrationEventArgs(
    ResultPanelPlacement Placement,
    bool SaveRequested);

internal enum ResultPanelImageUploadKind
{
    None,
    Atlas,
    ResultPage,
    Calibration,
}

internal sealed class ResultPanelImageUploadTracker
{
    private ResultPanelImageUploadKind _kind;

    public bool InFlight => _kind != ResultPanelImageUploadKind.None;

    public bool AtlasLoaded { get; private set; }

    public void Begin(ResultPanelImageUploadKind kind)
    {
        if (kind == ResultPanelImageUploadKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _kind = kind;
        AtlasLoaded = false;
    }

    public ResultPanelImageUploadKind Complete()
    {
        ResultPanelImageUploadKind completedKind = _kind;
        AtlasLoaded = completedKind == ResultPanelImageUploadKind.Atlas;
        _kind = ResultPanelImageUploadKind.None;
        return completedKind;
    }

    public void Reset()
    {
        _kind = ResultPanelImageUploadKind.None;
        AtlasLoaded = false;
    }
}
