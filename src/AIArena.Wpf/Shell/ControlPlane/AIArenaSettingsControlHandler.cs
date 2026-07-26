namespace AIArena.Wpf;

internal sealed class AIArenaSettingsControlHandler
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        AIArenaControlCommands.SettingsState,
        AIArenaControlCommands.SettingsOpen,
        AIArenaControlCommands.SettingsClose,
        AIArenaControlCommands.SettingsSearch,
        AIArenaControlCommands.SettingsUpdate
    };

    private static readonly string[] BooleanArguments =
    [
        "compactTranscript",
        "followTranscript",
        "turnCompare",
        "matchTimeline",
        "battleReview",
        "memoryNotes",
        "decisionCard",
        "autoModerator",
        "styleFit",
        "internetDetails",
        "voiceEnabled",
        "worldEnabled",
        "agentWorkspaceEnabled"
    ];

    private readonly ShellOverlayControlService overlays;
    private readonly AppPreferenceControlService preferences;
    private readonly AIArenaControlPlaneEventHub events;

    public AIArenaSettingsControlHandler(
        ShellOverlayControlService overlays,
        AppPreferenceControlService preferences,
        AIArenaControlPlaneEventHub events)
    {
        this.overlays = overlays;
        this.preferences = preferences;
        this.events = events;
    }

    public bool CanHandle(string command) => Commands.Contains(command);

    public AIArenaControlResponse Execute(AIArenaControlRequest request)
    {
        return request.Command switch
        {
            AIArenaControlCommands.SettingsState => AIArenaControlResponse.Success(
                request,
                "Settings state captured.",
                overlays.CaptureSettings()),
            AIArenaControlCommands.SettingsOpen => OverlayResponse(
                request,
                overlays.OpenSettings(AIArenaControlArguments.OptionalString(request, "query"))),
            AIArenaControlCommands.SettingsClose => OverlayResponse(request, overlays.CloseSettings()),
            AIArenaControlCommands.SettingsSearch => OverlayResponse(
                request,
                overlays.SearchSettings(AIArenaControlArguments.OptionalString(request, "query"))),
            AIArenaControlCommands.SettingsUpdate => Update(request),
            _ => AIArenaControlResponse.Error(request, "unknown_command", $"Unsupported Settings command '{request.Command}'.")
        };
    }

    private AIArenaControlResponse Update(AIArenaControlRequest request)
    {
        var values = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BooleanArguments)
        {
            if (!AIArenaControlArguments.TryOptionalBool(request, name, out var value))
            {
                return AIArenaControlResponse.Error(
                    request,
                    "invalid_argument",
                    $"args.{name} must be true or false.",
                    overlays.CaptureSettings());
            }

            values[name] = value;
        }

        var result = preferences.Update(new AIArenaPreferencePatch(
            values["compactTranscript"],
            values["followTranscript"],
            AIArenaControlArguments.OptionalString(request, "topStripMode"),
            values["turnCompare"],
            values["matchTimeline"],
            values["battleReview"],
            values["memoryNotes"],
            values["decisionCard"],
            values["autoModerator"],
            values["styleFit"],
            values["internetDetails"],
            values["voiceEnabled"],
            values["worldEnabled"],
            values["agentWorkspaceEnabled"]));
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.Data);
        }

        events.Publish("settings.changed", result.Message, result.Data);
        return AIArenaControlResponse.Success(request, result.Message, result.Data);
    }

    private AIArenaControlResponse OverlayResponse(
        AIArenaControlRequest request,
        AIArenaShellOverlayControlResult<AIArenaSettingsControlState> result)
    {
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.State);
        }

        events.Publish("shell.overlay.changed", result.Message, result.State);
        return AIArenaControlResponse.Success(request, result.Message, result.State);
    }
}
