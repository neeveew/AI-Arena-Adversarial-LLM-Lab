using System.Text.Json;
using System.IO;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void CollaborateUsesAssignedServerAndSettings()
    {
        RunStaTest(() =>
        {
            var core = WorkspaceRoutingSnapshot();
            var view = WorkspaceRoutingView(core);
            var client = new SequentialAgentModelClient("A useful answer.");
            var coordinator = CreateCollaborateCoordinatorForTest(client,
                new TextBox { Text = "Explain the tradeoffs." }, new TextBlock(),
                () => view, _ => { }, new RecordingCollaborateHistoryStore());
            coordinator.Initialize();
            coordinator.SendAsync().GetAwaiter().GetResult();

            Require(client.CompletedConfigs.Count == 1, "Collaborate did not complete its assigned Narrator call.");
            var sent = client.CompletedConfigs.Single();
            Require(sent.BaseUrl == "http://127.0.0.1:11434/v1" && sent.ApiMode == "ollama_native"
                    && sent.ApiToken == "role-token-fixture" && sent.Model == "same-model",
                "Collaborate replaced the assigned server, adapter, or credentials with the shared connection.");
            Require(sent.ConfiguredContextWindow == 4096 && sent.ContextLength == 4096
                    && sent.HistoryPolicy == "rolling_80" && sent.ResponseTone == "custom"
                    && sent.CustomTone == "State the evidence clearly." && sent.Temperature == 0
                    && sent.Timeout == 900 && sent.Reasoning == "low" && !sent.NativeStatefulChat
                    && sent.NativeIdleTtlSeconds == 42 && sent.Extra?.ContainsKey("routing_fixture") == true,
                "Collaborate dropped the assigned model's canonical settings or request options.");
            Require(client.CompletedMessages.Single().Any(message => message.Role == "system"
                    && message.Content.Contains("State the evidence clearly.", StringComparison.Ordinal)),
                "Collaborate did not apply the assigned model's response tone.");
            Require(!JsonSerializer.Serialize(view).Contains("role-token-fixture", StringComparison.Ordinal),
                "New role request credentials leaked into serialized presentation data.");
            Require(core.Configs["narrator"].MaxOutputTokens == 555,
                "Preparing a Collaborate request mutated the persisted output allowance.");
        });
    }

    static void CollaborateFallbackKeepsDistinctServersForSameModel()
    {
        RunStaTest(() =>
        {
            var view = WorkspaceRoutingView(WorkspaceRoutingSnapshot());
            var client = new SequentialAgentModelClient("", "Fallback answer.");
            var coordinator = CreateCollaborateCoordinatorForTest(client,
                new TextBox { Text = "Explain the tradeoffs." }, new TextBlock(),
                () => view, _ => { }, new RecordingCollaborateHistoryStore());
            coordinator.Initialize();
            coordinator.SendAsync().GetAwaiter().GetResult();

            Require(client.CompletedConfigs.Count == 2,
                "Identical model names on distinct servers incorrectly suppressed the shared fallback.");
            var primary = client.CompletedConfigs[0];
            var fallback = client.CompletedConfigs[1];
            Require(primary.BaseUrl == "http://127.0.0.1:11434/v1"
                    && fallback.BaseUrl == "http://127.0.0.1:1234/v1"
                    && fallback.ApiMode == "lmstudio_native" && fallback.ApiToken == "shared-token-fixture"
                    && fallback.ConfiguredContextWindow == 8192 && fallback.Reasoning == "off",
                "Collaborate fallback reused the primary server or its model settings.");
            Require(!client.CompletedMessages[1].Any(message => message.Content.Contains("State the evidence clearly.", StringComparison.Ordinal)),
                "Collaborate fallback inherited the failed model's custom tone.");
        });
    }

    static void CollaborateLegacyRoutesRespectDefaultOff()
    {
        RunStaTest(() =>
        {
            var core = WorkspaceRoutingSnapshot(explicitRole: false);
            core.Engine.DefaultForUnassignedAgentsEnabled = false;
            var view = WorkspaceRoutingView(core);
            var client = new SequentialAgentModelClient("", "Must not fallback.");
            var coordinator = CreateCollaborateCoordinatorForTest(client,
                new TextBox { Text = "Explain the tradeoffs." }, new TextBlock(),
                () => view, _ => { }, new RecordingCollaborateHistoryStore());
            coordinator.Initialize();
            coordinator.SendAsync().GetAwaiter().GetResult();

            Require(view.NarratorModel == "same-model" && client.CompletedConfigs.Count == 1
                    && client.CompletedConfigs[0].BaseUrl == "http://127.0.0.1:11434/v1",
                "A legacy same-name assignment on a distinct server was treated as inherited or used a disabled fallback.");

            core.Configs.Remove("narrator");
            var unassigned = WorkspaceRoutingView(core);
            Require(CollaborateCoordinator.MissingConfiguredModelRoles(unassigned, "fast").SequenceEqual(["Narrator"]),
                "Default-off readiness allowed an unassigned Narrator to use the shared connection.");
        });
    }

    static void AgentWorkspacePreservesSharedRequestOptions()
    {
        var core = WorkspaceRoutingSnapshot();
        var view = WorkspaceRoutingView(core);
        var config = AgentWorkspaceCoordinator.Config(view, "same-model", 2048);
        Require(config.BaseUrl == "http://127.0.0.1:1234/v1" && config.ApiMode == "lmstudio_native"
                && config.ApiToken == "shared-token-fixture" && config.ContextLength == 8192
                && config.ConfiguredContextWindow == 8192 && config.Temperature == 0
                && config.Extra?.ContainsKey("shared_fixture") == true && config.MaxOutputTokens == 2048,
            "Agent Workspace dropped the saved shared request options or canonical context.");
        var withoutReasoning = AgentWorkspaceCoordinator.WithReasoningDisabled(config);
        Require(withoutReasoning.Extra?.ContainsKey("shared_fixture") == true && withoutReasoning.Reasoning == "off",
            "Agent reasoning recovery dropped the shared provider options.");
        config.Extra!["caller_only"] = JsonSerializer.SerializeToElement(true);
        Require(!core.Configs["shared"].Extra!.ContainsKey("caller_only"),
            "Request-local extension options mutated the source snapshot.");

        RunStaTest(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-routing-agent-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                core.Engine.DefaultForUnassignedAgentsEnabled = false;
                var current = WorkspaceRoutingView(core);
                var client = new SequentialAgentModelClient("A useful explanation.");
                var settings = new WpfSettings { AgentWorkspacePath = root, AgentBuilderOnlyDefault = true };
                using var coordinator = CreateWorkspaceProfileTestCoordinator(settings,
                    new WpfSettingsStore(Path.Combine(root, "settings.json")),
                    (_, _) => Task.FromResult("A small isolated test workspace."),
                    modelClient: client, snapshot: () => current,
                    promptText: new TextBox { Text = "Explain these tradeoffs." });
                coordinator.Initialize();
                coordinator.DebugSendAsync().GetAwaiter().GetResult();
                Require(client.CompletedConfigs.Count == 1
                        && client.CompletedConfigs[0].BaseUrl == "http://127.0.0.1:1234/v1"
                        && client.CompletedConfigs[0].Extra?.ContainsKey("shared_fixture") == true,
                    "The actual Agent run lost its shared route/options when Arena Default was disabled.");
            }
            finally
            {
                Require(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                    "Temporary routing test path escaped its expected parent.");
                Directory.Delete(root, recursive: true);
            }
        });

        core.ModelSettings[ModelRuntimeSettingsRegistry.Identity(core.Configs["shared"])].ConfiguredContextWindow = 0;
        var providerDefault = AgentWorkspaceCoordinator.Config(WorkspaceRoutingView(core), "same-model", 1024);
        Require(providerDefault.ConfiguredContextWindow == 0 && providerDefault.ContextLength == 0,
            "Shared workspace requests revived a legacy context override after selecting Provider default.");
    }

    private static ArenaSnapshot WorkspaceRoutingSnapshot(bool explicitRole = true)
    {
        var core = new ArenaSnapshot { ModelSettingsVersion = ModelRuntimeSettingsRegistry.CurrentSchemaVersion };
        core.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1", ApiMode = "lmstudio_native", Model = "same-model",
            ApiToken = "shared-token-fixture", Temperature = 0, Reasoning = "off",
            ContextLength = 16384, Extra = new() { ["shared_fixture"] = JsonSerializer.SerializeToElement(true) }
        };
        core.Configs["narrator"] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:11434/v1", ApiMode = "ollama_native", Model = "same-model",
            ExplicitModelAssignment = explicitRole, ApiToken = "role-token-fixture", Temperature = 0,
            Timeout = 900, Reasoning = "low", MaxOutputTokens = 555,
            NativeStatefulChat = false, NativeIdleTtlSeconds = 42,
            Extra = new() { ["routing_fixture"] = JsonSerializer.SerializeToElement(true) }
        };
        foreach (var (key, context) in new[] { ("shared", 8192), ("narrator", 4096) })
        {
            var identity = ModelRuntimeSettingsRegistry.Identity(core.Configs[key]);
            core.ModelSettings[identity] = new ModelRuntimeSettings
            {
                ModelIdentity = identity, ConfiguredContextWindow = context, HistoryPolicy = "rolling_80",
                ResponseTone = key == "narrator" ? "custom" : "default",
                CustomTone = key == "narrator" ? "State the evidence clearly." : ""
            };
        }
        return core;
    }

    private static AIArena.Wpf.Models.ArenaViewSnapshot WorkspaceRoutingView(ArenaSnapshot core) =>
        SnapshotViewMapper.FromCore(new SessionSummary("routing-test", "", true, 0, 0, 0, DateTimeOffset.Now), core);
}
