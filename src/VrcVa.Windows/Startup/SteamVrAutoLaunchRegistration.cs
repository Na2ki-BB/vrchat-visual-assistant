using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Startup;

/// <summary>
/// Registers VRCVA with an already-running SteamVR instance. This class never starts SteamVR.
/// </summary>
internal sealed class SteamVrAutoLaunchRegistration
{
    internal const string ApplicationKey = "com.na2kibb.vrcva";
    internal const string AutoStartArgument = "--steamvr-autostart";
    internal const string ManifestFileName = "vrcva.vrmanifest";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly IOpenVrApplicationsFactory _applicationsFactory;
    private readonly string _manifestPath;
    private readonly Func<string?> _executablePathProvider;

    public SteamVrAutoLaunchRegistration()
        : this(
            new OpenVrApplicationsFactory(),
            GetDefaultManifestPath(),
            GetCurrentExecutablePath)
    {
    }

    internal SteamVrAutoLaunchRegistration(
        IOpenVrApplicationsFactory applicationsFactory,
        string manifestPath,
        Func<string?> executablePathProvider)
    {
        ArgumentNullException.ThrowIfNull(applicationsFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(executablePathProvider);

        _applicationsFactory = applicationsFactory;
        _manifestPath = Path.GetFullPath(manifestPath);
        _executablePathProvider = executablePathProvider;
    }

    public SteamVrAutoLaunchResult Apply(bool enabled)
    {
        try
        {
            OpenVrApplicationsConnectionResult connection =
                _applicationsFactory.TryCreate(out IOpenVrApplications? applications);
            if (connection == OpenVrApplicationsConnectionResult.Unavailable)
            {
                applications?.Dispose();
                return SteamVrAutoLaunchResult.Pending();
            }

            if (connection != OpenVrApplicationsConnectionResult.Connected || applications is null)
            {
                applications?.Dispose();
                return SteamVrAutoLaunchResult.Failed();
            }

            using (applications)
            {
                return enabled
                    ? Enable(applications)
                    : Disable(applications);
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return SteamVrAutoLaunchResult.Failed();
        }
    }

    public SteamVrAutoLaunchResult GetStatus()
    {
        try
        {
            OpenVrApplicationsConnectionResult connection =
                _applicationsFactory.TryCreate(out IOpenVrApplications? applications);
            if (connection == OpenVrApplicationsConnectionResult.Unavailable)
            {
                applications?.Dispose();
                return SteamVrAutoLaunchResult.Pending();
            }

            if (connection != OpenVrApplicationsConnectionResult.Connected || applications is null)
            {
                applications?.Dispose();
                return SteamVrAutoLaunchResult.Failed();
            }

            using (applications)
            {
                if (!applications.IsApplicationInstalled(ApplicationKey))
                {
                    return SteamVrAutoLaunchResult.Disabled(
                        isRegistered: false,
                        settingWasApplied: false);
                }

                return applications.GetApplicationAutoLaunch(ApplicationKey)
                    ? SteamVrAutoLaunchResult.Enabled(settingWasApplied: false)
                    : SteamVrAutoLaunchResult.Disabled(
                        isRegistered: true,
                        settingWasApplied: false);
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            return SteamVrAutoLaunchResult.Failed();
        }
    }

    internal static string CreateManifestJson(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullExecutablePath = Path.GetFullPath(executablePath);
        string? workingDirectory = Path.GetDirectoryName(fullExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException(
                "The executable path has no working directory.",
                nameof(executablePath));
        }

        string actionManifestPath = Path.Combine(
            workingDirectory,
            "OpenVr",
            "Assets",
            "actions.json");
        SteamVrManifest manifest = new(
            Source: "builtin",
            Applications:
            [
                new SteamVrManifestApplication(
                    AppKey: ApplicationKey,
                    LaunchType: "binary",
                    BinaryPathWindows: fullExecutablePath,
                    WorkingDirectory: workingDirectory,
                    Arguments: AutoStartArgument,
                    ActionManifestPath: Path.GetFullPath(actionManifestPath),
                    // OpenVR permits SetApplicationAutoLaunch only for applications
                    // whose IsDashboardOverlay application property is true.
                    IsDashboardOverlay: true,
                    Strings: new SteamVrManifestStrings(
                        English: new SteamVrManifestText(
                            Name: "VRCVA",
                            Description: "VRChat Visual Assistant"),
                        Japanese: new SteamVrManifestText(
                            Name: "VRCVA",
                            Description: "VRChat内の情報を支援するアシスタント")))
            ]);

        return JsonSerializer.Serialize(manifest, ManifestJsonOptions);
    }

    private SteamVrAutoLaunchResult Enable(IOpenVrApplications applications)
    {
        string? executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return SteamVrAutoLaunchResult.Failed();
        }

        WriteManifestAtomically(CreateManifestJson(executablePath));
        OpenVrApplicationError addError = applications.AddApplicationManifest(
            _manifestPath,
            temporary: false);
        if (addError is not OpenVrApplicationError.None
            and not OpenVrApplicationError.AppKeyAlreadyExists)
        {
            return SteamVrAutoLaunchResult.Failed(addError);
        }

        if (!applications.IsApplicationInstalled(ApplicationKey))
        {
            return SteamVrAutoLaunchResult.Pending(
                SteamVrAutoLaunchPendingReason.ManifestReloadRequired);
        }

        OpenVrApplicationError setError = applications.SetApplicationAutoLaunch(
            ApplicationKey,
            enabled: true);
        if (setError != OpenVrApplicationError.None)
        {
            return SteamVrAutoLaunchResult.Failed(setError);
        }

        return applications.GetApplicationAutoLaunch(ApplicationKey)
            ? SteamVrAutoLaunchResult.Enabled()
            : SteamVrAutoLaunchResult.Failed(OpenVrApplicationError.UnknownApplication);
    }

    private static SteamVrAutoLaunchResult Disable(IOpenVrApplications applications)
    {
        if (!applications.IsApplicationInstalled(ApplicationKey))
        {
            return SteamVrAutoLaunchResult.Disabled(isRegistered: false);
        }

        OpenVrApplicationError error = applications.SetApplicationAutoLaunch(
            ApplicationKey,
            enabled: false);
        if (error != OpenVrApplicationError.None)
        {
            return SteamVrAutoLaunchResult.Failed(error);
        }

        return applications.GetApplicationAutoLaunch(ApplicationKey)
            ? SteamVrAutoLaunchResult.Failed(OpenVrApplicationError.UnknownApplication)
            : SteamVrAutoLaunchResult.Disabled(isRegistered: true);
    }

    private void WriteManifestAtomically(string json)
    {
        string? directory = Path.GetDirectoryName(_manifestPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The SteamVR manifest path has no directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetDefaultManifestPath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data folder is unavailable.");
        }

        return Path.Combine(
            localApplicationData,
            "VRCVA",
            "SteamVR",
            ManifestFileName);
    }

    private static string? GetCurrentExecutablePath() =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;

    private static bool IsExpectedFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException;

    private sealed record SteamVrManifest(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("applications")] SteamVrManifestApplication[] Applications);

    private sealed record SteamVrManifestApplication(
        [property: JsonPropertyName("app_key")] string AppKey,
        [property: JsonPropertyName("launch_type")] string LaunchType,
        [property: JsonPropertyName("binary_path_windows")] string BinaryPathWindows,
        [property: JsonPropertyName("working_directory")] string WorkingDirectory,
        [property: JsonPropertyName("arguments")] string Arguments,
        [property: JsonPropertyName("action_manifest_path")] string ActionManifestPath,
        [property: JsonPropertyName("is_dashboard_overlay")] bool IsDashboardOverlay,
        [property: JsonPropertyName("strings")] SteamVrManifestStrings Strings);

    private sealed record SteamVrManifestStrings(
        [property: JsonPropertyName("en_us")] SteamVrManifestText English,
        [property: JsonPropertyName("ja_jp")] SteamVrManifestText Japanese);

    private sealed record SteamVrManifestText(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description);
}

internal sealed record SteamVrAutoLaunchResult(
    SteamVrAutoLaunchStatus Status,
    bool? IsEnabled,
    bool IsRegistered,
    string UserMessage,
    SteamVrAutoLaunchPendingReason? PendingReason = null,
    OpenVrApplicationError? Error = null)
{
    public bool Succeeded =>
        Status is SteamVrAutoLaunchStatus.Enabled or SteamVrAutoLaunchStatus.Disabled;

    public static SteamVrAutoLaunchResult Enabled(bool settingWasApplied = true) => new(
        SteamVrAutoLaunchStatus.Enabled,
        IsEnabled: true,
        IsRegistered: true,
        settingWasApplied
            ? "SteamVR起動時にVRCVAを自動で開く設定を有効にしました。"
            : "SteamVR起動時にVRCVAを自動で開く設定は有効です。");

    public static SteamVrAutoLaunchResult Disabled(
        bool isRegistered,
        bool settingWasApplied = true) => new(
        SteamVrAutoLaunchStatus.Disabled,
        IsEnabled: false,
        IsRegistered: isRegistered,
        isRegistered
            ? settingWasApplied
                ? "SteamVR起動時にVRCVAを自動で開く設定を無効にしました。"
                : "SteamVR起動時にVRCVAを自動で開く設定は無効です。"
            : "SteamVRへのVRCVA自動起動登録はまだありません。");

    public static SteamVrAutoLaunchResult Pending(
        SteamVrAutoLaunchPendingReason reason =
            SteamVrAutoLaunchPendingReason.SteamVrUnavailable) => new(
        SteamVrAutoLaunchStatus.PendingSteamVr,
        IsEnabled: null,
        IsRegistered: reason == SteamVrAutoLaunchPendingReason.ManifestReloadRequired,
        reason == SteamVrAutoLaunchPendingReason.ManifestReloadRequired
            ? "SteamVRへの登録を保存しました。VRCVAを終了し、SteamVRの再起動後にVRCVAを一度起動してください。"
            : "SteamVRへ接続できないため、自動起動設定は保留中です。VRCVAを終了し、SteamVRを起動してからVRCVAを一度起動してください。",
        PendingReason: reason);

    public static SteamVrAutoLaunchResult Failed(OpenVrApplicationError? error = null) => new(
        SteamVrAutoLaunchStatus.Failed,
        IsEnabled: null,
        IsRegistered: false,
        "SteamVRの自動起動設定を更新できませんでした。SteamVRを起動して、もう一度お試しください。",
        Error: error);
}

internal enum SteamVrAutoLaunchStatus
{
    Enabled,
    Disabled,
    PendingSteamVr,
    Failed,
}

internal enum SteamVrAutoLaunchPendingReason
{
    SteamVrUnavailable,
    ManifestReloadRequired,
}
