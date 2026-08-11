using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf.Services;

internal sealed record PersistedRoutingEvidenceContext(
    ArenaExperimentContract Experiment,
    string CurrentSetupFingerprint,
    string CurrentPlanFingerprint,
    string? SelectedScenarioId,
    string AgentId,
    string CurrentModelId,
    IReadOnlyDictionary<string, string> ProviderModels,
    IReadOnlyDictionary<string, IReadOnlyList<ArenaRouteConstraintEvidence>> ConstraintsByModel);

internal sealed record RoutingEvidenceLoadResult(
    ArenaRouteOptimizationRequest? Request,
    ImmutableArray<string> Diagnostics,
    int CompatibleRuns,
    int CompatibleEvaluations)
{
    internal bool IsAvailable => Request is not null;
}

internal interface IRoutingEvidenceSource
{
    Task<RoutingEvidenceLoadResult> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Builds optimizer input only from compatible completed experiment runs and
/// finalized observed rubric results. Raw transcript/provider content is never
/// read. Ambiguous duplicate judgments are omitted instead of averaged into an
/// invented observation.
/// </summary>
internal sealed class PersistedRoutingEvidenceSource : IRoutingEvidenceSource
{
    private const int MaximumDiagnostics = 24;
    private readonly ExperimentRunStore runStore;
    private readonly ArenaRubricStore rubricStore;
    private readonly ArenaExperimentPackStore packStore;
    private readonly Func<CancellationToken, Task<PersistedRoutingEvidenceContext?>> contextProvider;
    private readonly TimeProvider timeProvider;

    internal PersistedRoutingEvidenceSource(
        ExperimentRunStore runStore,
        ArenaRubricStore rubricStore,
        ArenaExperimentPackStore packStore,
        Func<CancellationToken, Task<PersistedRoutingEvidenceContext?>> contextProvider,
        TimeProvider? timeProvider = null)
    {
        this.runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        this.rubricStore = rubricStore ?? throw new ArgumentNullException(nameof(rubricStore));
        this.packStore = packStore ?? throw new ArgumentNullException(nameof(packStore));
        this.contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RoutingEvidenceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var context = await contextProvider(cancellationToken).ConfigureAwait(false);
        if (context is null)
            return Unavailable("No active experiment context is available.");
        if (!IsHash(context.CurrentSetupFingerprint)
            || !IsHash(context.CurrentPlanFingerprint)
            || string.IsNullOrWhiteSpace(context.AgentId)
            || string.IsNullOrWhiteSpace(context.CurrentModelId))
            return Unavailable("The active routing context has no valid setup identity or current route.");

        ArenaExperimentExpansion expansion;
        try
        {
            expansion = ExperimentExpander.Expand(context.Experiment);
        }
        catch
        {
            return Unavailable("The active experiment contract is invalid, so route evidence is unavailable.");
        }

        var runsTask = runStore.LoadAllAsync(cancellationToken);
        var rubricsTask = rubricStore.LoadRubricsAsync(cancellationToken);
        var resultsTask = rubricStore.LoadResultsAsync(cancellationToken);
        var packsTask = packStore.LoadScenarioPacksAsync(cancellationToken);
        await Task.WhenAll(runsTask, rubricsTask, resultsTask, packsTask).ConfigureAwait(false);
        var loadedRuns = await runsTask.ConfigureAwait(false);
        var loadedRubrics = await rubricsTask.ConfigureAwait(false);
        var loadedResults = await resultsTask.ConfigureAwait(false);
        var loadedPacks = await packsTask.ConfigureAwait(false);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (!loadedRuns.Diagnostics.IsEmpty) diagnostics.Add($"Ignored {loadedRuns.Diagnostics.Length} invalid run artifact(s).");
        if (!loadedRubrics.Diagnostics.IsEmpty) diagnostics.Add($"Ignored {loadedRubrics.Diagnostics.Length} invalid rubric artifact(s).");
        if (!loadedResults.Diagnostics.IsEmpty) diagnostics.Add($"Ignored {loadedResults.Diagnostics.Length} invalid evaluation artifact(s).");
        if (!loadedPacks.Diagnostics.IsEmpty) diagnostics.Add($"Ignored {loadedPacks.Diagnostics.Length} invalid scenario-pack artifact(s).");

        var packs = loadedPacks.Artifacts
            .Where(item => string.Equals(item.Id, context.Experiment.ScenarioPackId, StringComparison.Ordinal))
            .ToArray();
        if (packs.Length != 1)
            return new(null, Bounded(diagnostics.Append("The experiment's exact persisted scenario pack is unavailable.")), 0, 0);
        var scenarios = packs[0].Scenarios
            .Where(item => context.SelectedScenarioId is null
                || string.Equals(item.Id, context.SelectedScenarioId, StringComparison.Ordinal))
            .ToArray();
        var scenario = scenarios.Length == 1 ? scenarios[0] : null;
        if (scenario is null)
            return new(null, Bounded(diagnostics.Append("The exact executed scenario is ambiguous or unavailable.")), 0, 0);
        if (!scenario.SetupFingerprint.Equals(context.CurrentSetupFingerprint, StringComparison.OrdinalIgnoreCase))
            return new(null, Bounded(diagnostics.Append("The current setup differs from the persisted executed scenario.")), 0, 0);
        var setupFingerprint = scenario.SetupFingerprint;

        var cellByKey = expansion.Cells.ToDictionary(item => item.CellKey, StringComparer.Ordinal);
        var identityCompatibleRuns = loadedRuns.Runs
            .Where(item => item.State == ArenaExperimentRunState.Completed
                && string.Equals(item.ExperimentId, context.Experiment.Id, StringComparison.Ordinal)
                && string.Equals(item.ExperimentFingerprint, expansion.ExperimentFingerprint, StringComparison.Ordinal)
                && cellByKey.TryGetValue(item.CellKey, out var cell)
                && string.Equals(cell.VariantFingerprint, item.VariantFingerprint, StringComparison.Ordinal))
            .OrderBy(item => item.CellKey, StringComparer.Ordinal)
            .ToImmutableArray();
        var compatibleRuns = identityCompatibleRuns
            .Where(item => ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(item, context.CurrentPlanFingerprint))
            .ToImmutableArray();
        if (identityCompatibleRuns.Length != compatibleRuns.Length)
            diagnostics.Add($"Ignored {identityCompatibleRuns.Length - compatibleRuns.Length} completed run(s) whose latest attempt does not match the current execution plan.");
        if (compatibleRuns.IsEmpty)
            diagnostics.Add("No completed runs match the active experiment fingerprint.");

        var rubricByIdentity = loadedRubrics.Artifacts
            .GroupBy(item => (item.Id, item.Version))
            .ToDictionary(group => group.Key, group => group.Single());
        var models = context.ProviderModels.Values
            .Append(context.CurrentModelId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (models.Length < 2)
            return new(null, Bounded(diagnostics.Append("At least two distinct model identities are required.")), compatibleRuns.Length, 0);

        var samplesByModel = models.ToDictionary(
            model => model,
            _ => ImmutableArray.CreateBuilder<ArenaRouteSampleEvidence>(),
            StringComparer.Ordinal);
        var completedRunsByModel = models.ToDictionary(
            model => model,
            _ => ImmutableArray.CreateBuilder<ArenaExperimentRunContract>(),
            StringComparer.Ordinal);
        var compatibleEvaluationCount = 0;
        foreach (var run in compatibleRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = cellByKey[run.CellKey];
            if (!context.ProviderModels.TryGetValue(cell.ProviderProfileId, out var model)
                || !samplesByModel.TryGetValue(model, out var samples))
            {
                diagnostics.Add("A completed run references a provider profile without a current model identity.");
                continue;
            }
            completedRunsByModel[model].Add(run);

            var score = FindSingleObservedScore(run, loadedResults.Artifacts, rubricByIdentity);
            if (score.Kind == ObservedScoreKind.Ambiguous)
            {
                diagnostics.Add($"Run {SafeReference(run.Id)} has multiple observed judgments and was omitted rather than averaged.");
                continue;
            }
            if (score.Kind != ObservedScoreKind.Available || score.Value is null || score.ResultId is null)
                continue;

            compatibleEvaluationCount++;
            samples.Add(new(
                run.Id,
                setupFingerprint,
                [new(
                    "quality",
                    score.Value,
                    new(
                        $"route-metric:{StableSuffix(run.Id, score.ResultId)}",
                        ArenaEvidenceState.Observed,
                        "A normalized quality score was read from one finalized observed rubric result.",
                        ReferenceId: score.ResultId))]));
        }

        var candidates = models.Select(model =>
        {
            var trialConstraints = TrialObservedConstraints(model, completedRunsByModel[model].ToImmutable());
            var suppliedConstraints = context.ConstraintsByModel.TryGetValue(model, out var values)
                ? values.ToImmutableArray()
                : ImmutableArray<ArenaRouteConstraintEvidence>.Empty;
            var suppliedIds = suppliedConstraints.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (trialConstraints.Any(item => suppliedIds.Contains(item.Id)))
            {
                diagnostics.Add($"A caller-supplied stricter constraint replaced one trial-derived constraint for model {SafeReference(model)}.");
            }
            var constraints = trialConstraints.Where(item => !suppliedIds.Contains(item.Id))
                .Concat(suppliedConstraints)
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToImmutableArray();
            if (constraints.IsEmpty)
                diagnostics.Add($"Trial-scoped execution constraints are unavailable for model {SafeReference(model)} because it has no compatible completed run.");
            if (!constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Hardware))
                diagnostics.Add($"Current observed hardware evidence is unavailable for model {SafeReference(model)}; historical trials do not establish the current machine.");
            return new ArenaRouteCandidateEvidence(model, samplesByModel[model].ToImmutable(), constraints);
        }).ToImmutableArray();

        var now = timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
            return Unavailable("Routing evidence time is unavailable.");
        var proposalId = $"route-proposal:{StableSuffix(context.Experiment.Id, setupFingerprint, context.AgentId, string.Join("\n", compatibleRuns.Select(item => item.Id)))}";
        var request = new ArenaRouteOptimizationRequest(
            proposalId,
            now,
            context.Experiment.Id,
            setupFingerprint,
            [new(
                $"route-target:{StableSuffix(context.AgentId)}",
                context.AgentId,
                context.CurrentModelId,
                [new("quality", 1m)],
                candidates)]);
        return new(request, Bounded(diagnostics), compatibleRuns.Length, compatibleEvaluationCount);
    }

    private static ObservedScore FindSingleObservedScore(
        ArenaExperimentRunContract run,
        ImmutableArray<ArenaRubricEvaluationResultContract> results,
        IReadOnlyDictionary<(string Id, string Version), ArenaRubricContract> rubrics)
    {
        if (run.Attempts < 1)
            return new(ObservedScoreKind.Unavailable, null, null);
        var latestTrialId = ArenaExperimentRunPolicy.CreateTrialId(run.CellKey, run.Attempts);
        if (!run.TrialIds.Contains(latestTrialId, StringComparer.Ordinal))
            return new(ObservedScoreKind.Unavailable, null, null);
        IReadOnlySet<string> runReferences = new HashSet<string>([latestTrialId], StringComparer.Ordinal);
        var scores = new List<(decimal Score, string ResultId)>();
        foreach (var result in results.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!rubrics.ContainsKey((result.RubricId, result.RubricVersion))) continue;
            var side = SubjectSide(result, runReferences);
            if (side == 0) continue;

            foreach (var evaluator in result.DeterministicResults
                         .Concat(result.HumanResults)
                         .Concat(result.ModelJudgeResults)
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                if (evaluator.Provenance.State != ArenaEvidenceState.Observed) continue;
                var raw = side == 1 ? evaluator.WeightedScoreA : evaluator.WeightedScoreB;
                if (raw is null) continue;
                // ArenaRubricService and the result codec define weighted
                // scores as normalized 0..1 values. Re-normalizing them by the
                // rubric's raw criterion range would understate non-unit rubrics.
                scores.Add((raw.Value, result.Id));
            }
        }

        return scores.Count switch
        {
            0 => new(ObservedScoreKind.Unavailable, null, null),
            1 => new(ObservedScoreKind.Available, scores[0].Score, scores[0].ResultId),
            _ => new(ObservedScoreKind.Ambiguous, null, null)
        };
    }

    internal static int SubjectSide(ArenaRubricEvaluationResultContract result, IReadOnlySet<string> runReferences)
    {
        if (result.BlindReveal is not null)
        {
            var matchesA = runReferences.Contains(result.BlindReveal.LabelAReferenceId);
            var matchesB = runReferences.Contains(result.BlindReveal.LabelBReferenceId);
            return matchesA == matchesB ? 0 : matchesA ? 1 : 2;
        }

        var subjectA = result.SubjectReferenceIds.Length > 0
            && runReferences.Contains(result.SubjectReferenceIds[0]);
        var subjectB = result.SubjectReferenceIds.Length > 1
            && runReferences.Contains(result.SubjectReferenceIds[1]);
        return subjectA == subjectB ? 0 : subjectA ? 1 : 2;
    }

    private static ImmutableArray<ArenaRouteConstraintEvidence> TrialObservedConstraints(
        string model,
        ImmutableArray<ArenaExperimentRunContract> runs)
    {
        if (runs.IsEmpty) return [];
        var ordered = runs.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var referenceRunId = ordered[0].Id;
        var suffix = StableSuffix(model, string.Join("\n", ordered.Select(item => item.Id)));
        return
        [
            new(
                $"constraint:trial-capability:{suffix}",
                ArenaRouteConstraintKind.Capability,
                $"Completed {ordered.Length} compatible chat trial(s) for this exact experiment setup; no untested provider capability is claimed.",
                true,
                new(
                    $"constraint-evidence:trial-capability:{suffix}",
                    ArenaEvidenceState.Observed,
                    "A compatible completed trial observed provider chat execution for this exact setup.",
                    ReferenceId: referenceRunId))
        ];
    }

    private static RoutingEvidenceLoadResult Unavailable(string diagnostic) => new(null, [diagnostic], 0, 0);

    private static ImmutableArray<string> Bounded(IEnumerable<string> values) =>
        [.. values.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).Take(MaximumDiagnostics)];

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string SafeReference(string value) => value.Length <= 48 ? value : value[..45] + "…";

    private static string StableSuffix(params string[] values) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)))[..8]);

    private enum ObservedScoreKind { Unavailable, Available, Ambiguous }
    private sealed record ObservedScore(ObservedScoreKind Kind, decimal? Value, string? ResultId);
}

/// <summary>
/// Presentation coordinator for proposal generation and the separate route
/// application boundary. An integration that does not supply an application
/// delegate still gets a working optimizer; Apply stays honestly disabled.
/// </summary>
public sealed partial class ModelRoutingOptimizerCoordinator : IDisposable
{
    private static readonly Regex ApproverIdPattern = new("^[a-z0-9][a-z0-9._:-]{0,79}$", RegexOptions.CultureInvariant);
    private readonly ModelRoutingOptimizerControl control;
    private readonly IRoutingEvidenceSource? evidenceSource;
    private readonly Func<ArenaRouteProposalContract, string, DateTimeOffset, CancellationToken, Task<ArenaRouteApplicationReceiptContract>>? applyApproved;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private RoutingEvidenceLoadResult? loadedEvidence;
    private ArenaRouteProposalContract? proposal;
    private bool disposed;

    internal ModelRoutingOptimizerCoordinator(
        ModelRoutingOptimizerControl control,
        IRoutingEvidenceSource? evidenceSource = null,
        Func<ArenaRouteProposalContract, string, DateTimeOffset, CancellationToken, Task<ArenaRouteApplicationReceiptContract>>? applyApproved = null,
        TimeProvider? timeProvider = null)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        this.evidenceSource = evidenceSource;
        this.applyApproved = applyApproved;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        control.Initialize(this);
        control.SetEvidence(
            evidenceSource is null
                ? "Unavailable — no persisted experiment/evaluation evidence source is connected."
                : "Refresh to load compatible persisted experiment/evaluation evidence.",
            evidenceSource is null ? ["No scores or constraints are fabricated while evidence is unavailable."] : [],
            canPropose: false);
        control.SetProposal(
            "No route proposal exists.",
            [],
            canApply: false,
            applicationConnected: applyApproved is not null);
    }

    internal ArenaRouteProposalContract? CurrentProposal => proposal;
    internal ArenaRouteApplicationReceiptContract? LastReceipt { get; private set; }

    public async Task RefreshEvidenceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await actionGate.WaitAsync(cancellationToken);
        try
        {
            control.SetBusy(true);
            proposal = null;
            LastReceipt = null;
            control.SetProposal("No route proposal exists.", [], false, applyApproved is not null);
            if (evidenceSource is null)
            {
                loadedEvidence = null;
                control.SetEvidence(
                    "Unavailable — no persisted experiment/evaluation evidence source is connected.",
                    ["No scores or constraints are fabricated while evidence is unavailable."],
                    false);
                control.SetStatus("Routing evidence is unavailable; no optimization is claimed.");
                return;
            }

            try
            {
                loadedEvidence = await evidenceSource.LoadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                loadedEvidence = null;
                control.SetEvidence("Unavailable — persisted evidence could not be read safely.", ["Private error content was not retained."], false);
                control.SetStatus("Routing evidence refresh failed without retaining private error content.");
                return;
            }

            var available = loadedEvidence.IsAvailable;
            control.SetEvidence(
                available
                    ? $"{loadedEvidence.CompatibleRuns} compatible completed run(s); {loadedEvidence.CompatibleEvaluations} unambiguous observed evaluation(s)."
                    : "Compatible observed route evidence is unavailable.",
                loadedEvidence.Diagnostics,
                available);
            control.SetStatus(available
                ? "Compatible evidence loaded. Building a proposal remains a separate non-mutating action."
                : "No route proposal can be built from the available evidence.");
        }
        finally
        {
            control.SetBusy(false);
            actionGate.Release();
        }
    }

    public async Task BuildProposalAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await actionGate.WaitAsync(cancellationToken);
        try
        {
            control.SetBusy(true);
            if (loadedEvidence?.Request is null)
            {
                control.SetStatus("Refresh compatible persisted evidence before building a proposal.");
                return;
            }

            try
            {
                proposal = ArenaModelRoutingOptimizer.Propose(loadedEvidence.Request);
            }
            catch
            {
                proposal = null;
                control.SetProposal("No valid route proposal was produced.", [], false, applyApproved is not null);
                control.SetStatus("The optimizer rejected the evidence shape; no route was changed.");
                return;
            }

            var items = proposal.Changes.Select(ToItem).ToArray();
            var canApply = proposal.Status == ArenaRouteProposalStatus.Proposed
                && proposal.Changes.All(item => item.EvidenceSufficiency == ArenaEvidenceSufficiency.Sufficient);
            var applicationConnected = applyApproved is not null;
            control.SetProposal(
                $"{proposal.Status} · {proposal.Changes.Length} target(s) · setup {proposal.SetupFingerprint[..12]}…",
                items,
                canApply,
                applicationConnected);
            control.SetStatus(canApply
                ? applicationConnected
                    ? "Proposal ready. Routing is unchanged until the exact proposal is explicitly approved and applied."
                    : "Proposal ready, but route application is unavailable until session load/save delegates are connected."
                : "Evidence is insufficient or does not support a change; routing remains unchanged.");
        }
        finally
        {
            control.SetBusy(false);
            actionGate.Release();
        }
    }

    public async Task ApplyApprovedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await actionGate.WaitAsync(cancellationToken);
        try
        {
            control.SetBusy(true);
            if (proposal is null || proposal.Status != ArenaRouteProposalStatus.Proposed || applyApproved is null)
            {
                control.SetStatus("Route application is unavailable; no provider setting was changed.");
                return;
            }
            if (!control.IsExplicitlyApproved)
            {
                control.SetStatus("Explicit approval is required for this exact proposal.");
                return;
            }
            var approver = control.ApproverId.Trim();
            if (!ApproverIdPattern.IsMatch(approver))
            {
                control.SetStatus("Approver ID must use 1–80 lowercase letters, digits, dots, underscores, colons, or hyphens.");
                return;
            }
            var approvedAtUtc = timeProvider.GetUtcNow();
            if (approvedAtUtc == default || approvedAtUtc.Offset != TimeSpan.Zero)
            {
                control.SetStatus("Approval time is unavailable; the route was not changed.");
                return;
            }

            try
            {
                var returnedReceipt = await applyApproved(proposal, approver, approvedAtUtc, cancellationToken);
                if (!IsExactReceipt(proposal, returnedReceipt, approver, approvedAtUtc))
                {
                    LastReceipt = null;
                    control.SetStatus("The application boundary returned invalid or mismatched receipt evidence; no application claim is shown.");
                    return;
                }
                LastReceipt = returnedReceipt;
            }
            catch (ArenaRouteApplicationConflictException)
            {
                var conflictedProposal = proposal;
                proposal = null;
                loadedEvidence = null;
                control.SetEvidence("Refresh required after route conflict.", [], false);
                control.SetProposal(
                    $"Stale proposal {conflictedProposal.Id} · refresh compatible evidence before rebuilding.",
                    conflictedProposal.Changes.Select(ToItem),
                    canApply: false,
                    applicationConnected: applyApproved is not null);
                control.SetStatus("The setup or current model changed after proposal creation; refresh evidence before trying again.");
                return;
            }
            catch (ArenaRouteApplicationBusyException)
            {
                control.SetStatus("Stop the active arena operation or Auto Chat before applying an approved route.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                control.SetStatus("The approved route could not be persisted; private error content was not retained.");
                return;
            }

            var appliedProposal = proposal;
            proposal = null;
            loadedEvidence = null;
            control.SetEvidence("Refresh required after route application.", [], false);
            control.SetProposal(
                $"Applied receipt {LastReceipt.Id} · {LastReceipt.Changes.Length} change(s).",
                LastReceipt.Changes.Select(change => new RouteProposalItem(
                    $"{change.AgentId}: {change.PreviousModelId} → {change.AppliedModelId}",
                    "Observed explicit approval and concurrency-checked persistence.",
                    $"Receipt references proposal {appliedProposal.Id}.")),
                false,
                applicationConnected: true);
            control.SetReceiptAvailable(true);
            control.SetStatus("Approved route applied. Route settings are persisted; the immutable receipt is process-only and can be copied.");
        }
        finally
        {
            control.SetBusy(false);
            actionGate.Release();
        }
    }

    internal void CopyReceipt()
    {
        ThrowIfDisposed();
        if (LastReceipt is null)
        {
            control.SetStatus("No process-only route receipt is available to copy.");
            return;
        }
        try
        {
            Clipboard.SetText(ArenaContractCodec.Serialize(LastReceipt));
            control.SetStatus("Canonical content-free route receipt copied; it remains process-only unless saved elsewhere.");
        }
        catch
        {
            control.SetStatus("The process-only route receipt could not be copied.");
        }
    }

    internal static Func<ArenaRouteProposalContract, string, DateTimeOffset, CancellationToken, Task<ArenaRouteApplicationReceiptContract>>
        CreateSessionApplicationBoundary(
            ArenaRouteApplicationService service,
            Func<string?> activeSessionId,
            SemaphoreSlim operationLock,
            Func<bool> isArenaBusy,
            Func<bool> isAutoChatRunning,
            Func<Func<CancellationToken, Task>, Task> trackOperationAsync,
            Func<string, CancellationToken, Task> refreshSessionAsync)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(activeSessionId);
        ArgumentNullException.ThrowIfNull(operationLock);
        ArgumentNullException.ThrowIfNull(isArenaBusy);
        ArgumentNullException.ThrowIfNull(isAutoChatRunning);
        ArgumentNullException.ThrowIfNull(trackOperationAsync);
        ArgumentNullException.ThrowIfNull(refreshSessionAsync);
        return async (routeProposal, approvedBy, approvedAtUtc, cancellationToken) =>
        {
            var sessionId = activeSessionId();
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("The active persisted session is unavailable.");
            ThrowIfArenaMutationBusy(isArenaBusy, isAutoChatRunning);

            ArenaRouteApplicationReceiptContract? receipt = null;
            await trackOperationAsync(async shutdownCancellationToken =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    shutdownCancellationToken);
                var operationCancellationToken = linkedCancellation.Token;
                ThrowIfArenaMutationBusy(isArenaBusy, isAutoChatRunning);
                await operationLock.WaitAsync(operationCancellationToken).ConfigureAwait(true);
                try
                {
                    ThrowIfArenaMutationBusy(isArenaBusy, isAutoChatRunning);
                    var currentSessionId = activeSessionId();
                    if (!string.Equals(sessionId, currentSessionId, StringComparison.OrdinalIgnoreCase))
                        throw new ArenaRouteApplicationConflictException("The active session changed before route application.");

                    receipt = await service.ApplyApprovedAsync(
                        sessionId,
                        routeProposal,
                        approvedBy,
                        approvedAtUtc,
                        operationCancellationToken).ConfigureAwait(true);

                    // Persistence has crossed its atomic commit boundary. Finish the
                    // bounded shell refresh while TrackAsync keeps shutdown draining;
                    // later cancellation must not turn a persisted route into an
                    // unacknowledged application.
                    await refreshSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(true);
                }
                finally
                {
                    operationLock.Release();
                }
            }).ConfigureAwait(true);

            return receipt
                ?? throw new InvalidOperationException("The tracked route application completed without a receipt.");
        };
    }

    private static void ThrowIfArenaMutationBusy(Func<bool> isArenaBusy, Func<bool> isAutoChatRunning)
    {
        if (isArenaBusy() || isAutoChatRunning())
            throw new ArenaRouteApplicationBusyException();
    }

    private static RouteProposalItem ToItem(ArenaModelRouteChange change)
    {
        var components = string.Join(", ", change.ScoreComponents.Select(item =>
            item.CurrentScore is null || item.ProposedScore is null
                ? $"{item.Id}=unavailable"
                : $"{item.Id} {item.CurrentScore:0.###}→{item.ProposedScore:0.###}"));
        var constraints = change.Constraints.IsEmpty
            ? "Constraints unavailable."
            : string.Join(", ", change.Constraints.Select(item => $"{item.Kind}:{item.Evidence.State}/{(item.Satisfied == true ? "pass" : item.Satisfied == false ? "fail" : "unavailable")}"));
        return new(
            $"{change.AgentId}: {change.CurrentModelId} → {change.ProposedModelId}",
            $"{change.EvidenceSufficiency} · {change.SampleCount} sample(s) · {components}",
            constraints);
    }

    private static bool IsExactReceipt(
        ArenaRouteProposalContract proposal,
        ArenaRouteApplicationReceiptContract receipt,
        string approvedBy,
        DateTimeOffset approvedAtUtc)
    {
        if (!ArenaContractCodec.Validate(receipt).IsValid
            || !string.Equals(receipt.ProposalId, proposal.Id, StringComparison.Ordinal)
            || !string.Equals(receipt.ExperimentId, proposal.ExperimentId, StringComparison.Ordinal)
            || !string.Equals(receipt.SetupFingerprint, proposal.SetupFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(receipt.ApprovedBy, approvedBy, StringComparison.Ordinal)
            || receipt.ApprovedAtUtc != approvedAtUtc
            || receipt.Changes.Length != proposal.Changes.Length)
            return false;

        var expected = proposal.Changes
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => (item.Id, item.AgentId, item.CurrentModelId, item.ProposedModelId));
        var actual = receipt.Changes
            .OrderBy(item => item.ProposalChangeId, StringComparer.Ordinal)
            .Select(item => (item.ProposalChangeId, item.AgentId, item.PreviousModelId, item.AppliedModelId));
        return expected.SequenceEqual(actual);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose() => disposed = true;
}

internal sealed class ArenaRouteApplicationBusyException : InvalidOperationException
{
    internal ArenaRouteApplicationBusyException()
        : base("Route application is unavailable while the arena or Auto Chat is active.")
    {
    }
}
