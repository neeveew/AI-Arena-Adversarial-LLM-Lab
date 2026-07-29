using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;


internal static partial class Program
{
static void MainWindowShutdownRecloseIsDeferred()
{
    RunStaTest(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var closeCount = 0;

        MainWindow.ScheduleCloseAfterCleanup(dispatcher, () => closeCount++);
        Require(closeCount == 0, "Shutdown cleanup must not re-enter Window.Close from the active Closing event.");

        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = dispatcher.BeginInvoke(
            new Action(() => frame.Continue = false),
            System.Windows.Threading.DispatcherPriority.Background);
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        Require(closeCount == 1, "Shutdown cleanup should schedule exactly one deferred close.");
    });

    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var closingStart = source.IndexOf("private async void MainWindow_Closing", StringComparison.Ordinal);
    var closingEnd = source.IndexOf("internal static void ScheduleCloseAfterCleanup", closingStart, StringComparison.Ordinal);
    Require(closingStart >= 0 && closingEnd > closingStart, "the async window shutdown handler should remain discoverable");
    var closing = source[closingStart..closingEnd];
    var requestShutdown = closing.IndexOf("_arenaOperationCoordinator?.RequestShutdown()", StringComparison.Ordinal);
    var stopProviderTimer = closing.IndexOf("_providerHealthTimer.Stop()", StringComparison.Ordinal);
    var stopAutoChat = closing.IndexOf("StopAutoChatAsync", StringComparison.Ordinal);
    var drainOperations = closing.IndexOf("_arenaOperationCoordinator.DrainAsync()", StringComparison.Ordinal);
    var deferredClose = closing.IndexOf("ScheduleCloseAfterCleanup", StringComparison.Ordinal);
    Require(requestShutdown >= 0, "window shutdown should immediately reject and cancel general arena work");
    Require(stopProviderTimer >= 0 && stopProviderTimer < requestShutdown, "provider timers must stop before the tracked-operation shutdown barrier begins");
    Require(stopAutoChat > requestShutdown, "general work cancellation should begin before auto-chat is drained");
    Require(drainOperations > stopAutoChat, "general arena work should drain after auto-chat releases the shared operation lock");
    Require(deferredClose > drainOperations, "services and the window must remain alive until general arena work is drained");

    Exception? reportedFailure = null;
    MainWindow.RunUiCommitSafelyAsync(
        () => Task.FromException(new IOException("simulated provider save failure")),
        exception => reportedFailure = exception).GetAwaiter().GetResult();
    Require(reportedFailure is IOException, "provider commit failures should be reported without escaping an async UI handler");

    MainWindow.RunUiCommitSafelyAsync(
        () => Task.FromException(new IOException("simulated provider save failure")),
        _ => throw new InvalidOperationException("simulated reporting failure")).GetAwaiter().GetResult();

    var providerHandlersStart = source.IndexOf("private async void ProviderBaseUrlText_Commit", StringComparison.Ordinal);
    var providerHandlersEnd = source.IndexOf("private void SetAppSettingsVisible", providerHandlersStart, StringComparison.Ordinal);
    Require(providerHandlersStart >= 0 && providerHandlersEnd > providerHandlersStart, "provider commit handlers should remain discoverable");
    var providerHandlers = source[providerHandlersStart..providerHandlersEnd];
    Require(
        providerHandlers.Contains("RunProviderCommitSafelyAsync", StringComparison.Ordinal),
        "provider focus and selection commits should route persistence failures through the non-throwing UI guard");
    Require(
        providerHandlers.Contains("operationCoordinator.TrackAsync", StringComparison.Ordinal),
        "provider commits should register their actual task with the window shutdown drain");
    Require(
        providerHandlers.Contains("commit(coordinator, cancellationToken)", StringComparison.Ordinal),
        "provider shutdown cancellation should reach the actual coordinator operation");
    Require(
        source.Contains("SaveAndTestProviderQuickSetupAsync", StringComparison.Ordinal)
        && source.Contains("(baseUrl, model, statusText) => RunProviderCommitSafelyAsync", StringComparison.Ordinal)
        && source.Contains("RunProviderControlOperationAsync", StringComparison.Ordinal)
        && source.Contains("operationCancellationToken => _providerControlHandler.ExecuteAsync", StringComparison.Ordinal),
        "quick setup and control-plane provider mutations should use tracked provider boundaries");
    Require(
        source.Contains("RunTrackedBackgroundOperationSafelyAsync", StringComparison.Ordinal)
        && source.Contains("ProviderReachability.TestProviderAsync(cancellationToken)", StringComparison.Ordinal)
        && source.Contains("ProviderReachability.RefreshModelsAsync(cancellationToken)", StringComparison.Ordinal),
        "provider popup, timer, and startup work should use the tracked shutdown boundary");
    Require(
        source.Contains("cancellationToken => RefreshAdvertisedModelsAsync(force, cancellationToken)", StringComparison.Ordinal),
        "opening provider settings should route its immediate model refresh through the tracked background boundary");
    Require(
        source.Contains("callerCancellationToken", StringComparison.Ordinal)
        && source.Contains("CancellationTokenSource.CreateLinkedTokenSource", StringComparison.Ordinal),
        "control-plane provider mutations should link request cancellation with application shutdown");
    Require(
        source.Contains("PersistInternetSettingForActiveSessionAsync", StringComparison.Ordinal),
        "the direct Internet toggle should persist through a dedicated active-session path");

    var providerSettingsSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs"));
    Require(
        providerSettingsSource.Contains("Func<AIArena.Core.Models.ArenaSnapshot, string, CancellationToken, Task>", StringComparison.Ordinal)
        && providerSettingsSource.Contains("Func<string, CancellationToken, Task> refreshActiveSessionAsync", StringComparison.Ordinal)
        && providerSettingsSource.Contains("Func<bool, CancellationToken, Task> refreshProviderReachabilityAsync", StringComparison.Ordinal)
        && providerSettingsSource.Contains("saveSnapshotWithFeedbackAsync(snapshot, session.Id, cancellationToken)", StringComparison.Ordinal)
        && providerSettingsSource.Contains("refreshProviderReachabilityAsync(true, cancellationToken)", StringComparison.Ordinal)
        && providerSettingsSource.Contains("providerRuntime.TestAsync(session.Id, allRoles: false, cancellationToken)", StringComparison.Ordinal),
        "provider persistence and reachability delegates should preserve tracked cancellation through their final stages");

    var sessionMutationSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ArenaSessionMutationCoordinator.cs"));
    Require(
        sessionMutationSource.Contains("await refreshActiveSessionAsync(\"Session settings applied.\")", StringComparison.Ordinal)
        && !sessionMutationSource.Contains("refreshProviderReachabilityAsync", StringComparison.Ordinal),
        "advanced/context Apply should remain tracked through its session refresh without re-saving or probing provider identity fields");
}

static void SnapshotViewMapperPreservesProviderTelemetry()
{
    var session = new SessionSummary("session", "snapshot.json", true, 1, 0, 0, DateTimeOffset.UtcNow);
    var snapshot = new ArenaSnapshot();
    snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model", ApiToken = "secret-token", NativeStatefulChat = false, NativeIdleTtlSeconds = 1200 };
    snapshot.Engine.Internet.UseInternet = true;
    snapshot.Configs["alpha"] = new ModelProviderConfig { Model = "   " };
    snapshot.Engine.Agents.Add(new DialogueAgent
    {
        Id = "alpha",
        Name = "Alpha",
        Persona = "Maps evidence.",
        Active = true
    });
    snapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 1,
        Speaker = "Alpha",
        SpeakerId = "alpha",
        Kind = "message",
        Status = "ok",
        Text = "Telemetry turn.",
        Model = new ModelMetadata
        {
            Model = "local-model",
            LatencyMs = 321,
            PromptTokens = 100,
            CompletionTokens = 25,
            TotalTokens = 125,
            TokensPerSecond = 31.25,
            TimeToFirstTokenMs = 246,
            ModelLoadTimeMs = 1750
        },
        Metadata = new Dictionary<string, JsonElement>
        {
            ["provider_response_id"] = JsonSerializer.SerializeToElement("resp_native")
        }
    });
    snapshot.GenerationHistory.Add(new GenerationHistoryEntry
    {
        Id = "history-older",
        Kind = "random",
        Label = "Older history match",
        Style = "technical",
        Intensity = "sharp",
        RolePack = "benchmark_duel",
        Absurdity = "grounded",
        ScenarioSeed = "seed-1",
        PersonaSeed = "seed-1",
        CreatedAt = 100,
        Match = new GeneratedMatchSnapshot
        {
            Topic = "History topic",
            Global = "History global rule",
            NarratorBrief = "History narrator brief",
            Personas =
            [
                new GeneratedPersonaSnapshot { AgentId = "alpha", Role = "Planner", Persona = "plans" },
                new GeneratedPersonaSnapshot { AgentId = "beta", Role = "Skeptic", Persona = "tests" },
                new GeneratedPersonaSnapshot { AgentId = "narrator", Role = "Narrator", Persona = "observes" }
            ]
        }
    });
    snapshot.GenerationHistory.Add(new GenerationHistoryEntry
    {
        Id = "",
        Kind = "random",
        Label = "Invalid history match",
        CreatedAt = 300,
        Match = new GeneratedMatchSnapshot
        {
            Topic = "Invalid topic"
        }
    });
    snapshot.GenerationHistory.Add(new GenerationHistoryEntry
    {
        Id = "history-newer",
        Kind = "ai_choice",
        Label = "Newer history match",
        Style = "creative",
        Intensity = "spicy",
        RolePack = "governance_board",
        Absurdity = "odd",
        ScenarioSeed = "ai-choice",
        PersonaSeed = "ai-choice",
        CreatedAt = 200,
        Match = new GeneratedMatchSnapshot
        {
            Topic = "Newer topic",
            Global = "Newer global rule",
            NarratorBrief = "Newer narrator brief",
            Personas =
            [
                new GeneratedPersonaSnapshot { AgentId = "alpha", Role = "Chair", Persona = "chairs" },
                new GeneratedPersonaSnapshot { AgentId = "narrator", Role = "Narrator", Persona = "observes" }
            ]
        }
    });

    var rendered = SnapshotViewMapper.FromCore(session, snapshot);
    var message = rendered.Messages.Single();
    var alpha = rendered.Agents.Single(agent => agent.Id == "alpha");
    var worldAlpha = AgentWorldLayout.Build(rendered).Avatars.Single(avatar => avatar.Id == "alpha");
    var history = rendered.GenerationHistory.First();

    Require(Math.Abs(message.TokensPerSecond - 31.25) < 0.001, "rendered transcript should preserve tokens/sec");
    Require(message.TimeToFirstTokenMs == 246, "rendered transcript should preserve TTFT");
    Require(message.ProviderResponseId == "resp_native", "rendered transcript should preserve provider response id");
    Require(message.ModelLoadTimeMs == 1750, "rendered transcript should preserve model load time");
    Require(alpha.Model == "shared-model", "whitespace agent model override should fall back to shared model");
    Require(worldAlpha.Model == "shared-model", "world avatars should inherit the shared model when an agent override is blank");
    Require(rendered.ProviderApiToken == "secret-token", "rendered snapshot should preserve provider API token for settings UI");
    Require(!rendered.ProviderNativeStatefulChat, "rendered snapshot should preserve native stateful chat setting");
    Require(rendered.ProviderNativeIdleTtlSeconds == 1200, "rendered snapshot should preserve native idle TTL setting");
    Require(rendered.InternetEnabled, "rendered snapshot should preserve the direct internet setting");
    Require(rendered.GenerationHistory.Count == 2, "rendered history should skip invalid blank-id entries");
    Require(history.Id == "history-newer", "rendered history should show newest entries first");
    Require(history.Global == "Newer global rule", "rendered history should preserve global instruction");
    Require(history.NarratorBrief == "Newer narrator brief", "rendered history should preserve narrator brief");
    Require(history.PersonaCount == 1, "rendered history should count participant personas only");
    Require(history.PersonaPreview.Contains("alpha: Chair", StringComparison.OrdinalIgnoreCase), "rendered history should include persona preview");
}
static void SnapshotViewMapperAttachesLatestInternetSourcesToAgents()
{
    var session = new SessionSummary("session", "snapshot.json", true, 1, 0, 0, DateTimeOffset.UtcNow);
    var snapshot = new ArenaSnapshot();
    snapshot.Configs["shared"] = new ModelProviderConfig { Model = "shared-model" };
    snapshot.Engine.Agents.Add(new DialogueAgent
    {
        Id = "alpha",
        Name = "Alpha",
        Persona = "Checks live claims.",
        Active = true
    });
    snapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 1,
        Speaker = "Alpha",
        SpeakerId = "alpha",
        Kind = "message",
        Status = "ok",
        Text = "Earlier sourced reply.",
        Metadata = new Dictionary<string, JsonElement>
        {
            ["tool_request"] = JsonSerializer.SerializeToElement(new
            {
                requester_id = "alpha",
                tool = "web_search",
                query = "earlier query"
            }),
            ["tool_result"] = JsonSerializer.SerializeToElement(new
            {
                query = "earlier query",
                checked_at = "2026-06-17T10:00:00Z",
                sources = new[]
                {
                    new { source = "Old", title = "Old result", url = "https://example.test/old", snippet = "stale" }
                }
            })
        }
    });
    snapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 2,
        Speaker = "Tool",
        SpeakerId = "system",
        Kind = "internet",
        Status = "ok",
        Text = "Latest sourced reply.",
        Metadata = new Dictionary<string, JsonElement>
        {
            ["tool_request"] = JsonSerializer.SerializeToElement(new
            {
                requester_id = "alpha",
                tool = "web_search",
                query = "latest current affairs"
            }),
            ["tool_result"] = JsonSerializer.SerializeToElement(new
            {
                query = "latest current affairs",
                checked_at = "2026-06-17T11:00:00Z",
                sources = new[]
                {
                    new { source = "BBC", title = "Latest politics", url = "https://www.bbc.com/news/politics", snippet = "current update" }
                }
            })
        }
    });

    var rendered = SnapshotViewMapper.FromCore(session, snapshot);
    var alpha = rendered.Agents.Single(agent => agent.Id == "alpha");
    var sources = alpha.InternetSources;

    Require(alpha.HasInternetSources, "agents with source-backed turns should expose an internet source cue");
    Require(sources is not null, "agents with source-backed turns should include source metadata");
    Require(sources!.Query == "latest current affairs", "agent source cue should keep the latest search query");
    Require(sources.Sources.Count == 1, "agent source cue should preserve source count");
    Require(sources.Sources.Single().Contains("https://www.bbc.com/news/politics", StringComparison.Ordinal), "agent source cue should include the source URL");
}

static void AgentInternetSourcesPresenterFormatsCopyText()
{
    var summary = new AgentInternetSourceSummary(
        "latest AI regulation news",
        "2026-06-17 12:00:00 +01:00",
        ["BBC - AI regulation story - https://www.bbc.com/news/technology - Short source note"],
        [
            new AgentInternetSourceItem(
                "AI regulation story",
                "bbc.com",
                "https://www.bbc.com/news/technology",
                "Short source note",
                "2026-06-17",
                "BBC - AI regulation story - https://www.bbc.com/news/technology - Short source note")
        ]);

    var copied = AgentInternetSourcesPresenter.FormatSourcesForCopy(summary);

    Require(copied.Contains("Query: latest AI regulation news", StringComparison.Ordinal), "source copy text should include query");
    Require(copied.Contains("Checked: 2026-06-17", StringComparison.Ordinal), "source copy text should include checked time");
    Require(copied.Contains("https://www.bbc.com/news/technology", StringComparison.Ordinal), "source copy text should include URL");
    Require(copied.Contains("Short source note", StringComparison.Ordinal), "source copy text should include snippet");
    Require(
        AgentInternetSourcesPresenter.TryNormalizeWebSourceUrl("https://example.com/story", out var normalized)
        && normalized == "https://example.com/story",
        "source opening should accept normal HTTPS URLs");
    Require(!AgentInternetSourcesPresenter.TryNormalizeWebSourceUrl("file:///C:/Windows/win.ini", out _), "source opening should reject file URLs");
    Require(!AgentInternetSourcesPresenter.TryNormalizeWebSourceUrl("ms-settings:privacy", out _), "source opening should reject shell protocols");
    Require(!AgentInternetSourcesPresenter.TryNormalizeWebSourceUrl("https://user:secret@example.com/", out _), "source opening should reject credential-bearing URLs");
}

static void CustomMatchSummaryCoordinatorNormalizesCardText()
{
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText("") == "No topic is set for this match yet.", "blank topic should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.ScenarioTopicText(" Debate topic ") == " Debate topic ", "topic text should be preserved");
    Require(CustomMatchSummaryCoordinator.ScenarioGlobalText(" ") == "No global instruction is set for this match yet.", "blank global instruction should use empty-state copy");
    Require(CustomMatchSummaryCoordinator.AgentPersonaText("") == "(no persona)", "blank agent persona should use placeholder");
    Require(CustomMatchSummaryCoordinator.AgentPersonaText("skeptical analyst") == "skeptical analyst", "agent persona should be preserved");
    Require(CustomMatchSummaryCoordinator.NarratorPersonaText("") == "(no narrator persona)", "blank narrator persona should use placeholder");
    Require(CustomMatchSummaryCoordinator.NarratorPersonaText("referee") == "referee", "narrator persona should be preserved");

    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "local-model",
        providerLastError: "",
        turnIndex: 0,
        messages: [],
        agents:
        [
            new AgentState("alpha", "Alpha", "waiting", "persona", "", "", "", "local-model", true, true, []),
            new AgentState("beta", "Beta", "waiting", "persona", "", "", "", "local-model", true, false, []),
            new AgentState("gamma", "Gamma", "waiting", "persona", "", "", "", "local-model", false, true, [])
        ]) with
    {
        ScenarioGeneratorRolePack = "benchmark_duel",
        ScenarioGeneratorStyle = "technical",
        ScenarioGeneratorIntensity = "sharp",
        ScenarioGeneratorAbsurdity = "grounded",
        ScenarioGeneratorSeed = "seed-123",
        TopicLocked = true,
        RivalryMatrixEnabled = true,
        RivalryMatrix = [new RivalryMatrixItem("alpha", "beta", "fact_check")]
    };
    Require(CustomMatchSummaryCoordinator.SetupProfileText(snapshot).Contains("benchmark duel", StringComparison.OrdinalIgnoreCase), "setup profile should summarize role pack");
    Require(!CustomMatchSummaryCoordinator.SetupProfileText(snapshot).Contains("seed 123", StringComparison.OrdinalIgnoreCase), "setup profile should describe the run shape, not echo the raw seed");
    Require(CustomMatchSummaryCoordinator.RunShapeText(snapshot).Contains("2 active: Alpha -> Beta -> Narrator", StringComparison.Ordinal), "run shape should show active cast order and narrator handoff");
    Require(CustomMatchSummaryCoordinator.RunShapeText(snapshot).Contains("turn budget", StringComparison.OrdinalIgnoreCase), "run shape should include turn budget context");
    Require(CustomMatchSummaryCoordinator.RelationshipMapText(snapshot).Contains("alpha -> beta", StringComparison.OrdinalIgnoreCase), "relationship map should show active relationship links");
    Require(CustomMatchSummaryCoordinator.RelationshipMapText(snapshot).Contains("fact-check", StringComparison.OrdinalIgnoreCase), "relationship map should format stance labels");
    Require(CustomMatchSummaryCoordinator.RelationshipMapText(snapshot).Contains("covers 1/2", StringComparison.OrdinalIgnoreCase), "relationship map should include graph coverage insight");
    Require(CustomMatchSummaryCoordinator.LockPlanText(snapshot).Contains("topic", StringComparison.OrdinalIgnoreCase), "lock plan should include topic locks");
    Require(CustomMatchSummaryCoordinator.LockPlanText(snapshot).Contains("Alpha", StringComparison.Ordinal), "lock plan should include active locked agent names");
    Require(!CustomMatchSummaryCoordinator.LockPlanText(snapshot).Contains("Gamma", StringComparison.Ordinal), "lock plan should ignore inactive locked agents");
    var setupSource = CustomMatchSummaryCoordinator.SetupSourceText(snapshot);
    Require(setupSource.Contains("Random", StringComparison.OrdinalIgnoreCase), "setup source should classify how the setup was produced");
    Require(!setupSource.Contains("seed 123", StringComparison.OrdinalIgnoreCase), "setup source should not echo the raw seed");
    var constraints = CustomMatchSummaryCoordinator.RunConstraintText(snapshot);
    Require(constraints.Contains("2 active agent", StringComparison.OrdinalIgnoreCase), "run constraints should count only active agents");
    Require(constraints.Contains("2 lock", StringComparison.OrdinalIgnoreCase), "run constraints should include topic and active-agent locks but ignore inactive lock noise");
    Require(constraints.Contains("1 relationship rule", StringComparison.OrdinalIgnoreCase), "run constraints should include relationship rules");
    Require(constraints.Contains("coverage 1/2", StringComparison.OrdinalIgnoreCase), "run constraints should include relationship graph coverage");
    var setupBrief = CustomMatchSummaryCoordinator.CurrentSetupBrief(snapshot);
    Require(setupBrief.Contains("AI Arena current setup", StringComparison.Ordinal), "current setup brief should have a stable title");
    Require(setupBrief.Contains("Relationship map:", StringComparison.Ordinal), "current setup brief should include relationship map");
    Require(setupBrief.Contains("Preset match: Model Duel", StringComparison.Ordinal), "current setup brief should include preset match metadata");
    Require(setupBrief.Contains("Provider:", StringComparison.Ordinal), "current setup brief should include provider context");
    using var setupSpec = JsonDocument.Parse(CustomMatchSummaryCoordinator.CurrentSetupSpec(snapshot));
    Require(setupSpec.RootElement.GetProperty("schema").GetString() == "ai_arena.current_setup.v1", "current setup spec should expose schema");
    Require(setupSpec.RootElement.GetProperty("tuning").GetProperty("presetMatches").EnumerateArray().Any(item => item.GetString() == "Model Duel"), "current setup spec should include preset match metadata");
    Require(setupSpec.RootElement.GetProperty("relationship").GetProperty("links").GetArrayLength() == 1, "current setup spec should include valid relationship links");
    Require(setupSpec.RootElement.GetProperty("cast").GetArrayLength() == 2, "current setup spec should include active cast only");

    var invalidRelationshipSnapshot = snapshot with
    {
        RivalryMatrix =
        [
            new RivalryMatrixItem("alpha", "alpha", "challenge"),
            new RivalryMatrixItem("gamma", "alpha", "support"),
            new RivalryMatrixItem("alpha", "beta", "neutral")
        ]
    };
    Require(CustomMatchSummaryCoordinator.RelationshipMapText(invalidRelationshipSnapshot).Contains("no active participant rules", StringComparison.OrdinalIgnoreCase), "relationship map should ignore invalid, inactive, self, and neutral rules");
    Require(CustomMatchSummaryCoordinator.RunConstraintText(invalidRelationshipSnapshot).Contains("neutral relationships", StringComparison.OrdinalIgnoreCase), "run constraints should ignore invalid relationship noise");
}

static void ScenarioSeedInspectorCoordinatorFormatsMetadata()
{
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("", "") == "Manual", "blank seed should be manual");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("manual-seed", "") == "Random", "nonblank seed should be random");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("ai-choice", "") == "AI Choice", "AI choice seed should be detected");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("YOLO-123", "") == "Wild Seed", "Wild Seed source should be detected");
    Require(ScenarioSeedInspectorCoordinator.ScenarioSeedSource("manual", "yolo") == "Wild Seed", "Wild Seed persona style should win");
    var seedTip = ScenarioSeedInspectorCoordinator.SeedToolTip("scenario-abc", "persona-xyz");
    Require(seedTip.Contains("scenario-abc", StringComparison.Ordinal), "seed tooltip should carry the full scenario seed");
    Require(seedTip.Contains("persona-xyz", StringComparison.Ordinal), "seed tooltip should carry the full persona seed");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("auto"), "auto role pack should be hidden");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("AUTO"), "auto role pack should hide case-insensitively");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowRolePack("-"), "placeholder role pack should be hidden");
    Require(ScenarioSeedInspectorCoordinator.ShouldShowRolePack("absurd_lab"), "custom role pack should be visible");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("grounded"), "grounded absurdity should be hidden");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("GROUNDED"), "grounded absurdity should hide case-insensitively");
    Require(!ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("-"), "placeholder absurdity should be hidden");
    Require(ScenarioSeedInspectorCoordinator.ShouldShowAbsurdity("maximum"), "non-grounded absurdity should be visible");
}

static void ProviderQuickSetupCoordinatorFormatsDefaults()
{
    var agent = new AgentState("alpha", "Alpha", "waiting", "", "default", "default", "", "agent-model", true, false, []);
    var snapshot = SnapshotForOverviewTest(true, "shared-model", "", 0, [], [agent]);

    Require(!ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, agent), "online provider with usable model should hide quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderOnline = false }, agent), "offline provider should show quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderModel = "" }, agent with { Model = "" }), "missing shared and current model should show quick setup");
    Require(ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot with { ProviderModel = "" }, agent), "missing shared provider model should show quick setup even when current agent has a model");
    Require(!ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, agent with { Model = "" }), "shared model should satisfy missing current agent model");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot with { ProviderBaseUrl = "-" }) == "http://127.0.0.1:1234/v1", "blank quick setup base URL should use LM Studio default");
    Require(ProviderQuickSetupCoordinator.QuickBaseUrl(snapshot with { ProviderBaseUrl = "http://host/v1" }) == "http://host/v1", "custom base URL should be preserved");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot, agent) == "agent-model", "agent model should populate quick setup model first");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot, agent with { Model = "" }) == "shared-model", "shared provider model should backfill quick setup model");
    Require(ProviderQuickSetupCoordinator.QuickModelText(snapshot with { ProviderModel = "" }, agent with { Model = "" }) == "", "missing model should leave quick setup model blank");
}

static void MainWindowComboBoxTemplateUsesThemeResources()
{
    // The shell adopts the Arena control system, so the combo box template lives in
    // the shared dictionary while the window keeps only intent-level styles.
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var controlStyles = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Theming/ControlStyles.xaml"));
    foreach (var markup in new[] { xaml, controlStyles })
    {
        Require(!markup.Contains("SystemColors.WindowBrushKey", StringComparison.Ordinal), "combo box template should not pin popup window color to one theme");
        Require(!markup.Contains("SystemColors.ControlBrushKey", StringComparison.Ordinal), "combo box template should not pin control color to one theme");
        Require(!markup.Contains("SystemColors.HighlightBrushKey", StringComparison.Ordinal), "combo box template should not pin selection highlight to one theme");
    }

    Require(controlStyles.Contains("PART_EditableTextBox", StringComparison.Ordinal), "combo box template should keep editable model picker support");
    Require(controlStyles.Contains("DisabledTextBrush", StringComparison.Ordinal), "combo box template should dim disabled editable controls");
    Require(xaml.Contains("TargetType=\"ComboBox\" BasedOn=\"{StaticResource Arena.ComboBox}\"", StringComparison.Ordinal), "the shell combo box should adopt the Arena control system");
}

static void ThemeBrushDefaultsMirrorTheDefaultPalette()
{
    // These brushes are startup and design-time defaults; ApplyTheme overwrites
    // every one of them at runtime. That is exactly why they drifted unnoticed
    // while they lived in MainWindow.xaml - three Nav brushes held literals for
    // values ThemePalette computes, and nothing compared the two. This pins the
    // mirror so the designer cannot start lying about the shipped theme again.
    var markup = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Theming/ThemeBrushes.xaml"));
    var theme = ThemePalette.Resolve("dark-arena");

    var expected = new (string Key, Color Color)[]
    {
        ("AppBackgroundBrush", theme.AppBackground),
        ("TopBarBrush", theme.TopBar),
        ("PanelBrush", theme.Panel),
        ("CardBrush", theme.Card),
        ("InputBrush", theme.Input),
        ("TranscriptHeaderBrush", theme.Panel),
        ("TranscriptBodyBrush", theme.Card),
        ("ControlBorderBrush", theme.Border),
        ("TextBrush", theme.Text),
        ("MutedTextBrush", theme.MutedText),
        ("PrimaryBrush", theme.Primary),
        ("PrimaryBorderBrush", theme.PrimaryBorder),
        ("AssistBrush", theme.Assist),
        ("AssistBorderBrush", theme.AssistBorder),
        ("DangerBrush", theme.Danger),
        ("DangerBorderBrush", theme.DangerBorder),
        ("DangerTextBrush", theme.DangerText),
        ("DisabledBrush", theme.Disabled),
        ("DisabledBorderBrush", theme.DisabledBorder),
        ("DisabledTextBrush", theme.DisabledText),
        ("HoverBorderBrush", theme.HoverBorder),
        ("NavHoverBrush", theme.NavHover),
        ("NavActiveBrush", theme.NavActive),
        ("NavPressedBrush", theme.NavPressed),
        ("PressedPrimaryBrush", theme.PressedPrimary),
        ("OverlayBrush", theme.Overlay),
        ("AlphaAccentBrush", theme.AlphaAccent),
        ("BetaAccentBrush", theme.BetaAccent),
        ("GammaAccentBrush", theme.GammaAccent),
        ("DeltaAccentBrush", theme.DeltaAccent),
        ("NarratorAccentBrush", theme.NarratorAccent),
        ("OperatorAccentBrush", theme.OperatorAccent),
    };

    foreach (var (key, color) in expected)
    {
        var match = Regex.Match(markup, "<SolidColorBrush x:Key=\"" + Regex.Escape(key) + "\" Color=\"(?<value>#[0-9A-Fa-f]+)\"");
        Require(match.Success, $"ThemeBrushes.xaml should define {key}");

        var declared = match.Groups["value"].Value;
        var expectedHex = color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        Require(
            string.Equals(declared, expectedHex, StringComparison.OrdinalIgnoreCase),
            $"{key} default {declared} should match the dark-arena palette value {expectedHex}");
    }

    // The window must not reintroduce its own copies; the application scope owns them.
    var window = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    Require(
        !window.Contains("<SolidColorBrush x:Key=\"AppBackgroundBrush\"", StringComparison.Ordinal),
        "MainWindow.xaml should not redeclare themed brushes that ThemeBrushes.xaml owns");
}

static ResourceDictionary LoadDesignTokenDictionary()
{
    var markup = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Theming/DesignTokens.xaml"));
    return (ResourceDictionary)XamlReader.Parse(markup);
}

static void DesignTokenResourcesMatchTheirWpfContract()
{
    RunStaTest(() =>
    {
        var dictionary = LoadDesignTokenDictionary();
        var expectedThickness = new (string Key, Thickness Value)[]
        {
            ("Arena.Inset.Control", new Thickness(8, 4, 8, 4)),
            ("Arena.Inset.Card", new Thickness(10)),
            ("Arena.Inset.Panel", new Thickness(12)),
            ("Arena.Inset.RailAction", new Thickness(7, 4, 7, 4)),
            ("Arena.Inset.QuickAction", new Thickness(10, 4, 10, 4)),
            ("Arena.Inset.MatchSetupAction", new Thickness(11, 6, 11, 6)),
            ("Arena.Gap.Inline.Before.Compact", new Thickness(6, 0, 0, 0)),
            ("Arena.Gap.Inline.Before.Default", new Thickness(8, 0, 0, 0)),
            ("Arena.Gap.Inline.Before.Spacious", new Thickness(12, 0, 0, 0)),
            ("Arena.Gap.Inline.After.Tight", new Thickness(0, 0, 5, 0)),
            ("Arena.Gap.Inline.After.Compact", new Thickness(0, 0, 6, 0)),
            ("Arena.Gap.Inline.After.Default", new Thickness(0, 0, 8, 0)),
            ("Arena.Gap.Stack.Before.Micro", new Thickness(0, 3, 0, 0)),
            ("Arena.Gap.Stack.Before.Tight", new Thickness(0, 4, 0, 0)),
            ("Arena.Gap.Stack.Before.Default", new Thickness(0, 8, 0, 0)),
            ("Arena.Gap.Stack.After.Tight", new Thickness(0, 0, 0, 4)),
            ("Arena.Gap.Stack.After.Compact", new Thickness(0, 0, 0, 6)),
            ("Arena.Gap.Stack.After.Default", new Thickness(0, 0, 0, 8)),
            ("Arena.Gap.Stack.After.Comfortable", new Thickness(0, 0, 0, 10)),
            ("Arena.Gap.Stack.After.Section", new Thickness(0, 0, 0, 12)),
            ("Arena.Gap.Inline", new Thickness(6, 0, 0, 0)),
            ("Arena.Gap.Stack", new Thickness(0, 0, 0, 8)),
            ("Arena.Gap.Section", new Thickness(0, 0, 0, 12)),
        };

        foreach (var (key, expected) in expectedThickness)
        {
            Require(dictionary.Contains(key), $"DesignTokens.xaml should define {key}");
            var resource = dictionary[key];
            Require(resource is Thickness, $"{key} should resolve as Thickness");
            var actual = (Thickness)resource;
            Require(actual.Equals(expected), $"{key} should remain {expected}, found {actual}");
        }

        Require(
            ((Thickness)dictionary["Arena.Gap.Inline"]).Equals(
                (Thickness)dictionary["Arena.Gap.Inline.Before.Compact"]),
            "legacy inline gap should equal its canonical compatibility resource");
        Require(
            ((Thickness)dictionary["Arena.Gap.Stack"]).Equals(
                (Thickness)dictionary["Arena.Gap.Stack.After.Default"]),
            "legacy stack gap should equal its canonical compatibility resource");
        Require(
            ((Thickness)dictionary["Arena.Gap.Section"]).Equals(
                (Thickness)dictionary["Arena.Gap.Stack.After.Section"]),
            "legacy section gap should equal its canonical compatibility resource");

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                continue;
            }
            if (key.StartsWith("Arena.Gap.", StringComparison.Ordinal) ||
                key.StartsWith("Arena.Inset.", StringComparison.Ordinal))
            {
                Require(entry.Value is Thickness, $"{key} should resolve as Thickness");
            }
            if ((key.StartsWith("Arena.Type.", StringComparison.Ordinal) &&
                 key.EndsWith("Size", StringComparison.Ordinal)) ||
                key.StartsWith("Arena.Space.", StringComparison.Ordinal))
            {
                Require(entry.Value is double, $"{key} should resolve as Double");
            }
            if (key.StartsWith("Arena.Radius.", StringComparison.Ordinal))
            {
                Require(entry.Value is CornerRadius, $"{key} should resolve as CornerRadius");
            }
        }

        var appMarkup = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/App.xaml"));
        var brushesIndex = appMarkup.IndexOf("ThemeBrushes.xaml", StringComparison.Ordinal);
        var tokensIndex = appMarkup.IndexOf("DesignTokens.xaml", StringComparison.Ordinal);
        var controlsIndex = appMarkup.IndexOf("ControlStyles.xaml", StringComparison.Ordinal);
        var surfacesIndex = appMarkup.IndexOf("SurfaceStyles.xaml", StringComparison.Ordinal);
        Require(brushesIndex >= 0, "App.xaml should merge themed brushes");
        Require(tokensIndex > brushesIndex, "App.xaml should merge design tokens after themed brushes");
        Require(controlsIndex > tokensIndex, "App.xaml should merge control styles after design tokens");
        Require(surfacesIndex > controlsIndex, "App.xaml should merge surface styles after control styles");
    });
}

static void MainWindowCollaboratePromptUsesMultilineAlignment()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var prompt = XamlElementBlock(xaml, "CollaboratePromptText", "TextBox");

    Require(prompt.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal), "collaborate composer should remain multiline");
    Require(prompt.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "collaborate composer should wrap long prompts");
    Require(prompt.Contains("VerticalContentAlignment=\"Top\"", StringComparison.Ordinal), "collaborate composer should align multiline text to the top");
    Require(prompt.Contains("HorizontalContentAlignment=\"Left\"", StringComparison.Ordinal), "collaborate composer should align multiline text to the left");
}

static void MainWindowCollaboratePromptAssistButtonsStayCompact()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    foreach (var name in new[]
             {
                 "CollaboratePlanPromptButton",
                 "CollaborateCritiquePromptButton",
                 "CollaborateShipPromptButton",
                 "CollaborateExplainPromptButton"
             })
    {
        var button = XamlElementBlock(xaml, name, "Button");
        Require(button.Contains("Style=\"{StaticResource CollaborateQuickActionButton}\"", StringComparison.Ordinal), $"{name} should use the shared collaborate quick-action style");
        Require(button.Contains("Click=\"", StringComparison.Ordinal), $"{name} should wire a click handler");
        Require(button.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should describe its prompt-assist action");
        Require(!button.Contains("MinHeight=\"", StringComparison.Ordinal), $"{name} should inherit its compact height from the shared style");
        Require(!button.Contains("Padding=\"", StringComparison.Ordinal), $"{name} should inherit its padding from the shared style");
    }

    var quickActionStyle = Regex.Match(
        xaml,
        "<Style x:Key=\"CollaborateQuickActionButton\"[\\s\\S]*?</Style>",
        RegexOptions.CultureInvariant).Value;
    Require(quickActionStyle.Length > 0, "collaborate quick-action style should exist");
    Require(quickActionStyle.Contains("Arena.Target.Compact", StringComparison.Ordinal), "collaborate quick actions should retain a 28-DIP target");
    Require(quickActionStyle.Contains("Arena.Inset.QuickAction", StringComparison.Ordinal), "collaborate quick actions should use the measured inset");

    var budget = XamlElementBlock(xaml, "CollaboratePromptBudgetText", "TextBlock");
    Require(budget.Contains("MutedTextBrush", StringComparison.Ordinal), "prompt budget should use muted text styling");
    Require(budget.Contains("TextAlignment=\"Right\"", StringComparison.Ordinal), "prompt budget should align with the composer actions");
    Require(budget.Contains("Prompt 0 chars", StringComparison.Ordinal), "prompt budget should start with an empty prompt estimate");

    var receipt = XamlElementBlock(xaml, "CollaborateContextReceiptButton", "Button");
    Require(receipt.Contains("Style=\"{StaticResource CompactButton}\"", StringComparison.Ordinal), "context receipt should use the compact command style");
    Require(receipt.Contains("Content=\"Receipt\"", StringComparison.Ordinal), "context receipt should keep a concise visible label");
    Require(receipt.Contains("ToolTip=\"", StringComparison.Ordinal), "context receipt should describe what it previews");

    Require(xaml.Contains("<ComboBoxItem Content=\"Red Team\" Tag=\"redteam\" />", StringComparison.Ordinal), "collaborate mode picker should expose Red Team mode with a stable tag");
}

static void MainWindowAgentSectionIsTopLevel()
{
    var windowXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var topBarXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    var xaml = windowXaml + Environment.NewLine + topBarXaml;
    var railXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml"));
    var code = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var railHost = XamlStartTag(xaml, "ShellNavigationRail", "controls:ShellNavigationRailControl");
    var navButton = XamlStartTag(railXaml, "AgentNavButtonElement", "Button");
    var panel = XamlStartTag(xaml, "AgentWorkspacePanel", "Grid");
    var labViewToggle = XamlStartTag(xaml, "LabViewToggleGroup", "Border");
    var worldDebugToggle = XamlStartTag(xaml, "WorldDebugCheckBox", "CheckBox");
    var agentWorkspaceToggle = XamlStartTag(xaml, "AgentWorkspaceCheckBox", "CheckBox");
    var controlPlaneToggle = XamlStartTag(xaml, "ControlPlaneCheckBox", "CheckBox");

    Require(railHost.Contains("AgentNavigationRequested=\"AgentNavButton_Click\"", StringComparison.Ordinal), "the reusable navigation rail should forward Agent navigation into the existing shell handler");
    Require(railHost.Contains("SessionPerformanceRequested=\"SessionOverviewPerformance_MouseLeftButtonUp\"", StringComparison.Ordinal), "the reusable navigation rail should preserve session-summary activation behavior");
    Require(navButton.Contains("Content=\"Agent\"", StringComparison.Ordinal), "Agent nav button should be labeled as Agent");
    Require(navButton.Contains("Click=\"AgentNavButton_Click\"", StringComparison.Ordinal), "Agent nav button should route through its own click handler");
    Require(!navButton.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal), "Agent nav should be visible by default");
    Require(agentWorkspaceToggle.Contains("Content=\"Show Agent workspace in navigation\"", StringComparison.Ordinal), "Settings should expose the Agent workspace navigation toggle");
    Require(agentWorkspaceToggle.Contains("AgentWorkspaceCheckBox_Changed", StringComparison.Ordinal), "Agent workspace toggle should persist and apply shell visibility");
    Require(agentWorkspaceToggle.Contains("AutomationProperties.Name=", StringComparison.Ordinal) && agentWorkspaceToggle.Contains("AutomationProperties.HelpText=", StringComparison.Ordinal), "Agent workspace toggle should explain its behavior to accessibility clients");
    Require(controlPlaneToggle.Contains("Content=\"PowerShell control plane\"", StringComparison.Ordinal), "Settings should expose the local PowerShell control-plane toggle");
    Require(controlPlaneToggle.Contains("ControlPlaneCheckBox_Changed", StringComparison.Ordinal), "control-plane toggle should persist and start or stop the host");
    Require(controlPlaneToggle.Contains("AutomationProperties.Name=\"Toggle AI Arena control plane\"", StringComparison.Ordinal), "control-plane toggle should expose automation naming");
    var powerShellSettingsIndex = xaml.IndexOf("<Expander Header=\"PowerShell Control\"", StringComparison.Ordinal);
    var controlPlaneToggleIndex = xaml.IndexOf("x:Name=\"ControlPlaneCheckBox\"", StringComparison.Ordinal);
    var agentSettingsIndex = xaml.IndexOf("x:Name=\"AgentSettingsExpander\"", StringComparison.Ordinal);
    var agentWorkspaceToggleIndex = xaml.IndexOf("x:Name=\"AgentWorkspaceCheckBox\"", StringComparison.Ordinal);
    Require(powerShellSettingsIndex >= 0 && controlPlaneToggleIndex > powerShellSettingsIndex, "control-plane toggle should live in the normal PowerShell Control Settings section");
    Require(!topBarXaml.Contains("ControlPlaneCheckBox", StringComparison.Ordinal), "control-plane toggle should not live in the Debug popup");
    Require(agentSettingsIndex >= 0 && agentWorkspaceToggleIndex > agentSettingsIndex, "Agent workspace toggle should live in the normal Agent workspace Settings section");
    Require(!topBarXaml.Contains("AgentWorkspaceCheckBox", StringComparison.Ordinal), "Agent workspace toggle should not live in the Debug popup");
    Require(!xaml.Contains("AgentWorkspaceDebugCheckBox", StringComparison.Ordinal), "Debug should no longer own the Agent workspace preference");
    Require(panel.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal), "Agent workspace should be a dedicated switchable shell panel");
    Require(!xaml.Contains("WorldNavButton", StringComparison.Ordinal), "AI World should no longer be a left rail nav button");
    Require(labViewToggle.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal), "Transcript and World selector should be hidden by default");
    Require(worldDebugToggle.Contains("Content=\"AI World (3D)\"", StringComparison.Ordinal), "Debug menu should expose the experimental AI World toggle");
    Require(worldDebugToggle.Contains("WorldDebugChanged", StringComparison.Ordinal), "AI World debug toggle should route through the reusable top-bar interaction contract");
    Require(worldDebugToggle.Contains("AutomationProperties.Name=", StringComparison.Ordinal) && worldDebugToggle.Contains("AutomationProperties.HelpText=", StringComparison.Ordinal), "AI World debug toggle should explain its experimental behavior to accessibility clients");
    Require(xaml.IndexOf("WorldDebugCheckBox", StringComparison.Ordinal) > xaml.IndexOf("DebugMenuPopup", StringComparison.Ordinal), "AI World toggle should live in the top Debug menu");
    Require(xaml.IndexOf("LabTranscriptViewButton", StringComparison.Ordinal) < xaml.IndexOf("LabWorldViewButton", StringComparison.Ordinal), "enabled Lab view selector should still offer Transcript before World");
    Require(railXaml.IndexOf("AgentNavButtonElement", StringComparison.Ordinal) < railXaml.IndexOf("CollaborateNavButtonElement", StringComparison.Ordinal), "Agent nav should not be nested after AI Collaborate controls");
    Require(xaml.IndexOf("AgentWorkspacePanel", StringComparison.Ordinal) < xaml.IndexOf("CollaboratePanel", StringComparison.Ordinal), "Agent workspace should be a sibling before Collaborate, not content inside Collaborate");
    Require(railXaml.IndexOf("AgentLeftRailContextPanelElement", StringComparison.Ordinal) < railXaml.IndexOf("CollaborateLeftRailContextPanelElement", StringComparison.Ordinal), "Agent left-rail context should be separate from Collaborate left-rail context");
    Require(xaml.IndexOf("AgentTopBarMetrics", StringComparison.Ordinal) < xaml.IndexOf("CollaborateTopBarMetrics", StringComparison.Ordinal), "Agent top metrics should be separate from Collaborate metrics");
    Require(xaml.IndexOf("AgentRightRailPanel", StringComparison.Ordinal) < xaml.IndexOf("CollaborateRightRailPanel", StringComparison.Ordinal), "Agent right rail should be separate from Collaborate right rail");
    Require(code.Contains("private void AgentNavButton_Click", StringComparison.Ordinal), "MainWindow should expose a dedicated Agent nav handler");
    Require(code.Contains("IsAgentWorkspaceEnabled(_wpfSettings)", StringComparison.Ordinal), "Agent nav should obey the normal Agent workspace preference");
    Require(code.Contains("IsWorldDebugEnabled(_wpfSettings)", StringComparison.Ordinal), "AI World entry points should be guarded by the debug toggle");
    Require(code.Contains("ApplyWorldDebugVisibility(persistIfForcedOff: true)", StringComparison.Ordinal), "disabling master debug controls should immediately force AI World back to Transcript");
    Require(code.Contains("ApplyAgentWorkspaceVisibility", StringComparison.Ordinal), "MainWindow should apply Agent visibility after settings changes");
    Require(code.Contains("ShellNavigation.ShowAgentPanel();", StringComparison.Ordinal), "Agent nav should call the Agent shell surface");
    Require(code.Contains("AgentWorkspace.RefreshProviderState();", StringComparison.Ordinal), "Agent nav should refresh workspace/provider chrome when opened");

    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings()), "AI World debug should default off");
    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings { ShowWorldDebug = true }), "AI World should remain off without master debug controls");
    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings { AllowDebugControls = true }), "master debug controls alone should not enable AI World");
    Require(MainWindow.IsWorldDebugEnabled(new WpfSettings { AllowDebugControls = true, ShowWorldDebug = true }), "AI World should enable only when both debug gates are on");
    Require(MainWindow.IsAgentWorkspaceEnabled(new WpfSettings()), "Agent workspace should be enabled by default");
    Require(MainWindow.IsAgentWorkspaceEnabled(new WpfSettings { AllowDebugControls = false, ShowAgentWorkspace = true }), "Agent workspace should not require Debug controls");
    Require(!MainWindow.IsAgentWorkspaceEnabled(new WpfSettings { AllowDebugControls = true, ShowAgentWorkspace = false }), "an explicit Agent workspace opt-out should hide it even when Debug is enabled");
}

static void ShellCommandStateMapsContextualWorkspaceCommands()
{
    var expected = new Dictionary<ShellSurface, (bool MatchSetup, bool Search, bool Export, bool View)>
    {
        [ShellSurface.Lab] = (true, true, true, true),
        [ShellSurface.World] = (true, false, false, false),
        [ShellSurface.MatchSetup] = (true, true, true, true),
        [ShellSurface.Agent] = (false, false, false, false),
        [ShellSurface.Collaborate] = (false, true, true, false)
    };

    foreach (var (surface, visibility) in expected)
    {
        var state = ShellCommandState.For(surface);
        Require(state.ShowMatchSetup == visibility.MatchSetup, $"{surface} Match Setup command visibility changed unexpectedly");
        Require(state.ShowSearch == visibility.Search, $"{surface} search command visibility changed unexpectedly");
        Require(state.ShowExport == visibility.Export, $"{surface} export command visibility changed unexpectedly");
        Require(state.ShowView == visibility.View, $"{surface} View command visibility changed unexpectedly");

        Require(
            state.ShowSearch == !string.IsNullOrWhiteSpace(state.SearchAutomationName)
            && state.ShowSearch == !string.IsNullOrWhiteSpace(state.SearchHelpText),
            $"{surface} search visibility and accessibility copy should stay in sync");
        Require(
            state.ShowExport == !string.IsNullOrWhiteSpace(state.ExportAutomationName)
            && state.ShowExport == !string.IsNullOrWhiteSpace(state.ExportHelpText),
            $"{surface} export visibility and accessibility copy should stay in sync");
    }

    var lab = ShellCommandState.For(ShellSurface.Lab);
    var collaborate = ShellCommandState.For(ShellSurface.Collaborate);
    Require(lab.SearchAutomationName.Contains("transcript", StringComparison.OrdinalIgnoreCase), "Lab search should announce transcript scope");
    Require(lab.ExportAutomationName.Contains("transcript", StringComparison.OrdinalIgnoreCase), "Lab export should announce transcript scope");
    Require(collaborate.SearchAutomationName.Contains("Collaborate", StringComparison.OrdinalIgnoreCase), "Collaborate search should announce its active workspace");
    Require(collaborate.ExportAutomationName.Contains("Collaborate", StringComparison.OrdinalIgnoreCase), "Collaborate export should announce its active workspace");
    Require(ShellCommandState.For(ShellSurface.MatchSetup) == lab, "Match Setup should preserve the complete Lab command layout while replacing the transcript canvas");
}

static void MainWindowContextualCommandHostsAndProviderMetricsStayWired()
{
    var document = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var topBarDocument = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    XElement Named(string name)
    {
        return document.Descendants().Concat(topBarDocument.Descendants()).SingleOrDefault(element =>
                   string.Equals((string?)element.Attribute(xamlNamespace + "Name"), name, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"MainWindow XAML should contain '{name}'.");
    }

    var searchHost = Named("SearchCommandHost");
    Require(searchHost.Name.LocalName == "Grid", "search commands should have a dedicated visibility host");
    Require(searchHost.Descendants().Contains(Named("TranscriptSearchButton")), "SearchCommandHost should own the contextual search button");

    var viewHost = Named("ViewMenuHost");
    Require(viewHost.Name.LocalName == "Grid", "View should have a dedicated visibility host");
    Require(viewHost.Descendants().Contains(Named("ViewMenuButton")), "ViewMenuHost should own the transcript View command");
    var viewAndDebugGroup = Named("ViewAndDebugToolbarGroup");
    Require(viewAndDebugGroup.Descendants().Contains(viewHost) && viewAndDebugGroup.Descendants().Contains(Named("DebugMenuHost")), "the View and Debug hosts should share a visibility-aware toolbar group");
    Require(!viewAndDebugGroup.Descendants().Contains(searchHost), "the visibility-aware View and Debug group should not hide the always-available Agent help commands");
    var collapsedVisibilityConditions = viewAndDebugGroup
        .Descendants()
        .Where(element => element.Name.LocalName == "Condition"
            && string.Equals((string?)element.Attribute("Value"), "Collapsed", StringComparison.Ordinal))
        .Select(element => (string?)element.Attribute("Binding"))
        .ToArray();
    Require(
        collapsedVisibilityConditions.Any(binding => binding?.Contains("ElementName=ViewMenuHost", StringComparison.Ordinal) == true)
        && collapsedVisibilityConditions.Any(binding => binding?.Contains("ElementName=DebugMenuHost", StringComparison.Ordinal) == true),
        "the shared View and Debug toolbar chrome should collapse when both contextual hosts are collapsed");
    Require(
        viewAndDebugGroup.Descendants().Any(element => element.Name.LocalName == "Setter"
            && string.Equals((string?)element.Attribute("Property"), "Visibility", StringComparison.Ordinal)
            && string.Equals((string?)element.Attribute("Value"), "Collapsed", StringComparison.Ordinal)),
        "the empty View and Debug toolbar group should not leave phantom chrome in Agent mode");

    var themePicker = Named("ThemePicker");
    var visualsSection = themePicker.Ancestors().SingleOrDefault(element =>
        element.Name.LocalName == "Expander"
        && string.Equals((string?)element.Attribute("Header"), "Visuals", StringComparison.Ordinal));
    Require(visualsSection is not null, "the application theme should live in the Visuals settings section");
    Require(!themePicker.Ancestors().Contains(Named("TopBarCommandPanel")), "the low-frequency theme preference should not consume top-toolbar command space");

    foreach (var name in new[] { "AgentTopProviderStatusButton", "CollaborateTopProviderStatusButton" })
    {
        var provider = Named(name);
        Require(provider.Name.LocalName == "Border", $"{name} should remain a metric pill");
        Require(
            string.Equals((string?)provider.Attribute("Style"), "{StaticResource InteractiveTopMetricPill}", StringComparison.Ordinal),
            $"{name} should expose the same visible interaction affordance as the Lab provider metric");
        Require(
            string.Equals((string?)provider.Attribute("MouseLeftButtonUp"), "ProviderPointerActivated", StringComparison.Ordinal),
            $"{name} should route pointer activation through the reusable top bar");
        Require(
            string.Equals((string?)provider.Attribute("KeyDown"), "ProviderKeyboardActivated", StringComparison.Ordinal),
            $"{name} should route keyboard activation through the reusable top bar");
        Require(!string.IsNullOrWhiteSpace((string?)provider.Attribute("AutomationProperties.Name")), $"{name} should expose an automation name");
        Require(!string.IsNullOrWhiteSpace((string?)provider.Attribute("AutomationProperties.HelpText")), $"{name} should explain its provider-health destination");
    }
}

static void MainWindowNavigationTransitionsPreserveContext()
{
    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));

    var surfaceMethods = new Dictionary<string, ShellSurface>
    {
        ["private void ShowTranscriptPanel(bool clearFilters)"] = ShellSurface.Lab,
        ["private void ShowCustomMatchPanel()"] = ShellSurface.MatchSetup,
        ["private void ShowWorldPanel()"] = ShellSurface.World,
        ["private void ShowAgentPanel()"] = ShellSurface.Agent,
        ["private void ShowCollaboratePanel()"] = ShellSurface.Collaborate
    };
    foreach (var (signature, surface) in surfaceMethods)
    {
        var method = CSharpMethodBlock(source, signature);
        var selectSurface = method.IndexOf($"_activeShellSurface = ShellSurface.{surface}", StringComparison.Ordinal);
        var applyCommands = method.IndexOf("ApplyShellCommandState(_activeShellSurface)", StringComparison.Ordinal);
        Require(selectSurface >= 0, $"{signature} should select the {surface} shell surface");
        Require(applyCommands > selectSurface, $"{signature} should apply contextual commands after selecting {surface}");
    }

    var escape = CSharpMethodBlock(source, "private bool CloseTopmostShellOverlay()");
    var matchSetupVisibility = escape.IndexOf("CustomMatchPanel.Visibility == Visibility.Visible", StringComparison.Ordinal);
    var closeMatchSetup = escape.IndexOf("CloseMatchSetupFlyout()", StringComparison.Ordinal);
    Require(matchSetupVisibility >= 0 && closeMatchSetup > matchSetupVisibility, "Escape should close Match Setup when it is the topmost shell surface");

    var showMatchSetup = CSharpMethodBlock(source, "private void ShowCustomMatchPanel()");
    Require(showMatchSetup.Contains("_matchSetupReturnSurface = _activeShellSurface", StringComparison.Ordinal), "opening Match Setup should capture the current shell surface");
    Require(showMatchSetup.Contains("_matchSetupFocusReturnTarget = Keyboard.FocusedElement ?? MatchSetupButton", StringComparison.Ordinal), "opening Match Setup should capture its focus return target");
    Require(showMatchSetup.Contains("CloseMatchSetupButton.Focus()", StringComparison.Ordinal), "opening Match Setup should move focus into the flyout");

    var toggleMatchSetup = CSharpMethodBlock(source, "private void MatchSetupButton_Click(object sender, RoutedEventArgs e)");
    var visibleSetup = toggleMatchSetup.IndexOf("CustomMatchPanel.Visibility == Visibility.Visible", StringComparison.Ordinal);
    var closeVisibleSetup = toggleMatchSetup.IndexOf("CloseMatchSetupFlyout()", StringComparison.Ordinal);
    var showClosedSetup = toggleMatchSetup.IndexOf("ShowCustomMatchPanel()", StringComparison.Ordinal);
    Require(visibleSetup >= 0 && closeVisibleSetup > visibleSetup && showClosedSetup > closeVisibleSetup, "the persistent Match Setup command should close an open setup before opening a closed one");

    var applyShellCommands = CSharpMethodBlock(source, "private void ApplyShellCommandState(ShellSurface surface)");
    Require(applyShellCommands.Contains("ShellCommandState.For(surface)", StringComparison.Ordinal), "Match Setup should preserve the Lab command layout through its surface command state");
    Require(applyShellCommands.Contains("surface == ShellSurface.MatchSetup && state.ShowMatchSetup", StringComparison.Ordinal), "the preserved Match Setup command should expose its open state");
    Require(!applyShellCommands.Contains("_matchSetupReturnSurface", StringComparison.Ordinal), "Match Setup commands should stay in Lab context instead of inheriting an unrelated return workspace");

    var labViewToggle = CSharpMethodBlock(source, "private void LabViewToggle_Click(object sender, RoutedEventArgs e)");
    var openMatchSetupCheck = labViewToggle.IndexOf("CustomMatchPanel.Visibility == Visibility.Visible", StringComparison.Ordinal);
    var closeBeforeSwitch = labViewToggle.IndexOf("CloseMatchSetupFlyout()", StringComparison.Ordinal);
    var applySelectedView = labViewToggle.IndexOf("ApplyLabViewMode(tag, persist: true)", StringComparison.Ordinal);
    Require(openMatchSetupCheck >= 0 && closeBeforeSwitch > openMatchSetupCheck && applySelectedView > closeBeforeSwitch, "the Transcript/World selector should publish a normal Match Setup close before switching Lab views");

    var labViewToggleVisibility = CSharpMethodBlock(source, "private void UpdateLabViewToggleVisibility()");
    Require(!labViewToggleVisibility.Contains("CustomMatchPanel.Visibility", StringComparison.Ordinal), "opening Match Setup should not remove an enabled Transcript/World top-rail group");
    Require(labViewToggleVisibility.Contains("TranscriptPanel.Visibility == Visibility.Visible", StringComparison.Ordinal), "the Lab view toggle should remain tied to the underlying transcript surface");

    var closeMatchSetupMethod = CSharpMethodBlock(source, "private void CloseMatchSetupFlyout()");
    foreach (var surface in new[] { ShellSurface.World, ShellSurface.Agent, ShellSurface.Collaborate })
    {
        Require(closeMatchSetupMethod.Contains($"case ShellSurface.{surface}", StringComparison.Ordinal), $"closing Match Setup should restore the prior {surface} surface");
    }
    Require(closeMatchSetupMethod.Contains("ShowTranscriptPanel(clearFilters: false)", StringComparison.Ordinal), "closing Match Setup should fall back to Lab without clearing transcript state");
    Require(closeMatchSetupMethod.Contains("RestoreOverlayFocus(", StringComparison.Ordinal), "closing Match Setup should restore focus to its opener");
    Require(closeMatchSetupMethod.Contains("returnTarget", StringComparison.Ordinal) && closeMatchSetupMethod.Contains("MatchSetupButton", StringComparison.Ordinal), "Match Setup focus restoration should keep a stable fallback");

    var pointerTurns = CSharpMethodBlock(source, "private void SessionOverviewTurns_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)");
    Require(pointerTurns.Contains("ShowTranscriptPanel(clearFilters: false)", StringComparison.Ordinal), "the Turns metric should preserve transcript filters for pointer users");
    Require(!pointerTurns.Contains("clearFilters: true", StringComparison.Ordinal), "the Turns metric should not silently clear filters");

    var overviewKeyboard = CSharpMethodBlock(source, "private void SessionOverviewHotspot_KeyDown(object sender, KeyEventArgs e)");
    var turnsCaseStart = overviewKeyboard.IndexOf("case \"turns\":", StringComparison.Ordinal);
    var turnsCaseEnd = overviewKeyboard.IndexOf("case \"performance\":", turnsCaseStart, StringComparison.Ordinal);
    Require(turnsCaseStart >= 0 && turnsCaseEnd > turnsCaseStart, "the keyboard Turns action should remain discoverable");
    var turnsCase = overviewKeyboard[turnsCaseStart..turnsCaseEnd];
    Require(turnsCase.Contains("ShowTranscriptPanel(clearFilters: false)", StringComparison.Ordinal), "the keyboard Turns action should preserve transcript filters");
    Require(!turnsCase.Contains("clearFilters: true", StringComparison.Ordinal), "the keyboard Turns action should not silently clear filters");

    var providerPointer = CSharpMethodBlock(source, "private void TopProviderValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)");
    var providerKeyboard = CSharpMethodBlock(source, "private void TopProviderValue_KeyDown(object sender, KeyEventArgs e)");
    Require(providerPointer.Contains("ShowProviderHealthPopup(sender as UIElement)", StringComparison.Ordinal), "provider pointer activation should pass the actual opener");
    Require(providerKeyboard.Contains("ShowProviderHealthPopup(sender as UIElement)", StringComparison.Ordinal), "provider keyboard activation should pass the actual opener");

    var showProvider = CSharpMethodBlock(source, "private void ShowProviderHealthPopup(UIElement? opener = null)");
    Require(showProvider.Contains("opener ?? ActiveProviderStatusButton()", StringComparison.Ordinal), "provider health should resolve a dynamic active-surface opener");
    Require(showProvider.Contains("ProviderHealthPopup.PlacementTarget = target", StringComparison.Ordinal), "provider health should anchor to the dynamic opener");
    Require(showProvider.Contains("_providerHealthFocusReturnTarget = target", StringComparison.Ordinal), "provider health should restore focus to the dynamic opener");

    var activeProvider = CSharpMethodBlock(source, "private UIElement ActiveProviderStatusButton()");
    Require(activeProvider.Contains("return AgentTopProviderStatusButton", StringComparison.Ordinal), "Agent should resolve its own provider metric opener");
    Require(activeProvider.Contains("return CollaborateTopProviderStatusButton", StringComparison.Ordinal), "Collaborate should resolve its own provider metric opener");
    Require(activeProvider.Contains("return TopProviderStatusButton", StringComparison.Ordinal), "Lab should remain the provider opener fallback");

    var providerDeepLink = CSharpMethodBlock(source, "private void OpenModelProviderSettings(string? baseUrl = null, string? model = null)");
    var clearSearch = providerDeepLink.IndexOf("SettingsSearchText.Clear()", StringComparison.Ordinal);
    var openProvider = providerDeepLink.IndexOf("AppSettingsWorkflow.OpenModelProviderSettings", StringComparison.Ordinal);
    Require(clearSearch >= 0 && openProvider > clearSearch, "provider deep links should clear a stale Settings filter before revealing and focusing the provider section");
}

static string CSharpMethodBlock(string source, string signature)
{
    var signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
    Require(signatureStart >= 0, $"C# method '{signature}' should exist");
    var bodyStart = source.IndexOf('{', signatureStart + signature.Length);
    Require(bodyStart >= 0, $"C# method '{signature}' should have a body");

    var depth = 0;
    for (var index = bodyStart; index < source.Length; index++)
    {
        switch (source[index])
        {
            case '{':
                depth++;
                break;
            case '}':
                depth--;
                if (depth == 0)
                {
                    return source[signatureStart..(index + 1)];
                }
                break;
        }
    }

    throw new InvalidOperationException($"C# method '{signature}' should have balanced braces.");
}

static void MainWindowAgentCommandRailExposesApprovalContract()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var shellStart = xaml.IndexOf("AgentCommandShellPicker", StringComparison.Ordinal);
    var shellEnd = xaml.IndexOf("AgentCommandText", shellStart, StringComparison.Ordinal);
    Require(shellStart >= 0 && shellEnd > shellStart, "Agent command shell picker should appear before the command editor");
    var shellPicker = xaml[shellStart..shellEnd];
    var workspaceControls = XamlStartTag(xaml, "AgentWorkspaceControlsExpander", "Expander");
    var composerMenuButton = XamlStartTag(xaml, "AgentComposerMenuButton", "Button");
    var composerControls = XamlStartTag(xaml, "AgentComposerControlsPopup", "Popup");
    var advancedControls = XamlStartTag(xaml, "AgentAdvancedRailExpander", "Expander");
    var commandText = XamlStartTag(xaml, "AgentCommandText", "TextBox");
    var preview = XamlStartTag(xaml, "AgentCommandPreviewButton", "Button");
    var run = XamlStartTag(xaml, "AgentCommandRunButton", "Button");
    var stopCommand = XamlStartTag(xaml, "AgentCommandStopButton", "Button");
    var reject = XamlStartTag(xaml, "AgentCommandRejectButton", "Button");
    var phaseSummary = XamlStartTag(xaml, "AgentPhaseSummaryText", "TextBlock");
    var phaseItems = XamlStartTag(xaml, "AgentPhaseItems", "StackPanel");
    var evidenceSummary = XamlStartTag(xaml, "AgentBuildEvidenceSummaryText", "TextBlock");
    var evidenceItems = XamlStartTag(xaml, "AgentBuildEvidenceItems", "StackPanel");
    var outputSummary = XamlStartTag(xaml, "AgentOutputSummaryText", "TextBlock");
    var outputItems = XamlStartTag(xaml, "AgentOutputItems", "StackPanel");
    var copyCommand = XamlStartTag(xaml, "AgentCommandCopyButton", "Button");
    var clearCommand = XamlStartTag(xaml, "AgentCommandClearButton", "Button");
    var useHeld = XamlStartTag(xaml, "AgentCommandUseHeldButton", "Button");
    var approveAll = XamlStartTag(xaml, "AgentCommandApproveAllButton", "Button");
    var approveAllStatus = XamlStartTag(xaml, "AgentCommandApproveAllStatusText", "TextBlock");
    var autoContinue = XamlStartTag(xaml, "AgentCommandAutoContinueButton", "Button");
    var autoContinueStatus = XamlStartTag(xaml, "AgentCommandAutoContinueStatusText", "TextBlock");
    var buildApp = XamlStartTag(xaml, "AgentBuildAppPromptButton", "Button");
    var nextStep = XamlStartTag(xaml, "AgentNextStepPromptButton", "Button");
    var verify = XamlStartTag(xaml, "AgentVerifyPromptButton", "Button");
    var rescue = XamlStartTag(xaml, "AgentRescueCommandButton", "Button");
    var promptEditor = XamlStartTag(xaml, "AgentPromptText", "TextBox");
    var source = XamlStartTag(xaml, "AgentCommandSourceText", "TextBlock");
    var risks = XamlStartTag(xaml, "AgentCommandRiskItems", "WrapPanel");
    var approval = XamlStartTag(xaml, "AgentCommandApprovalText", "TextBlock");
    var output = XamlStartTag(xaml, "AgentCommandOutputText", "TextBox");
    var copyOutput = XamlStartTag(xaml, "AgentCommandCopyOutputButton", "Button");
    var copyReceipt = XamlStartTag(xaml, "AgentCommandCopyReceiptButton", "Button");
    var workSummary = XamlStartTag(xaml, "AgentCommandWorkSummaryText", "TextBlock");
    var copyBrief = XamlStartTag(xaml, "AgentCommandCopyBriefButton", "Button");
    var stageNext = XamlStartTag(xaml, "AgentCommandStageNextButton", "Button");
    var stageVerify = XamlStartTag(xaml, "AgentCommandStageVerifyButton", "Button");
    var stageArtifact = XamlStartTag(xaml, "AgentCommandStageArtifactButton", "Button");
    var historySummary = XamlStartTag(xaml, "AgentCommandHistorySummaryText", "TextBlock");
    var historyItems = XamlStartTag(xaml, "AgentCommandHistoryItems", "StackPanel");
    var replayLast = XamlStartTag(xaml, "AgentCommandReplayLastButton", "Button");
    var copyHistory = XamlStartTag(xaml, "AgentCommandCopyHistoryButton", "Button");
    var workspaceDrawerStart = xaml.IndexOf("AgentWorkspaceControlsExpander", StringComparison.Ordinal);
    var composerDrawerStart = xaml.IndexOf("AgentComposerControlsPopup", StringComparison.Ordinal);
    var promptEditorStart = xaml.IndexOf("AgentPromptText", StringComparison.Ordinal);
    var advancedDrawerStart = xaml.IndexOf("AgentAdvancedRailExpander", StringComparison.Ordinal);
    var activityStart = xaml.IndexOf("Agent Activity", advancedDrawerStart, StringComparison.Ordinal);

    Require(workspaceControls.Contains("IsExpanded=\"False\"", StringComparison.Ordinal), "Agent workspace controls should be collapsed by default");
    Require(composerMenuButton.Contains("Click=\"AgentComposerMenuButton_Click\"", StringComparison.Ordinal), "Agent composer should open deep controls from a compact menu button");
    Require(composerMenuButton.Contains("AutomationProperties.Name=\"Open Agent controls\"", StringComparison.Ordinal), "Agent composer menu button should expose an automation name");
    Require(composerControls.Contains("Placement=\"Top\"", StringComparison.Ordinal), "Agent composer controls should open as an above-composer popup");
    Require(composerControls.Contains("StaysOpen=\"False\"", StringComparison.Ordinal), "Agent composer controls popup should dismiss like a menu");
    Require(advancedControls.Contains("IsExpanded=\"False\"", StringComparison.Ordinal), "Agent command/output tuning should be collapsed by default");
    Require(xaml.IndexOf("AgentWorkspacePathText", workspaceDrawerStart, StringComparison.Ordinal) < xaml.IndexOf("AgentConversationFrame", workspaceDrawerStart, StringComparison.Ordinal), "workspace picker should live in the workspace drawer before the transcript");
    Require(xaml.IndexOf("AgentBuildAppPromptButton", composerDrawerStart, StringComparison.Ordinal) < promptEditorStart, "prompt-assist chips should live in the composer controls popup");
    Require(xaml.IndexOf("AgentCommandAutoContinueButton", composerDrawerStart, StringComparison.Ordinal) < promptEditorStart, "Auto Continue tuning should live in the composer controls popup");
    Require(buildApp.Contains("Click=\"AgentComposerMenuAction_Click\"", StringComparison.Ordinal), "composer popup actions should dismiss after use");
    Require(autoContinue.Contains("Click=\"AgentComposerMenuAction_Click\"", StringComparison.Ordinal), "Auto Continue popup action should dismiss after use");
    Require(promptEditor.Contains("Height=\"72\"", StringComparison.Ordinal), "Agent prompt editor should keep a stable height");
    Require(promptEditor.Contains("MaxHeight=\"72\"", StringComparison.Ordinal), "Agent prompt editor should not resize the composer rail");
    Require(xaml.IndexOf("AgentCommandApproveAllButton", promptEditorStart, StringComparison.Ordinal) < xaml.IndexOf("AgentCommandApproveAllStatusText", promptEditorStart, StringComparison.Ordinal), "Full Access should remain visible as the session autonomy control");
    Require(xaml.IndexOf("AgentPhaseSummaryText", StringComparison.Ordinal) < advancedDrawerStart, "Agent progress should stay visible outside advanced controls");
    Require(xaml.IndexOf("AgentOutputSummaryText", StringComparison.Ordinal) < advancedDrawerStart, "Agent outputs should stay visible outside advanced controls");
    Require(xaml.IndexOf("AgentCommandStageArtifactButton", StringComparison.Ordinal) < advancedDrawerStart, "Use Artifact should stay visible outside advanced controls");
    Require(xaml.IndexOf("Command Approval", advancedDrawerStart, StringComparison.Ordinal) < activityStart, "command approval should live inside the advanced drawer");
    Require(xaml.IndexOf("Terminal Output", advancedDrawerStart, StringComparison.Ordinal) < activityStart, "terminal output should live inside the advanced drawer");
    Require(xaml.IndexOf("Command History", advancedDrawerStart, StringComparison.Ordinal) < activityStart, "command history should live inside the advanced drawer");
    Require(buildApp.Contains("Content=\"Build App\"", StringComparison.Ordinal), "Agent prompt chips should expose an app-building workflow");
    Require(nextStep.Contains("Content=\"Next Step\"", StringComparison.Ordinal), "Agent prompt chips should expose a terminal-output follow-up workflow");
    Require(verify.Contains("Content=\"Verify\"", StringComparison.Ordinal), "Agent prompt chips should expose a verification workflow");
    Require(verify.Contains("ToolTip=\"", StringComparison.Ordinal), "Agent verify prompt should describe its command-verification purpose");
    Require(rescue.Contains("Content=\"Rescue\"", StringComparison.Ordinal), "Agent prompt chips should expose a command rescue workflow");
    Require(rescue.Contains("ToolTip=\"", StringComparison.Ordinal), "Agent rescue prompt should describe its command-recovery purpose");
    Require(phaseSummary.Contains("Ready for a software task", StringComparison.Ordinal), "Agent work loop should show an initial phase summary");
    Require(phaseItems.Contains("AgentPhaseItems", StringComparison.Ordinal), "Agent work loop should expose a stable phase row host");
    Require(xaml.Contains("Build Evidence", StringComparison.Ordinal), "Agent right rail should expose build evidence separately from role phases");
    Require(evidenceSummary.Contains("No app-building task yet", StringComparison.Ordinal), "Agent build evidence should show an initial summary");
    Require(evidenceItems.Contains("AgentBuildEvidenceItems", StringComparison.Ordinal), "Agent build evidence should expose a stable row host");
    Require(outputSummary.Contains("No artifacts yet", StringComparison.Ordinal), "Agent outputs should show an initial empty summary");
    Require(outputItems.Contains("AgentOutputItems", StringComparison.Ordinal), "Agent outputs should expose a stable row host");
    Require(source.Contains("Source: manual command", StringComparison.Ordinal), "Agent command rail should expose command provenance");
    Require(shellPicker.Contains("Content=\"Terminal\"", StringComparison.Ordinal), "Agent command rail should offer Terminal mode");
    Require(shellPicker.Contains("Content=\"PowerShell\"", StringComparison.Ordinal), "Agent command rail should offer PowerShell mode");
    Require(commandText.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal), "Agent command editor should support multiline commands");
    Require(commandText.Contains("FontFamily=\"Consolas\"", StringComparison.Ordinal), "Agent command editor should use a terminal-friendly font");
    Require(preview.Contains("Content=\"Preview\"", StringComparison.Ordinal), "Agent command rail should require preview before run");
    Require(run.Contains("Content=\"Approve\"", StringComparison.Ordinal), "Agent run button should make approval explicit");
    Require(run.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "Agent run button should start disabled until preview passes");
    Require(stopCommand.Contains("Content=\"Stop\"", StringComparison.Ordinal), "Agent command rail should expose active command cancellation");
    Require(stopCommand.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "Agent command stop should start disabled until a command is running");
    Require(stopCommand.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "Agent command stop should expose automation help");
    Require(reject.Contains("Content=\"Reject\"", StringComparison.Ordinal), "Agent command rail should expose rejection");
    Require(reject.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "Agent reject button should start disabled until preview passes");
    Require(copyCommand.Contains("Content=\"Copy\"", StringComparison.Ordinal), "Agent command rail should expose command copy");
    Require(clearCommand.Contains("Content=\"Clear\"", StringComparison.Ordinal), "Agent command rail should expose command clearing");
    Require(useHeld.Contains("Content=\"Use Held\"", StringComparison.Ordinal), "Agent command rail should expose held proposal staging");
    Require(useHeld.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "held proposal staging should start disabled");
    Require(approveAll.Contains("Content=\"Approval\"", StringComparison.Ordinal), "Agent composer should start in explicit Approval mode");
    Require(approveAll.Contains("AutomationProperties.Name=\"Approval mode for Agent commands\"", StringComparison.Ordinal), "Agent composer should label the manual approval state");
    Require(approveAll.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "Full Access should explain that workspace validation still applies");
    Require(approveAll.Contains("blocked-preview stops", StringComparison.Ordinal), "Full Access help text should explain blocked-preview stops");
    Require(approveAllStatus.Contains("Approval mode", StringComparison.Ordinal), "Full Access status should start in explicit-approval mode");
    Require(approveAllStatus.Contains("explicit approval", StringComparison.Ordinal), "explicit command approval should use Approval wording");
    Require(autoContinue.Contains("Content=\"Auto Continue\"", StringComparison.Ordinal), "Agent command rail should expose bounded follow-up loops");
    Require(autoContinue.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "Auto Continue should explain its follow-up command behavior");
    Require(autoContinue.Contains("loop guards", StringComparison.Ordinal), "Auto Continue help text should mention loop guards");
    Require(autoContinueStatus.Contains("Auto Continue is off", StringComparison.Ordinal), "Auto Continue status should start in manual next-step mode");
    var riskMargin = Regex.Match(
        risks,
        "Margin=\\\"\\{DynamicResource (?<key>Arena\\.[^}]+)\\}\\\"",
        RegexOptions.CultureInvariant);
    Require(riskMargin.Success, "Agent risk chips should use a design-token margin");
    RunStaTest(() =>
    {
        var tokens = LoadDesignTokenDictionary();
        var key = riskMargin.Groups["key"].Value;
        Require(tokens.Contains(key), $"Agent risk-chip margin token {key} should exist");
        var resource = tokens[key];
        Require(resource is Thickness, $"Agent risk-chip margin token {key} should be a Thickness");
        var margin = (Thickness)resource;
        Require(
            margin.Equals(new Thickness(0, 0, 0, 8)),
            $"Agent risk chips should retain a bottom margin of 8, found {margin}");
    });
    Require(approval.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "Agent approval preview should wrap long invocations");
    Require(output.Contains("IsReadOnly=\"True\"", StringComparison.Ordinal), "Agent terminal output should not be editable");
    Require(output.Contains("HorizontalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), "Agent terminal output should preserve wide command lines");
    Require(output.Contains("FontFamily=\"Consolas\"", StringComparison.Ordinal), "Agent terminal output should use a terminal-friendly font");
    Require(copyOutput.Contains("Content=\"Copy Output\"", StringComparison.Ordinal), "Agent output panel should expose a copy-output action");
    Require(copyOutput.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "copy output should start disabled until command output exists");
    Require(copyReceipt.Contains("Content=\"Copy Receipt\"", StringComparison.Ordinal), "Agent output panel should expose a copy-receipt action");
    Require(copyReceipt.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "copy receipt should start disabled until a file receipt exists");
    Require(copyReceipt.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "copy receipt action should expose automation help");
    Require(workSummary.Contains("No command result yet", StringComparison.Ordinal), "Agent output panel should expose an initial work brief summary");
    Require(workSummary.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "work brief summary should wrap in the right rail");
    Require(copyBrief.Contains("Content=\"Copy Brief\"", StringComparison.Ordinal), "Agent output panel should expose a copy-brief action");
    Require(copyBrief.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "copy brief should start disabled until a command result exists");
    Require(copyBrief.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "copy brief action should expose automation help");
    Require(stageNext.Contains("Content=\"Stage Next\"", StringComparison.Ordinal), "Agent output panel should expose a result-aware next-step action");
    Require(stageNext.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "stage next should start disabled until a command result exists");
    Require(stageNext.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "stage next action should expose automation help");
    Require(stageNext.Contains("follow-up or repair", StringComparison.Ordinal), "stage next help should describe follow-up and repair behavior");
    Require(stageVerify.Contains("Content=\"Stage Verify\"", StringComparison.Ordinal), "Agent output panel should expose a stage-verify action");
    Require(stageVerify.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "stage verify should start disabled until a command result exists");
    Require(stageVerify.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "stage verify action should expose automation help");
    Require(stageArtifact.Contains("Content=\"Use Artifact\"", StringComparison.Ordinal), "Agent output panel should expose direct artifact-command staging");
    Require(stageArtifact.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "artifact command staging should start disabled until an artifact suggestion exists");
    Require(stageArtifact.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "artifact command staging should explain approval-rail behavior");
    Require(xaml.Contains("Command History", StringComparison.Ordinal), "Agent right rail should expose command history");
    Require(historySummary.Contains("No commands recorded yet", StringComparison.Ordinal), "command history should start with an empty-state summary");
    Require(historyItems.Contains("AgentCommandHistoryItems", StringComparison.Ordinal), "command history should expose a stable row host");
    Require(replayLast.Contains("Content=\"Replay Last\"", StringComparison.Ordinal), "command history should expose replay");
    Require(replayLast.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "command replay should start disabled");
    Require(copyHistory.Contains("Content=\"Copy History\"", StringComparison.Ordinal), "command history should expose copy");
    Require(copyHistory.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "command history copy should start disabled");
    Require(copyHistory.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "copy history action should expose automation help");
}

static void MainWindowExportButtonSwitchesContext()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    var code = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var button = XamlStartTag(xaml, "ExportTranscriptBottomButton", "Button");
    var status = XamlStartTag(xaml, "ExportStatusText", "TextBlock");

    Require(button.Contains("Click=\"TranscriptExportRequested\"", StringComparison.Ordinal), "top export button should route through the reusable control's single interaction contract");
    Require(button.Contains("AutomationProperties.Name=\"Export transcript\"", StringComparison.Ordinal), "top export button should expose a transcript fallback automation name");
    Require(status.Contains("TextTrimming=\"CharacterEllipsis\"", StringComparison.Ordinal), "export status should stay compact in the top bar");
    Require(code.Contains("CollaboratePanel.Visibility == Visibility.Visible", StringComparison.Ordinal), "export handler should detect the visible Collaborate surface");
    Require(code.Contains("Collaborate.ExportCurrentConversation(this);", StringComparison.Ordinal), "export handler should route Collaborate exports through the Collaborate coordinator");
    Require(code.Contains("SetExportContext(collaborate: true);", StringComparison.Ordinal), "Collaborate navigation should switch export labels");
    Require(code.Contains("SetExportContext(surface == ShellSurface.Collaborate);", StringComparison.Ordinal), "contextual shell commands should restore the export labels for their active surface");
    Require(code.Contains("AutomationProperties.SetName(ExportTranscriptBottomButton, \"Export AI Collaborate chat\");", StringComparison.Ordinal), "Collaborate export context should expose an accessible button name");
    Require(code.Contains("Export: chat", StringComparison.Ordinal), "Collaborate export context should show a chat scope badge");

    var filterHandlerStart = code.IndexOf("private void TranscriptFilter_Changed", StringComparison.Ordinal);
    var filterHandlerEnd = code.IndexOf("private void ClearTranscriptSearchButton_Click", filterHandlerStart, StringComparison.Ordinal);
    Require(filterHandlerStart >= 0 && filterHandlerEnd > filterHandlerStart, "transcript filter handler should remain discoverable");
    var filterHandler = code[filterHandlerStart..filterHandlerEnd];
    Require(filterHandler.Contains("CollaboratePanel.Visibility == Visibility.Visible", StringComparison.Ordinal), "Collaborate search typing should preserve the chat export context");
    Require(filterHandler.Contains("SetExportContext(collaborate: true);", StringComparison.Ordinal), "Collaborate search typing should restore the chat export label instead of transcript scope text");
}

static void MainWindowMatchSetupControlsExposeAutomation()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var topBarXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    foreach (var name in new[]
             {
                 "MatchSetupButton",
                 "CloseMatchSetupButton",
                 "RandomSeedButton",
                 "AiChoiceButton",
                 "CurrentTopicsButton",
                 "YoloScenarioButton",
                 "ApplyAgentCountButton",
                 "ReplayGenerationButton",
                 "ReplayNewRunButton",
                 "CopyGenerationSeedButton",
                 "CopyGenerationBriefButton",
                 "CopyGenerationSpecButton",
                 "CopyGenerationDiffButton",
                 "CopyGenerationRubricButton",
                 "CopyCurrentSetupBriefButton",
                 "CopyCurrentSetupSpecButton",
                 "ImportCurrentSetupSpecButton",
                 "ApplyRivalryMatrixPatternButton",
                 "ClearRivalryMatrixButton",
                 "ApplyRivalryMatrixButton",
                 "ForkCurrentMatchButton",
                 "OpenForkParentButton"
             })
    {
        var button = XamlStartTag(name == "MatchSetupButton" ? topBarXaml : xaml, name, "Button");
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should expose automation help text");
        Require(button.Contains("ToolTip=\"", StringComparison.Ordinal) || name == "CloseMatchSetupButton", $"{name} should retain a tooltip for mouse users");
    }

    foreach (var name in new[]
             {
                 "RandomSeedPresetPicker",
                 "RandomSeedRolePackPicker",
                 "RandomSeedStylePicker",
                 "RandomSeedIntensityPicker",
                 "RandomSeedAbsurdityPicker",
                 "AgentCountPresetPicker",
                 "AgentCountPicker",
                 "RivalryMatrixPatternPicker",
                 "GenerationHistoryFilterPicker",
                 "GenerationHistoryPicker"
             })
    {
        var comboBox = XamlStartTag(xaml, name, "ComboBox");
        Require(comboBox.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(comboBox.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should expose automation help text");
        Require(comboBox.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should retain a tooltip for mouse users");
    }

    var exactAgentCount = XamlStartTag(xaml, "AgentCountPicker", "ComboBox");
    Require(exactAgentCount.Contains("SelectionChanged=\"AgentCountPicker_SelectionChanged\"", StringComparison.Ordinal), "exact agent count picker should keep preset/status in sync");
    var currentTopicsButton = XamlStartTag(xaml, "CurrentTopicsButton", "Button");
    Require(currentTopicsButton.Contains("Click=\"CurrentTopicsButton_Click\"", StringComparison.Ordinal), "Current Topics button should call the current-topic seed handler");
    Require(xaml.Contains("<ComboBoxItem Content=\"Current Topics\" Tag=\"current_topics\"", StringComparison.Ordinal), "generation history filter should include Current Topics");

    Require(!xaml.Contains("AiChoiceTopicPromptText", StringComparison.Ordinal), "AI Choice topic prompt should live in the click dialog, not the setup toolbar");
    var aiChoiceDialog = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/Dialogs/AiChoicePromptDialog.xaml"));
    var aiChoiceTopicPrompt = XamlStartTag(aiChoiceDialog, "PromptText", "TextBox");
    Require(aiChoiceTopicPrompt.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "AI Choice dialog topic prompt should expose an automation name");
    Require(aiChoiceTopicPrompt.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "AI Choice dialog topic prompt should expose automation help text");
    Require(aiChoiceTopicPrompt.Contains("ToolTip=\"", StringComparison.Ordinal), "AI Choice dialog topic prompt should retain a tooltip for mouse users");
    var cancelButton = XamlStartTag(aiChoiceDialog, "CancelButton", "Button");
    var generateButton = XamlStartTag(aiChoiceDialog, "GenerateButton", "Button");
    Require(cancelButton.Contains("Grid.Column=\"2\"", StringComparison.Ordinal) == false, "AI Choice dialog cancel button should stay on the left");
    Require(generateButton.Contains("Grid.Column=\"2\"", StringComparison.Ordinal), "AI Choice dialog generate button should stay on the right");

    var historyStatus = XamlStartTag(xaml, "GenerationHistoryStatusText", "TextBlock");
    Require(historyStatus.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "generation history status should expose an automation name");
    Require(historyStatus.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "generation history status should expose automation help text");
    Require(historyStatus.Contains("ToolTip=\"", StringComparison.Ordinal), "generation history status should retain a tooltip for mouse users");

    var readinessStatus = XamlStartTag(xaml, "SetupReadinessStatusText", "TextBlock");
    Require(readinessStatus.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "setup readiness status should expose an automation name");
    Require(readinessStatus.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "setup readiness status should expose automation help text");
    Require(readinessStatus.Contains("ToolTip=\"", StringComparison.Ordinal), "setup readiness status should retain a tooltip for mouse users");
    var readinessBadges = XamlStartTag(xaml, "SetupReadinessBadgeItems", "WrapPanel");
    Require(readinessBadges.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "setup readiness badges should expose an automation name");
    Require(readinessBadges.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "setup readiness badges should expose automation help text");
    var readinessChecklist = XamlStartTag(xaml, "SetupReadinessChecklistItems", "StackPanel");
    Require(readinessChecklist.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "setup readiness checklist should expose an automation name");
    Require(readinessChecklist.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "setup readiness checklist should expose automation help text");

    var forkLineage = XamlStartTag(xaml, "ForkLineageText", "TextBlock");
    Require(forkLineage.Contains("AutomationProperties.Name=\"Current run lineage\"", StringComparison.Ordinal), "fork lineage should expose an automation name");
    Require(forkLineage.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "fork lineage should announce branch changes without interrupting the operator");

    var recipeStatus = XamlStartTag(xaml, "GenerationPresetStatusText", "TextBlock");
    Require(recipeStatus.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "generation recipe status should expose an automation name");
    Require(recipeStatus.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "generation recipe status should expose automation help text");
    Require(recipeStatus.Contains("ToolTip=\"", StringComparison.Ordinal), "generation recipe status should retain a tooltip for mouse users");

    foreach (var presetTag in new[] { "bureaucracy_inferno", "alien_courtroom", "meme_tribunal", "paranoid_compliance", "model_duel", "tool_reliability_trial", "governance_board", "policy_crisis_room", "market_shock", "tech_ethics_hearing", "geopolitical_risk_desk", "black_box_audit", "approval_maze", "launch_war_room", "template_forge", "memory_handoff" })
    {
        Require(xaml.Contains($"Tag=\"{presetTag}\"", StringComparison.Ordinal), $"generation preset {presetTag} should appear in Match Setup");
    }

    foreach (var rolePackTag in new[] { "benchmark_duel", "governance_board", "tool_ops" })
    {
        Require(xaml.Contains($"Tag=\"{rolePackTag}\"", StringComparison.Ordinal), $"role pack {rolePackTag} should appear in Match Setup");
    }

    var helpPopup = XamlStartTag(xaml, "GenerationHelpPopup", "Popup");
    Require(helpPopup.Contains("Placement=\"Bottom\"", StringComparison.Ordinal), "generation help popup should open from the triggering button instead of stale mouse position");

    var rivalryToggle = XamlStartTag(xaml, "RivalryMatrixEnabledCheckBox", "CheckBox");
    Require(rivalryToggle.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "relationship matrix toggle should expose an automation name");
    Require(rivalryToggle.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "relationship matrix toggle should expose automation help text");
    Require(rivalryToggle.Contains("ToolTip=\"", StringComparison.Ordinal), "relationship matrix toggle should retain a tooltip");

    var rivalryStatus = XamlStartTag(xaml, "RivalryMatrixStatusText", "TextBlock");
    Require(rivalryStatus.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "relationship matrix status should expose an automation name");
    Require(rivalryStatus.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "relationship matrix status should expose automation help text");
    Require(rivalryStatus.Contains("ToolTip=\"", StringComparison.Ordinal), "relationship matrix status should retain a tooltip");
    var rivalryInsight = XamlStartTag(xaml, "RivalryMatrixInsightText", "TextBlock");
    Require(rivalryInsight.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "relationship matrix insight should expose an automation name");
    Require(rivalryInsight.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "relationship matrix insight should expose automation help text");
    Require(rivalryInsight.Contains("ToolTip=\"", StringComparison.Ordinal), "relationship matrix insight should retain a tooltip");
    Require(xaml.Contains("x:Name=\"RivalryMatrixPreviewItems\"", StringComparison.Ordinal), "relationship matrix should include a pressure graph preview surface");
    foreach (var patternTag in new[] { "skeptic_sweep", "paired_crossfire", "spotlight_defense" })
    {
        Require(xaml.Contains($"Tag=\"{patternTag}\"", StringComparison.Ordinal), $"relationship pattern {patternTag} should appear in Match Setup");
    }

    foreach (var helpTag in new[] { "generate", "tune", "recent" })
    {
        var marker = $"Tag=\"{helpTag}\"";
        var markerIndex = xaml.IndexOf(marker, StringComparison.Ordinal);
        Require(markerIndex >= 0, $"Match Setup help button '{helpTag}' should exist");
        var start = xaml.LastIndexOf("<Button", markerIndex, StringComparison.Ordinal);
        var end = xaml.IndexOf(">", markerIndex, StringComparison.Ordinal);
        var button = xaml[start..(end + 1)];
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{helpTag} help button should expose an automation name");
        Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{helpTag} help button should expose help text");
    }
}

static void MainWindowMatchSetupMatrixHasClearAction()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var clear = XamlStartTag(xaml, "ClearRivalryMatrixButton", "Button");
    var apply = XamlStartTag(xaml, "ApplyRivalryMatrixButton", "Button");
    var status = XamlElementBlock(xaml, "RivalryMatrixStatusText", "TextBlock");

    Require(clear.Contains("Click=\"ClearRivalryMatrixButton_Click\"", StringComparison.Ordinal), "relationship matrix should wire the clear draft action");
    Require(clear.Contains("Content=\"Clear\"", StringComparison.Ordinal), "relationship matrix clear action should use a concise visible label");
    Require(clear.Contains("Grid.Column=\"1\"", StringComparison.Ordinal), "clear action should sit before the apply action");
    Require(apply.Contains("Grid.Column=\"2\"", StringComparison.Ordinal), "apply action should remain after clear");
    Require(status.Contains("VerticalAlignment=\"Center\"", StringComparison.Ordinal), "relationship matrix status should remain vertically aligned with actions");
}

static void MainWindowOperatorQuickInterventionsExposeAutomation()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var meter = XamlStartTag(xaml, "OperatorTurnMeterText", "TextBlock");
    Require(meter.Contains("0 chars / ~0 tok | Public transcript", StringComparison.Ordinal), "operator meter should advertise the default public route");
    Require(meter.Contains("AutomationProperties.Name=\"Operator draft meter\"", StringComparison.Ordinal), "operator meter should expose an automation name");
    var routeHint = XamlStartTag(xaml, "OperatorRouteHintText", "TextBlock");
    Require(routeHint.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "operator route hint should expose automation help");
    var quickHint = XamlStartTag(xaml, "OperatorQuickInterventionHintText", "TextBlock");
    Require(quickHint.Contains("AutomationProperties.Name=\"Operator quick interventions\"", StringComparison.Ordinal), "operator intervention hint should expose an automation name");
    Require(xaml.Contains("OperatorQuickInterventionHintText", StringComparison.Ordinal), "operator quick intervention hint should exist");
    foreach (var name in new[]
    {
        "OperatorQuickInterventionAButton",
        "OperatorQuickInterventionBButton",
        "OperatorQuickInterventionCButton",
        "OperatorQuickInterventionDButton"
    })
    {
        var button = XamlStartTag(xaml, name, "Button");
        Require(button.Contains("Style=\"{StaticResource CompactRailActionButton}\"", StringComparison.Ordinal), $"{name} should use the shared compact rail-action style");
        Require(button.Contains("ToolTip=\"Stage an operator intervention.\"", StringComparison.Ordinal), $"{name} should explain the staging behavior before dynamic tooltips load");
        Require(!button.Contains("MinHeight=\"", StringComparison.Ordinal), $"{name} should inherit its stable height from the shared style");
        Require(!button.Contains("Padding=\"", StringComparison.Ordinal), $"{name} should inherit its padding from the shared style");
    }

    var railActionStyle = Regex.Match(
        xaml,
        "<Style x:Key=\"CompactRailActionButton\"[\\s\\S]*?</Style>",
        RegexOptions.CultureInvariant).Value;
    Require(railActionStyle.Length > 0, "compact rail-action style should exist");
    Require(railActionStyle.Contains("BasedOn=\"{StaticResource CompactButton}\"", StringComparison.Ordinal), "compact rail actions should retain compact button behavior");
    Require(railActionStyle.Contains("Arena.Target.Icon", StringComparison.Ordinal), "compact rail actions should retain a 30-DIP target");
    Require(railActionStyle.Contains("Arena.Inset.RailAction", StringComparison.Ordinal), "compact rail actions should use the measured rail inset");
}

static void MainWindowVoiceTtsSettingsExposeAutomation()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    foreach (var name in new[]
             {
                 "VoiceTtsEnabledCheckBox",
                 "VoiceTtsAutoNarratorCheckBox"
             })
    {
        var checkBox = XamlStartTag(xaml, name, "CheckBox");
        Require(checkBox.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(checkBox.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should expose automation help text");
        Require(checkBox.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should retain a tooltip for mouse users");
        Require(checkBox.Contains("VoiceTtsSettings_Changed", StringComparison.Ordinal), $"{name} should persist TTS changes");
    }

    var voicePicker = XamlStartTag(xaml, "VoiceTtsVoicePicker", "ComboBox");
    Require(voicePicker.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "voice picker should expose an automation name");
    Require(voicePicker.Contains("VoiceTtsVoicePicker_SelectionChanged", StringComparison.Ordinal), "voice picker should persist selection changes");

    foreach (var sliderName in new[] { "VoiceTtsRateSlider", "VoiceTtsVolumeSlider" })
    {
        var slider = XamlStartTag(xaml, sliderName, "Slider");
        Require(slider.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{sliderName} should expose an automation name");
        Require(slider.Contains("ValueChanged=\"VoiceTtsSlider_Changed\"", StringComparison.Ordinal), $"{sliderName} should persist slider changes");
    }

    foreach (var name in new[] { "TestVoiceTtsButton", "StopVoiceTtsButton" })
    {
        var button = XamlStartTag(xaml, name, "Button");
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should expose automation help text");
        Require(button.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should retain a tooltip for mouse users");
    }

    Require(!xaml.Contains("StopVoicePlaybackButton", StringComparison.Ordinal), "Stop Voice should be merged into the Speak toggle button");
    foreach (var name in new[] { "SpeakLatestNarratorButton" })
    {
        var button = XamlStartTag(xaml, name, "Button");
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should expose automation help text");
        Require(button.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should retain a tooltip for mouse users");
        Require(button.Contains("Click=\"", StringComparison.Ordinal), $"{name} should wire a click handler");
    }

    Require(xaml.Contains("<UniformGrid x:Name=\"ArenaControlGrid\" Columns=\"3\" Margin=\"0,0,-6,-6\">", StringComparison.Ordinal), "arena controls should expose an adaptive grid whose row count follows its responsive column count");
    var autoChatStart = xaml.IndexOf("x:Name=\"AutoChatButton\"", StringComparison.Ordinal);
    var stopStart = xaml.IndexOf("x:Name=\"StopButton\"", autoChatStart, StringComparison.Ordinal);
    var stableRunCellEnd = xaml.IndexOf("</Grid>", stopStart, StringComparison.Ordinal);
    Require(autoChatStart >= 0 && stopStart > autoChatStart && stableRunCellEnd > stopStart, "start and pause should share one stable pointer location");
    var speakStart = xaml.IndexOf("x:Name=\"SpeakLatestNarratorButton\"", StringComparison.Ordinal);
    var resetStart = xaml.IndexOf("x:Name=\"ResetButton\"", StringComparison.Ordinal);
    Require(resetStart > speakStart, "the destructive Reset action should follow the frequent arena actions instead of separating Start from Pause");
    foreach (var name in new[] { "AutoChatButton", "OneTurnButton", "ResetButton", "StopButton", "NarrateNowButton" })
    {
        var button = XamlStartTag(xaml, name, "Button");
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an explicit automation name");
        Require(button.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{name} should explain its arena action to assistive technology");
        Require(button.Contains("ToolTip=\"", StringComparison.Ordinal), $"{name} should retain a matching mouse tooltip");
    }

    var status = XamlStartTag(xaml, "VoiceTtsStatusText", "TextBlock");
    Require(status.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), "voice TTS status should expose an automation name");
    Require(status.Contains("ToolTip=\"", StringComparison.Ordinal), "voice TTS status should retain a tooltip");
}

static void MainWindowOperatorTurnTextUsesMultilineScrollAffordance()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var editor = XamlElementBlock(xaml, "OperatorTurnText", "TextBox");

    Require(editor.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal), "operator turn editor should remain multiline");
    Require(editor.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "operator turn editor should wrap long public turns");
    Require(editor.Contains("VerticalContentAlignment=\"Top\"", StringComparison.Ordinal), "operator turn editor should align multiline text to the top");
    Require(editor.Contains("HorizontalContentAlignment=\"Left\"", StringComparison.Ordinal), "operator turn editor should align multiline text to the left");
    Require(editor.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), "operator turn editor should expose a vertical scrollbar for long turns");
}

static void MainWindowTranscriptSearchPopupSizesResponsively()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    var popupFrame = XamlStartTag(xaml, "TranscriptSearchPopupFrame", "Grid");

    Require(!popupFrame.Contains(" Width=\"760\"", StringComparison.Ordinal), "transcript search popup should not hard-code a fixed width");
    Require(popupFrame.Contains("MinWidth=\"420\"", StringComparison.Ordinal), "transcript search popup should keep a usable minimum width");
    Require(popupFrame.Contains("MaxWidth=\"760\"", StringComparison.Ordinal), "transcript search popup should keep the desktop width cap");
    Require(popupFrame.Contains("PlacementTarget.ActualWidth", StringComparison.Ordinal), "transcript search popup should size from the live placement target");
}

static void MainWindowOverlaysPreserveKeyboardAndAccessibilityContracts()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"))
        + Environment.NewLine
        + File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"))
        + Environment.NewLine
        + File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml"));
    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));

    var settingsScrim = XamlStartTag(xaml, "AppSettingsScrim", "Border");
    Require(settingsScrim.Contains("MouseLeftButtonUp=\"AppSettingsScrim_MouseLeftButtonUp\"", StringComparison.Ordinal), "settings scrim should dismiss the drawer on pointer activation");
    Require(settingsScrim.Contains("Panel.ZIndex=\"19\"", StringComparison.Ordinal), "settings scrim should sit above the shell and below the drawer");
    Require(settingsScrim.Contains("AutomationProperties.Name=\"Dismiss app settings\"", StringComparison.Ordinal), "settings scrim should expose its dismiss action");

    var settingsPanel = XamlStartTag(xaml, "AppSettingsPanel", "Border");
    Require(settingsPanel.Contains("IsVisibleChanged=\"AppSettingsPanel_IsVisibleChanged\"", StringComparison.Ordinal), "settings visibility changes should drive focus handoff for every open path");
    Require(settingsPanel.Contains("FocusManager.IsFocusScope=\"True\"", StringComparison.Ordinal), "settings should define an independent focus scope");
    Require(settingsPanel.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", StringComparison.Ordinal), "settings keyboard navigation should remain contained in the drawer");
    Require(settingsPanel.Contains("KeyboardNavigation.ControlTabNavigation=\"Cycle\"", StringComparison.Ordinal), "Control+Tab should remain contained in app settings");

    var themePicker = XamlStartTag(xaml, "ThemePicker", "ComboBox");
    Require(themePicker.Contains("AutomationProperties.Name=\"Application theme\"", StringComparison.Ordinal), "theme picker should expose its purpose to automation clients");
    Require(themePicker.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "theme picker should explain the adaptive System option");
    Require(themePicker.Contains("ToolTip=\"", StringComparison.Ordinal), "theme picker should retain a mouse affordance");

    var providerStatusButton = XamlStartTag(xaml, "TopProviderStatusButton", "Border");
    Require(providerStatusButton.Contains("Style=\"{StaticResource InteractiveTopMetricPill}\"", StringComparison.Ordinal), "the provider status opener should expose visible hover and keyboard-focus states");
    Require(providerStatusButton.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), "the provider status opener should explain its action to assistive technology");

    foreach (var name in new[] { "ProviderHealthPopup", "ViewMenuPopup", "DebugMenuPopup" })
    {
        var popup = XamlStartTag(xaml, name, "Popup");
        Require(popup.Contains("Opened=\"", StringComparison.Ordinal), $"{name} should move focus inside when opened");
        Require(popup.Contains("Closed=\"", StringComparison.Ordinal), $"{name} should restore focus when closed");
    }

    foreach (var name in new[] { "TranscriptSearchPopupContent", "ProviderHealthPopupContent", "ViewMenuPopupContent", "DebugMenuPopupContent" })
    {
        var popupContent = XamlStartTag(xaml, name, "Border");
        Require(popupContent.Contains("PreviewKeyDown=\"", StringComparison.Ordinal), $"{name} should handle Escape inside the popup window");
        Require(popupContent.Contains("FocusManager.IsFocusScope=\"True\"", StringComparison.Ordinal), $"{name} should define an independent focus scope");
        Require(popupContent.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", StringComparison.Ordinal), $"{name} should cycle keyboard focus within the flyout");
        Require(popupContent.Contains("KeyboardNavigation.ControlTabNavigation=\"Cycle\"", StringComparison.Ordinal), $"{name} should contain Control+Tab within the flyout");
        Require(popupContent.Contains("KeyboardNavigation.DirectionalNavigation=\"Contained\"", StringComparison.Ordinal), $"{name} should keep directional navigation within the flyout");
        Require(popupContent.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose a concise automation name");
    }

    foreach (var name in new[]
    {
        "AppSettingsButton",
        "CloseAppSettingsButton",
        "ProviderHealthCloseButton",
        "DiagnosticDetailCloseButton",
        "AgentPerformanceDetailCloseButton",
        "UseOperatorTemplateButton",
        "SaveOperatorTemplateButton",
        "DeleteOperatorTemplateButton"
    })
    {
        var button = XamlStartTag(xaml, name, "Button");
        Require(button.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal), $"{name} should expose an explicit automation name");
    }

    foreach (var name in new[]
    {
        "ShellStatusTextElement",
        "AgentStatusText",
        "CollaborateStatusText",
        "ProviderTestStatus",
        "VoiceTtsStatusText",
        "InternetBackendStatusText",
        "InternetDiagnosticResultText",
        "SettingsTransferStatusText"
    })
    {
        var status = XamlStartTag(xaml, name, "TextBlock");
        Require(status.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), $"{name} should announce asynchronous status changes politely");
    }

    Require(source.Contains("protected override void OnPreviewKeyDown", StringComparison.Ordinal), "the main shell should route Escape to the topmost overlay");
    Require(source.Contains("CloseTopmostShellOverlay()", StringComparison.Ordinal), "the main shell should close overlays in deterministic z-order");
    Require(source.Contains("FocusOverlayEntry", StringComparison.Ordinal), "popup opening should move focus to an actionable entry");
    Require(source.Contains("RestoreOverlayFocus", StringComparison.Ordinal), "overlay closure should restore focus to its opener");
    Require(source.Contains("ProviderHealthPopup_Opened", StringComparison.Ordinal), "provider health should explicitly move focus inside its popup window");
    Require(source.Contains("TranscriptSearchPopup_PreviewKeyDown", StringComparison.Ordinal), "search should handle Escape after focus moves from the text editor to a result row");
}

static void MainWindowAdaptiveShellLayoutStaysWired()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    var topBarXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    var railXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml"));
    var windowTag = xaml[..(xaml.IndexOf('>') + 1)];
    var topBarLayout = XamlStartTag(topBarXaml, "TopBarLayoutGrid", "Grid");
    var topBarStatus = XamlStartTag(topBarXaml, "TopBarStatus", "Grid");
    var topBarCommands = XamlStartTag(topBarXaml, "TopBarCommandPanel", "WrapPanel");
    var saveStatusProxy = XamlStartTag(topBarXaml, "SaveStatusText", "TextBlock");
    var statusDock = XamlStartTag(railXaml, "ShellStatusDockElement", "Border");
    var statusText = XamlStartTag(railXaml, "ShellStatusTextElement", "TextBlock");
    var transcriptSearchPopup = XamlStartTag(topBarXaml, "TranscriptSearchPopup", "Popup");
    var matchSetupButton = XamlStartTag(topBarXaml, "MatchSetupButton", "Button");
    var viewMenuButton = XamlStartTag(topBarXaml, "ViewMenuButton", "Button");
    var diagnosticsGrid = XamlStartTag(xaml, "TranscriptDiagnosticsGrid", "UniformGrid");
    var telemetryGrid = XamlStartTag(xaml, "TranscriptTelemetryGrid", "UniformGrid");
    var applySettingsButton = XamlStartTag(xaml, "ApplySettingsButton", "Button");

    Require(windowTag.Contains("SizeChanged=\"MainWindow_SizeChanged\"", StringComparison.Ordinal), "the shell window should route size changes into adaptive rail layout");
    Require(windowTag.Contains("UseLayoutRounding=\"True\"", StringComparison.Ordinal), "the shell should round layout at the root for crisp fractional-DPI borders");
    Require(windowTag.Contains("SnapsToDevicePixels=\"True\"", StringComparison.Ordinal), "the shell should snap its root visual to device pixels");
    Require(topBarLayout.Contains("x:Name=\"TopBarLayoutGrid\"", StringComparison.Ordinal), "the top bar should expose an adaptive grid host");
    var topBarDocument = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    var topBarLayoutElement = topBarDocument.Descendants().Single(element =>
        string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "TopBarLayoutGrid", StringComparison.Ordinal));
    var primaryTopBarRow = topBarLayoutElement
        .Elements()
        .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
        .Elements()
        .First();
    var topBarRows = topBarLayoutElement
        .Elements()
        .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
        .Elements()
        .ToArray();
    Require(string.Equals((string?)primaryTopBarRow.Attribute("MinHeight"), "38", StringComparison.Ordinal), "the shared top-bar primary row should reserve the 38-DIP command-group height");
    Require(topBarRows.Length == 2, "the top bar should contain only its primary row and narrow command row");
    var toolbarGroupStyle = topBarDocument.Descendants().Single(element =>
        element.Name.LocalName == "Style"
        && string.Equals((string?)element.Attribute(xamlNamespace + "Key"), "ToolbarGroup", StringComparison.Ordinal));
    Require(
        toolbarGroupStyle.Elements().Any(element =>
            element.Name.LocalName == "Setter"
            && string.Equals((string?)element.Attribute("Property"), "Height", StringComparison.Ordinal)
            && string.Equals((string?)element.Attribute("Value"), "38", StringComparison.Ordinal)),
        "top-rail toolbar groups should share the Match Setup button's explicit 38-DIP height");
    Require(topBarStatus.Contains("Grid.Row=\"0\"", StringComparison.Ordinal) && topBarStatus.Contains("VerticalAlignment=\"Center\"", StringComparison.Ordinal), "the top-bar metrics should occupy the shared centered primary row");
    Require(!topBarXaml.Contains("TopBarSecondaryStatus", StringComparison.Ordinal), "status should no longer add a visual secondary row beneath the top-bar metrics");
    Require(topBarCommands.Contains("Grid.Row=\"1\"", StringComparison.Ordinal) && topBarCommands.Contains("Grid.ColumnSpan=\"2\"", StringComparison.Ordinal), "the top bar should fail safe to the narrow two-row arrangement before its first size pass");
    Require(topBarCommands.Contains("HorizontalAlignment=\"Right\"", StringComparison.Ordinal), "stacked top-bar commands should remain visually anchored to the right");
    Require(topBarCommands.Contains("VerticalAlignment=\"Center\"", StringComparison.Ordinal), "inline top-bar commands should share the primary-row centerline with the metrics");
    var mainWindowSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    Require(mainWindowSource.Contains("Grid.SetRow(TopBarCommandPanel, stacked ? 1 : 0);", StringComparison.Ordinal), "the adaptive shell should move commands between the narrow command row and shared primary row");
    Require(mainWindowSource.Contains("ShellNavigationRail.Presentation = ShellTopBar.Presentation;", StringComparison.Ordinal), "the top bar and navigation status dock should share the exact presentation model instance");
    Require(transcriptSearchPopup.Contains("PlacementTarget=\"{Binding ElementName=TopBarLayoutGrid}\"", StringComparison.Ordinal), "the transcript search popup should open below the complete multi-row top bar");
    Require(matchSetupButton.Contains("Style=\"{StaticResource Arena.Button.Primary}\"", StringComparison.Ordinal), "Match Setup should retain primary emphasis in the top rail");
    Require(matchSetupButton.Contains("Height=\"38\"", StringComparison.Ordinal) && matchSetupButton.Contains("VerticalAlignment=\"Center\"", StringComparison.Ordinal), "Match Setup should match the 38-DIP top-rail command-group height");
    Require(matchSetupButton.Contains("Width=\"104\"", StringComparison.Ordinal), "Match Setup and Close Setup should share a fixed width so toggling does not shift neighboring commands");
    Require(matchSetupButton.Contains("Padding=\"10,0\"", StringComparison.Ordinal), "Match Setup should use compact horizontal-only toolbar padding");
    Require(viewMenuButton.Contains("Content=\"{Binding ViewButtonLabel}\"", StringComparison.Ordinal), "the closed View control should keep the active preset visible");
    Require(statusDock.Contains("Grid.Row=\"3\"", StringComparison.Ordinal), "the visible status dock should occupy the navigation rail's previously unused bottom row");
    Require(statusDock.Contains("Visibility=\"{Binding ShowStatusDock", StringComparison.Ordinal), "routine status should release the bottom-rail space");
    Require(statusDock.Contains("ToolTip=\"{Binding DisplayStatusToolTip}\"", StringComparison.Ordinal), "the bottom status dock should expose deterministic pointer detail");
    Require(statusDock.Contains("AutomationProperties.HelpText=\"{Binding DisplayStatusHelpText}\"", StringComparison.Ordinal), "the bottom status dock should expose deterministic automation help");
    Require(statusText.Contains("Text=\"{Binding DisplayStatus}\"", StringComparison.Ordinal), "the bottom status text should bind to the shared display projection");
    Require(statusText.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "the bottom status dock should be the polite live announcement surface");
    Require(statusText.Contains("LineHeight=\"16\"", StringComparison.Ordinal)
        && statusText.Contains("LineStackingStrategy=\"BlockLineHeight\"", StringComparison.Ordinal)
        && statusText.Contains("MaxHeight=\"32\"", StringComparison.Ordinal),
        "the bottom status dock should reserve exactly two deterministic text lines");
    Require(saveStatusProxy.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal), "the legacy save-status target should stay permanently nonvisual");
    Require(!saveStatusProxy.Contains("AutomationProperties.LiveSetting", StringComparison.Ordinal), "the compatibility save target should not duplicate live announcements");

    var topBarPresentation = new AIArena.Wpf.ViewModels.ShellTopBarPresentationViewModel();
    var changedProperties = new HashSet<string>(StringComparer.Ordinal);
    var changedPropertyOrder = new List<string>();
    topBarPresentation.PropertyChanged += (_, args) =>
    {
        if (!string.IsNullOrWhiteSpace(args.PropertyName))
        {
            changedProperties.Add(args.PropertyName);
            changedPropertyOrder.Add(args.PropertyName);
        }
    };
    topBarPresentation.ArenaStatus = "Provider online.";
    Require(!topBarPresentation.ShowStatusDock, "routine provider health should stay inside the provider metric instead of repeating in the bottom dock");
    changedPropertyOrder.Clear();
    topBarPresentation.ArenaStatus = "Select a model before running the arena.";
    Require(topBarPresentation.ShowStatusDock, "actionable arena status should reveal the bottom status dock");
    Require(topBarPresentation.DisplayStatus == "Select a model before running the arena.", "persistent actionable status should be the visible projection");
    Require(topBarPresentation.DisplayStatusToolTip == topBarPresentation.DisplayStatus, "persistent status should use its exact text as the tooltip");
    Require(topBarPresentation.DisplayStatusHelpText.Contains(topBarPresentation.DisplayStatus, StringComparison.Ordinal), "persistent status help should include the exact actionable state");
    Require(
        changedPropertyOrder.IndexOf("ShowStatusDock") < changedPropertyOrder.IndexOf("DisplayStatus"),
        "the live status dock should become visible before actionable text is projected");

    var firstGeneration = topBarPresentation.ShowTransientStatus(
        "Screenshot saved: first.png",
        @"C:\captures\first.png",
        "AI Arena saved the first screenshot.");
    Require(topBarPresentation.ShowStatusDock, "a transient receipt should reveal the bottom status dock");
    Require(topBarPresentation.DisplayStatus == "Screenshot saved: first.png", "transient status should temporarily override the persistent status");
    Require(topBarPresentation.DisplayStatusToolTip == @"C:\captures\first.png", "transient status should preserve its detailed path tooltip");
    Require(topBarPresentation.DisplayStatusHelpText == "AI Arena saved the first screenshot.", "transient status should preserve its automation help");

    topBarPresentation.ArenaStatus = "Select a provider model.";
    Require(topBarPresentation.DisplayStatus == "Screenshot saved: first.png", "persistent changes should not interrupt an active transient receipt");
    var secondGeneration = topBarPresentation.ShowTransientStatus(
        "Screenshot saved: second.png",
        @"C:\captures\second.png",
        "AI Arena saved the second screenshot.");
    Require(!topBarPresentation.ClearTransientStatus(firstGeneration), "a stale receipt timer must not clear a newer transient status");
    Require(topBarPresentation.DisplayStatus == "Screenshot saved: second.png", "rejecting a stale clear should retain the newest receipt");
    Require(topBarPresentation.ClearTransientStatus(secondGeneration), "the current transient generation should clear successfully");
    Require(topBarPresentation.DisplayStatus == "Select a provider model.", "clearing the current receipt should restore the latest persistent status");
    Require(topBarPresentation.ShowStatusDock, "restored actionable status should keep the bottom dock visible");
    changedPropertyOrder.Clear();
    topBarPresentation.ArenaStatus = "Ready.";
    Require(!topBarPresentation.ShowStatusDock, "routine status should collapse the bottom dock after a transient receipt expires");
    Require(
        changedPropertyOrder.IndexOf("DisplayStatus") < changedPropertyOrder.IndexOf("ShowStatusDock"),
        "the visible live region should project routine status before the dock collapses");
    foreach (var propertyName in new[] { "DisplayStatus", "DisplayStatusToolTip", "DisplayStatusHelpText", "ShowStatusDock" })
    {
        Require(changedProperties.Contains(propertyName), $"{propertyName} should notify the shared status binding when its projection changes");
    }

    var screenshotSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs"));
    Require(screenshotSource.Contains("ShowTransientStatus(receiptText, result.Path, helpText)", StringComparison.Ordinal), "screenshot receipts should move through the shared status presentation");
    Require(screenshotSource.Contains("ClearTransientStatus(generation)", StringComparison.Ordinal), "screenshot receipt expiry should clear only its own generation");
    Require(!screenshotSource.Contains("SetTransientStatusVisible", StringComparison.Ordinal), "screenshot receipts should not use the retired visibility-only status API");
    Require(!screenshotSource.Contains("SaveStatusText.Visibility", StringComparison.Ordinal), "the compatibility save target should never become visual");
    Require(!screenshotSource.Contains("SaveStatusText.Text.Equals", StringComparison.Ordinal), "screenshot receipt expiry should not rely on text equality for stale-clear protection");
    Require(screenshotSource.Contains("ArenaRunStatus.Text", StringComparison.Ordinal), "the control-plane snapshot should retain the persistent arena-status compatibility target");
    foreach (var presetButtonName in new[] { "ViewPresetFocusedButton", "ViewPresetDiagnosticsButton", "ViewPresetCompactButton", "ViewPresetReviewButton" })
    {
        var presetButton = XamlStartTag(topBarXaml, presetButtonName, "Button");
        Require(presetButton.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal), $"{presetButtonName} should explain its layout outcome before activation");
        Require(presetButton.Contains("ToolTip=\"", StringComparison.Ordinal), $"{presetButtonName} should expose the same outcome to pointer users");
    }
    Require(diagnosticsGrid.Contains("x:Name=\"TranscriptDiagnosticsGrid\"", StringComparison.Ordinal), "the diagnostics grid should remain addressable by the adaptive coordinator");
    Require(!diagnosticsGrid.Contains("MinWidth=\"900\"", StringComparison.Ordinal), "diagnostics should reflow instead of forcing a 900-DIP overflow surface");
    Require(telemetryGrid.Contains("x:Name=\"TranscriptTelemetryGrid\"", StringComparison.Ordinal), "the telemetry grid should be addressable by the adaptive coordinator");
    Require(applySettingsButton.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), "session Apply should start disabled until a tracked field changes");
    Require(xaml.Contains("x:Name=\"SettingsPendingChangesText\"", StringComparison.Ordinal), "settings should expose an exact pending-change receipt");
    Require(xaml.Contains("x:Name=\"ApplySettingsLabel\"", StringComparison.Ordinal), "settings should update the Apply label with the pending-change count");

    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    Require(source.Contains("PreserveCurrentSessionSettingsDraft();", StringComparison.Ordinal), "same-session refreshes should capture pending Apply-only settings before snapshot rendering");
    Require(source.Contains("ReconcileSessionSettingsAfterSnapshot", StringComparison.Ordinal), "snapshot rendering should restore per-session drafts after updating their persisted baseline");
    Require(source.Contains("Unapplied session changes", StringComparison.Ordinal), "app exit should ask before discarding unapplied session drafts");

    var handlerStart = source.IndexOf("private void MainWindow_SizeChanged", StringComparison.Ordinal);
    var handlerEnd = source.IndexOf("private void ApplyRightRailCollapsed", handlerStart, StringComparison.Ordinal);
    Require(handlerStart >= 0 && handlerEnd > handlerStart, "the adaptive rail size handler should remain implemented");
    var sizeHandler = source[handlerStart..handlerEnd];
    Require(!sizeHandler.Contains("_wpfSettingsStore.Save", StringComparison.Ordinal), "automatic rail collapse should never persist a user preference");
    Require(!sizeHandler.Contains("TranscriptDiagnosticsGrid.Columns", StringComparison.Ordinal), "window resizing should not compete with the dashboard's actual-width column writer");
    Require(sizeHandler.Contains("ApplyTopBarLayout(ShouldStackTopBar", StringComparison.Ordinal), "window resize should reflow top-bar commands at the responsive breakpoint");
}

static void MainWindowLiveAgentsViewportStaysConstrained()
{
    var document = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    var rail = document.Root ?? throw new InvalidOperationException("the navigation rail XAML should have a root element");
    Require((string?)rail.Attribute("MinWidth") == "184", "the reusable navigation rail should align with the compact 184-DIP shell width");

    var sessionOverviewPanel = document
        .Descendants()
        .SingleOrDefault(element =>
            element.Name.LocalName == "Border"
            && string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "ArenaSessionOverviewPanelElement", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("the session overview shell should remain present in the navigation rail");
    var sessionDetails = sessionOverviewPanel
        .Descendants()
        .SingleOrDefault(element =>
            element.Name.LocalName == "Expander"
            && string.Equals((string?)element.Attribute("Header"), "Session details", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("session metrics should be grouped under the Session details disclosure");
    Require((string?)sessionDetails.Attribute("IsExpanded") == "False", "Session details should start collapsed so Live Agents remains the primary rail content");
    Require((string?)sessionDetails.Attribute("AutomationProperties.Name") == "Session details", "the session metric disclosure should expose an accessible name");
    Require(!string.IsNullOrWhiteSpace((string?)sessionDetails.Attribute("AutomationProperties.HelpText")), "the session metric disclosure should describe its outcome to automation clients");
    var metricNames = sessionDetails
        .Descendants()
        .Select(element => (string?)element.Attribute(xamlNamespace + "Name"))
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .ToHashSet(StringComparer.Ordinal);
    Require(
        new[]
        {
            "SessionOverviewMatchTextElement",
            "SessionOverviewTurnsTextElement",
            "SessionOverviewParticipantsTextElement",
            "SessionOverviewTokensTextElement",
            "SessionOverviewProviderTextElement",
            "SessionOverviewContextTextElement"
        }.All(metricNames.Contains),
        "collapsing Session details should preserve every named session metric binding");

    var liveAgentsPanel = document
        .Descendants()
        .SingleOrDefault(element =>
            element.Name.LocalName == "Border"
            && string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "ArenaLiveAgentsPanelElement", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("the Live Agents panel should remain present in the shell navigation rail");
    var viewportGrid = liveAgentsPanel
        .Elements()
        .SingleOrDefault(element => element.Name.LocalName == "Grid")
        ?? throw new InvalidOperationException("the Live Agents panel should use a finite grid viewport instead of an unconstrained stack");
    var rowDefinitions = viewportGrid
        .Elements()
        .SingleOrDefault(element => element.Name.LocalName == "Grid.RowDefinitions")?
        .Elements()
        .Where(element => element.Name.LocalName == "RowDefinition")
        .ToArray()
        ?? [];

    Require(rowDefinitions.Length == 2, "the Live Agents viewport should define heading and scrolling rows");
    Require((string?)rowDefinitions[0].Attribute("Height") == "Auto", "the Live Agents heading should size to its content");
    Require((string?)rowDefinitions[1].Attribute("Height") == "*", "the Live Agents list should receive the remaining finite height");

    var scrollViewer = viewportGrid
        .Elements()
        .SingleOrDefault(element =>
            element.Name.LocalName == "ScrollViewer"
            && string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "AgentItemsScrollViewerElement", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("the Live Agents list should expose its constrained scroll viewport");
    Require((string?)scrollViewer.Attribute("Grid.Row") == "1", "the Live Agents scroll viewport should occupy the finite star row");
    Require((string?)scrollViewer.Attribute("VerticalAlignment") == "Stretch", "the Live Agents scroll viewport should stretch to the available row height");
    Require((string?)scrollViewer.Attribute("VerticalScrollBarVisibility") == "Auto", "the Live Agents list should reveal a scrollbar when its cards overflow");
    Require(scrollViewer.Attribute("MaxHeight") is null, "the Live Agents scroll viewport should not measure against a detached fixed-height cap");
    Require(
        scrollViewer.Elements().Any(element =>
            element.Name.LocalName == "StackPanel"
            && string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "AgentItemsElement", StringComparison.Ordinal)),
        "the agent card host should remain inside the constrained scroll viewport");
}

static void MainWindowEmptyExportStatusReleasesToolbarSpace()
{
    var document = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    var exportStatus = document
        .Descendants()
        .SingleOrDefault(element =>
            element.Name.LocalName == "TextBlock"
            && string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "ExportStatusText", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("the export status should remain present in the top command bar");

    Require(exportStatus.Attribute("Width") is null, "an empty export status must not reserve a fixed toolbar width");
    Require((string?)exportStatus.Attribute("MaxWidth") == "118", "populated export status text should remain bounded without reserving empty space");

    var style = exportStatus
        .Elements()
        .SelectMany(element => element.Elements())
        .SingleOrDefault(element => element.Name.LocalName == "Style")
        ?? throw new InvalidOperationException("the export status should define visibility behavior for its empty state");
    Require(
        style.Elements().Any(element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Visibility"
            && (string?)element.Attribute("Value") == "Visible"),
        "populated export status text should remain visible");

    var emptyTextTrigger = style
        .Descendants()
        .SingleOrDefault(element =>
            element.Name.LocalName == "Trigger"
            && (string?)element.Attribute("Property") == "Text"
            && (string?)element.Attribute("Value") == "")
        ?? throw new InvalidOperationException("empty export status text should have an explicit collapse trigger");
    Require(
        emptyTextTrigger.Elements().Any(element =>
            element.Name.LocalName == "Setter"
            && (string?)element.Attribute("Property") == "Visibility"
            && (string?)element.Attribute("Value") == "Collapsed"),
        "empty export status text should collapse out of toolbar measurement");
}

static void MainWindowRightRailCollapsePreservesKeyboardContext()
{
    var source = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
    var methodStart = source.IndexOf("private void ApplyRightRailCollapsed()", StringComparison.Ordinal);
    var methodEnd = source.IndexOf("internal static bool ShouldAutoCollapseRightRail", methodStart, StringComparison.Ordinal);
    Require(methodStart >= 0 && methodEnd > methodStart, "the right-rail layout method should remain discoverable");
    var method = source[methodStart..methodEnd];

    var focusCapture = method.IndexOf("RightRailScrollViewer.IsKeyboardFocusWithin", StringComparison.Ordinal);
    var visibilityChange = method.IndexOf("RightRailScrollViewer.Visibility =", StringComparison.Ordinal);
    var focusHandoff = method.IndexOf("Keyboard.Focus(RightRailToggleButton)", StringComparison.Ordinal);
    Require(focusCapture >= 0, "right-rail collapse should detect keyboard focus within the rail");
    Require(visibilityChange > focusCapture, "right-rail focus state must be captured before the rail is collapsed");
    Require(focusHandoff > visibilityChange, "right-rail collapse should hand focus off only after hiding the focused subtree");
    Require(method.Contains("collapsed && RightRailScrollViewer.IsKeyboardFocusWithin", StringComparison.Ordinal), "focus handoff should run only for an effective collapse with focus inside the rail");
    Require(method.Contains("RightRailScrollViewer.Visibility == Visibility.Collapsed", StringComparison.Ordinal), "the deferred focus handoff should verify that the rail is still collapsed");
    Require(method.Contains("RightRailToggleButton.IsVisible", StringComparison.Ordinal), "the focus handoff should require a visible toggle target");
    Require(method.Contains("RightRailToggleButton.IsEnabled", StringComparison.Ordinal), "the focus handoff should require an enabled toggle target");
    Require(method.Contains("Dispatcher.BeginInvoke", StringComparison.Ordinal), "right-rail focus should move after WPF completes the visibility transition");
    Require(method.Contains("DispatcherPriority.Input", StringComparison.Ordinal), "right-rail focus restoration should run at input priority");

    var collapseMethod = CSharpMethodBlock(source, "private void ApplyRightRailCollapsed(double windowWidth)");
    Require(collapseMethod.Contains("ShouldOverlayRightRail(_rightRailAutoCollapseActive, collapsed)", StringComparison.Ordinal), "right-rail collapse should distinguish a narrow temporary reveal from a docked rail");
    Require(collapseMethod.Contains("RightRailColumn.Width = collapsed || overlay", StringComparison.Ordinal), "a narrow overlay rail should not reserve a blank fixed-width layout column");
    Require(collapseMethod.Contains("ApplyRightRailPresentation(overlay, windowWidth)", StringComparison.Ordinal), "right-rail collapse should apply a width-aware docked or overlay presentation");
}

static void MainWindowRightRailAdaptsWithoutChangingPreferences()
{
    Require(MainWindow.ResolveNavigationRailWidth(MainWindow.SupportedMinimumWindowWidth) == MainWindow.NavigationRailCompactWidth, "minimum-width windows should reclaim center workspace while preserving readable navigation labels");
    Require(MainWindow.ResolveNavigationRailWidth(1100) > MainWindow.NavigationRailCompactWidth, "navigation width should grow continuously above the supported minimum");
    Require(MainWindow.ResolveNavigationRailWidth(1100) < MainWindow.NavigationRailStandardWidth, "compact windows should preserve center workspace without a breakpoint jump");
    Require(MainWindow.ResolveNavigationRailWidth(1500) == MainWindow.NavigationRailStandardWidth, "the default window should keep the compact standard navigation rail");
    Require(MainWindow.ResolveNavigationRailWidth(1920) == MainWindow.NavigationRailComfortableWidth, "very wide windows may restore the comfortable live-agent rail");
    Require(MainWindow.ResolveNavigationRailWidth(double.NaN) == MainWindow.NavigationRailStandardWidth, "invalid navigation measurements should fail safe to the standard rail width");

    Require(MainWindow.ResolveRightRailDockWidth(MainWindow.SupportedMinimumWindowWidth) == MainWindow.RightRailCompactWidth, "minimum-width windows should use the compact docked support rail");
    Require(MainWindow.ResolveRightRailDockWidth(1100) > MainWindow.RightRailCompactWidth, "right rail should grow continuously with available width");
    Require(MainWindow.ResolveRightRailDockWidth(MainWindow.RightRailAutoCollapseWidth) < 320, "the first docked layout should keep a compact support rail instead of jumping directly to full width");
    Require(MainWindow.ResolveRightRailDockWidth(MainWindow.RightRailFullWidthMinWindowWidth) == 320, "comfortable windows should restore the compact full support rail width");
    Require(MainWindow.ResolveArenaControlColumns(MainWindow.RightRailCompactWidth) == 2, "compact right rails should use two action columns so labels remain readable");
    Require(MainWindow.ResolveArenaControlColumns(380) == 3, "full right rails should retain the efficient three-column action layout");
    Require(TranscriptViewCoordinator.ResolveDashboardLayout(754, "diagnostics").DiagnosticsColumns == 2, "minimum-width diagnostics should reflow to two columns from actual available width");
    Require(TranscriptViewCoordinator.ResolveDashboardLayout(810, "diagnostics").DiagnosticsColumns == 3, "medium-width diagnostics should reflow to three columns from actual available width");
    var defaultDashboard = TranscriptViewCoordinator.ResolveDashboardLayout(950, "diagnostics");
    Require(defaultDashboard.DiagnosticsColumns == 3 && defaultDashboard.IsStacked, "default-width diagnostics should stack into three columns instead of competing with the filter rail");
    Require(TranscriptViewCoordinator.ResolveDashboardLayout(TranscriptViewCoordinator.WideDashboardMinWidth - 1, "diagnostics").DiagnosticsColumns == 3, "the dashboard should stay stacked until diagnostics and the full inline filter rail can both fit");
    Require(TranscriptViewCoordinator.ResolveDashboardLayout(1500, "diagnostics").DiagnosticsColumns == 6, "ultrawide dashboards should restore six inline diagnostic columns");

    var previousCenterWidth = MainWindow.ResolveExpandedCenterWorkspaceWidth(MainWindow.SupportedMinimumWindowWidth);
    for (var width = MainWindow.SupportedMinimumWindowWidth + 1; width <= 2400; width++)
    {
        var centerWidth = MainWindow.ResolveExpandedCenterWorkspaceWidth(width);
        Require(centerWidth + 0.001 >= previousCenterWidth, $"center workspace must not shrink when outer width grows ({width - 1} to {width})");
        previousCenterWidth = centerWidth;
    }

    Require(!MainWindow.ShouldStackTopBar(1500), "the default window should keep the top bar inline");
    Require(MainWindow.ShouldStackTopBar(MainWindow.SupportedMinimumWindowWidth), "the minimum supported window width should stack commands below status metrics");
    Require(MainWindow.ShouldStackTopBar(MainWindow.TopBarInlineMinWidth - 1), "top-bar commands should remain stacked below the wide breakpoint");
    Require(!MainWindow.ShouldStackTopBar(MainWindow.TopBarInlineMinWidth), "the wide breakpoint should restore the inline top bar");
    Require(MainWindow.ShouldStackTopBar(double.NaN), "invalid layout widths should fail safe to the non-clipping stacked top bar");

    Require(!MainWindow.ShouldAutoCollapseRightRail(1500), "the default window should keep the right rail expanded");
    Require(!MainWindow.ShouldAutoCollapseRightRail(MainWindow.RightRailAutoCollapseWidth), "the auto-collapse breakpoint should remain inclusive on the expanded side");
    Require(MainWindow.ShouldAutoCollapseRightRail(MainWindow.RightRailAutoCollapseWidth - 1), "narrow windows should auto-collapse the right rail");
    Require(!MainWindow.ShouldAutoCollapseRightRail(double.NaN), "invalid layout widths should not activate auto-collapse");

    Require(MainWindow.IsRightRailEffectivelyCollapsed(userCollapsed: false, autoCollapseActive: true, narrowRevealRequested: false), "auto-collapse should hide a rail with no temporary reveal");
    Require(!MainWindow.IsRightRailEffectivelyCollapsed(userCollapsed: false, autoCollapseActive: true, narrowRevealRequested: true), "a narrow-window reveal should temporarily show the rail");
    Require(MainWindow.IsRightRailEffectivelyCollapsed(userCollapsed: true, autoCollapseActive: true, narrowRevealRequested: true), "an explicit collapsed preference should override temporary reveal state");
    Require(!MainWindow.IsRightRailEffectivelyCollapsed(userCollapsed: false, autoCollapseActive: false, narrowRevealRequested: false), "a wide window should honor the expanded preference");
    Require(MainWindow.IsRightRailEffectivelyCollapsed(userCollapsed: false, autoCollapseActive: false, narrowRevealRequested: false, widthCollapseLatched: true), "a rail collapsed by a narrow resize should stay collapsed when the window widens until the user reveals it");

    Require(MainWindow.ShouldOverlayRightRail(autoCollapseActive: true, collapsed: false), "a temporarily revealed narrow right rail should overlay the center workspace");
    Require(!MainWindow.ShouldOverlayRightRail(autoCollapseActive: true, collapsed: true), "a collapsed narrow right rail has nothing to overlay");
    Require(!MainWindow.ShouldOverlayRightRail(autoCollapseActive: false, collapsed: false), "an expanded wide right rail should remain docked");
    Require(!MainWindow.ShouldOverlayRightRail(autoCollapseActive: false, collapsed: true), "a user-collapsed wide right rail has nothing to overlay");
}

static void MainWindowSnapshotRefreshSkipsUnchangedSessionScans()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-refresh-stamp", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var snapshotPath = Path.Combine(root, "snapshot.json");
        File.WriteAllText(snapshotPath, "{}");
        var observed = MainWindow.TryGetSessionDirectoryLastModified(snapshotPath);

        Require(observed is not null, "an existing session directory should expose its refresh stamp");
        var observedValue = observed.GetValueOrDefault();
        Require(!MainWindow.SnapshotRefreshRequiresSessionScan(observedValue, observed), "an unchanged session directory should skip the expensive summary scan");
        Require(MainWindow.SnapshotRefreshRequiresSessionScan(observedValue.AddSeconds(-1), observed), "a changed session directory should trigger a summary refresh");
        Require(MainWindow.SnapshotRefreshRequiresSessionScan(observedValue, null), "a missing session directory should trigger recovery through the session list");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void MainWindowInternetSettingsUseOneDirectToggle()
{
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));

    Require(xaml.Contains("x:Name=\"UseInternetCheckBox\"", StringComparison.Ordinal), "internet settings should keep the direct enable toggle");
    Require(xaml.Contains("x:Name=\"InternetBackendStatusText\"", StringComparison.Ordinal), "internet settings should keep backend health visibility");
    Require(xaml.Contains("x:Name=\"TestInternetButton\"", StringComparison.Ordinal), "internet settings should expose a standalone internet diagnostic action");
    Require(xaml.Contains("x:Name=\"InternetDiagnosticResultText\"", StringComparison.Ordinal), "internet settings should expose diagnostic results");
    Require(xaml.Contains("does not require an active arena session", StringComparison.OrdinalIgnoreCase), "internet diagnostics should explain their session independence");
    Require(!xaml.Contains("InternetModePicker", StringComparison.Ordinal), "internet settings should not retain a hidden legacy mode picker");
    Require(!xaml.Contains("InternetSourceScopePicker", StringComparison.Ordinal), "internet settings should not retain a hidden legacy source-scope picker");
    Require(!xaml.Contains("CurateNewsButton", StringComparison.Ordinal), "arena controls should not expose a dedicated curator action");
    Require(!xaml.Contains("NewsPanel", StringComparison.Ordinal), "the shell should not retain a dedicated news panel");
}

static void MainWindowModelProviderUsesProgressiveDisclosure()
{
    var document = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    XElement Named(string name) => document
        .Descendants()
        .SingleOrDefault(element => string.Equals((string?)element.Attribute(xamlNamespace + "Name"), name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"MainWindow XAML should contain {name}.");

    var provider = Named("ModelProviderSettingsExpander");
    Require((string?)provider.Attribute("Header") == "Models & provider", "the provider section should use a task-oriented heading");
    Require((string?)provider.Attribute("IsExpanded") == "False", "the provider section should default collapsed");

    var subsectionNames = new[]
    {
        "ProviderSavedSetupsExpander",
        "ProviderCustomConnectionExpander",
        "ProviderRoleRoutingExpander",
        "ProviderRecommendationsExpander",
        "ProviderLocalModelToolsExpander",
        "ProviderAdvancedCallsExpander"
    };
    foreach (var subsectionName in subsectionNames)
    {
        var subsection = Named(subsectionName);
        Require(subsection.Ancestors().Contains(provider), $"{subsectionName} should stay inside Models & provider");
        Require((string?)subsection.Attribute("IsExpanded") == "False", $"{subsectionName} should default collapsed");
        Require((string?)subsection.Attribute("Style") == "{StaticResource SettingsSubsectionExpander}", $"{subsectionName} should use the compact subsection style");
    }

    bool IsInsideOptionalSubsection(XElement element) => element
        .Ancestors()
        .Any(ancestor => subsectionNames.Contains((string?)ancestor.Attribute(xamlNamespace + "Name"), StringComparer.Ordinal));

    foreach (var essentialName in new[] { "ProviderPresetPicker", "ProviderModelText", "TestProviderButton" })
    {
        var essential = Named(essentialName);
        Require(essential.Ancestors().Contains(provider), $"{essentialName} should stay in Models & provider");
        Require(!IsInsideOptionalSubsection(essential), $"{essentialName} should remain on the short primary setup path");
    }

    var expectedGroups = new Dictionary<string, string>
    {
        ["ProviderProfilePicker"] = "ProviderSavedSetupsExpander",
        ["ProviderApiModePicker"] = "ProviderCustomConnectionExpander",
        ["AlphaRoleModelText"] = "ProviderRoleRoutingExpander",
        ["TestAllRolesButton"] = "ProviderRoleRoutingExpander",
        ["AutoConfigureButton"] = "ProviderRecommendationsExpander",
        ["DownloadModelText"] = "ProviderLocalModelToolsExpander",
        ["ProviderTimeoutText"] = "ProviderAdvancedCallsExpander"
    };
    foreach (var (controlName, groupName) in expectedGroups)
    {
        Require(Named(controlName).Ancestors().Contains(Named(groupName)), $"{controlName} should stay inside {groupName}");
    }

    Require(!document.Descendants().Any(element => string.Equals((string?)element.Attribute(xamlNamespace + "Name"), "ActiveParticipantsPicker", StringComparison.Ordinal)), "Settings should not duplicate the Match Setup participant picker");
    Require(Named("StreamModelResponsesCheckBox").Ancestors().Contains(Named("AgentSettingsExpander")), "Agent streaming should live with Agent workspace settings");
    Require(Named("UseDefaultModelForAllRolesButton").Descendants().Any(element => (string?)element.Attribute("Text") == "Use default for every role"), "role inheritance should be described as following the default model");
    var providerSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs"));
    var inheritStart = providerSource.IndexOf("public async Task UseDefaultModelForAllRolesAsync", StringComparison.Ordinal);
    var inheritEnd = providerSource.IndexOf("public void SaveRoleModelDrafts", inheritStart, StringComparison.Ordinal);
    Require(inheritStart >= 0 && inheritEnd > inheritStart, "the role-inheritance action should remain implemented");
    var inheritMethod = providerSource[inheritStart..inheritEnd];
    Require(inheritMethod.Contains("SetRoleModelText(key, \"\")", StringComparison.Ordinal), "using the default model should clear explicit role-model overrides");
    Require(!inheritMethod.Contains("SetRoleModelText(key, model)", StringComparison.Ordinal), "using the default model should not copy a value that later stops inheriting");

    var advanced = Named("ProviderAdvancedCallsExpander");
    foreach (var grid in advanced.Descendants().Where(element => element.Name.LocalName == "Grid"))
    {
        var editableFields = grid
            .Descendants()
            .Where(element => element.Name.LocalName is "TextBox" or "ComboBox" or "PasswordBox")
            .Count(element => element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "Grid") == grid);
        Require(editableFields <= 2, "advanced model-call rows should not pack more than two editable fields into the 520px drawer");
    }

    var downloadFieldGrid = Named("DownloadModelText").Ancestors().First(element => element.Name.LocalName == "Grid");
    var downloadActionGrid = Named("DownloadModelButton").Ancestors().First(element => element.Name.LocalName == "Grid");
    Require(downloadFieldGrid != downloadActionGrid, "model download fields and actions should use separate rows");

    var subsectionStyle = document
        .Descendants()
        .Single(element => element.Name.LocalName == "Style" && (string?)element.Attribute(xamlNamespace + "Key") == "SettingsSubsectionExpander");
    var subsectionToggle = subsectionStyle
        .Descendants()
        .Single(element => element.Name.LocalName == "ToggleButton" && (string?)element.Attribute(xamlNamespace + "Name") == "SubsectionHeader");
    Require(subsectionToggle.Attribute("AutomationProperties.Name") is not null, "subsection headers should expose their labels to UI Automation");
}

static void SettingsSearchExpandsNestedDisclosuresAndRestoresState()
{
    RunStaTest(() =>
    {
    var reasoning = new Expander
    {
        Header = "Advanced model calls",
        Content = new TextBlock { Text = "Reasoning level" },
        IsExpanded = false
    };
    var downloads = new Expander
    {
        Header = "Local model tools",
        Content = new TextBlock { Text = "Download model" },
        IsExpanded = true
    };
    var root = new StackPanel();
    root.Children.Add(reasoning);
    root.Children.Add(downloads);

    Require(MainWindow.SettingsNodeMatches(reasoning, "REASONING"), "settings search should match nested text case-insensitively");
    Require(!MainWindow.SettingsNodeMatches(reasoning, "download"), "settings search should reject unrelated nested text");

    var expanders = new List<Expander>();
    MainWindow.CollectSettingsExpanders(root, expanders);
    Require(expanders.SequenceEqual(new[] { reasoning, downloads }), "settings search should discover nested disclosure controls in visual order");
    var priorExpansion = expanders.ToDictionary(expander => expander, expander => expander.IsExpanded);

    MainWindow.ApplyNestedSettingsSearch(root, "reasoning");
    Require(reasoning.Visibility == Visibility.Visible && reasoning.IsExpanded, "a matching nested subsection should be shown and expanded");
    Require(downloads.Visibility == Visibility.Collapsed && !downloads.IsExpanded, "a nonmatching nested subsection should be hidden during search");

    MainWindow.RestoreSettingsExpansion(expanders, priorExpansion);
    Require(reasoning.Visibility == Visibility.Visible && !reasoning.IsExpanded, "clearing search should restore a previously collapsed subsection");
    Require(downloads.Visibility == Visibility.Visible && downloads.IsExpanded, "clearing search should restore a previously expanded subsection");
    var xaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    Require(xaml.Contains("x:Name=\"SettingsSearchFeedbackText\"", StringComparison.Ordinal)
        && xaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "settings search should announce its visible result count or empty state");
    Require(xaml.Contains("x:Name=\"SettingsSearchClearButton\"", StringComparison.Ordinal)
        && xaml.Contains("Click=\"SettingsSearchClearButton_Click\"", StringComparison.Ordinal), "settings search feedback should include a direct clear action");
    });
}

}
