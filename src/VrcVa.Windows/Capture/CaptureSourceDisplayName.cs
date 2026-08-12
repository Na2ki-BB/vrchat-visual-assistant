namespace VrcVa.Windows.Capture;

internal static class CaptureSourceDisplayName
{
    public static string Get(string sourceKind) => sourceKind switch
    {
        "openvr-eye-mirror-left" => "SteamVRアイミラー（左眼）",
        "openvr-eye-mirror-right" => "SteamVRアイミラー（右眼）",
        "windows-graphics-capture-client-area" => "VRChatウィンドウ（フォールバック）",
        "explicit-image-file" => "指定画像",
        _ => "不明",
    };
}
