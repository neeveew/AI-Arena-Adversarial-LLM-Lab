using AgentArtifactVerification = AIArena.Wpf.AgentWorkspaceCoordinator.AgentArtifactVerification;
using AgentResultFollowUpDescriptor = AIArena.Wpf.AgentWorkspaceCoordinator.AgentResultFollowUpDescriptor;
using AgentWorkspaceFileReceipt = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt;

namespace AIArena.Wpf;

/// <summary>
/// Pure command-result policy for the Agent workspace. The coordinator owns UI
/// controls and loop counters; this service decides result labels, follow-up
/// prompts, and no-change repair rules.
/// </summary>
internal static class AgentCommandResultService
{
    internal static string CommandNextAction(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool promptRequiresCommand,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        if (isArtifactVerificationResult)
        {
            var actionTitle = string.IsNullOrWhiteSpace(artifactActionTitle)
                ? "Artifact preview/verification"
                : artifactActionTitle;
            return result.Ok
                ? $"{actionTitle} completed; no workspace file changes were expected. Use Stage Next only if the output shows an issue or the app needs another build step."
                : $"{actionTitle} failed. Use Stage Repair to ask for one repair command based on the output.";
        }

        if (result.Ok)
        {
            if (WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
            {
                return "The file receipt scan hit its cap, so changes outside the scanned window are unknown. Use Stage Verify or Stage Next to inspect the intended output path before assuming no files changed.";
            }

            return SuccessfulNoChangeRequiresRepair(result, receipt, promptRequiresCommand, isArtifactVerificationResult)
                ? "No tracked files changed for an action request. Use Stage Repair to ask for a repair or file-writing command."
                : "Use Stage Verify or Stage Next to ask the Agent to validate or continue from this output.";
        }

        return result.Canceled
            ? "Use Stage Retry to ask the Agent for a safer, shorter follow-up command."
            : "Use Stage Repair to ask the Agent for one repair command based on the failure output.";
    }

    internal static AgentResultFollowUpDescriptor ResultFollowUpDescriptor(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt? receipt,
        bool promptRequiresCommand,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        var receiptSummary = receipt is null ? "No file receipt was captured." : receipt.Summary;
        if (receipt is not null && isArtifactVerificationResult)
        {
            var actionTitle = string.IsNullOrWhiteSpace(artifactActionTitle) ? "Artifact check" : artifactActionTitle;
            var actionLower = actionTitle.ToLowerInvariant();
            return result.Ok
                ? new AgentResultFollowUpDescriptor(
                    "Stage Next",
                    $"Stage a continuation prompt from the successful {actionLower} result.",
                    "Next staged",
                    $"Continuation prompt staged from the latest {actionLower}.",
                    "Artifact follow-up staged",
                    $"{actionTitle} completed and no workspace file changes were expected. {receiptSummary}",
                    $"Artifact follow-up prompt staged from the latest {actionLower}.",
                    $"Next-step prompt staged from {actionLower}.")
                : new AgentResultFollowUpDescriptor(
                    "Stage Repair",
                    $"Stage a repair prompt from the failed {actionLower} result.",
                    "Repair staged",
                    $"Repair prompt staged from the failed {actionLower}.",
                    "Artifact repair staged",
                    $"{actionTitle} failed. {receiptSummary}",
                    $"Artifact repair prompt staged from the latest {actionLower}.",
                    "Artifact repair prompt staged.");
        }

        if (result.Canceled)
        {
            return new AgentResultFollowUpDescriptor(
                "Stage Retry",
                "Stage a safer retry prompt from the cancelled command output.",
                "Retry staged",
                "Safer retry prompt staged from the cancelled command.",
                "Retry prompt staged",
                $"The last command was cancelled. {receiptSummary}",
                "Retry prompt staged from the cancelled command.",
                "Retry prompt staged.");
        }

        if (!result.Ok)
        {
            return new AgentResultFollowUpDescriptor(
                "Stage Repair",
                "Stage a repair prompt from the failed command output.",
                "Repair staged",
                "Repair prompt staged from the failed command.",
                "Repair prompt staged",
                $"The last command failed or timed out. {receiptSummary}",
                "Repair prompt staged from the latest command failure.",
                "Repair prompt staged.");
        }

        if (receipt is not null && SuccessfulNoChangeRequiresRepair(result, receipt, promptRequiresCommand, isArtifactVerificationResult))
        {
            return new AgentResultFollowUpDescriptor(
                "Stage Repair",
                "Stage a file-writing repair prompt because the last app command changed no files.",
                "Repair staged",
                "No-change repair prompt staged from the latest command.",
                "No-change repair staged",
                $"The last app-building command succeeded but changed no tracked workspace files. {receiptSummary}",
                "No-change repair prompt staged.",
                "No-change repair prompt staged.");
        }

        if (receipt is not null && WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            return new AgentResultFollowUpDescriptor(
                "Stage Next",
                "Stage a cautious follow-up prompt because the file receipt scan was limited.",
                "Next staged",
                "Limited-scan follow-up prompt staged from the latest command.",
                "Limited-scan follow-up staged",
                $"The last command completed, but the file receipt scan reached its cap and changes outside the scanned window are unknown. {receiptSummary}",
                "Limited-scan follow-up prompt staged from the latest command result.",
                "Limited-scan follow-up prompt staged.");
        }

        if (receipt is not null && WorkspaceScannerService.ReceiptHasChanges(receipt))
        {
            return new AgentResultFollowUpDescriptor(
                "Stage Next",
                "Stage a continuation prompt from the latest changed files and command output.",
                "Next staged",
                "Continuation prompt staged from the latest command result.",
                "Next-step prompt staged",
                $"The last command changed workspace files. {receiptSummary}",
                "Next-step prompt staged from the latest command result.",
                "Next-step prompt staged.");
        }

        return new AgentResultFollowUpDescriptor(
            "Stage Next",
            "Stage a cautious follow-up prompt from the latest command output.",
            "Next staged",
            "Follow-up prompt staged from the latest command result.",
            "Next-step prompt staged",
            $"The last command completed. {receiptSummary}",
            "Next-step prompt staged from the latest command result.",
            "Next-step prompt staged.");
    }

    internal static AgentCommandRailViewModel CommandRailViewModel(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool promptRequiresCommand,
        bool isArtifactVerificationResult,
        string artifactActionTitle)
    {
        return AgentCommandRailViewModel.FromCommandResult(
            result,
            receipt,
            SuccessfulNoChangeRequiresRepair(result, receipt, promptRequiresCommand, isArtifactVerificationResult),
            isArtifactVerificationResult,
            string.IsNullOrWhiteSpace(artifactActionTitle) ? "Artifact check" : artifactActionTitle);
    }

    internal static bool SuccessfulNoChangeRequiresRepair(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool promptRequiresCommand,
        bool isArtifactVerificationResult)
    {
        return result.Ok
            && promptRequiresCommand
            && WorkspaceScannerService.ReceiptHasKnownNoChanges(receipt)
            && !SuccessfulNoChangeIsExpected(result, isArtifactVerificationResult)
            && !isArtifactVerificationResult;
    }

    internal static bool SuccessfulNoChangeIsExpected(AgentCommandResult result, bool isArtifactVerificationResult)
    {
        if (!result.Ok || isArtifactVerificationResult)
        {
            return false;
        }

        return AgentWorkspaceCoordinator.CommandLooksLikeVerificationOrInspection(result.Command);
    }

    internal static bool IsArtifactVerificationResult(
        bool lastCommandWasArtifactVerification,
        AgentArtifactVerification? latestArtifactVerification,
        AgentCommandResult result)
    {
        return lastCommandWasArtifactVerification
            && latestArtifactVerification is not null
            && latestArtifactVerification.Shell.Equals(result.Shell, StringComparison.OrdinalIgnoreCase)
            && CommandsEquivalent(latestArtifactVerification.Command, result.Command);
    }

    internal static bool CommandsEquivalent(string left, string right)
    {
        return NormalizeCommandForLoopComparison(left)
            .Equals(NormalizeCommandForLoopComparison(right), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeCommandForLoopComparison(string command)
    {
        return string.Join(
            " ",
            (command ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
