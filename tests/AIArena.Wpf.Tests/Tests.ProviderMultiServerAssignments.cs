using System.IO;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderMultiServerProjectionDisambiguatesIdenticalModelNames()
    {
        using var fixture = new MultiServerAssignmentFixture();
        var snapshot = fixture.Load();
        snapshot.Configs["beta"] = MultiServerConfig(11434, ModelProviderApiModes.OllamaNative, "ollama-assignment-secret");
        fixture.Store.SaveSnapshotAsync(snapshot, fixture.Session.Id).GetAwaiter().GetResult();
        snapshot = fixture.Load();
        var batch = ProviderModelAssignmentProjectionService.CreateBatch(fixture.Session.Id, snapshot);
        var lm = batch.Project("same-model", sourceConfig: fixture.LmStudio);
        var ollama = batch.Project("same-model", sourceConfig: fixture.Ollama);
        Require(lm.Targets.Single(target => target.Id == "alpha").Assigned
                && !lm.Targets.Single(target => target.Id == "beta").Assigned
                && !ollama.Targets.Single(target => target.Id == "alpha").Assigned
                && ollama.Targets.Single(target => target.Id == "beta").Assigned,
            "identical model names must check only the role assigned to the selected server");
        Require(lm.Targets.Single(target => target.IsDefault).Assigned
                && !ollama.Targets.Single(target => target.IsDefault).Assigned,
            "the Default checkbox must belong only to the actual default model server");
        var betaFingerprint = ollama.Targets.Single(target => target.Id == "beta").AssignmentFingerprint;
        snapshot.Configs["beta"] = MultiServerConfig(1234, ModelProviderApiModes.LmStudioNative, "lm-assignment-secret");
        var moved = ProviderModelAssignmentProjectionService.CreateBatch(fixture.Session.Id, snapshot)
            .Project("same-model", sourceConfig: fixture.Ollama);
        Require(betaFingerprint != moved.Targets.Single(target => target.Id == "beta").AssignmentFingerprint,
            "moving the same-named role model to another server must invalidate an old checkbox receipt");
    }

    static void ProviderMultiServerAssignmentsPreserveIndependentConnections()
    {
        using var fixture = new MultiServerAssignmentFixture();
        var assigned = fixture.Assign("beta", "same-model", fixture.Ollama);
        var snapshot = fixture.Load();
        Require(assigned.Ok && snapshot.Configs["beta"].BaseUrl == fixture.Ollama.BaseUrl
                && snapshot.Configs["beta"].ApiMode == ModelProviderApiModes.OllamaNative
                && snapshot.Configs["beta"].ApiToken == "ollama-assignment-secret"
                && snapshot.Configs["beta"].Model == "same-model",
            "assigning an Ollama model must persist its server, adapter, token, and original model identifier together");
        Require(snapshot.Configs["alpha"].BaseUrl == fixture.LmStudio.BaseUrl
                && snapshot.Configs["alpha"].ApiToken == "lm-assignment-secret"
                && snapshot.Configs["alpha"].Temperature == 0.35,
            "assigning Beta to Ollama must leave Alpha's LM Studio connection and generation override intact");
        var defaultChanged = fixture.Assign("shared", "another-default", fixture.Ollama);
        snapshot = fixture.Load();
        Require(defaultChanged.Ok && snapshot.Configs["shared"].BaseUrl == fixture.Ollama.BaseUrl
                && snapshot.Configs["shared"].Model == "another-default",
            "choosing another server's Default model must switch the shared fallback connection");
        Require(snapshot.Configs["alpha"].BaseUrl == fixture.LmStudio.BaseUrl
                && snapshot.Configs["alpha"].Model == "same-model"
                && snapshot.Configs["alpha"].ApiMode == ModelProviderApiModes.LmStudioNative
                && snapshot.Configs["alpha"].ApiToken == "lm-assignment-secret"
                && snapshot.Configs["beta"].BaseUrl == fixture.Ollama.BaseUrl
                && snapshot.Configs["beta"].Model == "same-model",
            "changing Default must preserve explicit role assignments and their independent server credentials");
        Require(fixture.RefreshFlags.Count == 2 && fixture.RefreshFlags.All(refreshModels => !refreshModels),
            "model assignments must persist routing without starting model load or discovery operations");
        var audit = File.ReadAllText(fixture.Events.EventPath(fixture.Session.Id));
        Require(!audit.Contains("lm-assignment-secret", StringComparison.Ordinal)
                && !audit.Contains("ollama-assignment-secret", StringComparison.Ordinal),
            "multi-server assignment audit records must not expose either provider credential");
    }

    static void ProviderMultiServerRejectsStaleSourceCredentials()
    {
        foreach (var token in new[] { "rotated-credential", "" })
        {
            using var fixture = new MultiServerAssignmentFixture();
            var snapshot = fixture.Load();
            snapshot.Configs["alpha"] = MultiServerConfig(11434, ModelProviderApiModes.OllamaNative, token);
            fixture.Store.SaveSnapshotAsync(snapshot, fixture.Session.Id).GetAwaiter().GetResult();
            var before = fixture.Load();
            var beforeConfigs = System.Text.Json.JsonSerializer.Serialize(before.Configs);
            var beforeSettings = System.Text.Json.JsonSerializer.Serialize(before.ModelSettings);
            var assignment = fixture.Assign("beta", "same-model", fixture.Ollama);
            var configuration = fixture.Configure(fixture.Ollama, 4096, ModelResponseTones.Concise);
            var connection = fixture.Service.ApplyAsync(new AIArenaProviderConfigurationPatch(
                BaseUrl: fixture.Ollama.BaseUrl, ApiMode: fixture.Ollama.ApiMode, ApiToken: fixture.Ollama.ApiToken,
                ClearApiToken: false, Model: null, TimeoutSeconds: null, Temperature: null, MaxOutputTokens: null,
                ContextLength: null, Reasoning: null, NativeStatefulChat: null, NativeIdleTtlSeconds: null,
                RoleModels: new Dictionary<string, string>(), RefreshModels: true),
                expectedSourceConfig: fixture.Ollama).GetAwaiter().GetResult();
            Require(!connection.Ok && connection.ErrorCode == "connection_changed",
                "a connection probe must not restore a source credential changed while discovery was running");
            snapshot = fixture.Load();
            Require(!assignment.Ok && assignment.ErrorCode == "stale_provider" && !configuration.Ok,
                "a cached source must be rejected after another role rotates or clears that server's credential");
            Require(snapshot.PersistenceRevision == before.PersistenceRevision
                    && System.Text.Json.JsonSerializer.Serialize(snapshot.Configs) == beforeConfigs
                    && System.Text.Json.JsonSerializer.Serialize(snapshot.ModelSettings) == beforeSettings
                    && snapshot.Configs["alpha"].ApiToken == token && fixture.RefreshFlags.Count == 0,
                "stale source writes must not restore credentials, assign a role, or alter model settings");
            var currentSource = MultiServerConfig(11434, ModelProviderApiModes.OllamaNative, token);
            Require(fixture.Assign("beta", "same-model", currentSource).Ok,
                "refreshing the source must allow the same assignment with its current credential");
        }
    }

    static void ProviderMultiServerModelSettingsStayOnTheirSourceServer()
    {
        using var fixture = new MultiServerAssignmentFixture();
        var ollamaResult = fixture.Configure(fixture.Ollama, 4096, ModelResponseTones.Concise);
        Require(ollamaResult.Ok && ollamaResult.Configuration.ConfiguredContextWindow == 4096
                && ollamaResult.Configuration.ResponseTone == ModelResponseTones.Concise,
            "saving an Ollama model's settings must return that server's authoritative settings");
        var snapshot = fixture.Load();
        var untouchedLm = ProviderConfigurationControlService.CaptureModelConfiguration(
            fixture.Session.Id, snapshot, "same-model", sourceConfig: fixture.LmStudio);
        Require(untouchedLm.ConfiguredContextWindow == 0 && untouchedLm.ResponseTone == ModelResponseTones.Default,
            "same-named LM Studio model settings must not change when Ollama settings are saved");
        var lmResult = fixture.Configure(fixture.LmStudio, 2048, ModelResponseTones.Analytical);
        snapshot = fixture.Load();
        var preservedOllama = ProviderConfigurationControlService.CaptureModelConfiguration(
            fixture.Session.Id, snapshot, "same-model", sourceConfig: fixture.Ollama);
        Require(lmResult.Ok && preservedOllama.ConfiguredContextWindow == 4096
                && preservedOllama.ResponseTone == ModelResponseTones.Concise,
            "independent context and response-tone settings must survive a later update to another server's same-named model");
        Require(snapshot.ModelSettings.ContainsKey(ModelRuntimeSettingsRegistry.Identity(fixture.LmStudio))
                && snapshot.ModelSettings.ContainsKey(ModelRuntimeSettingsRegistry.Identity(fixture.Ollama)),
            "model settings must remain indexed by the existing server-qualified runtime identities");
    }

    static void ProviderMultiServerCoreRoutingRetainsSameNamedRoleServers()
    {
        using var fixture = new MultiServerAssignmentFixture();
        var snapshot = fixture.Load();
        // Legacy snapshots can lack the explicit marker. A distinct server is
        // still an explicit role route even when its model name matches Default.
        snapshot.Configs["beta"] = new ModelProviderConfig
        {
            BaseUrl = fixture.Ollama.BaseUrl, ApiMode = fixture.Ollama.ApiMode,
            ApiToken = fixture.Ollama.ApiToken, Model = "same-model"
        };
        var beta = ModelProviderRouting.Resolve(snapshot, "beta", out var fallback);
        Require(beta?.BaseUrl == fixture.Ollama.BaseUrl && fallback?.BaseUrl == fixture.LmStudio.BaseUrl,
            "same-named models on different servers must retain the selected role route and distinct default fallback");
        snapshot.Engine.DefaultForUnassignedAgentsEnabled = false;
        beta = ModelProviderRouting.Resolve(snapshot, "beta", out fallback);
        var alpha = ModelProviderRouting.Resolve(snapshot, "alpha", out _);
        Require(beta?.ApiMode == ModelProviderApiModes.OllamaNative && beta.ApiToken == "ollama-assignment-secret"
                && fallback is null && alpha?.ApiMode == ModelProviderApiModes.LmStudioNative,
            "disabling Default must keep explicit or legacy-distinct role connections on their own servers");
        Require(ModelProviderRouting.Resolve(snapshot, "gamma", out _) is null,
            "an unassigned role must remain unassigned when Default is disabled");
    }

    private static ModelProviderConfig MultiServerConfig(int port, string mode, string token) => new()
    {
        BaseUrl = $"http://127.0.0.1:{port}/v1", ApiMode = mode, ApiToken = token,
        Model = "same-model", ExplicitModelAssignment = true, Temperature = 0.35, MaxOutputTokens = 777
    };

    private sealed class MultiServerAssignmentFixture : IDisposable
    {
        private readonly string root = CreateProviderCatalogTestRoot("multi-server-assignment");
        private readonly SemaphoreSlim operationLock = new(1, 1);
        public ModelProviderConfig LmStudio { get; } = MultiServerConfig(1234, ModelProviderApiModes.LmStudioNative, "lm-assignment-secret");
        public ModelProviderConfig Ollama { get; } = MultiServerConfig(11434, ModelProviderApiModes.OllamaNative, "ollama-assignment-secret");
        public SessionStore Store { get; }
        public EventLogStore Events { get; }
        public SessionSummary Session { get; }
        public ProviderConfigurationControlService Service { get; }
        public List<bool> RefreshFlags { get; } = [];
        public MultiServerAssignmentFixture()
        {
            Store = new SessionStore(root);
            Events = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs.Clear();
            snapshot.Configs["shared"] = LmStudio;
            snapshot.Configs["alpha"] = LmStudio;
            Store.SaveSnapshotAsync(snapshot, "multi-server").GetAwaiter().GetResult();
            Session = Store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            Service = new ProviderConfigurationControlService(Store, Events, operationLock, () => Session, () => false,
                (_, refresh, _) => { RefreshFlags.Add(refresh); return Task.CompletedTask; });
        }
        public ArenaSnapshot Load() => Store.LoadSnapshotAsync(Session.Id).GetAwaiter().GetResult()!;
        public ProviderModelAssignmentControlResult Assign(string role, string model, ModelProviderConfig source)
        {
            var projection = ProviderModelAssignmentProjectionService.CreateBatch(Session.Id, Load()).Project(model, sourceConfig: source);
            var target = projection.Targets.Single(item => item.Id == role);
            return Service.SetModelAssignmentAsync(new ProviderModelAssignmentRequest(role, model, true,
                projection.ProviderFingerprint, target.AssignmentFingerprint), sourceConfig: source).GetAwaiter().GetResult();
        }
        public ProviderModelConfigurationControlResult Configure(ModelProviderConfig source, int context, string tone)
        {
            var projection = ProviderConfigurationControlService.CaptureModelConfiguration(Session.Id, Load(), "same-model", sourceConfig: source);
            return Service.SetModelConfigurationAsync(new ProviderModelConfigurationRequest("same-model", context,
                ModelHistoryPolicies.Rolling80, tone, "", projection.ConfigurationIdentity), sourceConfig: source).GetAwaiter().GetResult();
        }
        public void Dispose()
        {
            operationLock.Dispose();
            DeleteProviderCatalogTestRoot(root);
        }
    }
}