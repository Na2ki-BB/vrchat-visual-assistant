using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Settings;

/// <summary>
/// Non-secret, local preferences for the current Windows user.
/// API keys and captured/OCR/translated text must never be added here.
/// </summary>
internal sealed record VrcVaSettings(
    ResultPanelPlacement ResultPanel,
    VrcVaOnboardingSettings Onboarding,
    WristLauncherPlacement WristLauncher)
{
    public VrcVaSettings(
        ResultPanelPlacement resultPanel,
        VrcVaOnboardingSettings onboarding)
        : this(resultPanel, onboarding, WristLauncherPlacement.Default)
    {
    }

    public static VrcVaSettings Default => new(
        ResultPanelPlacement.Default,
        VrcVaOnboardingSettings.Default,
        WristLauncherPlacement.Default);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ResultPanel);
        ArgumentNullException.ThrowIfNull(Onboarding);
        ArgumentNullException.ThrowIfNull(WristLauncher);
        ResultPanel.Validate();
        WristLauncher.Validate();
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
