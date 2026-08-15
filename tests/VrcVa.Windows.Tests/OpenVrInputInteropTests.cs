using System.IO;
using System.Text.Json;
using VrcVa.Windows.Diagnostics;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrInputInteropTests
{
    [Fact]
    public void ValidateAbi_AcceptsExpectedWindowsX64Layouts() =>
        OpenVrInputInterop.ValidateAbi();

    [Fact]
    public void ResolveActionManifestPath_UsesOnlyExpectedRelativeAssetPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vrcva-input-{Guid.NewGuid():N}");
        string assets = Path.Combine(directory, "OpenVr", "Assets");
        Directory.CreateDirectory(assets);
        string expected = Path.Combine(assets, "actions.json");
        File.WriteAllText(expected, "{}");

        try
        {
            Assert.Equal(Path.GetFullPath(expected), OpenVrInputInterop.ResolveActionManifestPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(300, 300)]
    public void ParseTimeout_AcceptsDocumentedBounds(int input, int expected)
    {
        TimeSpan timeout = SteamVrInputDiagnosticRunner.ParseTimeout(
            ["--steamvr-input-pass-through-check", "--seconds", input.ToString()]);

        Assert.Equal(TimeSpan.FromSeconds(expected), timeout);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("301")]
    [InlineData("invalid")]
    public void ParseTimeout_RejectsInvalidValues(string input) =>
        Assert.Throws<ArgumentException>(() => SteamVrInputDiagnosticRunner.ParseTimeout(
            ["--steamvr-input-pass-through-check", "--seconds", input]));

    [Fact]
    public void ShippedManifest_BindsOnlyRightTriggerAndRightPose()
    {
        string sourceRoot = FindRepositoryRoot();
        string manifestPath = Path.Combine(
            sourceRoot,
            "src",
            "VrcVa.Windows",
            "OpenVr",
            "Assets",
            "actions.json");
        string bindingPath = Path.Combine(
            sourceRoot,
            "src",
            "VrcVa.Windows",
            "OpenVr",
            "Assets",
            "bindings_oculus_touch.json");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using JsonDocument binding = JsonDocument.Parse(File.ReadAllText(bindingPath));
        string manifestJson = manifest.RootElement.GetRawText();
        string bindingJson = binding.RootElement.GetRawText();

        Assert.Contains(OpenVrInputInterop.SelectActionPath, manifestJson, StringComparison.Ordinal);
        Assert.Contains(OpenVrInputInterop.PointerPoseActionPath, manifestJson, StringComparison.Ordinal);
        Assert.Contains("/user/hand/right/input/trigger", bindingJson, StringComparison.Ordinal);
        Assert.Contains("/user/hand/right/pose/tip", bindingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("/user/hand/right/pose/raw", bindingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("joystick", bindingJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbstick", bindingJson, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VRChatVisualAssistant.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output path.");
    }
}
