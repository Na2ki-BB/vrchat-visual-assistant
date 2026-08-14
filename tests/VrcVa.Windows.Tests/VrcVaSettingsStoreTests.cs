using System.IO;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Settings;

namespace VrcVa.Windows.Tests;

public sealed class VrcVaSettingsStoreTests
{
    [Fact]
    public void Load_WhenFileIsAbsent_ReturnsDefaults()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            VrcVaSettingsStore store = new(Path.Combine(directory, "settings.json"));

            Assert.Equal(VrcVaSettings.Default, store.Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_Version1_MigratesPlacementAndUsesIncompleteOnboarding()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "Version": 1,
                  "ResultPanel": {
                    "Anchor": 1,
                    "X": -0.12,
                    "Y": 0.2,
                    "Z": -0.35,
                    "PitchDegrees": -15,
                    "YawDegrees": 20,
                    "RollDegrees": 5,
                    "WidthMeters": 0.7
                  }
                }
                """);

            VrcVaSettings settings = new VrcVaSettingsStore(path).Load();

            Assert.Equal(ResultPanelAnchor.RightHand, settings.ResultPanel.Anchor);
            Assert.Equal(-0.12, settings.ResultPanel.X);
            Assert.Equal(VrcVaOnboardingSettings.Default, settings.Onboarding);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNonSecretSettingsWithoutTemporaryFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            VrcVaSettings expected = new(
                ResultPanelPlacement.CreateDefault(ResultPanelAnchor.Headset),
                new VrcVaOnboardingSettings(
                    IsCompleted: true,
                    SteamVrAutoLaunchEnabled: true));
            VrcVaSettingsStore store = new(path);

            store.Save(expected);

            Assert.Equal(expected, store.Load());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            string json = File.ReadAllText(path);
            Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SourceText", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("JapaneseText", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsUnknownFutureVersion()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"Version\":3,\"ResultPanel\":null,\"Onboarding\":null}");

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VrcVaSettingsStore(path).Load());

            Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "VrcVa.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
