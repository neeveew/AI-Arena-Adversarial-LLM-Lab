using System.Collections.Immutable;
using System.Windows.Automation;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ResilienceLabsRegisterResponsiveAccessibleFeatures()
    {
        Require(!FaultInjectionLabControl.UsesCompactLayout(960)
                && FaultInjectionLabControl.UsesCompactLayout(620)
                && !ModelRoutingOptimizerControl.UsesCompactLayout(960)
                && ModelRoutingOptimizerControl.UsesCompactLayout(620),
            "resilience feature responsive breakpoints changed");

        RunStaTest(() =>
        {
            var host = new ExperimentLabControl();
            var fault = new FaultInjectionLabControl();
            var routing = new ModelRoutingOptimizerControl();
            host.RegisterFeature(fault.FeatureRegistration);
            host.RegisterFeature(routing.FeatureRegistration);
            Require(host.RegisteredFeatures.Select(item => item.Key).TakeLast(2).SequenceEqual(["fault-injection", "routing-optimizer"]),
                "resilience features did not register independently through Experiment Lab");
            Require(AutomationProperties.GetName(fault) == "Fault-Injection Lab"
                    && AutomationProperties.GetName(routing) == "Model Routing Optimizer",
                "resilience roots lost automation names");
            fault.ApplyResponsiveLayout(compact: true);
            fault.ApplyResponsiveLayout(compact: false);
            routing.ApplyResponsiveLayout(compact: true);
            routing.ApplyResponsiveLayout(compact: false);
        });

        var faultXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/FaultInjectionLabControl.xaml"));
        var routeXaml = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/UI/Controls/ModelRoutingOptimizerControl.xaml"));
        Require(faultXaml.Contains("DynamicResource CardBrush", StringComparison.Ordinal)
                && faultXaml.Contains("AutomationProperties.Name=\"Arm fault profile\"", StringComparison.Ordinal)
                && routeXaml.Contains("DynamicResource CardBrush", StringComparison.Ordinal)
                && routeXaml.Contains("AutomationProperties.Name=\"Apply explicitly approved route\"", StringComparison.Ordinal)
                && !faultXaml.Contains("Storyboard", StringComparison.Ordinal)
                && !routeXaml.Contains("Storyboard", StringComparison.Ordinal),
            "resilience controls lost theme, accessibility, or reduced-motion static contracts");
    }

    static void FaultLabArmsRunsAndDisarmsEveryBoundedKind()
    {
        var at = new DateTimeOffset(2037, 1, 2, 3, 4, 5, TimeSpan.Zero);
        foreach (var kind in Enum.GetValues<ArenaFaultKind>())
        {
            var (profile, options) = FaultInjectionLabCoordinator.BuildProfile(
                new(kind, "deterministic-seed", "3", "40", "75", "2", "4"),
                at);
            Require(profile.Injections.Single().Kind == kind
                    && profile.Injections.Single().AtSequence == 3
                    && profile.Injections.Single().DurationMilliseconds == 40
                    && profile.Injections.Single().Intensity == 75
                    && profile.Injections.Single().MaxOccurrences == 2
                    && options.MaximumConcurrentRequests == 4
                    && ArenaContractCodec.Validate(profile).IsValid,
                $"fault lab did not preserve bounded {kind} inputs");
        }

        RunStaTest(() =>
        {
            var control = new FaultInjectionLabControl();
            control.SetInput(new(ArenaFaultKind.Timeout, "probe-seed", "0", "0", "100", "1", "2"));
            var provider = new ResilienceRecordingProviderClient();
            using var coordinator = new FaultInjectionLabCoordinator(
                control,
                provider,
                () => ResilienceConfig("model-current"),
                new ResilienceFixedTimeProvider(at));
            coordinator.ArmAsync().GetAwaiter().GetResult();
            Require(coordinator.IsArmed && control.CanRunProbe, "fault profile did not enter the armed lifecycle");
            coordinator.RunProbeAsync().GetAwaiter().GetResult();
            Require(control.ObservationCount == 1
                    && provider.ChatCalls == 0
                    && !control.Status.Contains("recovered", StringComparison.OrdinalIgnoreCase),
                "fault probe did not retain content-free cause evidence or fabricated recovery");
            coordinator.DisarmAsync().GetAwaiter().GetResult();
            Require(!coordinator.IsArmed && !control.CanRunProbe, "fault profile did not disarm safely");
        });
    }

    static void RoutingOptimizerConsumesPersistedObservedEvidenceAndRequiresApproval()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-routing-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var at = new DateTimeOffset(2037, 2, 3, 4, 5, 6, TimeSpan.Zero);
                var experiment = new ArenaExperimentContract(
                    ArenaContractSchemas.Experiment,
                    "experiment:routing-ui",
                    at,
                    "Observed local routing comparison",
                    ArenaExperimentStatus.Completed,
                    "scenario-pack:routing-ui",
                    null,
                    ["provider:candidate", "provider:current"],
                    ["rubric:routing-quality"],
                    [],
                    [new("dimension:routing-temperature", "temperature", ["0.7"])],
                    2,
                    1,
                    2,
                    [],
                    []);
                var expansion = ExperimentExpander.Expand(experiment);
                var runStore = new ExperimentRunStore(root);
                var rubricStore = new ArenaRubricStore(root);
                var packStore = new ArenaExperimentPackStore(root);
                ImmutableArray<ArenaScenarioInvariant> invariants =
                    [new("invariant:routing-complete", "rule:routing-complete", "Trial reaches a terminal state.", true)];
                ImmutableArray<ArenaScenarioDefinition> scenarios =
                [
                    new(
                        "scenario:routing-ui",
                        "1.0.0",
                        "Routing evidence scenario",
                        "session:active",
                        new string('a', 64),
                        "A bounded routing evidence seed.",
                        1,
                        ["routing"],
                        ["invariant:routing-complete"],
                        ["evidence:routing-pack"])
                ];
                var scenarioPack = new ArenaScenarioPackContract(
                    ArenaContractSchemas.ScenarioPack,
                    "scenario-pack:routing-ui",
                    at,
                    "Routing scenarios",
                    "1.0.0",
                    ArenaExperimentFingerprints.ScenarioPackContent(invariants, scenarios),
                    null,
                    invariants,
                    scenarios,
                    [ResilienceObserved("evidence:routing-pack")]);
                Require(packStore.SaveScenarioPackAsync(scenarioPack).GetAwaiter().GetResult().Succeeded, "routing scenario pack fixture did not persist");
                var rubric = new ArenaRubricContract(
                    ArenaContractSchemas.Rubric,
                    "rubric:routing-quality",
                    at,
                    "Routing quality",
                    "1.0.0",
                    [new("evaluator:observed", ArenaRubricEvaluatorKind.Human, null)],
                    [new("quality", "Quality", "Observed bounded local quality.", 1m, 0m, 10m)],
                    [ResilienceObserved("evidence:routing-rubric")]);
                Require(rubricStore.SaveRubricAsync(rubric).GetAwaiter().GetResult().Succeeded, "routing rubric fixture did not persist");

                var rubricService = new ArenaRubricService();
                foreach (var cell in expansion.Cells)
                {
                    var isCandidate = cell.ProviderProfileId == "provider:candidate";
                    var score = isCandidate ? 8.5m + (cell.Repetition * 0.5m) : 3.5m + (cell.Repetition * 0.5m);
                    var run = new ArenaExperimentRunContract(
                        ArenaContractSchemas.ExperimentRun,
                        cell.RunId,
                        at,
                        cell.ExperimentId,
                        cell.ExperimentFingerprint,
                        cell.VariantFingerprint,
                        cell.Repetition,
                        cell.CellKey,
                        ArenaExperimentRunState.Completed,
                        1,
                        at.AddSeconds(1),
                        [$"trial:{cell.RunId[4..]}"],
                        null,
                        [ResilienceObserved($"evidence:{cell.RunId[4..]}")]);
                    Require(runStore.SaveAsync(run).GetAwaiter().GetResult().Succeeded, "routing run fixture did not persist");
                    var result = rubricService.CreateSingleSubjectResult(
                        rubric,
                        $"evaluation:{cell.RunId[4..]}",
                        run.Id,
                        [new(
                            $"result:{cell.RunId[4..]}",
                            "evaluator:observed",
                            ArenaRubricJudgmentSource.Human,
                            "reviewer:local",
                            null,
                            [new("quality", score, null, ResilienceObserved($"score:{cell.RunId[4..]}"))],
                            ResilienceObserved($"provenance:{cell.RunId[4..]}"))],
                        at,
                        at.AddSeconds(2),
                        [ResilienceObserved($"evaluation-evidence:{cell.RunId[4..]}")]);
                    Require(rubricStore.SaveResultAsync(result).GetAwaiter().GetResult().Succeeded, "routing result fixture did not persist");
                }

                var constraints = new Dictionary<string, IReadOnlyList<ArenaRouteConstraintEvidence>>(StringComparer.Ordinal);
                var source = new PersistedRoutingEvidenceSource(
                    runStore,
                    rubricStore,
                    packStore,
                    _ => Task.FromResult<PersistedRoutingEvidenceContext?>(new(
                        experiment,
                        new string('a', 64),
                        "scenario:routing-ui",
                        "alpha",
                        "model-current",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["provider:current"] = "model-current",
                            ["provider:candidate"] = "model-candidate"
                        },
                        constraints)),
                    new ResilienceFixedTimeProvider(at.AddMinutes(1)));
                var loaded = source.LoadAsync().GetAwaiter().GetResult();
                Require(loaded.IsAvailable && loaded.CompatibleRuns == 4 && loaded.CompatibleEvaluations == 4,
                    "persisted routing source did not load exact compatible observed evidence");
                var trialCandidate = loaded.Request!.Targets.Single().Candidates.Single(item => item.ModelId == "model-candidate");
                Require(trialCandidate.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Hardware
                            && item.Evidence.State == ArenaEvidenceState.Observed
                            && expansion.Cells.Any(cell => cell.RunId == item.Evidence.ReferenceId))
                        && trialCandidate.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Capability
                            && item.Evidence.State == ArenaEvidenceState.Observed
                            && expansion.Cells.Any(cell => cell.RunId == item.Evidence.ReferenceId)),
                    "compatible completed trials did not derive narrow run-referenced execution constraints");

                var strictSource = new PersistedRoutingEvidenceSource(
                    runStore,
                    rubricStore,
                    packStore,
                    _ => Task.FromResult<PersistedRoutingEvidenceContext?>(new(
                        experiment,
                        new string('a', 64),
                        "scenario:routing-ui",
                        "alpha",
                        "model-current",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["provider:current"] = "model-current",
                            ["provider:candidate"] = "model-candidate"
                        },
                        new Dictionary<string, IReadOnlyList<ArenaRouteConstraintEvidence>>(StringComparer.Ordinal)
                        {
                            ["model-candidate"] =
                            [
                                new(
                                    "constraint:candidate:policy",
                                    ArenaRouteConstraintKind.Policy,
                                    "Observed local policy rejected this candidate route.",
                                    false,
                                    ResilienceObserved("evidence:candidate:policy"))
                            ]
                        })),
                    new ResilienceFixedTimeProvider(at.AddMinutes(1)));
                var strictProposal = ArenaModelRoutingOptimizer.Propose(strictSource.LoadAsync().GetAwaiter().GetResult().Request!);
                Require(strictProposal.Status != ArenaRouteProposalStatus.Proposed
                        && strictProposal.Changes.Single().Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Policy && item.Satisfied == false),
                    "caller-supplied stricter observed constraint was not merged into trial-derived constraints");

                ArenaRouteProposalContract? appliedProposal = null;
                var control = new ModelRoutingOptimizerControl();
                using var coordinator = new ModelRoutingOptimizerCoordinator(
                    control,
                    source,
                    (proposal, approver, approvedAt, _) =>
                    {
                        appliedProposal = proposal;
                        var changes = proposal.Changes.Select(change => new ArenaAppliedRouteChange(
                            change.Id,
                            change.AgentId,
                            change.CurrentModelId,
                            change.ProposedModelId)).ToImmutableArray();
                        var receipt = new ArenaRouteApplicationReceiptContract(
                            ArenaContractSchemas.RouteApplicationReceipt,
                            "route-receipt:routing-ui",
                            approvedAt,
                            proposal.Id,
                            proposal.ExperimentId,
                            proposal.SetupFingerprint,
                            approver,
                            approvedAt,
                            approvedAt,
                            changes,
                            ResilienceObserved("approval:routing-ui"),
                            [ResilienceObserved("application:routing-ui")]);
                        return Task.FromResult(receipt);
                    },
                    new ResilienceFixedTimeProvider(at.AddMinutes(2)));
                RunExperimentDispatcherTask(async () =>
                {
                    await coordinator.RefreshEvidenceAsync();
                    await coordinator.BuildProposalAsync();
                    Require(coordinator.CurrentProposal?.Status == ArenaRouteProposalStatus.Proposed
                            && control.ProposalItemCount == 1
                            && !control.CanApply
                            && appliedProposal is null,
                        "optimizer mutated routing or bypassed explicit approval");
                    control.SetExplicitApproval(true);
                    Require(control.CanApply, "explicit proposal approval did not enable the separate apply action");
                    await coordinator.ApplyApprovedAsync();
                    Require(appliedProposal is not null
                            && coordinator.LastReceipt is not null
                            && control.Status.Contains("process-only", StringComparison.OrdinalIgnoreCase)
                            && !control.Status.Contains(root, StringComparison.OrdinalIgnoreCase),
                        "approved application did not return a clearly process-only receipt boundary");
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    static void RoutingEvidenceRejectsLogicalRunSelfPairs()
    {
        var runReferences = new HashSet<string>(["run:self", "trial:self"], StringComparer.Ordinal);
        var normalSelfPair = ResiliencePairResult(
            ["run:self", "trial:self"],
            blindReveal: null);
        var blindSelfPair = ResiliencePairResult(
            ["run:self", "trial:self"],
            new("trial:self", "run:self"));
        var normalCrossRun = ResiliencePairResult(
            ["run:other", "trial:self"],
            blindReveal: null);
        var blindCrossRun = ResiliencePairResult(
            ["run:self", "run:other"],
            new("run:self", "run:other"));

        Require(PersistedRoutingEvidenceSource.SubjectSide(normalSelfPair, runReferences) == 0
                && PersistedRoutingEvidenceSource.SubjectSide(blindSelfPair, runReferences) == 0,
            "routing evidence treated two aliases of one logical run as opposing pairwise subjects");
        Require(PersistedRoutingEvidenceSource.SubjectSide(normalCrossRun, runReferences) == 2
                && PersistedRoutingEvidenceSource.SubjectSide(blindCrossRun, runReferences) == 1,
            "routing evidence rejected a pair with exactly one matching logical-run side");
    }

    static void RouteApplicationBoundarySerializesTracksAndRefreshes()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-route-boundary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var approvedAt = new DateTimeOffset(2038, 3, 4, 5, 6, 7, TimeSpan.Zero);
                var store = new AIArena.Core.Persistence.SessionStore(root);
                var snapshot = AIArena.Core.Persistence.SessionStore.CreateDefaultSnapshot();
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = ResilienceConfig("model-current");
                store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
                var persisted = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
                var proposal = ResilienceRouteProposal(
                    ResilienceSetupFingerprint(persisted),
                    "alpha",
                    approvedAt);
                var service = new ArenaRouteApplicationService(store, new ResilienceFixedTimeProvider(approvedAt.AddSeconds(1)));

                RunExperimentDispatcherTask(async () =>
                {
                    using var heldProviderGate = new SemaphoreSlim(1, 1);
                    await heldProviderGate.WaitAsync();
                    var busy = false;
                    var autoChat = false;
                    var refreshes = 0;
                    var trackedCalls = 0;
                    var operations = ResilienceArenaOperations(heldProviderGate, () => autoChat);
                    var boundary = ModelRoutingOptimizerCoordinator.CreateSessionApplicationBoundary(
                        service,
                        () => "active",
                        heldProviderGate,
                        () => busy,
                        () => autoChat,
                        action =>
                        {
                            trackedCalls++;
                            return operations.TrackAsync(action);
                        },
                        async (sessionId, cancellationToken) =>
                        {
                            _ = await store.LoadSnapshotAsync(sessionId, cancellationToken)
                                ?? throw new InvalidOperationException("refreshed route session was unavailable");
                            refreshes++;
                        });

                    var apply = boundary(proposal, "operator:local", approvedAt, CancellationToken.None);
                    await Task.Yield();
                    var unchangedWhileHeld = await store.LoadSnapshotAsync("active");
                    Require(!apply.IsCompleted
                            && unchangedWhileHeld!.Configs[ModelProviderRouting.SharedConfigKey].Model == "model-current",
                        "approved route crossed the shared mutation gate while a provider turn held it");
                    heldProviderGate.Release();
                    var receipt = await apply.WaitAsync(TimeSpan.FromSeconds(5));
                    var updated = await store.LoadSnapshotAsync("active");
                    Require(receipt.Changes.Single().AgentId == "alpha"
                            && updated!.Configs["alpha"].Model == "model-candidate"
                            && trackedCalls == 1
                            && refreshes == 1,
                        "tracked route application did not persist once, return its receipt, and refresh the active session once");

                    busy = true;
                    var rejectedBusy = false;
                    try
                    {
                        await boundary(proposal, "operator:local", approvedAt, CancellationToken.None);
                    }
                    catch (ArenaRouteApplicationBusyException)
                    {
                        rejectedBusy = true;
                    }
                    Require(rejectedBusy && trackedCalls == 1,
                        "route application entered the tracked mutation lifecycle while the arena was already busy");
                    busy = false;

                    autoChat = true;
                    var rejectedAutoChat = false;
                    try
                    {
                        await boundary(proposal, "operator:local", approvedAt, CancellationToken.None);
                    }
                    catch (ArenaRouteApplicationBusyException)
                    {
                        rejectedAutoChat = true;
                    }
                    Require(rejectedAutoChat && trackedCalls == 1,
                        "route application entered the tracked mutation lifecycle while Auto Chat was active");
                    autoChat = false;

                    var latest = await store.LoadSnapshotAsync("active") ?? throw new InvalidOperationException("route fixture disappeared");
                    var shutdownProposal = ResilienceRouteProposal(
                        ResilienceSetupFingerprint(latest),
                        "beta",
                        approvedAt.AddMinutes(1));
                    using var shutdownGate = new SemaphoreSlim(0, 1);
                    var shutdownOperations = ResilienceArenaOperations(shutdownGate, () => false);
                    var shutdownBoundary = ModelRoutingOptimizerCoordinator.CreateSessionApplicationBoundary(
                        new ArenaRouteApplicationService(store, new ResilienceFixedTimeProvider(approvedAt.AddMinutes(1).AddSeconds(1))),
                        () => "active",
                        shutdownGate,
                        () => false,
                        () => false,
                        shutdownOperations.TrackAsync,
                        (_, _) => Task.CompletedTask);
                    var pending = shutdownBoundary(
                        shutdownProposal,
                        "operator:local",
                        approvedAt.AddMinutes(1),
                        CancellationToken.None);
                    await Task.Yield();
                    Require(!pending.IsCompleted, "held-provider shutdown fixture never entered the tracked mutation wait");
                    await shutdownOperations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var cancelledByShutdown = false;
                    try
                    {
                        await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException)
                    {
                        cancelledByShutdown = true;
                    }
                    var afterShutdown = await store.LoadSnapshotAsync("active");
                    Require(cancelledByShutdown && !afterShutdown!.Configs.ContainsKey("beta"),
                        "shutdown drain completed without cancelling and containing the queued route mutation");
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static ArenaEvidenceAssertion ResilienceObserved(string id) =>
        new(id, ArenaEvidenceState.Observed, "Observed bounded resilience fixture evidence.", ReferenceId: id);

    private static ArenaRubricEvaluationResultContract ResiliencePairResult(
        ImmutableArray<string> subjectReferenceIds,
        ArenaBlindPairwiseReveal? blindReveal) => new(
            ArenaRubricResultSchemas.RubricResult,
            $"evaluation:{Guid.NewGuid():N}",
            new DateTimeOffset(2037, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2037, 1, 1, 0, 0, 1, TimeSpan.Zero),
            "rubric:routing-pair",
            "1.0.0",
            subjectReferenceIds,
            blindReveal is not null,
            blindReveal,
            [],
            [],
            [],
            [],
            []);

    private static ArenaRouteProposalContract ResilienceRouteProposal(
        string setupFingerprint,
        string agentId,
        DateTimeOffset createdAtUtc) =>
        ArenaModelRoutingOptimizer.Propose(new(
            $"route-proposal:{Guid.NewGuid():N}",
            createdAtUtc,
            "experiment:route-boundary",
            setupFingerprint,
            [new(
                $"route-target:{agentId}",
                agentId,
                "model-current",
                [new("quality", 1m)],
                [
                    ResilienceRouteCandidate("model-current", "current", setupFingerprint, [0.40m, 0.45m]),
                    ResilienceRouteCandidate("model-candidate", "candidate", setupFingerprint, [0.80m, 0.85m])
                ])]));

    private static string ResilienceSetupFingerprint(ArenaSnapshot snapshot) =>
        (string)(typeof(AIArena.Core.Persistence.SessionStore)
            .GetMethod("SetupFingerprint", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(null, [snapshot])
            ?? throw new InvalidOperationException("Session setup fingerprint boundary was unavailable to the WPF regression."));

    private static ArenaRouteCandidateEvidence ResilienceRouteCandidate(
        string model,
        string prefix,
        string setupFingerprint,
        decimal[] scores) => new(
            model,
            [.. scores.Select((score, index) => new ArenaRouteSampleEvidence(
                $"run:{prefix}:{index}",
                setupFingerprint,
                [new("quality", score, ResilienceObserved($"evidence:{prefix}:score:{index}"))]))],
            [
                new(
                    $"constraint:{prefix}:hardware",
                    ArenaRouteConstraintKind.Hardware,
                    "Observed compatible local hardware.",
                    true,
                    ResilienceObserved($"evidence:{prefix}:hardware")),
                new(
                    $"constraint:{prefix}:capability",
                    ArenaRouteConstraintKind.Capability,
                    "Observed compatible chat capability.",
                    true,
                    ResilienceObserved($"evidence:{prefix}:capability"))
            ]);

    private static ArenaOperationCoordinator ResilienceArenaOperations(
        SemaphoreSlim operationLock,
        Func<bool> autoChatRunning) => new(
            operationLock,
            new TextBlock(),
            new TextBlock(),
            new Button(),
            new Button(),
            new Button(),
            new Button(),
            new Button(),
            [],
            () => false,
            _ => { },
            autoChatRunning,
            (_, _) => { },
            (_, _) => { },
            (_, _) => { },
            _ => { },
            () => { },
            _ => { },
            _ => { },
            _ => { },
            _ => { });

    private static ModelProviderConfig ResilienceConfig(string model) => new()
    {
        BaseUrl = "http://127.0.0.1:65535/v1",
        ApiToken = "private-token",
        Model = model
    };

    private sealed class ResilienceFixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ResilienceRecordingProviderClient : IModelProviderClient, IStreamingModelProviderClient
    {
        internal int ChatCalls { get; private set; }

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            ChatCalls++;
            return Task.FromResult(new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                "bounded",
                "",
                1,
                1,
                1,
                2,
                "",
                DateTimeOffset.UtcNow));
        }

        public Task<ModelCompletionResult> CompleteChatStreamingAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress,
            CancellationToken cancellationToken = default) =>
            CompleteChatAsync(config, messages, cancellationToken);
    }
}
