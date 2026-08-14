using AIArena.Wpf;
using AIArena.Wpf.Models;

internal static partial class Program
{
    private static void MatchSetupSnapshotProjectionStaysLazyAndCurrent()
    {
        var state = new MatchSetupSnapshotRenderState();
        var snapshot = MatchSetupSnapshot("session-a", "Initial topic");

        // Baseline receipt: before this gate, MainWindow invoked four Match
        // Setup child projections for every RenderSnapshot, including hidden
        // refreshes. 100 hidden refreshes therefore produced 400 calls.
        const int hiddenRefreshes = 100;
        var optimizedProjectionPlans = 0;
        for (var index = 0; index < hiddenRefreshes; index++)
        {
            state.Observe(snapshot with { TurnIndex = index, ProviderLastLatencyMs = index });
            if (state.Plan(visible: false) is not null)
            {
                optimizedProjectionPlans++;
            }
        }

        Require(optimizedProjectionPlans == 0,
            $"hidden Match Setup should schedule zero projections; observed {optimizedProjectionPlans} versus baseline 400 child calls");
        Require(state.LatestSnapshot?.TurnIndex == hiddenRefreshes - 1,
            "the hidden projection gate should still retain the newest authoritative snapshot");

        var opened = state.Plan(visible: true);
        Require(opened is not null && opened.Snapshot.TurnIndex == hiddenRefreshes - 1,
            "opening Match Setup should project the newest snapshot rather than the last visible snapshot");
        Require(opened!.Areas == MatchSetupProjectionArea.All,
            "the first visible projection should initialize every Match Setup component");
        state.Commit(opened);

        state.Observe(opened.Snapshot with { TurnIndex = 999, ProviderLastLatencyMs = 999 });
        Require(state.Plan(visible: true) is null,
            "ordinary turn and telemetry changes should not rebuild visible Match Setup");

        state.Observe(opened.Snapshot with { TurnCount = opened.Snapshot.TurnCount + 1 });
        var turnCountChanged = state.Plan(visible: true);
        Require(turnCountChanged is not null
            && turnCountChanged.Areas.HasFlag(MatchSetupProjectionArea.SetupSummary),
            "the visible run-shape summary should refresh when its displayed turn count advances");
        state.Commit(turnCountChanged!);

        state.Observe(opened.Snapshot with { ScenarioTopic = "Externally updated topic" });
        var changed = state.Plan(visible: true);
        Require(changed is not null
            && changed.Areas.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && changed.Areas.HasFlag(MatchSetupProjectionArea.SetupFeedback),
            "authoritative scenario changes should reconcile the visible setup summary and readiness feedback");

        state.Observe(opened.Snapshot with { ScenarioTopic = "Newer external topic" });
        state.Commit(changed!);
        var afterStaleCommit = state.Plan(visible: true);
        Require(afterStaleCommit is not null && afterStaleCommit.Snapshot.ScenarioTopic == "Newer external topic",
            "a stale projection completion must not mark a newer observed snapshot as rendered");

        state.Reset();
        Require(state.LatestSnapshot is null && state.Plan(visible: true) is null,
            "fallback/session reset should discard pending projection state");
    }

    private static void MatchSetupProjectionIdentityScopesRelevantChanges()
    {
        var baseline = MatchSetupSnapshot("session-a", "Topic");
        var identity = MatchSetupProjectionIdentity.Create(baseline);

        var seedChange = MatchSetupProjectionIdentity.Create(baseline with { ScenarioGeneratorSeed = "seed-2" })
            .ChangesSince(identity);
        Require(seedChange.HasFlag(MatchSetupProjectionArea.SeedInspector)
            && seedChange.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && seedChange.HasFlag(MatchSetupProjectionArea.SetupFeedback)
            && !seedChange.HasFlag(MatchSetupProjectionArea.RivalryMatrix),
            "seed changes should update seed, summary, and readiness projections without rebuilding rivalry controls");

        var history = new GenerationHistoryItem(
            "history-1", "random", "Generated", "audit", "high", "debate", "grounded",
            "scenario-seed", "persona-seed", 42, "Prior topic");
        var historyChange = MatchSetupProjectionIdentity.Create(baseline with { GenerationHistory = [history] })
            .ChangesSince(identity);
        Require(historyChange.HasFlag(MatchSetupProjectionArea.GenerationHistory)
            && historyChange.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && historyChange.HasFlag(MatchSetupProjectionArea.SetupFeedback),
            "history changes should update replay controls and all history-dependent setup evidence");

        var rivalryChange = MatchSetupProjectionIdentity.Create(baseline with
        {
            RivalryMatrixEnabled = true,
            RivalryMatrix = [new RivalryMatrixItem("alpha", "beta", "challenge")]
        }).ChangesSince(identity);
        Require(rivalryChange.HasFlag(MatchSetupProjectionArea.RivalryMatrix)
            && rivalryChange.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && rivalryChange.HasFlag(MatchSetupProjectionArea.SetupFeedback),
            "relationship changes should update controls, summary, and readiness");

        var factoryChange = MatchSetupProjectionIdentity.Create(baseline with { FactoryMode = true })
            .ChangesSince(identity);
        Require(factoryChange.HasFlag(MatchSetupProjectionArea.ModelBehavior)
            && factoryChange.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && factoryChange.HasFlag(MatchSetupProjectionArea.SetupFeedback),
            "Factory changes should update the behavior toggle and every mode-dependent setup projection");

        var sessionChange = MatchSetupProjectionIdentity.Create(baseline with { SessionId = "session-b" })
            .ChangesSince(identity);
        Require(sessionChange.HasFlag(MatchSetupProjectionArea.SetupSummary)
            && sessionChange.HasFlag(MatchSetupProjectionArea.SetupFeedback)
            && sessionChange.HasFlag(MatchSetupProjectionArea.RivalryMatrix),
            "session identity changes should rebuild session-bound controls even when content matches");
    }

    private static void MatchSetupRivalryRefreshPreservesLocalDrafts()
    {
        MatchSetupCoordinator.RivalryMatrixSelection[] previous =
        [
            new("alpha", "beta", "challenge"),
            new("beta", "alpha", "support")
        ];
        MatchSetupCoordinator.RivalryMatrixSelection[] draft =
        [
            new("alpha", "beta", "fact_check"),
            new("beta", "alpha", "support")
        ];
        MatchSetupCoordinator.RivalryMatrixSelection[] external =
        [
            new("alpha", "gamma", "rival"),
            new("beta", "alpha", "steelman"),
            new("gamma", "alpha", "challenge")
        ];

        var reconciled = MatchSetupCoordinator.ReconcileRivalryDraft(
            previousEnabled: true,
            previous,
            draftEnabled: false,
            draft,
            nextEnabled: true,
            external,
            ["alpha", "beta", "gamma"]);

        Require(!reconciled.Enabled,
            "a locally edited enabled toggle should survive an external snapshot refresh");
        Require(reconciled.Selections.Single(item => item.Source == "alpha").Stance == "fact_check",
            "an edited local rule should survive an overlapping authoritative change");
        Require(reconciled.Selections.Single(item => item.Source == "beta").Stance == "steelman",
            "an untouched rule should adopt the new authoritative value");
        Require(reconciled.Selections.Single(item => item.Source == "gamma").Target == "alpha",
            "a newly active authoritative agent rule should appear during reconciliation");
        Require(reconciled.RetainedEdits == 2 && reconciled.Conflicts == 1,
            "draft reconciliation should report retained toggle/rule edits and the overlapping rule conflict");

        var removedTarget = MatchSetupCoordinator.ReconcileRivalryDraft(
            true,
            previous,
            true,
            draft,
            true,
            [new("alpha", "", "neutral")],
            ["alpha"]);
        Require(removedTarget.Selections.Single().Target == "" && removedTarget.InvalidTargets == 1,
            "a retained draft must not point at an agent removed by the authoritative roster");

        var savedDraft = MatchSetupCoordinator.ReconcileRivalryDraft(
            previousEnabled: true,
            previous,
            draftEnabled: false,
            draft,
            nextEnabled: false,
            draft,
            ["alpha", "beta"]);
        Require(!savedDraft.Enabled
            && savedDraft.Selections.Single(item => item.Source == "alpha").Stance == "fact_check"
            && savedDraft.RetainedEdits == 0
            && savedDraft.Conflicts == 0,
            "an authoritative refresh matching the local controls should establish a saved baseline, not report an unsaved draft");
    }

    private static ArenaViewSnapshot MatchSetupSnapshot(string sessionId, string topic)
    {
        var alpha = new AgentState(
            "alpha", "Alpha", "waiting", "Challenge assumptions", "direct", "default", "#40A9FF",
            "model-a", true, false, []);
        var beta = new AgentState(
            "beta", "Beta", "waiting", "Test the evidence", "measured", "default", "#FFCB6B",
            "model-b", true, false, []);
        return SnapshotForOverviewTest(true, "shared-model", "", 0, [], [alpha, beta]) with
        {
            SessionId = sessionId,
            ScenarioTopic = topic,
            ScenarioGlobal = "Produce an auditable comparison.",
            ScenarioGeneratorSeed = "seed-1",
            PersonaGeneratorSeed = "persona-1",
            ProviderHistoryPolicy = "strict"
        };
    }
}
