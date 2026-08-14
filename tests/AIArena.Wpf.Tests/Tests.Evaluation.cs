using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;

internal static partial class Program
{
    private static void ArenaEvaluationCapturesRepeatableSecretFreeEvidence()
    {
        const string secret = "secret-api-token-that-must-not-persist";
        const string messageBody = "MESSAGE-BODY-MUST-NOT-PERSIST";
        var capturedAt = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
        var snapshot = EvaluationSnapshot(
            "model-a",
            [
                EvaluationMessage(1, "alpha", messageBody, 900, 120, 31),
                EvaluationMessage(2, "beta", "However, test the evidence and assumption.", 1100, 140, 29),
                EvaluationMessage(3, "gamma", "Evidence is incomplete; verify the risk.", 1000, 130, 30)
            ],
            apiToken: secret,
            baseUrl: $"http://user:{secret}@127.0.0.1:8080/v1?api_key={secret}#fragment");
        var service = new ArenaEvaluationService();

        var first = service.Capture("evaluation-session", snapshot, capturedAt);
        var second = service.Capture("evaluation-session", snapshot, capturedAt.AddMinutes(1));
        var exactDuplicate = service.Capture("evaluation-session", snapshot, capturedAt);
        var qa = service.EvaluateQa(
            first,
            first,
            new ArenaRuntimeQaEvidence(InterruptionRecovered: true, ProviderReconnectSucceeded: true));
        var export = service.ExportJson(first, service.Compare(first, first), qa);

        Require(first.Schema == AIArena.Wpf.ArenaEvaluationSchemas.Evaluation, "evaluation record should expose a versioned schema");
        Require(first.RunFingerprint == second.RunFingerprint,
            "equivalent snapshots should retain one deterministic evidence fingerprint independent of capture time");
        Require(first.RunId != second.RunId && first.RunId == exactDuplicate.RunId,
            "separately timed identical trials should receive distinct capture IDs while an exact duplicate remains stable");
        Require(service.NormalizeForStorage(first)?.RunId == first.RunId,
            "normalizing the same captured trial should recompute its identical capture-specific run ID");
        Require(first.ExactSetupFingerprint.Length == 64 && first.ScenarioFingerprint.Length == 64,
            "evaluation should capture exact and model-neutral setup fingerprints");
        Require(first.Evidence.ModelTurns == 3
            && first.Evidence.LatencySamples == 3
            && first.Evidence.UsageSamples == 3
            && first.Evidence.ThroughputSamples == 3,
            "evaluation should retain aggregate telemetry evidence counts");
        Require(first.Models.Count == 1 && first.Models[0].Model == "model-a" && first.Models[0].Turns == 3,
            "evaluation should aggregate telemetry by safe model identity");
        Require(first.Metrics.AverageLatencyMs == 1000 && first.Metrics.AverageTokensPerSecond == 30,
            "evaluation should calculate stable aggregate telemetry");

        var tokenEvidence = service.Capture(
            "token-evidence",
            EvaluationSnapshot(
                "model-token-evidence",
                [
                    EvaluationMessage(1, "alpha", "Completion-only telemetry.", 800, 50, 20, promptTokens: 0, totalTokens: 0),
                    EvaluationMessage(2, "beta", "Total and prompt fallback.", 900, 0, 20, promptTokens: 100, totalTokens: 140),
                    EvaluationMessage(3, "gamma", "Total without prompt is ambiguous.", 1000, 0, 20, promptTokens: 0, totalTokens: 140)
                ]),
            capturedAt);
        Require(tokenEvidence.Evidence.UsageSamples == 2
            && tokenEvidence.Metrics.AverageGeneratedTokens == 45
            && tokenEvidence.Models.Single().AverageGeneratedTokens == 45,
            "generated-token evidence should prefer completion tokens, fall back to total minus prompt, and exclude total-only ambiguity");

        var package = MatchSetupPackageCodec.Parse(first.PortableSetupJson);
        Require(package.Ok && package.Package is not null,
            "evaluation should retain a replayable exact Match Setup package");
        Require(first.PortableSetupJson.Contains("model-a", StringComparison.Ordinal)
            && first.PortableSetupJson.Contains("127.0.0.1:8080", StringComparison.Ordinal)
            && !first.PortableSetupJson.Contains("user:", StringComparison.Ordinal)
            && !first.PortableSetupJson.Contains("api_key", StringComparison.OrdinalIgnoreCase),
            "replay package should retain non-secret provider assignment while stripping endpoint credentials and query data");
        Require(!export.Contains(secret, StringComparison.Ordinal)
            && !export.Contains(messageBody, StringComparison.Ordinal)
            && !export.Contains("apiToken", StringComparison.OrdinalIgnoreCase)
            && !export.Contains("api_token", StringComparison.OrdinalIgnoreCase),
            "evaluation export must not retain transcript bodies or API credentials");

        const string sessionMarker = "SESSION-MARKER-NOT-FOR-EVIDENCE";
        const string steeringMarker = "STEERING-MARKER-NOT-FOR-EVIDENCE";
        const string personaMarker = "PERSONA-MARKER-NOT-FOR-EVIDENCE";
        const string modelPathMarker = "MODEL-PATH-MARKER-NOT-FOR-EVIDENCE";
        var privateModelPath = $@"C:\Models\{modelPathMarker}\qwen3-safe.gguf";
        var separatedSetup = EvaluationSnapshot(
            privateModelPath,
            [
                EvaluationMessage(1, "alpha", "Aggregate-only export.", 800, 80, 24, model: privateModelPath),
                EvaluationMessage(2, "beta", "Replay stays separate.", 900, 90, 25, model: privateModelPath)
            ]);
        separatedSetup.Engine.Steering.Topic = steeringMarker;
        separatedSetup.Engine.Agents[0].Persona = personaMarker;
        var separatedRecord = service.Capture(sessionMarker, separatedSetup, capturedAt);
        var separatedExport = service.ExportJson(separatedRecord);
        Require(!separatedExport.Contains(sessionMarker, StringComparison.Ordinal)
            && !separatedExport.Contains(steeringMarker, StringComparison.Ordinal)
            && !separatedExport.Contains(personaMarker, StringComparison.Ordinal)
            && !separatedExport.Contains(modelPathMarker, StringComparison.Ordinal)
            && !separatedExport.Contains("portableSetupJson", StringComparison.OrdinalIgnoreCase)
            && !separatedExport.Contains("sessionId", StringComparison.OrdinalIgnoreCase),
            "Copy Evidence payloads must omit session identity and the full replay setup, including steering, persona, and local model path details");
        Require(separatedExport.Contains("qwen3-safe.gguf", StringComparison.Ordinal)
            && separatedRecord.PortableSetupJson.Contains(sessionMarker, StringComparison.Ordinal)
            && separatedRecord.PortableSetupJson.Contains(steeringMarker, StringComparison.Ordinal)
            && separatedRecord.PortableSetupJson.Contains(personaMarker, StringComparison.Ordinal)
            && separatedRecord.PortableSetupJson.Contains("qwen3-safe.gguf", StringComparison.Ordinal)
            && !separatedRecord.PortableSetupJson.Contains(modelPathMarker, StringComparison.Ordinal),
            "aggregate evidence and replay setup should retain the safe model basename without disclosing a local model path");
        var pathModel = @"C:\Models\private\qwen3-8b-q4.gguf";
        var pathModelRecord = service.Capture(
            "path-model",
            EvaluationSnapshot(
                pathModel,
                [
                    EvaluationMessage(1, "alpha", "Path label test.", 800, 80, 24, model: pathModel),
                    EvaluationMessage(2, "beta", "Publisher label test.", 900, 90, 25, model: "publisher/model-id")
                ]),
            capturedAt);
        Require(pathModelRecord.Models.Any(model => model.Model == "qwen3-8b-q4.gguf")
            && pathModelRecord.Models.All(model => !model.Model.Contains(":\\", StringComparison.Ordinal)),
            "persisted model aggregates should reduce absolute local model paths to a safe basename");
        Require(pathModelRecord.Models.Any(model => model.Model == "publisher/model-id"),
            "ordinary publisher/model identifiers should remain intact in aggregate evidence");
        Require(qa.Ready && qa.OverallReadiness == "ready",
            "complete runtime evidence with a comparable baseline should pass automated QA");
    }

    private static void ArenaEvaluationKeepsFactoryAndMixedEvidenceHonest()
    {
        var service = new ArenaEvaluationService();
        var capturedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z");

        var factorySnapshot = EvaluationSnapshot(
            "factory-model",
            [
                EvaluationMessage(1, "alpha", "Raw response one.", 800, 80, 20, model: "factory-model", promptMode: "factory"),
                EvaluationMessage(2, "beta", "Raw response two.", 1200, 120, 30, model: "factory-model", promptMode: "factory"),
                EvaluationMessage(3, "gamma", "Model call failed: provider unavailable.", 0, 0, 0, status: "error", model: "factory-model", promptMode: "factory")
            ]);
        factorySnapshot.Engine.FactoryMode = true;
        AnchorFactoryGroup(factorySnapshot, "Factory root prompt must remain private in aggregate evidence.");

        var factory = service.Capture("factory-evidence", factorySnapshot, capturedAt);
        var factoryQa = service.EvaluateQa(factory, minimumTurns: 2);
        var factoryComparison = service.Compare(factory, factory);

        Require(factory.Evidence.ModelTurns == 3
            && factory.Evidence.SuccessfulModelTurns == 2
            && factory.Evidence.FailedModelTurns == 1
            && factory.Evidence.ProviderErrors == 1,
            "Factory evaluation should retain observed success and provider-failure evidence");
        Require(factory.Evidence.LatencySamples == 2
            && factory.Evidence.UsageSamples == 2
            && factory.Evidence.ThroughputSamples == 2
            && factory.Metrics.AverageLatencyMs == 1000
            && factory.Metrics.AverageGeneratedTokens == 100
            && factory.Metrics.AverageTokensPerSecond == 25,
            "Factory evaluation should retain measured provider telemetry from successful turns");
        Require(factory.Evidence.ArenaPromptModeTurns == 0
            && factory.Evidence.FactoryPromptModeTurns == 3
            && factory.Evidence.DiscourseTurns == 2,
            "Factory evaluation should count prompt modes and exclude failed provider text from discourse evidence");
        Require(factory.FactoryGroupContext is
            {
                Contract: FactoryConversationService.ContractVersion,
                CausalSampleCount: 3,
                IncludedEntryCount: 3,
                EligibleEntryCount: 3,
                OmittedEntryCount: 0
            }
            && factory.FactoryGroupContext.ContextFingerprint.Length == 64,
            "Factory evaluation should retain only a privacy-safe group-context fingerprint and bounded counts");
        Require(ArenaEvaluationPresentation.RunLabel(factory).Contains("Factory group 3 causal call(s), latest 3/3 entries", StringComparison.Ordinal),
            "Factory evaluation summaries should disclose the bounded group-context sample without exposing its content");
        var factoryExport = service.ExportJson(factory);
        Require(factoryExport.Contains("public_group_v1", StringComparison.Ordinal)
            && factoryExport.Contains("contextFingerprint", StringComparison.Ordinal)
            && !factoryExport.Contains("Factory root prompt", StringComparison.Ordinal)
            && !factoryExport.Contains("factory-group:", StringComparison.Ordinal)
            && !factoryExport.Contains("rootMessageId", StringComparison.OrdinalIgnoreCase),
            "Factory aggregate evidence should expose context identity without transcript text, conversation IDs, or root IDs");
        Require(factory.Evidence.VoiceStyleSamples == 0
            && factory.Metrics.BattleReviewScore is null
            && factory.Metrics.AverageVoiceStyleScore is null
            && factory.Metrics.RoleDriftPercent is null,
            "Factory output must not be scored against Match Setup battle, voice, or role contracts that were not sent");
        var factoryQualityGate = factoryQa.Gates.Single(gate => gate.Id == "quality.sample");
        Require(factoryQualityGate.Status == ArenaQaGateStatuses.Unavailable
            && !factoryQualityGate.Required
            && factoryQualityGate.Explanation.Contains("Factory or mixed", StringComparison.Ordinal),
            "Factory Match Setup quality should be explicitly unavailable without making an inapplicable gate required");
        Require(factoryComparison.Metrics.Single(metric => metric.Id == "quality.score").Status == ArenaEvaluationStatuses.Unavailable
            && factoryComparison.Metrics.Single(metric => metric.Id == "quality.voice-style").Status == ArenaEvaluationStatuses.Unavailable
            && factoryComparison.Metrics.Single(metric => metric.Id == "telemetry.average-latency").Status == ArenaEvaluationStatuses.Unchanged
            && factoryComparison.Metrics.Single(metric => metric.Id == "run.failed-turns").Status == ArenaEvaluationStatuses.Unchanged,
            "Factory comparisons should retain telemetry and failure evidence while withholding Match Setup quality comparisons");
        var normalizedFactory = service.NormalizeForStorage(factory with
        {
            Evidence = factory.Evidence with { VoiceStyleSamples = 2 },
            Metrics = factory.Metrics with
            {
                BattleReviewScore = 99,
                AverageVoiceStyleScore = 99,
                RoleDriftPercent = 99
            }
        });
        Require(normalizedFactory is not null
            && normalizedFactory.Evidence.VoiceStyleSamples == 0
            && normalizedFactory.Metrics.BattleReviewScore is null
            && normalizedFactory.Metrics.AverageVoiceStyleScore is null
            && normalizedFactory.Metrics.RoleDriftPercent is null,
            "persisted Factory records should normalize stale Match Setup quality evidence to unavailable");
        Require(service.NormalizeForStorage(factory with
            {
                FactoryGroupContext = factory.FactoryGroupContext! with { CausalSampleCount = 2 }
            }) is null,
            "Factory context evidence whose causal sample count disagrees with observed Factory turns should be rejected");
        var migratedLegacyFactory = service.NormalizeForStorage(factory with { FactoryGroupContext = null });
        Require(migratedLegacyFactory?.FactoryGroupContext is
            {
                Contract: FactoryConversationService.ContractVersion,
                ContextFingerprint.Length: 0,
                CausalSampleCount: 0,
                IncludedEntryCount: 0,
                EligibleEntryCount: 0,
                OmittedEntryCount: 0
            }
            && service.Compare(migratedLegacyFactory, migratedLegacyFactory).Status == ArenaEvaluationStatuses.Unavailable
            && service.EvaluateQa(migratedLegacyFactory).Gates.Single(gate => gate.Id == "factory.group-context") is
            {
                Required: true,
                Status: ArenaQaGateStatuses.Unavailable
            },
            "legacy Factory evaluation rows should migrate to explicit unavailable group evidence without fabricating comparability");
        var legacyWithoutBaselineQa = service.EvaluateQa(migratedLegacyFactory!);
        var legacyBaselineQa = service.EvaluateQa(migratedLegacyFactory!, migratedLegacyFactory);
        Require(legacyBaselineQa.OverallReadiness == legacyWithoutBaselineQa.OverallReadiness
            && legacyBaselineQa.Failed == legacyWithoutBaselineQa.Failed
            && legacyBaselineQa.Gates.Single(gate => gate.Id == "baseline.comparable").Status == ArenaQaGateStatuses.Unavailable
            && legacyBaselineQa.Gates.Single(gate => gate.Id == "baseline.regression").Status == ArenaQaGateStatuses.Unavailable,
            "missing Factory group identity should keep baseline claims unavailable without adding a failure beyond observed runtime failures");
        var rawFactoryWithoutPromptMetadata = factory with
        {
            FactoryGroupContext = null,
            Evidence = factory.Evidence with
            {
                ArenaPromptModeTurns = factory.Evidence.ModelTurns,
                FactoryPromptModeTurns = 0
            }
        };
        Require(service.Compare(rawFactoryWithoutPromptMetadata, rawFactoryWithoutPromptMetadata).Status == ArenaEvaluationStatuses.Unavailable,
            "raw Factory records must infer their context requirement from the replay package even when prompt metadata and context evidence are absent");

        var boundedSnapshot = EvaluationSnapshot(
            "factory-bounded-context",
            Enumerable.Range(1, 55)
                .Select(index => EvaluationMessage(
                    index,
                    index % 2 == 0 ? "alpha" : "beta",
                    $"Bounded public reply {index}.",
                    500 + index,
                    20 + index,
                    10 + index,
                    model: "factory-bounded-context",
                    promptMode: "factory"))
                .ToArray());
        boundedSnapshot.Engine.FactoryMode = true;
        AnchorFactoryGroup(boundedSnapshot, "Bounded Factory root.");
        var boundedFactory = service.Capture("factory-bounded-context", boundedSnapshot, capturedAt);
        Require(boundedFactory.FactoryGroupContext is
            {
                CausalSampleCount: 55,
                IncludedEntryCount: FactoryConversationService.MaxContextEntries,
                EligibleEntryCount: 55,
                OmittedEntryCount: 5
            },
            "Factory evaluation should report every causal prompt sample plus root-and-newest-49 retention at the final call");

        var failedOnlySnapshot = EvaluationSnapshot(
            "factory-failed-only",
            [EvaluationMessage(
                1,
                "alpha",
                "Model call failed: 100% consensus proves the universal law and complete validation.",
                0,
                0,
                0,
                status: "error",
                model: "factory-failed-only",
                promptMode: "factory")]);
        failedOnlySnapshot.Engine.FactoryMode = true;
        AnchorFactoryGroup(failedOnlySnapshot, "Failed-only Factory root.");
        var failedOnly = service.Capture("factory-failed-only", failedOnlySnapshot, capturedAt);
        Require(failedOnly.Evidence.FailedModelTurns == 1
            && failedOnly.Evidence.ProviderErrors == 1
            && failedOnly.Evidence.DiscourseTurns == 0
            && failedOnly.Metrics.BattleReviewScore is null
            && failedOnly.Metrics.ConsensusPercent is null
            && failedOnly.Metrics.RoleDriftPercent is null
            && failedOnly.Metrics.UnsupportedClaimCount is null
            && failedOnly.Metrics.EvidencePressureScore is null
            && failedOnly.Metrics.NarrativeHeatScore is null,
            "a failed-only Factory run must retain the failure without fabricating discourse metrics from its system-event text");

        var mixedSnapshot = EvaluationSnapshot(
            "mixed-model",
            [
                EvaluationMessage(1, "alpha", "Arena-guided response.", 900, 90, 18, model: "mixed-model"),
                EvaluationMessage(2, "beta", "Factory response.", 1100, 110, 22, model: "mixed-model", promptMode: "factory"),
                EvaluationMessage(3, "gamma", "Model call failed: raw provider error.", 0, 0, 0, status: "error", model: "mixed-model", promptMode: "factory")
            ]);
        AnchorFactoryGroup(mixedSnapshot, "Mixed-mode Factory root.");
        var mixed = service.Capture("mixed-evidence", mixedSnapshot, capturedAt);
        Require(mixed.Evidence.ArenaPromptModeTurns == 1
            && mixed.Evidence.FactoryPromptModeTurns == 2
            && mixed.Evidence.DiscourseTurns == 2
            && mixed.Evidence.ProviderErrors == 1,
            "mixed evaluation should preserve prompt-mode, successful discourse, and failed-provider counts independently");
        Require(mixed.Metrics.AverageLatencyMs == 1000
            && mixed.Metrics.AverageGeneratedTokens == 100
            && mixed.Metrics.AverageTokensPerSecond == 20
            && mixed.Metrics.BattleReviewScore is null
            && mixed.Metrics.AverageVoiceStyleScore is null
            && mixed.Metrics.RoleDriftPercent is null,
            "mixed prompt histories should retain telemetry while withholding Match Setup-dependent quality");
        var mixedQualityGate = service.EvaluateQa(mixed, minimumTurns: 2).Gates.Single(gate => gate.Id == "quality.sample");
        Require(mixedQualityGate.Status == ArenaQaGateStatuses.Unavailable
            && !mixedQualityGate.Required,
            "mixed prompt histories should expose Match Setup quality as explicitly unavailable");

        var differentGroupSnapshot = EvaluationSnapshot(
            "factory-model-b",
            [
                EvaluationMessage(1, "alpha", "Raw response one.", 800, 80, 20, model: "factory-model-b", promptMode: "factory"),
                EvaluationMessage(2, "beta", "Raw response two.", 1200, 120, 30, model: "factory-model-b", promptMode: "factory")
            ]);
        differentGroupSnapshot.Engine.FactoryMode = true;
        AnchorFactoryGroup(differentGroupSnapshot, "Independently reconstructed Factory root.");
        var differentGroup = service.Capture("factory-different-group", differentGroupSnapshot, capturedAt);
        var refusedGroupComparison = service.Compare(factory, differentGroup);
        Require(factory.ScenarioFingerprint == differentGroup.ScenarioFingerprint
            && refusedGroupComparison.Status == ArenaEvaluationStatuses.NotComparable
            && refusedGroupComparison.Summary.Contains("public-group context", StringComparison.Ordinal),
            "Factory runs with matching model-neutral setup but different durable group context must not produce directional deltas");
        var mismatchedGroupQa = service.EvaluateQa(differentGroup, factory);
        Require(mismatchedGroupQa.OverallReadiness == "blocked"
            && mismatchedGroupQa.Gates.Single(gate => gate.Id == "baseline.comparable").Status == ArenaQaGateStatuses.Fail,
            "two valid but different Factory group identities should remain a proven non-comparable baseline failure");

        var sharedInput = EvaluationSnapshot("factory-shared-input", []);
        sharedInput.Engine.FactoryMode = true;
        AnchorFactoryGroup(sharedInput, "Identical causal input for two model trials.");
        var leftSameInput = JsonSerializer.Deserialize<ArenaSnapshot>(JsonSerializer.Serialize(sharedInput))
            ?? throw new InvalidOperationException("Factory comparison fixture clone failed.");
        var rightSameInput = JsonSerializer.Deserialize<ArenaSnapshot>(JsonSerializer.Serialize(sharedInput))
            ?? throw new InvalidOperationException("Factory comparison fixture clone failed.");
        AppendFactoryOutput(leftSameInput, EvaluationMessage(
            1, "alpha", "First model output.", 700, 70, 20, model: "factory-model-left", promptMode: "factory"));
        AppendFactoryOutput(rightSameInput, EvaluationMessage(
            1, "alpha", "Materially different second model output.", 900, 90, 22, model: "factory-model-right", promptMode: "factory"));
        var leftSameInputRecord = service.Capture("factory-same-input-left", leftSameInput, capturedAt);
        var rightSameInputRecord = service.Capture("factory-same-input-right", rightSameInput, capturedAt);
        var sameInputComparison = service.Compare(leftSameInputRecord, rightSameInputRecord);
        Require(leftSameInputRecord.FactoryGroupContext?.ContextFingerprint
                == rightSameInputRecord.FactoryGroupContext?.ContextFingerprint
            && sameInputComparison.Status == ArenaEvaluationStatuses.Insufficient,
            "different one-turn outputs from the same exact causal Factory prompt should pass context identity and remain insufficient only because the metric sample is small");
        var factoryContextGate = factoryQa.Gates.Single(gate => gate.Id == "factory.group-context");
        Require(factoryContextGate.Required
            && factoryContextGate.Status == ArenaQaGateStatuses.Pass
            && factoryContextGate.Evidence.Contains("3/3 included", StringComparison.Ordinal),
            "Factory QA should require and report valid privacy-safe group context evidence");

        var successfulText = EvaluationMessage(1, "alpha", "A bounded proposal with one open question.", 900, 90, 18);
        var clean = service.Capture(
            "clean-discourse",
            EvaluationSnapshot("model-clean", [successfulText]),
            capturedAt);
        var noisy = service.Capture(
            "noisy-discourse",
            EvaluationSnapshot(
                "model-clean",
                [
                    successfulText,
                    EvaluationMessage(2, "beta", "Model call failed: 100% consensus proves an absolute universal law.", 0, 0, 0, status: "error"),
                    EvaluationMessage(3, "system", "Complete validation with 100% consensus.", 0, 0, 0, model: "system-event")
                ]),
            capturedAt);
        Require(noisy.Evidence.DiscourseTurns == 1
            && noisy.Evidence.ProviderErrors == 1
            && noisy.Metrics.BattleReviewScore == clean.Metrics.BattleReviewScore
            && noisy.Metrics.ConsensusPercent == clean.Metrics.ConsensusPercent
            && noisy.Metrics.RoleDriftPercent == clean.Metrics.RoleDriftPercent
            && noisy.Metrics.UnsupportedClaimCount == clean.Metrics.UnsupportedClaimCount
            && noisy.Metrics.EvidencePressureScore == clean.Metrics.EvidencePressureScore
            && noisy.Metrics.NarrativeHeatScore == clean.Metrics.NarrativeHeatScore,
            "failed and system-event text must remain runtime evidence without influencing discourse-derived metrics");
    }

    private static void ArenaEvaluationComparesSameScenarioAndClassifiesRegression()
    {
        var service = new ArenaEvaluationService();
        var baselineSnapshot = EvaluationSnapshot(
            "model-baseline",
            [
                EvaluationMessage(1, "alpha", "However, verify the evidence and risk.", 900, 120, 40),
                EvaluationMessage(2, "beta", "The assumption is unsupported; test it.", 1000, 125, 42),
                EvaluationMessage(3, "gamma", "Evidence and uncertainty require review.", 1100, 130, 38)
            ]);
        var candidateSnapshot = EvaluationSnapshot(
            "model-candidate",
            [
                EvaluationMessage(1, "alpha", "A generic response.", 3000, 180, 12),
                EvaluationMessage(2, "beta", "Another generic response.", 3600, 190, 10),
                EvaluationMessage(3, "gamma", "Provider turn failed.", 0, 0, 0, status: "error")
            ]);
        var baseline = service.Capture("baseline", baselineSnapshot, DateTimeOffset.UnixEpoch);
        var candidate = service.Capture("candidate", candidateSnapshot, DateTimeOffset.UnixEpoch.AddMinutes(1));

        var comparison = service.Compare(baseline, candidate);

        Require(baseline.ScenarioFingerprint == candidate.ScenarioFingerprint,
            "provider/model changes should not alter the model-neutral scenario fingerprint");
        Require(baseline.ExactSetupFingerprint != candidate.ExactSetupFingerprint,
            "provider/model changes should alter the exact replay fingerprint");
        Require(comparison.Status == ArenaEvaluationStatuses.Regressed
            && comparison.RegressedMetricCount >= 3,
            "same-scenario latency, throughput, success, and failure regressions should classify deterministically");
        Require(comparison.Metrics.Single(metric => metric.Id == "telemetry.average-latency").Status
            == ArenaEvaluationStatuses.Regressed,
            "latency increase beyond 15% and 250 ms should be a regression");
        Require(comparison.Metrics.Single(metric => metric.Id == "run.failed-turns").Status
            == ArenaEvaluationStatuses.Regressed,
            "any evidenced failed-turn increase should be a regression");
        Require(comparison.Metrics.All(metric => metric.BaselineEvidence >= 0 && metric.CandidateEvidence >= 0),
            "every metric classification should expose its evidence counts");

        var providerIdentitySnapshot = EvaluationSnapshot(
            "model-from-another-provider",
            [
                EvaluationMessage(1, "alpha", "Provider identity changed.", 950, 100, 30),
                EvaluationMessage(2, "beta", "Setup shape stayed fixed.", 1050, 110, 29)
            ],
            baseUrl: "http://127.0.0.1:1234/v1");
        var providerIdentity = service.Capture("provider-identity", providerIdentitySnapshot, DateTimeOffset.UnixEpoch);
        Require(baseline.ScenarioFingerprint == providerIdentity.ScenarioFingerprint,
            "endpoint and model identity changes should remain comparable when routing and tuning shape are unchanged");

        var tuningChanges = new (string Name, Action<ArenaSnapshot> Apply)[]
        {
            ("API mode", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], apiMode: ModelProviderApiModes.LlamaCppNative)),
            ("timeout", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], timeout: 240)),
            ("temperature", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], temperature: 0.9)),
            ("maximum output", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], maxOutputTokens: 1024)),
            ("context", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], contextLength: 16384)),
            ("reasoning", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], reasoning: "high")),
            ("stateful routing", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], nativeStatefulChat: false)),
            ("idle lifecycle", snapshot => snapshot.Configs["shared"] = EvaluationProviderWith(snapshot.Configs["shared"], nativeIdleTtlSeconds: 600))
        };
        foreach (var (name, apply) in tuningChanges)
        {
            var changedSetup = EvaluationSnapshot(
                "model-baseline",
                [
                    EvaluationMessage(1, "alpha", "Same scenario, changed setup.", 950, 100, 30),
                    EvaluationMessage(2, "beta", "Comparison must be refused.", 1050, 110, 29)
                ]);
            apply(changedSetup);
            var changedEvaluation = service.Capture($"changed-{name}", changedSetup, DateTimeOffset.UnixEpoch);
            Require(baseline.ScenarioFingerprint != changedEvaluation.ScenarioFingerprint,
                $"a changed {name} setting must alter the scenario fingerprint");
            Require(service.Compare(baseline, changedEvaluation).Status == ArenaEvaluationStatuses.NotComparable,
                $"a changed {name} setting must not be reported as a model-only comparison");
        }

        var routedBaselineSnapshot = EvaluationSnapshot(
            "route-model-a",
            [EvaluationMessage(1, "alpha", "Routing baseline.", 900, 100, 30)]);
        routedBaselineSnapshot.Configs["alpha"] = EvaluationProviderWith(
            routedBaselineSnapshot.Configs["shared"],
            model: "route-model-a");
        var routedIdentitySnapshot = EvaluationSnapshot(
            "route-model-b",
            [EvaluationMessage(1, "alpha", "Routing identity candidate.", 900, 100, 30)]);
        routedIdentitySnapshot.Configs["alpha"] = EvaluationProviderWith(
            routedIdentitySnapshot.Configs["shared"],
            model: "route-model-b");
        var routedSplitSnapshot = EvaluationSnapshot(
            "route-model-b",
            [EvaluationMessage(1, "alpha", "Routing shape candidate.", 900, 100, 30)]);
        routedSplitSnapshot.Configs["alpha"] = EvaluationProviderWith(
            routedSplitSnapshot.Configs["shared"],
            model: "route-model-c");
        var routedBaseline = service.Capture("routed-baseline", routedBaselineSnapshot, DateTimeOffset.UnixEpoch);
        var routedIdentity = service.Capture("routed-identity", routedIdentitySnapshot, DateTimeOffset.UnixEpoch);
        var routedSplit = service.Capture("routed-split", routedSplitSnapshot, DateTimeOffset.UnixEpoch);
        Require(routedBaseline.ScenarioFingerprint == routedIdentity.ScenarioFingerprint,
            "renaming a shared role-model assignment should preserve its routing shape");
        Require(routedBaseline.ScenarioFingerprint != routedSplit.ScenarioFingerprint,
            "splitting one shared model assignment into distinct role models should alter routing shape");

        var responseLengthBaseline = service.Capture(
            "response-length-baseline",
            EvaluationSnapshot(
                "response-model-a",
                [
                    EvaluationMessage(1, "alpha", "Long baseline response.", 900, 200, 30),
                    EvaluationMessage(2, "beta", "Second long baseline response.", 900, 220, 30)
                ]),
            DateTimeOffset.UnixEpoch);
        var responseLengthCandidate = service.Capture(
            "response-length-candidate",
            EvaluationSnapshot(
                "response-model-b",
                [
                    EvaluationMessage(1, "alpha", "Short candidate response.", 900, 100, 30),
                    EvaluationMessage(2, "beta", "Second short candidate response.", 900, 110, 30)
                ]),
            DateTimeOffset.UnixEpoch.AddMinutes(1));
        var responseLengthComparison = service.Compare(responseLengthBaseline, responseLengthCandidate);
        var responseLengthMetric = responseLengthComparison.Metrics.Single(metric => metric.Id == "telemetry.generated-tokens");
        Require(responseLengthMetric.Status == ArenaEvaluationStatuses.Regressed
            && responseLengthMetric.BaselineValue == 210
            && responseLengthMetric.CandidateValue == 105
            && responseLengthMetric.Threshold.Contains("20%", StringComparison.Ordinal),
            "a material generated-response reduction should be an evidenced truncation-risk regression with a declared threshold");
        Require(service.Compare(responseLengthCandidate, responseLengthBaseline).Metrics
                .Single(metric => metric.Id == "telemetry.generated-tokens").Status == ArenaEvaluationStatuses.Unchanged,
            "longer responses should be reported without being claimed as an automatic quality improvement");

        var differentScenarioSnapshot = EvaluationSnapshot(
            "model-candidate",
            [EvaluationMessage(1, "alpha", "Different scenario.", 1000, 100, 20)]);
        differentScenarioSnapshot.Engine.Steering.Topic = "A materially different topic";
        var differentScenario = service.Capture("different", differentScenarioSnapshot, DateTimeOffset.UnixEpoch);
        var refused = service.Compare(baseline, differentScenario);
        Require(refused.Status == ArenaEvaluationStatuses.NotComparable
            && refused.ComparableMetricCount == 0,
            "different scenario fingerprints must never be compared as equivalent runs");

        var empty = service.Capture(
            "empty",
            EvaluationSnapshot("model-empty", []),
            DateTimeOffset.UnixEpoch);
        var insufficient = service.Compare(empty, empty);
        Require(insufficient.Status == ArenaEvaluationStatuses.Insufficient
            && insufficient.Metrics.All(metric => metric.Status == ArenaEvaluationStatuses.Unavailable),
            "missing telemetry should remain explicitly unavailable rather than becoming zero-valued evidence");
    }

    private static void ArenaEvaluationQaReportsExplicitEvidenceStates()
    {
        var service = new ArenaEvaluationService();
        var sparseSnapshot = EvaluationSnapshot(
            "model-sparse",
            [EvaluationMessage(1, "alpha", "Only one turn.", 0, 0, 0, status: "error")]);
        sparseSnapshot.Engine.Internet.UseInternet = true;
        sparseSnapshot.Engine.Agents[0].Status = "thinking";
        sparseSnapshot.Engine.LastError = "Bearer secret-provider-error-that-must-not-persist";
        var sparse = service.Capture("sparse", sparseSnapshot, DateTimeOffset.UnixEpoch);

        var qa = service.EvaluateQa(sparse, runtimeEvidence: new ArenaRuntimeQaEvidence());

        Require(qa.OverallReadiness == "blocked" && !qa.Ready,
            "failed required runtime gates should block readiness");
        Require(qa.Gates.Select(gate => gate.Id).Distinct(StringComparer.Ordinal).Count() == qa.Gates.Count,
            "runtime QA gates should expose stable unique IDs");
        Require(qa.Gates.Single(gate => gate.Id == "run.minimum-turn-sample").Status == ArenaQaGateStatuses.Fail,
            "minimum turn sample should fail with one model turn");
        Require(sparse.Evidence.ProviderErrors == 1
            && qa.Gates.Single(gate => gate.Id == "runtime.provider-errors").Status == ArenaQaGateStatuses.Fail,
            "an observed failed model turn should remain attributable provider-failure evidence");
        Require(qa.Gates.Single(gate => gate.Id == "runtime.stuck-thinking").Status == ArenaQaGateStatuses.Fail,
            "stuck thinking state should fail explicitly");
        Require(qa.Gates.Single(gate => gate.Id == "runtime.interruption-recovery").Status == ArenaQaGateStatuses.Unavailable,
            "missing interruption probes must remain unavailable");
        Require(!qa.Gates.Single(gate => gate.Id == "runtime.interruption-recovery").Required,
            "an unavailable interruption probe must not become an impossible UI readiness requirement");
        Require(qa.Gates.Single(gate => gate.Id == "runtime.provider-reconnect").Status == ArenaQaGateStatuses.Unavailable,
            "missing reconnect probes must remain unavailable");
        Require(!qa.Gates.Single(gate => gate.Id == "runtime.provider-reconnect").Required,
            "an unavailable reconnect probe must not become an impossible UI readiness requirement");
        Require(qa.Gates.Single(gate => gate.Id == "telemetry.latency-completeness").Status == ArenaQaGateStatuses.Unavailable,
            "missing latency telemetry must not claim completeness");
        Require(qa.Gates.Single(gate => gate.Id == "internet.evidence").Status == ArenaQaGateStatuses.Unavailable,
            "Internet enabled without sourced turns must not claim Internet coverage");
        Require(qa.Gates.Single(gate => gate.Id == "baseline.comparable").Status == ArenaQaGateStatuses.Unavailable,
            "missing baseline must not claim repeatability");
        Require(qa.Gates.All(gate => gate.Status is "pass" or "warn" or "fail" or "unavailable"),
            "QA gate status vocabulary should stay app-consumable");

        var exported = service.ExportJson(sparse, qa: qa);
        Require(!exported.Contains("secret-provider-error", StringComparison.Ordinal),
            "runtime QA export must count provider errors without retaining their bodies");

        var noTurnSnapshot = EvaluationSnapshot("model-not-run", []);
        noTurnSnapshot.Configs["shared"] = EvaluationProviderWith(
            noTurnSnapshot.Configs["shared"],
            lastError: "Persisted offline probe must not become run evidence.",
            lastTestOk: false);
        noTurnSnapshot.Engine.LastError = "Unattributed prior engine state.";
        var noTurn = service.Capture("not-run", noTurnSnapshot, DateTimeOffset.UnixEpoch);
        var noTurnQa = service.EvaluateQa(noTurn);
        Require(noTurn.Evidence.ModelTurns == 0 && noTurn.Evidence.ProviderErrors == 0,
            "stale provider-health and unattributed engine errors must not become run-specific provider failures");
        Require(noTurnQa.OverallReadiness == "partial"
            && noTurnQa.Failed == 0
            && noTurnQa.Gates.Single(gate => gate.Id == "run.minimum-turn-sample").Status == ArenaQaGateStatuses.Unavailable
            && noTurnQa.Gates.Single(gate => gate.Id == "runtime.provider-errors").Status == ArenaQaGateStatuses.Unavailable,
            "a capture with no model-turn evidence should remain incomplete rather than report failed runtime QA");

        var nonModelSnapshot = EvaluationSnapshot("model-not-used", []);
        var transcriptService = new TranscriptService();
        nonModelSnapshot.Engine.Messages.Add(transcriptService.CreateOperatorMessage("Operator direction.", 1));
        nonModelSnapshot.Engine.Messages.Add(transcriptService.CreateInternetToolMessage(
            new InternetToolRequest
            {
                Tool = "search",
                Query = "bounded test query",
                RequesterId = "alpha"
            },
            new InternetToolResult
            {
                Ok = false,
                Tool = "search",
                Error = "Synthetic tool failure.",
                CheckedAt = DateTimeOffset.UnixEpoch
            },
            2));
        nonModelSnapshot.Engine.TurnCount = 2;
        var nonModel = service.Capture("non-model-records", nonModelSnapshot, DateTimeOffset.UnixEpoch);
        var nonModelQa = service.EvaluateQa(nonModel);
        Require(nonModel.Evidence.TranscriptMessages == 2
            && nonModel.Evidence.TranscriptErrors == 1
            && nonModel.Evidence.ModelTurns == 0
            && nonModel.Evidence.ProviderErrors == 0,
            "operator and failed Internet-tool records must not become provider-backed model-turn evidence");
        Require(nonModelQa.Gates.Single(gate => gate.Id == "run.minimum-turn-sample").Status == ArenaQaGateStatuses.Unavailable
            && nonModelQa.Gates.Single(gate => gate.Id == "runtime.provider-errors").Status == ArenaQaGateStatuses.Unavailable
            && nonModelQa.Gates.Single(gate => gate.Id == "runtime.transcript-errors").Status == ArenaQaGateStatuses.Fail,
            "non-model records should leave model gates unavailable while retaining an observed transcript error");

        var healthy = service.Capture(
            "healthy",
            EvaluationSnapshot(
                "model-healthy",
                [
                    EvaluationMessage(1, "alpha", "However, inspect the evidence.", 900, 100, 30),
                    EvaluationMessage(2, "beta", "Verify assumptions and risk.", 1000, 110, 31),
                    EvaluationMessage(3, "gamma", "Keep uncertainty explicit.", 1100, 120, 29)
                ]),
            DateTimeOffset.UnixEpoch);
        var uiQa = service.EvaluateQa(healthy);
        Require(uiQa.Ready && uiQa.OverallReadiness == "ready",
            "the UI QA path should reach Ready when every available required gate passes");
        Require(uiQa.Gates.Where(gate => gate.Id is "runtime.interruption-recovery" or "runtime.provider-reconnect")
                .All(gate => !gate.Required && gate.Status == ArenaQaGateStatuses.Unavailable),
            "unrun runtime probes should remain explicit optional unavailable evidence in UI QA");
        Require(uiQa.Summary.Contains("optional evidence", StringComparison.OrdinalIgnoreCase),
            "Ready status should still disclose unavailable optional probes");

        var failedRuntimeProbe = service.EvaluateQa(
            healthy,
            runtimeEvidence: new ArenaRuntimeQaEvidence(
                InterruptionRecovered: false,
                ProviderReconnectSucceeded: false));
        Require(!failedRuntimeProbe.Ready && failedRuntimeProbe.OverallReadiness == "blocked",
            "real failing runtime probe evidence must block QA readiness");
        Require(failedRuntimeProbe.Gates
                .Where(gate => gate.Id is "runtime.interruption-recovery" or "runtime.provider-reconnect")
                .All(gate => gate.Required && gate.Status == ArenaQaGateStatuses.Fail),
            "provided failing runtime probes must remain required failure evidence rather than being downgraded to unavailable");
    }

    private static void ArenaEvaluationHistoryIsBoundedAtomicAndRecoversCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-evaluation-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "history.json");
        try
        {
            var service = new ArenaEvaluationService();
            var store = new ArenaEvaluationHistoryStore(path, service);
            var baseline = service.Capture(
                "baseline",
                EvaluationSnapshot(
                    "baseline-model",
                    [
                        EvaluationMessage(1, "alpha", "Baseline body must not persist.", 1000, 100, 20),
                        EvaluationMessage(2, "beta", "Second baseline body.", 1100, 110, 21)
                    ],
                    apiToken: "history-secret-token"),
                DateTimeOffset.UnixEpoch);
            var baselineQa = service.EvaluateQa(
                baseline,
                baseline,
                new ArenaRuntimeQaEvidence(true, true));
            store.Save(baseline, baselineQa, setBaseline: true);

            for (var index = 0; index < ArenaEvaluationHistoryStore.MaximumEntries + 7; index++)
            {
                var snapshot = EvaluationSnapshot(
                    $"model-{index:D2}",
                    [
                        EvaluationMessage(1, "alpha", $"Body {index} must not persist.", 900 + index, 100 + index, 20 + index),
                        EvaluationMessage(2, "beta", "Another body.", 1000 + index, 110 + index, 21 + index)
                    ]);
                var evaluation = service.Capture(
                    $"candidate-{index:D2}",
                    snapshot,
                    DateTimeOffset.UnixEpoch.AddMinutes(index + 1));
                store.Save(evaluation);
            }

            var repeatedSnapshot = EvaluationSnapshot(
                "repeated-model",
                [
                    EvaluationMessage(1, "alpha", "Repeated trial one.", 900, 100, 20),
                    EvaluationMessage(2, "beta", "Repeated trial two.", 1000, 110, 21)
                ]);
            var repeatedFirst = service.Capture(
                "repeated-trial",
                repeatedSnapshot,
                DateTimeOffset.UnixEpoch.AddDays(2));
            var repeatedSecond = service.Capture(
                "repeated-trial",
                repeatedSnapshot,
                DateTimeOffset.UnixEpoch.AddDays(2).AddMinutes(1));
            Require(repeatedFirst.RunFingerprint == repeatedSecond.RunFingerprint
                && repeatedFirst.RunId != repeatedSecond.RunId,
                "repeat trials should share evidence identity without sharing capture identity");
            store.Save(repeatedFirst);
            store.Save(repeatedSecond);
            store.Save(repeatedSecond);

            var loaded = store.Load();
            var json = File.ReadAllText(path);
            Require(loaded.Schema == ArenaEvaluationHistorySchemas.History
                && loaded.Entries.Count <= ArenaEvaluationHistoryStore.MaximumEntries,
                "evaluation history should remain schema-versioned and entry bounded");
            Require(loaded.BaselineFor(baseline.ScenarioFingerprint)?.Evaluation.RunId == baseline.RunId,
                "bounded history should preserve the explicit scenario baseline when possible");
            Require(loaded.Entries.Count(entry => entry.Evaluation.RunId == repeatedFirst.RunId) == 1
                && loaded.Entries.Count(entry => entry.Evaluation.RunId == repeatedSecond.RunId) == 1,
                "separately timed repeat trials should remain separate history rows while an exact duplicate save is deduplicated");
            Require(new FileInfo(path).Length <= ArenaEvaluationHistoryStore.MaximumHistoryBytes,
                "evaluation history should remain byte bounded");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
                "atomic history writes should not leave temporary siblings");
            Require(!json.Contains("history-secret-token", StringComparison.Ordinal)
                && !json.Contains("Baseline body must not persist", StringComparison.Ordinal)
                && !json.Contains("apiToken", StringComparison.OrdinalIgnoreCase),
                "evaluation history must not contain API credentials or transcript bodies");

            File.WriteAllText(path, "{ not valid json");
            var recovered = store.Load();
            Require(recovered.Entries.Count == 0 && !string.IsNullOrWhiteSpace(store.LastLoadWarning),
                "corrupt history should recover to an explicit empty state with a warning");
            Require(Directory.EnumerateFiles(root, "history.corrupt.*.json", SearchOption.TopDirectoryOnly).Any(),
                "corrupt history should be moved aside instead of overwritten silently");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void ArenaEvaluationPresentationExposesNonColourModelComparisons()
    {
        var metric = new ArenaEvaluationMetricComparison(
            "telemetry.average-latency",
            "Average latency",
            ArenaEvaluationStatuses.Improved,
            1200,
            800,
            -400,
            "ms",
            "15% and 250 ms",
            3,
            3,
            "Latency improved with enough evidence.");
        var unavailable = metric with
        {
            Status = ArenaEvaluationStatuses.Unavailable,
            BaselineValue = null,
            CandidateValue = null,
            Delta = null,
            Explanation = "No telemetry samples."
        };
        var unavailableZeroCount = unavailable with
        {
            Label = "Failed model turns",
            BaselineValue = 0,
            CandidateValue = 0,
            Explanation = "At least two model turns are required in each run."
        };
        var models = new[]
        {
            new ArenaEvaluationModelAggregate("model-a", 3, 3, 0, 900, 1000, 120, 31.25)
        };
        var report = new ArenaRuntimeQaReport(
            "partial",
            false,
            7,
            1,
            0,
            2,
            "Required evidence is incomplete.",
            []);

        Require(ArenaEvaluationPresentation.FormatMetric(metric).StartsWith("✓", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatMetric(metric).Contains("1,200", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatMetric(metric).Contains("800", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatMetric(metric).Contains("−400", StringComparison.Ordinal),
            "comparison rows should include a non-colour state glyph and exact baseline, candidate, and delta values");
        Require(ArenaEvaluationPresentation.FormatMetric(unavailable).StartsWith("—", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatMetric(unavailable).Contains("unavailable", StringComparison.OrdinalIgnoreCase),
            "missing model evidence should stay visibly unavailable without a zero placeholder");
        Require(ArenaEvaluationPresentation.FormatMetric(unavailableZeroCount).Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            && !ArenaEvaluationPresentation.FormatMetric(unavailableZeroCount).Contains("0 → 0", StringComparison.Ordinal),
            "numeric placeholders on an unavailable metric must not look like an observed comparison");
        Require(ArenaEvaluationPresentation.FormatModels(models).Contains("model-a", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatModels(models).Contains("31", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatModels(models).Contains("tok/s", StringComparison.Ordinal),
            "model comparison summaries should expose observed turns, latency, and throughput");
        Require(ArenaEvaluationPresentation.FormatQa(report).Contains("PARTIAL", StringComparison.Ordinal)
            && ArenaEvaluationPresentation.FormatQa(report).Contains("2 unavailable", StringComparison.Ordinal),
            "QA presentation should disclose incomplete evidence counts");
        Require(ArenaEvaluationPresentation.BrushKey(ArenaEvaluationStatuses.Regressed) == "DangerTextBrush"
            && ArenaEvaluationPresentation.BrushKey(ArenaEvaluationStatuses.Improved) == "PrimaryBorderBrush",
            "comparison states should retain theme-aware colour reinforcement in addition to non-colour glyphs");

        Require(ArenaEvaluationPresentation.DisplayMetricLabel(metric) == "Latency"
            && ArenaEvaluationPresentation.DisplayMetricLabel(metric with { Id = "quality.score" }) == "Quality score"
            && ArenaEvaluationPresentation.DisplayMetricLabel(metric with { Id = "telemetry.generated-tokens" }) == "Generated tokens"
            && ArenaEvaluationPresentation.DisplayMetricLabel(metric with { Id = "telemetry.throughput" }) == "Throughput"
            && ArenaEvaluationPresentation.DisplayMetricLabel(metric with { Id = "run.success-rate" }) == "Successful turns"
            && ArenaEvaluationPresentation.DisplayMetricLabel(metric with { Id = "run.failed-turns" }) == "Failed turns",
            "metric cards should use the requested latency, quality, token, throughput, successful-turn, and failed-turn labels");

        RunStaTest(() =>
        {
            static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
            {
                foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                {
                    if (child is T match) yield return match;
                    foreach (var descendant in Descendants<T>(child)) yield return descendant;
                }
            }

            var comparisonCard = ArenaEvaluationCoordinator.CreateMetricCard(metric, AccentResourceBrush);
            var unavailableCard = ArenaEvaluationCoordinator.CreateMetricCard(unavailableZeroCount, AccentResourceBrush);
            var host = new Window
            {
                Width = 320,
                Height = 520,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new StackPanel { Children = { comparisonCard, unavailableCard } }
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                var comparisonCardPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(comparisonCard)
                    ?? throw new InvalidOperationException("Metric card did not create an automation grouping peer.");
                var unavailableCardPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(unavailableCard)
                    ?? throw new InvalidOperationException("Unavailable metric card did not create an automation grouping peer.");
                var bars = Descendants<MetricSparklineControl>(comparisonCard).Single();
                var barsPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(bars)
                    ?? throw new InvalidOperationException("Metric sparkline did not create an automation peer.");
                var comparisonText = Descendants<TextBlock>(comparisonCard).Select(block => block.Text).ToArray();
                var unavailableText = Descendants<TextBlock>(unavailableCard).Select(block => block.Text).ToArray();
                Require(comparisonCard.Tag?.ToString() == "Ready",
                    "hosted comparable metric card lost its semantic Ready state");
                Require(comparisonCardPeer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Group
                    && comparisonCardPeer.GetName().Contains("Latency", StringComparison.Ordinal)
                    && comparisonCardPeer.GetItemStatus() == "Ready",
                    "hosted comparable metric card did not expose its aggregate grouping peer");
                Require(barsPeer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Image
                    && barsPeer.GetName().Contains("baseline and current comparison bars", StringComparison.Ordinal)
                    && barsPeer.GetHelpText().Contains("Baseline 1,200 ms; current 800 ms.", StringComparison.Ordinal)
                    && bars.Mode == "bars"
                    && bars.Values.SequenceEqual([1200d, 800d])
                    && comparisonText.Contains("1,200 ms", StringComparer.Ordinal)
                    && comparisonText.Contains("800 ms", StringComparer.Ordinal)
                    && comparisonText.Any(text => text.Contains("↓ −400 ms", StringComparison.Ordinal)),
                    "hosted metric card did not expose exact values, directional delta, and a real accessible comparison-bar peer");
                Require(unavailableCard.Tag?.ToString() == "Unavailable",
                    "hosted unavailable metric card lost its semantic state");
                Require(unavailableCardPeer.GetAutomationControlType() == System.Windows.Automation.Peers.AutomationControlType.Group
                    && unavailableCardPeer.GetItemStatus() == "Unavailable"
                    && unavailableCardPeer.GetName().Contains("Unavailable", StringComparison.Ordinal),
                    "hosted unavailable metric card did not expose its aggregate grouping peer");
                Require(!Descendants<MetricSparklineControl>(unavailableCard).Any()
                    && unavailableText.Count(text => text == "Unavailable") >= 2
                    && !unavailableText.Any(text => text.Contains("0 → 0", StringComparison.Ordinal)),
                    "unavailable hosted metric cards must omit numeric bars and must not present placeholder zeroes as observations");
            }
            finally
            {
                host.Close();
            }
        });

        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-evaluation-state-cards-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                const string sessionId = "evaluation-state-cards";
                var store = new SessionStore(root);
                var snapshot = EvaluationSnapshot(
                    "model-state-cards",
                    [
                        EvaluationMessage(1, "alpha", "Baseline evidence.", 800, 80, 24),
                        EvaluationMessage(2, "beta", "Second baseline sample.", 900, 90, 25)
                    ]);
                store.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
                var service = new ArenaEvaluationService();
                var history = new ArenaEvaluationHistoryStore(Path.Combine(root, "history.json"), service);
                var comparisonSummary = new TextBlock();
                var comparisonItems = new StackPanel();
                var qaSummary = new TextBlock();
                var qaItems = new StackPanel();
                var coordinator = new ArenaEvaluationCoordinator(
                    store,
                    service,
                    history,
                    new TextBlock(),
                    new TextBlock(),
                    new TextBlock(),
                    new TextBlock(),
                    comparisonSummary,
                    comparisonItems,
                    new StackPanel(),
                    qaSummary,
                    qaItems,
                    new Button(),
                    new Button(),
                    new Button(),
                    new Button(),
                    new Button(),
                    () => new SessionSummary(sessionId, "", true, 2, 0, 0, DateTimeOffset.UtcNow),
                    () => false,
                    AccentResourceBrush,
                    _ => { });

                Require(comparisonItems.Children.OfType<Border>().Any(card => Equals(card.Tag, "Empty"))
                        && qaItems.Children.OfType<Border>().Any(card => Equals(card.Tag, "Unavailable")),
                    "initial comparison and QA surfaces did not retain explicit Empty and Unavailable cards");
                RunExperimentDispatcherTask(() => coordinator.CaptureBaselineAsync());
                Require(comparisonItems.Children.OfType<Border>().Any(card => Equals(card.Tag, "Empty"))
                        && comparisonSummary.Text.Contains("Baseline is ready", StringComparison.Ordinal),
                    "baseline-only transition left the comparison surface visually blank");

                var changed = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("Evaluation state-card fixture disappeared.");
                changed.Configs["shared"] = EvaluationProviderWith(
                    changed.Configs["shared"],
                    temperature: 0.9);
                store.SaveSnapshotAsync(changed, sessionId).GetAwaiter().GetResult();
                RunExperimentDispatcherTask(() => coordinator.CompareCurrentAsync());
                Require(comparisonItems.Children.OfType<Border>().Any(card => Equals(card.Tag, "Unavailable"))
                        && comparisonSummary.Text.Contains("required", StringComparison.OrdinalIgnoreCase),
                    "empty-metric comparison transition lost its explicit Unavailable card and reason");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static void ArenaEvaluationSurfaceStaysReplayableAndAccessible()
    {
        var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
        var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
        var coordinator = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ArenaEvaluationCoordinator.cs");

        foreach (var control in new[]
                 {
                     "ArenaEvaluationCaptureBaselineButton",
                     "ArenaEvaluationCompareCurrentButton",
                     "ArenaEvaluationRunQaButton",
                     "ArenaEvaluationCopyEvidenceButton",
                     "ArenaEvaluationCopyReplaySetupButton"
                 })
        {
            Require(xaml.Contains($"x:Name=\"{control}\"", StringComparison.Ordinal),
                $"evaluation surface should expose {control}");
        }

        Require(xaml.Contains("Header=\"Model Comparison &amp; QA\"", StringComparison.Ordinal)
            && xaml.Contains("AutomationProperties.Name=\"Model comparison and runtime QA\"", StringComparison.Ordinal)
            && xaml.Contains("AutomationProperties.LiveSetting=\"Polite\"", StringComparison.Ordinal),
            "model comparison should be a named, keyboard-expandable, politely announcing right-rail surface");
        Require(xaml.Contains("AutomationProperties.HelpText=\"Copy the exact secret-free Match Setup JSON", StringComparison.Ordinal)
            && source.Contains(".CopyReplaySetup()", StringComparison.Ordinal)
            && coordinator.Contains("PortableSetupJson", StringComparison.Ordinal),
            "the evaluation surface should expose an accessible exact replay-package action");
        Require(source.Contains("coordinator.CaptureBaselineAsync", StringComparison.Ordinal)
            && source.Contains("coordinator.CompareCurrentAsync", StringComparison.Ordinal)
            && source.Contains("coordinator.RunQaAsync", StringComparison.Ordinal)
            && source.Contains("_arenaEvaluationCoordinator?.RefreshAvailability()", StringComparison.Ordinal),
            "the shell should wire capture, comparison, QA, and session/busy refresh paths");
        Require(coordinator.Contains("BaselineFor(candidate.ScenarioFingerprint)", StringComparison.Ordinal)
            && coordinator.Contains("The active session changed; stale evaluation evidence was discarded.", StringComparison.Ordinal)
            && coordinator.Contains("ShellClipboard.TrySetText", StringComparison.Ordinal),
            "the coordinator should compare only matching scenarios, reject stale captures, and use the safe clipboard helper");
        Require(coordinator.Contains("replay setup and session identity were excluded", StringComparison.Ordinal)
            && coordinator.Contains("evaluationService.ExportJson(lastCandidate", StringComparison.Ordinal),
            "Copy Evidence should disclose that its aggregate export excludes the separate replay setup and session identity");
    }

    private static ArenaSnapshot EvaluationSnapshot(
        string model,
        IReadOnlyList<DialogueMessage> messages,
        string apiToken = "",
        string baseUrl = "http://127.0.0.1:8080/v1")
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Steering.Topic = "Repeatable local runtime evaluation";
        snapshot.Engine.Steering.Global = "Compare the same captured scenario with evidence.";
        snapshot.Engine.TurnCount = messages.Count;
        snapshot.Engine.Messages = messages.ToList();
        foreach (var agent in snapshot.Engine.Agents)
        {
            agent.VoiceStyle = "skeptical";
            agent.Status = "waiting";
        }

        snapshot.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = baseUrl,
            ApiMode = "openai_compatible",
            ApiToken = apiToken,
            Model = model,
            Timeout = 120,
            Temperature = 0.4,
            MaxOutputTokens = 512,
            ContextLength = 8192,
            Reasoning = "medium",
            LastTestOk = true
        };
        return snapshot;
    }

    private static ModelProviderConfig EvaluationProviderWith(
        ModelProviderConfig source,
        string? baseUrl = null,
        string? apiMode = null,
        string? model = null,
        int? timeout = null,
        double? temperature = null,
        int? maxOutputTokens = null,
        int? contextLength = null,
        string? reasoning = null,
        bool? nativeStatefulChat = null,
        int? nativeIdleTtlSeconds = null,
        string? lastError = null,
        bool? lastTestOk = null)
    {
        return new ModelProviderConfig
        {
            BaseUrl = baseUrl ?? source.BaseUrl,
            ApiMode = apiMode ?? source.ApiMode,
            ApiToken = source.ApiToken,
            Model = model ?? source.Model,
            Timeout = timeout ?? source.Timeout,
            Temperature = temperature ?? source.Temperature,
            MaxOutputTokens = maxOutputTokens ?? source.MaxOutputTokens,
            ContextLength = contextLength ?? source.ContextLength,
            Reasoning = reasoning ?? source.Reasoning,
            NativeStatefulChat = nativeStatefulChat ?? source.NativeStatefulChat,
            NativeIdleTtlSeconds = nativeIdleTtlSeconds ?? source.NativeIdleTtlSeconds,
            PreviousResponseId = source.PreviousResponseId,
            LastError = lastError ?? source.LastError,
            LastLatencyMs = source.LastLatencyMs,
            LastTestOk = lastTestOk ?? source.LastTestOk,
            Extra = source.Extra
        };
    }

    private static DialogueMessage EvaluationMessage(
        int turn,
        string speakerId,
        string text,
        int latencyMs,
        int completionTokens,
        double tokensPerSecond,
        string status = "ok",
        string model = "model-a",
        int? promptTokens = null,
        int? totalTokens = null,
        string promptMode = "")
    {
        var message = new DialogueMessage
        {
            Turn = turn,
            Speaker = char.ToUpperInvariant(speakerId[0]) + speakerId[1..],
            SpeakerId = speakerId,
            Text = text,
            Status = status,
            Kind = "message",
            CreatedAt = 1_700_000_000 + turn,
            Model = new ModelMetadata
            {
                Model = model,
                LatencyMs = latencyMs,
                PromptTokens = promptTokens ?? (completionTokens > 0 ? completionTokens * 2 : 0),
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens ?? (completionTokens > 0 ? completionTokens * 3 : 0),
                TokensPerSecond = tokensPerSecond,
                TimeToFirstTokenMs = latencyMs > 0 ? Math.Max(1, latencyMs / 4) : 0
            }
        };
        if (!string.IsNullOrWhiteSpace(promptMode))
        {
            message.Metadata["prompt_mode"] = JsonSerializer.SerializeToElement(promptMode.Trim().ToLowerInvariant());
        }

        return message;
    }

    private static void AnchorFactoryGroup(ArenaSnapshot snapshot, string operatorText)
    {
        var existing = snapshot.Engine.Messages.ToArray();
        snapshot.Engine.Messages.Clear();
        var root = new TranscriptService().CreateOperatorMessage(operatorText, 0, preserveOuterWhitespace: true);
        snapshot.Engine.Messages.Add(root);
        var service = new FactoryConversationService();
        var inspection = service.Resolve(snapshot);
        Require(inspection.HasUsableRoot
            && inspection.IsAnchored
            && !inspection.IsOrphaned
            && inspection.ContextFingerprint.Length == 64,
            "Factory evaluation fixture could not establish its durable public-group root");
        foreach (var message in existing
                     .OrderBy(message => message.Turn)
                     .ThenBy(message => message.CreatedAt))
        {
            if (FactoryConversationService.HasFactoryPromptContract(message))
            {
                AppendFactoryOutput(snapshot, message);
            }
            else
            {
                service.StampPublicParticipant(snapshot, message);
                snapshot.Engine.Messages.Add(message);
            }
        }
    }

    private static void AppendFactoryOutput(ArenaSnapshot snapshot, DialogueMessage message)
    {
        var service = new FactoryConversationService();
        var context = service.BuildPromptContext(snapshot, message.SpeakerId);
        Require(context.Ok && context.ContextFingerprint.Length == 64,
            "Factory evaluation fixture could not capture causal prompt evidence");
        service.StampPublicParticipant(message, context);
        snapshot.Engine.Messages.Add(message);
    }
}
