using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VrcVa.Windows.OpenVr;

internal sealed class OpenVrInterop : IOpenVrOverlayCaptureGate, IDisposable
{
    private const string SystemInterface = "FnTable:IVRSystem_026";
    private const string OverlayInterface = "FnTable:IVROverlay_027";
    private const ulong InvalidOverlayHandle = 0;
    private const uint InvalidTrackedDeviceIndex = uint.MaxValue;
    private const uint HmdTrackedDeviceIndex = 0;
    private const int VrEventSize = 64;
    private const int OverlayIntersectionParamsSize = 28;
    private const int OverlayIntersectionResultsSize = 36;
    private const uint OverlayIntersectionMaskPrimitiveSize = 20;
    private const string DefaultOverlayKey = "com.na2kibb.vrcva.result";
    private const string DefaultOverlayName = "VRChat Visual Assistant Result";

    private readonly OpenVrRuntime _runtime;
    private readonly GetTrackedDeviceIndexForControllerRoleDelegate _getTrackedDeviceIndexForControllerRole;
    private readonly IsTrackedDeviceConnectedDelegate _isTrackedDeviceConnected;
    private readonly CreateOverlayDelegate _createOverlay;
    private readonly DestroyOverlayDelegate _destroyOverlay;
    private readonly HideOverlayDelegate _hideOverlay;
    private readonly IsOverlayVisibleDelegate _isOverlayVisible;
    private readonly WaitFrameSyncDelegate _waitFrameSync;
    private readonly SetOverlayWidthInMetersDelegate _setOverlayWidthInMeters;
    private readonly SetOverlayTextureBoundsDelegate _setOverlayTextureBounds;
    private readonly SetOverlayTransformTrackedDeviceRelativeDelegate _setOverlayTransform;
    private readonly SetOverlayTransformAbsoluteDelegate _setOverlayTransformAbsolute;
    private readonly SetOverlayInputMethodDelegate _setOverlayInputMethod;
    private readonly SetOverlayMouseScaleDelegate _setOverlayMouseScale;
    private readonly ComputeOverlayIntersectionDelegate _computeOverlayIntersection;
    private readonly SetOverlayIntersectionMaskDelegate _setOverlayIntersectionMask;
    private readonly SetOverlayFlagDelegate _setOverlayFlag;
    private readonly SetOverlaySortOrderDelegate _setOverlaySortOrder;
    private readonly SetOverlayRawDelegate _setOverlayRaw;
    private readonly ShowOverlayDelegate _showOverlay;
    private readonly PollNextOverlayEventDelegate _pollNextOverlayEvent;
    private readonly string _overlayKey;
    private readonly string _overlayName;
    private ResultPanelPlacement _placement;
    private ResultPanelTransform _placementTransform;
    private OverlayTextureView _textureView;
    private ulong _overlayHandle;
    private bool _disposed;

    private OpenVrInterop(
        OpenVrRuntime runtime,
        IntPtr systemFunctionTable,
        IntPtr overlayFunctionTable,
        ResultPanelPlacement placement,
        OverlaySurfaceSpec surface,
        string overlayKey,
        string overlayName)
    {
        ValidateAbi();

        _runtime = runtime;
        _placement = placement;
        _placementTransform = placement.CreateTransform();
        _textureView = OverlayTextureView.CreateFull(surface);
        _overlayKey = ValidateOverlayText(overlayKey, nameof(overlayKey));
        _overlayName = ValidateOverlayText(overlayName, nameof(overlayName));
        _placement.Validate();
        _getTrackedDeviceIndexForControllerRole = OpenVrRuntime.GetFunction<GetTrackedDeviceIndexForControllerRoleDelegate>(
            systemFunctionTable,
            SystemSlot.GetTrackedDeviceIndexForControllerRole);
        _isTrackedDeviceConnected = OpenVrRuntime.GetFunction<IsTrackedDeviceConnectedDelegate>(
            systemFunctionTable,
            SystemSlot.IsTrackedDeviceConnected);
        _createOverlay = OpenVrRuntime.GetFunction<CreateOverlayDelegate>(overlayFunctionTable, OverlaySlot.CreateOverlay);
        _destroyOverlay = OpenVrRuntime.GetFunction<DestroyOverlayDelegate>(overlayFunctionTable, OverlaySlot.DestroyOverlay);
        _setOverlayFlag = OpenVrRuntime.GetFunction<SetOverlayFlagDelegate>(overlayFunctionTable, OverlaySlot.SetOverlayFlag);
        _setOverlaySortOrder = OpenVrRuntime.GetFunction<SetOverlaySortOrderDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlaySortOrder);
        _setOverlayWidthInMeters = OpenVrRuntime.GetFunction<SetOverlayWidthInMetersDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayWidthInMeters);
        _setOverlayTextureBounds = OpenVrRuntime.GetFunction<SetOverlayTextureBoundsDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayTextureBounds);
        _setOverlayTransform = OpenVrRuntime.GetFunction<SetOverlayTransformTrackedDeviceRelativeDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayTransformTrackedDeviceRelative);
        _setOverlayTransformAbsolute = OpenVrRuntime.GetFunction<SetOverlayTransformAbsoluteDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayTransformAbsolute);
        _showOverlay = OpenVrRuntime.GetFunction<ShowOverlayDelegate>(overlayFunctionTable, OverlaySlot.ShowOverlay);
        _hideOverlay = OpenVrRuntime.GetFunction<HideOverlayDelegate>(overlayFunctionTable, OverlaySlot.HideOverlay);
        _isOverlayVisible = OpenVrRuntime.GetFunction<IsOverlayVisibleDelegate>(
            overlayFunctionTable,
            OverlaySlot.IsOverlayVisible);
        _waitFrameSync = OpenVrRuntime.GetFunction<WaitFrameSyncDelegate>(
            overlayFunctionTable,
            OverlaySlot.WaitFrameSync);
        _pollNextOverlayEvent = OpenVrRuntime.GetFunction<PollNextOverlayEventDelegate>(
            overlayFunctionTable,
            OverlaySlot.PollNextOverlayEvent);
        _setOverlayInputMethod = OpenVrRuntime.GetFunction<SetOverlayInputMethodDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayInputMethod);
        _setOverlayMouseScale = OpenVrRuntime.GetFunction<SetOverlayMouseScaleDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayMouseScale);
        _computeOverlayIntersection = OpenVrRuntime.GetFunction<ComputeOverlayIntersectionDelegate>(
            overlayFunctionTable,
            OverlaySlot.ComputeOverlayIntersection);
        _setOverlayIntersectionMask = OpenVrRuntime.GetFunction<SetOverlayIntersectionMaskDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayIntersectionMask);
        _setOverlayRaw = OpenVrRuntime.GetFunction<SetOverlayRawDelegate>(
            overlayFunctionTable,
            OverlaySlot.SetOverlayRaw);
    }

    public bool LastPlacementUsedFallback { get; private set; }

    public static bool TryCreate(ResultPanelPlacement placement, out OpenVrInterop? interop)
        => TryCreate(
            placement,
            new OverlaySurfaceSpec(ResultPanelTexture.PixelWidth, ResultPanelTexture.PixelHeight),
            DefaultOverlayKey,
            DefaultOverlayName,
            out interop);

    public static bool TryCreate(
        ResultPanelPlacement placement,
        OverlaySurfaceSpec surface,
        string overlayKey,
        string overlayName,
        out OpenVrInterop? interop)
    {
        placement.Validate();
        surface.Validate();
        _ = ValidateOverlayText(overlayKey, nameof(overlayKey));
        _ = ValidateOverlayText(overlayName, nameof(overlayName));
        interop = null;
        if (!OpenVrRuntime.TryAcquire(out OpenVrRuntime? runtime))
        {
            return false;
        }

        OpenVrInterop? candidate = null;
        try
        {
            IntPtr systemTable = runtime!.GetInterface(SystemInterface);
            IntPtr overlayTable = runtime.GetInterface(OverlayInterface);
            candidate = new OpenVrInterop(
                runtime,
                systemTable,
                overlayTable,
                placement,
                surface,
                overlayKey,
                overlayName);
            candidate.CreateAndConfigureOverlay();
            interop = candidate;
            return true;
        }
        catch
        {
            if (candidate is not null)
            {
                candidate.Dispose();
            }
            else
            {
                runtime?.Dispose();
            }

            throw;
        }
    }

    public static bool TryCreate(
        WristLauncherPlacement placement,
        OverlaySurfaceSpec surface,
        string overlayKey,
        string overlayName,
        out OpenVrInterop? interop)
    {
        ArgumentNullException.ThrowIfNull(placement);
        placement.Validate();
        ResultPanelPlacement bootstrap = CreatePlacementMetadata(placement);
        if (!TryCreate(bootstrap, surface, overlayKey, overlayName, out interop))
        {
            return false;
        }

        try
        {
            _ = interop!.SetPlacement(placement);
            return true;
        }
        catch
        {
            interop?.Dispose();
            interop = null;
            throw;
        }
    }

    public static bool TryCreate(out OpenVrInterop? interop) =>
        TryCreate(ResultPanelPlacement.HeadsetFallback, out interop);

    public bool SetPlacement(ResultPanelPlacement placement)
    {
        ThrowIfDisposed();
        placement.Validate();
        _placement = placement;
        _placementTransform = placement.CreateTransform();
        LastPlacementUsedFallback = ApplyPlacement();
        return LastPlacementUsedFallback;
    }

    public bool SetPlacement(WristLauncherPlacement placement)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(placement);
        placement.Validate();
        _placement = CreatePlacementMetadata(placement);
        _placementTransform = placement.CreateTransform();
        LastPlacementUsedFallback = ApplyPlacement();
        return LastPlacementUsedFallback;
    }

    public void SetWidth(float widthMeters)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(widthMeters) || widthMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMeters));
        }

        EnsureSuccess(_setOverlayWidthInMeters(_overlayHandle, widthMeters));
    }

    public void SetSortOrder(uint sortOrder)
    {
        ThrowIfDisposed();
        EnsureSuccess(_setOverlaySortOrder(_overlayHandle, sortOrder));
    }

    public void SetAbsoluteTransform(OpenVrAbsoluteTransform transform)
    {
        ThrowIfDisposed();
        HmdMatrix34 native = HmdMatrix34.From(transform);
        EnsureSuccess(_setOverlayTransformAbsolute(
            _overlayHandle,
            TrackingUniverseOrigin.Standing,
            ref native));
    }

    public void SetImage(ReadOnlySpan<byte> rgbaPixels, uint width, uint height)
    {
        ThrowIfDisposed();
        if (rgbaPixels.Length != checked((int)(width * height * 4)))
        {
            throw new ArgumentException("The RGBA buffer size does not match its dimensions.", nameof(rgbaPixels));
        }

        unsafe
        {
            fixed (byte* buffer = rgbaPixels)
            {
                EnsureSuccess(_setOverlayRaw(
                    _overlayHandle,
                    (IntPtr)buffer,
                    width,
                    height,
                    4));
            }
        }
    }

    public void SelectAtlasCell(int cell, int columns, int rows)
    {
        ThrowIfDisposed();
        SetTextureView(OverlayTextureView.CreateAtlasCell(
            _textureView.Surface,
            cell,
            columns,
            rows));
    }

    public void SelectFullTexture()
    {
        ThrowIfDisposed();
        SetTextureView(OverlayTextureView.CreateFull(_textureView.Surface));
    }

    public void Show()
    {
        ThrowIfDisposed();
        LastPlacementUsedFallback = ApplyPlacement();
        EnsureSuccess(_showOverlay(_overlayHandle));
    }

    public void ShowCurrentTransform()
    {
        ThrowIfDisposed();
        EnsureSuccess(_showOverlay(_overlayHandle));
    }

    public bool TryShowTrackedDeviceOnly(float? widthMeters = null)
    {
        ThrowIfDisposed();
        if (_placement.Anchor == ResultPanelAnchor.Headset)
        {
            throw new InvalidOperationException("A headset-relative overlay is not a controller-only surface.");
        }

        ETrackedControllerRole role = _placement.Anchor == ResultPanelAnchor.LeftHand
            ? ETrackedControllerRole.LeftHand
            : ETrackedControllerRole.RightHand;
        uint trackedDevice = _getTrackedDeviceIndexForControllerRole(role);
        if (trackedDevice == InvalidTrackedDeviceIndex
            || !_isTrackedDeviceConnected(trackedDevice))
        {
            Hide();
            return false;
        }

        float width = widthMeters ?? checked((float)_placement.WidthMeters);
        if (!float.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMeters));
        }

        EnsureSuccess(_setOverlayWidthInMeters(_overlayHandle, width));
        HmdMatrix34 transform = HmdMatrix34.From(_placementTransform);
        if (_setOverlayTransform(_overlayHandle, trackedDevice, ref transform)
            != EvrOverlayError.None)
        {
            Hide();
            return false;
        }

        LastPlacementUsedFallback = false;
        EnsureSuccess(_showOverlay(_overlayHandle));
        return true;
    }

    public void Hide()
    {
        if (_disposed || _overlayHandle == InvalidOverlayHandle)
        {
            return;
        }

        _ = _hideOverlay(_overlayHandle);
    }

    public bool IsVisible()
    {
        ThrowIfDisposed();
        return _isOverlayVisible(_overlayHandle);
    }

    public void HideAndConfirmInvisible(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureSuccess(_hideOverlay(_overlayHandle));

        Stopwatch timer = Stopwatch.StartNew();
        while (_isOverlayVisible(_overlayHandle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= TimeSpan.FromSeconds(1))
            {
                throw new InvalidOperationException(
                    "The VRCVA overlay remained visible after HideOverlay.");
            }

            Thread.Sleep(5);
        }
    }

    public void EndCaptureSuppression()
    {
        // A single overlay has no deferred-show coordinator. The owning panel
        // implements the actual multi-overlay suppression lifecycle.
    }

    public void WaitFrameSync(uint timeoutMilliseconds = 1000)
    {
        ThrowIfDisposed();
        EnsureSuccess(_waitFrameSync(timeoutMilliseconds));
    }

    public bool TryComputeIntersection(OpenVrRay ray, out OpenVrIntersection intersection)
    {
        OpenVrIntersectionAttempt attempt;
        return TryComputeIntersection(ray, out intersection, out attempt);
    }

    public bool TryComputeIntersection(
        OpenVrRay ray,
        out OpenVrIntersection intersection,
        out OpenVrIntersectionOutcome outcome)
    {
        bool hit = TryComputeIntersection(
            ray,
            out intersection,
            out OpenVrIntersectionAttempt attempt);
        outcome = attempt.Outcome;
        return hit;
    }

    public bool TryComputeIntersection(
        OpenVrRay ray,
        out OpenVrIntersection intersection,
        out OpenVrIntersectionAttempt attempt)
    {
        ThrowIfDisposed();
        if (!_isOverlayVisible(_overlayHandle))
        {
            intersection = default;
            attempt = new OpenVrIntersectionAttempt(
                OpenVrIntersectionOutcome.OverlayHidden,
                default);
            return false;
        }

        VrOverlayIntersectionParams parameters = new()
        {
            Source = HmdVector3.From(ray.Source),
            Direction = HmdVector3.From(ray.Direction),
            Origin = TrackingUniverseOrigin.Standing,
        };
        VrOverlayIntersectionResults results = default;
        if (!_computeOverlayIntersection(_overlayHandle, ref parameters, ref results))
        {
            intersection = default;
            attempt = new OpenVrIntersectionAttempt(
                OpenVrIntersectionOutcome.NativeMiss,
                default);
            return false;
        }

        OpenVrNativeIntersection nativeIntersection = new(
            new OverlayLocalPoint(results.UVs.X, results.UVs.Y),
            results.Point.ToOpenVrVector3(),
            results.Normal.ToOpenVrVector3(),
            results.Distance);
        return TryMapNativeIntersection(
            ray,
            _textureView,
            nativeIntersection,
            out intersection,
            out attempt);
    }

    public bool TryPollEvent(out OpenVrEvent overlayEvent)
    {
        ThrowIfDisposed();
        VrEvent nativeEvent = default;
        if (!_pollNextOverlayEvent(
                _overlayHandle,
                ref nativeEvent,
                VrEventSize))
        {
            overlayEvent = default;
            return false;
        }

        overlayEvent = new OpenVrEvent(
            nativeEvent.EventType,
            nativeEvent.Data.Mouse.X,
            nativeEvent.Data.Mouse.Y,
            nativeEvent.Data.Mouse.Button,
            nativeEvent.Data.Scroll.YDelta);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_overlayHandle != InvalidOverlayHandle)
        {
            _ = _hideOverlay(_overlayHandle);
            _ = _destroyOverlay(_overlayHandle);
            _overlayHandle = InvalidOverlayHandle;
        }

        _runtime.Dispose();
    }

    private void CreateAndConfigureOverlay()
    {
        IntPtr key = Marshal.StringToCoTaskMemUTF8(_overlayKey);
        IntPtr name = Marshal.StringToCoTaskMemUTF8(_overlayName);
        try
        {
            EnsureSuccess(_createOverlay(key, name, ref _overlayHandle));
        }
        finally
        {
            Marshal.FreeCoTaskMem(key);
            Marshal.FreeCoTaskMem(name);
        }

        SetTextureView(_textureView);
        LastPlacementUsedFallback = ApplyPlacement();

        EnsureSuccess(_setOverlayInputMethod(_overlayHandle, VrOverlayInputMethod.None));
        ApplyInteractionConfiguration(
            _textureView.Surface,
            SetMouseScale,
            SetIntersectionMask);
        SetFlag(VrOverlayFlag.MakeOverlaysInteractiveIfVisible, false);
    }

    private void SetMouseScale(OpenVrOverlayMouseScaleCall call)
    {
        HmdVector2 mouseScale = new(call.Width, call.Height);
        EnsureSuccess(_setOverlayMouseScale(_overlayHandle, ref mouseScale));
    }

    private void SetIntersectionMask(OpenVrOverlayIntersectionMaskCall call)
    {
        VrOverlayIntersectionMaskPrimitive intersectionMask =
            VrOverlayIntersectionMaskPrimitive.CreateRectangle(call.Rectangle);
        EnsureSuccess(_setOverlayIntersectionMask(
            _overlayHandle,
            ref intersectionMask,
            call.PrimitiveCount,
            call.PrimitiveSize));
    }

    private bool ApplyPlacement()
    {
        EnsureSuccess(_setOverlayWidthInMeters(
            _overlayHandle,
            checked((float)_placement.WidthMeters)));

        HmdMatrix34 transform = HmdMatrix34.From(_placementTransform);
        if (_placement.Anchor == ResultPanelAnchor.Headset)
        {
            EnsureSuccess(_setOverlayTransform(
                _overlayHandle,
                HmdTrackedDeviceIndex,
                ref transform));
            return false;
        }

        ETrackedControllerRole role = _placement.Anchor == ResultPanelAnchor.LeftHand
            ? ETrackedControllerRole.LeftHand
            : ETrackedControllerRole.RightHand;
        uint trackedDevice = _getTrackedDeviceIndexForControllerRole(role);
        if (trackedDevice != InvalidTrackedDeviceIndex
            && _isTrackedDeviceConnected(trackedDevice))
        {
            EvrOverlayError error = _setOverlayTransform(
                _overlayHandle,
                trackedDevice,
                ref transform);
            if (error == EvrOverlayError.None)
            {
                return false;
            }
        }

        ResultPanelPlacement fallback = ResultPanelPlacement.HeadsetFallback;
        EnsureSuccess(_setOverlayWidthInMeters(
            _overlayHandle,
            checked((float)fallback.WidthMeters)));
        HmdMatrix34 fallbackTransform = HmdMatrix34.From(fallback.CreateTransform());
        EnsureSuccess(_setOverlayTransform(
            _overlayHandle,
            HmdTrackedDeviceIndex,
            ref fallbackTransform));
        return true;
    }

    private static ResultPanelPlacement CreatePlacementMetadata(
        WristLauncherPlacement placement) => new(
            ResultPanelAnchor.LeftHand,
            placement.X,
            placement.Y,
            placement.Z,
            PitchDegrees: 0,
            YawDegrees: 0,
            RollDegrees: 0,
            placement.MenuWidthMeters);

    private void SetTextureView(OverlayTextureView textureView)
    {
        OverlayTextureBounds viewBounds = textureView.UpperLeftTextureBounds;
        VrTextureBounds nativeBounds = new(
            viewBounds.UMin,
            viewBounds.VMin,
            viewBounds.UMax,
            viewBounds.VMax);
        EnsureSuccess(_setOverlayTextureBounds(_overlayHandle, ref nativeBounds));
        _textureView = textureView;
    }

    private void SetFlag(VrOverlayFlag flag, bool enabled = true) =>
        EnsureSuccess(_setOverlayFlag(_overlayHandle, flag, enabled));

    private static void EnsureSuccess(EvrOverlayError error)
    {
        if (error != EvrOverlayError.None)
        {
            throw new InvalidOperationException($"OpenVR overlay call failed with code {(int)error}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static void ValidateAbi()
    {
        AssertSize<VrEvent>(VrEventSize);
        AssertSize<HmdVector2>(8);
        AssertSize<HmdVector3>(12);
        AssertSize<VrOverlayIntersectionParams>(OverlayIntersectionParamsSize);
        AssertSize<VrOverlayIntersectionResults>(OverlayIntersectionResultsSize);
        AssertSize<VrOverlayIntersectionMaskRectangle>(16);
        AssertSize<VrOverlayIntersectionMaskPrimitiveData>(16);
        AssertSize<VrOverlayIntersectionMaskPrimitive>(
            checked((int)OverlayIntersectionMaskPrimitiveSize));
    }

    internal const int SetOverlayMouseScaleFunctionSlot = OverlaySlot.SetOverlayMouseScale;

    internal const int SetOverlayIntersectionMaskFunctionSlot = OverlaySlot.SetOverlayIntersectionMask;

    internal const uint IntersectionMaskPrimitiveAbiSize = OverlayIntersectionMaskPrimitiveSize;

    internal static OpenVrOverlayInteractionConfiguration CreateInteractionConfiguration(
        OverlaySurfaceSpec surface)
    {
        surface.Validate();
        return new OpenVrOverlayInteractionConfiguration(
            surface.LogicalWidth,
            surface.LogicalHeight,
            new OpenVrIntersectionMaskRectangleSpec(
                0,
                0,
                surface.LogicalWidth,
                surface.LogicalHeight));
    }

    internal static void ApplyInteractionConfiguration(
        OverlaySurfaceSpec surface,
        Action<OpenVrOverlayMouseScaleCall> setMouseScale,
        Action<OpenVrOverlayIntersectionMaskCall> setIntersectionMask)
    {
        ArgumentNullException.ThrowIfNull(setMouseScale);
        ArgumentNullException.ThrowIfNull(setIntersectionMask);
        OpenVrOverlayInteractionConfiguration configuration =
            CreateInteractionConfiguration(surface);
        setMouseScale(new OpenVrOverlayMouseScaleCall(
            configuration.MouseScaleWidth,
            configuration.MouseScaleHeight));
        setIntersectionMask(new OpenVrOverlayIntersectionMaskCall(
            configuration.IntersectionMask,
            PrimitiveCount: 1,
            PrimitiveSize: OverlayIntersectionMaskPrimitiveSize));
    }

    internal static bool TryMapNativeIntersection(
        OpenVrRay ray,
        OverlayTextureView textureView,
        OpenVrNativeIntersection nativeIntersection,
        out OpenVrIntersection intersection,
        out OpenVrIntersectionAttempt attempt)
    {
        OverlayLocalPoint rawPoint = nativeIntersection.RawPoint;
        if (!textureView.TryMapAtlasGlobalLowerOriginToTopLeftLocal(
                rawPoint.X,
                rawPoint.Y,
                out OverlayLocalPoint localPoint))
        {
            intersection = default;
            attempt = new OpenVrIntersectionAttempt(
                OpenVrIntersectionOutcome.MappingRejected,
                rawPoint);
            return false;
        }

        intersection = new OpenVrIntersection(
            localPoint,
            nativeIntersection.Point,
            nativeIntersection.Normal,
            ray.Direction,
            nativeIntersection.Distance,
            rawPoint);
        attempt = new OpenVrIntersectionAttempt(OpenVrIntersectionOutcome.Hit, rawPoint);
        return true;
    }

    private static void AssertSize<T>(int expected)
        where T : struct
    {
        int actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"OpenVR ABI size mismatch for {typeof(T).Name}: expected {expected}, actual {actual}.");
        }
    }

    private static string ValidateOverlayText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0'))
        {
            throw new ArgumentException("OpenVR overlay text cannot contain a null character.", parameterName);
        }

        // The published OpenVR ABI allows 128 bytes including the null terminator.
        if (Encoding.UTF8.GetByteCount(value) >= 128)
        {
            throw new ArgumentException("OpenVR overlay text must be shorter than 128 UTF-8 bytes.", parameterName);
        }

        return value;
    }

    // Slots are from Valve's generated IVRSystem_026 function table.
    private static class SystemSlot
    {
        public const int GetTrackedDeviceIndexForControllerRole = 18;
        public const int IsTrackedDeviceConnected = 21;
    }

    // Slots are from Valve's generated IVROverlay_027 function table. Published
    // OpenVR interface versions remain available for backwards compatibility.
    private static class OverlaySlot
    {
        public const int CreateOverlay = 1;
        public const int DestroyOverlay = 2;
        public const int SetOverlayFlag = 10;
        public const int SetOverlaySortOrder = 19;
        public const int SetOverlayWidthInMeters = 21;
        public const int SetOverlayTextureBounds = 29;
        public const int SetOverlayTransformAbsolute = 32;
        public const int SetOverlayTransformTrackedDeviceRelative = 34;
        public const int ShowOverlay = 41;
        public const int HideOverlay = 42;
        public const int IsOverlayVisible = 43;
        public const int WaitFrameSync = 45;
        public const int PollNextOverlayEvent = 46;
        public const int SetOverlayInputMethod = 48;
        public const int SetOverlayMouseScale = 50;
        public const int ComputeOverlayIntersection = 51;
        public const int SetOverlayIntersectionMask = 53;
        public const int SetOverlayRaw = 60;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetTrackedDeviceIndexForControllerRoleDelegate(
        ETrackedControllerRole deviceType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsTrackedDeviceConnectedDelegate(uint deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError CreateOverlayDelegate(
        IntPtr key,
        IntPtr name,
        ref ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError DestroyOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayFlagDelegate(
        ulong overlayHandle,
        VrOverlayFlag flag,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayWidthInMetersDelegate(
        ulong overlayHandle,
        float widthInMeters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlaySortOrderDelegate(
        ulong overlayHandle,
        uint sortOrder);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayTextureBoundsDelegate(
        ulong overlayHandle,
        ref VrTextureBounds textureBounds);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayTransformTrackedDeviceRelativeDelegate(
        ulong overlayHandle,
        uint trackedDevice,
        ref HmdMatrix34 transform);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayTransformAbsoluteDelegate(
        ulong overlayHandle,
        TrackingUniverseOrigin origin,
        ref HmdMatrix34 transform);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError ShowOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError HideOverlayDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool IsOverlayVisibleDelegate(ulong overlayHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError WaitFrameSyncDelegate(uint timeoutMilliseconds);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool PollNextOverlayEventDelegate(
        ulong overlayHandle,
        ref VrEvent overlayEvent,
        uint eventSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayInputMethodDelegate(
        ulong overlayHandle,
        VrOverlayInputMethod inputMethod);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayMouseScaleDelegate(
        ulong overlayHandle,
        ref HmdVector2 mouseScale);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool ComputeOverlayIntersectionDelegate(
        ulong overlayHandle,
        ref VrOverlayIntersectionParams parameters,
        ref VrOverlayIntersectionResults results);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayIntersectionMaskDelegate(
        ulong overlayHandle,
        ref VrOverlayIntersectionMaskPrimitive maskPrimitives,
        uint numberOfMaskPrimitives,
        uint primitiveSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrOverlayError SetOverlayRawDelegate(
        ulong overlayHandle,
        IntPtr buffer,
        uint width,
        uint height,
        uint bytesPerPixel);

    private enum EvrOverlayError
    {
        None = 0,
    }

    private enum ETrackedControllerRole
    {
        LeftHand = 1,
        RightHand = 2,
    }

    private enum VrOverlayInputMethod
    {
        None = 0,
        Mouse = 1,
    }

    private enum TrackingUniverseOrigin
    {
        Standing = 1,
    }

    internal enum VrOverlayIntersectionMaskPrimitiveType
    {
        Rectangle = 0,
        Circle = 1,
    }

    [Flags]
    private enum VrOverlayFlag
    {
        SendVrDiscreteScrollEvents = 64,
        MakeOverlaysInteractiveIfVisible = 65536,
        SendVrSmoothScrollEvents = 131072,
        EnableControlBar = 8388608,
        EnableControlBarClose = 33554432,
        EnableClickStabilization = 134217728,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdMatrix34
    {
        public float M0;
        public float M1;
        public float M2;
        public float M3;
        public float M4;
        public float M5;
        public float M6;
        public float M7;
        public float M8;
        public float M9;
        public float M10;
        public float M11;

        public static HmdMatrix34 From(ResultPanelTransform transform) => new()
        {
            M0 = transform.M0,
            M1 = transform.M1,
            M2 = transform.M2,
            M3 = transform.M3,
            M4 = transform.M4,
            M5 = transform.M5,
            M6 = transform.M6,
            M7 = transform.M7,
            M8 = transform.M8,
            M9 = transform.M9,
            M10 = transform.M10,
            M11 = transform.M11,
        };

        public static HmdMatrix34 From(OpenVrAbsoluteTransform transform) => new()
        {
            M0 = transform.M0,
            M1 = transform.M1,
            M2 = transform.M2,
            M3 = transform.M3,
            M4 = transform.M4,
            M5 = transform.M5,
            M6 = transform.M6,
            M7 = transform.M7,
            M8 = transform.M8,
            M9 = transform.M9,
            M10 = transform.M10,
            M11 = transform.M11,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector2(float x, float y)
    {
        public float X = x;
        public float Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector3
    {
        public float X;
        public float Y;
        public float Z;

        public static HmdVector3 From(OpenVrVector3 value) => new()
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
        };

        public readonly OpenVrVector3 ToOpenVrVector3() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrOverlayIntersectionParams
    {
        public HmdVector3 Source;
        public HmdVector3 Direction;
        public TrackingUniverseOrigin Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrOverlayIntersectionResults
    {
        public HmdVector3 Point;
        public HmdVector3 Normal;
        public HmdVector2 UVs;
        public float Distance;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VrOverlayIntersectionMaskRectangle
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;

        public static VrOverlayIntersectionMaskRectangle From(
            OpenVrIntersectionMaskRectangleSpec rectangle) => new()
            {
                TopLeftX = rectangle.X,
                TopLeftY = rectangle.Y,
                Width = rectangle.Width,
                Height = rectangle.Height,
            };
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct VrOverlayIntersectionMaskPrimitiveData
    {
        [FieldOffset(0)]
        public VrOverlayIntersectionMaskRectangle Rectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VrOverlayIntersectionMaskPrimitive
    {
        public VrOverlayIntersectionMaskPrimitiveType PrimitiveType;
        public VrOverlayIntersectionMaskPrimitiveData Primitive;

        public static VrOverlayIntersectionMaskPrimitive CreateRectangle(
            OpenVrIntersectionMaskRectangleSpec rectangle) => new()
            {
                PrimitiveType = VrOverlayIntersectionMaskPrimitiveType.Rectangle,
                Primitive = new VrOverlayIntersectionMaskPrimitiveData
                {
                    Rectangle = VrOverlayIntersectionMaskRectangle.From(rectangle),
                },
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrTextureBounds(float uMin, float vMin, float uMax, float vMax)
    {
        public float UMin = uMin;
        public float VMin = vMin;
        public float UMax = uMax;
        public float VMax = vMax;
    }

    // On 64-bit Windows, VREvent_Data_t is 8-byte aligned because the native
    // union includes uint64 members. The payload therefore starts at byte 16,
    // and the complete VREvent_t is 64 bytes. Using a sequential managed union
    // containing only the mouse/scroll members incorrectly produces 60 bytes;
    // SteamVR then rejects PollNextOverlayEvent without returning any events.
    [StructLayout(LayoutKind.Explicit, Size = VrEventSize)]
    private struct VrEvent
    {
        [FieldOffset(0)]
        public uint EventType;

        [FieldOffset(4)]
        public uint TrackedDeviceIndex;

        [FieldOffset(8)]
        public float EventAgeSeconds;

        [FieldOffset(16)]
        public VrEventData Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct VrEventData
    {
        [FieldOffset(0)]
        public VrEventMouse Mouse;

        [FieldOffset(0)]
        public VrEventScroll Scroll;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrEventMouse
    {
        public float X;
        public float Y;
        public uint Button;
        public uint CursorIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrEventScroll
    {
        public float XDelta;
        public float YDelta;
        public uint Unused;
        public float ViewportScale;
        public uint CursorIndex;
    }
}

internal enum OpenVrIntersectionOutcome
{
    OverlayHidden,
    NativeMiss,
    MappingRejected,
    Hit,
}

internal readonly record struct OpenVrIntersectionAttempt(
    OpenVrIntersectionOutcome Outcome,
    OverlayLocalPoint RawPoint)
{
    public bool HasRawPoint =>
        Outcome is OpenVrIntersectionOutcome.MappingRejected or OpenVrIntersectionOutcome.Hit;
}

internal readonly record struct OpenVrNativeIntersection(
    OverlayLocalPoint RawPoint,
    OpenVrVector3 Point,
    OpenVrVector3 Normal,
    float Distance);

internal readonly record struct OpenVrIntersectionMaskRectangleSpec(
    float X,
    float Y,
    float Width,
    float Height)
{
    public bool Contains(float x, float y) =>
        x >= X
        && x <= X + Width
        && y >= Y
        && y <= Y + Height;

    public bool ContainsRectangle(float x, float y, float width, float height) =>
        width >= 0
        && height >= 0
        && Contains(x, y)
        && Contains(x + width, y + height);
}

internal readonly record struct OpenVrOverlayInteractionConfiguration(
    float MouseScaleWidth,
    float MouseScaleHeight,
    OpenVrIntersectionMaskRectangleSpec IntersectionMask);

internal readonly record struct OpenVrOverlayMouseScaleCall(float Width, float Height);

internal readonly record struct OpenVrOverlayIntersectionMaskCall(
    OpenVrIntersectionMaskRectangleSpec Rectangle,
    uint PrimitiveCount,
    uint PrimitiveSize);

internal readonly record struct OpenVrEvent(
    uint EventType,
    float MouseX,
    float MouseY,
    uint MouseButton,
    float ScrollY)
{
    public const uint LeftMouseButton = 1;
    public const uint MouseMove = 300;
    public const uint MouseButtonDown = 301;
    public const uint MouseButtonUp = 302;
    public const uint ScrollDiscrete = 305;
    public const uint ScrollSmooth = 309;
    public const uint ImageLoaded = 508;
    public const uint ImageFailed = 517;
    public const uint OverlayClosed = 534;
}
