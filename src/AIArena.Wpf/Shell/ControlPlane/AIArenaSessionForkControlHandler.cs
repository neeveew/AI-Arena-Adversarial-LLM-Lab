namespace AIArena.Wpf;

/// <summary>
/// Thin protocol adapter for the shared current-match fork workflow.
/// </summary>
internal sealed class AIArenaSessionForkControlHandler
{
    private readonly SessionForkWorkflowService workflow;

    public AIArenaSessionForkControlHandler(SessionForkWorkflowService workflow)
    {
        this.workflow = workflow;
    }

    public bool CanHandle(string command)
    {
        return command.Equals(AIArenaControlCommands.SessionFork, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AIArenaControlResponse> ExecuteAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanHandle(request.Command))
        {
            return AIArenaControlResponse.Error(
                request,
                "not_implemented",
                $"Command '{request.Command}' is not handled by the session-fork control family.");
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "name", out var name))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "session.fork args.name must be a string when supplied.");
        }

        var result = await workflow.ForkCurrentAsync(name, cancellationToken);
        return result.Ok
            ? AIArenaControlResponse.Success(request, result.Message, result.Receipt)
            : AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result.Receipt);
    }
}
