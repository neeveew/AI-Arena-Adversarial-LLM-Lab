namespace AIArena.Wpf;

/// <summary>
/// Owns the Match Setup control-plane family while the WPF host supplies visual
/// transitions and the roster coordinator supplies the normal persistence path.
/// </summary>
internal sealed class AIArenaMatchSetupControlHandler
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        AIArenaControlCommands.MatchSetupState,
        AIArenaControlCommands.MatchSetupOpen,
        AIArenaControlCommands.MatchSetupClose,
        AIArenaControlCommands.MatchSetupExport,
        AIArenaControlCommands.MatchSetupImport,
        AIArenaControlCommands.MatchRosterSet,
        AIArenaControlCommands.MatchMatrixState,
        AIArenaControlCommands.MatchMatrixSet
    };

    private readonly ShellOverlayControlService overlays;
    private readonly Func<int, Task<AIArenaAgentRosterResizeResult>> resizeRoster;
    private readonly RivalryMatrixControlService matrix;
    private readonly MatchSetupPortabilityService portability;
    private readonly AIArenaControlPlaneEventHub events;

    public AIArenaMatchSetupControlHandler(
        ShellOverlayControlService overlays,
        Func<int, Task<AIArenaAgentRosterResizeResult>> resizeRoster,
        RivalryMatrixControlService matrix,
        MatchSetupPortabilityService portability,
        AIArenaControlPlaneEventHub events)
    {
        this.overlays = overlays;
        this.resizeRoster = resizeRoster;
        this.matrix = matrix;
        this.portability = portability;
        this.events = events;
    }

    public bool CanHandle(string command) => Commands.Contains(command);

    public async Task<AIArenaControlResponse> ExecuteAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken = default)
    {
        switch (request.Command)
        {
            case AIArenaControlCommands.MatchSetupState:
                return AIArenaControlResponse.Success(request, "Match Setup state captured.", overlays.CaptureMatchSetup());
            case AIArenaControlCommands.MatchSetupOpen:
                return OverlayResponse(request, overlays.OpenMatchSetup(AIArenaControlArguments.OptionalString(request, "section")));
            case AIArenaControlCommands.MatchSetupClose:
                return OverlayResponse(request, overlays.CloseMatchSetup());
            case AIArenaControlCommands.MatchSetupExport:
                return PortabilityResponse(request, await portability.ExportAsync(cancellationToken));
            case AIArenaControlCommands.MatchSetupImport:
                {
                    var packageJson = AIArenaControlArguments.String(request, "json");
                    var packagePath = AIArenaControlArguments.OptionalString(request, "path") ?? "";
                    var requestedName = AIArenaControlArguments.OptionalString(request, "name") ?? "";
                    var result = !string.IsNullOrWhiteSpace(packageJson)
                        ? await portability.ImportAsync(packageJson, requestedName, cancellationToken)
                        : await portability.ImportFileAsync(packagePath, requestedName, cancellationToken);
                    return PortabilityResponse(request, result);
                }
            case AIArenaControlCommands.MatchRosterSet:
                return await ResizeRosterAsync(request);
            case AIArenaControlCommands.MatchMatrixState:
                return AIArenaControlResponse.Success(request, "Relationship matrix state captured.", await matrix.CaptureAsync(cancellationToken));
            case AIArenaControlCommands.MatchMatrixSet:
                return await SetMatrixAsync(request, cancellationToken);
            default:
                return AIArenaControlResponse.Error(request, "unknown_command", $"Unsupported Match Setup command '{request.Command}'.");
        }
    }

    private async Task<AIArenaControlResponse> SetMatrixAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (!AIArenaControlArguments.TryOptionalBool(request, "enabled", out var enabled))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.enabled must be true or false.", await matrix.CaptureAsync(cancellationToken));
        }

        var result = await matrix.ApplyPatternAsync(
            AIArenaControlArguments.String(request, "pattern"),
            enabled ?? true,
            cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.State);
        }

        events.Publish("match.matrix.changed", result.Message, result.State);
        return AIArenaControlResponse.Success(request, result.Message, result.State);
    }

    private async Task<AIArenaControlResponse> ResizeRosterAsync(AIArenaControlRequest request)
    {
        if (!AIArenaControlArguments.TryRequiredInt(request, "count", out var count))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "match.roster.set requires integer args.count between 1 and 8.",
                overlays.CaptureMatchSetup());
        }

        var result = await resizeRoster(count);
        var state = overlays.CaptureMatchSetup();
        var data = new { state, requestedCount = count };
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, data);
        }

        events.Publish("match.roster.changed", result.Message, data);
        return AIArenaControlResponse.Success(request, result.Message, data);
    }

    private AIArenaControlResponse OverlayResponse(
        AIArenaControlRequest request,
        AIArenaShellOverlayControlResult<AIArenaMatchSetupControlState> result)
    {
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.State);
        }

        events.Publish("shell.overlay.changed", result.Message, result.State);
        return AIArenaControlResponse.Success(request, result.Message, result.State);
    }

    private AIArenaControlResponse PortabilityResponse(
        AIArenaControlRequest request,
        AIArenaMatchSetupPackageResult result)
    {
        var data = new { result.State, result.Receipt };
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, data);
        }

        events.Publish(
            result.Receipt?.Operation == "import" ? "match.setup.imported" : "match.setup.exported",
            result.Message,
            data);
        return AIArenaControlResponse.Success(request, result.Message, data);
    }
}
