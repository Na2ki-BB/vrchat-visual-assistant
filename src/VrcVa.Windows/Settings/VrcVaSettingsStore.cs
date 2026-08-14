using System.IO;
using System.Text.Json;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Settings;

internal sealed class VrcVaSettingsStore
{
    private const int CurrentVersion = 2;
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
            CurrentVersion => LoadVersion2(json),
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
                new StoredSettings(CurrentVersion, settings.ResultPanel, settings.Onboarding),
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

        VrcVaSettings migrated = new(stored.ResultPanel, VrcVaOnboardingSettings.Default);
        migrated.Validate();
        return migrated;
    }

    private static VrcVaSettings LoadVersion2(string json)
    {
        StoredSettings? stored = Deserialize<StoredSettings>(json);
        if (stored?.ResultPanel is null || stored.Onboarding is null)
        {
            throw new InvalidDataException("The VRCVA version 2 settings file is invalid.");
        }

        VrcVaSettings settings = new(stored.ResultPanel, stored.Onboarding);
        settings.Validate();
        return settings;
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

    private sealed record StoredSettings(
        int Version,
        ResultPanelPlacement? ResultPanel,
        VrcVaOnboardingSettings? Onboarding);
}
