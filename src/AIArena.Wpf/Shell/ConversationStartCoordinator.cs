using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

/// <summary>Relocates the single Operator composer and sequences its durable first send before a run.</summary>
internal sealed class ConversationStartCoordinator
{
    private readonly Border composer;
    private readonly ContentControl dock;
    private readonly ContentControl starterHost;
    private readonly FrameworkElement starterPanel;
    private readonly TextBlock hintText;
    private readonly Button sendAndStartButton;
    private readonly TextBox turnText;
    private readonly OperatorTurnCoordinator operatorTurns;
    private readonly Func<ArenaViewSnapshot?> snapshot;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<Task> startAutoChatAsync;
    private readonly Action revealArena;
    private readonly Action<string> setRunStatus;
    private readonly Action<bool>? setOpeningPresentation;
    private ArenaViewSnapshot? appliedSnapshot;
    private int contextVersion;
    private bool busy;
    private bool autoChatRunning;
    private bool sending;
    private string sendFailureHint = "";

    public ConversationStartCoordinator(
        Border composer,
        ContentControl dock,
        ContentControl starterHost,
        FrameworkElement starterPanel,
        TextBlock hintText,
        Button sendAndStartButton,
        TextBox turnText,
        OperatorTurnCoordinator operatorTurns,
        Func<ArenaViewSnapshot?> snapshot,
        Func<SessionSummary?> activeSession,
        Func<Task> startAutoChatAsync,
        Action revealArena,
        Action<string> setRunStatus,
        Action<bool>? setOpeningPresentation = null)
    {
        this.composer = composer;
        this.dock = dock;
        this.starterHost = starterHost;
        this.starterPanel = starterPanel;
        this.hintText = hintText;
        this.sendAndStartButton = sendAndStartButton;
        this.turnText = turnText;
        this.operatorTurns = operatorTurns;
        this.snapshot = snapshot;
        this.activeSession = activeSession;
        this.startAutoChatAsync = startAutoChatAsync;
        this.revealArena = revealArena;
        this.setRunStatus = setRunStatus;
        this.setOpeningPresentation = setOpeningPresentation;
        turnText.TextChanged += (_, _) =>
        {
            sendFailureHint = "";
            UpdateActions();
        };
    }

    internal static bool ShouldCenterComposer(ArenaViewSnapshot? current) =>
        current is { FactoryMode: true, MatchEnded: false, HasUnresolvedContextFailure: false }
        && ArenaOperationCoordinator.FactoryInputState(current) == FactoryConversationInputState.None;

    public void ApplySnapshot(ArenaViewSnapshot current)
    {
        if (appliedSnapshot is not null
            && (!appliedSnapshot.SessionId.Equals(current.SessionId, StringComparison.OrdinalIgnoreCase)
                || !appliedSnapshot.SessionInstanceId.Equals(current.SessionInstanceId, StringComparison.Ordinal)
                || appliedSnapshot.FactoryMode != current.FactoryMode))
        {
            contextVersion++;
            sendFailureHint = "";
        }
        appliedSnapshot = current;
        var opening = ShouldCenterComposer(current);
        var destination = opening ? starterHost : dock;
        if (!ReferenceEquals(destination.Content, composer))
        {
            if (ReferenceEquals(dock.Content, composer)) dock.Content = null;
            if (ReferenceEquals(starterHost.Content, composer)) starterHost.Content = null;
            destination.Content = composer;
        }
        starterPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        dock.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
        operatorTurns.SetOpeningConversationPresentation(opening);
        setOpeningPresentation?.Invoke(opening);
        UpdateActions();
    }

    public void UpdateBusyState(bool isBusy, bool isAutoChatRunning)
    {
        busy = isBusy;
        autoChatRunning = isAutoChatRunning;
        UpdateActions();
    }

    public bool FocusIfOnlyStartingMessageMissing()
    {
        var current = snapshot();
        if (busy || autoChatRunning || current is null
            || !ArenaOperationCoordinator.EvaluateReadiness(current).RequiresConversationStart)
            return false;
        ApplySnapshot(current);
        revealArena();
        setRunStatus("Write the first public message, then choose Send and start Auto Chat.");
        turnText.BringIntoView();
        turnText.Focus();
        turnText.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (ShouldCenterComposer(snapshot()) && !busy) turnText.Focus();
        }));
        return true;
    }

    public async Task SendAndStartAsync()
    {
        var current = snapshot();
        if (sending || busy || autoChatRunning || !ShouldCenterComposer(current)) return;
        var readiness = ArenaOperationCoordinator.EvaluateReadiness(current!);
        if (!readiness.RequiresConversationStart)
        {
            setRunStatus(readiness.Message);
            UpdateActions();
            return;
        }
        var version = contextVersion;
        OperatorTurnSendReceipt? receipt;
        sending = true;
        sendFailureHint = "";
        UpdateActions();
        try
        {
            receipt = await operatorTurns.SendConversationStartAsync(current!.SessionId, current.SessionInstanceId);
        }
        finally
        {
            sending = false;
            UpdateActions();
        }
        if (receipt is null)
        {
            sendFailureHint = "The first message was not sent. Your draft is retained; check the reported error and try again.";
            UpdateActions();
            return;
        }

        if (!receipt.Completed)
        {
            sendFailureHint = "The public message was saved, but completion failed. Review the transcript and reported error before retrying.";
            UpdateActions();
            return;
        }

        var latest = snapshot();
        if (contextVersion != version || busy || autoChatRunning
            || latest is null || !ReceiptStillCurrent(receipt, latest, activeSession()?.Id))
        {
            setRunStatus("Public message saved. The session or mode changed, so Auto Chat was not started.");
            return;
        }
        var latestReadiness = ArenaOperationCoordinator.EvaluateReadiness(latest);
        if (!latestReadiness.CanRun)
        {
            setRunStatus($"Public message saved. {latestReadiness.Message}");
            return;
        }
        await startAutoChatAsync();
    }

    internal static bool ReceiptStillCurrent(
        OperatorTurnSendReceipt receipt, ArenaViewSnapshot current, string? activeSessionId) =>
        receipt.FactoryMode && current.FactoryMode
        && string.Equals(activeSessionId, receipt.SessionId, StringComparison.OrdinalIgnoreCase)
        && current.SessionId.Equals(receipt.SessionId, StringComparison.OrdinalIgnoreCase)
        && current.SessionInstanceId.Equals(receipt.SessionInstanceId, StringComparison.Ordinal);

    private void UpdateActions()
    {
        var current = snapshot();
        var readiness = current is null ? null : ArenaOperationCoordinator.EvaluateReadiness(current);
        var opening = ShouldCenterComposer(current);
        sendAndStartButton.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        sendAndStartButton.IsEnabled = opening && readiness?.RequiresConversationStart == true
            && !busy && !autoChatRunning && !sending && !string.IsNullOrWhiteSpace(turnText.Text);
        var hint = sendFailureHint.Length > 0 ? sendFailureHint
            : readiness is { RequiresConversationStart: false, CanRun: false } ? readiness.Message
            : "Send the first message for all agents to respond to.";
        hintText.Text = hint;
        sendAndStartButton.ToolTip = hint;
        AutomationProperties.SetHelpText(sendAndStartButton, hint);
    }
}
