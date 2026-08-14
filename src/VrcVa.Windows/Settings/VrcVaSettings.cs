using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Settings;

/// <summary>
/// Non-secret, local preferences for the current Windows user.
/// API keys and captured/OCR/translated text must never be added here.
/// </summary>
internal sealed record VrcVaSettings(
    ResultPanelPlacement ResultPanel,
    VrcVaOnboardingSettings Onboarding)
{
    public static VrcVaSettings Default => new(
        ResultPanelPlacement.Default,
        VrcVaOnboardingSettings.Default);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ResultPanel);
        ArgumentNullException.ThrowIfNull(Onboarding);
        ResultPanel.Validate();
    }
}

/// <summary>
/// Records only whether the local setup was completed and whether the user asked
/// SteamVR to launch VRCVA. The actual SteamVR registration is checked separately.
/// </summary>
internal sealed record VrcVaOnboardingSettings(
    bool IsCompleted,
    bool SteamVrAutoLaunchEnabled)
{
    public static VrcVaOnboardingSettings Default => new(
        IsCompleted: false,
        SteamVrAutoLaunchEnabled: false);
}
