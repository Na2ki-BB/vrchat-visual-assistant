using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Capture;

internal sealed record OpenVrEyeCaptureOptions(OpenVrEye Eye)
{
    internal const string EnvironmentVariable = "VRCVA_OPENVR_EYE";

    public static OpenVrEyeCaptureOptions FromEnvironment(out string? warning)
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        return Parse(value, out warning);
    }

    internal static OpenVrEyeCaptureOptions Parse(string? value, out string? warning)
    {
        warning = null;
        if (string.IsNullOrEmpty(value)
            || value.Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenVrEyeCaptureOptions(OpenVrEye.Left);
        }

        if (value.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenVrEyeCaptureOptions(OpenVrEye.Right);
        }

        warning = $"{EnvironmentVariable} は left または right を指定してください。左眼を使用します。";
        return new OpenVrEyeCaptureOptions(OpenVrEye.Left);
    }
}
