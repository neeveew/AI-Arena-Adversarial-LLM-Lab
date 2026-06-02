using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Persistence;

namespace AIArena.Wpf.Services;

public sealed class WpfSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string SettingsPath { get; }
    public string LastLoadWarning { get; private set; } = "";

    public WpfSettingsStore()
    {
        var dataRoot = NativeDataPaths.DefaultDataRoot();
        SettingsPath = NativeDataPaths.ConfigPath(dataRoot, "native-wpf-settings.json");
    }

    public WpfSettingsStore(string settingsPath)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? NativeDataPaths.ConfigPath(NativeDataPaths.DefaultDataRoot(), "native-wpf-settings.json")
            : settingsPath;
    }

    public WpfSettings Load()
    {
        LastLoadWarning = "";
        if (!File.Exists(SettingsPath))
        {
            return new WpfSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return Normalize(JsonSerializer.Deserialize<WpfSettings>(json) ?? new WpfSettings());
        }
        catch (Exception ex)
        {
            LastLoadWarning = JsonFileRecovery.BackupCorruptFile(SettingsPath, "Settings", ex);
            return new WpfSettings();
        }
    }

    public void Save(WpfSettings settings)
    {
        LastLoadWarning = "";
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = $"{SettingsPath}.tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(SettingsPath))
        {
            File.SetAttributes(SettingsPath, File.GetAttributes(SettingsPath) & ~FileAttributes.ReadOnly);
        }

        File.Move(tempPath, SettingsPath, overwrite: true);
    }

    private static WpfSettings Normalize(WpfSettings settings)
    {
        settings.ThemeId = ThemePalette.NormalizeId(settings.ThemeId);
        settings.AvatarStyle = NormalizeChoice(settings.AvatarStyle, "pack");
        settings.TopStripMode = NormalizeChoice(settings.TopStripMode, "diagnostics");
        settings.RandomSeedStyle = NormalizeChoice(settings.RandomSeedStyle, "auto");
        settings.RandomSeedIntensity = NormalizeChoice(settings.RandomSeedIntensity, "normal");
        settings.RandomSeedRolePack = NormalizeChoice(settings.RandomSeedRolePack, "auto");
        settings.RandomSeedAbsurdity = NormalizeChoice(settings.RandomSeedAbsurdity, "grounded");
        settings.RandomSeedPreset = NormalizeChoice(settings.RandomSeedPreset, "manual");
        settings.OperatorTemplates ??= [];
        return settings;
    }

    private static string NormalizeChoice(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

public sealed class WpfSettings
{
    public string ThemeId { get; set; } = "dark-blue";
    public string AvatarStyle { get; set; } = "pack";
    public bool ChampionAvatars { get; set; } = true;
    public bool SystemEventGlyphs { get; set; } = true;
    public bool CompactTranscriptMode { get; set; }
    public bool TurnCompareMode { get; set; }
    public bool ShowMatchQualityTimeline { get; set; }
    public bool ShowAgentMemoryNotes { get; set; }
    public bool ShowDecisionCard { get; set; }
    public bool ShowAutoModerator { get; set; } = true;
    public bool AllowDebugControls { get; set; }
    public bool ShowStyleFit { get; set; }
    public bool EnforceVoiceDrift { get; set; }
    public string TopStripMode { get; set; } = "diagnostics";
    public bool ShowTranscriptDiagnostics { get; set; } = true;
    public string RandomSeedStyle { get; set; } = "auto";
    public string RandomSeedIntensity { get; set; } = "normal";
    public string RandomSeedRolePack { get; set; } = "auto";
    public string RandomSeedAbsurdity { get; set; } = "grounded";
    public string RandomSeedPreset { get; set; } = "manual";
    public List<string> OperatorTemplates { get; set; } =
    [
        "Challenge the strongest assumption in the last turn.",
        "Summarize the disagreement and ask for the smallest concrete next step.",
        "Force the agents to separate facts, guesses, and decisions.",
        "Ask for risks, reversibility, and what would change the conclusion."
    ];
}
