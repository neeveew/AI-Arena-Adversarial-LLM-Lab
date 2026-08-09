namespace AIArena.Wpf;

internal sealed class AIArenaAppControlHandler
{
    private readonly AIArenaScreenshotControlService screenshots;
    private readonly AIArenaControlPlaneEventHub events;
    private readonly Action<AIArenaScreenshotControlResult>? onScreenshotCaptured;
    private readonly AIArenaUiVerificationControlService? verification;

    public AIArenaAppControlHandler(
        AIArenaScreenshotControlService screenshots,
        AIArenaControlPlaneEventHub events,
        Action<AIArenaScreenshotControlResult>? onScreenshotCaptured = null,
        AIArenaUiVerificationControlService? verification = null)
    {
        this.screenshots = screenshots;
        this.events = events;
        this.onScreenshotCaptured = onScreenshotCaptured;
        this.verification = verification;
    }

    public bool CanHandle(string command)
    {
        return command.Equals(AIArenaControlCommands.AppScreenshot, StringComparison.OrdinalIgnoreCase)
            || command.Equals(AIArenaControlCommands.AppQaWindowSize, StringComparison.OrdinalIgnoreCase)
            || command.Equals(AIArenaControlCommands.AppQaStructureCapture, StringComparison.OrdinalIgnoreCase)
            || command.Equals(AIArenaControlCommands.AppQaFocusAdvance, StringComparison.OrdinalIgnoreCase)
            || command.Equals(AIArenaControlCommands.AppQaFocusFeature, StringComparison.OrdinalIgnoreCase)
            || command.Equals(AIArenaControlCommands.AppQaMotionSet, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AIArenaControlResponse> ExecuteAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Command.Equals(AIArenaControlCommands.AppQaWindowSize, StringComparison.OrdinalIgnoreCase))
        {
            return await SetQaWindowSizeAsync(request, cancellationToken);
        }

        if (request.Command.Equals(AIArenaControlCommands.AppQaStructureCapture, StringComparison.OrdinalIgnoreCase))
        {
            return await CaptureQaStructureAsync(request, cancellationToken);
        }

        if (request.Command.Equals(AIArenaControlCommands.AppQaFocusAdvance, StringComparison.OrdinalIgnoreCase))
        {
            return await AdvanceQaFocusAsync(request, cancellationToken);
        }

        if (request.Command.Equals(AIArenaControlCommands.AppQaFocusFeature, StringComparison.OrdinalIgnoreCase))
        {
            return await FocusQaFeatureAsync(request, cancellationToken);
        }

        if (request.Command.Equals(AIArenaControlCommands.AppQaMotionSet, StringComparison.OrdinalIgnoreCase))
        {
            return await SetQaMotionAsync(request, cancellationToken);
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "path", out var path))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.path must be a string.");
        }

        if (!AIArenaControlArguments.TryOptionalDouble(request, "renderDpiScale", out var renderDpiScale))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "args.renderDpiScale must be numeric when supplied.");
        }

        var result = await screenshots.CaptureAsync(path, cancellationToken, renderDpiScale);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.screenshot.captured", result.Message, result);
        onScreenshotCaptured?.Invoke(result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }

    public void ResetProcessOverrides()
    {
        verification?.ResetProcessOverrides();
    }

    private async Task<AIArenaControlResponse> SetQaWindowSizeAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (verification is null)
        {
            return AIArenaControlResponse.Error(request, "not_available", "UI verification controls are not available.");
        }

        if (!AIArenaControlArguments.TryRequiredInt(request, "width", out var width)
            || !AIArenaControlArguments.TryRequiredInt(request, "height", out var height))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "app.qa.window.size requires integer args.width and args.height.");
        }

        var result = await verification.SetWindowSizeAsync(width, height, cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.qa.window.sized", result.Message, result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }

    private async Task<AIArenaControlResponse> CaptureQaStructureAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (verification is null)
        {
            return AIArenaControlResponse.Error(request, "not_available", "UI verification controls are not available.");
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "path", out var path))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.path must be a relative string.");
        }
        if (!AIArenaControlArguments.TryOptionalDouble(request, "renderDpiScale", out var renderDpiScale))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "args.renderDpiScale must be numeric when supplied.");
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "treeFingerprint", out var treeFingerprint, allowEmpty: false)
            || string.IsNullOrWhiteSpace(treeFingerprint)
            || !AIArenaControlArguments.TryOptionalString(request, "expectedState", out var expectedState, allowEmpty: false)
            || string.IsNullOrWhiteSpace(expectedState))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "app.qa.structure.capture requires string args.treeFingerprint and args.expectedState.");
        }

        var result = await verification.CaptureStructureAsync(
            path,
            treeFingerprint,
            expectedState,
            cancellationToken,
            renderDpiScale);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.qa.structure.captured", result.Message, result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }

    private async Task<AIArenaControlResponse> AdvanceQaFocusAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (verification is null)
        {
            return AIArenaControlResponse.Error(request, "not_available", "UI verification controls are not available.");
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "direction", out var direction, allowEmpty: false))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.direction must be a string.");
        }

        var result = await verification.AdvanceKeyboardFocusAsync(direction, cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.qa.focus.advanced", result.Message, result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }

    private async Task<AIArenaControlResponse> SetQaMotionAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (verification is null)
        {
            return AIArenaControlResponse.Error(request, "not_available", "UI verification controls are not available.");
        }

        if (!AIArenaControlArguments.TryOptionalString(request, "mode", out var mode, allowEmpty: false)
            || string.IsNullOrWhiteSpace(mode))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "app.qa.motion.set requires string args.mode.");
        }

        var result = await verification.SetMotionPreferenceAsync(mode, cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.qa.motion.changed", result.Message, result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }

    private async Task<AIArenaControlResponse> FocusQaFeatureAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (verification is null)
        {
            return AIArenaControlResponse.Error(request, "not_available", "UI verification controls are not available.");
        }

        var result = await verification.FocusSelectedFeatureContentAsync(cancellationToken);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, result);
        }

        events.Publish("app.qa.focus.feature", result.Message, result);
        return AIArenaControlResponse.Success(request, result.Message, result);
    }
}
