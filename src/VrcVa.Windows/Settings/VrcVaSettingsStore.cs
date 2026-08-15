using System.IO;
using System.Text.Json;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Settings;

internal sealed class VrcVaSettingsStore
{
    private const int CurrentVersion = 5;
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

    public VrcVaSettings Load()
    {
        if (!File.Exists(_path))
        {
            return VrcVaSettings.Default;
        }

        string json = File.ReadAllText(_path);
        int version;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Version", out JsonElement versionElement)
                || !versionElement.TryGetInt32(out version))
            {
                throw new InvalidDataException("The VRCVA settings file has no valid version.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The VRCVA settings file is invalid JSON.", exception);
        }

        return version switch
        {
            1 => LoadVersion1(json),
            2 => LoadVersion2(json),
            3 => LoadVersion3(json),
            4 => LoadVersion4(json),
            CurrentVersion => LoadVersion5(json),
            > CurrentVersion => throw new InvalidDataException(
                $"The VRCVA settings file version {version} is newer than this application supports."),
            _ => throw new InvalidDataException(
                $"The VRCVA settings file version {version} is unsupported."),
        };
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
                new Version5StoredSettings(
                    CurrentVersion,
                    settings.ResultPanel,
                    settings.Onboarding,
                    Version4WristLauncherPlacement.From(settings.WristLauncher)),
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
