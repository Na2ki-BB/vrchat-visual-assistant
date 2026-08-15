using System.IO;
using System.Text.Json;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Startup;

namespace VrcVa.Windows.Tests;

public sealed class SteamVrAutoLaunchRegistrationTests
{
    [Fact]
    public void ApplicationsAbi_UsesOpenVr1267InterfaceAndSlots()
    {
        Assert.Equal("FnTable:IVRApplications_007", OpenVrApplicationsInterop.ApplicationsInterface);
        Assert.Equal(0, OpenVrApplicationsInterop.ApplicationsSlot.AddApplicationManifest);
        Assert.Equal(2, OpenVrApplicationsInterop.ApplicationsSlot.IsApplicationInstalled);
        Assert.Equal(17, OpenVrApplicationsInterop.ApplicationsSlot.SetApplicationAutoLaunch);
        Assert.Equal(18, OpenVrApplicationsInterop.ApplicationsSlot.GetApplicationAutoLaunch);
    }

    [Fact]
    public void CreateManifestJson_UsesFixedApplicationContractAndAbsolutePaths()
    {
        string executablePath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "VRCVA Folder",
            "VrcVa.exe"));

        using JsonDocument document = JsonDocument.Parse(
            SteamVrAutoLaunchRegistration.CreateManifestJson(executablePath));

        JsonElement root = document.RootElement;
        Assert.Equal("builtin", root.GetProperty("source").GetString());
        JsonElement application = Assert.Single(root.GetProperty("applications").EnumerateArray());
        Assert.Equal(
            SteamVrAutoLaunchRegistration.ApplicationKey,
            application.GetProperty("app_key").GetString());
        Assert.Equal("binary", application.GetProperty("launch_type").GetString());
        Assert.Equal(executablePath, application.GetProperty("binary_path_windows").GetString());
        Assert.Equal(
            Path.GetDirectoryName(executablePath),
            application.GetProperty("working_directory").GetString());
        Assert.Equal(
            SteamVrAutoLaunchRegistration.AutoStartArgument,
            application.GetProperty("arguments").GetString());
        Assert.Equal(
            Path.Combine(
                Path.GetDirectoryName(executablePath)!,
                "OpenVr",
                "Assets",
                "actions.json"),
            application.GetProperty("action_manifest_path").GetString());
        Assert.True(application.GetProperty("is_dashboard_overlay").GetBoolean());
        Assert.False(root.TryGetProperty("api_key", out _));
    }

    [Fact]
    public void Apply_Enable_WritesManifestThenRegistersAndVerifiesAutoLaunch()
    {
        using TemporaryDirectory directory = new();
        string executablePath = directory.CreateFile("VrcVa.exe", "test executable");
        string manifestPath = Path.Combine(directory.Path, "registration", "vrcva.vrmanifest");
        FakeApplications applications = new()
        {
            Installed = true,
            AutoLaunch = true,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            executablePath);

        SteamVrAutoLaunchResult result = registration.Apply(enabled: true);

        Assert.Equal(SteamVrAutoLaunchStatus.Enabled, result.Status);
        Assert.True(result.Succeeded);
        Assert.True(result.IsEnabled);
        Assert.True(result.IsRegistered);
        Assert.Equal(
            [
                $"Add:{Path.GetFullPath(manifestPath)}:False",
                $"Installed:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                $"Set:{SteamVrAutoLaunchRegistration.ApplicationKey}:True",
                $"Get:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                "Dispose",
            ],
            applications.Calls);
        Assert.True(File.Exists(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(manifestPath)!, "*.tmp"));
        Assert.DoesNotContain(executablePath, result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenSteamVrIsStopped_ReturnsPendingWithoutWritingManifest()
    {
        using TemporaryDirectory directory = new();
        string manifestPath = Path.Combine(directory.Path, "vrcva.vrmanifest");
        FakeApplicationsFactory factory = new(
            OpenVrApplicationsConnectionResult.Unavailable,
            applications: null);
        SteamVrAutoLaunchRegistration registration = new(
            factory,
            manifestPath,
            () => Path.Combine(directory.Path, "VrcVa.exe"));

        SteamVrAutoLaunchResult result = registration.Apply(enabled: true);

        Assert.Equal(SteamVrAutoLaunchStatus.PendingSteamVr, result.Status);
        Assert.Equal(
            SteamVrAutoLaunchPendingReason.SteamVrUnavailable,
            result.PendingReason);
        Assert.False(result.Succeeded);
        Assert.Null(result.IsEnabled);
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void Apply_WhenManifestNeedsSteamVrReload_ReturnsRegisteredPendingWithoutSettingFlag()
    {
        using TemporaryDirectory directory = new();
        string executablePath = directory.CreateFile("VrcVa.exe", "test executable");
        string manifestPath = Path.Combine(directory.Path, "vrcva.vrmanifest");
        FakeApplications applications = new()
        {
            Installed = false,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            executablePath);

        SteamVrAutoLaunchResult result = registration.Apply(enabled: true);

        Assert.Equal(SteamVrAutoLaunchStatus.PendingSteamVr, result.Status);
        Assert.Equal(
            SteamVrAutoLaunchPendingReason.ManifestReloadRequired,
            result.PendingReason);
        Assert.True(result.IsRegistered);
        Assert.Null(result.IsEnabled);
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(
            [
                $"Add:{Path.GetFullPath(manifestPath)}:False",
                $"Installed:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                "Dispose",
            ],
            applications.Calls);
    }

    [Fact]
    public void Apply_Disable_OnlyClearsAutoLaunchAndLeavesManifestUntouched()
    {
        using TemporaryDirectory directory = new();
        string manifestPath = directory.CreateFile("vrcva.vrmanifest", "existing manifest");
        FakeApplications applications = new()
        {
            Installed = true,
            AutoLaunch = false,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            Path.Combine(directory.Path, "unused.exe"));

        SteamVrAutoLaunchResult result = registration.Apply(enabled: false);

        Assert.Equal(SteamVrAutoLaunchStatus.Disabled, result.Status);
        Assert.True(result.Succeeded);
        Assert.False(result.IsEnabled);
        Assert.True(result.IsRegistered);
        Assert.Equal("existing manifest", File.ReadAllText(manifestPath));
        Assert.Equal(
            [
                $"Installed:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                $"Set:{SteamVrAutoLaunchRegistration.ApplicationKey}:False",
                $"Get:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                "Dispose",
            ],
            applications.Calls);
    }

    [Fact]
    public void Apply_DisableWhenNotRegistered_DoesNotCreateOrDeleteFiles()
    {
        using TemporaryDirectory directory = new();
        string manifestPath = Path.Combine(directory.Path, "vrcva.vrmanifest");
        FakeApplications applications = new()
        {
            Installed = false,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            Path.Combine(directory.Path, "unused.exe"));

        SteamVrAutoLaunchResult result = registration.Apply(enabled: false);

        Assert.Equal(SteamVrAutoLaunchStatus.Disabled, result.Status);
        Assert.False(result.IsRegistered);
        Assert.False(File.Exists(manifestPath));
        Assert.Equal(
            [
                $"Installed:{SteamVrAutoLaunchRegistration.ApplicationKey}",
                "Dispose",
            ],
            applications.Calls);
    }

    [Fact]
    public void Apply_WhenRegistrationFails_ReturnsTypedFailureWithoutPath()
    {
        using TemporaryDirectory directory = new();
        string executablePath = directory.CreateFile("VrcVa.exe", "test executable");
        string manifestPath = Path.Combine(directory.Path, "vrcva.vrmanifest");
        FakeApplications applications = new()
        {
            AddError = OpenVrApplicationError.InvalidManifest,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            executablePath);

        SteamVrAutoLaunchResult result = registration.Apply(enabled: true);

        Assert.Equal(SteamVrAutoLaunchStatus.Failed, result.Status);
        Assert.Equal(OpenVrApplicationError.InvalidManifest, result.Error);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain(executablePath, result.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(manifestPath, result.UserMessage, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"Add:{Path.GetFullPath(manifestPath)}:False",
                "Dispose",
            ],
            applications.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetStatus_ReportsRegisteredStateWithoutWritingManifest(bool autoLaunch)
    {
        using TemporaryDirectory directory = new();
        string manifestPath = Path.Combine(directory.Path, "vrcva.vrmanifest");
        FakeApplications applications = new()
        {
            Installed = true,
            AutoLaunch = autoLaunch,
        };
        SteamVrAutoLaunchRegistration registration = CreateRegistration(
            applications,
            manifestPath,
            Path.Combine(directory.Path, "unused.exe"));

        SteamVrAutoLaunchResult result = registration.GetStatus();

        Assert.Equal(
            autoLaunch ? SteamVrAutoLaunchStatus.Enabled : SteamVrAutoLaunchStatus.Disabled,
            result.Status);
        Assert.Equal(autoLaunch, result.IsEnabled);
        Assert.True(result.IsRegistered);
        Assert.False(File.Exists(manifestPath));
    }

    private static SteamVrAutoLaunchRegistration CreateRegistration(
        FakeApplications applications,
        string manifestPath,
        string executablePath) =>
        new(
            new FakeApplicationsFactory(
                OpenVrApplicationsConnectionResult.Connected,
                applications),
            manifestPath,
            () => executablePath);

    private sealed class FakeApplicationsFactory(
        OpenVrApplicationsConnectionResult result,
        IOpenVrApplications? applications) : IOpenVrApplicationsFactory
    {
        public OpenVrApplicationsConnectionResult TryCreate(
            out IOpenVrApplications? createdApplications)
        {
            createdApplications = applications;
            return result;
        }
    }

    private sealed class FakeApplications : IOpenVrApplications
    {
        public List<string> Calls { get; } = [];

        public OpenVrApplicationError AddError { get; init; } = OpenVrApplicationError.None;

        public OpenVrApplicationError SetError { get; init; } = OpenVrApplicationError.None;

        public bool Installed { get; init; }

        public bool AutoLaunch { get; init; }

        public OpenVrApplicationError AddApplicationManifest(string manifestPath, bool temporary)
        {
            Calls.Add($"Add:{manifestPath}:{temporary}");
            return AddError;
        }

        public bool IsApplicationInstalled(string applicationKey)
        {
            Calls.Add($"Installed:{applicationKey}");
            return Installed;
        }

        public OpenVrApplicationError SetApplicationAutoLaunch(string applicationKey, bool enabled)
        {
            Calls.Add($"Set:{applicationKey}:{enabled}");
            return SetError;
        }

        public bool GetApplicationAutoLaunch(string applicationKey)
        {
            Calls.Add($"Get:{applicationKey}");
            return AutoLaunch;
        }

        public void Dispose() => Calls.Add("Dispose");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "VrcVa.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string relativePath, string contents)
        {
            string path = System.IO.Path.Combine(Path, relativePath);
            string? directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
