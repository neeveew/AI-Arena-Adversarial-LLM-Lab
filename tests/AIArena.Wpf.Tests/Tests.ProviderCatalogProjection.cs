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

static void ProviderCatalogProjectionMergesTransitiveAliases()
{
    var config = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        Model = "beta"
    };
    var projection = new ProviderModelCatalogProjectionService();
    var lease = projection.BeginRefresh("alias-session", config);
    var source = LmStudioModelCatalog.Success(
    [
        Model("alpha", "Alpha", ["alpha", "left"], loaded: false, maxContextLength: 32768),
        Model("beta", "Beta", ["beta", "right"], loaded: true, maxContextLength: 8192, loadedContextLength: 8192),
        Model("bridge", "Bridge", ["bridge", "left", "right"], loaded: false)
    ]);

    var result = ProviderModelCatalogProjectionService.FromLmStudio(
        lease,
        source,
        config.Model,
        DateTimeOffset.UnixEpoch);

    var merged = result.LoadedModels.Single();
    Require(result.AvailableModels.Count == 0
            && result.OmittedModelCount == 0
            && !result.ConfiguredModelMissing
            && result.ConfiguredModel == "alpha",
        "transitively overlapping aliases should form one complete configured catalog row");
    Require(merged.Id == "alpha"
            && merged.LoadState == ProviderModelLoadState.Loaded
            && merged.CanUnload
            && !merged.CanLoad
            && merged.ContextLength == 8192
            && new[] { "alpha", "left", "beta", "right", "bridge" }.All(alias =>
                merged.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)),
        "alias union should retain a deterministic canonical identity, every safe alias, and the strongest observed residency evidence");

    var reversedLease = projection.BeginRefresh("alias-session-reversed", config);
    var reversedResult = ProviderModelCatalogProjectionService.FromLmStudio(
        reversedLease,
        LmStudioModelCatalog.Success(source.Models.Reverse().ToArray()),
        config.Model,
        DateTimeOffset.UnixEpoch.AddSeconds(5));
    var reversedMerged = reversedResult.LoadedModels.Single();
    Require(reversedMerged.Id == merged.Id
            && reversedResult.ConfiguredModel == result.ConfiguredModel
            && reversedMerged.Aliases.SequenceEqual(merged.Aliases, StringComparer.OrdinalIgnoreCase),
        "reordering equivalent provider aliases changed the canonical Models row key or alias contract across heartbeats");

    var snapshot = SessionStore.CreateDefaultSnapshot();
    snapshot.Configs[ModelProviderRouting.SharedConfigKey] = config;
    var assignment = ProviderModelAssignmentProjectionService
        .CreateBatch("alias-session", snapshot)
        .Project(merged.Id, merged.Aliases);
    Require(assignment.Targets.Single(target => target.IsDefault).Assigned,
        "a configured provider alias did not project onto its canonical model row assignment");

    var availableLease = projection.BeginRefresh("available-alias-session", new ModelProviderConfig
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        Model = "unavailable-first"
    });
    var availableSource = LmStudioModelCatalog.Success(
    [
        Model("unavailable-first", "Shared Alias", ["unavailable-first", "shared-alias"], loaded: false, maxContextLength: 32768, hasResidencyEvidence: false),
        Model("available-second", "Shared Alias", ["available-second", "shared-alias"], loaded: false, maxContextLength: 8192)
    ]);
    var availableResult = ProviderModelCatalogProjectionService.FromLmStudio(
        availableLease,
        availableSource,
        "unavailable-first",
        DateTimeOffset.UnixEpoch);
    var availableMerged = availableResult.AvailableModels.Single();
    Require(availableMerged.LoadState == ProviderModelLoadState.NotLoaded
            && availableMerged.CanLoad
            && availableMerged.ContextLength == 8192,
        "deduplication exposed Available while retaining metadata from a weaker unavailable alias");

    static LmStudioModelInfo Model(
        string key,
        string displayName,
        IReadOnlyList<string> aliases,
        bool loaded,
        int maxContextLength = 4096,
        int loadedContextLength = 4096,
        bool hasResidencyEvidence = true)
    {
        return new LmStudioModelInfo(
            Key: key,
            DisplayName: displayName,
            Type: "llm",
            Publisher: "local",
            Architecture: "test",
            QuantizationName: "Q4",
            BitsPerWeight: 4,
            SizeBytes: 1_000_000,
            ParamsString: "1B",
            LoadedInstances: loaded ? [new LmStudioLoadedInstance("opaque-instance", loadedContextLength, 1, null, null)] : [],
            MaxContextLength: maxContextLength,
            Format: "gguf",
            Vision: false,
            TrainedForToolUse: false,
            ReasoningOptions: [],
            ReasoningDefault: "",
            SelectedVariant: "",
            Aliases: aliases,
            Description: "",
            HasResidencyEvidence: hasResidencyEvidence);
    }
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

    Require(compatible.CatalogEvidence == ProviderCatalogEvidenceState.Partial
        && compatible.ResidencyEvidence == ProviderCatalogEvidenceState.Unavailable
        && compatible.LoadedModels.Count == 0,
        "display-capped compatible catalogs must report partial catalog evidence without inferring loaded residency");
    Require(compatible.AvailableModels.Count == ProviderModelCatalogProjectionService.MaximumModelCount
        && compatible.OmittedModelCount == 45,
        "generic model catalogs should deduplicate and retain a bounded 256-row projection");
    Require(compatible.ConfiguredModelMissing
        && compatible.AvailableModels[0].IsConfiguredOnly
        && compatible.AvailableModels[0].Id == "configured-only",
        "a configured model missing from a fresh catalog should remain visible as a neutral pinned row");

    var sourceCappedLease = projection.BeginRefresh("compatible-source-cap", compatibleConfig);
    var sourceCapped = ProviderModelCatalogProjectionService.FromCompatible(
        sourceCappedLease,
        Enumerable.Range(0, 1024)
            .Select(index => $"source-capped-{index:0000}")
            .ToArray(),
        catalogAvailable: true,
        error: "",
        configuredModel: "source-capped-0000",
        checkedAt: DateTimeOffset.UnixEpoch,
        additionalOmittedModelCount: 476);
    Require(sourceCapped.CatalogEvidence == ProviderCatalogEvidenceState.Partial
            && sourceCapped.ResidencyEvidence == ProviderCatalogEvidenceState.Unavailable
            && sourceCapped.AvailableModels.Count == ProviderModelCatalogProjectionService.MaximumModelCount
            && sourceCapped.OmittedModelCount == 1244
            && sourceCapped.Status.Contains("1244", StringComparison.Ordinal)
            && sourceCapped.Status.Contains("omitted", StringComparison.OrdinalIgnoreCase),
        "compatible source-parser and display omissions did not combine on the Models surface as explicit Partial evidence");
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

static void ProviderModelAssignmentBatchesSharedProjectionState()
{
    var snapshot = SessionStore.CreateDefaultSnapshot();
    snapshot.PersistenceRevision = 42;
    snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "batch-secret",
        Model = "default-model"
    };
    snapshot.Configs["alpha"] = new ModelProviderConfig { Model = "alpha-model" };
    snapshot.Configs["narrator"] = new ModelProviderConfig { Model = "narrator-model" };

    var batch = ProviderModelAssignmentProjectionService.CreateBatch("batch-session", snapshot);
    var projections = batch.ProjectMany(["default-model", "alpha-model", "unassigned-model"]);

    Require(projections.Count == 3
            && projections.All(item => item.SessionId == "batch-session"
                && item.PersistenceRevision == 42
                && ReferenceEquals(item.ProviderFingerprint, batch.ProviderFingerprint)),
        "one assignment batch should reuse its snapshot identity for every projected model");
    Require(!batch.ProviderFingerprint.Contains("batch-secret", StringComparison.Ordinal),
        "the reusable batch identity must remain privacy safe");

    var defaultProjection = projections[0];
    var alphaProjection = projections[1];
    var otherProjection = projections[2];
    Require(defaultProjection.Targets.Single(target => target.IsDefault).Assigned
            && !alphaProjection.Targets.Single(target => target.IsDefault).Assigned,
        "batch projection should preserve exact Default assignment semantics per requested model");
    Require(alphaProjection.Targets.Single(target => target.Id == "alpha").Assigned
            && !defaultProjection.Targets.Single(target => target.Id == "alpha").Assigned
            && otherProjection.Targets.All(target => !target.Assigned),
        "batch projection should preserve explicit and unmatched target assignment truth");
    Require(defaultProjection.Targets.Select(target => target.Id)
            .SequenceEqual(alphaProjection.Targets.Select(target => target.Id), StringComparer.Ordinal)
            && defaultProjection.Targets.Zip(alphaProjection.Targets).All(pair =>
                ReferenceEquals(pair.First.AssignmentFingerprint, pair.Second.AssignmentFingerprint)),
        "target order and precomputed assignment fingerprints should be reused across a batch");

    var serviceBatch = ProviderModelAssignmentProjectionService.ProjectMany(
        "batch-session",
        snapshot,
        ["default-model", "alpha-model"]);
    Require(serviceBatch.Select(item => item.Model).SequenceEqual(["default-model", "alpha-model"]),
        "the batch convenience API should preserve caller order without recomputing per-model snapshot state");
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

        var aliasSnapshot = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("alias assignment snapshot should load");
        ProviderConfigurationControlService.SaveRoleModelConfig(
            aliasSnapshot.Configs,
            "theta",
            "yi-coder-alias",
            aliasSnapshot.Configs[ModelProviderRouting.SharedConfigKey],
            temperatureOverride: 1.25,
            maxOutputTokensOverride: 7777);
        sessionStore.SaveSnapshotAsync(aliasSnapshot, "assignment-session").GetAwaiter().GetResult();
        var aliasCurrent = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("persisted alias assignment snapshot should load");
        var aliasProjection = ProviderModelAssignmentProjectionService
            .CreateBatch("assignment-session", aliasCurrent)
            .Project("yi-coder", ["yi-coder-alias"]);
        var aliasTheta = aliasProjection.Targets.Single(target => target.Id == "theta");
        Require(aliasTheta.Assigned,
            "an explicit provider alias did not render as assigned on its canonical catalog row");
        var aliasUnassigned = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "theta",
                "yi-coder",
                Assigned: false,
                aliasProjection.ProviderFingerprint,
                aliasTheta.AssignmentFingerprint,
                EquivalentModelIds: ["yi-coder-alias"]))
            .GetAwaiter()
            .GetResult();
        var afterAliasUnassign = sessionStore.LoadSnapshotAsync("assignment-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("alias-unassigned snapshot should load");
        Require(aliasUnassigned.Ok
                && afterAliasUnassign.Configs["theta"].Model == "yi-coder"
                && aliasUnassigned.Assignment.Targets.Single(target => target.Id == "theta").InheritsDefault
                && Math.Abs(afterAliasUnassign.Configs["theta"].Temperature - 1.25) < 0.000001
                && afterAliasUnassign.Configs["theta"].MaxOutputTokens == 7777
                && refreshFlags.SequenceEqual([false, false, false, false]),
            "unchecking an alias-equivalent explicit route did not restore Default inheritance atomically");

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

static void ProviderOptionalDefaultPreservesExplicitRoutesAndDormantOverrides()
{
    var root = CreateProviderCatalogTestRoot("optional-default");
    try
    {
        const string sessionId = "optional-default-session";
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var shared = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            Model = "model-a",
            Temperature = 0.7,
            MaxOutputTokens = 4096
        };
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = shared;
        ProviderConfigurationControlService.SaveRoleModelConfig(
            snapshot.Configs,
            "beta",
            "",
            shared,
            temperatureOverride: 1.2,
            maxOutputTokensOverride: 7777,
            explicitAssignment: false);
        sessionStore.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
        var active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
            .Single(session => session.Id == sessionId);
        var refreshes = new List<bool>();
        using var operationLock = new SemaphoreSlim(1, 1);
        var service = new ProviderConfigurationControlService(
            sessionStore,
            eventLogStore,
            operationLock,
            () => active,
            () => false,
            (_, refreshModels, _) =>
            {
                refreshes.Add(refreshModels);
                return Task.CompletedTask;
            });

        var initial = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("optional-default snapshot should load");
        var initialRevision = initial.PersistenceRevision;
        var modelA = ProviderModelAssignmentProjectionService.Project(sessionId, initial, "model-a");
        var alphaTarget = modelA.Targets.Single(target => target.Id == "alpha");
        var explicitAlpha = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                "alpha",
                "model-a",
                Assigned: true,
                modelA.ProviderFingerprint,
                alphaTarget.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        Require(explicitAlpha.Ok, "same-as-shared explicit role assignment should save");

        var afterExplicitAlpha = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("same-as-shared explicit snapshot should load");
        var aliasDefaultProjection = ProviderModelAssignmentProjectionService
            .CreateBatch(sessionId, afterExplicitAlpha)
            .Project("model-a-canonical", ["model-a"]);
        var defaultTarget = aliasDefaultProjection.Targets.Single(target => target.IsDefault);
        var disabled = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "model-a-canonical",
                Assigned: false,
                aliasDefaultProjection.ProviderFingerprint,
                defaultTarget.AssignmentFingerprint,
                EquivalentModelIds: ["model-a"]))
            .GetAwaiter()
            .GetResult();
        var afterDisable = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("default-disabled snapshot should load");
        var disabledAlpha = ModelProviderRouting.Resolve(afterDisable, "alpha", out var disabledAlphaFallback);
        var disabledBeta = ModelProviderRouting.Resolve(afterDisable, "beta", out var disabledBetaFallback);
        Require(disabled.Ok
                && !afterDisable.Engine.DefaultForUnassignedAgentsEnabled
                && afterDisable.Configs[ModelProviderRouting.SharedConfigKey].Model == "model-a"
                && afterDisable.Configs["alpha"].Model == "model-a"
                && afterDisable.Configs["alpha"].ExplicitModelAssignment
                && disabledAlpha?.Model == "model-a"
                && disabledAlphaFallback is null,
            "turning Default off should leave the shared provider model intact and preserve an explicit same-model route");
        Require(disabledBeta is null
                && disabledBetaFallback is null
                && afterDisable.Configs["beta"].Model == "model-a"
                && !afterDisable.Configs["beta"].ExplicitModelAssignment
                && Math.Abs(afterDisable.Configs["beta"].Temperature - 1.2) < 0.000001
                && afterDisable.Configs["beta"].MaxOutputTokens == 7777,
            "turning Default off should make inherited roles unassigned without losing dormant generation overrides");
        var disabledProjection = disabled.Assignment;
        Require(!disabledProjection.Targets.Single(target => target.IsDefault).Assigned
                && disabledProjection.Targets.Single(target => target.Id == "alpha").Assigned
                && !disabledProjection.Targets.Single(target => target.Id == "beta").Assigned
                && !disabledProjection.Targets.Single(target => target.Id == "beta").InheritsDefault,
            "disabled-default projection should distinguish explicit, inherited, and unassigned roles truthfully");

        var rawDisabledProjection = ProviderModelAssignmentProjectionService.Project(sessionId, afterDisable, "model-a");
        var reenabledTarget = rawDisabledProjection.Targets.Single(target => target.IsDefault);
        var reenabled = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "model-a",
                Assigned: true,
                rawDisabledProjection.ProviderFingerprint,
                reenabledTarget.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        Require(reenabled.Ok, "turning the same Default back on should save");

        var modelB = ProviderModelAssignmentProjectionService.Project(
            sessionId,
            sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("re-enabled snapshot should load"),
            "model-b");
        var switched = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "model-b",
                Assigned: true,
                modelB.ProviderFingerprint,
                modelB.Targets.Single(target => target.IsDefault).AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        var final = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("switched-default snapshot should load");
        Require(switched.Ok
                && final.PersistenceRevision == initialRevision + 4
                && final.Engine.DefaultForUnassignedAgentsEnabled
                && final.Configs[ModelProviderRouting.SharedConfigKey].Model == "model-b"
                && final.Configs["alpha"].Model == "model-a"
                && final.Configs["alpha"].ExplicitModelAssignment
                && final.Configs["beta"].Model == "model-b"
                && !final.Configs["beta"].ExplicitModelAssignment
                && Math.Abs(final.Configs["beta"].Temperature - 1.2) < 0.000001
                && final.Configs["beta"].MaxOutputTokens == 7777,
            "re-enabling or replacing Default should preserve explicit routes and reactivate inherited override carriers atomically");
        Require(refreshes.SequenceEqual([false, false, false, false]),
            "routing toggles must never request provider residency changes");
        Require(File.ReadLines(eventLogStore.EventPath(sessionId)).Count(line =>
                line.Contains("provider_model_assignment_changed", StringComparison.Ordinal)) == 4,
            "each routing change should emit exactly one secret-safe assignment event");
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
        var unsetDefault = service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(
                ModelProviderRouting.SharedConfigKey,
                "default-model",
                Assigned: false,
                projection.ProviderFingerprint,
                defaultTarget.AssignmentFingerprint))
            .GetAwaiter()
            .GetResult();
        var afterDefaultOff = sessionStore.LoadSnapshotAsync("stale-session").GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("default-off snapshot should load");
        Require(unsetDefault.Ok
                && !afterDefaultOff.Engine.DefaultForUnassignedAgentsEnabled
                && afterDefaultOff.Configs[ModelProviderRouting.SharedConfigKey].Model == "default-model",
            "the active Default should turn off without clearing the shared provider model");

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
        Require(persisted.Configs["alpha"].Model == "concurrent-model" && refreshCount == 1,
            "only the successful Default-off change should refresh; rejected stale and inactive assignments must not reroute agents");
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
            // Provider presentation updates deliberately restore selection, focus, and
            // viewport continuity at ContextIdle. Let that queued UI work settle before
            // the next lifecycle action so this hosted scenario observes the same stable
            // state a user sees after the async operation completes.
            dispatcher.Invoke(
                static () => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
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
            // Choose a genuinely different observed model. Alias-equivalent defaults are
            // already assigned and intentionally non-actionable; this fixture isolates the
            // routing-only ProviderFingerprint refresh that occurs during a real change.
            const string originalDefault = "old-default-model";
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
            var requestedProjection = ProviderModelAssignmentProjectionService.CreateBatch(sessionId, initial)
                .Project(requestedDefault, ["Provider observed model"]);
            Require(initial.Configs[ModelProviderRouting.SharedConfigKey].Model == originalDefault
                    && !requestedProjection.Targets.Single(target => target.IsDefault).Assigned,
                $"Test setup did not retain a distinct default ('{initial.Configs[ModelProviderRouting.SharedConfigKey].Model}').");

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
                    $"Provider-observed model Default target was not actionable (selected '{control.SelectedModelId}', enabled {defaultTarget.IsEnabled}, status '{AutomationProperties.GetItemStatus(defaultTarget)}', help '{AutomationProperties.GetHelpText(defaultTarget)}').");
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
                        && control.AssignmentStatus.Text.Contains(
                            "Changed Default for unassigned agents to Provider observed model",
                            StringComparison.Ordinal)
                        && !control.AssignmentStatus.Text.Contains("provider or session changed", StringComparison.OrdinalIgnoreCase),
                    $"The host refresh before service completion did not retain the completed Default transfer "
                    + $"(pending={control.HasPendingAssignment}, selected='{control.SelectedModelId}', "
                    + $"checked={savedDefaultTarget.IsChecked}, itemStatus='{AutomationProperties.GetItemStatus(control.AssignmentStatus)}', "
                    + $"status='{control.AssignmentStatus.Text}').");
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

static void ProviderModelsStaleResidencyMergeRemainsBoundedAndProviderNeutral()
{
    const string sessionId = "bounded-stale-residency";
    const string fingerprint = "same-provider-fingerprint";
    var previousItems = Enumerable.Range(0, ProviderModelCatalogProjectionService.MaximumModelCount)
        .Select(index => Item($"prior-{index:000}", ProviderModelLoadState.NotLoaded))
        .ToArray();
    var candidateItems = Enumerable.Range(0, ProviderModelCatalogProjectionService.MaximumModelCount)
        .Select(index => Item($"candidate-{index:000}", ProviderModelLoadState.Unavailable))
        .ToArray();
    var previous = new ProviderModelCatalogSnapshot(
        1,
        sessionId,
        fingerprint,
        ProviderCatalogEvidenceState.Ready,
        ProviderCatalogEvidenceState.Ready,
        [],
        previousItems,
        "prior-000",
        false,
        0,
        "256 available.",
        DateTimeOffset.UnixEpoch);
    var candidate = new ProviderModelCatalogSnapshot(
        2,
        sessionId,
        fingerprint,
        ProviderCatalogEvidenceState.Ready,
        ProviderCatalogEvidenceState.Unavailable,
        [],
        candidateItems,
        "candidate-000",
        false,
        0,
        "Ollama running inventory unavailable.",
        DateTimeOffset.UnixEpoch.AddSeconds(5));

    var merged = ProviderModelsSurfaceCoordinator.PreserveLastConfirmedResidency(
        previous,
        candidate,
        preserveMissingRows: true,
        selectedModelId: "prior-255");

    Require(merged.Models.Count == ProviderModelCatalogProjectionService.MaximumModelCount
            && merged.Models.Any(item => item.Id == "candidate-000")
            && merged.Models.Any(item => item.Id == "prior-255")
            && merged.CatalogEvidence == ProviderCatalogEvidenceState.Partial
            && merged.OmittedModelCount == ProviderModelCatalogProjectionService.MaximumModelCount,
        "merging disjoint last-confirmed residency exceeded the 256-row cap or dropped configured/selected pins without omission evidence");
    Require(merged.Status.Contains("Provider load-state", StringComparison.Ordinal)
            && merged.Status.Contains("256", StringComparison.Ordinal)
            && !merged.Status.Contains("LM Studio", StringComparison.OrdinalIgnoreCase),
        "provider-neutral stale-residency evidence was mislabeled or omitted its bounded-merge count");
    Require(!ProviderModelsSurfaceCoordinator.LifecycleHelpFor(merged.Models[0], lmStudioLifecycle: false)
                .Contains("LM Studio", StringComparison.OrdinalIgnoreCase)
            && !ProviderModelsSurfaceCoordinator.LifecycleHelpFor(
                    Item("ollama-loaded", ProviderModelLoadState.Loaded),
                    lmStudioLifecycle: false)
                .Contains("LM Studio", StringComparison.OrdinalIgnoreCase),
        "Ollama stale or loaded rows exposed LM Studio-specific lifecycle guidance");

    static ProviderModelCatalogItem Item(string id, ProviderModelLoadState state) => new(
        id,
        id,
        state,
        CanLoad: false,
        CanUnload: false,
        "local",
        "Q4",
        4096,
        1_000_000,
        "chat",
        [id]);
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
                FlushProviderModelsDispatcher(host);
                Require(control.SelectedModelId == modelId
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.CatalogStatus.Text.Contains("Auto-checking every 5 seconds", StringComparison.Ordinal),
                    $"LM Studio available evidence did not expose the selected Load action and five-second heartbeat contract "
                    + $"(selected='{control.SelectedModelId}', rows={control.CatalogRowCount}, visible={control.VisibleCatalogRowCount}, "
                    + $"action='{control.LifecycleAction.Content}', status='{control.CatalogStatus.Text}')");

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
                FlushProviderModelsDispatcher(host);
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
                FlushProviderModelsDispatcher(host);
                Require(!handler.Loaded
                        && control.SelectedModelId == modelId
                        && control.LifecycleAction.Content?.ToString() == "Load model"
                        && control.LifecycleStatus.Text.Contains("unloaded from LM Studio", StringComparison.OrdinalIgnoreCase)
                        && handler.Requests.Count(path => path == "/api/v1/models/unload") == 1,
                    $"confirmed LM Studio unload did not return the retained selection to Available exactly once "
                    + $"(loaded={handler.Loaded}, selected='{control.SelectedModelId}', action='{control.LifecycleAction.Content}', "
                    + $"status='{control.LifecycleStatus.Text}', unloadRequests={handler.Requests.Count(path => path == "/api/v1/models/unload")})");

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
                FlushProviderModelsDispatcher(host);
                Require(handler.Loaded
                        && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                        && control.LifecycleAction.Content?.ToString() == "Unload model"
                        && control.LifecycleAction.IsEnabled,
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
                    return lifecycleTask ?? throw new InvalidOperationException(
                        $"Post-mutation session-switch load did not start (enabled={control.LifecycleAction.IsEnabled}, content='{control.LifecycleAction.Content}', itemStatus='{AutomationProperties.GetItemStatus(control.LifecycleAction)}', lifecycle='{control.LifecycleStatus.Text}').");
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
                    "a session switch after the LM Studio POST started was reinterpreted as a definite failure "
                    + $"(active='{active?.Id}', expected='{alternateSessionId}', loaded={handler.Loaded}, "
                    + $"receipt={control.HasUnconfirmedLifecycleReceipt}, status='{AutomationProperties.GetItemStatus(control.LifecycleStatus)}', "
                    + $"text='{control.LifecycleStatus.Text}', action='{control.LifecycleAction.Content}', selected='{control.SelectedModelId}')");
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
                    "returning to the original session did not reconcile the post-mutation orphan from authoritative loaded residency "
                    + $"(selected='{control.SelectedModelId}', receipt={control.HasUnconfirmedLifecycleReceipt}, "
                    + $"status='{AutomationProperties.GetItemStatus(control.LifecycleStatus)}', text='{control.LifecycleStatus.Text}', "
                    + $"action='{control.LifecycleAction.Content}', enabled={control.LifecycleAction.IsEnabled}, "
                    + $"loaded={control.LoadedRowCount}, catalog={control.AvailableCatalogRowCount})");

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
