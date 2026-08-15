using System.IO;
using System.Runtime.InteropServices;

namespace VrcVa.Windows.OpenVr;

internal sealed class OpenVrInputInterop : IDisposable
{
    internal const string ActionSetPath = "/actions/vrcva";
    internal const string SelectActionPath = "/actions/vrcva/in/select";
    internal const string PointerPoseActionPath = "/actions/vrcva/in/pointer_pose";
    internal const string RightHandPath = "/user/hand/right";

    private const string InputInterface = "FnTable:IVRInput_010";
    private const string SystemInterface = "FnTable:IVRSystem_026";
    private const ulong InvalidInputValueHandle = 0;
    private const uint InvalidTrackedDeviceIndex = uint.MaxValue;
    private const int MaximumTrackedDeviceCount = 64;

    private readonly OpenVrRuntime _runtime;
    private readonly UpdateActionStateDelegate _updateActionState;
    private readonly GetDigitalActionDataDelegate _getDigitalActionData;
    private readonly GetPoseActionDataRelativeToNowDelegate _getPoseActionData;
    private readonly GetDeviceToAbsoluteTrackingPoseDelegate _getTrackedPoses;
    private readonly GetTrackedDeviceIndexForControllerRoleDelegate _getControllerRole;
    private readonly TrackedDevicePose[] _trackedPoses = new TrackedDevicePose[MaximumTrackedDeviceCount];
    private readonly ulong _actionSet;
    private readonly ulong _selectAction;
    private readonly ulong _pointerPoseAction;
    private readonly ulong _rightHand;
    private bool _disposed;

    private OpenVrInputInterop(
        OpenVrRuntime runtime,
        IntPtr inputFunctionTable,
        IntPtr systemFunctionTable,
        string manifestPath)
    {
        ValidateAbi();
        _runtime = runtime;

        SetActionManifestPathDelegate setActionManifestPath = OpenVrRuntime.GetFunction<SetActionManifestPathDelegate>(
            inputFunctionTable,
            InputSlot.SetActionManifestPath);
        GetActionSetHandleDelegate getActionSetHandle = OpenVrRuntime.GetFunction<GetActionSetHandleDelegate>(
            inputFunctionTable,
            InputSlot.GetActionSetHandle);
        GetActionHandleDelegate getActionHandle = OpenVrRuntime.GetFunction<GetActionHandleDelegate>(
            inputFunctionTable,
            InputSlot.GetActionHandle);
        GetInputSourceHandleDelegate getInputSourceHandle = OpenVrRuntime.GetFunction<GetInputSourceHandleDelegate>(
            inputFunctionTable,
            InputSlot.GetInputSourceHandle);
        _updateActionState = OpenVrRuntime.GetFunction<UpdateActionStateDelegate>(
            inputFunctionTable,
            InputSlot.UpdateActionState);
        _getDigitalActionData = OpenVrRuntime.GetFunction<GetDigitalActionDataDelegate>(
            inputFunctionTable,
            InputSlot.GetDigitalActionData);
        _getPoseActionData = OpenVrRuntime.GetFunction<GetPoseActionDataRelativeToNowDelegate>(
            inputFunctionTable,
            InputSlot.GetPoseActionDataRelativeToNow);
        _getTrackedPoses = OpenVrRuntime.GetFunction<GetDeviceToAbsoluteTrackingPoseDelegate>(
            systemFunctionTable,
            SystemSlot.GetDeviceToAbsoluteTrackingPose);
        _getControllerRole = OpenVrRuntime.GetFunction<GetTrackedDeviceIndexForControllerRoleDelegate>(
            systemFunctionTable,
            SystemSlot.GetTrackedDeviceIndexForControllerRole);

        EnsureSuccess(setActionManifestPath(manifestPath), "SetActionManifestPath");
        _actionSet = GetHandle(ActionSetPath, getActionSetHandle);
        _selectAction = GetHandle(SelectActionPath, getActionHandle);
        _pointerPoseAction = GetHandle(PointerPoseActionPath, getActionHandle);
        _rightHand = GetHandle(RightHandPath, getInputSourceHandle);
    }

    public static bool TryCreate(out OpenVrInputInterop? input, string? applicationBaseDirectory = null)
    {
        input = null;
        string manifestPath = ResolveActionManifestPath(applicationBaseDirectory);
        if (!OpenVrRuntime.TryAcquire(out OpenVrRuntime? runtime))
        {
            return false;
        }

        try
        {
            IntPtr inputTable = runtime!.GetInterface(InputInterface);
            IntPtr systemTable = runtime.GetInterface(SystemInterface);
            input = new OpenVrInputInterop(runtime, inputTable, systemTable, manifestPath);
            return true;
        }
        catch
        {
            runtime?.Dispose();
            throw;
        }
    }

    public OpenVrPointerInputSample Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        VrActiveActionSet activeSet = new()
        {
            ActionSet = _actionSet,
            RestrictedToDevice = InvalidInputValueHandle,
            SecondaryActionSet = 0,
            Padding = 0,
            Priority = 0,
        };
        EnsureSuccess(
            _updateActionState(ref activeSet, (uint)Marshal.SizeOf<VrActiveActionSet>(), 1),
            "UpdateActionState");

        InputDigitalActionData digital = default;
        EnsureSuccess(
            _getDigitalActionData(
                _selectAction,
                ref digital,
                (uint)Marshal.SizeOf<InputDigitalActionData>(),
                _rightHand),
            "GetDigitalActionData");

        InputPoseActionData pose = default;
        EnsureSuccess(
            _getPoseActionData(
                _pointerPoseAction,
                TrackingUniverseOrigin.Standing,
                0,
                ref pose,
                (uint)Marshal.SizeOf<InputPoseActionData>(),
                _rightHand),
            "GetPoseActionDataRelativeToNow");

        _getTrackedPoses(
            TrackingUniverseOrigin.Standing,
            0,
            _trackedPoses,
            MaximumTrackedDeviceCount);
        TrackedDevicePose hmdPose = _trackedPoses[0];
        uint leftDevice = _getControllerRole(ETrackedControllerRole.LeftHand);
        TrackedDevicePose leftPose = leftDevice < (uint)_trackedPoses.Length
            ? _trackedPoses[(int)leftDevice]
            : default;

        return new OpenVrPointerInputSample(
            digital.Active,
            digital.State,
            digital.Changed,
            pose.Active,
            pose.Pose.PoseIsValid,
            pose.Pose.DeviceIsConnected,
            CreatePose(pose.Pose.DeviceToAbsoluteTracking),
            hmdPose.PoseIsValid && hmdPose.DeviceIsConnected,
            CreatePose(hmdPose.DeviceToAbsoluteTracking),
            leftDevice != InvalidTrackedDeviceIndex
                && leftPose.PoseIsValid
                && leftPose.DeviceIsConnected,
            CreatePose(leftPose.DeviceToAbsoluteTracking));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Dispose();
    }

    internal static string ResolveActionManifestPath(string? applicationBaseDirectory = null)
    {
        string baseDirectory = string.IsNullOrWhiteSpace(applicationBaseDirectory)
            ? AppContext.BaseDirectory
            : Path.GetFullPath(applicationBaseDirectory);
        string manifestPath = Path.Combine(baseDirectory, "OpenVr", "Assets", "actions.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "The SteamVR Input action manifest was not found.",
                manifestPath);
        }

        return Path.GetFullPath(manifestPath);
    }

    internal static void ValidateAbi()
    {
        AssertSize<VrActiveActionSet>(32);
        AssertSize<InputDigitalActionData>(24);
        AssertSize<TrackedDevicePose>(80);
        AssertSize<InputPoseActionData>(96);
        AssertSize<HmdMatrix34>(48);
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

    private static ulong GetHandle<TDelegate>(string path, TDelegate getHandle)
        where TDelegate : Delegate
    {
        ulong handle = 0;
        EvrInputError error = getHandle switch
        {
            GetActionSetHandleDelegate actionSet => actionSet(path, ref handle),
            GetActionHandleDelegate action => action(path, ref handle),
            GetInputSourceHandleDelegate source => source(path, ref handle),
            _ => throw new ArgumentOutOfRangeException(nameof(getHandle)),
        };
        EnsureSuccess(error, $"GetHandle({path})");
        if (handle == 0)
        {
            throw new InvalidOperationException($"OpenVR returned an invalid handle for {path}.");
        }

        return handle;
    }

    private static void EnsureSuccess(EvrInputError error, string operation)
    {
        if (error != EvrInputError.None)
        {
            throw new InvalidOperationException(
                $"OpenVR input operation {operation} failed with code {(int)error}.");
        }
    }

    private static OpenVrPose CreatePose(HmdMatrix34 value) => new(
        value.M0,
        value.M1,
        value.M2,
        value.M3,
        value.M4,
        value.M5,
        value.M6,
        value.M7,
        value.M8,
        value.M9,
        value.M10,
        value.M11);

    private static class InputSlot
    {
        public const int SetActionManifestPath = 0;
        public const int GetActionSetHandle = 1;
        public const int GetActionHandle = 2;
        public const int GetInputSourceHandle = 3;
        public const int UpdateActionState = 4;
        public const int GetDigitalActionData = 5;
        public const int GetPoseActionDataRelativeToNow = 7;
    }

    private static class SystemSlot
    {
        // IVRSystem_026 adds ComputeDistortionSet before the older methods.
        public const int GetDeviceToAbsoluteTrackingPose = 12;
        public const int GetTrackedDeviceIndexForControllerRole = 18;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate EvrInputError SetActionManifestPathDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string actionManifestPath);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate EvrInputError GetActionSetHandleDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string actionSetName,
        ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate EvrInputError GetActionHandleDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string actionName,
        ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate EvrInputError GetInputSourceHandleDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string inputSourcePath,
        ref ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrInputError UpdateActionStateDelegate(
        ref VrActiveActionSet activeSets,
        uint activeActionSetSize,
        uint activeActionSetCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrInputError GetDigitalActionDataDelegate(
        ulong action,
        ref InputDigitalActionData actionData,
        uint actionDataSize,
        ulong restrictToDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate EvrInputError GetPoseActionDataRelativeToNowDelegate(
        ulong action,
        TrackingUniverseOrigin origin,
        float predictedSecondsFromNow,
        ref InputPoseActionData actionData,
        uint actionDataSize,
        ulong restrictToDevice);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetDeviceToAbsoluteTrackingPoseDelegate(
        TrackingUniverseOrigin origin,
        float predictedSecondsFromNow,
        [Out] TrackedDevicePose[] trackedDevicePoses,
        uint trackedDevicePoseCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetTrackedDeviceIndexForControllerRoleDelegate(
        ETrackedControllerRole role);

    private enum EvrInputError
    {
        None = 0,
    }

    private enum TrackingUniverseOrigin
    {
        Standing = 1,
    }

    private enum ETrackedControllerRole
    {
        LeftHand = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VrActiveActionSet
    {
        public ulong ActionSet;
        public ulong RestrictedToDevice;
        public ulong SecondaryActionSet;
        public uint Padding;
        public int Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputDigitalActionData
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool Active;

        public ulong ActiveOrigin;

        [MarshalAs(UnmanagedType.I1)]
        public bool State;

        [MarshalAs(UnmanagedType.I1)]
        public bool Changed;

        public float UpdateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputPoseActionData
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool Active;

        public ulong ActiveOrigin;
        public TrackedDevicePose Pose;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackedDevicePose
    {
        public HmdMatrix34 DeviceToAbsoluteTracking;
        public HmdVector3 Velocity;
        public HmdVector3 AngularVelocity;
        public int TrackingResult;

        [MarshalAs(UnmanagedType.I1)]
        public bool PoseIsValid;

        [MarshalAs(UnmanagedType.I1)]
        public bool DeviceIsConnected;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HmdVector3
    {
        public float X;
        public float Y;
        public float Z;
    }

    internal readonly record struct OpenVrPose(
        float M0,
        float M1,
        float M2,
        float M3,
        float M4,
        float M5,
        float M6,
        float M7,
        float M8,
        float M9,
        float M10,
        float M11);
}

internal readonly record struct OpenVrPointerInputSample(
    bool SelectActive,
    bool SelectPressed,
    bool SelectChanged,
    bool PoseActive,
    bool PoseValid,
    bool DeviceConnected,
    OpenVrInputInterop.OpenVrPose Pose,
    bool HmdPoseValid,
    OpenVrInputInterop.OpenVrPose HmdPose,
    bool LeftPoseValid,
    OpenVrInputInterop.OpenVrPose LeftPose)
{
    public bool SelectPressedThisFrame => SelectActive && SelectChanged && SelectPressed;
}
