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
