using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class ArenaRunCoordinator
{
    private readonly TurnRunnerService turnRunner;
    private readonly NarratorService narratorService;
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly Button autoChatButton;
    private readonly Button oneTurnButton;
    private readonly Button narrateNowButton;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<bool> shouldEnforceVoiceDrift;
    private readonly Func<TimeSpan> autoChatCadence;
    private readonly Action<bool, string, bool, Button?> setArenaBusy;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;
    private readonly Func<DialogueMessage, bool, Task<bool>> handleInternetApprovalDialogAsync;
    private readonly Func<string, bool> isAgentSpeaker;

    private CancellationTokenSource? autoChatCancellation;

    public ArenaRunCoordinator(
        TurnRunnerService turnRunner,
        NarratorService narratorService,
        SemaphoreSlim arenaOperationLock,
        Button autoChatButton,
        Button oneTurnButton,
        Button narrateNowButton,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<bool> shouldEnforceVoiceDrift,
        Func<TimeSpan> autoChatCadence,
        Action<bool, string, bool, Button?> setArenaBusy,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus,
        Func<DialogueMessage, bool, Task<bool>> handleInternetApprovalDialogAsync,
        Func<string, bool> isAgentSpeaker)
    {
        this.turnRunner = turnRunner;
        this.narratorService = narratorService;
        this.arenaOperationLock = arenaOperationLock;
        this.autoChatButton = autoChatButton;
        this.oneTurnButton = oneTurnButton;
        this.narrateNowButton = narrateNowButton;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.shouldEnforceVoiceDrift = shouldEnforceVoiceDrift;
        this.autoChatCadence = autoChatCadence;
        this.setArenaBusy = setArenaBusy;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
        this.handleInternetApprovalDialogAsync = handleInternetApprovalDialogAsync;
        this.isAgentSpeaker = isAgentSpeaker;
    }

    public bool IsAutoChatRunning => autoChatCancellation is not null;

    public async Task StartAutoChatAsync()
    {
        if (activeSession() is null || autoChatCancellation is not null)
        {
            return;
        }

        autoChatCancellation = new CancellationTokenSource();
        var token = autoChatCancellation.Token;
        var finalStatus = "Auto Chat running...";
        setArenaBusy(true, "Auto Chat running...", true, autoChatButton);

        try
        {
            while (!token.IsCancellationRequested && activeSession() is { } session)
            {
                await arenaOperationLock.WaitAsync(token);
                OneTurnResult result;
                try
                {
                    result = await turnRunner.RunOneTurnAsync(session.Id, shouldEnforceVoiceDrift(), token);
                }
                finally
                {
                    arenaOperationLock.Release();
                }

                var status = AutoChatStatus(result);
                finalStatus = status;
                await refreshActiveSessionAsync(status);
                if (!result.Ok)
                {
                    break;
                }

                if (IsPendingInternetApproval(result.Message))
                {
                    var resolved = await handleInternetApprovalDialogAsync(result.Message!, true);
                    if (!resolved)
                    {
                        finalStatus = "Auto Chat paused for internet approval.";
                        SetBothStatuses(finalStatus);
                        break;
                    }
                }

                await Task.Delay(autoChatCadence(), token);
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Auto Chat stopped.";
            SetBothStatuses(finalStatus);
        }
        finally
        {
            autoChatCancellation?.Dispose();
            autoChatCancellation = null;
            setArenaBusy(false, finalStatus, false, null);
        }
    }

    public void StopAutoChat()
    {
        autoChatCancellation?.Cancel();
        setArenaRunStatus("Stopping Auto Chat...");
    }

    public async Task NarrateNowAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runArenaBusyAsync("Narrator thinking...", narrateNowButton, async () =>
        {
            var result = await narratorService.NarrateNowAsync(session.Id);
            var status = NarratorStatus(result);
            await refreshActiveSessionAsync(status);
        }, true);
    }

    public async Task RunOneTurnAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runArenaBusyAsync("Running native 1 TURN...", oneTurnButton, async () =>
        {
            var result = await turnRunner.RunOneTurnAsync(session.Id, shouldEnforceVoiceDrift());
            var status = OneTurnStatus(result);
            await refreshActiveSessionAsync(status);
            SetBothStatuses(status);
            if (IsPendingInternetApproval(result.Message))
            {
                await handleInternetApprovalDialogAsync(result.Message!, false);
            }
        }, false);
    }

    public async Task RunAgentTurnAsync(AgentState agent)
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runArenaBusyAsync($"Running {agent.Name} once...", null, async () =>
        {
            var result = await turnRunner.RunAgentTurnAsync(session.Id, agent.Id, shouldEnforceVoiceDrift());
            await refreshActiveSessionAsync(AgentTurnStatus(agent, result));
        }, false);
    }

    public async Task RetryTranscriptMessageAsync(TranscriptMessage message)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null || message.Turn <= 0 || !isAgentSpeaker(message.SpeakerId))
        {
            return;
        }

        await runArenaBusyAsync($"Retrying turn {message.Turn} with {message.Speaker}...", null, async () =>
        {
            var result = await turnRunner.RetryTurnAsync(session.Id, message.Turn, message.SpeakerId, message.CreatedAt, shouldEnforceVoiceDrift());
            await refreshActiveSessionAsync(RetryStatus(message, result));
        }, false);
    }

    internal static string AutoChatStatus(OneTurnResult result)
    {
        return result.Ok && result.Message is not null
            ? $"Auto Chat: {result.Message.Speaker} spoke ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
            : $"Auto Chat stopped: {result.Error}";
    }

    internal static string OneTurnStatus(OneTurnResult result)
    {
        return result.Ok && result.Message is not null
            ? $"1 TURN complete: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
            : $"1 TURN failed: {result.Error}";
    }

    internal static string NarratorStatus(NarratorResult result)
    {
        return result.Ok && result.Message is not null
            ? $"Narrator added turn {result.Message.Turn} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
            : $"Narrator failed: {result.Error}";
    }

    internal static string AgentTurnStatus(AgentState agent, OneTurnResult result)
    {
        return result.Ok && result.Message is not null
            ? $"{agent.Name} one-shot complete: {result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms"
            : $"{agent.Name} one-shot failed: {result.Error}";
    }

    internal static string RetryStatus(TranscriptMessage originalMessage, OneTurnResult result)
    {
        return result.Ok && result.Message is not null
            ? $"Retry replaced turn {originalMessage.Turn}: {result.Message.Speaker} ({result.Message.Model.Model}, {result.Message.Model.LatencyMs} ms)"
            : $"Retry failed: {result.Error}";
    }

    internal static bool IsPendingInternetApproval(DialogueMessage? message)
    {
        return message is not null
            && message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase);
    }

    private void SetBothStatuses(string status)
    {
        setArenaRunStatus(status);
        setLoadStatus(status);
    }
}
