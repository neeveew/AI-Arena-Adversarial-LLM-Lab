using AIArena.Core.Models;

namespace AIArena.Wpf;

internal static class RoleStyleCatalog
{
    public static IReadOnlyList<(string Tag, string Label)> VoiceStyleOptions { get; } =
    [
        ("default", "Voice: Default"),
        ("scientific", "Voice: Scientific"),
        ("legal_policy", "Voice: Legal / Policy"),
        ("plain_language", "Voice: Plain"),
        ("idioms", "Voice: Idioms"),
        ("cute", "Voice: Cute"),
        ("poetic", "Voice: Poetic"),
        ("socratic", "Voice: Socratic"),
        ("bullet_only", "Voice: Bullet-only"),
        ("skeptical", "Voice: Skeptical"),
        ("executive_brief", "Voice: Executive"),
        ("evidence_ledger", "Voice: Evidence"),
        ("no_analogies", "Voice: No analogies"),
        ("hedge_uncertainty", "Voice: Hedge"),
        ("bark_only", "Voice: Bark-only"),
        ("science_gibberish", "Voice: Science gibberish")
    ];

    public static IReadOnlyList<(string Tag, string Label)> AgentPressureOptions { get; } =
    [
        ("default", "Pressure: Default"),
        ("calm", "Pressure: Calm"),
        ("assertive", "Pressure: Assertive"),
        ("contrarian", "Pressure: Contrarian"),
        ("evidence", "Pressure: Evidence"),
        ("risk", "Pressure: Risk"),
        ("concise", "Pressure: Concise"),
        ("expansive", "Pressure: Expansive"),
        ("chaos", "Pressure: Chaos")
    ];

    public static string NormalizeVoiceStyleTag(string? value)
    {
        var cleaned = NormalizeTag(value);
        return VoiceStyleOptions.Any(option => option.Tag.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            ? cleaned
            : "default";
    }

    public static string VoiceStyleLabel(string? value)
    {
        var tag = NormalizeVoiceStyleTag(value);
        var label = VoiceStyleOptions.First(option => option.Tag == tag).Label;
        return label.Replace("Voice: ", "", StringComparison.OrdinalIgnoreCase);
    }

    public static string VoiceStyleChipText(string? value)
    {
        return NormalizeVoiceStyleTag(value).Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Voice: {VoiceStyleLabel(value)}";
    }

    public static string NormalizeAgentPressureTag(string? value)
    {
        var cleaned = NormalizeTag(value);
        return AgentPressureOptions.Any(option => option.Tag.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            ? cleaned
            : "default";
    }

    public static string AgentPressureLabel(string? value)
    {
        var tag = NormalizeAgentPressureTag(value);
        var label = AgentPressureOptions.First(option => option.Tag == tag).Label;
        return label.Replace("Pressure: ", "", StringComparison.OrdinalIgnoreCase);
    }

    public static string AgentPressureChipText(string? value)
    {
        return NormalizeAgentPressureTag(value).Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Pressure: {AgentPressureLabel(value)}";
    }

    public static string VoiceAdherenceState(int score, int samples)
    {
        if (samples <= 0)
        {
            return "none";
        }

        return score >= 74 ? "strong" : score >= 46 ? "drifting" : "broken";
    }

    public static string VoiceAdherenceDisplayState(string state)
    {
        return state.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? "strong cues"
            : state.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? "partial cues"
                : state.Equals("broken", StringComparison.OrdinalIgnoreCase)
                    ? "low cues"
                    : "no cues";
    }

    public static string VoiceAdherenceChipText(VoiceAdherenceDiagnostic diagnostic)
    {
        var cue = diagnostic.State.Equals("strong", StringComparison.OrdinalIgnoreCase)
            ? "strong"
            : diagnostic.State.Equals("drifting", StringComparison.OrdinalIgnoreCase)
                ? "partial"
                : diagnostic.State.Equals("broken", StringComparison.OrdinalIgnoreCase)
                    ? "low"
                    : "none";
        return $"Cues: {cue} {diagnostic.Score}";
    }

    public static bool IsStrictVoiceStyle(string? voiceStyle)
    {
        var normalized = NormalizeVoiceStyleTag(voiceStyle);
        return normalized.Equals("bullet_only", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("evidence_ledger", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no_analogies", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("bark_only", StringComparison.OrdinalIgnoreCase);
    }

    public static string VoiceAdherenceTooltip(VoiceAdherenceDiagnostic diagnostic)
    {
        var evidence = diagnostic.Evidence.Count == 0
            ? "Evidence: -"
            : $"Evidence: {string.Join("; ", diagnostic.Evidence)}";
        var missing = diagnostic.Missing.Count == 0
            ? "Missing: -"
            : $"Missing: {string.Join("; ", diagnostic.Missing)}";
        return $"{diagnostic.Summary}{Environment.NewLine}{evidence}{Environment.NewLine}{missing}";
    }

    private static string NormalizeTag(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    }
}
