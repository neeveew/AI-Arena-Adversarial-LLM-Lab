using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public static class ArenaExperimentRunPolicy
{
    public const string ProcessRestartReason = "process_restart";

    public static string CreateCellKey(
        string experimentFingerprint,
        string variantFingerprint,
        int repetition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantFingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(repetition);

        var identity = $"{experimentFingerprint.ToLowerInvariant()}\n{variantFingerprint.ToLowerInvariant()}\n{repetition}";
        return $"cell:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    public static string CreateTrialId(string cellKey, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return $"trial:{ExperimentExpander.Hash($"{cellKey}\n{attempt}")}";
    }

    public static string CreateExecutionPlanEvidenceId(string trialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trialId);
        return $"evidence:{ExperimentExpander.Hash($"{trialId}\nexecution_plan")}";
    }

    /// <summary>
    /// Proves that the latest logical attempt retained evidence for the supplied
    /// resolved execution plan. Older trial evidence never authorizes reuse.
    /// </summary>
    public static bool LatestAttemptMatchesPlan(
        ArenaExperimentRunContract run,
        string planFingerprintOrReference)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Attempts < 1 || string.IsNullOrWhiteSpace(planFingerprintOrReference))
        {
            return false;
        }

        var planReference = planFingerprintOrReference.StartsWith("plan:", StringComparison.Ordinal)
            ? planFingerprintOrReference
            : $"plan:{planFingerprintOrReference}";
        var latestTrialId = CreateTrialId(run.CellKey, run.Attempts);
        if (!run.TrialIds.Contains(latestTrialId, StringComparer.Ordinal))
        {
            return false;
        }
        var latestPlanEvidenceId = CreateExecutionPlanEvidenceId(latestTrialId);
        return run.Evidence.Any(evidence =>
            evidence.Id.Equals(latestPlanEvidenceId, StringComparison.Ordinal)
            && evidence.State == ArenaEvidenceState.Observed
            && evidence.ReferenceId?.Equals(planReference, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// A process restart cannot prove that an in-flight provider trial completed.
    /// Queued and terminal cells retain their state; only Running is normalized to
    /// an explicit Interrupted terminal state for deterministic resume handling.
    /// </summary>
    public static ArenaExperimentRunContract NormalizeAfterRestart(
        ArenaExperimentRunContract run,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (observedAtUtc.Offset != TimeSpan.Zero || observedAtUtc == default)
        {
            throw new ArgumentException("Restart observation time must be UTC.", nameof(observedAtUtc));
        }

        if (run.State != ArenaExperimentRunState.Running)
        {
            return run;
        }

        return run with
        {
            State = ArenaExperimentRunState.Interrupted,
            UpdatedAtUtc = observedAtUtc < run.UpdatedAtUtc ? run.UpdatedAtUtc : observedAtUtc,
            InterruptionReason = ProcessRestartReason
        };
    }

    public static bool IsTerminal(ArenaExperimentRunState state) =>
        state is ArenaExperimentRunState.Completed
            or ArenaExperimentRunState.Failed
            or ArenaExperimentRunState.Cancelled
            or ArenaExperimentRunState.Interrupted;
}
