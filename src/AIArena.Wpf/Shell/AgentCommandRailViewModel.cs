using System.Globalization;

using AgentWorkspaceFileReceipt = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt;

namespace AIArena.Wpf;

/// <summary>
/// Pure command-rail presentation state for the Agent workspace. WPF controls are
/// still wired by the coordinator; this record keeps status copy out of UI glue.
/// </summary>
internal sealed record AgentCommandRailViewModel(
    string CommandStatus,
    string CommandSource,
    string BuildEvidenceSummary)
{
    public static IReadOnlyList<AgentCommandRiskChip> RiskChipsForPreview(AgentCommandPreview preview)
    {
        var border = preview.Ok ? "AssistBorderBrush" : "DangerBorderBrush";
        return preview.Risks
            .DefaultIfEmpty(preview.Ok ? "Workspace scoped" : "Blocked")
            .Select(risk => new AgentCommandRiskChip(risk, border))
            .ToArray();
    }

    public static string PreviewStatus(AgentCommandPreview preview)
    {
        if (!preview.Ok)
        {
            return "Preview blocked.";
        }

        return preview.Risks.Count == 0
            ? "Preview ready. Approval required."
            : $"Preview ready with {preview.Risks.Count.ToString(CultureInfo.InvariantCulture)} risk flag{(preview.Risks.Count == 1 ? "" : "s")}.";
    }

    public static string RunningStatus(string shell)
    {
        return $"Running {shell}...";
    }

    public static string OutputSummary(IReadOnlyList<AgentWorkspaceCoordinator.AgentOutputItem> items)
    {
        if (items.Count == 0)
        {
            return "No artifacts yet.";
        }

        var first = items[0];
        return $"{first.Label}: {first.State}.";
    }

    public static AgentCommandRailViewModel FromCommandResult(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool noChangeRequiresRepair,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        var actionTitle = string.IsNullOrWhiteSpace(artifactActionTitle)
            ? "Artifact check"
            : artifactActionTitle;

        return new AgentCommandRailViewModel(
            CommandStatusAfterResult(result, receipt, noChangeRequiresRepair, isArtifactVerificationResult, actionTitle),
            CommandSourceAfterResult(result, receipt, noChangeRequiresRepair, isArtifactVerificationResult, actionTitle),
            BuildEvidenceAfterResult(result, receipt, noChangeRequiresRepair, isArtifactVerificationResult, actionTitle));
    }

    public static string BuildWorkSummaryLine(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        string nextAction,
        string artifactSuggestionSummary = "",
        string artifactVerificationSummary = "",
        bool artifactVerificationSucceeded = false)
    {
        var state = result.Ok
            ? $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}"
            : result.Canceled
                ? "Cancelled"
                : result.TimedOut
                    ? "Timed out"
                    : $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}";
        string changed;
        if (artifactVerificationSucceeded && WorkspaceScannerService.ReceiptHasKnownNoChanges(receipt))
        {
            changed = "No tracked file changes expected";
        }
        else if (WorkspaceScannerService.ReceiptHasChanges(receipt))
        {
            changed = receipt.Summary;
        }
        else if (WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            changed = "File scan limited; changes outside the scanned window are unknown";
        }
        else
        {
            changed = "No tracked file changes";
        }

        var paths = ChangedPathSummary(receipt, 2);
        if (!string.IsNullOrWhiteSpace(paths))
        {
            changed = $"{changed} ({paths})";
        }

        var artifact = string.IsNullOrWhiteSpace(artifactSuggestionSummary)
            ? ""
            : $" Artifact: {Truncate(artifactSuggestionSummary, 90)}.";
        var artifactCheck = string.IsNullOrWhiteSpace(artifactVerificationSummary)
            ? ""
            : $" Artifact check: {Truncate(artifactVerificationSummary, 90)}.";
        return $"{state} in {FormatElapsed(result.Elapsed)} | {changed}.{artifact}{artifactCheck} {Truncate(nextAction, 120)}";
    }

    private static string CommandStatusAfterResult(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool noChangeRequiresRepair,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        var status = result.Ok
            ? $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)} in {FormatElapsed(result.Elapsed)}."
            : result.Canceled
                ? "Command cancelled."
                : result.TimedOut
                    ? "Command timed out."
                    : $"Exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
        status = $"{status} {receipt.Summary}.";
        if (isArtifactVerificationResult && result.Ok)
        {
            status = $"{status} {artifactActionTitle} completed; no workspace file changes were expected.";
        }
        else if (noChangeRequiresRepair)
        {
            status = $"{status} No tracked file changes; the app may not be written yet.";
        }
        else if (WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            status = $"{status} File scan limited; changes outside the scanned window are unknown.";
        }

        return status;
    }

    private static string CommandSourceAfterResult(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool noChangeRequiresRepair,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        if (result.Canceled)
        {
            return "Command cancelled. Use Next Step for a safer retry.";
        }

        if (!result.Ok)
        {
            return isArtifactVerificationResult
                ? $"{artifactActionTitle} failed. Use Stage Repair."
                : "Last command failed. Use Next Step to repair.";
        }

        if (isArtifactVerificationResult)
        {
            return $"{artifactActionTitle} succeeded. Continue or inspect the preview.";
        }

        return noChangeRequiresRepair
            ? "No files changed. Use Next Step to repair."
            : WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt)
                ? "File scan limited. Use Next Step to inspect or verify the expected output."
                : "Last command finished. Use Next Step for follow-up.";
    }

    private static string BuildEvidenceAfterResult(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool noChangeRequiresRepair,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        if (result.Canceled)
        {
            return "Command cancelled; repair or choose a smaller next step.";
        }

        if (!result.Ok)
        {
            if (isArtifactVerificationResult)
            {
                return $"{artifactActionTitle} failed; repair next.";
            }

            return result.TimedOut
                ? "Command timed out; repair next."
                : "Command failed; repair next.";
        }

        if (isArtifactVerificationResult)
        {
            return $"{artifactActionTitle} succeeded; review the preview or continue.";
        }

        if (noChangeRequiresRepair)
        {
            return "No workspace file changes detected; repair next.";
        }

        if (WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            return "File scan limited; changes outside the scanned window are unknown.";
        }

        return WorkspaceScannerService.ReceiptHasChanges(receipt)
            ? "Workspace files changed; verify next."
            : "Command completed; verify if needed.";
    }

    private static string ChangedPathSummary(AgentWorkspaceFileReceipt receipt, int maxPaths)
    {
        var paths = receipt.Created
            .Concat(receipt.Modified)
            .Concat(receipt.Deleted)
            .Take(Math.Max(0, maxPaths))
            .ToArray();
        if (paths.Length == 0)
        {
            return "";
        }

        var total = receipt.Created.Count + receipt.Modified.Count + receipt.Deleted.Count;
        var suffix = total > paths.Length
            ? $", +{(total - paths.Length).ToString(CultureInfo.InvariantCulture)} more"
            : "";
        return $"{string.Join(", ", paths)}{suffix}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds < 1
            ? $"{Math.Max(1, (int)Math.Round(elapsed.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)}ms"
            : $"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..Math.Max(0, maxChars - 16)] + "... [truncated]";
    }
}

internal sealed record AgentCommandRiskChip(string Label, string BorderResourceKey);
