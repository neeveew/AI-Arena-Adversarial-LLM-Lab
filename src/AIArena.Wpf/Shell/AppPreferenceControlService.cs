using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed record AIArenaPreferencePatch(
    bool? CompactTranscript = null,
    bool? FollowTranscript = null,
    string? TopStripMode = null,
    bool? TurnCompare = null,
    bool? MatchTimeline = null,
    bool? BattleReview = null,
    bool? MemoryNotes = null,
    bool? DecisionCard = null,
    bool? AutoModerator = null,
    bool? StyleFit = null,
    bool? InternetDetails = null,
    bool? VoiceEnabled = null,
    bool? WorldEnabled = null,
    bool? AgentWorkspaceEnabled = null);

internal sealed record AIArenaPreferenceControlData(
    AIArenaSettingsControlState State,
    IReadOnlyList<string> Changed);

internal sealed record AIArenaPreferenceControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaPreferenceControlData Data);

internal sealed class AppPreferenceControlService
{
    private static readonly HashSet<string> TopStripModes =
        new(["diagnostics", "telemetry", "hidden"], StringComparer.OrdinalIgnoreCase);

    private readonly WpfSettingsStore settingsStore;
    private readonly Func<WpfSettings> settings;
    private readonly Action apply;
    private readonly Func<AIArenaSettingsControlState> capture;

    public AppPreferenceControlService(
        WpfSettingsStore settingsStore,
        Func<WpfSettings> settings,
        Action apply,
        Func<AIArenaSettingsControlState> capture)
    {
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.apply = apply;
        this.capture = capture;
    }

    public AIArenaPreferenceControlResult Update(AIArenaPreferencePatch patch)
    {
        if (!HasValues(patch))
        {
            return Failure("missing_argument", "settings.update requires at least one supported preference.");
        }

        var topStrip = patch.TopStripMode?.Trim().ToLowerInvariant();
        if (topStrip is not null && !TopStripModes.Contains(topStrip))
        {
            return Failure("invalid_argument", "args.topStripMode must be diagnostics, telemetry, or hidden.");
        }

        var current = settings();
        if (patch.WorldEnabled == true && !current.AllowDebugControls)
        {
            return Failure("debug_controls_required", "Enable master debug controls in the UI before exposing World.");
        }

        var changed = new List<string>();
        Set(patch.CompactTranscript, current.CompactTranscriptMode, value => current.CompactTranscriptMode = value, "compactTranscript", changed);
        Set(patch.FollowTranscript, current.FollowTranscript, value => current.FollowTranscript = value, "followTranscript", changed);
        Set(patch.TurnCompare, current.TurnCompareMode, value => current.TurnCompareMode = value, "turnCompare", changed);
        Set(patch.MatchTimeline, current.ShowMatchQualityTimeline, value => current.ShowMatchQualityTimeline = value, "matchTimeline", changed);
        Set(patch.BattleReview, current.ShowBattleReview, value => current.ShowBattleReview = value, "battleReview", changed);
        Set(patch.MemoryNotes, current.ShowAgentMemoryNotes, value => current.ShowAgentMemoryNotes = value, "memoryNotes", changed);
        Set(patch.DecisionCard, current.ShowDecisionCard, value => current.ShowDecisionCard = value, "decisionCard", changed);
        Set(patch.AutoModerator, current.ShowAutoModerator, value => current.ShowAutoModerator = value, "autoModerator", changed);
        Set(patch.StyleFit, current.ShowStyleFit, value => current.ShowStyleFit = value, "styleFit", changed);
        Set(patch.InternetDetails, current.ShowTranscriptInternetDetails, value => current.ShowTranscriptInternetDetails = value, "internetDetails", changed);
        Set(patch.VoiceEnabled, current.VoiceTtsEnabled, value => current.VoiceTtsEnabled = value, "voiceEnabled", changed);
        Set(patch.WorldEnabled, current.ShowWorldDebug, value => current.ShowWorldDebug = value, "worldEnabled", changed);
        Set(
            patch.AgentWorkspaceEnabled,
            current.ShowAgentWorkspace,
            value =>
            {
                current.ShowAgentWorkspace = value;
                current.AgentWorkspacePreferenceVersion = 1;
            },
            "agentWorkspaceEnabled",
            changed);
        if (topStrip is not null && !topStrip.Equals(current.TopStripMode, StringComparison.OrdinalIgnoreCase))
        {
            current.TopStripMode = topStrip;
            current.ShowTranscriptDiagnostics = topStrip.Equals("diagnostics", StringComparison.OrdinalIgnoreCase);
            changed.Add("topStripMode");
        }

        if (changed.Count > 0)
        {
            settingsStore.Save(current);
            apply();
        }

        var message = changed.Count == 0
            ? "Settings already matched the requested preferences."
            : $"Updated {changed.Count} setting preference(s).";
        return new AIArenaPreferenceControlResult(
            true,
            "",
            message,
            new AIArenaPreferenceControlData(capture(), changed));
    }

    private AIArenaPreferenceControlResult Failure(string errorCode, string message)
    {
        return new AIArenaPreferenceControlResult(
            false,
            errorCode,
            message,
            new AIArenaPreferenceControlData(capture(), []));
    }

    private static bool HasValues(AIArenaPreferencePatch patch)
    {
        return patch.CompactTranscript is not null
            || patch.FollowTranscript is not null
            || patch.TopStripMode is not null
            || patch.TurnCompare is not null
            || patch.MatchTimeline is not null
            || patch.BattleReview is not null
            || patch.MemoryNotes is not null
            || patch.DecisionCard is not null
            || patch.AutoModerator is not null
            || patch.StyleFit is not null
            || patch.InternetDetails is not null
            || patch.VoiceEnabled is not null
            || patch.WorldEnabled is not null
            || patch.AgentWorkspaceEnabled is not null;
    }

    private static void Set(
        bool? requested,
        bool current,
        Action<bool> assign,
        string name,
        List<string> changed)
    {
        if (requested is null || requested.Value == current)
        {
            return;
        }

        assign(requested.Value);
        changed.Add(name);
    }
}
