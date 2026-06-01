using AIArena.Wpf.Services;

var tests = new (string Name, Action Test)[]
{
    ("saves and reloads visual generation settings", SaveReloadVisualGenerationSettings),
    ("normalizes legacy theme and blank settings", NormalizeLegacyThemeAndBlankSettings),
    ("overwrites read-only settings file", OverwriteReadOnlySettingsFile)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void SaveReloadVisualGenerationSettings()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings
        {
            ThemeId = "dark-green",
            AvatarStyle = "procedural",
            TopStripMode = "telemetry",
            CompactTranscriptMode = true,
            TurnCompareMode = true,
            ShowMatchQualityTimeline = true,
            ShowAgentMemoryNotes = true,
            AllowDebugControls = true,
            ShowStyleFit = true,
            EnforceVoiceDrift = true,
            RandomSeedPreset = "chaos_room",
            RandomSeedRolePack = "absurd_lab",
            RandomSeedStyle = "technical",
            RandomSeedIntensity = "chaos",
            RandomSeedAbsurdity = "maximum",
            OperatorTemplates = ["one", "two"]
        });

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-green", "theme did not persist");
        Require(loaded.AvatarStyle == "procedural", "avatar style did not persist");
        Require(loaded.TopStripMode == "telemetry", "top strip did not persist");
        Require(loaded.CompactTranscriptMode, "compact transcript did not persist");
        Require(loaded.TurnCompareMode, "turn compare did not persist");
        Require(loaded.ShowMatchQualityTimeline, "quality timeline did not persist");
        Require(loaded.ShowAgentMemoryNotes, "memory notes did not persist");
        Require(loaded.AllowDebugControls, "debug controls did not persist");
        Require(loaded.ShowStyleFit, "style fit did not persist");
        Require(loaded.EnforceVoiceDrift, "voice enforcement did not persist");
        Require(loaded.RandomSeedPreset == "chaos_room", "seed preset did not persist");
        Require(loaded.RandomSeedRolePack == "absurd_lab", "role pack did not persist");
        Require(loaded.RandomSeedStyle == "technical", "seed style did not persist");
        Require(loaded.RandomSeedIntensity == "chaos", "seed intensity did not persist");
        Require(loaded.RandomSeedAbsurdity == "maximum", "absurdity did not persist");
        Require(loaded.OperatorTemplates.SequenceEqual(["one", "two"]), "operator templates did not persist");
    });
}

static void NormalizeLegacyThemeAndBlankSettings()
{
    WithTempSettingsStore(store =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, """
        {
          "themeId": "system",
          "avatarStyle": "",
          "topStripMode": "",
          "randomSeedStyle": " ",
          "randomSeedIntensity": "",
          "randomSeedRolePack": "",
          "randomSeedAbsurdity": "",
          "randomSeedPreset": "",
          "operatorTemplates": null
        }
        """);

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-blue", "legacy system theme should normalize to dark-blue");
        Require(loaded.AvatarStyle == "pack", "blank avatar style should normalize");
        Require(loaded.TopStripMode == "diagnostics", "blank top strip should normalize");
        Require(loaded.RandomSeedStyle == "auto", "blank random seed style should normalize");
        Require(loaded.RandomSeedIntensity == "normal", "blank random seed intensity should normalize");
        Require(loaded.RandomSeedRolePack == "auto", "blank role pack should normalize");
        Require(loaded.RandomSeedAbsurdity == "grounded", "blank absurdity should normalize");
        Require(loaded.RandomSeedPreset == "manual", "blank preset should normalize");
        Require(loaded.OperatorTemplates.Count > 0, "null operator templates should keep default templates");
    });
}

static void OverwriteReadOnlySettingsFile()
{
    WithTempSettingsStore(store =>
    {
        store.Save(new WpfSettings { ThemeId = "dark-green" });
        File.SetAttributes(store.SettingsPath, File.GetAttributes(store.SettingsPath) | FileAttributes.ReadOnly);
        store.Save(new WpfSettings
        {
            ThemeId = "dark-blue",
            RandomSeedPreset = "evidence_trial",
            RandomSeedIntensity = "sharp"
        });

        var loaded = store.Load();
        Require(loaded.ThemeId == "dark-blue", "read-only overwrite did not persist theme");
        Require(loaded.RandomSeedPreset == "evidence_trial", "read-only overwrite did not persist preset");
        Require(loaded.RandomSeedIntensity == "sharp", "read-only overwrite did not persist intensity");
    });
}

static void WithTempSettingsStore(Action<WpfSettingsStore> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-wpf-tests", Guid.NewGuid().ToString("N"));
    var store = new WpfSettingsStore(Path.Combine(root, "configs", "native-wpf-settings.json"));
    try
    {
        action(store);
    }
    finally
    {
        if (File.Exists(store.SettingsPath))
        {
            File.SetAttributes(store.SettingsPath, File.GetAttributes(store.SettingsPath) & ~FileAttributes.ReadOnly);
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
