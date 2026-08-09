using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Pure, proposal-only route comparison. It never receives an ArenaSnapshot or
/// ModelProviderConfig and therefore cannot mutate ModelProviderRouting. The
/// returned v1 contract still requires separate explicit approval and an
/// application service before any route can change.
/// </summary>
public static class ArenaModelRoutingOptimizer
{
    public static ArenaRouteProposalContract Propose(ArenaRouteOptimizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestShape(request);

        var changes = request.Targets
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(target => EvaluateTarget(request, target))
            .OrderBy(item => item.Change.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        var status = changes.Any(item => item.Change.EvidenceSufficiency != ArenaEvidenceSufficiency.Sufficient)
            ? ArenaRouteProposalStatus.InsufficientEvidence
            : changes.All(item => item.SupportsChange)
                ? ArenaRouteProposalStatus.Proposed
                : ArenaRouteProposalStatus.Dismissed;

        var proposal = new ArenaRouteProposalContract(
            ArenaContractSchemas.RouteProposal,
            request.ProposalId,
            request.CreatedAtUtc,
            request.ExperimentId,
            request.SetupFingerprint,
            status,
            [.. changes.Select(item => item.Change)]);

        var validation = ArenaContractCodec.Validate(proposal);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
        }

        return proposal;
    }

    private static TargetEvaluation EvaluateTarget(
        ArenaRouteOptimizationRequest request,
        ArenaRouteOptimizationTarget target)
    {
        var current = target.Candidates.Single(item =>
            string.Equals(item.ModelId, target.CurrentModelId, StringComparison.Ordinal));
        var alternatives = target.Candidates
            .Where(item => !string.Equals(item.ModelId, target.CurrentModelId, StringComparison.Ordinal))
            .OrderBy(item => item.ModelId, StringComparer.Ordinal)
            .ToArray();

        var evaluated = alternatives
            .Select(candidate => EvaluateCandidate(request, target, current, candidate))
            .OrderByDescending(item => item.ComparableWeightedScore)
            .ThenBy(item => item.Candidate.ModelId, StringComparer.Ordinal)
            .ToArray();
        var selected = evaluated.FirstOrDefault(item => item.Sufficiency == ArenaEvidenceSufficiency.Sufficient)
            ?? evaluated.OrderByDescending(item => item.Sufficiency).ThenBy(item => item.Candidate.ModelId, StringComparer.Ordinal).First();

        var supportsChange = selected.Sufficiency == ArenaEvidenceSufficiency.Sufficient
            && selected.CurrentWeightedScore is not null
            && selected.ProposedWeightedScore is not null
            && selected.ProposedWeightedScore.Value > selected.CurrentWeightedScore.Value;
        var reason = selected.Sufficiency != ArenaEvidenceSufficiency.Sufficient
            ? "No optimization is claimed because comparable evidence is insufficient."
            : supportsChange
                ? "Observed comparable samples support a higher weighted score; no route is applied automatically."
                : "Observed comparable samples do not support changing the route; no route is applied automatically.";

        var evidence = selected.Sufficiency switch
        {
            ArenaEvidenceSufficiency.Sufficient => new ArenaEvidenceAssertion(
                $"route-evidence:{StableSuffix(request.ProposalId, target.Id, selected.Candidate.ModelId)}",
                ArenaEvidenceState.Inferred,
                supportsChange
                    ? "A route change is proposed from observed comparable measurements."
                    : "A route change is not supported by observed comparable measurements.",
                Basis: "Weighted comparison of observed same-setup samples and observed constraints."),
            ArenaEvidenceSufficiency.Partial => new ArenaEvidenceAssertion(
                $"route-evidence:{StableSuffix(request.ProposalId, target.Id, selected.Candidate.ModelId)}",
                ArenaEvidenceState.Inferred,
                "The comparison is partial and cannot establish an optimization.",
                Basis: selected.Limitation),
            _ => new ArenaEvidenceAssertion(
                $"route-evidence:{StableSuffix(request.ProposalId, target.Id, selected.Candidate.ModelId)}",
                ArenaEvidenceState.Unavailable,
                "Comparable route evidence is unavailable.",
                Limitation: selected.Limitation)
        };

        var change = new ArenaModelRouteChange(
            $"route-change:{StableSuffix(request.ProposalId, target.Id, selected.Candidate.ModelId)}",
            target.AgentId,
            target.CurrentModelId,
            selected.Candidate.ModelId,
            reason,
            true,
            selected.RunIds,
            selected.ComparableSampleCount,
            selected.Sufficiency,
            selected.Components,
            selected.Constraints,
            evidence);
        return new TargetEvaluation(change, supportsChange);
    }

    private static CandidateEvaluation EvaluateCandidate(
        ArenaRouteOptimizationRequest request,
        ArenaRouteOptimizationTarget target,
        ArenaRouteCandidateEvidence current,
        ArenaRouteCandidateEvidence candidate)
    {
        var sameSetup = current.Samples.Concat(candidate.Samples).All(item =>
            string.Equals(item.SetupFingerprint, request.SetupFingerprint, StringComparison.Ordinal));
        var comparableSampleCount = sameSetup ? Math.Min(current.Samples.Length, candidate.Samples.Length) : 0;
        var runIds = sameSetup
            ? current.Samples.Concat(candidate.Samples).Select(item => item.RunId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray()
            : ImmutableArray<string>.Empty;

        var components = target.Objectives
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(objective => AggregateComponent(request, target, current, candidate, objective, sameSetup))
            .ToImmutableArray();
        var constraints = candidate.Constraints
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new ArenaRouteConstraint(item.Id, item.Kind, item.Description, item.Satisfied, item.Evidence))
            .ToImmutableArray();

        var hasHardware = constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Hardware);
        var hasCapability = constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Capability);
        var enoughSamples = comparableSampleCount >= 2;
        var observedScores = components.All(item => item.Evidence.State == ArenaEvidenceState.Observed
            && item.CurrentScore is not null
            && item.ProposedScore is not null);
        var observedConstraints = constraints.Length > 0
            && constraints.All(item => item.Evidence.State == ArenaEvidenceState.Observed && item.Satisfied == true);
        var hasUnavailable = components.Any(item => item.Evidence.State == ArenaEvidenceState.Unavailable)
            || constraints.Any(item => item.Evidence.State == ArenaEvidenceState.Unavailable);
        var sufficient = sameSetup && enoughSamples && observedScores && observedConstraints && hasHardware && hasCapability;
        var partial = sameSetup
            && comparableSampleCount > 0
            && !hasUnavailable
            && components.Any(item => item.CurrentScore is not null && item.ProposedScore is not null);
        var sufficiency = sufficient
            ? ArenaEvidenceSufficiency.Sufficient
            : partial ? ArenaEvidenceSufficiency.Partial : ArenaEvidenceSufficiency.Insufficient;

        var currentWeighted = WeightedScore(components, useProposed: false, target.Objectives);
        var proposedWeighted = WeightedScore(components, useProposed: true, target.Objectives);
        var limitation = Limitations(
            sameSetup,
            enoughSamples,
            observedScores,
            observedConstraints,
            hasHardware,
            hasCapability);

        return new CandidateEvaluation(
            candidate,
            comparableSampleCount,
            sufficiency,
            components,
            constraints,
            runIds,
            currentWeighted,
            proposedWeighted,
            proposedWeighted ?? decimal.MinValue,
            limitation);
    }

    private static ArenaRouteScoreComponent AggregateComponent(
        ArenaRouteOptimizationRequest request,
        ArenaRouteOptimizationTarget target,
        ArenaRouteCandidateEvidence current,
        ArenaRouteCandidateEvidence candidate,
        ArenaRouteObjective objective,
        bool sameSetup)
    {
        if (!sameSetup)
        {
            return UnavailableComponent(request, target, candidate, objective, "Samples do not share the required setup fingerprint.");
        }

        var currentMetrics = MetricsFor(current, objective.Id);
        var candidateMetrics = MetricsFor(candidate, objective.Id);
        if (currentMetrics.Length != current.Samples.Length
            || candidateMetrics.Length != candidate.Samples.Length
            || currentMetrics.Length == 0
            || candidateMetrics.Length == 0
            || currentMetrics.Any(item => item.Score is null || item.Evidence.State == ArenaEvidenceState.Unavailable)
            || candidateMetrics.Any(item => item.Score is null || item.Evidence.State == ArenaEvidenceState.Unavailable))
        {
            return UnavailableComponent(request, target, candidate, objective, "One or more required metric values are unavailable.");
        }

        var currentScore = currentMetrics.Average(item => item.Score!.Value);
        var proposedScore = candidateMetrics.Average(item => item.Score!.Value);
        if (!objective.HigherIsBetter)
        {
            currentScore = 1m - currentScore;
            proposedScore = 1m - proposedScore;
        }

        var state = currentMetrics.Concat(candidateMetrics).All(item => item.Evidence.State == ArenaEvidenceState.Observed)
            ? ArenaEvidenceState.Observed
            : ArenaEvidenceState.Inferred;
        var id = $"route-score-evidence:{StableSuffix(request.ProposalId, target.Id, candidate.ModelId, objective.Id)}";
        var evidence = state == ArenaEvidenceState.Observed
            ? new ArenaEvidenceAssertion(
                id,
                state,
                "The normalized comparison score was aggregated from observed same-setup samples.",
                ReferenceId: $"route-samples:{StableSuffix(target.Id, candidate.ModelId, objective.Id)}")
            : new ArenaEvidenceAssertion(
                id,
                state,
                "The normalized comparison score includes inferred sample evidence.",
                Basis: "At least one source metric was explicitly marked inferred.");
        return new ArenaRouteScoreComponent(objective.Id, objective.Weight, currentScore, proposedScore, evidence);
    }

    private static ArenaRouteScoreComponent UnavailableComponent(
        ArenaRouteOptimizationRequest request,
        ArenaRouteOptimizationTarget target,
        ArenaRouteCandidateEvidence candidate,
        ArenaRouteObjective objective,
        string limitation) =>
        new(
            objective.Id,
            objective.Weight,
            null,
            null,
            new ArenaEvidenceAssertion(
                $"route-score-evidence:{StableSuffix(request.ProposalId, target.Id, candidate.ModelId, objective.Id)}",
                ArenaEvidenceState.Unavailable,
                "The normalized comparison score is unavailable.",
                Limitation: limitation));

    private static ImmutableArray<ArenaRouteMetricEvidence> MetricsFor(
        ArenaRouteCandidateEvidence candidate,
        string objectiveId) =>
        [
            .. candidate.Samples
                .Select(sample => sample.Metrics.FirstOrDefault(metric => string.Equals(metric.Id, objectiveId, StringComparison.Ordinal)))
                .Where(metric => metric is not null)!
        ];

    private static decimal? WeightedScore(
        ImmutableArray<ArenaRouteScoreComponent> components,
        bool useProposed,
        ImmutableArray<ArenaRouteObjective> objectives)
    {
        if (components.Any(item => useProposed ? item.ProposedScore is null : item.CurrentScore is null)) return null;
        return components.Sum(item => item.Weight * (useProposed ? item.ProposedScore!.Value : item.CurrentScore!.Value));
    }

    private static string Limitations(
        bool sameSetup,
        bool enoughSamples,
        bool observedScores,
        bool observedConstraints,
        bool hasHardware,
        bool hasCapability)
    {
        var limitations = new List<string>();
        if (!sameSetup) limitations.Add("setup fingerprints differ");
        if (!enoughSamples) limitations.Add("fewer than two comparable samples exist for each model");
        if (!observedScores) limitations.Add("score evidence is inferred or unavailable");
        if (!observedConstraints) limitations.Add("constraint evidence is unsatisfied, inferred, or unavailable");
        if (!hasHardware) limitations.Add("hardware evidence is absent");
        if (!hasCapability) limitations.Add("capability evidence is absent");
        return limitations.Count == 0 ? "No limitation was identified." : string.Join("; ", limitations) + ".";
    }

    private static void ValidateRequestShape(ArenaRouteOptimizationRequest request)
    {
        if (request.Targets.IsDefaultOrEmpty || request.Targets.Length > 256)
            throw new ArgumentException("At least one and no more than 256 route targets are required.", nameof(request));
        if (request.CreatedAtUtc == default || request.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Creation time must be non-default UTC.", nameof(request));
        if (request.SetupFingerprint.Length != 64 || !request.SetupFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Setup fingerprint must be a SHA-256 value.", nameof(request));

        foreach (var target in request.Targets)
        {
            if (target.Objectives.IsDefaultOrEmpty || target.Objectives.Length > 128)
                throw new ArgumentException("Each route target requires bounded objectives.", nameof(request));
            if (target.Candidates.IsDefaultOrEmpty || target.Candidates.Length < 2 || target.Candidates.Length > 128)
                throw new ArgumentException("Each route target requires the current model and at least one candidate.", nameof(request));
            if (target.Candidates.Count(item => string.Equals(item.ModelId, target.CurrentModelId, StringComparison.Ordinal)) != 1)
                throw new ArgumentException("Each route target must include its current model exactly once.", nameof(request));
            if (target.Candidates.Select(item => item.ModelId).Distinct(StringComparer.Ordinal).Count() != target.Candidates.Length)
                throw new ArgumentException("Candidate model IDs must be unique.", nameof(request));
            if (target.Objectives.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != target.Objectives.Length)
                throw new ArgumentException("Objective IDs must be unique.", nameof(request));
            if (target.Objectives.Any(item => item.Weight <= 0m || item.Weight > 1m)
                || Math.Abs(target.Objectives.Sum(item => item.Weight) - 1m) > 0.000001m)
                throw new ArgumentException("Objective weights must be positive and total one.", nameof(request));

            foreach (var candidate in target.Candidates)
            {
                if (candidate.Samples.IsDefault || candidate.Samples.Length > 10_000)
                    throw new ArgumentException("Candidate samples must be initialized and bounded.", nameof(request));
                if (candidate.Constraints.IsDefault || candidate.Constraints.Length > 128)
                    throw new ArgumentException("Candidate constraints must be initialized and bounded.", nameof(request));
                foreach (var sample in candidate.Samples)
                {
                    if (sample.Metrics.IsDefault || sample.Metrics.Length > 128)
                        throw new ArgumentException("Sample metrics must be initialized and bounded.", nameof(request));
                    foreach (var metric in sample.Metrics)
                    {
                        ValidateEvidenceValue(metric.Score, metric.Evidence, "metric");
                        if (metric.Score is < 0m or > 1m)
                            throw new ArgumentException("Metric scores must be normalized from zero through one.", nameof(request));
                    }
                }
                foreach (var constraint in candidate.Constraints)
                {
                    ValidateEvidenceValue(constraint.Satisfied, constraint.Evidence, "constraint");
                }
            }
        }
    }

    private static void ValidateEvidenceValue<T>(T? value, ArenaEvidenceAssertion evidence, string label)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.State == ArenaEvidenceState.Unavailable && value is not null)
            throw new ArgumentException($"Unavailable {label} evidence cannot carry a result.");
        if (evidence.State != ArenaEvidenceState.Unavailable && value is null)
            throw new ArgumentException($"Observed or inferred {label} evidence requires a result.");
        if (evidence.State == ArenaEvidenceState.Observed && string.IsNullOrWhiteSpace(evidence.ReferenceId))
            throw new ArgumentException($"Observed {label} evidence requires a reference ID.");
        if (evidence.State == ArenaEvidenceState.Inferred && string.IsNullOrWhiteSpace(evidence.Basis))
            throw new ArgumentException($"Inferred {label} evidence requires a basis.");
        if (evidence.State == ArenaEvidenceState.Unavailable && string.IsNullOrWhiteSpace(evidence.Limitation))
            throw new ArgumentException($"Unavailable {label} evidence requires a limitation.");
    }

    private static string StableSuffix(params object[] values)
    {
        var input = string.Join("\n", values.Select(value => value?.ToString() ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 8)).ToLowerInvariant();
    }

    private sealed record TargetEvaluation(ArenaModelRouteChange Change, bool SupportsChange);

    private sealed record CandidateEvaluation(
        ArenaRouteCandidateEvidence Candidate,
        int ComparableSampleCount,
        ArenaEvidenceSufficiency Sufficiency,
        ImmutableArray<ArenaRouteScoreComponent> Components,
        ImmutableArray<ArenaRouteConstraint> Constraints,
        ImmutableArray<string> RunIds,
        decimal? CurrentWeightedScore,
        decimal? ProposedWeightedScore,
        decimal ComparableWeightedScore,
        string Limitation);
}
