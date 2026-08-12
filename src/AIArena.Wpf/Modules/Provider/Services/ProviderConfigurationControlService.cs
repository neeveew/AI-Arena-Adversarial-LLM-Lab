using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf.Services;

internal sealed record AIArenaProviderConfigurationPatch(
    string? BaseUrl,
    string? ApiMode,
    string? ApiToken,
    bool ClearApiToken,
    string? Model,
    int? TimeoutSeconds,
    double? Temperature,
    int? MaxOutputTokens,
    int? ContextLength,
    string? Reasoning,
    bool? NativeStatefulChat,
    int? NativeIdleTtlSeconds,
    IReadOnlyDictionary<string, string> RoleModels,
    bool RefreshModels,
    bool? DefaultForUnassignedAgentsEnabled = null)
{
    public bool HasMutation =>
        BaseUrl is not null
        || ApiMode is not null
        || ApiToken is not null
        || ClearApiToken
        || Model is not null
        || TimeoutSeconds.HasValue
        || Temperature.HasValue
        || MaxOutputTokens.HasValue
        || ContextLength.HasValue
        || Reasoning is not null
        || NativeStatefulChat.HasValue
        || NativeIdleTtlSeconds.HasValue
        || DefaultForUnassignedAgentsEnabled.HasValue
        || RoleModels.Count > 0;
}

internal sealed record AIArenaProviderConfigurationControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaProviderControlState State,
    IReadOnlyList<string> ChangedFields,
    bool RefreshModelsRequested);

/// <summary>
/// Applies provider configuration without reading or writing WPF controls. The UI
/// and local control plane share the same snapshot policy, app-wide operation lock,
/// optimistic-concurrency retry, and secret-safe audit record.
/// </summary>
internal sealed class ProviderConfigurationControlService
{
    internal static readonly string[] RoleKeys = ["alpha", "beta", "gamma", "delta", "narrator"];

    private const int MaxBaseUrlLength = 2048;
    private const int MaxModelLength = 1024;
    private const int MaxApiTokenLength = 16 * 1024;
    private const int MaxSafeErrorLength = 512;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, bool, CancellationToken, Task> refreshHostAsync;

    public ProviderConfigurationControlService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        SemaphoreSlim arenaOperationLock,
        Func<SessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string, bool, CancellationToken, Task> refreshHostAsync)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.arenaOperationLock = arenaOperationLock;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.refreshHostAsync = refreshHostAsync;
    }

    public async Task<AIArenaProviderControlState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = activeSession();
        if (session is null)
        {
            return EmptyState();
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        return snapshot is null ? EmptyState(session.Id) : CaptureState(session.Id, snapshot);
    }

    public async Task<AIArenaProviderConfigurationControlResult> ApplyAsync(
        AIArenaProviderConfigurationPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var validation = Validate(patch);
        if (!validation.Ok)
        {
            return Failure(validation.ErrorCode, validation.Message, await CaptureAsync(cancellationToken));
        }

        if (isArenaBusy())
        {
            return Failure("busy", "Provider configuration cannot change while the arena is running.", await CaptureAsync(cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return Failure("not_available", "No active session is available.", EmptyState());
        }

        IReadOnlyList<string> changedFields = [];
        AIArenaProviderControlState savedState = EmptyState(session.Id);
        await arenaOperationLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changedFields = [];
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (snapshot is null)
                {
                    return Failure("not_available", $"No snapshot was found for session {session.Id}.", EmptyState(session.Id));
                }

                var beforeShared = SharedConfig(snapshot);
                var beforeConnectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(
                    session.Id,
                    beforeShared);
                changedFields = ApplyPatch(snapshot, patch);
                var afterConnectionIdentity = ProviderModelCatalogProjectionService.ConnectionFingerprint(
                    session.Id,
                    SharedConfig(snapshot));
                if (!beforeConnectionIdentity.Equals(afterConnectionIdentity, StringComparison.Ordinal))
                {
                    snapshot.PendingModelConfigurationApplies.Clear();
                }
                if (changedFields.Count == 0)
                {
                    savedState = CaptureState(session.Id, snapshot);
                    break;
                }

                try
                {
                    await sessionStore.SaveSnapshotAsync(snapshot, session.Id, cancellationToken);
                    savedState = CaptureState(session.Id, snapshot);
                    await eventLogStore.AppendAsync(session.Id, "provider_configuration_changed", new
                    {
                        ChangedFields = changedFields,
                        savedState.ApiTokenConfigured,
                        savedState.ApiMode,
                        savedState.Model
                    }, cancellationToken);
                    break;
                }
                catch (SnapshotConcurrencyException) when (attempt == 0)
                {
                    // Reload the newest revision and reapply the complete validated patch once.
                }
                catch (SnapshotConcurrencyException)
                {
                    return Failure(
                        "conflict",
                        "Provider configuration changed concurrently; refresh the state and retry.",
                        await CaptureAsync(cancellationToken));
                }
            }
        }
        finally
        {
            arenaOperationLock.Release();
        }

        var message = changedFields.Count == 0
            ? "Provider configuration already matched the requested values."
            : "Provider configuration saved.";
        await refreshHostAsync(message, patch.RefreshModels, cancellationToken);
        var refreshedState = await CaptureAsync(cancellationToken);
        return new AIArenaProviderConfigurationControlResult(
            true,
            "",
            message,
            refreshedState.SessionId.Length == 0 ? savedState : refreshedState,
            changedFields,
            patch.RefreshModels);
    }

    public async Task<ProviderModelAssignmentControlResult> SetModelAssignmentAsync(
        ProviderModelAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateAssignmentRequest(request);
        if (!validation.Ok)
        {
            return AssignmentFailure(
                validation.ErrorCode,
                validation.Message,
                await CaptureAssignmentAsync(request.Model, cancellationToken));
        }

        if (isArenaBusy())
        {
            return AssignmentFailure(
                "busy",
                "Model assignments cannot change while the arena is running.",
                await CaptureAssignmentAsync(request.Model, cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return AssignmentFailure(
                "not_available",
                "No active session is available.",
                ProviderModelAssignmentProjection.Empty(ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model)));
        }

        IReadOnlyList<string> changedFields = [];
        var savedProjection = ProviderModelAssignmentProjection.Empty(
            ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model));
        await arenaOperationLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                changedFields = [];
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (snapshot is null)
                {
                    return AssignmentFailure(
                        "not_available",
                        $"No snapshot was found for session {session.Id}.",
                        ProviderModelAssignmentProjection.Empty(ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model)));
                }

                var equivalentModels = (request.EquivalentModelIds ?? [])
                    .Append(request.Model)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .SelectMany(value => new[]
                    {
                        value.Trim(),
                        ProviderModelCatalogProjectionService.SafeModelIdentifier(value)
                    })
                    .Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool MatchesEquivalentModel(string value) =>
                    equivalentModels.Contains(value.Trim())
                    || equivalentModels.Contains(ProviderModelCatalogProjectionService.SafeModelIdentifier(value));
                var assignmentBatch = ProviderModelAssignmentProjectionService.CreateBatch(session.Id, snapshot);
                var before = assignmentBatch.Project(request.Model, equivalentModels);
                if (!before.ProviderFingerprint.Equals(request.ExpectedProviderFingerprint, StringComparison.Ordinal))
                {
                    return AssignmentFailure(
                        "stale_provider",
                        "The provider or default model changed; refresh the model list and retry.",
                        before);
                }

                var target = before.Targets.FirstOrDefault(item =>
                    item.Id.Equals(request.TargetId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    return AssignmentFailure(
                        "inactive_target",
                        "That agent is no longer active in this session.",
                        before);
                }

                if (!target.AssignmentFingerprint.Equals(request.ExpectedAssignmentFingerprint, StringComparison.Ordinal))
                {
                    return AssignmentFailure(
                        "conflict",
                        "That model assignment changed concurrently; refresh and retry.",
                        before);
                }

                var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
                    ? configured
                    : new ModelProviderConfig();
                var requestedModel = request.Model.Trim();
                if (target.IsDefault)
                {
                    if (!request.Assigned)
                    {
                        if (target.Assigned
                            && snapshot.Engine.DefaultForUnassignedAgentsEnabled
                            && MatchesEquivalentModel(shared.Model))
                        {
                            snapshot.Engine.DefaultForUnassignedAgentsEnabled = false;
                            changedFields = ["defaultForUnassignedAgentsEnabled"];
                        }
                        else
                        {
                            savedProjection = before;
                            break;
                        }
                    }
                    else
                    {
                        changedFields = ApplyPatch(snapshot, DefaultModelPatch(requestedModel, true));
                    }
                }
                else
                {
                    var configuredModel = ProviderModelAssignmentProjectionService.ConfiguredRoleModel(
                        snapshot.Configs,
                        target.Id,
                        shared);
                    // A checked role switch is always an explicit assignment,
                    // even when its model currently matches the shared model.
                    // The persisted explicit marker keeps that intent distinct
                    // from inheritance when the Arena default is later disabled.
                    var desiredModel = request.Assigned ? requestedModel : "";
                    if (!request.Assigned
                        && configuredModel.Length > 0
                        && !MatchesEquivalentModel(configuredModel))
                    {
                        return AssignmentFailure(
                            "conflict",
                            "That agent is assigned to a different model; refresh and retry.",
                            before);
                    }

                    if (configuredModel.Equals(desiredModel, StringComparison.Ordinal))
                    {
                        savedProjection = before;
                        break;
                    }

                    var (temperatureOverride, maxOutputTokensOverride) = GenerationOverrides(
                        snapshot.Configs,
                        target.Id,
                        shared);
                    SaveRoleModelConfig(
                        snapshot.Configs,
                        target.Id,
                        desiredModel,
                        shared,
                        temperatureOverride,
                        maxOutputTokensOverride,
                        explicitAssignment: request.Assigned && desiredModel.Length > 0);
                    changedFields = [$"{target.Id}Model"];
                }

                if (changedFields.Count == 0)
                {
                    savedProjection = assignmentBatch.Project(request.Model, equivalentModels);
                    break;
                }

                try
                {
                    await sessionStore.SaveSnapshotAsync(snapshot, session.Id, cancellationToken);
                    savedProjection = ProviderModelAssignmentProjectionService
                        .CreateBatch(session.Id, snapshot)
                        .Project(request.Model, equivalentModels);
                    await eventLogStore.AppendAsync(session.Id, "provider_model_assignment_changed", new
                    {
                        TargetId = target.Id,
                        target.IsDefault,
                        target.IsNarrator,
                        request.Assigned,
                        Model = ProviderModelCatalogProjectionService.SafeModelIdentifier(requestedModel),
                        ChangedFields = changedFields
                    }, cancellationToken);
                    break;
                }
                catch (SnapshotConcurrencyException) when (attempt == 0)
                {
                    // Reload and require the same provider and target assignment fingerprints.
                }
                catch (SnapshotConcurrencyException)
                {
                    return AssignmentFailure(
                        "conflict",
                        "Model assignments changed concurrently; refresh and retry.",
                        await CaptureAssignmentAsync(request.Model, cancellationToken));
                }
            }
        }
        finally
        {
            arenaOperationLock.Release();
        }

        var message = changedFields.Count == 0
            ? "Model assignment already matched the requested value."
            : request.TargetId.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                ? request.Assigned
                    ? "Default for unassigned agents enabled."
                    : "Default for unassigned agents disabled."
                : request.Assigned
                    ? "Agent model assignment saved."
                    : savedProjection.Targets.Any(target =>
                        target.Id.Equals(request.TargetId, StringComparison.OrdinalIgnoreCase)
                        && target.InheritsDefault)
                        ? "Agent now uses the default model."
                        : "Agent is now unassigned.";
        await refreshHostAsync(message, false, cancellationToken);
        var refreshedSnapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        var refreshedProjection = refreshedSnapshot is null
            ? savedProjection
            : ProviderModelAssignmentProjectionService
                .CreateBatch(session.Id, refreshedSnapshot)
                .Project(request.Model, request.EquivalentModelIds);
        return new ProviderModelAssignmentControlResult(
            true,
            "",
            message,
            refreshedProjection,
            changedFields);
    }

    public async Task<ProviderModelConfigurationControlResult> SetModelConfigurationAsync(
        ProviderModelConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateModelConfigurationRequest(request);
        if (!validation.Ok)
        {
            return ModelConfigurationFailure(
                validation.ErrorCode,
                validation.Message,
                await CaptureModelConfigurationAsync(request.Model, request.EquivalentModelIds, cancellationToken));
        }

        if (isArenaBusy())
        {
            return ModelConfigurationFailure(
                "busy",
                "Model configuration cannot change while the arena is running.",
                await CaptureModelConfigurationAsync(request.Model, request.EquivalentModelIds, cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return ModelConfigurationFailure(
                "not_available",
                "No active session is available.",
                ProviderModelConfigurationProjection.Empty(ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model)));
        }

        IReadOnlyList<string> changedFields = [];
        var saved = ProviderModelConfigurationProjection.Empty(
            ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model));
        await arenaOperationLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (snapshot is null)
                {
                    return ModelConfigurationFailure(
                        "not_available",
                        $"No snapshot was found for session {session.Id}.",
                        saved);
                }

                var aliases = EquivalentModels(request.Model, request.EquivalentModelIds);
                var before = CaptureModelConfiguration(session.Id, snapshot, request.Model, aliases);
                if (!before.ConfigurationIdentity.Equals(request.ExpectedConfigurationIdentity, StringComparison.Ordinal))
                {
                    return ModelConfigurationFailure(
                        "conflict",
                        "This model configuration or provider connection changed; refresh and retry.",
                        before);
                }

                var normalizedHistory = ModelHistoryPolicies.NormalizeHistoryPolicy(request.HistoryPolicy);
                var normalizedTone = ModelResponseTones.NormalizeResponseTone(request.ResponseTone);
                var normalizedCustomTone = normalizedTone == ModelResponseTones.Custom
                    ? ModelResponseTones.NormalizeCustomTone(request.CustomTone)
                    : "";
                changedFields = ChangedModelConfigurationFields(
                    before,
                    request.ConfiguredContextWindow,
                    normalizedHistory,
                    normalizedTone,
                    normalizedCustomTone).ToList();

                var shared = SharedConfig(snapshot);
                var rawRoutedAliases = snapshot.Configs.Values
                    .Where(config => !string.IsNullOrWhiteSpace(config.Model)
                        && aliases.Any(alias => ModelAliasesMatch(alias, config.Model)))
                    .Select(config => config.Model.Trim());
                var allAliases = aliases
                    .Concat(rawRoutedAliases)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(128)
                    .ToArray();
                var aliasConfigs = allAliases.Select(model => ConfigForModel(shared, model)).ToArray();
                var requiresAliasRepair = aliasConfigs.Any(config =>
                {
                    var identity = ModelRuntimeSettingsRegistry.Identity(config);
                    return !snapshot.ModelSettings.TryGetValue(identity, out var current)
                        || current.ConfiguredContextWindow != request.ConfiguredContextWindow
                        || !ModelHistoryPolicies.NormalizeHistoryPolicy(current.HistoryPolicy).Equals(normalizedHistory, StringComparison.Ordinal)
                        || !ModelResponseTones.NormalizeResponseTone(current.ResponseTone).Equals(normalizedTone, StringComparison.Ordinal)
                        || !ModelResponseTones.NormalizeCustomTone(current.CustomTone).Equals(normalizedCustomTone, StringComparison.Ordinal);
                });
                if (requiresAliasRepair && changedFields.Count == 0)
                {
                    changedFields = ["equivalentModelAliases"];
                }

                if (changedFields.Count == 0)
                {
                    saved = before;
                    break;
                }

                ModelRuntimeSettingsRegistry.RegisterAliases(
                    snapshot,
                    aliasConfigs,
                    request.ConfiguredContextWindow,
                    normalizedHistory,
                    normalizedTone,
                    normalizedCustomTone);
                if (changedFields.Contains("configuredContextWindow", StringComparer.Ordinal)
                    && request.ContextApplyRequired.HasValue)
                {
                    var pendingChanged = request.ContextApplyRequired.Value
                        ? ModelRuntimeSettingsRegistry.MarkPendingConfigurationApplies(snapshot, aliasConfigs)
                        : ModelRuntimeSettingsRegistry.ClearPendingConfigurationApplies(snapshot, aliasConfigs);
                    if (pendingChanged)
                    {
                        changedFields = changedFields
                            .Append("pendingConfigurationApply")
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                    }
                }
                try
                {
                    await sessionStore.SaveSnapshotAsync(snapshot, session.Id, cancellationToken);
                    saved = CaptureModelConfiguration(session.Id, snapshot, request.Model, aliases);
                    await eventLogStore.AppendAsync(session.Id, "provider_model_configuration_changed", new
                    {
                        Model = ProviderModelCatalogProjectionService.SafeModelIdentifier(request.Model),
                        ChangedFields = changedFields,
                        AliasCount = allAliases.Length,
                        ConfiguredContextWindow = request.ConfiguredContextWindow,
                        HistoryPolicy = normalizedHistory,
                        ResponseTone = normalizedTone
                    }, cancellationToken);
                    break;
                }
                catch (SnapshotConcurrencyException) when (attempt == 0)
                {
                    // Reload once and require the original per-model identity again.
                }
                catch (SnapshotConcurrencyException)
                {
                    return ModelConfigurationFailure(
                        "conflict",
                        "Model configuration changed concurrently; refresh and retry.",
                        await CaptureModelConfigurationAsync(request.Model, request.EquivalentModelIds, cancellationToken));
                }
            }
        }
        finally
        {
            arenaOperationLock.Release();
        }

        var message = changedFields.Count == 0
            ? "Model configuration already matched the requested values."
            : "Model configuration saved. Routing and model residency were unchanged.";
        await refreshHostAsync(message, false, cancellationToken);
        var refreshed = await CaptureModelConfigurationAsync(request.Model, request.EquivalentModelIds, cancellationToken);
        return new ProviderModelConfigurationControlResult(
            true,
            "",
            message,
            refreshed.SessionId.Length == 0 ? saved : refreshed,
            changedFields);
    }

    /// <summary>
    /// Clears durable apply intent only after the caller has authoritatively
    /// confirmed the loaded provider instance under the shared operation lock.
    /// </summary>
    internal async Task<bool> ConfirmModelConfigurationAppliedUnderLockAsync(
        string model,
        IReadOnlyList<string>? equivalentModelIds,
        string expectedConfigurationIdentity,
        CancellationToken cancellationToken)
    {
        var session = activeSession();
        if (session is null)
        {
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSession = activeSession();
            if (currentSession is null || !currentSession.Id.Equals(session.Id, StringComparison.Ordinal))
            {
                return false;
            }

            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
            if (snapshot is null)
            {
                return false;
            }

            var aliases = EquivalentModels(model, equivalentModelIds);
            var projection = CaptureModelConfiguration(session.Id, snapshot, model, aliases);
            if (!projection.ConfigurationIdentity.Equals(expectedConfigurationIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            var shared = SharedConfig(snapshot);
            var rawRoutedAliases = snapshot.Configs.Values
                .Where(config => !string.IsNullOrWhiteSpace(config.Model)
                    && aliases.Any(alias => ModelAliasesMatch(alias, config.Model)))
                .Select(config => config.Model.Trim());
            var aliasConfigs = aliases
                .Concat(rawRoutedAliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .Select(alias => ConfigForModel(shared, alias))
                .ToArray();
            if (!ModelRuntimeSettingsRegistry.ClearPendingConfigurationApplies(snapshot, aliasConfigs))
            {
                return true;
            }

            try
            {
                await sessionStore.SaveSnapshotAsync(snapshot, session.Id, cancellationToken);
                await eventLogStore.AppendAsync(session.Id, "provider_model_configuration_applied", new
                {
                    Model = ProviderModelCatalogProjectionService.SafeModelIdentifier(model),
                    AliasCount = aliasConfigs.Length
                }, cancellationToken);
                return true;
            }
            catch (SnapshotConcurrencyException) when (attempt == 0)
            {
                // Reload once; the same expected configuration identity remains mandatory.
            }
            catch (SnapshotConcurrencyException)
            {
                return false;
            }
        }

        return false;
    }

    internal static (bool Ok, string ErrorCode, string Message) Validate(AIArenaProviderConfigurationPatch patch)
    {
        if (!patch.HasMutation)
        {
            return (false, "missing_argument", "provider.config.set requires at least one configuration value; refreshModels alone is not a change.");
        }

        if (patch.ApiToken is not null && patch.ClearApiToken)
        {
            return (false, "invalid_argument", "args.apiToken and args.clearApiToken cannot be used together.");
        }

        if (patch.BaseUrl is not null && !TryValidateBaseUrl(patch.BaseUrl, out var baseUrlError))
        {
            return (false, "invalid_argument", baseUrlError);
        }

        if (patch.ApiMode is not null && !IsSupportedApiMode(patch.ApiMode))
        {
            return (false, "invalid_argument", "args.apiMode must be openai_compatible, lmstudio_native, ollama_native, or llamacpp_native.");
        }

        if (patch.ApiToken is not null
            && (string.IsNullOrWhiteSpace(patch.ApiToken)
                || patch.ApiToken.Length > MaxApiTokenLength
                || patch.ApiToken.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            return (false, "invalid_argument", "args.apiToken must be non-empty, at most 16384 characters, and contain no NUL or line breaks.");
        }

        if (patch.Model is not null && !TryValidateModel(patch.Model, allowEmpty: true))
        {
            return (false, "invalid_argument", "args.model must be at most 1024 characters and contain no control characters.");
        }

        foreach (var (role, model) in patch.RoleModels)
        {
            if (!IsKnownAssignmentTarget(role, includeDefault: false))
            {
                return (false, "invalid_argument", $"Unknown provider role '{role}'.");
            }

            if (!TryValidateModel(model, allowEmpty: true))
            {
                return (false, "invalid_argument", $"args.{role}Model must be at most 1024 characters and contain no control characters.");
            }
        }

        if (patch.TimeoutSeconds is int timeout && timeout is < 1 or > 3600)
        {
            return (false, "invalid_argument", "args.timeoutSeconds must be between 1 and 3600.");
        }

        if (patch.Temperature is double temperature
            && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
        {
            return (false, "invalid_argument", "args.temperature must be a finite number between 0 and 2.");
        }

        if (patch.MaxOutputTokens is int maxOutputTokens && maxOutputTokens is < 1 or > 32768)
        {
            return (false, "invalid_argument", "args.maxOutputTokens must be between 1 and 32768.");
        }

        if (patch.ContextLength is int contextLength && contextLength is < 0 or > 1_048_576)
        {
            return (false, "invalid_argument", "args.contextLength must be between 0 and 1048576.");
        }

        if (patch.Reasoning is not null && !IsSupportedReasoning(patch.Reasoning))
        {
            return (false, "invalid_argument", "args.reasoning must be default, off, low, medium, high, or on.");
        }

        if (patch.NativeIdleTtlSeconds is int ttl && ttl is < 0 or > 86_400)
        {
            return (false, "invalid_argument", "args.nativeIdleTtlSeconds must be between 0 and 86400.");
        }

        return (true, "", "");
    }

    internal static IReadOnlyList<string> ApplyPatch(ArenaSnapshot snapshot, AIArenaProviderConfigurationPatch patch)
    {
        ModelRuntimeSettingsRegistry.Normalize(snapshot);
        var roleKeys = ConfigurationRoleKeys(snapshot)
            .Concat(patch.RoleModels.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existingShared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var shared)
            ? shared
            : new ModelProviderConfig();
        var resolvedExistingShared = ModelRuntimeSettingsRegistry.Resolve(snapshot, existingShared);
        var existingRoleModels = roleKeys.ToDictionary(
            role => role,
            role => ConfiguredRoleModel(snapshot.Configs, role, existingShared),
            StringComparer.OrdinalIgnoreCase);
        var roleOverrides = roleKeys.ToDictionary(
            role => role,
            role => GenerationOverrides(snapshot.Configs, role, existingShared),
            StringComparer.OrdinalIgnoreCase);

        var baseUrl = patch.BaseUrl is null
            ? existingShared.BaseUrl
            : ModelProviderHealthService.NormalizeBaseUrl(patch.BaseUrl.Trim());
        var apiMode = patch.ApiMode is null ? existingShared.ApiMode : patch.ApiMode.Trim().ToLowerInvariant();
        var apiToken = patch.ClearApiToken ? "" : patch.ApiToken ?? existingShared.ApiToken;
        var model = patch.Model is null ? existingShared.Model : patch.Model.Trim();
        var timeout = patch.TimeoutSeconds ?? existingShared.Timeout;
        var temperature = patch.Temperature ?? existingShared.Temperature;
        var maxOutputTokens = patch.MaxOutputTokens ?? existingShared.MaxOutputTokens;
        var contextLength = patch.ContextLength ?? existingShared.ContextLength;
        var reasoning = patch.Reasoning is null
            ? ModelProviderReasoningModes.Normalize(existingShared.Reasoning)
            : NormalizeReasoning(patch.Reasoning);
        var nativeStatefulChat = patch.NativeStatefulChat ?? existingShared.NativeStatefulChat;
        var nativeIdleTtlSeconds = patch.NativeIdleTtlSeconds ?? existingShared.NativeIdleTtlSeconds;
        var defaultForUnassignedAgentsEnabled = patch.DefaultForUnassignedAgentsEnabled
            ?? snapshot.Engine.DefaultForUnassignedAgentsEnabled;
        var readinessChanged = ProviderReadinessChanged(
            existingShared,
            baseUrl,
            apiMode,
            apiToken,
            model,
            contextLength,
            reasoning,
            nativeStatefulChat,
            nativeIdleTtlSeconds);
        var sameModelIdentity = ModelRuntimeSettingsRegistry.Identity(existingShared).Equals(
            ModelRuntimeSettingsRegistry.Identity(ConfigForModel(new ModelProviderConfig
            {
                BaseUrl = baseUrl,
                ApiMode = apiMode,
                Model = model
            }, model)),
            StringComparison.Ordinal);
        var updatedShared = new ModelProviderConfig
        {
            BaseUrl = baseUrl,
            ApiMode = apiMode,
            ApiToken = apiToken,
            Model = model,
            ExplicitModelAssignment = false,
            Timeout = timeout,
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            ContextLength = contextLength,
            ConfiguredContextWindow = sameModelIdentity ? resolvedExistingShared.ConfiguredContextWindow : 0,
            HistoryPolicy = sameModelIdentity
                ? resolvedExistingShared.HistoryPolicy
                : ModelRuntimeSettingsRegistry.DefaultHistoryPolicy(snapshot),
            ResponseTone = sameModelIdentity ? resolvedExistingShared.ResponseTone : ModelResponseTones.Default,
            CustomTone = sameModelIdentity ? resolvedExistingShared.CustomTone : "",
            Reasoning = reasoning,
            NativeStatefulChat = nativeStatefulChat,
            NativeIdleTtlSeconds = nativeIdleTtlSeconds,
            LastError = readinessChanged ? "" : existingShared.LastError,
            LastLatencyMs = readinessChanged ? 0 : existingShared.LastLatencyMs,
            LastTestOk = !readinessChanged && existingShared.LastTestOk,
            Extra = existingShared.Extra
        };

        var changed = ChangedSharedFields(existingShared, updatedShared, patch);
        if (snapshot.Engine.DefaultForUnassignedAgentsEnabled != defaultForUnassignedAgentsEnabled)
        {
            changed.Add("defaultForUnassignedAgentsEnabled");
        }

        snapshot.Engine.DefaultForUnassignedAgentsEnabled = defaultForUnassignedAgentsEnabled;
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = updatedShared;
        foreach (var role in roleKeys)
        {
            var configuredModel = patch.RoleModels.TryGetValue(role, out var requestedModel)
                ? requestedModel.Trim()
                : existingRoleModels[role];
            if (patch.RoleModels.ContainsKey(role)
                && !configuredModel.Equals(existingRoleModels[role], StringComparison.Ordinal))
            {
                changed.Add($"{role}Model");
            }

            var (temperatureOverride, maxOutputTokensOverride) = roleOverrides[role];
            SaveRoleModelConfig(
                snapshot.Configs,
                role,
                configuredModel,
                updatedShared,
                temperatureOverride,
                maxOutputTokensOverride);
        }

        ModelRuntimeSettingsRegistry.Normalize(snapshot);

        return changed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void SaveRoleModelConfig(
        IDictionary<string, ModelProviderConfig> configs,
        string role,
        string model,
        ModelProviderConfig shared,
        double? temperatureOverride = null,
        int? maxOutputTokensOverride = null,
        bool? explicitAssignment = null)
    {
        var roleIsExplicit = explicitAssignment
            ?? !string.IsNullOrWhiteSpace(model);
        if (string.IsNullOrWhiteSpace(model))
        {
            if (!temperatureOverride.HasValue && !maxOutputTokensOverride.HasValue)
            {
                configs.Remove(role);
                return;
            }

            model = shared.Model;
            roleIsExplicit = false;
        }

        var existing = configs.TryGetValue(role, out var current) ? current : null;
        var sameAsSharedModel = model.Trim().Equals(shared.Model.Trim(), StringComparison.Ordinal);
        var readinessChanged = existing is null || ProviderReadinessChanged(
            existing,
            shared.BaseUrl,
            shared.ApiMode,
            shared.ApiToken,
            model,
            shared.ContextLength,
            shared.Reasoning,
            shared.NativeStatefulChat,
            shared.NativeIdleTtlSeconds);
        configs[role] = new ModelProviderConfig
        {
            BaseUrl = shared.BaseUrl,
            ApiMode = shared.ApiMode,
            ApiToken = shared.ApiToken,
            Model = model,
            ExplicitModelAssignment = roleIsExplicit,
            Timeout = shared.Timeout,
            Temperature = temperatureOverride ?? shared.Temperature,
            MaxOutputTokens = maxOutputTokensOverride ?? shared.MaxOutputTokens,
            ContextLength = shared.ContextLength,
            ConfiguredContextWindow = sameAsSharedModel ? shared.ConfiguredContextWindow : 0,
            HistoryPolicy = shared.HistoryPolicy,
            ResponseTone = sameAsSharedModel ? shared.ResponseTone : ModelResponseTones.Default,
            CustomTone = sameAsSharedModel ? shared.CustomTone : "",
            Reasoning = shared.Reasoning,
            NativeStatefulChat = shared.NativeStatefulChat,
            NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds,
            LastError = readinessChanged ? "" : existing!.LastError,
            LastLatencyMs = readinessChanged ? 0 : existing!.LastLatencyMs,
            LastTestOk = !readinessChanged && existing!.LastTestOk,
            Extra = existing?.Extra
        };
    }

    internal static bool ProviderReadinessChanged(
        ModelProviderConfig existing,
        string baseUrl,
        string apiMode,
        string apiToken,
        string model,
        int contextLength,
        string reasoning,
        bool nativeStatefulChat,
        int nativeIdleTtlSeconds)
    {
        var identityChanged = !existing.BaseUrl.Trim().Equals(baseUrl.Trim(), StringComparison.Ordinal)
            || !ModelProviderApiModes.Normalize(existing.ApiMode).Equals(ModelProviderApiModes.Normalize(apiMode), StringComparison.OrdinalIgnoreCase)
            || !existing.ApiToken.Equals(apiToken, StringComparison.Ordinal)
            || !existing.Model.Trim().Equals(model.Trim(), StringComparison.Ordinal);
        if (identityChanged)
        {
            return true;
        }

        if (!ModelProviderApiModes.IsLmStudioNative(apiMode)
            && !ModelProviderApiModes.IsOllamaNative(apiMode))
        {
            // llama.cpp startup-time context/GPU configuration is inspected,
            // not sent as a per-request option by AI Arena. Generic compatible
            // providers likewise do not use these legacy native controls.
            return false;
        }

        var nativeOptionChanged = existing.ContextLength != contextLength
            || !ModelProviderReasoningModes.Normalize(existing.Reasoning).Equals(ModelProviderReasoningModes.Normalize(reasoning), StringComparison.OrdinalIgnoreCase)
            || existing.NativeIdleTtlSeconds != nativeIdleTtlSeconds;
        return ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? nativeOptionChanged || existing.NativeStatefulChat != nativeStatefulChat
            : nativeOptionChanged;
    }

    internal static AIArenaProviderControlState CaptureState(string sessionId, ArenaSnapshot snapshot)
    {
        var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
            ? configured
            : snapshot.Configs.Values.FirstOrDefault() ?? new ModelProviderConfig();
        var roles = ConfigurationRoleKeys(snapshot).Select(role =>
        {
            var configuredModel = ConfiguredRoleModel(snapshot.Configs, role, shared);
            var effectiveModel = configuredModel.Length > 0
                ? configuredModel
                : snapshot.Engine.DefaultForUnassignedAgentsEnabled
                    ? shared.Model.Trim()
                    : "";
            var (temperatureOverride, maxOutputTokensOverride) = GenerationOverrides(snapshot.Configs, role, shared);
            return new AIArenaProviderRoleControlState(
                role,
                configuredModel,
                effectiveModel,
                snapshot.Engine.DefaultForUnassignedAgentsEnabled
                    && configuredModel.Length == 0
                    && !string.IsNullOrWhiteSpace(shared.Model),
                temperatureOverride,
                maxOutputTokensOverride);
        }).ToArray();
        string Effective(string role) => roles.First(item => item.Id.Equals(role, StringComparison.OrdinalIgnoreCase)).EffectiveModel;
        return new AIArenaProviderControlState(
            shared.LastTestOk,
            shared.Model.Trim(),
            Effective("alpha"),
            Effective("beta"),
            Effective("gamma"),
            Effective("delta"),
            Effective("narrator"),
            SanitizeError(shared.LastError, shared.ApiToken))
        {
            ConfigurationIdentity = ConfigurationIdentity(snapshot),
            SessionId = sessionId,
            PersistenceRevision = snapshot.PersistenceRevision,
            Configured = !string.IsNullOrWhiteSpace(shared.BaseUrl),
            BaseUrl = SanitizeBaseUrl(shared.BaseUrl),
            ApiMode = ModelProviderApiModes.Normalize(shared.ApiMode),
            ApiTokenConfigured = !string.IsNullOrEmpty(shared.ApiToken),
            TimeoutSeconds = shared.Timeout,
            Temperature = shared.Temperature,
            MaxOutputTokens = shared.MaxOutputTokens,
            ContextLength = shared.ContextLength,
            Reasoning = string.IsNullOrWhiteSpace(shared.Reasoning) ? "default" : ModelProviderReasoningModes.Normalize(shared.Reasoning),
            NativeStatefulChat = shared.NativeStatefulChat,
            NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds,
            DefaultForUnassignedAgentsEnabled = snapshot.Engine.DefaultForUnassignedAgentsEnabled,
            LastTestOk = shared.LastTestOk,
            LastLatencyMs = shared.LastLatencyMs,
            Roles = roles,
            ModelSettings = snapshot.Configs.Values
                .Where(config => !string.IsNullOrWhiteSpace(config.Model))
                .GroupBy(config => ModelRuntimeSettingsRegistry.Identity(config), StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(64)
                .Select(config =>
                {
                    var model = ProviderModelCatalogProjectionService.SafeModelIdentifier(config.Model);
                    var projected = CaptureModelConfiguration(sessionId, snapshot, model, [config.Model]);
                    return new AIArenaProviderModelSettingsControlState(
                        model,
                        projected.ConfiguredContextWindow,
                        projected.HistoryPolicy,
                        projected.ResponseTone,
                        projected.CustomTone,
                        projected.ConfigurationIdentity);
                })
                .ToArray()
        };
    }

    internal static AIArenaProviderControlState EmptyState(string sessionId = "")
    {
        return new AIArenaProviderControlState(false, "", "", "", "", "", "", "")
        {
            SessionId = sessionId,
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            Reasoning = "default",
            Roles = []
        };
    }

    internal static string ConfigurationIdentity(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendIdentityValue(hash, snapshot.Engine.DefaultForUnassignedAgentsEnabled ? "1" : "0");
        AppendIdentityValue(hash, snapshot.ModelSettingsVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var key in new[] { ModelProviderRouting.SharedConfigKey }.Concat(ConfigurationRoleKeys(snapshot)))
        {
            AppendIdentityValue(hash, key);
            if (!snapshot.Configs.TryGetValue(key, out var config))
            {
                AppendIdentityValue(hash, "<missing>");
                continue;
            }

            AppendIdentityValue(hash, config.BaseUrl.Trim().TrimEnd('/'));
            AppendIdentityValue(hash, ModelProviderApiModes.Normalize(config.ApiMode));
            AppendIdentityValue(hash, config.ApiToken);
            AppendIdentityValue(hash, config.Model.Trim());
            AppendIdentityValue(hash, config.ExplicitModelAssignment ? "1" : "0");
            AppendIdentityValue(hash, config.Timeout.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.Temperature.ToString("R", CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.ContextLength.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, ModelProviderReasoningModes.Normalize(config.Reasoning));
            AppendIdentityValue(hash, config.NativeStatefulChat ? "1" : "0");
            AppendIdentityValue(hash, config.NativeIdleTtlSeconds.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (identity, settings) in snapshot.ModelSettings.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            AppendIdentityValue(hash, identity);
            AppendIdentityValue(hash, settings.ConfiguredContextWindow.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, ModelHistoryPolicies.NormalizeHistoryPolicy(settings.HistoryPolicy));
            AppendIdentityValue(hash, ModelResponseTones.NormalizeResponseTone(settings.ResponseTone));
            AppendIdentityValue(hash, ModelResponseTones.NormalizeCustomTone(settings.CustomTone));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendIdentityValue(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    internal static string SanitizeBaseUrl(string value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Regex.Replace(text, @"(?i)(https?://)[^/@\s]+@", "$1[redacted]@").Split(['?', '#'])[0];
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    internal static string SanitizeError(string value, string apiToken)
    {
        var message = value ?? "";
        if (!string.IsNullOrEmpty(apiToken))
        {
            message = message.Replace(apiToken, "[redacted]", StringComparison.Ordinal);
        }

        message = Regex.Replace(
            message,
            @"(?i)\bhttps?://[^\s<>""']+",
            match => SanitizeBaseUrl(match.Value));
        message = Regex.Replace(message, @"(?i)(bearer\s+)[^\s,;]+", "$1[redacted]");
        message = Regex.Replace(message, @"(?i)((?:api[_-]?key|token|authorization)\s*[:=]\s*)[^\s,;]+", "$1[redacted]");
        message = Regex.Replace(message, @"(?i)(https?://)[^/@\s]+@", "$1");
        if (message.Length > MaxSafeErrorLength)
        {
            message = message[..MaxSafeErrorLength] + "...";
        }

        return message;
    }

    private static AIArenaProviderConfigurationControlResult Failure(
        string errorCode,
        string message,
        AIArenaProviderControlState state)
    {
        return new AIArenaProviderConfigurationControlResult(false, errorCode, message, state, [], false);
    }

    private static List<string> ChangedSharedFields(
        ModelProviderConfig before,
        ModelProviderConfig after,
        AIArenaProviderConfigurationPatch patch)
    {
        var changed = new List<string>();
        Add(patch.BaseUrl is not null && !before.BaseUrl.Equals(after.BaseUrl, StringComparison.Ordinal), "baseUrl");
        Add(patch.ApiMode is not null && !before.ApiMode.Equals(after.ApiMode, StringComparison.OrdinalIgnoreCase), "apiMode");
        Add((patch.ApiToken is not null || patch.ClearApiToken) && !before.ApiToken.Equals(after.ApiToken, StringComparison.Ordinal), "apiToken");
        Add(patch.Model is not null && !before.Model.Equals(after.Model, StringComparison.Ordinal), "model");
        Add(patch.TimeoutSeconds.HasValue && before.Timeout != after.Timeout, "timeoutSeconds");
        Add(patch.Temperature.HasValue && before.Temperature != after.Temperature, "temperature");
        Add(patch.MaxOutputTokens.HasValue && before.MaxOutputTokens != after.MaxOutputTokens, "maxOutputTokens");
        Add(patch.ContextLength.HasValue && before.ContextLength != after.ContextLength, "contextLength");
        Add(patch.Reasoning is not null && !ModelProviderReasoningModes.Normalize(before.Reasoning).Equals(after.Reasoning, StringComparison.OrdinalIgnoreCase), "reasoning");
        Add(patch.NativeStatefulChat.HasValue && before.NativeStatefulChat != after.NativeStatefulChat, "nativeStatefulChat");
        Add(patch.NativeIdleTtlSeconds.HasValue && before.NativeIdleTtlSeconds != after.NativeIdleTtlSeconds, "nativeIdleTtlSeconds");
        return changed;

        void Add(bool condition, string field)
        {
            if (condition)
            {
                changed.Add(field);
            }
        }
    }

    private static (double? Temperature, int? MaxOutputTokens) GenerationOverrides(
        IReadOnlyDictionary<string, ModelProviderConfig> configs,
        string role,
        ModelProviderConfig shared)
    {
        if (!configs.TryGetValue(role, out var config))
        {
            return (null, null);
        }

        return (
            Math.Abs(config.Temperature - shared.Temperature) > 0.000_001 ? config.Temperature : null,
            config.MaxOutputTokens != shared.MaxOutputTokens ? config.MaxOutputTokens : null);
    }

    private static string ConfiguredRoleModel(
        IReadOnlyDictionary<string, ModelProviderConfig> configs,
        string role,
        ModelProviderConfig shared)
    {
        return ProviderModelAssignmentProjectionService.ConfiguredRoleModel(configs, role, shared);
    }

    private async Task<ProviderModelAssignmentProjection> CaptureAssignmentAsync(
        string model,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = activeSession();
        if (session is null)
        {
            return ProviderModelAssignmentProjection.Empty(
                ProviderModelCatalogProjectionService.SafeModelIdentifier(model));
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        return snapshot is null
            ? ProviderModelAssignmentProjection.Empty(ProviderModelCatalogProjectionService.SafeModelIdentifier(model))
            : ProviderModelAssignmentProjectionService.Project(session.Id, snapshot, model);
    }

    internal async Task<ProviderModelConfigurationProjection> CaptureModelConfigurationAsync(
        string model,
        IReadOnlyList<string>? equivalentModelIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = activeSession();
        if (session is null)
        {
            return ProviderModelConfigurationProjection.Empty(
                ProviderModelCatalogProjectionService.SafeModelIdentifier(model));
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        return snapshot is null
            ? ProviderModelConfigurationProjection.Empty(ProviderModelCatalogProjectionService.SafeModelIdentifier(model))
            : CaptureModelConfiguration(
                session.Id,
                snapshot,
                model,
                EquivalentModels(model, equivalentModelIds));
    }

    internal static ProviderModelConfigurationProjection CaptureModelConfiguration(
        string sessionId,
        ArenaSnapshot snapshot,
        string model,
        IReadOnlyList<string>? equivalentModelIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var shared = SharedConfig(snapshot);
        var aliases = EquivalentModels(model, equivalentModelIds);
        ModelRuntimeSettings? settings = null;
        foreach (var alias in aliases)
        {
            var identity = ModelRuntimeSettingsRegistry.Identity(ConfigForModel(shared, alias));
            if (snapshot.ModelSettings.TryGetValue(identity, out settings))
            {
                break;
            }
        }

        settings ??= new ModelRuntimeSettings
        {
            ConfiguredContextWindow = 0,
            HistoryPolicy = ModelRuntimeSettingsRegistry.DefaultHistoryPolicy(snapshot),
            ResponseTone = ModelResponseTones.Default,
            CustomTone = ""
        };
        var history = ModelHistoryPolicies.NormalizeHistoryPolicy(settings.HistoryPolicy);
        var tone = ModelResponseTones.NormalizeResponseTone(settings.ResponseTone);
        var customTone = tone == ModelResponseTones.Custom
            ? ModelResponseTones.NormalizeCustomTone(settings.CustomTone)
            : "";
        var contextWindow = ModelRuntimeSettingsRegistry.ClampConfiguredContextWindow(settings.ConfiguredContextWindow);
        var identityHash = ModelConfigurationIdentity(
            sessionId,
            shared,
            aliases,
            contextWindow,
            history,
            tone,
            customTone);
        return new ProviderModelConfigurationProjection(
            sessionId,
            ProviderModelCatalogProjectionService.SafeModelIdentifier(model),
            contextWindow,
            history,
            tone,
            customTone,
            identityHash,
            snapshot.PersistenceRevision);
    }

    private static string ModelConfigurationIdentity(
        string sessionId,
        ModelProviderConfig shared,
        IReadOnlyList<string> aliases,
        int contextWindow,
        string historyPolicy,
        string responseTone,
        string customTone)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendIdentityValue(hash, sessionId);
        AppendIdentityValue(hash, ProviderModelCatalogProjectionService.ConnectionFingerprint(sessionId, shared));
        foreach (var identity in aliases
                     .Select(alias => ModelRuntimeSettingsRegistry.Identity(ConfigForModel(shared, alias)))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            AppendIdentityValue(hash, identity);
        }
        AppendIdentityValue(hash, contextWindow.ToString(CultureInfo.InvariantCulture));
        AppendIdentityValue(hash, historyPolicy);
        AppendIdentityValue(hash, responseTone);
        AppendIdentityValue(hash, customTone);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IReadOnlyList<string> EquivalentModels(
        string model,
        IReadOnlyList<string>? equivalentModelIds)
    {
        return (equivalentModelIds ?? [])
            .Append(model)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(value => TryValidateModel(value, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
    }

    private static bool ModelAliasesMatch(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase)
        || ProviderModelCatalogProjectionService.SafeModelIdentifier(left)
            .Equals(ProviderModelCatalogProjectionService.SafeModelIdentifier(right), StringComparison.OrdinalIgnoreCase);

    private static ModelProviderConfig SharedConfig(ArenaSnapshot snapshot) =>
        snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var shared)
            ? shared
            : new ModelProviderConfig();

    private static ModelProviderConfig ConfigForModel(ModelProviderConfig shared, string model) => new()
    {
        BaseUrl = shared.BaseUrl,
        ApiMode = shared.ApiMode,
        ApiToken = shared.ApiToken,
        Model = model
    };

    private static IReadOnlyList<string> ChangedModelConfigurationFields(
        ProviderModelConfigurationProjection before,
        int contextWindow,
        string historyPolicy,
        string responseTone,
        string customTone)
    {
        var changed = new List<string>();
        if (before.ConfiguredContextWindow != contextWindow) changed.Add("configuredContextWindow");
        if (!before.HistoryPolicy.Equals(historyPolicy, StringComparison.Ordinal)) changed.Add("historyPolicy");
        if (!before.ResponseTone.Equals(responseTone, StringComparison.Ordinal)) changed.Add("responseTone");
        if (!before.CustomTone.Equals(customTone, StringComparison.Ordinal)) changed.Add("customTone");
        return changed;
    }

    private static (bool Ok, string ErrorCode, string Message) ValidateModelConfigurationRequest(
        ProviderModelConfigurationRequest request)
    {
        if (!TryValidateModel(request.Model, allowEmpty: false))
        {
            return (false, "invalid_argument", "The selected model must be non-empty, at most 1024 characters, and contain no control characters.");
        }
        if (!IsOpaqueFingerprint(request.ExpectedConfigurationIdentity))
        {
            return (false, "invalid_argument", "Refresh the model configuration before changing it.");
        }
        if (request.ConfiguredContextWindow != 0
            && request.ConfiguredContextWindow is < ModelRuntimeSettingsRegistry.MinimumConfiguredContextWindow
                or > ModelRuntimeSettingsRegistry.MaximumConfiguredContextWindow)
        {
            return (false, "invalid_argument", "Configured context must be Provider default (0) or between 512 and 1048576.");
        }
        if (!request.HistoryPolicy.Trim().Equals(
                ModelHistoryPolicies.NormalizeHistoryPolicy(request.HistoryPolicy),
                StringComparison.Ordinal))
        {
            return (false, "invalid_argument", "History policy must be strict, rolling_80, or chaptered.");
        }
        if (request.HistoryPolicy.Equals(ModelHistoryPolicies.Chaptered, StringComparison.Ordinal))
        {
            return (false, "not_available", "Chaptered history is reserved for a later release.");
        }
        if (!request.ResponseTone.Trim().Equals(
                ModelResponseTones.NormalizeResponseTone(request.ResponseTone),
                StringComparison.Ordinal))
        {
            return (false, "invalid_argument", "Response tone is unsupported.");
        }
        var custom = ModelResponseTones.NormalizeCustomTone(request.CustomTone);
        if (request.ResponseTone.Equals(ModelResponseTones.Custom, StringComparison.Ordinal)
            && (custom.Length == 0 || !custom.Equals(request.CustomTone.Trim(), StringComparison.Ordinal)))
        {
            return (false, "invalid_argument", "Custom tone must be non-empty, single-line, and at most 240 characters.");
        }
        return (true, "", "");
    }

    private static ProviderModelConfigurationControlResult ModelConfigurationFailure(
        string errorCode,
        string message,
        ProviderModelConfigurationProjection configuration) =>
        new(false, errorCode, message, configuration, []);

    private static ProviderModelAssignmentControlResult AssignmentFailure(
        string errorCode,
        string message,
        ProviderModelAssignmentProjection assignment)
    {
        return new ProviderModelAssignmentControlResult(
            false,
            errorCode,
            message,
            assignment,
            []);
    }

    private static (bool Ok, string ErrorCode, string Message) ValidateAssignmentRequest(
        ProviderModelAssignmentRequest request)
    {
        if (!IsKnownAssignmentTarget(request.TargetId, includeDefault: true))
        {
            return (false, "invalid_argument", "Select Default, an active arena agent, or Narrator.");
        }

        if (!TryValidateModel(request.Model, allowEmpty: false))
        {
            return (false, "invalid_argument", "The selected model must be non-empty, at most 1024 characters, and contain no control characters.");
        }

        if (!IsOpaqueFingerprint(request.ExpectedProviderFingerprint)
            || !IsOpaqueFingerprint(request.ExpectedAssignmentFingerprint))
        {
            return (false, "invalid_argument", "Refresh the model assignment state before changing it.");
        }

        return (true, "", "");
    }

    private static AIArenaProviderConfigurationPatch DefaultModelPatch(
        string model,
        bool defaultForUnassignedAgentsEnabled)
    {
        return new AIArenaProviderConfigurationPatch(
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
            RoleModels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            RefreshModels: false,
            DefaultForUnassignedAgentsEnabled: defaultForUnassignedAgentsEnabled);
    }

    private static IReadOnlyList<string> ConfigurationRoleKeys(ArenaSnapshot snapshot)
    {
        var active = snapshot.Engine.Agents
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .Select(agent => agent.Id.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AgentRosterService.ParticipantIds
            .Where(role => RoleKeys.Contains(role, StringComparer.OrdinalIgnoreCase)
                || active.Contains(role)
                || snapshot.Configs.ContainsKey(role))
            .Append("narrator")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsKnownAssignmentTarget(string targetId, bool includeDefault)
    {
        var normalized = (targetId ?? "").Trim();
        return (includeDefault && normalized.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase))
            || normalized.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            || AgentRosterService.ParticipantIds.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOpaqueFingerprint(string value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static bool TryValidateBaseUrl(string value, out string error)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxBaseUrlLength)
        {
            error = "args.baseUrl must be a non-empty HTTP or HTTPS URL no longer than 2048 characters.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "args.baseUrl must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "args.baseUrl cannot contain credentials, a query string, or a fragment.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateModel(string value, bool allowEmpty)
    {
        return (allowEmpty || !string.IsNullOrWhiteSpace(value))
            && value.Length <= MaxModelLength
            && !value.Any(char.IsControl);
    }

    private static bool IsSupportedApiMode(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is ModelProviderApiModes.OpenAiCompatible
            or ModelProviderApiModes.LmStudioNative
            or ModelProviderApiModes.OllamaNative
            or ModelProviderApiModes.LlamaCppNative;
    }

    private static bool IsSupportedReasoning(string value)
    {
        return value.Trim().ToLowerInvariant() is "default" or "off" or "low" or "medium" or "high" or "on";
    }

    private static string NormalizeReasoning(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "default" ? "" : normalized;
    }
}
