using System.IO;
using System.Text.Json;

namespace AIArena.Wpf.Services;

public sealed class WpfSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsPath { get; }

    public WpfSettingsStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = Path.Combine(localAppData, "AI Arena Alpha", "data");
        Directory.CreateDirectory(dataRoot);
        SettingsPath = Path.Combine(dataRoot, "native-wpf-settings.json");
    }

    public WpfSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new WpfSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<WpfSettings>(json) ?? new WpfSettings();
        }
        catch
        {
            return new WpfSettings();
        }
    }

    public void Save(WpfSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}

public sealed class WpfSettings
{
    public string ThemeId { get; set; } = "system";
    public string AvatarStyle { get; set; } = "pack";
    public bool ChampionAvatars { get; set; } = true;
    public bool SystemEventGlyphs { get; set; } = true;
    public bool CompactTranscriptMode { get; set; }
    public bool TurnCompareMode { get; set; }
    public bool ShowMatchQualityTimeline { get; set; }
    public bool ShowAgentMemoryNotes { get; set; }
    public string TopStripMode { get; set; } = "diagnostics";
    public bool ShowTranscriptDiagnostics { get; set; } = true;
    public List<string> OperatorTemplates { get; set; } =
    [
        "Challenge the strongest assumption in the last turn.",
        "Summarize the disagreement and ask for the smallest concrete next step.",
        "Force the agents to separate facts, guesses, and decisions.",
        "Ask for risks, reversibility, and what would change the conclusion."
    ];
}
