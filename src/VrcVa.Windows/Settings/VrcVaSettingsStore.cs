using System.IO;
using System.Text.Json;
using VrcVa.Core;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Settings;

internal sealed class VrcVaSettingsStore
{
    private const int CurrentVersion = 7;
    private const double QuaternionNormalizationTolerance = 1e-12;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public VrcVaSettingsStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcVa",
            "settings.json");
    }

    public string Path => _path;

    public VrcVaSettings Load() => Load(allowMissing: true, out _);

    public VrcVaSettings Load(bool allowMissing, out bool hasStoredSettings)
    {
        hasStoredSettings = false;
        string json;
        try
        {
            json = File.ReadAllText(_path);
            hasStoredSettings = true;
        }
        catch (Exception exception) when (
            allowMissing && exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // File.Exists also returns false for directories and access failures.
            // Only an actually absent initial file may opt into defaults.
            return VrcVaSettings.Default;
        }
        int version;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out version))
            {
                throw new InvalidDataException("The VRCVA settings file has no valid version.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The VRCVA settings file is invalid JSON.", exception);
        }

        try
        {
            return version switch
            {
                1 => LoadVersion1(json),
                2 => LoadVersion2(json),
                3 => LoadVersion3(json),
                4 => LoadVersion4(json),
                5 => LoadVersion5(json),
                6 => LoadVersion6(json),
                CurrentVersion => LoadVersion7(json),
                > CurrentVersion => throw new InvalidDataException(
                    $"The VRCVA settings file version {version} is newer than this application supports."),
                _ => throw new InvalidDataException(
                    $"The VRCVA settings file version {version} is unsupported."),
            };
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The VRCVA version {version} settings file contains invalid preferences.",
                exception);
        }
    }

    public void Save(VrcVaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The VRCVA settings path has no directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(
                new Version7StoredSettings(
                    CurrentVersion,
                    Version6ResultPanelPlacement.From(settings.ResultPanel),
                    Version6ResultPanelPlacement.From(settings.VoicePanel),
                    Version6OnboardingSettings.From(settings.Onboarding),
                    Version4WristLauncherPlacement.From(settings.WristLauncher),
                    Version6VoiceInputOptions.From(settings.VoiceInput),
                    Version6FeatureUsageLimits.From(settings.UsageLimits)),
                JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static VrcVaSettings LoadVersion1(string json)
    {
        LegacyStoredSettings? stored = Deserialize<LegacyStoredSettings>(json);
        if (stored?.ResultPanel is null)
        {
            throw new InvalidDataException("The VRCVA version 1 settings file is invalid.");
        }

        WristLauncherPlacement wristLauncher = WristLauncherPlacement.Default;
        VrcVaSettings migrated = new(
            AlignLegacyLeftHandResult(stored.ResultPanel, wristLauncher),
            VrcVaOnboardingSettings.Default,
            wristLauncher);
        migrated.Validate();
        return migrated;
    }

    private static VrcVaSettings LoadVersion2(string json)
    {
        Version2StoredSettings? stored = Deserialize<Version2StoredSettings>(json);
        if (stored?.ResultPanel is null || stored.Onboarding is null)
        {
            throw new InvalidDataException("The VRCVA version 2 settings file is invalid.");
        }

        WristLauncherPlacement wristLauncher = WristLauncherPlacement.Default;
        VrcVaSettings settings = new(
            AlignLegacyLeftHandResult(stored.ResultPanel, wristLauncher),
            stored.Onboarding,
            wristLauncher);
        settings.Validate();
        return settings;
    }

    private static VrcVaSettings LoadVersion3(string json)
    {
        Version3StoredSettings? stored = Deserialize<Version3StoredSettings>(json);
        if (stored?.ResultPanel is null
            || stored.Onboarding is null
            || stored.WristLauncher is null)
        {
            throw new InvalidDataException("The VRCVA version 3 settings file is invalid.");
        }

        WristLauncherPlacement wristLauncher = stored.WristLauncher.ToPlacement();
        VrcVaSettings settings = new(
            AlignLegacyLeftHandResult(stored.ResultPanel, wristLauncher),
            stored.Onboarding,
            wristLauncher);
        settings.Validate();
        return settings;
    }

    private static VrcVaSettings LoadVersion4(string json)
    {
        Version4StoredSettings? stored = Deserialize<Version4StoredSettings>(json);
        if (stored?.ResultPanel is null
            || stored.Onboarding is null
            || stored.WristLauncher is null)
        {
            throw new InvalidDataException("The VRCVA version 4 settings file is invalid.");
        }

        WristLauncherPlacement wristLauncher = stored.WristLauncher.ToPlacement();
        VrcVaSettings settings = new(
            AlignLegacyLeftHandResult(stored.ResultPanel, wristLauncher),
            stored.Onboarding,
            wristLauncher);
        settings.Validate();
        return settings;
    }

    private static VrcVaSettings LoadVersion5(string json)
    {
        Version5StoredSettings? stored = Deserialize<Version5StoredSettings>(json);
        if (stored?.ResultPanel is null
            || stored.Onboarding is null
            || stored.WristLauncher is null)
        {
            throw new InvalidDataException("The VRCVA version 5 settings file is invalid.");
        }

        VrcVaSettings settings = new(
            stored.ResultPanel,
            stored.Onboarding,
            stored.WristLauncher.ToPlacement(version: 5));
        settings.Validate();
        return settings;
    }

    private static VrcVaSettings LoadVersion6(string json)
    {
        Version6StoredSettings stored = Deserialize<Version6StoredSettings>(json);
        if (stored.ResultPanel is null
            || stored.Onboarding is null
            || stored.WristLauncher is null
            || stored.VoiceInput is null
            || stored.UsageLimits is null)
        {
            throw new InvalidDataException("The VRCVA version 6 settings file is invalid.");
        }

        VrcVaSettings settings = new(
            stored.ResultPanel.ToPlacement(),
            stored.Onboarding.ToSettings(),
            stored.WristLauncher.ToPlacement(version: 6))
        {
            VoicePanel = stored.ResultPanel.ToPlacement(),
            VoiceInput = stored.VoiceInput.ToOptions(),
            UsageLimits = stored.UsageLimits.ToLimits(),
        };
        settings.Validate();
        return settings;
    }

    private static VrcVaSettings LoadVersion7(string json)
    {
        Version7StoredSettings stored = Deserialize<Version7StoredSettings>(json);
        if (stored.ResultPanel is null
            || stored.VoicePanel is null
            || stored.Onboarding is null
            || stored.WristLauncher is null
            || stored.VoiceInput is null
            || stored.UsageLimits is null)
        {
            throw new InvalidDataException("The VRCVA version 7 settings file is invalid.");
        }

        VrcVaSettings settings = new(
            stored.ResultPanel.ToPlacement(version: 7, property: "ResultPanel"),
            stored.Onboarding.ToSettings(),
            stored.WristLauncher.ToPlacement(version: 7))
        {
            VoicePanel = stored.VoicePanel.ToPlacement(version: 7, property: "VoicePanel"),
            VoiceInput = stored.VoiceInput.ToOptions(),
            UsageLimits = stored.UsageLimits.ToLimits(),
        };
        settings.Validate();
        return settings;
    }

    private static ResultPanelPlacement AlignLegacyLeftHandResult(
        ResultPanelPlacement resultPanel,
        WristLauncherPlacement wristLauncher)
    {
        resultPanel.Validate();
        return resultPanel.Anchor == ResultPanelAnchor.LeftHand
            ? ResultPanelPlacement.CreateAlignedToWristLauncher(
                wristLauncher,
                resultPanel.WidthMeters)
            : resultPanel;
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidDataException("The VRCVA settings file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The VRCVA settings file is invalid JSON.", exception);
        }
    }

    private sealed record LegacyStoredSettings(int Version, ResultPanelPlacement? ResultPanel);

    private sealed record Version2StoredSettings(
        int Version,
        ResultPanelPlacement? ResultPanel,
        VrcVaOnboardingSettings? Onboarding);

    private sealed record Version3StoredSettings(
        int Version,
        ResultPanelPlacement? ResultPanel,
        VrcVaOnboardingSettings? Onboarding,
        Version3WristLauncherPlacement? WristLauncher);

    private sealed record Version3WristLauncherPlacement(
        double? X,
        double? Y,
        double? Z,
        double? PitchDegrees,
        double? YawDegrees,
        double? RollDegrees,
        double? MenuWidthMeters)
    {
        public WristLauncherPlacement ToPlacement()
        {
            double pitchDegrees = GetRequired(
                PitchDegrees,
                nameof(PitchDegrees),
                version: 3);
            double yawDegrees = GetRequired(
                YawDegrees,
                nameof(YawDegrees),
                version: 3);
            double rollDegrees = GetRequired(
                RollDegrees,
                nameof(RollDegrees),
                version: 3);
            ValidateVersion3Angle(pitchDegrees, nameof(PitchDegrees));
            ValidateVersion3Angle(yawDegrees, nameof(YawDegrees));
            ValidateVersion3Angle(rollDegrees, nameof(RollDegrees));

            WristLauncherPlacement placement = new(
                GetRequired(X, nameof(X), version: 3),
                GetRequired(Y, nameof(Y), version: 3),
                GetRequired(Z, nameof(Z), version: 3),
                WristLauncherRotation.FromEulerDegrees(
                    pitchDegrees,
                    yawDegrees,
                    rollDegrees),
                GetRequired(MenuWidthMeters, nameof(MenuWidthMeters), version: 3));
            placement.Validate();
            return placement;
        }
    }

    private sealed record Version4StoredSettings(
        int Version,
        ResultPanelPlacement? ResultPanel,
        VrcVaOnboardingSettings? Onboarding,
        Version4WristLauncherPlacement? WristLauncher);

    private sealed record Version5StoredSettings(
        int Version,
        ResultPanelPlacement? ResultPanel,
        VrcVaOnboardingSettings? Onboarding,
        Version4WristLauncherPlacement? WristLauncher);

    private sealed record Version6StoredSettings(
        int Version,
        Version6ResultPanelPlacement? ResultPanel,
        Version6OnboardingSettings? Onboarding,
        Version4WristLauncherPlacement? WristLauncher,
        Version6VoiceInputOptions? VoiceInput,
        Version6FeatureUsageLimits? UsageLimits);

    private sealed record Version7StoredSettings(
        int Version,
        Version6ResultPanelPlacement? ResultPanel,
        Version6ResultPanelPlacement? VoicePanel,
        Version6OnboardingSettings? Onboarding,
        Version4WristLauncherPlacement? WristLauncher,
        Version6VoiceInputOptions? VoiceInput,
        Version6FeatureUsageLimits? UsageLimits);

    private sealed record Version6ResultPanelPlacement(
        ResultPanelAnchor? Anchor,
        double? X,
        double? Y,
        double? Z,
        double? PitchDegrees,
        double? YawDegrees,
        double? RollDegrees,
        double? WidthMeters)
    {
        public static Version6ResultPanelPlacement From(ResultPanelPlacement placement) => new(
            placement.Anchor,
            placement.X,
            placement.Y,
            placement.Z,
            placement.PitchDegrees,
            placement.YawDegrees,
            placement.RollDegrees,
            placement.WidthMeters);

        public ResultPanelPlacement ToPlacement(int version = 6, string property = "ResultPanel") => new(
            GetStoredRequired(Anchor, $"{property}.{nameof(Anchor)}", version),
            GetStoredRequired(X, $"{property}.{nameof(X)}", version),
            GetStoredRequired(Y, $"{property}.{nameof(Y)}", version),
            GetStoredRequired(Z, $"{property}.{nameof(Z)}", version),
            GetStoredRequired(PitchDegrees, $"{property}.{nameof(PitchDegrees)}", version),
            GetStoredRequired(YawDegrees, $"{property}.{nameof(YawDegrees)}", version),
            GetStoredRequired(RollDegrees, $"{property}.{nameof(RollDegrees)}", version),
            GetStoredRequired(WidthMeters, $"{property}.{nameof(WidthMeters)}", version));
    }

    private sealed record Version6OnboardingSettings(
        bool? IsCompleted,
        bool? SteamVrAutoLaunchEnabled)
    {
        public static Version6OnboardingSettings From(VrcVaOnboardingSettings settings) => new(
            settings.IsCompleted,
            settings.SteamVrAutoLaunchEnabled);

        public VrcVaOnboardingSettings ToSettings() => new(
            GetVersion6Required(IsCompleted, $"Onboarding.{nameof(IsCompleted)}"),
            GetVersion6Required(SteamVrAutoLaunchEnabled, $"Onboarding.{nameof(SteamVrAutoLaunchEnabled)}"));
    }

    private sealed record Version6VoiceInputOptions(
        bool? IsEnabled,
        int? MaximumRecordingSeconds,
        int? FailedAudioRetentionSeconds)
    {
        public static Version6VoiceInputOptions From(VoiceInputOptions options) => new(
            options.IsEnabled,
            options.MaximumRecordingSeconds,
            options.FailedAudioRetentionSeconds);

        public VoiceInputOptions ToOptions() => new()
        {
            IsEnabled = GetVersion6Required(IsEnabled, $"VoiceInput.{nameof(IsEnabled)}"),
            MaximumRecordingSeconds = GetVersion6Required(
                MaximumRecordingSeconds,
                $"VoiceInput.{nameof(MaximumRecordingSeconds)}"),
            FailedAudioRetentionSeconds = GetVersion6Required(
                FailedAudioRetentionSeconds,
                $"VoiceInput.{nameof(FailedAudioRetentionSeconds)}"),
        };
    }

    private sealed record Version6FeatureUsageLimits(
        int? VoiceSeconds,
        int? VoiceRequests,
        int? TranslationRequests,
        int? SearchInterpretationRequests)
    {
        public static Version6FeatureUsageLimits From(FeatureUsageLimits limits) => new(
            limits.VoiceSeconds,
            limits.VoiceRequests,
            limits.TranslationRequests,
            limits.SearchInterpretationRequests);

        public FeatureUsageLimits ToLimits() => new()
        {
            VoiceSeconds = GetVersion6Required(VoiceSeconds, $"UsageLimits.{nameof(VoiceSeconds)}"),
            VoiceRequests = GetVersion6Required(VoiceRequests, $"UsageLimits.{nameof(VoiceRequests)}"),
            TranslationRequests = GetVersion6Required(
                TranslationRequests,
                $"UsageLimits.{nameof(TranslationRequests)}"),
            SearchInterpretationRequests = GetVersion6Required(
                SearchInterpretationRequests,
                $"UsageLimits.{nameof(SearchInterpretationRequests)}"),
        };
    }

    private sealed record Version4WristLauncherPlacement(
        double? X,
        double? Y,
        double? Z,
        Version4WristLauncherRotation? Rotation,
        double? MenuWidthMeters)
    {
        public static Version4WristLauncherPlacement From(WristLauncherPlacement placement)
        {
            placement.Validate();
            return new Version4WristLauncherPlacement(
                placement.X,
                placement.Y,
                placement.Z,
                Version4WristLauncherRotation.From(placement.Rotation),
                placement.MenuWidthMeters);
        }

        public WristLauncherPlacement ToPlacement(int version = 4)
        {
            if (Rotation is null)
            {
                throw new InvalidDataException(
                    $"The VRCVA version {version} wrist launcher rotation is missing.");
            }

            WristLauncherPlacement placement = new(
                GetRequired(X, nameof(X), version),
                GetRequired(Y, nameof(Y), version),
                GetRequired(Z, nameof(Z), version),
                Rotation.ToRotation(version),
                GetRequired(MenuWidthMeters, nameof(MenuWidthMeters), version));
            placement.Validate();
            return placement;
        }
    }

    private sealed record Version4WristLauncherRotation(
        double? X,
        double? Y,
        double? Z,
        double? W)
    {
        public static Version4WristLauncherRotation From(WristLauncherRotation rotation)
        {
            rotation.Validate();
            return new Version4WristLauncherRotation(
                rotation.X,
                rotation.Y,
                rotation.Z,
                rotation.W);
        }

        public WristLauncherRotation ToRotation(int version = 4)
        {
            double x = GetRequired(X, nameof(X), version);
            double y = GetRequired(Y, nameof(Y), version);
            double z = GetRequired(Z, nameof(Z), version);
            double w = GetRequired(W, nameof(W), version);
            double normSquared = (x * x) + (y * y) + (z * z) + (w * w);
            if (!double.IsFinite(normSquared)
                || Math.Abs(normSquared - 1) > QuaternionNormalizationTolerance)
            {
                throw new InvalidDataException(
                    $"The VRCVA version {version} wrist launcher rotation must be normalized.");
            }

            WristLauncherRotation rotation = new(x, y, z, w);
            rotation.Validate();
            return rotation;
        }
    }

    private static double GetRequired(double? value, string name, int version)
    {
        if (value is not double required || !double.IsFinite(required))
        {
            throw new InvalidDataException(
                $"The VRCVA version {version} wrist launcher {name} is invalid.");
        }

        return required;
    }

    private static T GetVersion6Required<T>(T? value, string name)
        where T : struct
    {
        return GetStoredRequired(value, name, version: 6);
    }

    private static T GetStoredRequired<T>(T? value, string name, int version)
        where T : struct => value ?? throw new InvalidDataException(
            $"The VRCVA version {version} settings {name} is missing or null.");

    private static void ValidateVersion3Angle(double value, string name)
    {
        if (value < ResultPanelPlacement.MinimumRotationDegrees
            || value > ResultPanelPlacement.MaximumRotationDegrees)
        {
            throw new InvalidDataException(
                $"The VRCVA version 3 wrist launcher {name} is invalid.");
        }
    }
}
