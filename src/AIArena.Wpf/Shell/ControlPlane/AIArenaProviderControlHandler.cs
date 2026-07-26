using AIArena.Core.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

/// <summary>
/// Owns the provider command family. Protocol parsing stays separate from
/// persistence and network diagnostics, and no command drives a WPF control.
/// </summary>
internal sealed class AIArenaProviderControlHandler
{
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        AIArenaControlCommands.ProviderState,
        AIArenaControlCommands.ProviderConfigSet,
        AIArenaControlCommands.ProviderModelSet,
        AIArenaControlCommands.ProviderTest,
        AIArenaControlCommands.ProviderModelsRefresh
    };

    private readonly ProviderConfigurationControlService configuration;
    private readonly ProviderRuntimeService runtime;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;
    private readonly AIArenaControlPlaneEventHub events;
    private ProviderDiagnosticsCache? diagnostics;

    public AIArenaProviderControlHandler(
        ProviderConfigurationControlService configuration,
        ProviderRuntimeService runtime,
        Func<SessionSummary?> activeSession,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        AIArenaControlPlaneEventHub events)
    {
        this.configuration = configuration;
        this.runtime = runtime;
        this.activeSession = activeSession;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.events = events;
    }

    public bool CanHandle(string command) => Commands.Contains(command);

    public async Task<AIArenaControlResponse> ExecuteAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.Command switch
        {
            AIArenaControlCommands.ProviderState => AIArenaControlResponse.Success(
                request,
                "Provider state captured.",
                Enrich(await configuration.CaptureAsync(cancellationToken))),
            AIArenaControlCommands.ProviderConfigSet => await ConfigureAsync(request, cancellationToken),
            AIArenaControlCommands.ProviderModelSet => await SetAllModelsAsync(request, cancellationToken),
            AIArenaControlCommands.ProviderTest => await TestAsync(request, cancellationToken),
            AIArenaControlCommands.ProviderModelsRefresh => await RefreshModelsAsync(request, cancellationToken),
            _ => AIArenaControlResponse.Error(request, "unknown_command", $"Unsupported provider command '{request.Command}'.")
        };
    }

    private async Task<AIArenaControlResponse> ConfigureAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        var parsed = TryParsePatch(request, out var patch, out var argumentError);
        if (!parsed)
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                argumentError,
                Enrich(await configuration.CaptureAsync(cancellationToken)));
        }

        var result = await configuration.ApplyAsync(patch!, cancellationToken);
        var state = Enrich(result.State);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, state);
        }

        var data = new
        {
            Changed = result.ChangedFields,
            Provider = state
        };
        events.Publish("provider.config.changed", result.Message, data);
        return AIArenaControlResponse.Success(request, result.Message, data);
    }

    private async Task<AIArenaControlResponse> SetAllModelsAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (!AIArenaControlArguments.TryOptionalString(request, "model", out var model, allowEmpty: false)
            || string.IsNullOrWhiteSpace(model))
        {
            return AIArenaControlResponse.Error(
                request,
                "missing_argument",
                "provider.model.set requires args.model.",
                Enrich(await configuration.CaptureAsync(cancellationToken)));
        }

        if (!AIArenaControlArguments.TryOptionalBool(request, "refreshModels", out var refreshModels))
        {
            return AIArenaControlResponse.Error(
                request,
                "invalid_argument",
                "args.refreshModels must be true or false.",
                Enrich(await configuration.CaptureAsync(cancellationToken)));
        }

        var roleModels = ProviderConfigurationControlService.RoleKeys.ToDictionary(
            role => role,
            _ => model,
            StringComparer.OrdinalIgnoreCase);
        var patch = new AIArenaProviderConfigurationPatch(
            BaseUrl: null,
            ApiMode: null,
            ApiToken: null,
            ClearApiToken: false,
            Model: model,
            TimeoutSeconds: null,
            Temperature: null,
            MaxOutputTokens: null,
            ContextLength: null,
            Reasoning: null,
            NativeStatefulChat: null,
            NativeIdleTtlSeconds: null,
            RoleModels: roleModels,
            RefreshModels: refreshModels == true);
        var result = await configuration.ApplyAsync(patch, cancellationToken);
        var state = Enrich(result.State);
        if (!result.Ok)
        {
            return AIArenaControlResponse.Error(request, result.ErrorCode, result.Message, state);
        }

        events.Publish("provider.model.changed", "Provider models changed.", new { model, Provider = state });
        return AIArenaControlResponse.Success(request, "Provider models changed.", state);
    }

    private async Task<AIArenaControlResponse> TestAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        if (!AIArenaControlArguments.TryOptionalBool(request, "allRoles", out var allRoles))
        {
            return AIArenaControlResponse.Error(request, "invalid_argument", "args.allRoles must be true or false.");
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromMinutes(5));
        ProviderRuntimeTestResult result;
        try
        {
            result = await runtime.TestAsync(activeSession()?.Id, allRoles == true, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AIArenaControlResponse.Error(
                request,
                "timeout",
                "Provider test exceeded the five-minute control-plane limit.");
        }
        if (!result.Available)
        {
            return AIArenaControlResponse.Error(request, "not_available", result.Status, result);
        }

        if (result.Busy)
        {
            return AIArenaControlResponse.Error(request, "busy", result.Status, result);
        }

        if (result.Persisted)
        {
            await refreshActiveSessionAsync(result.Status, cancellationToken);
        }

        var capturedState = await configuration.CaptureAsync(cancellationToken);
        var resultIsCurrent = result.Persisted
            && !result.Stale
            && DiagnosticIdentityMatches(result.CapturedSessionId, result.ConfigurationIdentity, capturedState);
        if (resultIsCurrent)
        {
            var previous = DiagnosticsFor(capturedState);
            diagnostics = new ProviderDiagnosticsCache(
                capturedState.SessionId,
                capturedState.ConfigurationIdentity,
                result.CheckedAt,
                previous?.LastModelListCheckedAt,
                previous?.AdvertisedModelCount ?? result.ModelCount,
                previous?.AdvertisedModels ?? []);
        }

        var state = Enrich(capturedState);
        var data = new
        {
            result.Ok,
            result.Reachable,
            result.Persisted,
            result.Stale,
            result.Status,
            result.BaseUrl,
            result.ApiMode,
            result.Model,
            result.Reply,
            result.LatencyMs,
            result.Error,
            result.CheckedAt,
            result.ModelCount,
            result.RoleResults,
            Provider = state
        };
        events.Publish("provider.test.completed", result.Status, data);
        // A completed diagnostic is a successful command even when the provider
        // reports a failed completion; data.Ok carries the diagnostic outcome.
        return AIArenaControlResponse.Success(request, result.Status, data);
    }

    private async Task<AIArenaControlResponse> RefreshModelsAsync(
        AIArenaControlRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(45));
        ProviderRuntimeModelsResult result;
        try
        {
            result = await runtime.RefreshModelsAsync(activeSession()?.Id, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AIArenaControlResponse.Error(
                request,
                "timeout",
                "Provider model discovery exceeded the 45-second control-plane limit.");
        }
        if (!result.Available)
        {
            return AIArenaControlResponse.Error(request, "not_available", result.Status, result);
        }

        if (result.Busy)
        {
            return AIArenaControlResponse.Error(request, "busy", result.Status, result);
        }

        var capturedState = await configuration.CaptureAsync(cancellationToken);
        var resultIsCurrent = DiagnosticIdentityMatches(
            result.CapturedSessionId,
            result.ConfigurationIdentity,
            capturedState);
        var staleMessage = "Provider configuration changed while model discovery was running; the stale catalog was discarded.";
        if (resultIsCurrent)
        {
            var previous = DiagnosticsFor(capturedState);
            diagnostics = new ProviderDiagnosticsCache(
                capturedState.SessionId,
                capturedState.ConfigurationIdentity,
                previous?.LastHealthCheckedAt,
                result.CheckedAt,
                result.ModelCount,
                result.Models);
        }

        var state = Enrich(capturedState);
        var data = new
        {
            Ok = result.Ok && resultIsCurrent,
            Status = resultIsCurrent ? result.Status : staleMessage,
            Stale = !resultIsCurrent,
            result.BaseUrl,
            result.ApiMode,
            result.Model,
            result.LatencyMs,
            result.CheckedAt,
            Count = resultIsCurrent ? result.ModelCount : 0,
            Truncated = resultIsCurrent && result.Truncated,
            Models = resultIsCurrent ? result.Models : [],
            Error = resultIsCurrent ? result.Error : staleMessage,
            Provider = state
        };
        var responseStatus = resultIsCurrent ? result.Status : staleMessage;
        events.Publish("provider.models.refreshed", responseStatus, data);
        return AIArenaControlResponse.Success(request, responseStatus, data);
    }

    private AIArenaProviderControlState Enrich(AIArenaProviderControlState state)
    {
        var current = DiagnosticsFor(state);
        if (current is null)
        {
            return state;
        }

        return state with
        {
            LastHealthCheckedAt = current.LastHealthCheckedAt,
            LastModelListCheckedAt = current.LastModelListCheckedAt,
            AdvertisedModelCount = current.AdvertisedModelCount,
            AdvertisedModels = current.AdvertisedModels
        };
    }

    private ProviderDiagnosticsCache? DiagnosticsFor(AIArenaProviderControlState state)
    {
        return diagnostics is { } current
            && current.SessionId.Equals(state.SessionId, StringComparison.OrdinalIgnoreCase)
            && current.ConfigurationIdentity.Equals(state.ConfigurationIdentity, StringComparison.Ordinal)
            ? current
            : null;
    }

    private static bool DiagnosticIdentityMatches(
        string sessionId,
        string configurationIdentity,
        AIArenaProviderControlState state)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && !string.IsNullOrWhiteSpace(configurationIdentity)
            && sessionId.Equals(state.SessionId, StringComparison.OrdinalIgnoreCase)
            && configurationIdentity.Equals(state.ConfigurationIdentity, StringComparison.Ordinal);
    }

    private static bool TryParsePatch(
        AIArenaControlRequest request,
        out AIArenaProviderConfigurationPatch? patch,
        out string error)
    {
        patch = null;
        error = "";
        if (!TryString(request, "baseUrl", out var baseUrl, out error)
            || !TryString(request, "apiMode", out var apiMode, out error)
            || !TryString(request, "apiToken", out var apiToken, out error)
            || !TryString(request, "model", out var model, out error)
            || !TryString(request, "reasoning", out var reasoning, out error))
        {
            return false;
        }

        var roleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in ProviderConfigurationControlService.RoleKeys)
        {
            var argument = $"{role}Model";
            if (!TryString(request, argument, out var roleModel, out error))
            {
                return false;
            }

            if (roleModel is not null)
            {
                roleModels[role] = roleModel;
            }
        }

        if (!AIArenaControlArguments.TryOptionalBool(request, "clearApiToken", out var clearApiToken))
        {
            error = "args.clearApiToken must be true or false.";
            return false;
        }

        if (!AIArenaControlArguments.TryOptionalBool(request, "nativeStatefulChat", out var nativeStatefulChat))
        {
            error = "args.nativeStatefulChat must be true or false.";
            return false;
        }

        if (!AIArenaControlArguments.TryOptionalBool(request, "refreshModels", out var refreshModels))
        {
            error = "args.refreshModels must be true or false.";
            return false;
        }

        if (!TryInt(request, "timeoutSeconds", out var timeoutSeconds, out error)
            || !TryDouble(request, "temperature", out var temperature, out error)
            || !TryInt(request, "maxOutputTokens", out var maxOutputTokens, out error)
            || !TryInt(request, "contextLength", out var contextLength, out error)
            || !TryInt(request, "nativeIdleTtlSeconds", out var nativeIdleTtlSeconds, out error))
        {
            return false;
        }

        patch = new AIArenaProviderConfigurationPatch(
            baseUrl,
            apiMode,
            apiToken,
            clearApiToken == true,
            model,
            timeoutSeconds,
            temperature,
            maxOutputTokens,
            contextLength,
            reasoning,
            nativeStatefulChat,
            nativeIdleTtlSeconds,
            roleModels,
            refreshModels == true);
        return true;
    }

    private static bool TryString(
        AIArenaControlRequest request,
        string name,
        out string? value,
        out string error)
    {
        if (AIArenaControlArguments.TryOptionalString(request, name, out value))
        {
            error = "";
            return true;
        }

        error = $"args.{name} must be a string.";
        return false;
    }

    private static bool TryInt(
        AIArenaControlRequest request,
        string name,
        out int? value,
        out string error)
    {
        if (AIArenaControlArguments.TryOptionalInt(request, name, out value))
        {
            error = "";
            return true;
        }

        error = $"args.{name} must be an integer.";
        return false;
    }

    private static bool TryDouble(
        AIArenaControlRequest request,
        string name,
        out double? value,
        out string error)
    {
        if (AIArenaControlArguments.TryOptionalDouble(request, name, out value))
        {
            error = "";
            return true;
        }

        error = $"args.{name} must be a number.";
        return false;
    }

    private sealed record ProviderDiagnosticsCache(
        string SessionId,
        string ConfigurationIdentity,
        DateTimeOffset? LastHealthCheckedAt,
        DateTimeOffset? LastModelListCheckedAt,
        int? AdvertisedModelCount,
        IReadOnlyList<string> AdvertisedModels);
}
