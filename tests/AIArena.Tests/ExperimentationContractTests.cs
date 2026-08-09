using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AIArena.Core.Models;
using AIArena.Core.Services;

internal static class ExperimentationContractTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    internal static void DeclaresEveryV1SchemaExactlyOnce()
    {
        string[] expected =
        [
            "ai_arena.experiment.v1", "ai_arena.experiment_run.v1", "ai_arena.branch.v1",
            "ai_arena.rubric.v1", "ai_arena.claim_ledger.v1", "ai_arena.memory_trace.v1",
            "ai_arena.scenario_pack.v1", "ai_arena.benchmark_pack.v1",
            "ai_arena.route_proposal.v1", "ai_arena.route_application_receipt.v1",
            "ai_arena.fault_profile.v1", "ai_arena.qa_evidence.v1"
        ];
        Require(ArenaContractSchemas.All.SequenceEqual(expected), "v1 schema identifiers changed");
        Require(ArenaContractSchemas.All.Distinct(StringComparer.Ordinal).Count() == expected.Length, "schema IDs are not unique");
        foreach (var contract in ValidContracts())
        {
            var result = ArenaContractCodec.Validate(contract);
            Require(result.IsValid, $"{contract.Schema} invalid: {Format(result.Issues)}");
        }
    }

    internal static void SerializesCanonicallyAndRoundTripsStrictly()
    {
        var contract = Experiment();
        var json = ArenaContractCodec.Serialize(contract);
        Require(json == ArenaContractCodec.Serialize(contract), "serialization was nondeterministic");
        Require(json.StartsWith("{\"benchmarkPackId\":", StringComparison.Ordinal), "properties were not sorted");
        Require(json.Contains("\"state\":\"observed\"", StringComparison.Ordinal), "enum wire form changed");
        Require(ArenaContractCodec.TryDeserialize<ArenaExperimentContract>(json, out var copy, out var issues), Format(issues));
        Require(copy is not null && ArenaContractCodec.Serialize(copy) == json, "canonical round trip changed payload");
        var unknown = json[..^1] + ",\"sourceContent\":\"private\"}";
        Require(!ArenaContractCodec.TryDeserialize<ArenaExperimentContract>(unknown, out _, out _), "unknown member was accepted");
        var nullItem = json.Replace("\"dimensions\":[{", "\"dimensions\":[null,{", StringComparison.Ordinal);
        Require(!ArenaContractCodec.TryDeserialize<ArenaExperimentContract>(nullItem, out _, out var nullIssues),
            "null contract collection item was accepted");
        Require(nullIssues.Any(issue => issue.Code == "collection.null_item"),
            "null contract collection item did not produce a bounded validation issue");
    }

    internal static void EnforcesObservedInferredAndUnavailableEvidence()
    {
        var contract = Experiment() with
        {
            Evidence =
            [
                new("ev:observed", ArenaEvidenceState.Observed, "No reference."),
                new("ev:inferred", ArenaEvidenceState.Inferred, "No basis."),
                new("ev:unavailable", ArenaEvidenceState.Unavailable, "No limitation.")
            ],
            Dimensions = [new("dimension:model", "model", ["z", "a"])]
        };
        var codes = ArenaContractCodec.Validate(contract).Issues.Select(issue => issue.Code).ToHashSet();
        Require(codes.Contains("evidence.observed_reference"), "observed reference was optional");
        Require(codes.Contains("evidence.inferred_basis"), "inference basis was optional");
        Require(codes.Contains("evidence.unavailable_limitation"), "unavailable limitation was optional");
        Require(codes.Contains("order.nondeterministic"), "unsorted dimension values were accepted");
    }

    internal static void RejectsSecretsAbsolutePathsAndSourceContent()
    {
        var issues = ArenaContractPrivacyRules.InspectJson(
            """{"api":"api_key=supersecret","password":"password=hunter2","github":"ghp_abcdefghijk","aws":"AKIAABCDEFGHIJKLMNOP","bearer":"Bearer abcdefghijklmnop","windows":"C:\\Users\\Cyber\\x","linux":"/root/private/x","mounted":"/mnt/c/private/x","data":"/data/private/x","workspace":"/workspace/project/file","sourceContent":"class X{}"}""");
        Require(issues.Any(issue => issue.Code == "privacy.secret"), "secret was accepted");
        Require(issues.Count(issue => issue.Code == "privacy.secret") >= 5, "credential forms were accepted");
        Require(issues.Count(issue => issue.Code == "privacy.absolute_path") >= 5, "cross-platform absolute paths were accepted");
        Require(issues.Any(issue => issue.Code == "privacy.source_content"), "source content was accepted");
        var qa = Qa() with { Artifacts = [new("artifact:x", "log", "../x.log", HashA, null)] };
        Require(ArenaContractCodec.Validate(qa).Issues.Any(issue => issue.Code == "privacy.relative_path"), "path traversal was accepted");
        Require(ArenaContractPrivacyRules.IsSafeRelativePath("artifacts/qa/run.json"), "safe relative path was rejected");
    }

    internal static void PersistsDeterministicExperimentRunsAndNormalizesRestart()
    {
        var key = ArenaExperimentRunPolicy.CreateCellKey(HashA, HashB, 3);
        Require(key == ArenaExperimentRunPolicy.CreateCellKey(HashA.ToUpperInvariant(), HashB, 3), "cell key was case-sensitive");
        Require(key != ArenaExperimentRunPolicy.CreateCellKey(HashA, HashB, 4), "repetition was omitted from cell key");
        var running = Run(ArenaExperimentRunState.Running, 3, attempts: 1, trialIds: ["trial:one"]);
        Require(ArenaContractCodec.Validate(running).IsValid, "running cell was invalid");
        var recovered = ArenaExperimentRunPolicy.NormalizeAfterRestart(running, At.AddSeconds(2));
        Require(recovered.State == ArenaExperimentRunState.Interrupted, "running cell was not interrupted after restart");
        Require(recovered.InterruptionReason == ArenaExperimentRunPolicy.ProcessRestartReason, "restart reason missing");
        Require(ArenaContractCodec.Validate(recovered).IsValid, "recovered run was invalid");
    }

    internal static void RequiresEvidenceBeforeQaCanSeal()
    {
        var incomplete = Qa() with { Verdict = ArenaQaVerdict.Sealed, IsWorkingTreeClean = false, CleanFullPasses = 1 };
        var codes = ArenaContractCodec.Validate(incomplete).Issues.Select(issue => issue.Code).ToHashSet();
        Require(codes.Contains("qa.clean_tree"), "dirty tree sealed");
        Require(codes.Contains("qa.clean_passes"), "single pass sealed");
        Require(codes.Contains("qa.user_inspection"), "uninspected evidence sealed");

        var sealedQa = SealedQa();
        var validation = ArenaContractCodec.Validate(sealedQa);
        Require(validation.IsValid, Format(validation.Issues));
        var json = ArenaContractCodec.Serialize(sealedQa);
        Require(ArenaContractCodec.TryDeserialize<ArenaQaEvidenceContract>(json, out var copy, out var issues), Format(issues));
        Require(copy is not null && ArenaContractCodec.Serialize(copy) == json, "QA evidence round trip changed");
    }

    internal static void RejectsFakeAndIncompleteSealManifests()
    {
        var fake = SealedQa() with
        {
            Gates = [new("gate:claimed-complete", ArenaQaGateOutcome.Pass, false, 1, new(1, 0, 0, 1), Observed("ev:fake"))],
            SchemaChecks = [new("schema:qa", ArenaContractSchemas.QaEvidence, null, ArenaQaGateOutcome.Pass, Observed("ev:fake-schema"))],
            Performance = [Performance("clean-release-build-duration")]
        };
        var codes = ArenaContractCodec.Validate(fake).Issues.Select(issue => issue.Code).ToHashSet();
        Require(codes.Contains("qa.gate_manifest"), "fabricated gate list sealed");
        Require(codes.Contains("qa.schema_manifest"), "incomplete schema list sealed");
        Require(codes.Contains("qa.performance_manifest"), "incomplete performance list sealed");

        var complete = SealedQa();
        var requiredGate = "pass-02.map-full-suite";
        var missingGate = complete with { Gates = complete.Gates.Remove(complete.Gates.Single(item => item.Id == requiredGate)) };
        Require(ArenaContractCodec.Validate(missingGate).Issues.Any(issue => issue.Code == "qa.gate_manifest"), "missing required suite item sealed");

        var withoutAutomation = complete with
        {
            Artifacts = complete.Artifacts.Remove(complete.Artifacts.Single(item => item.Kind == "automation-tree")),
            Inspection = complete.Inspection with { AutomationArtifactIds = [] }
        };
        Require(ArenaContractCodec.Validate(withoutAutomation).Issues.Any(issue => issue.Code == "qa.artifact_manifest"), "seal without automation artifacts passed");

        var missingMap = SealedQa() with { NestedRepositories = [] };
        Require(ArenaContractCodec.Validate(missingMap).Issues.Any(issue => issue.Code == "qa.repository_manifest"), "missing map provenance sealed");

        var debug = SealedQa() with { Environment = new("Windows", "x64", "10.0.0", "10.0.100", "Debug", false) };
        Require(ArenaContractCodec.Validate(debug).Issues.Any(issue => issue.Code == "qa.release"), "Debug environment sealed");

        var unknownManifest = SealedQa() with { SealManifestId = "ai_arena.qa_seal_manifest.v2" };
        Require(ArenaContractCodec.Validate(unknownManifest).Issues.Any(issue => issue.Code == "qa.manifest"), "unknown seal manifest passed");

        var limitation = complete.AcceptedLimitations[0];
        Require(ArenaContractCodec.Validate(complete with { AcceptedLimitations = complete.AcceptedLimitations.RemoveAt(0) })
            .Issues.Any(issue => issue.Code == "qa.limitation_manifest"), "seal with a deleted required limitation passed");
        Require(ArenaContractCodec.Validate(complete with
            {
                AcceptedLimitations = complete.AcceptedLimitations.SetItem(0, limitation with { Id = "limitation.renamed" })
            }).Issues.Any(issue => issue.Code == "qa.limitation_manifest"), "seal with a renamed required limitation passed");
        Require(ArenaContractCodec.Validate(complete with
            {
                AcceptedLimitations = complete.AcceptedLimitations.SetItem(0, limitation with
                {
                    Evidence = limitation.Evidence with { State = ArenaEvidenceState.Observed }
                })
            }).Issues.Any(issue => issue.Code == "qa.limitation_manifest"), "seal with a limitation promoted to observed evidence passed");
        Require(ArenaContractCodec.Validate(complete with
            {
                AcceptedLimitations = complete.AcceptedLimitations.SetItem(0, limitation with { Summary = "Full interactive coverage was proven." })
            }).Issues.Any(issue => issue.Code == "qa.limitation_manifest"), "seal with misleading limitation summary text passed");
        Require(ArenaContractCodec.Validate(complete with
            {
                AcceptedLimitations = complete.AcceptedLimitations.SetItem(0, limitation with
                {
                    Evidence = limitation.Evidence with
                    {
                        Summary = "Physical interaction was fully observed.",
                        ReferenceId = "artifact:unrelated",
                        Basis = "Inferred from a screenshot.",
                        Limitation = "No limitation remains."
                    }
                })
            }).Issues.Any(issue => issue.Code == "qa.limitation_manifest"), "seal with mutated limitation evidence semantics passed");
        var partialWithAcceptedLimitations = Qa() with { AcceptedLimitations = QaLimitations(userAccepted: true) };
        Require(ArenaContractCodec.Validate(partialWithAcceptedLimitations).Issues.Any(issue => issue.Code == "qa.limitation_manifest"),
            "pre-seal evidence accepted limitations before the sealed verdict");
    }

    internal static void RejectsUnavailableEvidenceReportedAsPass()
    {
        var unavailable = Unavailable("ev:unavailable-pass");
        var qa = SealedQa();
        qa = qa with { Gates = qa.Gates.SetItem(0, qa.Gates[0] with { Evidence = unavailable }) };
        Require(ArenaContractCodec.Validate(qa).Issues.Any(issue => issue.Code == "qa.gate_evidence"), "pass gate accepted unavailable evidence");

        qa = SealedQa();
        qa = qa with { SchemaChecks = qa.SchemaChecks.SetItem(0, qa.SchemaChecks[0] with { Evidence = unavailable }) };
        Require(ArenaContractCodec.Validate(qa).Issues.Any(issue => issue.Code == "qa.schema_evidence"), "pass schema accepted unavailable evidence");

        qa = SealedQa() with { Inspection = SealedQa().Inspection with { Evidence = unavailable } };
        Require(ArenaContractCodec.Validate(qa).Issues.Any(issue => issue.Code == "qa.inspection_evidence"), "accepted inspection used unavailable evidence");
    }

    internal static void RequiresCurrentVisualArtifactProvenance()
    {
        var qa = SealedQa();
        var screenshotIndex = qa.Artifacts.IndexOf(qa.Artifacts.Single(item => item.Kind == "rendered-ui-screenshot"));
        var withoutProvenance = qa with
        {
            Artifacts = qa.Artifacts.SetItem(screenshotIndex, qa.Artifacts[screenshotIndex] with { Provenance = null })
        };
        Require(ArenaContractCodec.Validate(withoutProvenance).Issues.Any(issue => issue.Code == "qa.artifact_provenance"), "provenance-free screenshot sealed");

        var stale = qa.Artifacts[screenshotIndex] with
        {
            Provenance = qa.Artifacts[screenshotIndex].Provenance! with { TreeFingerprint = HashB }
        };
        Require(ArenaContractCodec.Validate(qa with { Artifacts = qa.Artifacts.SetItem(screenshotIndex, stale) })
            .Issues.Any(issue => issue.Code == "qa.artifact_tree"), "stale screenshot sealed");

        var wrongInspectionTree = qa with { Inspection = qa.Inspection with { TreeFingerprint = HashB } };
        Require(ArenaContractCodec.Validate(wrongInspectionTree).Issues.Any(issue => issue.Code == "qa.inspection_tree"), "inspection was not tied to tested tree");
    }

    internal static void RequiresScenarioRouteAndApprovalProvenance()
    {
        var scenario = (ArenaScenarioPackContract)ValidContracts().Single(item => item.Schema == ArenaContractSchemas.ScenarioPack);
        Require(scenario.Invariants.Length == 1 && scenario.Migration is not null, "scenario invariants or migration provenance missing");
        Require(ArenaContractCodec.Validate(scenario).IsValid, "versioned scenario pack invalid");
        Require(ArenaContractCodec.Validate(scenario with { ContentFingerprint = "not-a-hash" })
            .Issues.Any(issue => issue.Code == "hash.invalid"), "scenario content identity was optional");

        var proposal = (ArenaRouteProposalContract)ValidContracts().Single(item => item.Schema == ArenaContractSchemas.RouteProposal);
        Require(ArenaContractCodec.Validate(proposal with { SetupFingerprint = "" }).Issues.Any(issue => issue.Code == "hash.invalid"), "route setup fingerprint was optional");
        var routeJson = JsonNode.Parse(ArenaContractCodec.Serialize(proposal))!.AsObject();
        routeJson["changes"]![0]!["scoreComponents"]![0]!.AsObject().Remove("evidence");
        Require(!ArenaContractCodec.TryDeserialize<ArenaRouteProposalContract>(routeJson.ToJsonString(), out _, out _),
            "old route score without explicit evidence was silently inferred");
        var inferredScore = proposal.Changes[0].ScoreComponents[0] with
        {
            Evidence = new("ev:score-inferred", ArenaEvidenceState.Inferred, "Inferred score.", Basis: "Model estimate.")
        };
        var inferredProposal = proposal with
        {
            Changes = [proposal.Changes[0] with { ScoreComponents = [inferredScore] }]
        };
        Require(ArenaContractCodec.Validate(inferredProposal).Issues.Any(issue => issue.Code == "route.score_evidence"),
            "proposed route accepted an inferred score component");
        var receipt = (ArenaRouteApplicationReceiptContract)ValidContracts().Single(item => item.Schema == ArenaContractSchemas.RouteApplicationReceipt);
        Require(ArenaContractCodec.Validate(receipt).IsValid, "approval/application receipt invalid");
        Require(ArenaContractCodec.Validate(receipt with { ApprovalEvidence = Unavailable("ev:no-approval") })
            .Issues.Any(issue => issue.Code == "route.approval_evidence"), "application receipt accepted unobserved approval");
    }

    private static ImmutableArray<IArenaVersionedContract> ValidContracts()
    {
        var observed = Observed("ev:one");
        ImmutableArray<ArenaScenarioInvariant> scenarioInvariants =
            [new("invariant:turn-budget", "rule:turn-budget", "Turn budget remains bounded.", true)];
        ImmutableArray<ArenaScenarioDefinition> scenarioDefinitions =
            [new("scenario:one", "1.0.0", "Smoke", "match-setup:v2", HashA, "seed-1", 4,
                ["smoke", "verification"], ["invariant:turn-budget"], ["ev:one"])];
        return
        [
            Experiment(),
            Run(ArenaExperimentRunState.Queued, 0, 0, []),
            new ArenaBranchContract(
                ArenaContractSchemas.Branch, "branch:one", At, null, "ParentSession", 2,
                new("message:one", 0, 0, HashA), HashB, 1, "ChildSession", At, [observed]),
            new ArenaRubricContract(
                ArenaContractSchemas.Rubric, "rubric:quality", At, "Quality", "1.0.0",
                [new("evaluator:deterministic", ArenaRubricEvaluatorKind.Deterministic, "profile:deterministic:v1")],
                [new("criterion:quality", "Quality", "Supported result.", 1m, 0m, 5m)], [observed]),
            new ArenaClaimLedgerContract(
                ArenaContractSchemas.ClaimLedger, "ledger:one", At, "experiment:one", "branch:one",
                [new("claim:one", "message:one", "agent:alpha", "The trial completed.", ArenaClaimStatus.Supported,
                    0.9m, ["ev:one"], [], ["reviewer:one"], observed)], [observed]),
            new ArenaMemoryTraceContract(
                ArenaContractSchemas.MemoryTrace, "memory:one", At, "experiment:one", "branch:one",
                [new("memory:entry:one", "agent:alpha", "shared/topic", "Bounded summary.", ArenaMemoryVisibility.Shared,
                    ArenaMemoryOrigin.Turn, At, At, At.AddHours(1), "message:one", "branch:one", null, null, observed)]),
            new ArenaScenarioPackContract(
                ArenaContractSchemas.ScenarioPack, "scenario-pack:one", At, "Scenarios", "1.0.0",
                ArenaExperimentFingerprints.ScenarioPackContent(scenarioInvariants, scenarioDefinitions),
                new("ai_arena.scenario_pack.v0", "0.9.0", HashA, "migrator:scenario:v1", At),
                scenarioInvariants,
                scenarioDefinitions,
                [observed]),
            BenchmarkPack(observed),
            new ArenaRouteProposalContract(
                ArenaContractSchemas.RouteProposal, "route:one", At, "experiment:one", HashA, ArenaRouteProposalStatus.Proposed,
                [new("route:change:one", "agent:alpha", "qwen/4b", "qwen/8b", "Two runs improved score.", true,
                    ["run:one", "run:two"], 2, ArenaEvidenceSufficiency.Sufficient,
                    [new("score:quality", 1m, 0.7m, 0.8m, Observed("ev:score-quality"))],
                    [new("constraint:hardware", ArenaRouteConstraintKind.Hardware, "Hardware budget is supported.", true, Observed("ev:hardware")),
                     new("constraint:capability", ArenaRouteConstraintKind.Capability, "Required chat capability is supported.", true, Observed("ev:capability"))],
                    new("ev:route", ArenaEvidenceState.Inferred, "May improve quality.", Basis: "Two observed runs."))]),
            new ArenaRouteApplicationReceiptContract(
                ArenaContractSchemas.RouteApplicationReceipt, "route-receipt:one", At.AddSeconds(2), "route:one",
                "experiment:one", HashA, "operator:local", At, At.AddSeconds(1),
                [new("route:change:one", "agent:alpha", "qwen/4b", "qwen/8b")], Observed("ev:approval"), [observed]),
            new ArenaFaultProfileContract(
                ArenaContractSchemas.FaultProfile, "fault:one", At, "Timeout", "seed-1",
                [new("fault:injection:one", ArenaFaultTarget.Provider, ArenaFaultKind.Timeout, 1, 250, 50, 1, "Interrupt safely.")],
                [Unavailable("ev:fault")]),
            Qa()
        ];
    }

    private static ArenaExperimentContract Experiment() => new(
        ArenaContractSchemas.Experiment, "experiment:one", At, "Local matrix", ArenaExperimentStatus.Draft,
        "scenario-pack:one", "benchmark:one", ["provider:local"], ["rubric:quality"], ["fault:one"],
        [new("dimension:model", "model", ["qwen/4b", "qwen/8b"])], 2, 4, 1, [], [Observed("ev:experiment")]);

    private static ArenaBenchmarkPackContract BenchmarkPack(ArenaEvidenceAssertion observed)
    {
        const string scenarioPackId = "scenario-pack:one";
        ImmutableArray<ArenaBenchmarkCase> cases =
            [new("case:one", "scenario:one", ["rubric:quality"], 2, ["chat", "reasoning"], ["ev:one"])];
        return new(
            ArenaContractSchemas.BenchmarkPack,
            "benchmark:one",
            At,
            "Benchmark",
            "1.0.0",
            ArenaExperimentFingerprints.BenchmarkPackContent(scenarioPackId, cases),
            null,
            scenarioPackId,
            cases,
            [observed]);
    }

    private static ArenaExperimentRunContract Run(
        ArenaExperimentRunState state,
        int repetition,
        int attempts,
        ImmutableArray<string> trialIds)
    {
        var reason = state == ArenaExperimentRunState.Interrupted ? ArenaExperimentRunPolicy.ProcessRestartReason : null;
        return new(
            ArenaContractSchemas.ExperimentRun, $"run:{state.ToString().ToLowerInvariant()}", At, "experiment:one",
            HashA, HashB, repetition, ArenaExperimentRunPolicy.CreateCellKey(HashA, HashB, repetition), state, attempts,
            At.AddSeconds(attempts), trialIds, reason, [Observed("ev:run")]);
    }

    private static ArenaQaEvidenceContract Qa() => new(
        ArenaContractSchemas.QaEvidence, "qa:one", At, new string('1', 40), HashA, ArenaQaSealManifestV1.Id, true,
        [new("map", new string('2', 40), HashB, true)], At, At.AddSeconds(10),
        ArenaQaVerdict.Partial, 1,
        new("Windows", "x64", "10.0.0", "10.0.100", "Release", true),
        [new("dotnet", "10.0.100"), new("powershell", "7.5.2")],
        [new("gate:core", ArenaQaGateOutcome.Pass, true, 2_000, new(5, 0, 0, 5), Observed("ev:gate"))],
        [new("artifact:automation", "automation-tree", "automation/tree.json", HashA,
                new(HashA, At.AddSeconds(2), "dark-blue", 1500, 960, 1m, "setup-needed", null, null)),
            new("artifact:screenshot", "rendered-ui-screenshot", "screenshots/view.png", HashB,
                new(HashA, At.AddSeconds(2), "dark-blue", 1500, 960, 1m, "setup-needed", "artifact:automation", null))],
        [Performance("clean-release-build-duration")],
        [new("schema:experiment", ArenaContractSchemas.Experiment, null, ArenaQaGateOutcome.Pass, Observed("ev:schema"))],
        new(false, ArenaEvidenceState.Unavailable, [], [], "No live provider configured."), QaLimitations(userAccepted: false),
        new(false, null, null, [], [], Unavailable("ev:inspection")), [Observed("ev:qa")]);

    private static ArenaQaEvidenceContract SealedQa()
    {
        var qa = Qa();
        var gates = ArenaQaSealManifestV1.RequiredGateIds(ArenaQaSealManifestV1.RequiredCleanPasses)
            .Select((id, index) => new ArenaQaGateEvidence(
                id,
                ArenaQaGateOutcome.Pass,
                true,
                100 + index,
                new(1, 0, 0, 1),
                Observed($"ev:gate:{index}")))
            .ToImmutableArray();
        var schemas = ArenaQaSealManifestV1.RequiredSchemaIds
            .Select((schema, index) => new ArenaQaSchemaCheck(
                $"schema:check:{index}", schema, null, ArenaQaGateOutcome.Pass, Observed($"ev:schema:{index}")))
            .ToImmutableArray();
        var performance = ArenaQaSealManifestV1.RequiredPerformanceMetrics
            .Select(Performance)
            .ToImmutableArray();
        return qa with
        {
            Verdict = ArenaQaVerdict.Sealed,
            CleanFullPasses = ArenaQaSealManifestV1.RequiredCleanPasses,
            Gates = gates,
            SchemaChecks = schemas,
            Performance = performance,
            AcceptedLimitations = QaLimitations(userAccepted: true),
            Inspection = new(true, At.AddSeconds(9), HashA, ["artifact:screenshot"], ["artifact:automation"], Observed("ev:inspection"))
        };
    }

    private static ImmutableArray<ArenaQaAcceptedLimitation> QaLimitations(bool userAccepted) =>
        [.. ArenaQaSealManifestV1.RequiredLimitations.Select(requirement => new ArenaQaAcceptedLimitation(
            requirement.Id,
            requirement.Summary,
            userAccepted,
            new(
                requirement.EvidenceId,
                ArenaEvidenceState.Unavailable,
                requirement.EvidenceSummary,
                requirement.ReferenceId,
                null,
                requirement.EvidenceLimitation)))];

    private static ArenaQaPerformanceMeasurement Performance(string metric) => new(
        $"performance:{metric}", metric, 100m, "milliseconds", ArenaQaThresholdKind.Maximum, 200m,
        Observed($"ev:performance:{metric}"));

    private static ArenaEvidenceAssertion Observed(string id) =>
        new(id, ArenaEvidenceState.Observed, "Recorded by deterministic local QA.", "artifact:screenshot");

    private static ArenaEvidenceAssertion Unavailable(string id) =>
        new(id, ArenaEvidenceState.Unavailable, "Evidence unavailable.", Limitation: "No compatible endpoint configured.");

    private static string Format(ImmutableArray<ArenaContractValidationIssue> issues) =>
        string.Join(" | ", issues.Select(issue => $"{issue.Code} {issue.Path}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
