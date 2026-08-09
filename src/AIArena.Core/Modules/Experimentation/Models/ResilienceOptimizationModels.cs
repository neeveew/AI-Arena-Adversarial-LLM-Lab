using System.Collections.Immutable;

namespace AIArena.Core.Models;

public enum ArenaProviderFaultOperation
{
    ModelDiscovery,
    ChatCompletion,
    StreamingChatCompletion
}

public enum ArenaFaultObservedOutcome
{
    InjectedFailure,
    CallerCancelled
}

/// <summary>
/// Content-free observation produced by the fault decorator. The cause of the
/// injected failure is deliberately separate from recovery evidence because a
/// provider boundary cannot prove whether a higher-level retry recovered.
/// </summary>
public sealed record ArenaFaultObservation(
    string ProfileId,
    string InjectionId,
    long Sequence,
    int Occurrence,
    ArenaFaultKind InjectedCause,
    ArenaProviderFaultOperation Operation,
    ArenaFaultObservedOutcome ObservedOutcome,
    ArenaEvidenceAssertion CauseEvidence,
    ArenaEvidenceAssertion RecoveryEvidence);

public sealed record ArenaFaultInjectionOptions(
    int MaximumConcurrentRequests = 4,
    int MaximumInjectedDelayMilliseconds = 5_000,
    int MaximumRetainedObservations = 1_024);

public sealed record ArenaRouteObjective(
    string Id,
    decimal Weight,
    bool HigherIsBetter = true);

public sealed record ArenaRouteMetricEvidence(
    string Id,
    decimal? Score,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaRouteSampleEvidence(
    string RunId,
    string SetupFingerprint,
    ImmutableArray<ArenaRouteMetricEvidence> Metrics);

public sealed record ArenaRouteConstraintEvidence(
    string Id,
    ArenaRouteConstraintKind Kind,
    string Description,
    bool? Satisfied,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaRouteCandidateEvidence(
    string ModelId,
    ImmutableArray<ArenaRouteSampleEvidence> Samples,
    ImmutableArray<ArenaRouteConstraintEvidence> Constraints);

public sealed record ArenaRouteOptimizationTarget(
    string Id,
    string AgentId,
    string CurrentModelId,
    ImmutableArray<ArenaRouteObjective> Objectives,
    ImmutableArray<ArenaRouteCandidateEvidence> Candidates);

/// <summary>
/// Pure optimizer input. It contains bounded measurements and evidence
/// references only; provider configuration, endpoints, credentials, prompts,
/// and response bodies are intentionally absent.
/// </summary>
public sealed record ArenaRouteOptimizationRequest(
    string ProposalId,
    DateTimeOffset CreatedAtUtc,
    string ExperimentId,
    string SetupFingerprint,
    ImmutableArray<ArenaRouteOptimizationTarget> Targets);
