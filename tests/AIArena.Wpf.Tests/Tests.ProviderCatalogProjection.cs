using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Windows.Automation;

internal static partial class Program
{
static void ProviderCatalogProjectionSeparatesLoadedAndAvailableEvidence()
{
    var snapshot = SessionStore.CreateDefaultSnapshot();
    snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "projection-secret",
        Model = "yi-coder-1.5b"
    };
    var projection = new ProviderModelCatalogProjectionService();
    var lease = projection.BeginRefresh("catalog-session", snapshot);
    var lmStudio = LmStudioModelCatalog.Success(LmStudioModelCatalogService.ParseModels(
        """
        {
          "models": [
            {
              "type": "llm",
              "publisher": "local",
              "key": "loaded-chat",
              "display_name": "Loaded Chat",
              "loaded_instances": [{ "id": "C:\\private\\runtime-instance", "config": { "context_length": 8192 } }]
            },
            {
              "type": "llm",
              "key": "yi-coder-1.5b",
              "display_name": "Yi Coder 1.5B",
              "loaded_instances": [],
              "max_context_length": 32768
            },
            {
              "type": "llm",
              "key": "missing-residency",
              "display_name": "Missing residency"
            },
            {
              "type": "llm",
              "key": "malformed-residency",
              "display_name": "Malformed residency",
              "loaded_instances": {}
            },
            {
              "type": "llm",
              "key": "malformed-instance",
              "display_name": "Malformed loaded instance",
              "loaded_instances": [null]
            },
            {
              "type": "llm",
              "key": "loaded-without-id",
              "display_name": "Loaded without instance id",
              "loaded_instances": [{ "id": "" }]
            },
            {
              "type": "embedding",
              "key": "private-embedding",
              "display_name": "Embedding",
              "loaded_instances": []
            }
          ]
        }
        """));
    var lmSnapshot = ProviderModelCatalogProjectionService.FromLmStudio(
        lease,
        lmStudio,
        "yi-coder-1.5b",
        DateTimeOffset.UnixEpoch);

    Require(lmSnapshot.CatalogEvidence == ProviderCatalogEvidenceState.Ready
        && lmSnapshot.ResidencyEvidence == ProviderCatalogEvidenceState.Partial,
        "LM Studio should keep catalog evidence while marking malformed residency evidence partial");
    var loadedChat = lmSnapshot.LoadedModels.Single(item => item.Id == "loaded-chat");
    Require(loadedChat.CanUnload && !loadedChat.CanLoad,
        "LM Studio loaded_instances should place a chat model only in Loaded with unload capability");
    var loadedWithoutId = lmSnapshot.LoadedModels.Single(item => item.Id == "loaded-without-id");
    Require(!loadedWithoutId.CanUnload && !loadedWithoutId.CanLoad,
        "LM Studio loaded evidence without an instance id must not enable an unload request");
    var yiCoder = lmSnapshot.AvailableModels.Single(item => item.Id == "yi-coder-1.5b");
    Require(yiCoder.LoadState == ProviderModelLoadState.NotLoaded
        && yiCoder.CanLoad
        && !yiCoder.CanUnload,
        "a configured Yi model with no loaded_instances must stay Available with load capability");
    Require(lmSnapshot.AvailableModels.Where(item =>
            item.Id is "missing-residency" or "malformed-residency" or "malformed-instance").All(item =>
                item.LoadState == ProviderModelLoadState.Unavailable
                && !item.CanLoad
                && !item.CanUnload),
        "missing or malformed loaded_instances must remain unavailable rather than observed Available");
    Require(loadedChat is not null
            && loadedChat.DisplayName.Length > 0
            && lmStudio.ChatModels.Single(item => item.Key == "loaded-chat").Tooltip().Contains("Loaded instances: 1", StringComparison.Ordinal)
            && !lmStudio.ChatModels.Single(item => item.Key == "loaded-chat").Tooltip().Contains("runtime-instance", StringComparison.OrdinalIgnoreCase),
        "LM Studio Settings tooltips must report a safe instance count without exposing opaque runtime IDs");
    var serializedLm = string.Join("|", lmSnapshot.Models.SelectMany(item =>
        new[] { item.Id, item.DisplayName, item.CapabilitySummary }.Concat(item.Aliases)));
    Require(!serializedLm.Contains("runtime-instance", StringComparison.OrdinalIgnoreCase)
        && !serializedLm.Contains("private-embedding", StringComparison.OrdinalIgnoreCase),
        "catalog projection must exclude loaded instance IDs and non-chat models");
    foreach (var malformed in new[]
             {
                 "{}",
                 "{\"models\":[null]}",
                 "{\"models\":[{\"type\":\"llm\",\"loaded_instances\":[]}]}"
             })
    {
        var rejected = false;
        try
        {
            _ = LmStudioModelCatalogService.ParseModels(malformed);
        }
        catch (System.Text.Json.JsonException)
        {
            rejected = true;
        }

        Require(rejected, "malformed LM Studio catalog envelopes must be unavailable rather than authoritative empty evidence");
    }

    snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:11434/api",
        ApiMode = ModelProviderApiModes.OllamaNative,
        ApiToken = "projection-secret",
        Model = "qwen3:8b"
    };
    var ollamaLease = projection.BeginRefresh("catalog-session", snapshot);
    var ollama = OllamaModelCatalog.Success(
        OllamaModelCatalogService.ParseTags(OllamaTagsJson()),
        runningModelsOk: false,
        "Running inventory failed at C:\\private\\ollama\\state.json via https://user:pass@example.test/api?token=projection-secret; bearer projection-secret");
    var ollamaSnapshot = ProviderModelCatalogProjectionService.FromOllama(
        ollamaLease,
        ollama,
        "qwen3:8b",
        DateTimeOffset.UnixEpoch);

    Require(ollamaSnapshot.CatalogEvidence == ProviderCatalogEvidenceState.Ready
        && ollamaSnapshot.ResidencyEvidence == ProviderCatalogEvidenceState.Unavailable,
        "Ollama tags success with /ps failure must preserve catalog evidence without inventing residency");
    Require(ollamaSnapshot.LoadedModels.Count == 0
        && ollamaSnapshot.AvailableModels.Count == 2
        && ollamaSnapshot.AvailableModels.All(item =>
            item.LoadState == ProviderModelLoadState.Unavailable && !item.CanLoad && !item.CanUnload),
        "unknown Ollama running state must not render observed zeroes or enable lifecycle actions");
    Require(!ollamaSnapshot.Status.Contains("private", StringComparison.OrdinalIgnoreCase)
        && !ollamaSnapshot.Status.Contains("state.json", StringComparison.OrdinalIgnoreCase)
        && !ollamaSnapshot.Status.Contains("projection-secret", StringComparison.Ordinal)
        && !ollamaSnapshot.Status.Contains("user:pass", StringComparison.Ordinal)
        && !ollamaSnapshot.Status.Contains("?token", StringComparison.OrdinalIgnoreCase),
        "catalog status must not disclose local paths, provider credentials, or URL queries");
}

static void ProviderCatalogProjectionPreservesLlamaAndCompatibleTruth()
{
    var config = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        Model = "yi-coder.gguf"
    };
    var projection = new ProviderModelCatalogProjectionService();
    var lease = projection.BeginRefresh("llama-session", config);
    var llamaSource = new LlamaCppRuntimeSnapshot(
        Available: true,
        Ready: true,
        IsLlamaCpp: true,
        Status: "ready",
        BuildInfo: "",
        Model: "yi-coder.gguf",
        Quantization: "Q4_K_M",
        ContextLength: 8192,
        GpuLayers: null,
        SlotCount: null,
        BusySlots: null,
        Sleeping: false,
        RouterMode: true,
        Loaded: true,
        ModelSizeBytes: null,
        MemoryBytes: null,
        ParameterCount: null,
        TokensPerSecond: null,
        Capabilities: new LlamaCppRuntimeCapabilities(
            Health: true,
            OpenAiModels: true,
            Props: false,
            Slots: false,
            RouterModels: true,
            ModelLifecycle: true,
            DetailedTelemetry: false),
        Models:
        [
            new LlamaCppRuntimeModel("C:\\private\\models\\yi-coder.gguf", "loaded", "Q4_K_M", 8192, null, null, true, 4_000_000_000, null),
            new LlamaCppRuntimeModel("available.gguf", "unloaded", "Q5_K_M", null, null, null, false, 5_000_000_000, null)
        ],
        Slots: [],
        Warnings: [],
        Error: "",
        CheckedAt: DateTimeOffset.UnixEpoch);
    var llamaSnapshot = ProviderModelCatalogProjectionService.FromLlamaCpp(
        lease,
        llamaSource,
        "yi-coder.gguf");

    Require(llamaSnapshot.LoadedModels.Single().Id == "yi-coder.gguf"
        && llamaSnapshot.LoadedModels.Single().CanUnload,
        "llama.cpp router loaded evidence should enable only unload for the resident model");
    Require(llamaSnapshot.AvailableModels.Single().Id == "available.gguf"
        && llamaSnapshot.AvailableModels.Single().CanLoad,
        "llama.cpp router unloaded evidence should enable only load for the available model");
    Require(llamaSnapshot.Models.All(item => !item.Id.Contains("private", StringComparison.OrdinalIgnoreCase)),
        "llama.cpp projection must keep server paths out of the UI contract");

    var compatibleConfig = new ModelProviderConfig
    {
        BaseUrl = config.BaseUrl,
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        Model = "configured-only"
    };
    var compatibleLease = projection.BeginRefresh("llama-session", compatibleConfig);
    var advertised = Enumerable.Range(0, 300)
        .Select(index => $"provider-model-{index:000}")
        .Append("provider-model-001")
        .ToArray();
    var compatible = ProviderModelCatalogProjectionService.FromCompatible(
        compatibleLease,
        advertised,
        catalogAvailable: true,
        error: "",
        configuredModel: "configured-only",
        checkedAt: DateTimeOffset.UnixEpoch);

    Require(compatible.CatalogEvidence == ProviderCatalogEvidenceState.Ready
        && compatible.ResidencyEvidence == ProviderCatalogEvidenceState.Unavailable
        && compatible.LoadedModels.Count == 0,
        "generic compatible catalogs must never infer loaded residency");
    Require(compatible.AvailableModels.Count == ProviderModelCatalogProjectionService.MaximumModelCount
        && compatible.OmittedModelCount == 45,
        "generic model catalogs should deduplicate and retain a bounded 256-row projection");
    Require(compatible.ConfiguredModelMissing
        && compatible.AvailableModels[0].IsConfiguredOnly
        && compatible.AvailableModels[0].Id == "configured-only",
        "a configured model missing from a fresh catalog should remain visible as a neutral pinned row");
}

static void ProviderCatalogProjectionRejectsStaleGenerations()
{
    var config = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        ApiToken = "first-secret",
        Model = "first-model"
    };
    var projection = new ProviderModelCatalogProjectionService();
    var firstLease = projection.BeginRefresh("first-session", config);
    var first = ProviderModelCatalogProjectionService.FromCompatible(
        firstLease,
        ["first-model"],
        catalogAvailable: true,
        error: "",
        configuredModel: config.Model,
        checkedAt: DateTimeOffset.UnixEpoch);

    var secondConfig = new ModelProviderConfig
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = "second-secret",
        Model = "second-model"
    };
    var secondLease = projection.BeginRefresh("second-session", secondConfig);
    var second = ProviderModelCatalogProjectionService.FromCompatible(
        secondLease,
        ["second-model"],
        catalogAvailable: true,
        error: "",
        configuredModel: secondConfig.Model,
        checkedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));

    Require(!projection.TryPublish(firstLease, first, out _),
        "a late catalog result from an older generation must be discarded");
    Require(projection.TryPublish(secondLease, second, out var published)
        && ReferenceEquals(projection.Current, second)
        && published.SessionId == "second-session",
        "the current session/provider generation should publish atomically");
    Require(firstLease.ProviderFingerprint != secondLease.ProviderFingerprint
        && !firstLease.ProviderFingerprint.Contains("secret", StringComparison.OrdinalIgnoreCase)
        && !secondLease.ProviderFingerprint.Contains("secret", StringComparison.OrdinalIgnoreCase),
        "provider fingerprints should bind session, token, and selected model without exposing them");
    var firstConnection = ProviderModelCatalogProjectionService.ConnectionFingerprint("first-session", config);
    var reroutedConfig = new ModelProviderConfig
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = "different-default-only"
    };
    Require(firstConnection == ProviderModelCatalogProjectionService.ConnectionFingerprint("first-session", reroutedConfig)
            && firstConnection != ProviderModelCatalogProjectionService.ConnectionFingerprint("second-session", reroutedConfig),
        "lifecycle connection identity must survive Default-model routing changes while remaining session-bound");
    projection.Invalidate();
    Require(projection.Current is null && !projection.TryPublish(secondLease, second, out _),
        "invalidating the projection should reject every in-flight prior lease");
}

static void ProviderModelAssignmentPersistsDynamicTargetsAtomically()
{
    var root = CreateProviderCatalogTestRoot("assignment");
    const string providerToken = "assignment-provider-secret";
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        AgentRosterService.EnsureParticipantCount(snapshot, AgentRosterService.MaxParticipants);
        var shared = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = providerToken,
            Model = "default-model",
            Temperature = 0.6,
            MaxOutputTokens = 4096
        };
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = shared;
        ProviderConfigurationControlService.SaveRoleModelConfig(
            snapshot.Configs,
            "theta",
            "theta-old",
            shared,
            temperatureOverride: 1.25,
            maxOutputTokensOverride: 7777);
        sessionStore.SaveSnapshotAsync(snapshot, "assignment-session").GetAwaiter().GetResult();
        SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
            .Single(session => session.Id == "assignment-session");
        var refreshFlags = new List<bool>();
        using var operationLock = new SemaphoreSlim(1, 1);
        var service = new ProviderConfigurationControlService(
            sessionStore,
            eventLogStore,
            operationLock,
            () => active,
            () => false,
            (_, refreshModels, _) =>
            {
                refreshFlags.Add(refreshModels);
                return Task.CompletedTask;
            });

        var initial = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("assignment snapshot should load");
        var initialRevision = initial.PersistenceRevision;
        var initialProjection = ProviderModelAssignmentProjectionService.Project(
            "assignment-session",
            initial,
            "yi-coder");
        Require(initialProjection.Targets.Count == 10
            && initialProjection.Targets[0].Id == ModelProviderRouting.SharedConfigKey
            && initialProjection.Targets.Any(target => target.Id == "theta")
            && initialProjection.Targets[^1].Id == "narrator",
            "assignment targets should be Default, every active Alpha-Theta slot, then Narrator");
        var theta = initialProjection.Targets.Single(target => target.Id == "theta");
        var assigned = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                theta.Id,
                "yi-coder",
                Assigned: true,
                initialProjection.ProviderFingerprint,
                theta.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        var afterAssign = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("assigned snapshot should load");

        Require(assigned.Ok
            && assigned.ChangedFields.SequenceEqual(["thetaModel"])
            && afterAssign.PersistenceRevision == initialRevision + 1
            && afterAssign.Configs["theta"].Model == "yi-coder",
            "one target check should persist one model override in exactly one snapshot revision");
        Require(Math.Abs(afterAssign.Configs["theta"].Temperature - 1.25) < 0.000001
            && afterAssign.Configs["theta"].MaxOutputTokens == 7777,
            "assigning a model must preserve per-agent generation overrides");

        var assignedTheta = assigned.Assignment.Targets.Single(target => target.Id == "theta");
        var unassigned = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "theta",
                "yi-coder",
                Assigned: false,
                assigned.Assignment.ProviderFingerprint,
                assignedTheta.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        var afterUnassign = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("unassigned snapshot should load");
        Require(unassigned.Ok
            && afterUnassign.PersistenceRevision == initialRevision + 2
            && afterUnassign.Configs["theta"].Model == "default-model"
            && unassigned.Assignment.Targets.Single(target => target.Id == "theta").InheritsDefault,
            "unchecking an agent should restore default inheritance without discarding generation overrides");

        var defaultTarget = unassigned.Assignment.Targets.Single(target => target.IsDefault);
        var defaultAssigned = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "yi-coder",
                Assigned: true,
                unassigned.Assignment.ProviderFingerprint,
                defaultTarget.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        var afterDefault = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("default-assigned snapshot should load");
        Require(defaultAssigned.Ok
            && afterDefault.PersistenceRevision == initialRevision + 3
            && afterDefault.Configs[ModelProviderRouting.SharedConfigKey].Model == "yi-coder"
            && afterDefault.Configs["theta"].Model == "yi-coder",
            "checking Default should atomically update the shared fallback and same-as-default override carriers");
        Require(Math.Abs(afterDefault.Configs["theta"].Temperature - 1.25) < 0.000001
            && afterDefault.Configs["theta"].MaxOutputTokens == 7777,
            "changing Default should preserve dynamic-role generation overrides through Alpha-Theta");
        Require(refreshFlags.SequenceEqual([false, false, false]),
            "immediate assignments should refresh host state but never request model loading or residency changes");

        var audit = File.ReadAllText(eventLogStore.EventPath("assignment-session"));
        Require(!audit.Contains(providerToken, StringComparison.Ordinal)
            && audit.Contains("provider_model_assignment_changed", StringComparison.Ordinal),
            "assignment evidence should be present without serializing provider credentials");
    }
    finally
    {
        DeleteProviderCatalogTestRoot(root);
    }
}

static void ProviderModelAssignmentRejectsStaleAndInactiveTargets()
{
    var root = CreateProviderCatalogTestRoot("stale-assignment");
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            Model = "default-model"
        };
        sessionStore.SaveSnapshotAsync(snapshot, "stale-session").GetAwaiter().GetResult();
        SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
            .Single(session => session.Id == "stale-session");
        var refreshCount = 0;
        using var operationLock = new SemaphoreSlim(1, 1);
        var service = new ProviderConfigurationControlService(
            sessionStore,
            eventLogStore,
            operationLock,
            () => active,
            () => false,
            (_, _, _) =>
            {
                refreshCount++;
                return Task.CompletedTask;
            });
        var current = sessionStore.LoadSnapshotAsync("stale-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("stale assignment snapshot should load");
        var projection = ProviderModelAssignmentProjectionService.Project("stale-session", current, "other-model");

        var inactive = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "theta",
                "other-model",
                Assigned: true,
                projection.ProviderFingerprint,
                ProviderModelAssignmentProjectionService.AssignmentFingerprint("")))
            .GetAwaiter()
            .GetResult();
        Require(!inactive.Ok && inactive.ErrorCode == "inactive_target",
            "known but inactive participant slots must not receive hidden model overrides");

        var defaultTarget = projection.Targets.Single(target => target.IsDefault);
        var cannotUnsetDefault = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "default-model",
                Assigned: false,
                projection.ProviderFingerprint,
                defaultTarget.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        Require(!cannotUnsetDefault.Ok && cannotUnsetDefault.ErrorCode == "invalid_operation",
            "the current Default target must not be left without a configured fallback");

        var providerChanged = sessionStore.LoadSnapshotAsync("stale-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("provider change snapshot should load");
        var previousShared = providerChanged.Configs[ModelProviderRouting.SharedConfigKey];
        providerChanged.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = previousShared.BaseUrl,
            ApiMode = previousShared.ApiMode,
            ApiToken = previousShared.ApiToken,
            Model = "new-default",
            Timeout = previousShared.Timeout,
            Temperature = previousShared.Temperature,
            MaxOutputTokens = previousShared.MaxOutputTokens,
            ContextLength = previousShared.ContextLength,
            Reasoning = previousShared.Reasoning,
            NativeStatefulChat = previousShared.NativeStatefulChat,
            NativeIdleTtlSeconds = previousShared.NativeIdleTtlSeconds
        };
        sessionStore.SaveSnapshotAsync(providerChanged, "stale-session").GetAwaiter().GetResult();
        var alphaBeforeProviderChange = projection.Targets.Single(target => target.Id == "alpha");
        var staleProvider = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "alpha",
                "other-model",
                Assigned: true,
                projection.ProviderFingerprint,
                alphaBeforeProviderChange.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        Require(!staleProvider.Ok && staleProvider.ErrorCode == "stale_provider",
            "a provider/default fingerprint change must reject a late catalog checkbox result");

        var afterProviderChange = sessionStore.LoadSnapshotAsync("stale-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("new provider projection snapshot should load");
        var fresh = ProviderModelAssignmentProjectionService.Project("stale-session", afterProviderChange, "other-model");
        var alpha = fresh.Targets.Single(target => target.Id == "alpha");
        ProviderConfigurationControlService.SaveRoleModelConfig(
            afterProviderChange.Configs,
            "alpha",
            "concurrent-model",
            afterProviderChange.Configs[ModelProviderRouting.SharedConfigKey]);
        sessionStore.SaveSnapshotAsync(afterProviderChange, "stale-session").GetAwaiter().GetResult();
        var staleTarget = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "alpha",
                "other-model",
                Assigned: true,
                fresh.ProviderFingerprint,
                alpha.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        Require(!staleTarget.Ok && staleTarget.ErrorCode == "conflict",
            "a concurrent target-routing change must reject a stale checkbox mutation without overwriting it");
        var persisted = sessionStore.LoadSnapshotAsync("stale-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("conflicted assignment snapshot should load");
        Require(persisted.Configs["alpha"].Model == "concurrent-model" && refreshCount == 0,
            "rejected stale and inactive assignments must not save, refresh, or reroute agents");
    }
    finally
    {
        DeleteProviderCatalogTestRoot(root);
    }
}

static void ProviderModelsDefaultAssignmentSurvivesHostRefresh()
{
    static void Pump(Func<Task> start)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var task = start();
            if (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = task.ContinueWith(
                    _ => dispatcher.BeginInvoke(
                        new Action(() => frame.Continue = false),
                        System.Windows.Threading.DispatcherPriority.Send),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    RunStaTest(() =>
    {
        var root = CreateProviderCatalogTestRoot("default-assignment-host-refresh");
        try
        {
            const string sessionId = "default-assignment-session";
            // LM Studio advertises the configured display-name alias but the
            // assignment persists its canonical key, producing the same
            // routing-only ProviderFingerprint change as choosing a new default.
            const string originalDefault = "Provider observed model";
            const string requestedDefault = "provider-observed-model";
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                ApiToken = "default-assignment-secret",
                Model = originalDefault
            };
            sessionStore.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
            SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
                .Single(session => session.Id == sessionId);
            var initial = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("default assignment snapshot should load");
            var originalProviderFingerprint = ProviderModelCatalogProjectionService.ProviderFingerprint(sessionId, initial);
            var originalConnectionFingerprint = ProviderModelCatalogProjectionService.ConnectionFingerprint(
                sessionId,
                initial.Configs[ModelProviderRouting.SharedConfigKey]);

            using var operationLock = new SemaphoreSlim(1, 1);
            ProviderModelsSurfaceCoordinator? coordinator = null;
            var hostRefreshCount = 0;
            var providerConfig = new ProviderConfigurationControlService(
                sessionStore,
                eventLogStore,
                operationLock,
                () => active,
                () => false,
                async (_, _, cancellationToken) =>
                {
                    hostRefreshCount++;
                    await (coordinator ?? throw new InvalidOperationException("Models coordinator was not initialized."))
                        .RefreshAsync(refreshCatalog: false, cancellationToken);
                });
            using var handler = new ProviderModelsLifecycleHttpHandler(requestedDefault);
            using var httpClient = new HttpClient(handler);
            var catalogService = new LmStudioModelCatalogService(httpClient);
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            coordinator = new ProviderModelsSurfaceCoordinator(
                control,
                sessionStore,
                providerConfig,
                new ModelProviderHealthService(httpClient),
                new ModelPreloadService(httpClient, catalogService),
                operationLock,
                () => active,
                () => false,
                catalogService);
            var host = new System.Windows.Window
            {
                Content = control,
                Width = 1300,
                Height = 800,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            host.Show();
            try
            {
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));
                FlushProviderModelsDispatcher(host);
                var requestedRow = FindProviderModelsDescendants<System.Windows.Controls.ListBoxItem>(control.MasterSurface)
                    .Single(item => AutomationProperties.GetName(item)
                        .StartsWith("Provider observed model", StringComparison.Ordinal));
                requestedRow.IsSelected = true;
                FlushProviderModelsDispatcher(host);
                var defaultTarget = FindProviderModelsDescendants<System.Windows.Controls.CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox)
                        .EndsWith("Default for unassigned agents", StringComparison.Ordinal));
                Task? assignmentTask = null;
                control.AssignmentChanged += (_, args) => assignmentTask = coordinator.SaveAssignmentAsync(args);

                Require(control.SelectedModelId == requestedDefault && defaultTarget.IsEnabled,
                    $"Provider-observed model Default target was not actionable (selected '{control.SelectedModelId}', enabled {defaultTarget.IsEnabled}).");
                Pump(() =>
                {
                    ToggleProviderModelsCheckBox(defaultTarget);
                    Require(assignmentTask is not null && control.HasPendingAssignment,
                        "Default checkbox did not establish a pending hosted assignment before refresh.");
                    return assignmentTask!;
                });
                FlushProviderModelsDispatcher(host);

                var persisted = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("saved default assignment snapshot should load");
                var savedProviderFingerprint = ProviderModelCatalogProjectionService.ProviderFingerprint(sessionId, persisted);
                var savedConnectionFingerprint = ProviderModelCatalogProjectionService.ConnectionFingerprint(
                    sessionId,
                    persisted.Configs[ModelProviderRouting.SharedConfigKey]);
                var savedDefaultTarget = FindProviderModelsDescendants<System.Windows.Controls.CheckBox>(control.DetailSurface)
                    .Single(checkBox => AutomationProperties.GetName(checkBox)
                        .EndsWith("Default for unassigned agents", StringComparison.Ordinal));
                Require(hostRefreshCount == 1
                        && persisted.PersistenceRevision == initial.PersistenceRevision + 1
                        && persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == requestedDefault
                        && !originalProviderFingerprint.Equals(savedProviderFingerprint, StringComparison.Ordinal)
                        && originalConnectionFingerprint.Equals(savedConnectionFingerprint, StringComparison.Ordinal),
                    "Default assignment did not persist exactly once while changing only the routing-sensitive provider fingerprint.");
                Require(!control.HasPendingAssignment
                        && control.SelectedModelId == requestedDefault
                        && savedDefaultTarget.IsChecked == true
                        && AutomationProperties.GetItemStatus(control.AssignmentStatus) == "Saved"
                        && control.AssignmentStatus.Text.Contains("Default model saved", StringComparison.Ordinal)
                        && !control.AssignmentStatus.Text.Contains("provider or session changed", StringComparison.OrdinalIgnoreCase),
                    "The host refresh before service completion reinterpreted a persisted Default assignment as failure.");
            }
            finally
            {
                host.Close();
                coordinator.Dispose();
            }
        }
        finally
        {
            DeleteProviderCatalogTestRoot(root);
        }
    });
}

static void ProviderModelsLifecycleUsesConfirmedLmStudioHeartbeat()
{
    static void Pump(Func<Task> start)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            var task = start();
            if (!task.IsCompleted)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                _ = task.ContinueWith(
                    _ => dispatcher.BeginInvoke(
                        new Action(() => frame.Continue = false),
                        System.Windows.Threading.DispatcherPriority.Send),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    RunStaTest(() =>
    {
        var root = CreateProviderCatalogTestRoot("lm-lifecycle-heartbeat");
        try
        {
            const string sessionId = "lm-lifecycle-session";
            const string modelId = "provider-observed-model";
            const string token = "lifecycle-secret";
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                ApiToken = token,
                Model = modelId,
                ContextLength = 4096,
                NativeIdleTtlSeconds = 45
            };
            sessionStore.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
            const string alternateSessionId = "lm-lifecycle-alternate-session";
            var alternateSnapshot = SessionStore.CreateDefaultSnapshot();
            alternateSnapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                ApiToken = token,
                Model = modelId,
                ContextLength = 4096,
                NativeIdleTtlSeconds = 45
            };
            sessionStore.SaveSnapshotAsync(alternateSnapshot, alternateSessionId).GetAwaiter().GetResult();
            SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
                .Single(session => session.Id == sessionId);
            var primarySession = active;
            var alternateSession = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
                .Single(session => session.Id == alternateSessionId);
            var initialRevision = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!
                .PersistenceRevision;

            using var operationLock = new SemaphoreSlim(1, 1);
            var busy = false;
            var providerConfig = new ProviderConfigurationControlService(
                sessionStore,
                eventLogStore,
                operationLock,
                () => active,
                () => busy,
                (_, _, _) => Task.CompletedTask);
            using var handler = new ProviderModelsLifecycleHttpHandler(modelId);
            using var httpClient = new HttpClient(handler);
            var catalogService = new LmStudioModelCatalogService(httpClient);
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            using var coordinator = new ProviderModelsSurfaceCoordinator(
                control,
                sessionStore,
                providerConfig,
                new ModelProviderHealthService(httpClient),
                new ModelPreloadService(httpClient, catalogService),
                operationLock,
                () => active,
                () => busy,
                catalogService);
            var host = new System.Windows.Window
            {
                Content = control,
                Width = 1300,
                Height = 800,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            host.Show();
            try
            {
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));
                Require(control.SelectedModelId == modelId
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.CatalogStatus.Text.Contains("Auto-checking every 5 seconds", StringComparison.Ordinal),
                    "LM Studio available evidence did not expose the selected Load action and five-second heartbeat contract");

                Task? lifecycleTask = null;
                control.LifecycleRequested += (_, args) =>
                    lifecycleTask = coordinator.RunLifecycleAsync(args);

                handler.IncludeModel = false;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Stale-row load did not start lifecycle validation.");
                });
                Require(handler.Requests.Count(path => path == "/api/v1/models/load") == 0
                        && control.LifecycleStatus.Text.Contains("did not accept", StringComparison.OrdinalIgnoreCase),
                    "a model removed before native preflight still issued a load POST or hid its rejection");
                handler.IncludeModel = true;
                lifecycleTask = null;
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));

                operationLock.Wait();
                try
                {
                    Pump(() =>
                    {
                        control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        return lifecycleTask ?? throw new InvalidOperationException("Locked lifecycle validation did not start.");
                    });
                }
                finally
                {
                    operationLock.Release();
                }
                Require(handler.Requests.Count(path => path == "/api/v1/models/load") == 0
                        && control.LifecycleStatus.Text.Contains("another arena or provider operation", StringComparison.OrdinalIgnoreCase),
                    "a pre-held shared arena lock did not reject the lifecycle request without a POST");
                lifecycleTask = null;
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));

                handler.BeforeNativeCatalogResponse = () => active = alternateSession;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Session-switch lifecycle validation did not start.");
                });
                Require(handler.Requests.Count(path => path == "/api/v1/models/load") == 0
                        && control.LifecycleStatus.Text.Contains("no lifecycle request was sent", StringComparison.OrdinalIgnoreCase),
                    "switching sessions during native preflight still issued a stale lifecycle mutation");
                active = primarySession;
                lifecycleTask = null;
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));

                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Load click did not start lifecycle work.");
                });
                Require(handler.Loaded
                        && control.SelectedModelId == modelId
                        && control.LifecycleAction.Content?.ToString() == "Unload model"
                        && control.LifecycleStatus.Text.Contains("loaded in LM Studio", StringComparison.OrdinalIgnoreCase),
                    "confirmed LM Studio load did not transfer the retained selection into Loaded");
                Require(handler.Requests.Count(path => path == "/api/v1/models/load") == 1
                        && handler.Requests.All(path => !path.Contains("chat", StringComparison.OrdinalIgnoreCase))
                        && handler.AuthorizationHeaders.All(value => value == $"Bearer {token}"),
                    "Load action did not issue exactly one authenticated native lifecycle request without inference");
                Require(handler.Bodies.Single(body => body.Contains("echo_load_config", StringComparison.Ordinal))
                        is var loadBody
                        && loadBody.Contains(modelId, StringComparison.Ordinal)
                        && loadBody.Contains("4096", StringComparison.Ordinal)
                        && loadBody.Contains("45", StringComparison.Ordinal)
                        && !loadBody.Contains("gpu", StringComparison.OrdinalIgnoreCase),
                    "LM Studio load payload changed model/context/TTL or introduced GPU placement");

                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Unload click did not start lifecycle work.");
                });
                Require(!handler.Loaded
                        && control.SelectedModelId == modelId
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleStatus.Text.Contains("unloaded from LM Studio", StringComparison.OrdinalIgnoreCase)
                        && handler.Requests.Count(path => path == "/api/v1/models/unload") == 1,
                    "confirmed LM Studio unload did not return the retained selection to Available exactly once");

                handler.Loaded = true;
                var requestsBeforeHeartbeat = handler.Requests.Count;
                Pump(() => coordinator.HeartbeatAsync());
                Require(control.LifecycleAction.Content?.ToString() == "Unload model"
                        && handler.Requests.Count == requestsBeforeHeartbeat + 1
                        && handler.Requests[^1] == "/api/v1/models"
                        && ProviderModelsSurfaceCoordinator.HeartbeatInterval == TimeSpan.FromSeconds(5),
                    "heartbeat did not reclassify externally changed LM Studio residency using one catalog-only probe");

                handler.ApplyMutations = false;
                handler.FailConfirmationAfterMutation = true;
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Unconfirmed unload did not start.");
                });
                Require(handler.Loaded
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation"
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleStatus.Text.Contains("has not confirmed", StringComparison.OrdinalIgnoreCase)
                        && control.CatalogStatus.Text.Contains("last confirmed grouping", StringComparison.OrdinalIgnoreCase),
                    "an accepted but unconfirmed unload moved the row or claimed success");

                handler.NativeCatalogUnavailable = false;
                handler.FailConfirmationAfterMutation = false;
                handler.Loaded = false;
                Pump(() => coordinator.HeartbeatAsync());
                Require(control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal),
                    "a later heartbeat did not reconcile an unconfirmed unload from authoritative residency evidence");

                handler.ApplyMutations = true;
                handler.LifecycleFailureBody =
                    $"token={token}; instance=opaque-instance-secret; C:/Users/private/model.gguf; /home/private/model.gguf; http://user:pass@localhost/path?token={token}";
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Hostile failure lifecycle did not start.");
                });
                var hostileStatus = control.LifecycleStatus.Text;
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Failed"
                        && !hostileStatus.Contains(token, StringComparison.Ordinal)
                        && !hostileStatus.Contains("opaque-instance-secret", StringComparison.Ordinal)
                        && !hostileStatus.Contains("model.gguf", StringComparison.OrdinalIgnoreCase)
                        && !hostileStatus.Contains("user:pass", StringComparison.OrdinalIgnoreCase),
                    "a real hostile lifecycle error exposed credentials, paths, or an opaque instance id");
                handler.LifecycleFailureBody = "";

                handler.ApplyMutations = false;
                handler.DropLoadResponseAfterRequest = true;
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Unknown-outcome load did not start.");
                });
                Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleStatus.Text.Contains("without a definite outcome", StringComparison.OrdinalIgnoreCase),
                    "a dropped connection after the lifecycle POST was reinterpreted as a definite failure");
                Pump(() => coordinator.HeartbeatAsync());
                Require(control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleAction.IsEnabled
                        && control.LifecycleStatus.Text.Contains("not yet confirmed", StringComparison.OrdinalIgnoreCase),
                    "authoritative unchanged residency did not release an unconfirmed request for a truthful retry");

                handler.ApplyMutations = true;
                handler.MalformedLoadSuccessBody = true;
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Malformed-success load did not start.");
                });
                Require(handler.Loaded
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleAction.Content?.ToString() == "Unload model",
                    "authoritative desired residency did not win over an indeterminate lifecycle response body");

                handler.LifecycleFailureBody = "server ended without a definite response";
                handler.LifecycleFailureStatusCode = System.Net.HttpStatusCode.InternalServerError;
                handler.ApplyMutationOnLifecycleFailure = true;
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Uncertain server unload did not start.");
                });
                Require(!handler.Loaded
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleAction.Content?.ToString() == "Awaiting confirmation",
                    "a server-side lifecycle outcome followed by HTTP 500 was reinterpreted as a definite rejection");
                handler.LifecycleFailureBody = "";
                handler.LifecycleFailureStatusCode = System.Net.HttpStatusCode.BadRequest;
                handler.ApplyMutationOnLifecycleFailure = false;
                handler.NativeCatalogUnavailable = false;
                Pump(() => coordinator.HeartbeatAsync());
                Require(control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal),
                    "a later authoritative heartbeat did not reconcile a previously uncertain server lifecycle outcome");

                handler.ApplyMutations = true;
                Task? switchedSessionRefresh = null;
                handler.AfterLoadMutation = () =>
                {
                    active = alternateSession;
                    switchedSessionRefresh = coordinator.RefreshAsync(
                        refreshCatalog: true,
                        CancellationToken.None);
                };
                lifecycleTask = null;
                Pump(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycleTask ?? throw new InvalidOperationException("Post-mutation session-switch load did not start.");
                });
                if (switchedSessionRefresh is not null)
                {
                    Pump(() => switchedSessionRefresh);
                }
                Require(active?.Id == alternateSessionId
                        && handler.Loaded
                        && !control.HasUnconfirmedLifecycleReceipt
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                        && control.LifecycleStatus.Text.Contains("load state is unknown", StringComparison.OrdinalIgnoreCase)
                        && !control.LifecycleStatus.Text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase),
                    "a session switch after the LM Studio POST started was reinterpreted as a definite failure");
                // The first post-operation heartbeat may legitimately replace
                // lifecycle-stale evidence. The following heartbeat is the
                // equivalent steady-state probe whose UI commit must be skipped.
                Pump(() => coordinator.HeartbeatAsync());
                var alternateApplyCount = control.PresentationApplyCount;
                var requestsBeforeAlternateHeartbeat = handler.Requests.Count;
                Pump(() => coordinator.HeartbeatAsync());
                Require(handler.Requests.Count == requestsBeforeAlternateHeartbeat + 1
                        && handler.Requests[^1] == "/api/v1/models",
                    "the alternate-session heartbeat did not perform exactly one catalog-only probe");
                Require(control.PresentationApplyCount == alternateApplyCount,
                    $"an equivalent heartbeat reapplied alternate-session UI for an orphan receipt owned by another connection (before {alternateApplyCount}, after {control.PresentationApplyCount})");
                active = primarySession;
                Pump(() => coordinator.RefreshAsync(refreshCatalog: true));
                Require(handler.Loaded
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleStatus.Text.Contains("LM Studio now confirms", StringComparison.Ordinal)
                        && control.LifecycleStatus.Text.Contains("is loaded", StringComparison.Ordinal)
                        && control.LifecycleAction.Content?.ToString() == "Unload model",
                    "returning to the original session did not reconcile the post-mutation orphan from authoritative loaded residency");

                busy = true;
                Pump(() => coordinator.RefreshAsync(refreshCatalog: false));
                Require(!control.LifecycleAction.IsEnabled,
                    "arena-busy presentation left LM Studio lifecycle mutation enabled");
                var persisted = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Require(persisted.PersistenceRevision == initialRevision
                        && persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == modelId,
                    "load/unload or heartbeat mutated durable model routing");

                var safe = ProviderModelCatalogProjectionService.SafeStatusForDisplay(
                    $"token={token} instance_id=opaque-instance-secret C:\\private\\models\\secret.gguf C:/private/second.gguf /home/private/third.gguf /secret.gguf http://user:pass@localhost/path?token={token}",
                    token);
                Require(!safe.Contains(token, StringComparison.Ordinal)
                        && !safe.Contains("opaque-instance-secret", StringComparison.Ordinal)
                        && !safe.Contains("secret.gguf", StringComparison.OrdinalIgnoreCase)
                        && !safe.Contains("second.gguf", StringComparison.OrdinalIgnoreCase)
                        && !safe.Contains("third.gguf", StringComparison.OrdinalIgnoreCase)
                        && !safe.Contains("secret.gguf", StringComparison.OrdinalIgnoreCase)
                        && !safe.Contains("user:pass", StringComparison.OrdinalIgnoreCase),
                    "lifecycle display sanitization exposed a token, credential, or local model path");
            }
            finally
            {
                host.Close();
            }
        }
        finally
        {
            DeleteProviderCatalogTestRoot(root);
        }
    });
}

static string CreateProviderCatalogTestRoot(string label)
{
    var workspace = Path.GetFullPath(Environment.CurrentDirectory);
    Require(Directory.Exists(Path.Combine(workspace, ".git")),
        "provider catalog tests must run from the repository root");
    var ownedParent = Path.GetFullPath(Path.Combine(
        workspace,
        "artifacts",
        "codex-visual-modernization",
        "models-surface",
        "catalog"));
    Require(ownedParent.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        "provider catalog test output must remain under the repository");
    var root = Path.Combine(ownedParent, $"{label}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
}

static void DeleteProviderCatalogTestRoot(string root)
{
    var workspace = Path.GetFullPath(Environment.CurrentDirectory);
    var ownedParent = Path.GetFullPath(Path.Combine(
        workspace,
        "artifacts",
        "codex-visual-modernization",
        "models-surface",
        "catalog"));
    var resolved = Path.GetFullPath(root);
    if (resolved.StartsWith(ownedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(resolved))
    {
        Directory.Delete(resolved, recursive: true);
    }
}

private sealed class ProviderModelsLifecycleHttpHandler(string modelId) : HttpMessageHandler
{
    public bool Loaded { get; set; }
    public bool IncludeModel { get; set; } = true;
    public bool ApplyMutations { get; set; } = true;
    public bool FailConfirmationAfterMutation { get; set; }
    public bool NativeCatalogUnavailable { get; set; }
    public string LifecycleFailureBody { get; set; } = "";
    public Action? BeforeNativeCatalogResponse { get; set; }
    public bool DropLoadResponseAfterRequest { get; set; }
    public bool MalformedLoadSuccessBody { get; set; }
    public System.Net.HttpStatusCode LifecycleFailureStatusCode { get; set; } = System.Net.HttpStatusCode.BadRequest;
    public bool ApplyMutationOnLifecycleFailure { get; set; }
    public Action? AfterLoadMutation { get; set; }
    public List<string> Requests { get; } = [];
    public List<string> Bodies { get; } = [];
    public List<string> AuthorizationHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        Requests.Add(path);
        AuthorizationHeaders.Add(request.Headers.TryGetValues("Authorization", out var values)
            ? string.Join(",", values)
            : "");
        Bodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (path.EndsWith("/api/v1/models/load", StringComparison.Ordinal))
        {
            if (LifecycleFailureBody.Length > 0)
            {
                if (ApplyMutationOnLifecycleFailure)
                {
                    Loaded = true;
                    NativeCatalogUnavailable = true;
                }

                return Json(LifecycleFailureBody, LifecycleFailureStatusCode);
            }

            if (ApplyMutations)
            {
                Loaded = true;
            }

            var afterLoadMutation = AfterLoadMutation;
            AfterLoadMutation = null;
            afterLoadMutation?.Invoke();

            if (DropLoadResponseAfterRequest)
            {
                DropLoadResponseAfterRequest = false;
                throw new HttpRequestException("Connection closed after the lifecycle request started.");
            }

            if (MalformedLoadSuccessBody)
            {
                MalformedLoadSuccessBody = false;
                return Json("not-json");
            }

            if (FailConfirmationAfterMutation)
            {
                NativeCatalogUnavailable = true;
            }

            return Json("{\"load_time_seconds\":0.1}");
        }

        if (path.EndsWith("/api/v1/models/unload", StringComparison.Ordinal))
        {
            if (LifecycleFailureBody.Length > 0)
            {
                if (ApplyMutationOnLifecycleFailure)
                {
                    Loaded = false;
                    NativeCatalogUnavailable = true;
                }

                return Json(LifecycleFailureBody, LifecycleFailureStatusCode);
            }

            if (ApplyMutations)
            {
                Loaded = false;
            }

            if (FailConfirmationAfterMutation)
            {
                NativeCatalogUnavailable = true;
            }

            return Json("{}");
        }

        if (path.EndsWith("/api/v1/models", StringComparison.Ordinal))
        {
            var beforeCatalogResponse = BeforeNativeCatalogResponse;
            BeforeNativeCatalogResponse = null;
            beforeCatalogResponse?.Invoke();
            if (NativeCatalogUnavailable)
            {
                return Json("{\"error\":\"native inventory unavailable\"}", System.Net.HttpStatusCode.ServiceUnavailable);
            }

            if (!IncludeModel)
            {
                return Json("{\"models\":[]}");
            }

            var instances = Loaded
                ? "[{\"id\":\"private-runtime-instance\",\"config\":{\"context_length\":4096}}]"
                : "[]";
            return Json($$"""
                {"models":[{"type":"llm","publisher":"local","key":"{{modelId}}","display_name":"Provider observed model","loaded_instances":{{instances}},"max_context_length":8192}]}
                """);
        }

        if (path.EndsWith("/v1/models", StringComparison.Ordinal))
        {
            return Json($$"""{"data":[{"id":"{{modelId}}"}]}""");
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage Json(
        string body,
        System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}
}
