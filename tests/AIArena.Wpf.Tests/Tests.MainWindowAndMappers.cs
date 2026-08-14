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
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
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

    var providerSettingsSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs");
    Require(
        providerSettingsSource.Contains("Func<AIArena.Core.Models.ArenaSnapshot, string, CancellationToken, Task>", StringComparison.Ordinal)
        && providerSettingsSource.Contains("Func<string, CancellationToken, Task> refreshActiveSessionAsync", StringComparison.Ordinal)
        && providerSettingsSource.Contains("Func<bool, CancellationToken, Task> refreshProviderReachabilityAsync", StringComparison.Ordinal)
        && providerSettingsSource.Contains("saveSnapshotWithFeedbackAsync(snapshot, session.Id, cancellationToken)", StringComparison.Ordinal)
        && providerSettingsSource.Contains("refreshProviderReachabilityAsync(true, cancellationToken)", StringComparison.Ordinal)
        && providerSettingsSource.Contains("providerRuntime.TestAsync(session.Id, allRoles: false, cancellationToken)", StringComparison.Ordinal),
        "provider persistence and reachability delegates should preserve tracked cancellation through their final stages");

    var sessionMutationSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ArenaSessionMutationCoordinator.cs");
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
    ModelRuntimeSettingsRegistry.Register(
        snapshot,
        snapshot.Configs["shared"],
        32768,
        ModelHistoryPolicies.Rolling80,
        ModelResponseTones.Concise,
        "");
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
            ["provider_response_id"] = JsonSerializer.SerializeToElement("resp_native"),
            ["completion_failure_kind"] = JsonSerializer.SerializeToElement("context_limit_exceeded"),
            ["completion_stop_reason"] = JsonSerializer.SerializeToElement("provider_error"),
            ["provider_status_code"] = JsonSerializer.SerializeToElement(400),
            ["provider_error_code"] = JsonSerializer.SerializeToElement("context_length_exceeded"),
            ["arena_history_budget_receipt"] = JsonSerializer.SerializeToElement(new
            {
                contract = "arena_history_budget_v1",
                history_policy = "rolling_80",
                configured_context_window = 32768,
                target_percent = 80,
                input_token_budget = 24000,
                output_token_reserve = 4096,
                estimated_prompt_tokens = 23800,
                eligible_entry_count = 52,
                included_entry_count = 31,
                omitted_entry_count = 21,
                included_message_ids = new[] { "private-id-must-not-project" },
                context_fingerprint = new string('a', 64),
                before_turn = 7,
                token_evidence = "estimated_v1"
            })
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
    Require(message.CompletionFailureKind == "context_limit_exceeded"
            && message.CompletionStopReason == "provider_error"
            && message.ProviderStatusCode == 400
            && message.ProviderErrorCode == "context_length_exceeded",
        "rendered transcript should project structured privacy-safe provider completion failure evidence");
    Require(message.HistoryBudgetReceipt is
        {
            Contract: "arena_history_budget_v1",
            HistoryPolicy: "rolling_80",
            ConfiguredContextWindow: 32768,
            IncludedEntryCount: 31,
            OmittedEntryCount: 21,
            BeforeTurn: 7,
            TokenEvidence: "estimated_v1"
        }
        && message.HistoryBudgetReceipt.ContextFingerprint == new string('a', 64),
        "rendered transcript should project the causal bounded history receipt without exposing included message ids");
    Require(alpha.Model == "shared-model", "whitespace agent model override should fall back to shared model");
    Require(worldAlpha.Model == "shared-model", "world avatars should inherit the shared model when an agent override is blank");
    Require(rendered.ProviderApiToken == "secret-token", "rendered snapshot should preserve provider API token for settings UI");
    Require(!rendered.ProviderNativeStatefulChat, "rendered snapshot should preserve native stateful chat setting");
    Require(rendered.ProviderNativeIdleTtlSeconds == 1200, "rendered snapshot should preserve native idle TTL setting");
    Require(rendered.ProviderConfiguredContextWindow == 32768
            && rendered.ProviderHistoryPolicy == ModelHistoryPolicies.Rolling80
            && rendered.ProviderResponseTone == ModelResponseTones.Concise
            && rendered.ModelSettings.Count == 1,
        "rendered snapshot should project canonical per-model behavior settings without transient residency fields");

    snapshot.Engine.MatchEnded = true;
    snapshot.Engine.MatchEndReason = "operator ended after context limit";
    var endedProjection = SnapshotViewMapper.FromCore(session, snapshot);
    Require(endedProjection.MatchEnded
            && endedProjection.MatchEndReason == "operator ended after context limit",
        "snapshot mapping should project the durable privacy-safe End Match state for readiness gating");
    Require(rendered.InternetEnabled, "rendered snapshot should preserve the direct internet setting");
    Require(rendered.GenerationHistory.Count == 2, "rendered history should skip invalid blank-id entries");
    Require(history.Id == "history-newer", "rendered history should show newest entries first");
    Require(history.Global == "Newer global rule", "rendered history should preserve global instruction");
    Require(history.NarratorBrief == "Newer narrator brief", "rendered history should preserve narrator brief");
    Require(history.PersonaCount == 1, "rendered history should count participant personas only");
    Require(history.PersonaPreview.Contains("alpha: Chair", StringComparison.OrdinalIgnoreCase), "rendered history should include persona preview");

    var typedFailureSnapshot = SessionStore.CreateDefaultSnapshot();
    var typedFailureKinds = new[]
    {
        "empty_public_content",
        "native_state_exhausted",
        "provider_loading"
    };
    for (var index = 0; index < typedFailureKinds.Length; index++)
    {
        typedFailureSnapshot.Engine.Messages.Add(new DialogueMessage
        {
            Turn = index + 1,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Kind = "error",
            Status = "error",
            Text = "Typed provider outcome.",
            Metadata = new Dictionary<string, JsonElement>
            {
                ["completion_failure_kind"] = JsonSerializer.SerializeToElement(typedFailureKinds[index])
            }
        });
    }
    var projectedFailureKinds = SnapshotViewMapper.FromCore(session, typedFailureSnapshot).Messages
        .Select(item => item.CompletionFailureKind)
        .ToArray();
    Require(projectedFailureKinds.SequenceEqual(typedFailureKinds),
        "snapshot mapping should retain empty-content, native-state exhaustion, and provider-loading outcomes");

    typedFailureSnapshot.Engine.Messages[0].Metadata["provider_error_code"] =
        JsonSerializer.SerializeToElement("sk-proj-1234567890abcdefghijklmnopqrstuvwxyz");
    var redactedProviderCode = SnapshotViewMapper.FromCore(session, typedFailureSnapshot).Messages[0].ProviderErrorCode;
    Require(redactedProviderCode.Length == 0,
        "snapshot mapping should reject token-shaped provider error metadata even when it is otherwise code-shaped");

    var groupSnapshot = SessionStore.CreateDefaultSnapshot();
    groupSnapshot.Engine.FactoryMode = true;
    groupSnapshot.Engine.Agents.Clear();
    groupSnapshot.Engine.Agents.Add(new DialogueAgent { Id = "alpha", Name = "Alpha", Active = true });
    groupSnapshot.Engine.Messages.Add(new TranscriptService().CreateOperatorMessage("Start group mapper fixture.", 1));
    groupSnapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 2,
        Speaker = "Alpha",
        SpeakerId = "alpha",
        Kind = "message",
        Status = "ok",
        Text = "Visible participant reply."
    });
    var factoryConversation = new FactoryConversationService();
    factoryConversation.Resolve(groupSnapshot);
    var renderedGroup = SnapshotViewMapper.FromCore(session, groupSnapshot);
    Require(renderedGroup.HasFactoryConversationRoot && renderedGroup.FactoryConversationRootAssigned, "snapshot mapping should expose a valid durable Factory root separately from the mode toggle");
    Require(renderedGroup.FactoryConversationEntryCount == 2 && renderedGroup.FactoryConversationOmittedCount == 0, "snapshot mapping should expose privacy-safe shared-group counts");

    groupSnapshot.Engine.Messages.RemoveAt(0);
    var renderedOrphan = SnapshotViewMapper.FromCore(session, groupSnapshot);
    Require(renderedOrphan.FactoryConversationRootAssigned && !renderedOrphan.HasFactoryConversationRoot, "snapshot mapping should preserve an orphan marker so WPF readiness cannot promote a later row to root");
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
    Require(setupSpec.RootElement.GetProperty("provider").GetProperty("defaultForUnassignedAgentsEnabled").GetBoolean()
            && setupSpec.RootElement.GetProperty("cast").EnumerateArray().All(item =>
                item.GetProperty("assignmentMode").GetString() == "inherit"),
        "current setup spec should disclose enabled Default inheritance");

    var explicitAlpha = snapshot with
    {
        DefaultForUnassignedAgentsEnabled = false,
        ExplicitRoleModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = snapshot.ProviderModel
        }
    };
    using var explicitSpec = JsonDocument.Parse(CustomMatchSummaryCoordinator.CurrentSetupSpec(explicitAlpha));
    var explicitCast = explicitSpec.RootElement.GetProperty("cast").EnumerateArray().ToArray();
    Require(!explicitSpec.RootElement.GetProperty("provider").GetProperty("defaultForUnassignedAgentsEnabled").GetBoolean()
            && explicitCast.Single(item => item.GetProperty("id").GetString() == "alpha").GetProperty("assignmentMode").GetString() == "explicit"
            && explicitCast.Single(item => item.GetProperty("id").GetString() == "beta").GetProperty("assignmentMode").GetString() == "unassigned"
            && CustomMatchSummaryCoordinator.CurrentSetupBrief(explicitAlpha).Contains("Default off", StringComparison.Ordinal),
        "current setup summary should distinguish explicit routes from unassigned roles when Default is off");

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

static void FactoryModeSetupPresentationDisclosesInactiveArenaBehavior()
{
    var publicOperatorTurn = TranscriptForTest(1, "Operator", "operator", "message", "ok") with
    {
        Text = "Return the raw completion for this debugging prompt."
    };
    var snapshot = SnapshotForOverviewTest(
        providerOnline: true,
        providerModel: "local-model",
        providerLastError: "",
        turnIndex: 0,
        messages: [publicOperatorTurn],
        agents:
        [
            new AgentState("alpha", "Alpha", "waiting", "", "default", "", "", "local-model", true, false, [])
        ]) with
    {
        FactoryMode = true,
        RivalryMatrixEnabled = true,
        RivalryMatrix = []
    };

    var report = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(snapshot);
    Require(report.Blockers.Count == 0, "one active agent, a selected model, and public Operator input should satisfy Factory run prerequisites");
    Require(report.Warnings.Count == 0, "blank Arena-only guidance should not become a Factory readiness warning");
    Require(report.Status.StartsWith("Ready: Factory mode, 1 active agent(s)", StringComparison.Ordinal), "Factory readiness should disclose its one-agent run shape");
    var badges = report.Badges.ToDictionary(badge => badge.Label, StringComparer.Ordinal);
    Require(badges["Mode"].Value == "Factory", "readiness should expose the active model-behavior mode without relying on colour");
    Require(badges["Agents"].Value == "1" && badges["Agents"].Kind == "ready", "one participant should be explicitly ready in Factory mode");
    Require(badges["Input"].Value == "Root ready", "readiness should distinguish a pending public Operator root from an anchored group");
    foreach (var facet in new[] { "Personas", "Criteria", "Matrix" })
    {
        Require(badges[facet].Value == "Inactive", $"the {facet} Arena-behavior facet should be labelled inactive in Factory mode");
        Require(badges[facet].Kind == "neutral", $"the inactive {facet} facet should not be styled as success, warning, or failure");
        Require(badges[facet].Tooltip.Contains("not", StringComparison.OrdinalIgnoreCase), $"the inactive {facet} badge should explain that its saved behavior is not applied");
    }

    Require(badges["Narrator"].Value == "Unavailable" && badges["Narrator"].Kind == "neutral", "Factory readiness should label narration unavailable without treating it as a failed setup");
    Require(badges["Narrator"].Tooltip.Contains("Factory mode", StringComparison.Ordinal), "the narrator badge should expose the mode reason");

    var constraints = CustomMatchSummaryCoordinator.RunConstraintText(snapshot);
    Require(constraints.Contains("Factory mode", StringComparison.Ordinal) && constraints.Contains("public Operator root ready to anchor", StringComparison.Ordinal), "run constraints should disclose Factory mode and its pending-root state");
    Require(constraints.Contains("Match Setup guidance is inactive", StringComparison.Ordinal), "run constraints should not imply that saved Arena guidance shapes Factory calls");

    var brief = CustomMatchSummaryCoordinator.CurrentSetupBrief(snapshot);
    Require(brief.Contains("Model behavior: Factory mode", StringComparison.Ordinal), "the copyable setup brief should disclose Factory mode");
    Require(brief.Contains("attributed public group history", StringComparison.Ordinal), "the copyable setup brief should disclose the Factory group-history boundary");
    Require(brief.Contains("public_group_v1", StringComparison.Ordinal), "the copyable setup brief should identify the deterministic Factory conversation contract");
    Require(brief.Contains("Match Setup saved but inactive", StringComparison.Ordinal), "the setup brief should distinguish saved guidance from applied guidance");

    using var spec = JsonDocument.Parse(CustomMatchSummaryCoordinator.CurrentSetupSpec(snapshot));
    var modelBehavior = spec.RootElement.GetProperty("modelBehavior");
    Require(modelBehavior.GetProperty("mode").GetString() == "factory", "the setup spec should disclose Factory mode as structured data");
    Require(!modelBehavior.GetProperty("applyMatchSetup").GetBoolean(), "the setup spec should explicitly say Match Setup is not applied");
    Require(modelBehavior.GetProperty("input").GetString() == "attributed_public_group_history", "the setup spec should expose the exact Factory input contract");
    Require(modelBehavior.GetProperty("contract").GetString() == "public_group_v1", "the setup spec should expose the versioned Factory conversation contract");
    Require(modelBehavior.GetProperty("rootState").GetString() == "pending", "an eligible unanchored Operator turn should remain visibly pending until the group is anchored");
    Require(!modelBehavior.GetProperty("narrationAvailable").GetBoolean(), "the setup spec should disclose Factory narration unavailability");

    var anchored = snapshot with
    {
        HasFactoryConversationRoot = true,
        FactoryConversationRootAssigned = true,
        FactoryConversationEntryCount = 56,
        FactoryConversationOmittedCount = 6
    };
    var anchoredReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(anchored);
    var anchoredInput = anchoredReport.Badges.Single(badge => badge.Label == "Input");
    var anchoredBrief = CustomMatchSummaryCoordinator.CurrentSetupBrief(anchored);
    Require(anchoredInput.Value == "Group ready" && anchoredInput.Kind == "ready", "an anchored Factory conversation should be exposed as a ready group without relying on colour");
    Require(anchoredInput.Tooltip.Contains("Models receive 50 of 56 eligible attributed public conversation entries", StringComparison.Ordinal), "anchored setup help should distinguish included model context from total eligible entries");
    Require(anchoredInput.Tooltip.Contains("6 older whole entries are omitted", StringComparison.Ordinal), "anchored setup help should truthfully report the fixed context-window omission count");
    Require(anchoredBrief.Contains("50/56 entries included; 6 omitted", StringComparison.Ordinal), "copyable setup summaries must not describe omitted Factory entries as model-visible context");
    using (var anchoredSpec = JsonDocument.Parse(CustomMatchSummaryCoordinator.CurrentSetupSpec(anchored)))
    {
        var anchoredBehavior = anchoredSpec.RootElement.GetProperty("modelBehavior");
        Require(anchoredBehavior.GetProperty("rootState").GetString() == "anchored", "the structured setup spec should distinguish an anchored Factory group");
        Require(anchoredBehavior.GetProperty("contextEntries").GetInt32() == 56 && anchoredBehavior.GetProperty("omittedEntries").GetInt32() == 6, "the setup spec should expose privacy-safe eligible and omission counts");
    }

    var orphaned = snapshot with
    {
        FactoryConversationRootAssigned = true,
        HasFactoryConversationRoot = false,
        FactoryConversationEntryCount = 0
    };
    var orphanedReport = ScenarioWorkflowCoordinator.BuildSetupReadinessReport(orphaned);
    var orphanedInput = orphanedReport.Badges.Single(badge => badge.Label == "Input");
    Require(orphanedReport.Blockers.Any(blocker => blocker.Contains("restore the anchored public Operator root", StringComparison.Ordinal)), "a missing root should block setup instead of promoting the later visible Operator turn");
    Require(orphanedInput.Value == "Root missing" && orphanedInput.Kind == "danger", "a missing Factory root should expose a non-colour error label and danger state");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var controlStyles = ReadWorkspaceFile("src/AIArena.Wpf/UI/Theming/ControlStyles.xaml");
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
    var markup = ReadWorkspaceFile("src/AIArena.Wpf/UI/Theming/ThemeBrushes.xaml");
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
    var window = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    Require(
        !window.Contains("<SolidColorBrush x:Key=\"AppBackgroundBrush\"", StringComparison.Ordinal),
        "MainWindow.xaml should not redeclare themed brushes that ThemeBrushes.xaml owns");
}

static ResourceDictionary LoadDesignTokenDictionary()
{
    var markup = ReadWorkspaceFile("src/AIArena.Wpf/UI/Theming/DesignTokens.xaml");
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

        var appMarkup = ReadWorkspaceFile("src/AIArena.Wpf/App.xaml");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var prompt = XamlElementBlock(xaml, "CollaboratePromptText", "TextBox");

    Require(prompt.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal), "collaborate composer should remain multiline");
    Require(prompt.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "collaborate composer should wrap long prompts");
    Require(prompt.Contains("VerticalContentAlignment=\"Top\"", StringComparison.Ordinal), "collaborate composer should align multiline text to the top");
    Require(prompt.Contains("HorizontalContentAlignment=\"Left\"", StringComparison.Ordinal), "collaborate composer should align multiline text to the left");
}

static void MainWindowCollaboratePromptAssistButtonsStayCompact()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
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
    var windowXaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var topBarXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
    var xaml = windowXaml + Environment.NewLine + topBarXaml;
    var railXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml");
    var code = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
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
    var debugSettingsIndex = xaml.IndexOf("x:Name=\"DebugControlsSettingsExpander\"", StringComparison.Ordinal);
    var controlPlaneToggleIndex = xaml.IndexOf("x:Name=\"ControlPlaneCheckBox\"", StringComparison.Ordinal);
    var internetSettingsIndex = xaml.IndexOf("<Expander Header=\"Internet Access\"", StringComparison.Ordinal);
    var agentSettingsIndex = xaml.IndexOf("x:Name=\"AgentSettingsExpander\"", StringComparison.Ordinal);
    var agentWorkspaceToggleIndex = xaml.IndexOf("x:Name=\"AgentWorkspaceCheckBox\"", StringComparison.Ordinal);
    Require(debugSettingsIndex >= 0
            && controlPlaneToggleIndex > debugSettingsIndex
            && controlPlaneToggleIndex < internetSettingsIndex,
        "control-plane toggle should live inside the Debug controls Settings category");
    Require(!xaml.Contains("<Expander Header=\"PowerShell Control\"", StringComparison.Ordinal), "PowerShell control should not consume a separate top-level Settings category");
    Require(!topBarXaml.Contains("ControlPlaneCheckBox", StringComparison.Ordinal), "control-plane toggle should remain in Settings rather than the transient Debug popup");
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
    Require(code.Contains("ExperimentLabNavButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed", StringComparison.Ordinal), "Experiment Lab navigation should follow the master debug toggle");
    Require(code.Contains("ApplyExperimentLabVisibility();", StringComparison.Ordinal), "MainWindow should apply Experiment Lab visibility after settings changes");
    Require(code.Contains("ApplyAgentWorkspaceVisibility", StringComparison.Ordinal), "MainWindow should apply Agent visibility after settings changes");
    Require(code.Contains("ShellNavigation.ShowAgentPanel();", StringComparison.Ordinal), "Agent nav should call the Agent shell surface");
    Require(code.Contains("AgentWorkspace.RefreshProviderState();", StringComparison.Ordinal), "Agent nav should refresh workspace/provider chrome when opened");

    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings()), "AI World debug should default off");
    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings { ShowWorldDebug = true }), "AI World should remain off without master debug controls");
    Require(!MainWindow.IsWorldDebugEnabled(new WpfSettings { AllowDebugControls = true }), "master debug controls alone should not enable AI World");
    Require(MainWindow.IsWorldDebugEnabled(new WpfSettings { AllowDebugControls = true, ShowWorldDebug = true }), "AI World should enable only when both debug gates are on");
    Require(!MainWindow.IsExperimentLabEnabled(new WpfSettings()), "Experiment Lab should default hidden with Debug controls off");
    Require(MainWindow.IsExperimentLabEnabled(new WpfSettings { AllowDebugControls = true }), "Experiment Lab should appear when Debug controls are enabled");
    Require(MainWindow.IsAgentWorkspaceEnabled(new WpfSettings()), "Agent workspace should be enabled by default");
    Require(MainWindow.IsAgentWorkspaceEnabled(new WpfSettings { AllowDebugControls = false, ShowAgentWorkspace = true }), "Agent workspace should not require Debug controls");
    Require(!MainWindow.IsAgentWorkspaceEnabled(new WpfSettings { AllowDebugControls = true, ShowAgentWorkspace = false }), "an explicit Agent workspace opt-out should hide it even when Debug is enabled");
}

static void ShellCommandStateMapsContextualWorkspaceCommands()
{
    var expected = new Dictionary<ShellSurface, (bool MatchSetup, bool Models, bool Search, bool Export, bool View)>
    {
        [ShellSurface.Lab] = (true, true, true, true, true),
        [ShellSurface.World] = (true, true, false, false, false),
        [ShellSurface.MatchSetup] = (true, true, true, true, true),
        [ShellSurface.Models] = (true, true, true, true, true),
        [ShellSurface.Agent] = (false, false, false, false, false),
        [ShellSurface.Collaborate] = (false, false, true, true, false)
    };

    foreach (var (surface, visibility) in expected)
    {
        var state = ShellCommandState.For(surface);
        Require(state.ShowMatchSetup == visibility.MatchSetup, $"{surface} Match Setup command visibility changed unexpectedly");
        Require(state.ShowModels == visibility.Models, $"{surface} Models command visibility changed unexpectedly");
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
    Require(ShellCommandState.For(ShellSurface.Models) == lab, "Models should preserve the complete Lab command layout while replacing the transcript canvas");
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

    var modelsButton = Named("ModelsButton");
    Require(modelsButton.Name.LocalName == "Button", "Models should be a direct top-rail command beside Match Setup");
    Require(!string.IsNullOrWhiteSpace((string?)modelsButton.Attribute("AutomationProperties.Name")), "Models should expose an automation name");
    Require(((string?)modelsButton.Attribute("AutomationProperties.HelpText"))?.Contains("assignments", StringComparison.OrdinalIgnoreCase) == true, "Models should explain its catalog and assignment destination");

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
    var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");

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

    var modelsSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.Models.cs");
    var showModels = CSharpMethodBlock(modelsSource, "private void ShowProviderModelsPanel()");
    Require(showModels.Contains("ShellNavigation.ShowProviderModelsPanel()", StringComparison.Ordinal), "Models should use the shell surface transition rather than an independent window");
    Require(showModels.Contains("_activeShellSurface = ShellSurface.Models", StringComparison.Ordinal), "Models should publish its contextual shell surface");
    Require(showModels.Contains("ApplyShellCommandState(_activeShellSurface)", StringComparison.Ordinal), "Models should preserve the contextual top rail");
    Require(showModels.Contains("ProviderModelsPanel.FocusCatalog()", StringComparison.Ordinal), "opening Models should move keyboard focus into the real catalog");

    var closeModels = CSharpMethodBlock(modelsSource, "private void CloseProviderModelsPanel()");
    Require(closeModels.Contains("RestoreOverlayFocus(", StringComparison.Ordinal), "closing Models should restore focus to its opener");
    Require(closeModels.Contains("ShowTranscriptPanel(clearFilters: false)", StringComparison.Ordinal), "closing Models should fall back to AI Lab without clearing transcript state");
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

static void ProviderModelsHeartbeatFollowsHostedEffectiveVisibility()
{
    RunStaTest(() =>
    {
        var timer = new ManualProviderModelsHeartbeatTimer(
            ProviderModelsSurfaceCoordinator.HeartbeatInterval);
        var heartbeatCount = 0;
        var observedTokens = new List<CancellationToken>();
        var firstHeartbeatRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task Heartbeat(CancellationToken cancellationToken)
        {
            heartbeatCount++;
            observedTokens.Add(cancellationToken);
            if (heartbeatCount == 1)
            {
                cancellationToken.Register(() =>
                    firstHeartbeatRelease.TrySetCanceled(cancellationToken));
                return firstHeartbeatRelease.Task;
            }

            return Task.CompletedTask;
        }

        using var heartbeat = new ProviderModelsHeartbeatController(timer, Heartbeat);
        var providerModelsPanel = new ProviderModelAssignmentsControl
        {
            Visibility = Visibility.Visible
        };
        AttachArenaPresentationResources(providerModelsPanel);
        var surfaceAncestor = new Grid();
        surfaceAncestor.Children.Add(providerModelsPanel);
        providerModelsPanel.IsVisibleChanged += (_, _) =>
            heartbeat.SetEffectivelyVisible(providerModelsPanel.IsVisible);

        var host = new Window
        {
            Content = surfaceAncestor,
            Width = 1100,
            Height = 700,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Opacity = 0,
            Left = -10000,
            Top = -10000
        };
        host.Closed += (_, _) => heartbeat.Dispose();

        static void DrainDispatcher() =>
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                static () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        static void PumpDispatcherUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(10)
                };
                EventHandler? tick = null;
                tick = (_, _) =>
                {
                    timer.Stop();
                    timer.Tick -= tick;
                    frame.Continue = false;
                };
                timer.Tick += tick;
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
        }

        host.Show();
        try
        {
            DrainDispatcher();
            Require(providerModelsPanel.IsVisible
                    && heartbeat.Interval == TimeSpan.FromSeconds(5)
                    && heartbeat.IsTimerRunning
                    && timer.StartCount == 1
                    && heartbeat.VisibilityGeneration == 1,
                "effective Models visibility did not start generation one on the production five-second interval");

            timer.RaiseTick();
            Require(heartbeatCount == 1
                    && heartbeat.HasInFlightHeartbeat
                    && observedTokens.Count == 1
                    && !observedTokens[0].IsCancellationRequested,
                "the visible hosted Models surface did not start exactly one cancellable heartbeat");

            surfaceAncestor.Visibility = Visibility.Collapsed;
            DrainDispatcher();
            Require(providerModelsPanel.Visibility == Visibility.Visible
                    && !providerModelsPanel.IsVisible
                    && observedTokens[0].IsCancellationRequested
                    && !heartbeat.IsTimerRunning,
                "collapsing a Models ancestor did not cancel its in-flight effective-visibility generation");

            PumpDispatcherUntil(() => !heartbeat.HasInFlightHeartbeat);
            Require(!heartbeat.HasInFlightHeartbeat,
                "the cancelled Models heartbeat did not release its single-flight gate");

            surfaceAncestor.Visibility = Visibility.Visible;
            DrainDispatcher();
            Require(providerModelsPanel.IsVisible
                    && heartbeat.IsTimerRunning
                    && timer.StartCount == 2
                    && heartbeat.VisibilityGeneration == 2,
                "reopening the hosted Models surface did not start a fresh heartbeat generation");

            timer.RaiseTick();
            PumpDispatcherUntil(() => heartbeatCount == 2 && !heartbeat.HasInFlightHeartbeat);
            Require(heartbeatCount == 2
                    && observedTokens.Count == 2
                    && !observedTokens[1].IsCancellationRequested,
                "the reopened Models surface did not admit one heartbeat under a fresh token");

            host.Close();
            DrainDispatcher();
            Require(heartbeat.IsDisposed
                    && timer.IsDisposed
                    && !heartbeat.IsTimerRunning,
                "closing the hosted shell did not dispose its Models heartbeat owner");

            timer.RaiseTick();
            DrainDispatcher();
            Require(heartbeatCount == 2,
                "a timer callback polled Models after the hosted shell closed");
        }
        finally
        {
            if (host.IsVisible)
            {
                host.Close();
            }
        }
    });
}

private sealed class ManualProviderModelsHeartbeatTimer(TimeSpan interval)
    : IProviderModelsHeartbeatTimer
{
    public event EventHandler? Tick;

    public TimeSpan Interval { get; } = interval;

    public bool IsEnabled { get; private set; }

    public bool IsDisposed { get; private set; }

    public int StartCount { get; private set; }

    public void Start()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ManualProviderModelsHeartbeatTimer));
        }

        StartCount++;
        IsEnabled = true;
    }

    public void Stop() => IsEnabled = false;

    public void Dispose()
    {
        IsEnabled = false;
        IsDisposed = true;
    }

    public void RaiseTick() => Tick?.Invoke(this, EventArgs.Empty);
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
    var code = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
    var button = XamlStartTag(xaml, "ExportTranscriptBottomButton", "Button");
    var status = XamlStartTag(xaml, "ExportStatusText", "TextBlock");

    Require(button.Contains("Click=\"TranscriptExportRequested\"", StringComparison.Ordinal), "top export button should route through the reusable control's single interaction contract");
    Require(button.Contains("AutomationProperties.Name=\"Export transcript\"", StringComparison.Ordinal), "top export button should expose a transcript fallback automation name");
    Require(status.Contains("Visibility=\"Collapsed\"", StringComparison.Ordinal)
            && status.Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
        "the migrated export status compatibility target should not occupy or announce from the top bar");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var topBarXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
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
    var aiChoiceDialog = ReadWorkspaceFile("src/AIArena.Wpf/Shell/Dialogs/AiChoicePromptDialog.xaml");
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

static void MainWindowFactoryModeToggleExposesAutomationAndControlState()
{
    var mainWindowXamlPath = FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var xaml = File.ReadAllText(mainWindowXamlPath);
    var coordinatorCode = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MatchSetupCoordinator.cs");
    var mainWindowCode = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
    var modeCard = XamlStartTag(xaml, "ModelBehaviorModeCard", "Border");
    var toggle = XamlStartTag(xaml, "ApplyMatchSetupToModelsCheckBox", "CheckBox");
    var status = XamlStartTag(xaml, "ApplyMatchSetupToModelsStatusText", "TextBlock");

    Require(modeCard.Contains("WorkflowInfoCard", StringComparison.Ordinal), "the model-behavior choice should use the shared Match Setup information surface");
    Require(toggle.Contains("Content=\"Apply Match Setup to models\"", StringComparison.Ordinal), "the toggle label should describe the enabled behavior instead of an ambiguous debug flag");
    Require(toggle.Contains("IsChecked=\"True\"", StringComparison.Ordinal), "new or legacy sessions should present the existing Arena behavior by default");
    Require(toggle.Contains("ToggleSwitchCheckBox", StringComparison.Ordinal), "the binary model-behavior choice should use the established keyboard-operable toggle style");
    Require(toggle.Contains("AutomationProperties.Name=\"Apply Match Setup to model behavior\"", StringComparison.Ordinal), "the model-behavior toggle should expose an unambiguous UI Automation name");
    Require(toggle.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal)
        && toggle.Contains("attributed public Operator and agent group history", StringComparison.Ordinal)
        && toggle.Contains("own replies as self-history", StringComparison.Ordinal)
        && toggle.Contains("system and error events remain visible", StringComparison.Ordinal), "toggle help should disclose Factory input and evidence-preservation semantics");
    Require(toggle.Contains("ToolTip=\"", StringComparison.Ordinal) && toggle.Contains("Factory mode", StringComparison.Ordinal), "pointer help should name the off-state mode");
    Require(status.Contains("AutomationProperties.Name=\"Model behavior mode status\"", StringComparison.Ordinal), "the live mode status should expose a stable automation name");
    Require(status.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "mode changes should be announced without interrupting the operator");
    Require(status.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "the mode explanation should wrap instead of clipping at narrow Match Setup widths");

    Require(coordinatorCode.Contains("Checked += ModelBehaviorMode_Changed", StringComparison.Ordinal)
        && coordinatorCode.Contains("Unchecked += ModelBehaviorMode_Changed", StringComparison.Ordinal), "keyboard and pointer toggle changes should share one persistence path");
    Require(coordinatorCode.Contains("factoryMode = applyMatchSetupToModelsCheckBox.IsChecked != true", StringComparison.Ordinal), "the unchecked state should map precisely to Factory mode");
    Require(coordinatorCode.Contains("AutomationProperties.SetItemStatus", StringComparison.Ordinal)
        && coordinatorCode.Contains("factoryMode ? \"Factory mode\" : \"Arena mode\"", StringComparison.Ordinal), "the hosted toggle should expose its semantic mode through UI Automation ItemStatus");
    Require(coordinatorCode.Contains("system and error events remain recorded", StringComparison.Ordinal), "the live Factory status should preserve evidence-honesty copy");

    var arenaState = new AIArenaMatchSetupControlState(false, "scenario", "arena", "session", "balanced", "", 1, false);
    Require(arenaState.ModelBehaviorMode == "arena", "the Match Setup control state should default compatibly to Arena mode");
    var factoryState = arenaState with { ModelBehaviorMode = "factory" };
    Require(factoryState.ModelBehaviorMode == "factory", "the Match Setup control state should carry Factory mode without changing its existing positional contract");
    Require(mainWindowCode.Contains("ModelBehaviorMode = snapshot?.FactoryMode == true ? \"factory\" : \"arena\"", StringComparison.Ordinal), "the hosted control-state projection should reflect the rendered session mode and default missing snapshots to Arena");

    RunStaTest(() =>
    {
        const string presentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        const string factoryStatus = "Factory mode — participants share attributed public group history while recognizing their own earlier replies. Arena guidance is saved but not sent; provider, model, and sampling settings still apply; system and error events remain recorded.";
        XNamespace presentation = presentationNamespace;
        XNamespace xNamespace = xamlNamespace;
        var document = XDocument.Load(mainWindowXamlPath, LoadOptions.PreserveWhitespace);
        XElement MainWindowStyle(string key) => document
            .Descendants(presentation + "Style")
            .Single(element => string.Equals((string?)element.Attribute(xNamespace + "Key"), key, StringComparison.Ordinal));
        var hintStyle = MainWindowStyle("HintText");
        var workflowInfoCardStyle = MainWindowStyle("WorkflowInfoCard");
        var toggleStyle = MainWindowStyle("ToggleSwitchCheckBox");
        var modelBehaviorCard = document
            .Descendants(presentation + "Border")
            .Single(element => string.Equals((string?)element.Attribute(xNamespace + "Name"), "ModelBehaviorModeCard", StringComparison.Ordinal));
        var assemblyName = typeof(MainWindow).Assembly.GetName().Name
            ?? throw new InvalidOperationException("WPF assembly name is unavailable.");
        var hostedXaml = new XElement(
            presentation + "Grid",
            new XAttribute("xmlns", presentationNamespace),
            new XAttribute(XNamespace.Xmlns + "x", xamlNamespace),
            new XElement(
                presentation + "Grid.Resources",
                new XElement(
                    presentation + "ResourceDictionary",
                    new XElement(
                        presentation + "ResourceDictionary.MergedDictionaries",
                        new[]
                        {
                            "UI/Theming/ThemeBrushes.xaml",
                            "UI/Theming/DesignTokens.xaml",
                            "UI/Theming/ControlStyles.xaml",
                            "UI/Theming/SurfaceStyles.xaml"
                        }.Select(relativePath => new XElement(
                            presentation + "ResourceDictionary",
                            new XAttribute("Source", $"/{assemblyName};component/{relativePath}")))),
                    new XElement(hintStyle),
                    new XElement(workflowInfoCardStyle),
                    new XElement(toggleStyle))),
            new XElement(modelBehaviorCard));
        var surface = XamlReader.Parse(hostedXaml.ToString(SaveOptions.DisableFormatting)) as Grid
            ?? throw new InvalidOperationException("Production Factory model-behavior card did not load as a hosted Grid.");
        var card = surface.FindName("ModelBehaviorModeCard") as Border
            ?? throw new InvalidOperationException("Hosted production model-behavior card was not registered in its XAML namescope.");
        var hostedToggle = surface.FindName("ApplyMatchSetupToModelsCheckBox") as CheckBox
            ?? throw new InvalidOperationException("Hosted production Factory toggle was not registered in its XAML namescope.");
        var hostedStatus = surface.FindName("ApplyMatchSetupToModelsStatusText") as TextBlock
            ?? throw new InvalidOperationException("Hosted production Factory status was not registered in its XAML namescope.");
        hostedStatus.Text = factoryStatus;

        var logicalViewport = new Grid
        {
            Width = 960,
            Height = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        logicalViewport.Children.Add(surface);
        var host = new Window
        {
            Width = 960,
            Height = 320,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
            Content = logicalViewport
        };

        static void DrainInput() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

        static Rect BoundsWithin(FrameworkElement element, Visual ancestor)
        {
            return element.TransformToAncestor(ancestor).TransformBounds(
                new Rect(new Point(), element.RenderSize));
        }

        static void RequireContentInside(FrameworkElement element, string label)
        {
            var contentBounds = VisualTreeHelper.GetDescendantBounds(element);
            Require(
                contentBounds.Left >= -1
                && contentBounds.Top >= -1
                && contentBounds.Right <= element.ActualWidth + 1
                && contentBounds.Bottom <= element.ActualHeight + 1,
                $"{label} rendered content outside its arranged bounds and would be clipped");
        }

        try
        {
            host.Show();
            host.Activate();
            host.UpdateLayout();
            DrainInput();

            var peer = UIElementAutomationPeer.CreatePeerForElement(hostedToggle)
                ?? new CheckBoxAutomationPeer(hostedToggle);
            var toggleProvider = peer.GetPattern(PatternInterface.Toggle) as System.Windows.Automation.Provider.IToggleProvider
                ?? throw new InvalidOperationException("Hosted production Factory toggle did not expose TogglePattern.");
            Require(
                peer.GetAutomationControlType() == AutomationControlType.CheckBox
                && peer.GetName() == "Apply Match Setup to model behavior"
                && peer.GetHelpText().Contains("attributed public Operator and agent group history", StringComparison.Ordinal),
                "hosted production Factory toggle did not preserve its CheckBox automation name and help contract");
            Require(toggleProvider.ToggleState == ToggleState.On && hostedToggle.IsChecked == true,
                "hosted production Factory toggle did not initialize in the compatible Arena on-state");
            toggleProvider.Toggle();
            DrainInput();
            Require(toggleProvider.ToggleState == ToggleState.Off && hostedToggle.IsChecked == false,
                "UI Automation TogglePattern did not move the production control into Factory mode");
            toggleProvider.Toggle();
            DrainInput();
            Require(hostedToggle.IsChecked == true && hostedToggle.Focus(),
                "hosted production Factory toggle could not be restored and focused for keyboard input");
            var inputSource = PresentationSource.FromVisual(hostedToggle)
                ?? PresentationSource.FromVisual(host)
                ?? throw new InvalidOperationException("Hosted production Factory toggle did not create a presentation source.");
            var spaceDown = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            var spaceUp = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Space)
            {
                RoutedEvent = Keyboard.KeyUpEvent
            };
            hostedToggle.RaiseEvent(spaceDown);
            hostedToggle.RaiseEvent(spaceUp);
            DrainInput();
            Require(spaceDown.Handled && spaceUp.Handled && hostedToggle.IsChecked == false,
                "Space did not operate the focused production Factory toggle through its WPF keyboard contract");

            var statusHeights = new Dictionary<double, double>();
            foreach (var width in new[] { 960d, 1500d })
            {
                logicalViewport.Width = width;
                host.UpdateLayout();
                DrainInput();
                var surfaceWidth = surface.ActualWidth;
                var cardBounds = BoundsWithin(card, surface);
                var toggleBounds = BoundsWithin(hostedToggle, surface);
                var statusBounds = BoundsWithin(hostedStatus, surface);
                Require(Math.Abs(surfaceWidth - width) <= 1,
                    $"hosted Factory surface did not arrange at the requested {width:0} DIP width");
                Require(
                    cardBounds.Left >= -1
                    && cardBounds.Right <= surfaceWidth + 1
                    && toggleBounds.Left >= cardBounds.Left - 1
                    && toggleBounds.Right <= cardBounds.Right + 1
                    && statusBounds.Left >= cardBounds.Left - 1
                    && statusBounds.Right <= cardBounds.Right + 1,
                    $"production Factory model-behavior content escaped its card at {width:0} DIP");
                Require(hostedStatus.TextWrapping == TextWrapping.Wrap && hostedStatus.ActualHeight > 0,
                    $"production Factory status did not retain measurable wrapping at {width:0} DIP");
                RequireContentInside(hostedToggle, $"production Factory toggle at {width:0} DIP");
                RequireContentInside(hostedStatus, $"production Factory status at {width:0} DIP");
                statusHeights[width] = hostedStatus.ActualHeight;
            }

            Require(statusHeights[960] > hostedStatus.FontSize * 1.5,
                "the full Factory explanation did not wrap at the supported 960 DIP inspection width");
            Require(statusHeights[1500] <= statusHeights[960] + 1,
                "the Factory explanation consumed more lines at 1500 DIP than at 960 DIP");
        }
        finally
        {
            host.Close();
        }
    });
}

static void AiLabHeaderAvoidsDuplicateMatchSetupAction()
{
    var shellXaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var header = XamlStartTag(shellXaml, "ArenaWorkspaceHeader", "controls:WorkspacePageHeaderControl");
    Require(
        !header.Contains("PrimaryActionText=\"Match setup\"", StringComparison.Ordinal)
        && header.Contains("IsCompactPresentation=\"True\"", StringComparison.Ordinal)
        && header.Contains("AnnounceStatusChanges=\"False\"", StringComparison.Ordinal)
        && header.Contains("PrimaryActionText=\"Transcript filters\"", StringComparison.Ordinal)
        && header.Contains("PrimaryActionRequested=\"TranscriptFiltersButton_Click\"", StringComparison.Ordinal),
        "the compact AI Lab header should use its one action for transient transcript filters instead of duplicating Match setup");

    var topBarXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
    var matchSetup = XamlStartTag(topBarXaml, "MatchSetupButton", "Button");
    Require(
        matchSetup.Contains("Content=\"Match setup\"", StringComparison.Ordinal)
        && matchSetup.Contains("Click=\"MatchSetupRequested\"", StringComparison.Ordinal),
        "the established top-bar Match setup command should remain visible and wired");

    var filtersPopup = XamlStartTag(shellXaml, "TranscriptFiltersPopup", "Popup");
    Require(filtersPopup.Contains("Placement=\"Bottom\"", StringComparison.Ordinal)
        && filtersPopup.Contains("HorizontalOffset=\"-", StringComparison.Ordinal)
        && filtersPopup.Contains("Opened=\"TranscriptFiltersPopup_Opened\"", StringComparison.Ordinal)
        && filtersPopup.Contains("Closed=\"TranscriptFiltersPopup_Closed\"", StringComparison.Ordinal),
        "transcript filters should use one anchored in-app flyout with explicit focus handoff");
    var turnPicker = XamlStartTag(shellXaml, "TranscriptTurnFilterPicker", "controls:RequiredSelectionListBox");
    Require(turnPicker.Contains("AutomationProperties.Name=\"Transcript turn range\"", StringComparison.Ordinal)
        && turnPicker.Contains("SelectionChanged=\"TranscriptTurnFilter_SelectionChanged\"", StringComparison.Ordinal)
        && turnPicker.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal),
        "turn filters should use one required-selection in-flyout list without opening a nested popup window");
    var turnSelectionHandler = typeof(MainWindow).GetMethod(
        "TranscriptTurnFilter_SelectionChanged",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Require(turnSelectionHandler?.GetParameters() is [_, var eventParameter]
        && eventParameter.ParameterType == typeof(SelectionChangedEventArgs),
        "the production turn selector should bind an exact SelectionChangedEventArgs bridge that WPF can load at runtime");
    var filtersOpened = CSharpMethodBlock(
        ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"),
        "private void TranscriptFiltersPopup_Opened(object? sender, EventArgs e)");
    var resetFilters = CSharpMethodBlock(
        ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"),
        "private void ResetTranscriptFiltersButton_Click(object sender, RoutedEventArgs e)");
    Require(filtersOpened.Contains("SelectedTranscriptTurnFilterEntry()", StringComparison.Ordinal)
        && resetFilters.Contains("SelectedTranscriptTurnFilterEntry().Focus()", StringComparison.Ordinal),
        "opening and resetting transcript filters should focus the selected option so arrow keys operate the list");
    foreach (var name in new[]
    {
        "TranscriptTurnFilterPicker",
        "TranscriptFilterSystemCheckBox",
        "TranscriptFilterAgentsCheckBox",
        "TranscriptFilterNarratorCheckBox",
        "TranscriptFilterOperatorCheckBox"
    })
    {
        Require(shellXaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal),
            $"the transcript filter flyout should preserve {name}");
    }
    foreach (var name in new[]
    {
        "TranscriptFilterSystemCheckBox",
        "TranscriptFilterAgentsCheckBox",
        "TranscriptFilterNarratorCheckBox",
        "TranscriptFilterOperatorCheckBox"
    })
    {
        var toggle = XamlStartTag(shellXaml, name, "CheckBox");
        Require(toggle.Contains("AutomationProperties.Name=\"", StringComparison.Ordinal)
            && toggle.Contains("AutomationProperties.HelpText=\"", StringComparison.Ordinal)
            && toggle.Contains("ToolTip=\"", StringComparison.Ordinal),
            $"{name} should explain its transient filter behavior to keyboard, pointer, and automation users");
    }
}

static void MainWindowMatchSetupMatrixHasClearAction()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var editor = XamlElementBlock(xaml, "OperatorTurnText", "TextBox");

    Require(editor.Contains("AcceptsReturn=\"True\"", StringComparison.Ordinal), "operator turn editor should remain multiline");
    Require(editor.Contains("TextWrapping=\"Wrap\"", StringComparison.Ordinal), "operator turn editor should wrap long public turns");
    Require(editor.Contains("VerticalContentAlignment=\"Top\"", StringComparison.Ordinal), "operator turn editor should align multiline text to the top");
    Require(editor.Contains("HorizontalContentAlignment=\"Left\"", StringComparison.Ordinal), "operator turn editor should align multiline text to the left");
    Require(editor.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal), "operator turn editor should expose a vertical scrollbar for long turns");
}

static void MainWindowTranscriptSearchPopupSizesResponsively()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
    var popupFrame = XamlStartTag(xaml, "TranscriptSearchPopupFrame", "Grid");

    Require(!popupFrame.Contains(" Width=\"760\"", StringComparison.Ordinal), "transcript search popup should not hard-code a fixed width");
    Require(popupFrame.Contains("MinWidth=\"420\"", StringComparison.Ordinal), "transcript search popup should keep a usable minimum width");
    Require(popupFrame.Contains("MaxWidth=\"760\"", StringComparison.Ordinal), "transcript search popup should keep the desktop width cap");
    Require(popupFrame.Contains("PlacementTarget.ActualWidth", StringComparison.Ordinal), "transcript search popup should size from the live placement target");
}

static void MainWindowOverlaysPreserveKeyboardAndAccessibilityContracts()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml")
        + Environment.NewLine
        + ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml")
        + Environment.NewLine
        + ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml");
    var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");

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

    foreach (var name in new[]
    {
        "ProviderHealthPopup",
        "ViewMenuPopup",
        "DebugMenuPopup",
        "CurrentSetupTransferPopup",
        "GenerationCopyPopup",
        "AgentComposerControlsPopup",
        "DiagnosticDetailPopup",
        "AgentPerformanceDetailPopup",
        "TranscriptFiltersPopup"
    })
    {
        var popup = XamlStartTag(xaml, name, "Popup");
        Require(popup.Contains("Opened=\"", StringComparison.Ordinal), $"{name} should move focus inside when opened");
        Require(popup.Contains("Closed=\"", StringComparison.Ordinal), $"{name} should restore focus when closed");
    }

    foreach (var (name, elementType) in new[]
    {
        ("TranscriptSearchPopupContent", "Border"),
        ("ProviderHealthPopupContent", "Border"),
        ("ViewMenuPopupContent", "Border"),
        ("DebugMenuPopupContent", "Border"),
        ("CurrentSetupTransferPopupContent", "shell:ShellPopupSurface"),
        ("GenerationCopyPopupContent", "shell:ShellPopupSurface"),
        ("AgentComposerControlsPopupContent", "shell:ShellPopupSurface"),
        ("DiagnosticDetailPopupContent", "shell:ShellPopupSurface"),
        ("AgentPerformanceDetailPopupContent", "shell:ShellPopupSurface"),
        ("TranscriptFiltersPopupContent", "shell:ShellPopupSurface")
    })
    {
        var popupContent = XamlStartTag(xaml, name, elementType);
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
        "VoiceTtsStatusText"
    })
    {
        var status = XamlStartTag(xaml, name, "TextBlock");
        Require(status.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), $"{name} should announce asynchronous status changes politely");
    }

    foreach (var name in new[]
    {
        "AgentStatusText",
        "CollaborateStatusText",
        "ProviderTestStatus",
        "InternetBackendStatusText",
        "InternetDiagnosticResultText",
        "SettingsTransferStatusText"
    })
    {
        var status = XamlStartTag(xaml, name, "TextBlock");
        Require(status.Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
            $"{name} should remain contextual while the universal status center owns its live announcement");
    }

    Require(source.Contains("protected override void OnPreviewKeyDown", StringComparison.Ordinal), "the main shell should route Escape to the topmost overlay");
    Require(source.Contains("CloseTopmostShellOverlay()", StringComparison.Ordinal), "the main shell should close overlays in deterministic z-order");
    Require(source.Contains("FocusOverlayEntry", StringComparison.Ordinal), "popup opening should move focus to an actionable entry");
    Require(source.Contains("RestoreOverlayFocus", StringComparison.Ordinal), "overlay closure should restore focus to its opener");
    Require(source.Contains("ProviderHealthPopup_Opened", StringComparison.Ordinal), "provider health should explicitly move focus inside its popup window");
    Require(source.Contains("TranscriptSearchPopup_PreviewKeyDown", StringComparison.Ordinal), "search should handle Escape after focus moves from the text editor to a result row");
    var closeTopmostOverlay = CSharpMethodBlock(source, "private bool CloseTopmostShellOverlay()");
    Require(closeTopmostOverlay.Contains("TranscriptFiltersPopup.IsOpen", StringComparison.Ordinal),
        "window-level Escape should close the transcript filter flyout even after focus moves outside its content");
    foreach (var name in new[]
    {
        "CurrentSetupTransferPopup",
        "GenerationCopyPopup",
        "AgentComposerControlsPopup",
        "DiagnosticDetailPopup",
        "AgentPerformanceDetailPopup",
        "TranscriptFiltersPopup"
    })
    {
        Require(source.Contains($"{name}_Opened", StringComparison.Ordinal), $"{name} should focus its first actionable entry");
        Require(source.Contains($"{name}_Closed", StringComparison.Ordinal), $"{name} should restore focus to its opener");
        Require(source.Contains($"{name}_PreviewKeyDown", StringComparison.Ordinal), $"{name} should close locally on Escape");
    }

    var closeTransientFlyouts = CSharpMethodBlock(source, "private void CloseNamedTransientShellFlyouts()");
    Require(closeTransientFlyouts.Contains("CurrentSetupTransferPopup.IsOpen = false", StringComparison.Ordinal)
        && closeTransientFlyouts.Contains("GenerationCopyPopup.IsOpen = false", StringComparison.Ordinal)
        && closeTransientFlyouts.Contains("TranscriptFiltersPopup.IsOpen = false", StringComparison.Ordinal),
        "shell transitions should close Match Setup child flyouts and transient transcript filters before changing surfaces");
    var showProvider = CSharpMethodBlock(source, "private void ShowProviderHealthPopup(UIElement? opener = null)");
    var openUserGuide = CSharpMethodBlock(source, "private void OpenUserGuideButton_Click(object sender, RoutedEventArgs e)");
    var rightRailToggle = CSharpMethodBlock(source, "private void RightRailToggleButton_Click(object sender, RoutedEventArgs e)");
    Require(showProvider.Contains("TranscriptFiltersPopup.IsOpen = false", StringComparison.Ordinal)
        && openUserGuide.Contains("TranscriptFiltersPopup.IsOpen = false", StringComparison.Ordinal)
        && rightRailToggle.Contains("CloseNamedTransientShellFlyouts()", StringComparison.Ordinal),
        "competing dialogs and anchor-layout changes should close transient transcript filters deterministically");
    var closeMatchSetup = CSharpMethodBlock(source, "private void CloseMatchSetupFlyout()");
    Require(closeMatchSetup.Contains("CurrentSetupTransferPopup.IsOpen = false", StringComparison.Ordinal)
        && closeMatchSetup.Contains("GenerationCopyPopup.IsOpen = false", StringComparison.Ordinal),
        "closing Match Setup should close its transfer and generated-match popup windows before restoring the prior surface");

    var diagnosticsSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/DiagnosticsWorkflowCoordinator.cs");
    Require(diagnosticsSource.Contains("openerCard.Invoked += DiagnosticChip_Invoked", StringComparison.Ordinal)
        && xaml.Contains("<shell:ShellPopupOpenerCard x:Name=\"FrictionChip\"", StringComparison.Ordinal),
        "diagnostic popup openers should use the peer-backed shared keyboard and automation invocation contract");
    var performanceSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/AgentPerformanceCoordinator.cs");
    Require(performanceSource.Contains("ConfigureDetailCard(card, stats, displayTitle)", StringComparison.Ordinal)
        && performanceSource.Contains("new ShellPopupOpenerCard", StringComparison.Ordinal)
        && performanceSource.Contains("openerCard.Invoked +=", StringComparison.Ordinal),
        "agent-performance popup openers should be keyboard reachable and use the peer-backed shared invocation contract");

    HostedMainWindowPopupKeyboardContract();
}

static void HostedMainWindowPopupKeyboardContract()
{
    RunStaTest(() =>
    {
        var opener = new Button { Content = "Open actions", Width = 120, Height = 36 };
        var refreshedOpener = new Button { Content = "Refreshed opener", Width = 120, Height = 36 };
        var outsideDestination = new Button { Content = "Outside destination", Width = 140, Height = 36 };
        var openerCard = new ShellPopupOpenerCard
        {
            Width = 140,
            Height = 36,
            Focusable = true,
            Child = new TextBlock { Text = "Diagnostic card" }
        };
        KeyboardNavigation.SetIsTabStop(openerCard, true);
        AutomationProperties.SetName(openerCard, "Open diagnostic detail");
        AutomationProperties.SetHelpText(openerCard, "Open bounded diagnostic evidence.");
        var openerInvocations = 0;
        openerCard.Invoked += (_, _) => openerInvocations++;
        var firstAction = new Button { Content = "First action", Width = 120, Height = 34 };
        var turnPicker = new RequiredSelectionListBox { Width = 140, Height = 68 };
        turnPicker.Items.Add(new ListBoxItem { Content = "All turns", IsSelected = true });
        turnPicker.Items.Add(new ListBoxItem { Content = "Latest 10" });
        var speakerToggle = new CheckBox { Content = "Agents", IsChecked = true, Height = 34 };
        var lastAction = new Button { Content = "Last action", Width = 120, Height = 34 };
        var popupContent = new StackPanel
        {
            Width = 180,
            Background = Brushes.White,
            Children = { firstAction, turnPicker, speakerToggle, lastAction }
        };
        var popupSurface = new ShellPopupSurface { Child = popupContent };
        AutomationProperties.SetName(popupSurface, "Hosted popup actions");
        FocusManager.SetIsFocusScope(popupSurface, true);
        KeyboardNavigation.SetTabNavigation(popupSurface, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(popupSurface, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(popupSurface, KeyboardNavigationMode.Contained);
        var popup = new Popup
        {
            PlacementTarget = opener,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = popupSurface
        };
        IInputElement? focusReturnTarget = null;
        popup.Opened += (_, _) =>
        {
            focusReturnTarget ??= Keyboard.FocusedElement ?? opener;
            MainWindow.FocusOverlayEntry(popup, firstAction);
        };
        popup.Closed += (_, _) =>
        {
            var returnTarget = popup.PlacementTarget ?? focusReturnTarget;
            focusReturnTarget = null;
            MainWindow.RestoreOverlayFocus(returnTarget, opener, () => !popup.IsOpen);
        };
        popupSurface.PreviewKeyDown += (_, args) => MainWindow.ClosePopupOnEscape(popup, args);
        var hostContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { opener, refreshedOpener, outsideDestination, openerCard }
        };
        var host = new Window
        {
            Width = 320,
            Height = 180,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
            Content = hostContent
        };

        static void DrainInput() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

        try
        {
            host.Show();
            host.Activate();
            host.UpdateLayout();
            var openerCardPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(openerCard)
                ?? throw new InvalidOperationException("Hosted popup opener card did not create an automation peer.");
            var invokeProvider = openerCardPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                as System.Windows.Automation.Provider.IInvokeProvider
                ?? throw new InvalidOperationException("Hosted popup opener card did not expose an Invoke provider.");
            Require(openerCardPeer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Button
                && openerCardPeer.GetName() == "Open diagnostic detail"
                && openerCardPeer.GetHelpText() == "Open bounded diagnostic evidence.",
                "hosted diagnostic/performance opener did not expose a peer-backed Button and Invoke contract");
            invokeProvider.Invoke();
            DrainInput();
            Require(openerInvocations == 1,
                "hosted diagnostic/performance opener did not route UI Automation Invoke through its shared activation contract");
            Require(opener.Focus(), "hosted popup opener was not keyboard focusable");
            popup.IsOpen = true;
            DrainInput();
            var popupSurfacePeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(popupSurface)
                ?? throw new InvalidOperationException("Hosted popup surface did not create an automation peer.");
            Require(popupSurfacePeer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Group
                && popupSurfacePeer.GetName() == "Hosted popup actions",
                "hosted popup surface did not expose its peer-backed automation group");
            Require(firstAction.IsKeyboardFocused,
                "hosted non-top-bar popup did not move keyboard focus to its first actionable entry");

            var turnPickerPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(turnPicker)
                ?? throw new InvalidOperationException("Hosted turn picker did not create a ListBox automation peer.");
            var turnSelection = turnPickerPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Selection)
                as System.Windows.Automation.Provider.ISelectionProvider
                ?? throw new InvalidOperationException("Hosted turn picker did not expose SelectionPattern.");
            var latestTurnItem = (ListBoxItem)turnPicker.Items[1];
            var latestTurnPeer = turnPickerPeer.GetChildren()?
                .FirstOrDefault(peer => peer.GetName().Equals("Latest 10", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Hosted turn option did not create a ListBoxItem automation peer.");
            var latestTurnSelection = latestTurnPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)
                as System.Windows.Automation.Provider.ISelectionItemProvider
                ?? throw new InvalidOperationException("Hosted turn option did not expose SelectionItemPattern.");
            Require(turnSelection.IsSelectionRequired,
                "hosted turn picker did not report its non-empty selection contract to UI Automation");
            var allTurnsItem = (ListBoxItem)turnPicker.Items[0];
            Require(allTurnsItem.Focus(), "hosted popup selected turn option was not keyboard focusable");
            var turnInputSource = PresentationSource.FromVisual(popupSurface)
                ?? PresentationSource.FromVisual(host)
                ?? throw new InvalidOperationException("Hosted turn picker did not create a presentation source.");
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, turnInputSource, Environment.TickCount, Key.Down)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            };
            allTurnsItem.RaiseEvent(down);
            DrainInput();
            Require(popup.IsOpen && down.Handled && turnPicker.SelectedIndex == 1,
                "Down from the focused selected turn option did not move selection while preserving the flyout");
            latestTurnSelection.RemoveFromSelection();
            DrainInput();
            Require(popup.IsOpen && turnPicker.SelectedIndex == 0 && turnSelection.GetSelection().Length == 1,
                "removing the selected turn through UI Automation left the required-selection list dishonest");
            latestTurnSelection.Select();
            DrainInput();
            Require(popup.IsOpen && turnPicker.SelectedIndex == 1 && latestTurnSelection.IsSelected,
                "selecting a turn-range list item did not preserve the parent flyout and SelectionItemPattern state");
            var speakerPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(speakerToggle)
                ?? throw new InvalidOperationException("Hosted speaker filter did not create a CheckBox automation peer.");
            var toggleProvider = speakerPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)
                as System.Windows.Automation.Provider.IToggleProvider
                ?? throw new InvalidOperationException("Hosted speaker filter did not expose TogglePattern.");
            toggleProvider.Toggle();
            DrainInput();
            Require(popup.IsOpen && speakerToggle.IsChecked == false,
                "toggling a speaker filter did not preserve the parent flyout and TogglePattern state");

            Require(firstAction.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)),
                "hosted popup did not process reverse keyboard traversal");
            DrainInput();
            Require(lastAction.IsKeyboardFocused,
                "hosted popup Shift+Tab navigation escaped instead of cycling to its last action");
            Require(lastAction.Focus(), "hosted popup final action was not keyboard focusable");
            Require(lastAction.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
                "hosted popup did not process forward keyboard traversal");
            DrainInput();
            Require(firstAction.IsKeyboardFocused,
                "hosted popup Tab navigation escaped instead of cycling to its first action");

            var inputSource = PresentationSource.FromVisual(popupSurface)
                ?? PresentationSource.FromVisual(host)
                ?? throw new InvalidOperationException("Hosted popup did not create a presentation source.");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, inputSource, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            popupSurface.RaiseEvent(escape);
            DrainInput();
            Require(!popup.IsOpen && escape.Handled,
                "hosted popup Escape handling did not close and consume the popup key event");
            Require(opener.IsKeyboardFocused,
                "hosted popup closure did not restore keyboard focus to its opener");

            popup.PlacementTarget = opener;
            popup.IsOpen = true;
            DrainInput();
            Require(firstAction.IsKeyboardFocused,
                "hosted popup did not re-enter its focus scope after reopening");
            popup.PlacementTarget = refreshedOpener;
            popup.IsOpen = false;
            DrainInput();
            Require(refreshedOpener.IsKeyboardFocused,
                "hosted popup closure restored a stale opener after its live placement target changed");

            Require(opener.Focus(), "hosted popup opener could not be refocused for pointer-dismissal coverage");
            popup.PlacementTarget = opener;
            popup.IsOpen = true;
            DrainInput();
            Require(firstAction.IsKeyboardFocused,
                "hosted popup did not focus its entry before pointer-dismissal coverage");
            Require(outsideDestination.Focus(), "hosted outside-click destination was not keyboard focusable");
            popup.IsOpen = false;
            DrainInput();
            Require(outsideDestination.IsKeyboardFocused,
                "outside-pointer dismissal was overwritten by popup focus restoration");
        }
        finally
        {
            popup.IsOpen = false;
            host.Close();
        }
    });
}

static void MainWindowAdaptiveShellLayoutStaysWired()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var topBarXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellTopBarControl.xaml");
    var railXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/ShellNavigationRailControl.xaml");
    var windowTag = xaml[..(xaml.IndexOf('>') + 1)];
    var topBarLayout = XamlStartTag(topBarXaml, "TopBarLayoutGrid", "Grid");
    var topBarStatus = XamlStartTag(topBarXaml, "TopBarStatus", "Grid");
    var topBarCommands = XamlStartTag(topBarXaml, "TopBarCommandPanel", "WrapPanel");
    var saveStatusProxy = XamlStartTag(topBarXaml, "SaveStatusText", "TextBlock");
    var statusCenterXaml = ReadWorkspaceFile("src/AIArena.Wpf/UI/Controls/UniversalStatusCenterControl.xaml");
    var statusCard = XamlStartTag(statusCenterXaml, "StatusCenterCard", "Border");
    var statusText = XamlStartTag(statusCenterXaml, "LiveAnnouncementText", "TextBlock");
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
    var mainWindowSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
    Require(mainWindowSource.Contains("Grid.SetRow(TopBarCommandPanel, stacked ? 1 : 0);", StringComparison.Ordinal), "the adaptive shell should move commands between the narrow command row and shared primary row");
    Require(mainWindowSource.Contains("ShellNavigationRail.Presentation = ShellTopBar.Presentation;", StringComparison.Ordinal), "the navigation rail and top bar should share the exact shell presentation model instance");
    Require(transcriptSearchPopup.Contains("PlacementTarget=\"{Binding ElementName=TopBarLayoutGrid}\"", StringComparison.Ordinal), "the transcript search popup should open below the complete multi-row top bar");
    Require(matchSetupButton.Contains("Style=\"{StaticResource Arena.Button.Primary}\"", StringComparison.Ordinal), "Match Setup should retain primary emphasis in the top rail");
    Require(matchSetupButton.Contains("Height=\"38\"", StringComparison.Ordinal) && matchSetupButton.Contains("VerticalAlignment=\"Center\"", StringComparison.Ordinal), "Match Setup should match the 38-DIP top-rail command-group height");
    Require(matchSetupButton.Contains("Width=\"104\"", StringComparison.Ordinal), "Match Setup and Close Setup should share a fixed width so toggling does not shift neighboring commands");
    Require(matchSetupButton.Contains("Padding=\"{DynamicResource Arena.Inset.ToolbarAction}\"", StringComparison.Ordinal), "Match Setup should use the compact horizontal-only toolbar padding token");
    Require(viewMenuButton.Contains("Content=\"{Binding ViewButtonLabel}\"", StringComparison.Ordinal), "the closed View control should keep the active preset visible");
    Require(statusCard.Contains("AutomationProperties.Name=\"Universal Status Center, four recent updates\"", StringComparison.Ordinal),
        "the universal status card should expose its fixed four-row purpose");
    Require(statusText.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal),
        "the universal status center should own the one polite shell live announcer");
    Require(!railXaml.Contains("ShellStatusDockElement", StringComparison.Ordinal)
            && !railXaml.Contains("ShellStatusTextElement", StringComparison.Ordinal),
        "the legacy bottom-left status dock should be removed after universal-center migration");
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
    Require(topBarPresentation.StatusCenter.AppStatus == "Ready"
            && topBarPresentation.ShowStatusDock,
        "routine provider health should stay silent while the fixed status center remains reserved");
    changedPropertyOrder.Clear();
    topBarPresentation.ArenaStatus = "Select a model before running the arena.";
    Require(topBarPresentation.ShowStatusDock, "the compatibility visibility flag should keep the fixed four-row center reserved");
    Require(topBarPresentation.DisplayStatus == "Select a model before running the arena.", "persistent actionable status should feed the typed visible projection");
    Require(topBarPresentation.DisplayStatusToolTip == topBarPresentation.DisplayStatus, "persistent status should expose its concise detail");
    Require(topBarPresentation.DisplayStatusHelpText.Contains(topBarPresentation.DisplayStatus, StringComparison.Ordinal), "persistent status help should include the exact actionable state");
    Require(topBarPresentation.StatusCenter.History.Count == 1,
        "an actionable compatibility status should publish exactly one typed history entry");

    var firstGeneration = topBarPresentation.ShowTransientStatus(
        "Screenshot saved: first.png",
        @"C:\captures\first.png",
        "AI Arena saved the first screenshot.");
    var firstTransient = topBarPresentation.StatusCenter.History.Single(entry =>
        entry.Summary.Equals("Screenshot saved: first.png", StringComparison.Ordinal));
    Require(topBarPresentation.DisplayStatus == "Select a model before running the arena.",
        "a foreground arena operation should retain primary priority over a recent success receipt");
    Require(firstTransient.State == ApplicationStatusState.Succeeded
            && !firstTransient.Detail.Contains(@"C:\captures", StringComparison.OrdinalIgnoreCase)
            && firstTransient.Detail.Contains("[local path]", StringComparison.Ordinal),
        "transient status history should retain the success while redacting absolute filesystem paths");

    topBarPresentation.ArenaStatus = "Select a provider model.";
    Require(topBarPresentation.DisplayStatus == "Select a provider model.",
        "a new action-required warning should outrank a recent success receipt");
    var secondGeneration = topBarPresentation.ShowTransientStatus(
        "Screenshot saved: second.png",
        @"C:\captures\second.png",
        "AI Arena saved the second screenshot.");
    Require(topBarPresentation.ClearTransientStatus(firstGeneration), "an older receipt should resolve only its own causal entry");
    Require(topBarPresentation.DisplayStatus == "Select a provider model.",
        "a newer success receipt should remain in history without displacing an action-required warning");
    Require(topBarPresentation.StatusCenter.History.Any(entry =>
            entry.Summary.Equals("Screenshot saved: second.png", StringComparison.Ordinal)
            && entry.State == ApplicationStatusState.Succeeded),
        "the newer success receipt should remain available in status history");
    Require(topBarPresentation.ClearTransientStatus(secondGeneration), "the current transient generation should clear successfully");
    Require(topBarPresentation.DisplayStatus == "Select a provider model.", "clearing the current receipt should restore the latest persistent status");
    Require(topBarPresentation.ShowStatusDock, "restored actionable status should keep the fixed center reserved");
    changedPropertyOrder.Clear();
    topBarPresentation.ArenaStatus = "Ready.";
    Require(topBarPresentation.ShowStatusDock
            && topBarPresentation.DisplayStatus == "Ready",
        "routine status should return to Ready without collapsing the stable center footprint");
    foreach (var propertyName in new[] { "DisplayStatus", "DisplayStatusToolTip", "DisplayStatusHelpText" })
    {
        Require(changedProperties.Contains(propertyName), $"{propertyName} should notify the shared status binding when its projection changes");
    }

    var screenshotSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs");
    Require(screenshotSource.Contains("ShowTransientStatus(receiptText, result.Path, helpText)", StringComparison.Ordinal), "screenshot receipts should move through the shared status presentation");
    Require(screenshotSource.Contains("ClearTransientStatus(generation)", StringComparison.Ordinal), "screenshot receipt expiry should clear only its own generation");
    Require(!screenshotSource.Contains("SetTransientStatusVisible", StringComparison.Ordinal), "screenshot receipts should not use the retired visibility-only status API");
    Require(!screenshotSource.Contains("SaveStatusText.Visibility", StringComparison.Ordinal), "the compatibility save target should never become visual");
    Require(!screenshotSource.Contains("SaveStatusText.Text.Equals", StringComparison.Ordinal), "screenshot receipt expiry should not rely on text equality for stale-clear protection");
    Require(screenshotSource.Contains("applicationStatus.Primary.Summary", StringComparison.Ordinal),
        "the control-plane AppStatus compatibility field should project the universal center's primary summary");
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

    var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
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

    Require(exportStatus.Attribute("Width") is null
            && exportStatus.Attribute("MaxWidth") is null
            && (string?)exportStatus.Attribute("Visibility") == "Collapsed"
            && (string?)exportStatus.Attribute("IsHitTestVisible") == "False"
            && (string?)exportStatus.Attribute("Focusable") == "False"
            && (string?)exportStatus.Attribute("AutomationProperties.LiveSetting") == "Off",
        "the migrated export status compatibility target should never reserve toolbar space or duplicate announcements");
}

static void MainWindowRightRailCollapsePreservesKeyboardContext()
{
    var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
    var methodStart = source.IndexOf("private void ApplyRightRailCollapsed()", StringComparison.Ordinal);
    var methodEnd = source.IndexOf("internal static bool ShouldAutoCollapseRightRail", methodStart, StringComparison.Ordinal);
    Require(methodStart >= 0 && methodEnd > methodStart, "the right-rail layout method should remain discoverable");
    var method = source[methodStart..methodEnd];

    var focusCapture = method.IndexOf("RightRailScrollViewer.IsKeyboardFocusWithin", StringComparison.Ordinal);
    var visibilityChange = method.IndexOf("RightRailScrollViewer.Visibility =", StringComparison.Ordinal);
    var focusHandoff = method.IndexOf("Keyboard.Focus(focusTarget)", StringComparison.Ordinal);
    Require(focusCapture >= 0, "right-rail collapse should detect keyboard focus within the rail");
    Require(visibilityChange > focusCapture, "right-rail focus state must be captured before the rail is collapsed");
    Require(focusHandoff > visibilityChange, "right-rail collapse should hand focus off only after hiding the focused subtree");
    Require(method.Contains("collapsed && RightRailScrollViewer.IsKeyboardFocusWithin", StringComparison.Ordinal), "focus handoff should run only for an effective collapse with focus inside the rail");
    Require(method.Contains("RightRailScrollViewer.Visibility == Visibility.Collapsed", StringComparison.Ordinal), "the deferred focus handoff should verify that the rail is still collapsed");
    Require(method.Contains("focusTarget.IsVisible", StringComparison.Ordinal), "the focus handoff should require a visible shell target");
    Require(method.Contains("focusTarget.IsEnabled", StringComparison.Ordinal), "the focus handoff should require an enabled shell target");
    Require(method.Contains("statusCenterHadFocus", StringComparison.Ordinal)
            && method.Contains("CollapsedStatusCenterButton", StringComparison.Ordinal),
        "collapsing the rail should preserve status-center context through the compact top-bar affordance");
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

static void MainWindowDebugControlsRemainDiscoverable()
{
    var document = XDocument.Load(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml"));
    XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    var section = document
        .Descendants()
        .Single(element => element.Name.LocalName == "Expander"
            && (string?)element.Attribute(xamlNamespace + "Name") == "DebugControlsSettingsExpander");
    Require(
        string.Equals((string?)section.Attribute("Header"), "Debug controls", StringComparison.Ordinal),
        "debug controls should have a plainly labeled top-level Settings section");

    var toggle = section
        .Descendants()
        .Single(element => element.Name.LocalName == "CheckBox"
            && (string?)element.Attribute(xamlNamespace + "Name") == "DebugControlsCheckBox");
    Require(
        string.Equals((string?)toggle.Attribute("Content"), "Allow debug controls", StringComparison.Ordinal),
        "the established debug-controls toggle should remain in the discoverable section");
    Require(
        toggle.Attribute("AutomationProperties.Name") is not null
        && toggle.Attribute("AutomationProperties.HelpText") is not null,
        "the debug-controls toggle should expose its purpose to UI Automation");
    Require(
        string.Equals((string?)toggle.Attribute("Checked"), "VisualSettings_Changed", StringComparison.Ordinal)
        && string.Equals((string?)toggle.Attribute("Unchecked"), "VisualSettings_Changed", StringComparison.Ordinal),
        "the promoted toggle should retain its persisted settings behavior");

    var controlPlaneToggle = section
        .Descendants()
        .Single(element => element.Name.LocalName == "CheckBox"
            && (string?)element.Attribute(xamlNamespace + "Name") == "ControlPlaneCheckBox");
    Require(
        string.Equals((string?)controlPlaneToggle.Attribute("Checked"), "ControlPlaneCheckBox_Changed", StringComparison.Ordinal)
        && string.Equals((string?)controlPlaneToggle.Attribute("Unchecked"), "ControlPlaneCheckBox_Changed", StringComparison.Ordinal)
        && controlPlaneToggle.Attribute("AutomationProperties.Name") is not null
        && ((string?)controlPlaneToggle.Attribute("AutomationProperties.HelpText"))?.Contains("independent", StringComparison.OrdinalIgnoreCase) == true,
        "PowerShell control should live inside Debug controls while retaining its independent persisted and accessible contract");
    Require(
        !document.Descendants().Any(element => element.Name.LocalName == "Expander"
            && string.Equals((string?)element.Attribute("Header"), "PowerShell Control", StringComparison.Ordinal)),
        "PowerShell control should not remain as a separate top-level Settings category");
}

static void MainWindowInternetSettingsUseOneDirectToggle()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");

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
    Require((string?)provider.Attribute("Header") == "Provider connection", "Settings should focus the provider section on connection rather than duplicating model selection");
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
        Require(subsection.Ancestors().Contains(provider), $"{subsectionName} should stay inside Provider connection");
        Require((string?)subsection.Attribute("IsExpanded") == "False", $"{subsectionName} should default collapsed");
        Require((string?)subsection.Attribute("Style") == "{StaticResource SettingsSubsectionExpander}", $"{subsectionName} should use the compact subsection style");
    }

    bool IsInsideOptionalSubsection(XElement element) => element
        .Ancestors()
        .Any(ancestor => subsectionNames.Contains((string?)ancestor.Attribute(xamlNamespace + "Name"), StringComparer.Ordinal));

    foreach (var essentialName in new[] { "ProviderPresetPicker", "TestProviderButton", "OpenModelsSurfaceButton" })
    {
        var essential = Named(essentialName);
        Require(essential.Ancestors().Contains(provider), $"{essentialName} should stay in Provider connection");
        Require(!IsInsideOptionalSubsection(essential), $"{essentialName} should remain on the short primary setup path");
    }

    var compatibilityModelPickerHost = Named("ProviderModelText").Ancestors().First(element => element.Name.LocalName == "Grid");
    Require((string?)compatibilityModelPickerHost.Attribute("Visibility") == "Collapsed", "Settings should not expose a second model selector");
    Require((string?)Named("ProviderRoleRoutingExpander").Attribute("Visibility") == "Collapsed", "Settings should not expose a second assignment surface");
    Require(Named("ProviderModelsPanel").Name.LocalName == "ProviderModelAssignmentsControl", "the top-rail Models surface should host the production catalog and assignment control");

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
    var providerSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs");
    var preloadLifecycle = CSharpMethodBlock(providerSource, "public async Task PreloadSelectedModelsAsync(");
    var unloadLifecycle = CSharpMethodBlock(providerSource, "public async Task UnloadSelectedModelsAsync(");
    Require(preloadLifecycle.Contains("RunLifecycleLockedAsync", StringComparison.Ordinal)
            && unloadLifecycle.Contains("RunLifecycleLockedAsync", StringComparison.Ordinal)
            && preloadLifecycle.Contains("mutationStarting:", StringComparison.Ordinal)
            && unloadLifecycle.Contains("mutationStarting:", StringComparison.Ordinal)
            && preloadLifecycle.Contains("mutationStarted", StringComparison.Ordinal)
            && unloadLifecycle.Contains("mutationStarted", StringComparison.Ordinal)
            && preloadLifecycle.Contains("MutationOutcomeUnknown", StringComparison.Ordinal)
            && unloadLifecycle.Contains("MutationOutcomeUnknown", StringComparison.Ordinal),
        "legacy Settings lifecycle commands should share the non-queuing arena/provider operation gate");
    var lifecycleGate = CSharpMethodBlock(providerSource, "private async Task RunLifecycleLockedAsync(");
    Require(lifecycleGate.Contains("arenaOperationLock.WaitAsync(0", StringComparison.Ordinal)
            && lifecycleGate.Contains("isArenaBusy()", StringComparison.Ordinal)
            && lifecycleGate.Contains("arenaOperationLock.Release()", StringComparison.Ordinal),
        "Settings lifecycle gating should reject overlap, recheck arena state, and release its shared lock");
    Require(providerSource.Contains("CaptureLifecycleContext", StringComparison.Ordinal)
            && providerSource.Contains("EnsureLifecycleContext", StringComparison.Ordinal)
            && providerSource.Contains("ProviderSettingsLifecycleContextChangedException", StringComparison.Ordinal)
            && providerSource.Contains("SafeStatusForDisplay", StringComparison.Ordinal),
        "Settings lifecycle must revalidate session/provider identity at mutation time and sanitize provider details");
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
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    Require(xaml.Contains("x:Name=\"SettingsSearchFeedbackText\"", StringComparison.Ordinal)
        && xaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal), "settings search should announce its visible result count or empty state");
    Require(xaml.Contains("x:Name=\"SettingsSearchClearButton\"", StringComparison.Ordinal)
        && xaml.Contains("Click=\"SettingsSearchClearButton_Click\"", StringComparison.Ordinal), "settings search feedback should include a direct clear action");
    });
}

}
