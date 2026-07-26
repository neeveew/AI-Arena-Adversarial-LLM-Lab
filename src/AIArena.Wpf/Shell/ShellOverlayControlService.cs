namespace AIArena.Wpf;

internal sealed record AIArenaMatchSetupControlState(
    bool Open,
    string Section,
    string ReturnView,
    string SessionId,
    string MatchType,
    string Scenario,
    int ActiveAgents,
    bool Busy);

internal sealed record AIArenaSettingsControlState(
    bool Open,
    string SearchQuery,
    string Theme,
    bool CompactTranscript,
    bool FollowTranscript,
    string TopStripMode,
    bool TurnCompare,
    bool MatchTimeline,
    bool BattleReview,
    bool MemoryNotes,
    bool DecisionCard,
    bool AutoModerator,
    bool StyleFit,
    bool InternetDetails,
    bool RightRailCollapsed,
    bool DebugControls,
    bool WorldEnabled,
    bool AgentWorkspaceEnabled,
    bool ControlPlaneEnabled,
    bool VoiceEnabled);

internal sealed record AIArenaShellOverlayControlResult<T>(
    bool Ok,
    string ErrorCode,
    string Message,
    T State);

/// <summary>
/// Protocol-independent overlay control boundary. The WPF host owns focus and visual
/// transitions; this service owns argument validation and stable automation receipts.
/// </summary>
internal sealed class ShellOverlayControlService
{
    private static readonly HashSet<string> MatchSetupSections =
        new(["scenario", "cast", "matrix", "saved"], StringComparer.OrdinalIgnoreCase);

    private readonly Func<AIArenaMatchSetupControlState> matchSetupState;
    private readonly Action showMatchSetup;
    private readonly Action closeMatchSetup;
    private readonly Func<string, bool> selectMatchSetupSection;
    private readonly Func<AIArenaSettingsControlState> settingsState;
    private readonly Action showSettings;
    private readonly Action closeSettings;
    private readonly Action<string> setSettingsSearch;

    public ShellOverlayControlService(
        Func<AIArenaMatchSetupControlState> matchSetupState,
        Action showMatchSetup,
        Action closeMatchSetup,
        Func<string, bool> selectMatchSetupSection,
        Func<AIArenaSettingsControlState> settingsState,
        Action showSettings,
        Action closeSettings,
        Action<string> setSettingsSearch)
    {
        this.matchSetupState = matchSetupState;
        this.showMatchSetup = showMatchSetup;
        this.closeMatchSetup = closeMatchSetup;
        this.selectMatchSetupSection = selectMatchSetupSection;
        this.settingsState = settingsState;
        this.showSettings = showSettings;
        this.closeSettings = closeSettings;
        this.setSettingsSearch = setSettingsSearch;
    }

    public AIArenaMatchSetupControlState CaptureMatchSetup() => matchSetupState();

    public AIArenaShellOverlayControlResult<AIArenaMatchSetupControlState> OpenMatchSetup(string? section)
    {
        var normalized = string.IsNullOrWhiteSpace(section) ? "scenario" : section.Trim().ToLowerInvariant();
        if (!MatchSetupSections.Contains(normalized))
        {
            return Failure(
                "invalid_argument",
                "match.setup.open requires args.section: scenario, cast, matrix, or saved.",
                matchSetupState());
        }

        showMatchSetup();
        if (!selectMatchSetupSection(normalized))
        {
            return Failure("not_available", $"Match Setup section '{normalized}' could not be selected.", matchSetupState());
        }

        return Success($"Match Setup opened to {normalized}.", matchSetupState());
    }

    public AIArenaShellOverlayControlResult<AIArenaMatchSetupControlState> CloseMatchSetup()
    {
        closeMatchSetup();
        return Success("Match Setup closed.", matchSetupState());
    }

    public AIArenaSettingsControlState CaptureSettings() => settingsState();

    public AIArenaShellOverlayControlResult<AIArenaSettingsControlState> OpenSettings(string? query)
    {
        showSettings();
        if (query is not null)
        {
            setSettingsSearch(NormalizeQuery(query));
        }

        return Success("Settings opened.", settingsState());
    }

    public AIArenaShellOverlayControlResult<AIArenaSettingsControlState> CloseSettings()
    {
        closeSettings();
        return Success("Settings closed.", settingsState());
    }

    public AIArenaShellOverlayControlResult<AIArenaSettingsControlState> SearchSettings(string? query)
    {
        var normalized = NormalizeQuery(query ?? "");
        showSettings();
        setSettingsSearch(normalized);
        var message = string.IsNullOrEmpty(normalized)
            ? "Settings search cleared."
            : $"Settings search updated: {normalized}.";
        return Success(message, settingsState());
    }

    internal static string NormalizeQuery(string query)
    {
        var normalized = string.Join(' ', (query ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized[..Math.Min(normalized.Length, 160)];
    }

    private static AIArenaShellOverlayControlResult<T> Success<T>(string message, T state) =>
        new(true, "", message, state);

    private static AIArenaShellOverlayControlResult<T> Failure<T>(string errorCode, string message, T state) =>
        new(false, errorCode, message, state);
}
