using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal static class ArenaEvaluationSchemas
{
    public const string Evaluation = "ai_arena.evaluation.v1";
    public const string Export = "ai_arena.evaluation_export.v1";
}

internal static class ArenaEvaluationStatuses
{
    public const string Unavailable = "unavailable";
    public const string NotComparable = "not_comparable";
    public const string Insufficient = "insufficient_evidence";
    public const string Unchanged = "unchanged";
    public const string Improved = "improved";
    public const string Regressed = "regressed";
}

internal static class ArenaQaGateStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Unavailable = "unavailable";
}

internal sealed record ArenaEvaluationEvidenceCounts(
    int TranscriptMessages,
    int ModelTurns,
    int SuccessfulModelTurns,
    int FailedModelTurns,
    int TranscriptErrors,
    int ProviderErrors,
    int StuckThinkingActors,
    int LatencySamples,
    int UsageSamples,
    int ThroughputSamples,
    int TimeToFirstTokenSamples,
    int VoiceStyleSamples,
    int DiscourseTurns,
    int InternetEvidenceTurns);

internal sealed record ArenaEvaluationMetrics(
    double? SuccessRatePercent,
    int? BattleReviewScore,
    double? AverageLatencyMs,
    int? P95LatencyMs,
    double? AverageGeneratedTokens,
    double? AverageTokensPerSecond,
    double? AverageVoiceStyleScore,
    int? ConsensusPercent,
    int? RoleDriftPercent,
    int? UnsupportedClaimCount,
    int? EvidencePressureScore,
    int? NarrativeHeatScore);

internal sealed record ArenaEvaluationModelAggregate(
    string Model,
    int Turns,
    int SuccessfulTurns,
    int FailedTurns,
    double? AverageLatencyMs,
    int? P95LatencyMs,
    double? AverageGeneratedTokens,
    double? AverageTokensPerSecond);

internal sealed record ArenaEvaluationRecord(
    string Schema,
    string RunId,
    string RunFingerprint,
    string ExactSetupFingerprint,
    string ScenarioFingerprint,
    string SessionId,
    DateTimeOffset CapturedAt,
    bool InternetEnabled,
    string PortableSetupJson,
    ArenaEvaluationEvidenceCounts Evidence,
    ArenaEvaluationMetrics Metrics,
    IReadOnlyList<ArenaEvaluationModelAggregate> Models);

internal sealed record ArenaEvaluationMetricComparison(
    string Id,
    string Label,
    string Status,
    double? BaselineValue,
    double? CandidateValue,
    double? Delta,
    string Unit,
    string Threshold,
    int BaselineEvidence,
    int CandidateEvidence,
    string Explanation);

internal sealed record ArenaEvaluationComparison(
    string Status,
    string BaselineRunId,
    string CandidateRunId,
    string ScenarioFingerprint,
    int ComparableMetricCount,
    int ImprovedMetricCount,
    int RegressedMetricCount,
    string Summary,
    IReadOnlyList<ArenaEvaluationMetricComparison> Metrics);

internal sealed record ArenaRuntimeQaEvidence(
    bool? InterruptionRecovered = null,
    bool? ProviderReconnectSucceeded = null);

internal sealed record ArenaRuntimeQaGate(
    string Id,
    string Status,
    bool Required,
    string Explanation,
    string Evidence);

internal sealed record ArenaRuntimeQaReport(
    string OverallReadiness,
    bool Ready,
    int Passed,
    int Warnings,
    int Failed,
    int Unavailable,
    string Summary,
    IReadOnlyList<ArenaRuntimeQaGate> Gates);

internal sealed record ArenaEvaluationExportEnvelope(
    string Schema,
    ArenaEvaluationEvidenceExport Evaluation,
    ArenaEvaluationComparison? Comparison,
    ArenaRuntimeQaReport? Qa);

internal sealed record ArenaEvaluationEvidenceExport(
    string Schema,
    string RunId,
    string RunFingerprint,
    string ExactSetupFingerprint,
    string ScenarioFingerprint,
    DateTimeOffset CapturedAt,
    bool InternetEnabled,
    ArenaEvaluationEvidenceCounts Evidence,
    ArenaEvaluationMetrics Metrics,
    IReadOnlyList<ArenaEvaluationModelAggregate> Models);

/// <summary>
/// Produces local, aggregate run evidence without retaining transcript bodies,
/// provider credentials, provider errors, or raw response payloads.
/// </summary>
internal sealed class ArenaEvaluationService
{
    private const int MinimumComparableSamples = 2;
    private const int MaximumSafeLabelLength = 160;
    private static readonly Regex SensitiveValueRegex = new(
        @"(?ix)(?:\b(?:api[_\s-]?key|access[_\s-]?token|authorization|bearer|client[_\s-]?secret|password|refresh[_\s-]?token)\b\s*(?::|=|\s)\s*[\""']?[A-Za-z0-9_+./~=-]{8,}|\bsk-(?:proj-)?[A-Za-z0-9_-]{12,}\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly DiscourseDiagnosticsService discourseDiagnostics;
    private readonly VoiceStyleAdherenceService voiceStyleAdherence;

    public ArenaEvaluationService(
        DiscourseDiagnosticsService? discourseDiagnostics = null,
        VoiceStyleAdherenceService? voiceStyleAdherence = null)
    {
        this.discourseDiagnostics = discourseDiagnostics ?? new DiscourseDiagnosticsService();
        this.voiceStyleAdherence = voiceStyleAdherence ?? new VoiceStyleAdherenceService();
    }

    public ArenaEvaluationRecord Capture(
        string sessionId,
        ArenaSnapshot snapshot,
        DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var captureTime = capturedAt ?? DateTimeOffset.UtcNow;

        var exactPackage = MatchSetupPackageCodec.FromSnapshot(sessionId, snapshot);
        var exactSetupFingerprint = MatchSetupPackageCodec.Fingerprint(exactPackage);
        var portableSetupJson = MatchSetupPackageCodec.Serialize(exactPackage);

        // FromSnapshot returns a fresh package. Alias only endpoint and model
        // identities in this copy so comparisons retain provider routing shape,
        // API mode, and every request/runtime tuning value without altering the
        // faithful, secret-free replay package captured above.
        var scenarioFingerprint = ModelNeutralSetupFingerprint(exactPackage);

        var summary = new SessionSummary(
            sessionId,
            "",
            HasSnapshot: true,
            snapshot.Engine.Messages.Count,
            CheckpointCount: 0,
            EventCount: 0,
            captureTime);
        var view = SnapshotViewMapper.FromCore(summary, snapshot);
        var messages = view.Messages
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ThenBy(message => message.SpeakerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var providerBackedSpeakerIds = snapshot.Engine.Agents
            .Select(agent => agent.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        providerBackedSpeakerIds.Add("narrator");
        var modelMessages = messages
            .Where(message => HasModelEvidence(message, providerBackedSpeakerIds))
            .ToArray();
        var successfulModelMessages = modelMessages.Where(IsSuccessful).ToArray();
        var failedModelMessages = modelMessages.Where(IsError).ToArray();
        var transcriptErrors = messages.Count(IsError);
        // Provider reachability metadata and Engine.LastError are mutable snapshot
        // state, not evidence that a provider failed during this captured run. A
        // persisted offline probe (or an unrelated engine error) must therefore
        // not be promoted into run-specific failure evidence. Failed model turns
        // are the durable, attributable provider-failure signal available here.
        var providerErrors = failedModelMessages.Length;
        var latencySamples = successfulModelMessages.Where(message => message.LatencyMs > 0).ToArray();
        var usageSamples = successfulModelMessages
            .Select(GeneratedTokens)
            .Where(tokens => tokens.HasValue)
            .Select(tokens => tokens!.Value)
            .ToArray();
        var throughputSamples = successfulModelMessages.Where(message => message.TokensPerSecond > 0).ToArray();
        var timeToFirstTokenSamples = successfulModelMessages.Where(message => message.TimeToFirstTokenMs > 0).ToArray();
        var voiceSamples = successfulModelMessages
            .Select(message => (Message: message, Diagnostic: voiceStyleAdherence.Analyze(message.VoiceStyle, message.Text)))
            .Where(item => !item.Diagnostic.State.Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var discourseTurns = messages.Count(message =>
            !message.SpeakerId.Equals("system", StringComparison.OrdinalIgnoreCase)
            && !message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase)
            && message.Kind is "message" or "" or "internet");
        var internetEvidenceTurns = messages.Count(message =>
            message.InternetSources.Count > 0
            || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase));
        var stuckThinkingActors = snapshot.Engine.Agents.Count(agent =>
            agent.Status.Equals("thinking", StringComparison.OrdinalIgnoreCase));
        if (snapshot.Engine.Narrator.Status.Equals("thinking", StringComparison.OrdinalIgnoreCase))
        {
            stuckThinkingActors++;
        }

        var personas = snapshot.Engine.Agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.Id))
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Persona ?? "", StringComparer.OrdinalIgnoreCase);
        var diagnostics = discourseDiagnostics.Analyze(
            messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn),
            personas);
        var battleReview = TranscriptAdjunctCoordinator.BuildBattleReview(messages, diagnostics);

        var evidence = new ArenaEvaluationEvidenceCounts(
            messages.Length,
            modelMessages.Length,
            successfulModelMessages.Length,
            failedModelMessages.Length,
            transcriptErrors,
            providerErrors,
            stuckThinkingActors,
            latencySamples.Length,
            usageSamples.Length,
            throughputSamples.Length,
            timeToFirstTokenSamples.Length,
            voiceSamples.Length,
            discourseTurns,
            internetEvidenceTurns);
        var metrics = new ArenaEvaluationMetrics(
            modelMessages.Length == 0
                ? null
                : Round(successfulModelMessages.Length * 100d / modelMessages.Length),
            discourseTurns == 0 ? null : battleReview.Score,
            AverageOrNull(latencySamples.Select(message => (double)message.LatencyMs)),
            Percentile95OrNull(latencySamples.Select(message => message.LatencyMs)),
            AverageOrNull(usageSamples.Select(tokens => (double)tokens)),
            AverageOrNull(throughputSamples.Select(message => message.TokensPerSecond)),
            AverageOrNull(voiceSamples.Select(item => (double)item.Diagnostic.Score)),
            discourseTurns == 0 ? null : diagnostics.ConsensusPercent,
            discourseTurns == 0 ? null : diagnostics.RoleDriftPercent,
            discourseTurns == 0 ? null : diagnostics.UnsupportedClaimCount,
            discourseTurns == 0 ? null : diagnostics.EvidencePressureScore,
            discourseTurns == 0 ? null : diagnostics.NarrativeHeatScore);
        var models = BuildModelAggregates(modelMessages);
        var runFingerprint = RunFingerprint(exactSetupFingerprint, snapshot.Engine.Internet.UseInternet, evidence, metrics, models);

        return new ArenaEvaluationRecord(
            ArenaEvaluationSchemas.Evaluation,
            RunId(runFingerprint, captureTime),
            runFingerprint,
            exactSetupFingerprint,
            scenarioFingerprint,
            SafeLabel(sessionId, "session"),
            captureTime,
            snapshot.Engine.Internet.UseInternet,
            portableSetupJson,
            evidence,
            metrics,
            models);
    }

    public ArenaEvaluationComparison Compare(
        ArenaEvaluationRecord? baseline,
        ArenaEvaluationRecord? candidate)
    {
        if (baseline is null || candidate is null)
        {
            return EmptyComparison(
                ArenaEvaluationStatuses.Unavailable,
                baseline?.RunId ?? "",
                candidate?.RunId ?? "",
                "A baseline and candidate run are both required.");
        }

        if (!baseline.ScenarioFingerprint.Equals(candidate.ScenarioFingerprint, StringComparison.Ordinal))
        {
            return EmptyComparison(
                ArenaEvaluationStatuses.NotComparable,
                baseline.RunId,
                candidate.RunId,
                "Runs use different model-neutral Match Setup fingerprints and were not compared.");
        }

        var metrics = new List<ArenaEvaluationMetricComparison>
        {
            CompareAbsolute(
                "quality.score",
                "Battle Review score",
                baseline.Metrics.BattleReviewScore,
                candidate.Metrics.BattleReviewScore,
                baseline.Evidence.DiscourseTurns,
                candidate.Evidence.DiscourseTurns,
                higherIsBetter: true,
                threshold: 5,
                unit: "points"),
            CompareAbsolute(
                "run.success-rate",
                "Successful model turns",
                baseline.Metrics.SuccessRatePercent,
                candidate.Metrics.SuccessRatePercent,
                baseline.Evidence.ModelTurns,
                candidate.Evidence.ModelTurns,
                higherIsBetter: true,
                threshold: 5,
                unit: "percentage points"),
            CompareFailureCount(baseline, candidate),
            CompareRelative(
                "telemetry.average-latency",
                "Average latency",
                baseline.Metrics.AverageLatencyMs,
                candidate.Metrics.AverageLatencyMs,
                baseline.Evidence.LatencySamples,
                candidate.Evidence.LatencySamples,
                higherIsBetter: false,
                relativeThreshold: 0.15,
                minimumAbsoluteDelta: 250,
                unit: "ms"),
            CompareResponseLength(
                baseline.Metrics.AverageGeneratedTokens,
                candidate.Metrics.AverageGeneratedTokens,
                baseline.Evidence.UsageSamples,
                candidate.Evidence.UsageSamples),
            CompareRelative(
                "telemetry.throughput",
                "Average throughput",
                baseline.Metrics.AverageTokensPerSecond,
                candidate.Metrics.AverageTokensPerSecond,
                baseline.Evidence.ThroughputSamples,
                candidate.Evidence.ThroughputSamples,
                higherIsBetter: true,
                relativeThreshold: 0.15,
                minimumAbsoluteDelta: 0,
                unit: "tokens/s"),
            CompareAbsolute(
                "quality.voice-style",
                "Voice-style fit",
                baseline.Metrics.AverageVoiceStyleScore,
                candidate.Metrics.AverageVoiceStyleScore,
                baseline.Evidence.VoiceStyleSamples,
                candidate.Evidence.VoiceStyleSamples,
                higherIsBetter: true,
                threshold: 8,
                unit: "points")
        };
        var comparable = metrics.Count(metric => metric.Status != ArenaEvaluationStatuses.Unavailable);
        var improved = metrics.Count(metric => metric.Status == ArenaEvaluationStatuses.Improved);
        var regressed = metrics.Count(metric => metric.Status == ArenaEvaluationStatuses.Regressed);
        var status = comparable == 0
            ? ArenaEvaluationStatuses.Insufficient
            : regressed > 0
                ? ArenaEvaluationStatuses.Regressed
                : improved > 0
                    ? ArenaEvaluationStatuses.Improved
                    : ArenaEvaluationStatuses.Unchanged;
        var summary = status switch
        {
            ArenaEvaluationStatuses.Regressed => $"Regression detected in {regressed} of {comparable} comparable metric(s).",
            ArenaEvaluationStatuses.Improved => $"No regression detected; {improved} of {comparable} comparable metric(s) improved.",
            ArenaEvaluationStatuses.Unchanged => $"No material change across {comparable} comparable metric(s).",
            _ => "The runs share a setup, but there is not enough metric evidence to classify a change."
        };

        return new ArenaEvaluationComparison(
            status,
            baseline.RunId,
            candidate.RunId,
            baseline.ScenarioFingerprint,
            comparable,
            improved,
            regressed,
            summary,
            metrics);
    }

    public ArenaRuntimeQaReport EvaluateQa(
        ArenaEvaluationRecord candidate,
        ArenaEvaluationRecord? baseline = null,
        ArenaRuntimeQaEvidence? runtimeEvidence = null,
        int minimumTurns = 2)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        runtimeEvidence ??= new ArenaRuntimeQaEvidence();
        minimumTurns = Math.Clamp(minimumTurns, 1, 100);
        var gates = new List<ArenaRuntimeQaGate>();

        var identitiesValid = IsFingerprint(candidate.ExactSetupFingerprint)
            && IsFingerprint(candidate.ScenarioFingerprint)
            && IsFingerprint(candidate.RunFingerprint);
        gates.Add(Gate(
            "setup.identity",
            identitiesValid ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Fail,
            required: true,
            identitiesValid
                ? "Exact, model-neutral, and run fingerprints are present."
                : "One or more required setup/run fingerprints are missing or invalid.",
            identitiesValid ? "3 validated SHA-256 fingerprints" : "fingerprint validation failed"));

        var packageParse = MatchSetupPackageCodec.Parse(candidate.PortableSetupJson);
        var packageMatches = packageParse.Ok
            && packageParse.Package is not null
            && MatchSetupPackageCodec.Fingerprint(packageParse.Package)
                .Equals(candidate.ExactSetupFingerprint, StringComparison.Ordinal);
        gates.Add(Gate(
            "setup.replay-package",
            packageMatches ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Fail,
            required: true,
            packageMatches
                ? "The exact secret-free Match Setup package is valid and matches the run fingerprint."
                : "The replay package is unavailable, invalid, or does not match the exact setup fingerprint.",
            packageMatches ? $"{Encoding.UTF8.GetByteCount(candidate.PortableSetupJson):N0} UTF-8 bytes" : "no valid replay evidence"));

        var minimumTurnStatus = candidate.Evidence.ModelTurns == 0
            ? ArenaQaGateStatuses.Unavailable
            : candidate.Evidence.ModelTurns >= minimumTurns
                ? ArenaQaGateStatuses.Pass
                : ArenaQaGateStatuses.Fail;
        gates.Add(Gate(
            "run.minimum-turn-sample",
            minimumTurnStatus,
            required: true,
            minimumTurnStatus switch
            {
                ArenaQaGateStatuses.Pass => "The run contains the configured minimum model-turn sample.",
                ArenaQaGateStatuses.Fail => "The attempted run is too small for the configured runtime QA sample.",
                _ => "No model-turn evidence is available; sample sufficiency was not evaluated."
            },
            $"{candidate.Evidence.ModelTurns}/{minimumTurns} model turn(s)"));

        var providerErrorStatus = candidate.Evidence.ModelTurns == 0
            ? ArenaQaGateStatuses.Unavailable
            : candidate.Evidence.ProviderErrors == 0
                ? ArenaQaGateStatuses.Pass
                : ArenaQaGateStatuses.Fail;
        gates.Add(Gate(
            "runtime.provider-errors",
            providerErrorStatus,
            required: true,
            providerErrorStatus switch
            {
                ArenaQaGateStatuses.Pass => "No failed provider-backed model turn was captured.",
                ArenaQaGateStatuses.Fail => "Provider-backed model turns failed; error bodies are intentionally not retained.",
                _ => "No model-turn evidence is available; provider failure state was not evaluated."
            },
            $"{candidate.Evidence.ProviderErrors}/{candidate.Evidence.ModelTurns} failed provider-backed model turn(s)"));

        gates.Add(Gate(
            "runtime.transcript-errors",
            candidate.Evidence.TranscriptErrors == 0 ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Fail,
            required: true,
            candidate.Evidence.TranscriptErrors == 0
                ? "No failed/error transcript records were captured."
                : "Failed/error transcript records were captured; message bodies are intentionally not retained.",
            $"{candidate.Evidence.TranscriptErrors} transcript error record(s)"));

        gates.Add(Gate(
            "runtime.stuck-thinking",
            candidate.Evidence.StuckThinkingActors == 0 ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Fail,
            required: true,
            candidate.Evidence.StuckThinkingActors == 0
                ? "No agent or narrator remained in thinking state at capture time."
                : "One or more actors remained in thinking state at capture time.",
            $"{candidate.Evidence.StuckThinkingActors} stuck actor(s)"));

        gates.Add(BooleanEvidenceGate(
            "runtime.interruption-recovery",
            runtimeEvidence.InterruptionRecovered,
            "Interrupted work recovered without a stuck state.",
            "Interruption recovery failed or left the runtime unhealthy.",
            "No interruption recovery probe was supplied.",
            required: runtimeEvidence.InterruptionRecovered.HasValue));
        gates.Add(BooleanEvidenceGate(
            "runtime.provider-reconnect",
            runtimeEvidence.ProviderReconnectSucceeded,
            "Provider reconnect completed successfully.",
            "Provider reconnect did not complete successfully.",
            "No provider reconnect probe was supplied.",
            required: runtimeEvidence.ProviderReconnectSucceeded.HasValue));

        gates.Add(CompletenessGate(
            "telemetry.latency-completeness",
            "latency",
            candidate.Evidence.LatencySamples,
            candidate.Evidence.SuccessfulModelTurns,
            required: true));
        gates.Add(CompletenessGate(
            "telemetry.usage-completeness",
            "generated-token usage",
            candidate.Evidence.UsageSamples,
            candidate.Evidence.SuccessfulModelTurns,
            required: true));

        var qualityStatus = candidate.Evidence.DiscourseTurns == 0
            ? ArenaQaGateStatuses.Unavailable
            : candidate.Evidence.DiscourseTurns < minimumTurns
                ? ArenaQaGateStatuses.Warn
                : ArenaQaGateStatuses.Pass;
        gates.Add(Gate(
            "quality.sample",
            qualityStatus,
            required: true,
            qualityStatus switch
            {
                ArenaQaGateStatuses.Pass => "The Battle Review score has a multi-turn discourse sample.",
                ArenaQaGateStatuses.Warn => "A quality score exists, but its discourse sample is smaller than the configured minimum.",
                _ => "No discourse evidence is available; no quality claim is made."
            },
            $"{candidate.Evidence.DiscourseTurns}/{minimumTurns} discourse turn(s)"));

        var internetStatus = !candidate.InternetEnabled || candidate.Evidence.InternetEvidenceTurns == 0
            ? ArenaQaGateStatuses.Unavailable
            : ArenaQaGateStatuses.Pass;
        gates.Add(Gate(
            "internet.evidence",
            internetStatus,
            required: candidate.InternetEnabled,
            !candidate.InternetEnabled
                ? "Internet was disabled; this run makes no Internet coverage claim."
                : candidate.Evidence.InternetEvidenceTurns == 0
                    ? "Internet was enabled, but no sourced Internet turn was captured; no Internet coverage claim is made."
                    : "At least one sourced Internet turn was captured.",
            $"{candidate.Evidence.InternetEvidenceTurns} sourced Internet turn(s)"));

        if (baseline is null)
        {
            gates.Add(Gate(
                "baseline.comparable",
                ArenaQaGateStatuses.Unavailable,
                required: false,
                "No baseline was supplied; no repeatability or cross-model comparison claim is made.",
                "0 baseline runs"));
            gates.Add(Gate(
                "baseline.regression",
                ArenaQaGateStatuses.Unavailable,
                required: false,
                "No comparable baseline was supplied; no regression claim is made.",
                "0 comparable metrics"));
        }
        else
        {
            var comparison = Compare(baseline, candidate);
            var comparable = comparison.Status is not ArenaEvaluationStatuses.NotComparable
                and not ArenaEvaluationStatuses.Unavailable;
            gates.Add(Gate(
                "baseline.comparable",
                comparable ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Fail,
                required: true,
                comparable
                    ? "Baseline and candidate share the model-neutral Match Setup fingerprint."
                    : "Baseline and candidate do not share a comparable model-neutral setup.",
                comparable ? "scenario fingerprints match" : "scenario fingerprints differ"));
            var regressionStatus = comparison.Status switch
            {
                ArenaEvaluationStatuses.Regressed => ArenaQaGateStatuses.Fail,
                ArenaEvaluationStatuses.Insufficient => ArenaQaGateStatuses.Unavailable,
                ArenaEvaluationStatuses.NotComparable => ArenaQaGateStatuses.Fail,
                ArenaEvaluationStatuses.Unavailable => ArenaQaGateStatuses.Unavailable,
                _ => ArenaQaGateStatuses.Pass
            };
            gates.Add(Gate(
                "baseline.regression",
                regressionStatus,
                required: true,
                comparison.Summary,
                $"{comparison.ComparableMetricCount} comparable, {comparison.RegressedMetricCount} regressed metric(s)"));
        }

        var passed = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Pass);
        var warnings = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Warn);
        var failed = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Fail);
        var unavailable = gates.Count(gate => gate.Status == ArenaQaGateStatuses.Unavailable);
        var requiredFailure = gates.Any(gate => gate.Required && gate.Status == ArenaQaGateStatuses.Fail);
        var requiredIncomplete = gates.Any(gate => gate.Required
            && gate.Status is ArenaQaGateStatuses.Warn or ArenaQaGateStatuses.Unavailable);
        var readiness = requiredFailure
            ? "blocked"
            : requiredIncomplete
                ? "partial"
                : "ready";
        var summary = readiness switch
        {
            "blocked" => $"Runtime QA blocked: {failed} gate(s) failed.",
            "partial" => "Runtime QA is partial because required evidence is incomplete.",
            _ when unavailable > 0 => $"All required runtime QA gates passed; {unavailable} optional evidence gate(s) remain unavailable.",
            _ => "All required runtime QA gates passed."
        };
        return new ArenaRuntimeQaReport(
            readiness,
            readiness.Equals("ready", StringComparison.Ordinal),
            passed,
            warnings,
            failed,
            unavailable,
            summary,
            gates);
    }

    public string ExportJson(
        ArenaEvaluationRecord evaluation,
        ArenaEvaluationComparison? comparison = null,
        ArenaRuntimeQaReport? qa = null)
    {
        var normalized = NormalizeForStorage(evaluation)
            ?? throw new InvalidOperationException("Evaluation evidence is invalid or no longer matches its replay package.");
        var envelope = new ArenaEvaluationExportEnvelope(
            ArenaEvaluationSchemas.Export,
            EvidenceExport(normalized),
            comparison,
            qa);
        return JsonSerializer.Serialize(envelope, ExportJsonOptions);
    }

    private static ArenaEvaluationEvidenceExport EvidenceExport(ArenaEvaluationRecord evaluation)
    {
        return new ArenaEvaluationEvidenceExport(
            evaluation.Schema,
            evaluation.RunId,
            evaluation.RunFingerprint,
            evaluation.ExactSetupFingerprint,
            evaluation.ScenarioFingerprint,
            evaluation.CapturedAt,
            evaluation.InternetEnabled,
            evaluation.Evidence,
            evaluation.Metrics,
            evaluation.Models);
    }

    internal ArenaEvaluationRecord? NormalizeForStorage(ArenaEvaluationRecord? evaluation)
    {
        if (evaluation is null || evaluation.Evidence is null || evaluation.Metrics is null || evaluation.Models is null)
        {
            return null;
        }

        var parsed = MatchSetupPackageCodec.Parse(evaluation.PortableSetupJson);
        if (!parsed.Ok || parsed.Package is null)
        {
            return null;
        }

        var package = parsed.Package;
        var exactFingerprint = MatchSetupPackageCodec.Fingerprint(package);
        var portableSetupJson = MatchSetupPackageCodec.Serialize(package);
        var scenarioFingerprint = ModelNeutralSetupFingerprint(package);
        if (!exactFingerprint.Equals(evaluation.ExactSetupFingerprint, StringComparison.Ordinal)
            || !scenarioFingerprint.Equals(evaluation.ScenarioFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var evidence = NormalizeEvidence(evaluation.Evidence);
        var metrics = NormalizeMetrics(evaluation.Metrics);
        var models = evaluation.Models
            .Where(model => model is not null)
            .Select(NormalizeModel)
            .Where(model => !string.IsNullOrWhiteSpace(model.Model))
            .GroupBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var runFingerprint = RunFingerprint(
            exactFingerprint,
            evaluation.InternetEnabled,
            evidence,
            metrics,
            models);
        var captureTime = evaluation.CapturedAt == default ? DateTimeOffset.UnixEpoch : evaluation.CapturedAt;
        return new ArenaEvaluationRecord(
            ArenaEvaluationSchemas.Evaluation,
            RunId(runFingerprint, captureTime),
            runFingerprint,
            exactFingerprint,
            scenarioFingerprint,
            SafeLabel(evaluation.SessionId, "session"),
            captureTime,
            evaluation.InternetEnabled,
            portableSetupJson,
            evidence,
            metrics,
            models);
    }

    private static string ModelNeutralSetupFingerprint(MatchSetupPackage package)
    {
        var providerAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in package.Setup.Providers
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(item => item.Value)
                     .Where(provider => provider is not null))
        {
            provider.BaseUrl = IdentityAlias(provider.BaseUrl, "provider", providerAliases);
            provider.Model = IdentityAlias(provider.Model, "model", modelAliases);
        }

        return MatchSetupPackageCodec.Fingerprint(package);
    }

    private static string IdentityAlias(
        string value,
        string prefix,
        IDictionary<string, string> aliases)
    {
        var identity = value?.Trim() ?? "";
        if (identity.Length == 0)
        {
            return "";
        }

        if (!aliases.TryGetValue(identity, out var alias))
        {
            alias = $"<{prefix}-{aliases.Count + 1}>";
            aliases[identity] = alias;
        }

        return alias;
    }

    private static ArenaEvaluationComparison EmptyComparison(
        string status,
        string baselineRunId,
        string candidateRunId,
        string summary)
    {
        return new ArenaEvaluationComparison(
            status,
            baselineRunId,
            candidateRunId,
            "",
            0,
            0,
            0,
            summary,
            []);
    }

    private static ArenaEvaluationMetricComparison CompareFailureCount(
        ArenaEvaluationRecord baseline,
        ArenaEvaluationRecord candidate)
    {
        if (baseline.Evidence.ModelTurns < MinimumComparableSamples
            || candidate.Evidence.ModelTurns < MinimumComparableSamples)
        {
            return UnavailableMetric(
                "run.failed-turns",
                "Failed model turns",
                baseline.Evidence.FailedModelTurns,
                candidate.Evidence.FailedModelTurns,
                baseline.Evidence.ModelTurns,
                candidate.Evidence.ModelTurns,
                "turns",
                "any increase",
                "At least two model turns are required in each run.");
        }

        var delta = candidate.Evidence.FailedModelTurns - baseline.Evidence.FailedModelTurns;
        var status = delta > 0
            ? ArenaEvaluationStatuses.Regressed
            : delta < 0
                ? ArenaEvaluationStatuses.Improved
                : ArenaEvaluationStatuses.Unchanged;
        return new ArenaEvaluationMetricComparison(
            "run.failed-turns",
            "Failed model turns",
            status,
            baseline.Evidence.FailedModelTurns,
            candidate.Evidence.FailedModelTurns,
            delta,
            "turns",
            "any increase",
            baseline.Evidence.ModelTurns,
            candidate.Evidence.ModelTurns,
            status switch
            {
                ArenaEvaluationStatuses.Regressed => "The candidate contains more failed model turns.",
                ArenaEvaluationStatuses.Improved => "The candidate contains fewer failed model turns.",
                _ => "Failed model-turn count is unchanged."
            });
    }

    private static ArenaEvaluationMetricComparison CompareAbsolute(
        string id,
        string label,
        double? baseline,
        double? candidate,
        int baselineEvidence,
        int candidateEvidence,
        bool higherIsBetter,
        double threshold,
        string unit)
    {
        if (baseline is null || candidate is null
            || baselineEvidence < MinimumComparableSamples
            || candidateEvidence < MinimumComparableSamples)
        {
            return UnavailableMetric(
                id,
                label,
                baseline,
                candidate,
                baselineEvidence,
                candidateEvidence,
                unit,
                $"{threshold.ToString("0.#", CultureInfo.InvariantCulture)} {unit}",
                "At least two evidenced samples are required in each run.");
        }

        var delta = Round(candidate.Value - baseline.Value);
        var directionalDelta = higherIsBetter ? delta : -delta;
        var status = directionalDelta <= -threshold
            ? ArenaEvaluationStatuses.Regressed
            : directionalDelta >= threshold
                ? ArenaEvaluationStatuses.Improved
                : ArenaEvaluationStatuses.Unchanged;
        return new ArenaEvaluationMetricComparison(
            id,
            label,
            status,
            Round(baseline.Value),
            Round(candidate.Value),
            delta,
            unit,
            $"material at ±{threshold.ToString("0.#", CultureInfo.InvariantCulture)} {unit}",
            baselineEvidence,
            candidateEvidence,
            MetricExplanation(label, status, delta, unit));
    }

    private static ArenaEvaluationMetricComparison CompareRelative(
        string id,
        string label,
        double? baseline,
        double? candidate,
        int baselineEvidence,
        int candidateEvidence,
        bool higherIsBetter,
        double relativeThreshold,
        double minimumAbsoluteDelta,
        string unit)
    {
        if (baseline is null || candidate is null || baseline <= 0
            || baselineEvidence < MinimumComparableSamples
            || candidateEvidence < MinimumComparableSamples)
        {
            return UnavailableMetric(
                id,
                label,
                baseline,
                candidate,
                baselineEvidence,
                candidateEvidence,
                unit,
                $"{relativeThreshold:P0}",
                "At least two positive evidenced samples are required in each run.");
        }

        var delta = candidate.Value - baseline.Value;
        var relativeDelta = delta / baseline.Value;
        var material = Math.Abs(relativeDelta) >= relativeThreshold
            && Math.Abs(delta) >= minimumAbsoluteDelta;
        var directionalDelta = higherIsBetter ? relativeDelta : -relativeDelta;
        var status = !material
            ? ArenaEvaluationStatuses.Unchanged
            : directionalDelta < 0
                ? ArenaEvaluationStatuses.Regressed
                : ArenaEvaluationStatuses.Improved;
        var threshold = minimumAbsoluteDelta > 0
            ? $"{relativeThreshold:P0} and {minimumAbsoluteDelta:0.#} {unit}"
            : $"{relativeThreshold:P0}";
        return new ArenaEvaluationMetricComparison(
            id,
            label,
            status,
            Round(baseline.Value),
            Round(candidate.Value),
            Round(delta),
            unit,
            threshold,
            baselineEvidence,
            candidateEvidence,
            MetricExplanation(label, status, Round(delta), unit));
    }

    private static ArenaEvaluationMetricComparison CompareResponseLength(
        double? baseline,
        double? candidate,
        int baselineEvidence,
        int candidateEvidence)
    {
        const double relativeReductionThreshold = 0.20;
        const double minimumTokenReduction = 32;
        const string id = "telemetry.generated-tokens";
        const string label = "Average generated tokens";
        const string threshold = "decrease at least 20% and 32 tokens";
        if (baseline is null || candidate is null || baseline <= 0
            || baselineEvidence < MinimumComparableSamples
            || candidateEvidence < MinimumComparableSamples)
        {
            return UnavailableMetric(
                id,
                label,
                baseline,
                candidate,
                baselineEvidence,
                candidateEvidence,
                "tokens",
                threshold,
                "At least two evidenced generated-token samples are required in each run.");
        }

        var delta = candidate.Value - baseline.Value;
        var materialReduction = delta <= -minimumTokenReduction
            && delta / baseline.Value <= -relativeReductionThreshold;
        var status = materialReduction
            ? ArenaEvaluationStatuses.Regressed
            : ArenaEvaluationStatuses.Unchanged;
        return new ArenaEvaluationMetricComparison(
            id,
            label,
            status,
            Round(baseline.Value),
            Round(candidate.Value),
            Round(delta),
            "tokens",
            threshold,
            baselineEvidence,
            candidateEvidence,
            materialReduction
                ? "The candidate generated materially fewer tokens; inspect for truncation or lost response detail."
                : "No material response-length reduction was observed; increases are reported without being labelled an improvement.");
    }

    private static ArenaEvaluationMetricComparison UnavailableMetric(
        string id,
        string label,
        double? baseline,
        double? candidate,
        int baselineEvidence,
        int candidateEvidence,
        string unit,
        string threshold,
        string explanation)
    {
        return new ArenaEvaluationMetricComparison(
            id,
            label,
            ArenaEvaluationStatuses.Unavailable,
            baseline,
            candidate,
            null,
            unit,
            threshold,
            baselineEvidence,
            candidateEvidence,
            explanation);
    }

    private static string MetricExplanation(string label, string status, double delta, string unit)
    {
        return status switch
        {
            ArenaEvaluationStatuses.Regressed => $"{label} regressed by {Math.Abs(delta):0.##} {unit} beyond the declared threshold.",
            ArenaEvaluationStatuses.Improved => $"{label} improved by {Math.Abs(delta):0.##} {unit} beyond the declared threshold.",
            _ => $"{label} changed by {Math.Abs(delta):0.##} {unit}, below the declared threshold."
        };
    }

    private static ArenaRuntimeQaGate BooleanEvidenceGate(
        string id,
        bool? evidence,
        string pass,
        string fail,
        string unavailable,
        bool required)
    {
        return Gate(
            id,
            evidence is null
                ? ArenaQaGateStatuses.Unavailable
                : evidence.Value
                    ? ArenaQaGateStatuses.Pass
                    : ArenaQaGateStatuses.Fail,
            required,
            evidence is null ? unavailable : evidence.Value ? pass : fail,
            evidence is null ? "probe not run" : evidence.Value ? "probe passed" : "probe failed");
    }

    private static ArenaRuntimeQaGate CompletenessGate(
        string id,
        string label,
        int samples,
        int successfulTurns,
        bool required)
    {
        if (successfulTurns <= 0 || samples <= 0)
        {
            return Gate(
                id,
                ArenaQaGateStatuses.Unavailable,
                required,
                $"No {label} evidence is available; no completeness claim is made.",
                $"{samples}/{successfulTurns} successful turn(s)");
        }

        var complete = samples >= successfulTurns;
        return Gate(
            id,
            complete ? ArenaQaGateStatuses.Pass : ArenaQaGateStatuses.Warn,
            required,
            complete
                ? $"{label} telemetry is present for every successful model turn."
                : $"{label} telemetry is incomplete; aggregate comparisons use only evidenced samples.",
            $"{samples}/{successfulTurns} successful turn(s)");
    }

    private static ArenaRuntimeQaGate Gate(
        string id,
        string status,
        bool required,
        string explanation,
        string evidence)
    {
        return new ArenaRuntimeQaGate(id, status, required, explanation, evidence);
    }

    private static IReadOnlyList<ArenaEvaluationModelAggregate> BuildModelAggregates(
        IReadOnlyList<TranscriptMessage> messages)
    {
        return messages
            .GroupBy(message => SafeModelLabel(message.Model), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var items = group.ToArray();
                var successes = items.Where(IsSuccessful).ToArray();
                var latency = successes.Where(message => message.LatencyMs > 0).ToArray();
                var usage = successes
                    .Select(GeneratedTokens)
                    .Where(tokens => tokens.HasValue)
                    .Select(tokens => tokens!.Value)
                    .ToArray();
                var throughput = successes.Where(message => message.TokensPerSecond > 0).ToArray();
                return new ArenaEvaluationModelAggregate(
                    group.Key,
                    items.Length,
                    successes.Length,
                    items.Count(IsError),
                    AverageOrNull(latency.Select(message => (double)message.LatencyMs)),
                    Percentile95OrNull(latency.Select(message => message.LatencyMs)),
                    AverageOrNull(usage.Select(tokens => (double)tokens)),
                    AverageOrNull(throughput.Select(message => message.TokensPerSecond)));
            })
            .OrderBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private static ArenaEvaluationEvidenceCounts NormalizeEvidence(ArenaEvaluationEvidenceCounts evidence)
    {
        return new ArenaEvaluationEvidenceCounts(
            NonNegative(evidence.TranscriptMessages),
            NonNegative(evidence.ModelTurns),
            NonNegative(evidence.SuccessfulModelTurns),
            NonNegative(evidence.FailedModelTurns),
            NonNegative(evidence.TranscriptErrors),
            NonNegative(evidence.ProviderErrors),
            NonNegative(evidence.StuckThinkingActors),
            NonNegative(evidence.LatencySamples),
            NonNegative(evidence.UsageSamples),
            NonNegative(evidence.ThroughputSamples),
            NonNegative(evidence.TimeToFirstTokenSamples),
            NonNegative(evidence.VoiceStyleSamples),
            NonNegative(evidence.DiscourseTurns),
            NonNegative(evidence.InternetEvidenceTurns));
    }

    private static ArenaEvaluationMetrics NormalizeMetrics(ArenaEvaluationMetrics metrics)
    {
        return new ArenaEvaluationMetrics(
            FiniteOrNull(metrics.SuccessRatePercent, 0, 100),
            IntegerOrNull(metrics.BattleReviewScore, 0, 100),
            FiniteOrNull(metrics.AverageLatencyMs, 0, int.MaxValue),
            IntegerOrNull(metrics.P95LatencyMs, 0, int.MaxValue),
            FiniteOrNull(metrics.AverageGeneratedTokens, 0, int.MaxValue),
            FiniteOrNull(metrics.AverageTokensPerSecond, 0, 1_000_000),
            FiniteOrNull(metrics.AverageVoiceStyleScore, 0, 100),
            IntegerOrNull(metrics.ConsensusPercent, 0, 100),
            IntegerOrNull(metrics.RoleDriftPercent, 0, 100),
            IntegerOrNull(metrics.UnsupportedClaimCount, 0, int.MaxValue),
            IntegerOrNull(metrics.EvidencePressureScore, 0, 100),
            IntegerOrNull(metrics.NarrativeHeatScore, 0, 100));
    }

    private static ArenaEvaluationModelAggregate NormalizeModel(ArenaEvaluationModelAggregate model)
    {
        return new ArenaEvaluationModelAggregate(
            SafeModelLabel(model.Model),
            NonNegative(model.Turns),
            NonNegative(model.SuccessfulTurns),
            NonNegative(model.FailedTurns),
            FiniteOrNull(model.AverageLatencyMs, 0, int.MaxValue),
            IntegerOrNull(model.P95LatencyMs, 0, int.MaxValue),
            FiniteOrNull(model.AverageGeneratedTokens, 0, int.MaxValue),
            FiniteOrNull(model.AverageTokensPerSecond, 0, 1_000_000));
    }

    private static string RunFingerprint(
        string exactSetupFingerprint,
        bool internetEnabled,
        ArenaEvaluationEvidenceCounts evidence,
        ArenaEvaluationMetrics metrics,
        IReadOnlyList<ArenaEvaluationModelAggregate> models)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            exactSetupFingerprint,
            internetEnabled,
            evidence,
            metrics,
            models = models.OrderBy(model => model.Model, StringComparer.OrdinalIgnoreCase).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string RunId(string fingerprint, DateTimeOffset capturedAt)
    {
        var captureIdentity = $"{fingerprint}\n{capturedAt.ToUniversalTime():O}";
        var captureFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(captureIdentity)))
            .ToLowerInvariant();
        return $"eval-{fingerprint[..12]}-{captureFingerprint[..12]}";
    }

    private static bool HasModelEvidence(
        TranscriptMessage message,
        IReadOnlySet<string> providerBackedSpeakerIds)
    {
        return providerBackedSpeakerIds.Contains(message.SpeakerId)
            && (string.IsNullOrWhiteSpace(message.Kind)
                || message.Kind.Equals("message", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(message.Model)
            && message.Model != "-";
    }

    private static int? GeneratedTokens(TranscriptMessage message)
    {
        if (message.CompletionTokens > 0)
        {
            return message.CompletionTokens;
        }

        return message.TotalTokens > 0 && message.PromptTokens > 0
            ? Math.Max(0, message.TotalTokens - message.PromptTokens)
            : null;
    }

    private static bool IsSuccessful(TranscriptMessage message)
    {
        return !IsError(message)
            && !message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            && !message.Status.Equals("thinking", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsError(TranscriptMessage message)
    {
        return message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || message.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || message.Status.Equals("interrupted", StringComparison.OrdinalIgnoreCase);
    }

    private static double? AverageOrNull(IEnumerable<double> values)
    {
        var samples = values.Where(double.IsFinite).ToArray();
        return samples.Length == 0 ? null : Round(samples.Average());
    }

    private static int? Percentile95OrNull(IEnumerable<int> values)
    {
        var samples = values.Where(value => value >= 0).OrderBy(value => value).ToArray();
        if (samples.Length == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1);
        return samples[index];
    }

    private static int NonNegative(int value) => Math.Max(0, value);

    private static double? FiniteOrNull(double? value, double minimum, double maximum)
    {
        return value is null || !double.IsFinite(value.Value)
            ? null
            : Round(Math.Clamp(value.Value, minimum, maximum));
    }

    private static int? IntegerOrNull(int? value, int minimum, int maximum)
    {
        return value is null ? null : Math.Clamp(value.Value, minimum, maximum);
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static string SafeLabel(string? value, string fallback)
    {
        var text = string.Join(
            " ",
            (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (text.Contains("://", StringComparison.Ordinal) || SensitiveValueRegex.IsMatch(text))
        {
            return $"redacted-{fallback}";
        }

        return text.Length <= MaximumSafeLabelLength
            ? text
            : text[..MaximumSafeLabelLength].TrimEnd();
    }

    private static string SafeModelLabel(string? value)
    {
        var text = string.Join(
            " ",
            (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var absolutePath = Regex.IsMatch(text, @"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)
            || text.StartsWith(@"\\", StringComparison.Ordinal)
            || text.StartsWith("/", StringComparison.Ordinal);
        if (absolutePath)
        {
            var leaf = text
                .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            return SafeLabel(leaf, "model");
        }

        return SafeLabel(text, "model");
    }

    private static bool IsFingerprint(string value)
    {
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
