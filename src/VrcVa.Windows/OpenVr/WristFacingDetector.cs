namespace VrcVa.Windows.OpenVr;

internal readonly record struct WristFacingSample(
    bool EntersFacingCone,
    bool RemainsInFacingCone,
    float Alignment);

internal static class WristFacingDetector
{
    private const float EnterAlignment = 0.64f;
    private const float ExitAlignment = 0.42f;

    public static WristFacingSample Evaluate(
        OpenVrInputInterop.OpenVrPose leftPose,
        OpenVrInputInterop.OpenVrPose hmdPose,
        ResultPanelTransform launcherTransform)
    {
        (float x, float y, float z) = TransformPoint(
            leftPose,
            launcherTransform.M3,
            launcherTransform.M7,
            launcherTransform.M11);
        (float nx, float ny, float nz) = TransformDirection(
            leftPose,
            -launcherTransform.M2,
            -launcherTransform.M6,
            -launcherTransform.M10);
        float toHeadX = hmdPose.M3 - x;
        float toHeadY = hmdPose.M7 - y;
        float toHeadZ = hmdPose.M11 - z;
        float normalLength = MathF.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        float headLength = MathF.Sqrt(
            (toHeadX * toHeadX) + (toHeadY * toHeadY) + (toHeadZ * toHeadZ));
        // OpenVR overlays are visible from both sides on the supported runtime.
        // Treat this as alignment with the panel plane rather than with one
        // signed surface normal, while keeping invalid geometry fail-closed.
        float alignment = normalLength <= float.Epsilon || headLength <= float.Epsilon
            ? -1
            : MathF.Abs(
                ((nx * toHeadX) + (ny * toHeadY) + (nz * toHeadZ))
                / (normalLength * headLength));
        return new WristFacingSample(
            alignment >= EnterAlignment,
            alignment >= ExitAlignment,
            alignment);
    }

    private static (float X, float Y, float Z) TransformPoint(
        OpenVrInputInterop.OpenVrPose pose,
        float x,
        float y,
        float z) =>
        (
            (pose.M0 * x) + (pose.M1 * y) + (pose.M2 * z) + pose.M3,
            (pose.M4 * x) + (pose.M5 * y) + (pose.M6 * z) + pose.M7,
            (pose.M8 * x) + (pose.M9 * y) + (pose.M10 * z) + pose.M11);

    private static (float X, float Y, float Z) TransformDirection(
        OpenVrInputInterop.OpenVrPose pose,
        float x,
        float y,
        float z) =>
        (
            (pose.M0 * x) + (pose.M1 * y) + (pose.M2 * z),
            (pose.M4 * x) + (pose.M5 * y) + (pose.M6 * z),
            (pose.M8 * x) + (pose.M9 * y) + (pose.M10 * z));
}
