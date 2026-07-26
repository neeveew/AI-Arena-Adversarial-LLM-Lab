using System.Globalization;

using AgentCommandHistoryItem = AIArena.Wpf.AgentWorkspaceCoordinator.AgentCommandHistoryItem;
using AgentWorkspaceFileReceipt = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt;

namespace AIArena.Wpf;

/// <summary>
/// Pure autonomy and loop-guard policy for the Agent workspace. The coordinator
/// owns counters, UI state, and pause side effects; this service decides when
/// auto-run should pause and what auto-continue prompt hints should say.
/// </summary>
internal static class AgentAutonomyPolicyService
{
    internal const int NoChangePauseThreshold = 2;

    internal static AgentAutonomyLoopDecision EvaluateRepeatedCommand(
        AgentCommandPreview preview,
        IEnumerable<AgentCommandHistoryItem> commandHistory)
    {
        var latest = commandHistory.FirstOrDefault(IsCompletedHistoryItem);
        if (latest is not null
            && latest.Shell.Equals(preview.Shell, StringComparison.OrdinalIgnoreCase)
            && AgentCommandResultService.CommandsEquivalent(latest.Command, preview.Command))
        {
            return AgentAutonomyLoopDecision.Pause(
                "Loop guard paused autonomy because Auto Continue proposed the same command again.");
        }

        return AgentAutonomyLoopDecision.Continue();
    }

    internal static AgentAutoContinueResultPolicy EvaluateAutoContinueResult(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        int consecutiveNoChangeResults,
        bool promptRequiresCommand,
        bool isArtifactVerificationResult,
        bool successfulNoChangeIsExpected)
    {
        if (isArtifactVerificationResult
            || !result.Ok
            || !promptRequiresCommand
            || WorkspaceScannerService.ReceiptHasChanges(receipt)
            || WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt)
            || successfulNoChangeIsExpected)
        {
            return AgentAutoContinueResultPolicy.Reset();
        }

        var nextCount = consecutiveNoChangeResults + 1;
        return nextCount >= NoChangePauseThreshold
            ? AgentAutoContinueResultPolicy.Pause(nextCount, "Loop guard paused autonomy after repeated no-change app commands.")
            : AgentAutoContinueResultPolicy.Continue(nextCount);
    }

    internal static string BuildAutoContinuePrompt(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        string latestCommandContext,
        bool isArtifactVerificationResult)
    {
        return $"""
            Continue this Agent run automatically from the latest approved command result.
            The last command {CommandState(result)}. {AutoContinueChangeHint(result, receipt, isArtifactVerificationResult)}
            Propose exactly one next command in a fenced powershell Command proposal block.
            If the app is complete enough, propose one verification or smoke-test command instead of prose.

            Latest command output:
            {latestCommandContext}
            """;
    }

    internal static string AutoContinueChangeHint(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        bool isArtifactVerificationResult)
    {
        if (isArtifactVerificationResult)
        {
            return "The last artifact preview or verification command did not need to change files. Continue only if the output shows an issue; otherwise propose the next build, smoke-test, or finishing verification step.";
        }

        if (WorkspaceScannerService.ReceiptHasChanges(receipt))
        {
            return "Continue with the next smallest useful build, run, verify, or repair step.";
        }

        if (WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            return "The file receipt scan hit its cap, so changes outside the scanned window are unknown. Prefer a narrow inspection of the intended output paths or a verification command before treating this as no-change.";
        }

        return "The last command did not change tracked workspace files, so prioritize a repair or file-writing command if this is still an app-building task.";
    }

    internal static string FollowUpActivityDetail(int remainingSteps)
    {
        return $"{remainingSteps.ToString(CultureInfo.InvariantCulture)} follow-up step{(remainingSteps == 1 ? "" : "s")} left.";
    }

    private static string CommandState(AgentCommandResult result)
    {
        return result.Ok ? "succeeded" : result.TimedOut ? "timed out" : "failed";
    }

    private static bool IsCompletedHistoryItem(AgentCommandHistoryItem item)
    {
        return !item.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)
            && !item.Status.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct AgentAutonomyLoopDecision(bool ShouldPause, string Reason)
{
    public static AgentAutonomyLoopDecision Continue()
    {
        return new AgentAutonomyLoopDecision(false, "");
    }

    public static AgentAutonomyLoopDecision Pause(string reason)
    {
        return new AgentAutonomyLoopDecision(true, reason);
    }
}

internal readonly record struct AgentAutoContinueResultPolicy(
    bool ShouldPause,
    int NextConsecutiveNoChangeResults,
    string Reason)
{
    public static AgentAutoContinueResultPolicy Reset()
    {
        return new AgentAutoContinueResultPolicy(false, 0, "");
    }

    public static AgentAutoContinueResultPolicy Continue(int nextConsecutiveNoChangeResults)
    {
        return new AgentAutoContinueResultPolicy(false, nextConsecutiveNoChangeResults, "");
    }

    public static AgentAutoContinueResultPolicy Pause(int nextConsecutiveNoChangeResults, string reason)
    {
        return new AgentAutoContinueResultPolicy(true, nextConsecutiveNoChangeResults, reason);
    }
}
