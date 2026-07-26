namespace AIArena.Wpf;

/// <summary>
/// Owns the Collaborate control-plane family so run control and review export
/// stay independently auditable from the main window command dispatcher.
/// </summary>
internal sealed class AIArenaCollaborateControlHandler
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        AIArenaControlCommands.CollaborateState,
        AIArenaControlCommands.CollaborateReview,
        AIArenaControlCommands.CollaborateSend,
        AIArenaControlCommands.CollaborateStop,
        AIArenaControlCommands.CollaborateFork,
        AIArenaControlCommands.CollaborateRepeat
    };

    private readonly CollaborateCoordinator collaborate;
    private readonly AIArenaControlPlaneEventHub events;

    public AIArenaCollaborateControlHandler(
        CollaborateCoordinator collaborate,
        AIArenaControlPlaneEventHub events)
    {
        this.collaborate = collaborate;
        this.events = events;
    }

    public bool CanHandle(string command) => Commands.Contains(command);

    public async Task<AIArenaControlResponse> ExecuteAsync(AIArenaControlRequest request)
    {
        switch (request.Command)
        {
            case AIArenaControlCommands.CollaborateState:
                return AIArenaControlResponse.Success(request, "Collaborate state captured.", collaborate.ControlState);
            case AIArenaControlCommands.CollaborateReview:
                return Review(request);
            case AIArenaControlCommands.CollaborateSend:
                return await SendAsync(request);
            case AIArenaControlCommands.CollaborateStop:
                collaborate.Stop();
                events.Publish("collaborate.stop.requested", "Collaborate stop requested.");
                return AIArenaControlResponse.Success(request, "Collaborate stop requested.", collaborate.ControlState);
            case AIArenaControlCommands.CollaborateFork:
                return MutateSavedRun(request, "fork", collaborate.ControlForkRecent);
            case AIArenaControlCommands.CollaborateRepeat:
                return MutateSavedRun(request, "repeat", collaborate.ControlRepeatRecent);
            default:
                return AIArenaControlResponse.Error(request, "unknown_command", $"Unsupported Collaborate command '{request.Command}'.");
        }
    }

    private AIArenaControlResponse Review(AIArenaControlRequest request)
    {
        var review = collaborate.CaptureControlReview(AIArenaControlArguments.OptionalString(request, "id") ?? "");
        return review.Available
            ? AIArenaControlResponse.Success(request, "Collaborate run review captured.", review)
            : AIArenaControlResponse.Error(request, "not_available", "No saved Collaborate run is available to review.", review);
    }

    private async Task<AIArenaControlResponse> SendAsync(AIArenaControlRequest request)
    {
        var prompt = AIArenaControlArguments.String(request, "prompt");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return AIArenaControlResponse.Error(request, "missing_argument", "collaborate.send requires args.prompt.");
        }

        await collaborate.ControlSendAsync(prompt);
        events.Publish("collaborate.prompt.sent", "Collaborate prompt sent.", new { promptLength = prompt.Length });
        return AIArenaControlResponse.Success(request, "Collaborate prompt sent.", collaborate.ControlState);
    }

    private AIArenaControlResponse MutateSavedRun(
        AIArenaControlRequest request,
        string action,
        Func<string, bool> mutation)
    {
        var changed = mutation(AIArenaControlArguments.OptionalString(request, "id") ?? "");
        if (!changed)
        {
            return AIArenaControlResponse.Error(
                request,
                "not_available",
                action == "fork"
                    ? "No saved Collaborate run is available to fork."
                    : "No saved Collaborate prompt is available to repeat.");
        }

        var eventType = action == "fork" ? "collaborate.forked" : "collaborate.repeated";
        var message = action == "fork" ? "Collaborate run forked." : "Collaborate prompt repeated.";
        events.Publish(eventType, message);
        return AIArenaControlResponse.Success(request, message, collaborate.ControlState);
    }
}
