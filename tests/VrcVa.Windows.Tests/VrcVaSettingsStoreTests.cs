using System.IO;
using System.Text.Json;
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
            Assert.Equal(WristLauncherPlacement.Default, settings.WristLauncher);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_Version2_PreservesExistingSettingsAndAddsDefaultWristLauncher()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "Version": 2,
                  "ResultPanel": {
                    "Anchor": 2,
                    "X": 0,
                    "Y": -0.08,
                    "Z": -1.2,
                    "PitchDegrees": 0,
                    "YawDegrees": 0,
                    "RollDegrees": 0,
                    "WidthMeters": 1.15
                  },
                  "Onboarding": {
                    "IsCompleted": true,
                    "SteamVrAutoLaunchEnabled": true
                  }
                }
                """);

            VrcVaSettings settings = new VrcVaSettingsStore(path).Load();

            Assert.Equal(
                ResultPanelPlacement.CreateDefault(ResultPanelAnchor.Headset),
                settings.ResultPanel);
            Assert.Equal(
                new VrcVaOnboardingSettings(
                    IsCompleted: true,
                    SteamVrAutoLaunchEnabled: true),
                settings.Onboarding);
            Assert.Equal(WristLauncherPlacement.Default, settings.WristLauncher);
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
                    SteamVrAutoLaunchEnabled: true),
                WristLauncherPlacement.Default with
                {
                    X = -0.11,
                    Rotation = WristLauncherRotation.FromEulerDegrees(12, -27, 31),
                    MenuWidthMeters = 0.44,
                });
            VrcVaSettingsStore store = new(path);

            store.Save(expected);

            Assert.Equal(expected, store.Load());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            string json = File.ReadAllText(path);
            Assert.Contains("\"Version\": 4", json, StringComparison.Ordinal);
            Assert.DoesNotContain("ChipWidthMeters", json, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SourceText", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("JapaneseText", json, StringComparison.OrdinalIgnoreCase);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement wristLauncher = document.RootElement.GetProperty("WristLauncher");
            Assert.False(wristLauncher.TryGetProperty("PitchDegrees", out _));
            Assert.False(wristLauncher.TryGetProperty("YawDegrees", out _));
            Assert.False(wristLauncher.TryGetProperty("RollDegrees", out _));
            Assert.True(wristLauncher.TryGetProperty("Rotation", out _));
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
            File.WriteAllText(path, "{\"Version\":5}");

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VrcVaSettingsStore(path).Load());

            Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_Version3WithoutWristLauncher_IsRejectedAsInvalid()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "Version": 3,
                  "ResultPanel": {
                    "Anchor": 0,
                    "X": 0.08,
                    "Y": 0.12,
                    "Z": -0.28,
                    "PitchDegrees": 0,
                    "YawDegrees": 0,
                    "RollDegrees": 0,
                    "WidthMeters": 0.62
                  },
                  "Onboarding": {
                    "IsCompleted": false,
                    "SteamVrAutoLaunchEnabled": false
                  }
                }
                """);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new VrcVaSettingsStore(path).Load());

            Assert.Contains("version 3", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_Version3_MigratesEulerRotationWithoutChangingTransform()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "Version": 3,
                  "ResultPanel": {
                    "Anchor": 0,
                    "X": 0.08,
                    "Y": 0.12,
                    "Z": -0.28,
                    "PitchDegrees": 0,
                    "YawDegrees": 0,
                    "RollDegrees": 0,
                    "WidthMeters": 0.62
                  },
                  "Onboarding": {
                    "IsCompleted": true,
                    "SteamVrAutoLaunchEnabled": true
                  },
                  "WristLauncher": {
                    "X": -0.07,
                    "Y": 0.04,
                    "Z": 0.22,
                    "PitchDegrees": -175,
                    "YawDegrees": 100,
                    "RollDegrees": -5,
                    "MenuWidthMeters": 0.38
                  }
                }
                """);

            VrcVaSettings settings = new VrcVaSettingsStore(path).Load();

            ResultPanelTransform legacyTransform = new ResultPanelPlacement(
                ResultPanelAnchor.LeftHand,
                X: -0.07,
                Y: 0.04,
                Z: 0.22,
                PitchDegrees: -175,
                YawDegrees: 100,
                RollDegrees: -5,
                WidthMeters: 0.38).CreateTransform();
            ResultPanelTransform migratedTransform = settings.WristLauncher.CreateTransform();
            AssertTransformEqual(legacyTransform, migratedTransform);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"X\":0,\"Y\":0,\"Z\":0,\"W\":0}")]
    [InlineData("{\"X\":0,\"Y\":0,\"Z\":0,\"W\":2}")]
    [InlineData("{\"X\":\"NaN\",\"Y\":0,\"Z\":0,\"W\":1}")]
    public void Load_Version4WithInvalidRotation_IsRejected(string rotationJson)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, $$"""
                {
                  "Version": 4,
                  "ResultPanel": {
                    "Anchor": 0,
                    "X": 0.08,
                    "Y": 0.12,
                    "Z": -0.28,
                    "PitchDegrees": 0,
                    "YawDegrees": 0,
                    "RollDegrees": 0,
                    "WidthMeters": 0.62
                  },
                  "Onboarding": {
                    "IsCompleted": true,
                    "SteamVrAutoLaunchEnabled": true
                  },
                  "WristLauncher": {
                    "X": -0.07,
                    "Y": 0.04,
                    "Z": 0.22,
                    "Rotation": {{rotationJson}},
                    "MenuWidthMeters": 0.38
                  }
                }
                """);

            Assert.Throws<InvalidDataException>(() => new VrcVaSettingsStore(path).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertTransformEqual(
        ResultPanelTransform expected,
        ResultPanelTransform actual)
    {
        Assert.Equal(expected.M0, actual.M0, precision: 6);
        Assert.Equal(expected.M1, actual.M1, precision: 6);
        Assert.Equal(expected.M2, actual.M2, precision: 6);
        Assert.Equal(expected.M3, actual.M3, precision: 6);
        Assert.Equal(expected.M4, actual.M4, precision: 6);
        Assert.Equal(expected.M5, actual.M5, precision: 6);
        Assert.Equal(expected.M6, actual.M6, precision: 6);
        Assert.Equal(expected.M7, actual.M7, precision: 6);
        Assert.Equal(expected.M8, actual.M8, precision: 6);
        Assert.Equal(expected.M9, actual.M9, precision: 6);
        Assert.Equal(expected.M10, actual.M10, precision: 6);
        Assert.Equal(expected.M11, actual.M11, precision: 6);
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
