namespace AIArena.Wpf.Models;

public sealed record GenerationHistoryItem(
    string Id,
    string Kind,
    string Label,
    string Style,
    string Intensity,
    string RolePack,
    string Absurdity,
    string ScenarioSeed,
    string PersonaSeed,
    double CreatedAt,
    string Topic,
    string Global = "",
    string NarratorBrief = "",
    int PersonaCount = 0,
    string PersonaPreview = "");
