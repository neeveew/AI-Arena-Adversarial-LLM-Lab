namespace AIArena.Wpf;

internal sealed class AIArenaAppControlHandler
{
    private readonly AIArenaScreenshotControlService screenshots;
    private readonly AIArenaControlPlaneEventHub events;
    private readonly Action<AIArenaScreenshotControlResult>? onScreenshotCaptured;

    public AIArenaAppControlHandler(
        AIArenaScreenshotControlService screenshots,
        AIArenaControlPlaneEventHub events,
        Action<AIArenaScreenshotControlResult>? onScreenshotCaptured = null)
    {
        this.screenshots = screenshots;
        this.events = events;
        this.onScreenshotCaptured = onScreenshotCaptured;
    }

    public bool CanHandle(string command)
    {
        return command.Equals(AIArenaControlCommands.AppScreenshot, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AIArenaControlResponse> ExecuteAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!AIArenaControlArguments.TryOptionalString(request, "path", out var path))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.path must be a string.");
        }

        var result = await screenshots.CaptureAsync(path, cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.screenshot.captured", result.Message, result);
        onScreenshotCaptured?.Invoke(result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }
}
