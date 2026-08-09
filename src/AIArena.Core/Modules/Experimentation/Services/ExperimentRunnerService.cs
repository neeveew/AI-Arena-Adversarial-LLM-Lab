using System.Collections.Concurrent;
using System.Collections.Immutable;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaExperimentCellExecutionContext(
    ArenaExperimentCellPlan Cell,
    int Attempt,
    string TrialId);

public sealed record ArenaExperimentCellExecutionResult(
    ArenaExperimentRunState State,
    string? InterruptionReason,
    ImmutableArray<ArenaEvidenceAssertion> Evidence)
{
    public static ArenaExperimentCellExecutionResult Completed(
        ImmutableArray<ArenaEvidenceAssertion> evidence = default) =>
        new(ArenaExperimentRunState.Completed, null, evidence.IsDefault ? [] : evidence);

    public static ArenaExperimentCellExecutionResult Failed(
        ImmutableArray<ArenaEvidenceAssertion> evidence = default) =>
        new(ArenaExperimentRunState.Failed, null, evidence.IsDefault ? [] : evidence);

    public static ArenaExperimentCellExecutionResult Interrupted(
        string reason,
        ImmutableArray<ArenaEvidenceAssertion> evidence = default) =>
        new(ArenaExperimentRunState.Interrupted, reason, evidence.IsDefault ? [] : evidence);
}

public interface IArenaExperimentCellExecutor
{
    Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
        ArenaExperimentCellExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IArenaExperimentExecutionPlanIdentity
{
    string PlanFingerprint { get; }
}

/// <summary>
/// Cancellation signal that can carry content-free observations made before the
/// caller cancelled (for example, an already-created isolated child session).
/// ExperimentRunnerService persists those observations with the cancelled cell.
/// </summary>
public sealed class ArenaExperimentCellExecutionCancelledException : OperationCanceledException
{
    public ArenaExperimentCellExecutionCancelledException(
        ImmutableArray<ArenaEvidenceAssertion> evidence,
        CancellationToken cancellationToken)
        : base("Experiment cell execution was cancelled by its caller.", cancellationToken)
    {
        Evidence = evidence.IsDefault ? [] : evidence;
    }

    public ImmutableArray<ArenaEvidenceAssertion> Evidence { get; }
}

public sealed record ArenaExperimentRunnerOptions(
    int? MaximumParallelism = null,
    IReadOnlyDictionary<string, int>? ProviderConcurrencyBudgets = null,
    Func<ArenaExperimentRunContract, bool>? RetryApproved = null);

public sealed record ArenaExperimentRunnerResult(
    int PlannedCells,
    int EligibleCells,
    int StartedCells,
    bool WasCancelled,
    ImmutableArray<ArenaExperimentRunContract> Runs,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

/// <summary>
/// Executes a pre-expanded matrix through an injected cell boundary. It owns
/// concurrency, retry/resume selection, cancellation state, and durable cell
/// transitions, while the executor owns provider/session behavior.
/// </summary>
public sealed class ExperimentRunnerService
{
    private const string ExecutorInterruptionReason = "executor_interruption";
    private readonly IArenaExperimentCellExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public ExperimentRunnerService(
        IArenaExperimentCellExecutor executor,
        TimeProvider? timeProvider = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ArenaExperimentRunnerResult> RunAsync(
        ArenaExperimentContract experiment,
        ArenaExperimentExpansion expansion,
        ExperimentRunStore store,
        ArenaExperimentRunnerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(
                experiment,
                expansion,
                store,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<ArenaExperimentRunnerResult> RunCoreAsync(
        ArenaExperimentContract experiment,
        ArenaExperimentExpansion expansion,
        ExperimentRunStore store,
        ArenaExperimentRunnerOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(store);
        options ??= new();
        ValidateInputs(experiment, expansion, options);

        await using var executionLease = await store.AcquireExecutionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var initial = await store.RecoverInterruptedAfterRestartAsync(executionLease, UtcNow(), cancellationToken).ConfigureAwait(false);
        var existingByCell = initial.Runs.ToDictionary(item => item.CellKey, StringComparer.Ordinal);
        var planFingerprint = (_executor as IArenaExperimentExecutionPlanIdentity)?.PlanFingerprint;
        var planReference = string.IsNullOrWhiteSpace(planFingerprint) ? null : $"plan:{planFingerprint}";
        var eligible = expansion.Cells
            .Where(cell => !existingByCell.TryGetValue(cell.CellKey, out var run)
                || run.State == ArenaExperimentRunState.Queued
                || (options.RetryApproved?.Invoke(run) ?? false))
            .ToImmutableArray();
        var diagnostics = new ConcurrentBag<ArenaArtifactDiagnostic>(initial.Diagnostics);
        if (planReference is not null)
        {
            foreach (var run in initial.Runs.Where(run =>
                expansion.Cells.Any(cell => cell.CellKey.Equals(run.CellKey, StringComparison.Ordinal))
                && ArenaExperimentRunPolicy.IsTerminal(run.State)
                && !LatestAttemptMatchesPlan(run, planReference)))
            {
                diagnostics.Add(new ArenaArtifactDiagnostic(
                    "experiment_run.execution_plan_mismatch",
                    ArenaArtifactDiagnosticSeverity.Error,
                    "experiment-runs/store.json",
                    "A terminal cell belongs to a different or unrecorded resolved execution plan; explicit retry approval is required."));
            }
        }

        var capacity = await store.ReserveCapacityAsync(
            executionLease,
            eligible.Select(cell => cell.CellKey),
            cancellationToken).ConfigureAwait(false);
        AddDiagnostics(diagnostics, capacity.Diagnostics);
        if (!capacity.IsAvailable)
        {
            return new(
                expansion.Cells.Length,
                eligible.Length,
                0,
                false,
                initial.Runs,
                [.. diagnostics
                    .Distinct()
                    .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                    .ThenBy(item => item.Code, StringComparer.Ordinal)]);
        }

        var maximumParallelism = Math.Min(
            experiment.MaxParallelism,
            options.MaximumParallelism ?? experiment.MaxParallelism);
        var providerGates = CreateProviderGates(expansion, options.ProviderConcurrencyBudgets, maximumParallelism);
        var started = 0;
        var wasCancelled = false;

        try
        {
            await Parallel.ForEachAsync(
                eligible,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = maximumParallelism
                },
                async (cell, token) =>
                {
                    var providerGate = providerGates[cell.ProviderProfileId];
                    await providerGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        existingByCell.TryGetValue(cell.CellKey, out var existing);
                        var attempt = (existing?.Attempts ?? 0) + 1;
                        var trialId = CreateTrialId(cell.CellKey, attempt);
                        var startedAt = UtcNow();
                        var running = CreateRun(
                            cell,
                            existing,
                            ArenaExperimentRunState.Running,
                            attempt,
                            trialId,
                            startedAt,
                            null,
                            Evidence("running", trialId, "Experiment cell execution started."));
                        var runningWrite = await store.SaveReservedAsync(executionLease, running, token).ConfigureAwait(false);
                        AddDiagnostics(diagnostics, runningWrite.Diagnostics);
                        if (!runningWrite.Succeeded || runningWrite.Artifact is null) return;

                        Interlocked.Increment(ref started);
                        ArenaExperimentCellExecutionResult outcome;
                        try
                        {
                            outcome = await _executor.ExecuteAsync(
                                new(cell, attempt, trialId),
                                token).ConfigureAwait(false);
                            ValidateExecutorOutcome(outcome);
                        }
                        catch (ArenaExperimentCellExecutionCancelledException exception) when (token.IsCancellationRequested)
                        {
                            outcome = new(
                                ArenaExperimentRunState.Cancelled,
                                null,
                                exception.Evidence.Add(Evidence("cancelled", trialId, "Experiment cell was cancelled by its caller.")));
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            outcome = new(
                                ArenaExperimentRunState.Cancelled,
                                null,
                                [Evidence("cancelled", trialId, "Experiment cell was cancelled by its caller.")]);
                        }
                        catch (Exception)
                        {
                            outcome = new(
                                ArenaExperimentRunState.Failed,
                                null,
                                [Evidence("failed", trialId, "Experiment cell executor failed without persisted exception content.")]);
                        }

                        var evidence = outcome.Evidence.IsDefaultOrEmpty
                            ? [Evidence(outcome.State.ToString().ToLowerInvariant(), trialId, StateSummary(outcome.State))]
                            : outcome.Evidence;
                        var terminal = CreateRun(
                            cell,
                            runningWrite.Artifact,
                            outcome.State,
                            attempt,
                            trialId,
                            UtcNow(),
                            outcome.State == ArenaExperimentRunState.Interrupted
                                ? outcome.InterruptionReason ?? ExecutorInterruptionReason
                                : null,
                            evidence);
                        var terminalWrite = await store.SaveReservedAsync(executionLease, terminal, CancellationToken.None).ConfigureAwait(false);
                        AddDiagnostics(diagnostics, terminalWrite.Diagnostics);
                    }
                    finally
                    {
                        providerGate.Release();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCancelled = true;
        }
        finally
        {
            foreach (var gate in providerGates.Values) gate.Dispose();
        }

        if (cancellationToken.IsCancellationRequested) wasCancelled = true;
        var final = await store.LoadAllAsync(UtcNow(), CancellationToken.None).ConfigureAwait(false);
        AddDiagnostics(diagnostics, final.Diagnostics);
        return new(
            expansion.Cells.Length,
            eligible.Length,
            started,
            wasCancelled,
            final.Runs,
            [.. diagnostics
                .Distinct()
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)]);
    }

    private static ArenaExperimentRunContract CreateRun(
        ArenaExperimentCellPlan cell,
        ArenaExperimentRunContract? existing,
        ArenaExperimentRunState state,
        int attempt,
        string trialId,
        DateTimeOffset updatedAt,
        string? interruptionReason,
        ArenaEvidenceAssertion evidence)
    {
        var evidenceItems = (existing?.Evidence ?? [])
            .Append(evidence)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            ArenaContractSchemas.ExperimentRun,
            cell.RunId,
            existing?.CreatedAtUtc ?? updatedAt,
            cell.ExperimentId,
            cell.ExperimentFingerprint,
            cell.VariantFingerprint,
            cell.Repetition,
            cell.CellKey,
            state,
            attempt,
            updatedAt,
            (existing?.TrialIds ?? [])
                .Append(trialId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToImmutableArray(),
            interruptionReason,
            evidenceItems);
    }

    private static ArenaExperimentRunContract CreateRun(
        ArenaExperimentCellPlan cell,
        ArenaExperimentRunContract? existing,
        ArenaExperimentRunState state,
        int attempt,
        string trialId,
        DateTimeOffset updatedAt,
        string? interruptionReason,
        ImmutableArray<ArenaEvidenceAssertion> evidence)
    {
        var mergedEvidence = (existing?.Evidence ?? [])
            .Concat(evidence)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        return new(
            ArenaContractSchemas.ExperimentRun,
            cell.RunId,
            existing?.CreatedAtUtc ?? updatedAt,
            cell.ExperimentId,
            cell.ExperimentFingerprint,
            cell.VariantFingerprint,
            cell.Repetition,
            cell.CellKey,
            state,
            attempt,
            updatedAt,
            (existing?.TrialIds ?? [])
                .Append(trialId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToImmutableArray(),
            interruptionReason,
            mergedEvidence);
    }

    private static Dictionary<string, SemaphoreSlim> CreateProviderGates(
        ArenaExperimentExpansion expansion,
        IReadOnlyDictionary<string, int>? budgets,
        int maximumParallelism)
    {
        var result = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        foreach (var provider in expansion.Cells.Select(item => item.ProviderProfileId).Distinct(StringComparer.Ordinal))
        {
            var budget = budgets is not null && budgets.TryGetValue(provider, out var configured)
                ? configured
                : maximumParallelism;
            if (budget < 1 || budget > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(budgets), "Provider concurrency budgets must be from 1 through 32.");
            }

            result.Add(provider, new SemaphoreSlim(Math.Min(budget, maximumParallelism)));
        }

        return result;
    }

    private static void ValidateInputs(
        ArenaExperimentContract experiment,
        ArenaExperimentExpansion expansion,
        ArenaExperimentRunnerOptions options)
    {
        var expected = ExperimentExpander.Expand(experiment);
        if (!string.Equals(expansion.ExperimentId, experiment.Id, StringComparison.Ordinal)
            || !string.Equals(expansion.ExperimentFingerprint, expected.ExperimentFingerprint, StringComparison.Ordinal)
            || expansion.Cells.Length != expected.Cells.Length
            || !expansion.Cells.Zip(expected.Cells).All(pair => CellMatches(pair.First, pair.Second)))
        {
            throw new InvalidDataException("Expansion does not match the supplied experiment contract.");
        }

        if (options.MaximumParallelism is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum parallelism must be from 1 through 32.");
        }
    }

    private static bool CellMatches(ArenaExperimentCellPlan left, ArenaExperimentCellPlan right) =>
        string.Equals(left.ExperimentId, right.ExperimentId, StringComparison.Ordinal)
        && string.Equals(left.ExperimentFingerprint, right.ExperimentFingerprint, StringComparison.Ordinal)
        && string.Equals(left.ProviderProfileId, right.ProviderProfileId, StringComparison.Ordinal)
        && string.Equals(left.VariantFingerprint, right.VariantFingerprint, StringComparison.Ordinal)
        && left.Repetition == right.Repetition
        && string.Equals(left.CellKey, right.CellKey, StringComparison.Ordinal)
        && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && left.Values.Length == right.Values.Length
        && left.Values.Zip(right.Values).All(pair => pair.First == pair.Second);

    private static void ValidateExecutorOutcome(ArenaExperimentCellExecutionResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.State is not (ArenaExperimentRunState.Completed
            or ArenaExperimentRunState.Failed
            or ArenaExperimentRunState.Interrupted))
        {
            throw new InvalidOperationException("Cell executor must return a completed, failed, or interrupted state.");
        }

        if (outcome.State == ArenaExperimentRunState.Interrupted
            && string.IsNullOrWhiteSpace(outcome.InterruptionReason))
        {
            throw new InvalidOperationException("Interrupted executor results require a bounded reason.");
        }
    }

    private static string CreateTrialId(string cellKey, int attempt) =>
        $"trial:{ExperimentExpander.Hash($"{cellKey}\n{attempt}")}";

    private static bool LatestAttemptMatchesPlan(ArenaExperimentRunContract run, string planReference)
    {
        if (run.Attempts < 1)
        {
            return false;
        }
        var latestTrialId = CreateTrialId(run.CellKey, run.Attempts);
        var latestPlanEvidenceId = $"evidence:{ExperimentExpander.Hash($"{latestTrialId}\nexecution_plan")}";
        return run.Evidence.Any(evidence =>
            evidence.Id.Equals(latestPlanEvidenceId, StringComparison.Ordinal)
            && evidence.ReferenceId?.Equals(planReference, StringComparison.Ordinal) == true);
    }

    private static ArenaEvidenceAssertion Evidence(string state, string trialId, string summary) =>
        new(
            $"evidence:{ExperimentExpander.Hash($"{trialId}\n{state}")}",
            ArenaEvidenceState.Observed,
            summary,
            trialId);

    private static string StateSummary(ArenaExperimentRunState state) => state switch
    {
        ArenaExperimentRunState.Completed => "Experiment cell executor reported completion.",
        ArenaExperimentRunState.Failed => "Experiment cell executor reported failure.",
        ArenaExperimentRunState.Interrupted => "Experiment cell executor reported interruption.",
        _ => "Experiment cell reached a terminal state."
    };

    private DateTimeOffset UtcNow()
    {
        var value = _timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }

    private static void AddDiagnostics(
        ConcurrentBag<ArenaArtifactDiagnostic> target,
        ImmutableArray<ArenaArtifactDiagnostic> values)
    {
        foreach (var value in values) target.Add(value);
    }
}
