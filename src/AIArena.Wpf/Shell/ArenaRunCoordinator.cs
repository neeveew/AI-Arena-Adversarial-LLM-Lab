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
    private readonly Func<string, Button?, Func<CancellationToken, Task>, bool, Task> runCancelableArenaBusyAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Action<DialogueMessage> speakNarratorMessage;

    private CancellationTokenSource? autoChatCancellation;
    private TaskCompletionSource<bool>? autoChatCompletion;

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
        Func<string, bool> isAgentSpeaker,
        Action<DialogueMessage>? speakNarratorMessage = null,
        Func<string, Button?, Func<CancellationToken, Task>, bool, Task>? runCancelableArenaBusyAsync = null)
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
        this.runCancelableArenaBusyAsync = runCancelableArenaBusyAsync
            ?? ((status, button, action, allowDuringAutoChat) =>
                runArenaBusyAsync(status, button, () => action(CancellationToken.None), allowDuringAutoChat));
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
        this.isAgentSpeaker = isAgentSpeaker;
        this.speakNarratorMessage = speakNarratorMessage ?? (_ => { });
    }

    public bool IsAutoChatRunning => autoChatCancellation is not null;

    /// <summary>
    /// Raised with a run-loop transition - "started", "stopped", "turn" or
    /// "narration" - after it actually happens, whichever route asked. The host
    /// turns these into control-plane events so clicking Auto Chat announces the
    /// same thing as the arena.start command.
    /// </summary>
    public Action<string>? RunLifecycle { get; set; }

    public async Task StartAutoChatAsync()
    {
        if (activeSession() is null || autoChatCancellation is not null)
        {
            return;
        }

        autoChatCancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        autoChatCompletion = completion;
        var token = autoChatCancellation.Token;
        var finalStatus = "Auto Chat running...";
        setArenaBusy(true, "Auto Chat running...", true, autoChatButton);
        RunLifecycle?.Invoke("started");

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

                await Task.Delay(autoChatCadence(), token);
            }
        }
        catch (OperationCanceledException)
        {
            finalStatus = "Auto Chat stopped.";
            SetBothStatuses(finalStatus);
        }
        catch (Exception ex)
        {
            finalStatus = $"Auto Chat stopped. {ArenaOperationCoordinator.OperationFailureStatus(ex)}";
            SetBothStatuses(finalStatus);
        }
        finally
        {
            autoChatCancellation?.Dispose();
            autoChatCancellation = null;
            try
            {
                setArenaBusy(false, finalStatus, false, null);
            }
            finally
            {
                // StopAutoChatAsync is a shutdown barrier. Publish completion only
                // after the final busy-state cleanup has run, while still releasing
                // waiters if a UI callback itself fails.
                completion.TrySetResult(true);
                if (ReferenceEquals(autoChatCompletion, completion))
                {
                    autoChatCompletion = null;
                }
            }
        }
    }

    public void StopAutoChat()
    {
        var cancellation = autoChatCancellation;
        if (cancellation is null)
        {
            return;
        }

        // Past the guard the loop is definitely being stopped, so this reports
        // the transition rather than every press of a button that was a no-op.
        RunLifecycle?.Invoke("stopped");

        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation is still requested even when a provider callback fails.
            // The running loop owns final cleanup and reports its terminal status.
        }
        catch (ObjectDisposedException)
        {
            // The loop completed between observing its cancellation source and
            // requesting cancellation; its finally block has already cleaned up.
            return;
        }

        setArenaRunStatus("Stopping Auto Chat...");
    }

    public async Task StopAutoChatAsync()
    {
        var completion = autoChatCompletion?.Task;
        if (completion is null)
        {
            return;
        }

        StopAutoChat();
        await completion;
    }

    public async Task NarrateNowAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runCancelableArenaBusyAsync("Narrator thinking...", narrateNowButton, async cancellationToken =>
        {
            var result = await narratorService.NarrateNowAsync(session.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = NarratorStatus(result);
            await refreshActiveSessionAsync(status);
            if (result.Ok && result.Message is not null)
            {
                speakNarratorMessage(result.Message);
            }
        }, true);

        RunLifecycle?.Invoke("narration");
    }

    public async Task RunOneTurnAsync()
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runCancelableArenaBusyAsync("Running native 1 TURN...", oneTurnButton, async cancellationToken =>
        {
            var result = await turnRunner.RunOneTurnAsync(session.Id, shouldEnforceVoiceDrift(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var status = OneTurnStatus(result);
            await refreshActiveSessionAsync(status);
            SetBothStatuses(status);
        }, false);

        RunLifecycle?.Invoke("turn");
    }

    public async Task RunAgentTurnAsync(AgentState agent)
    {
        var session = activeSession();
        if (session is null)
        {
            setLoadStatus("No active session.");
            return;
        }

        await runCancelableArenaBusyAsync($"Running {agent.Name} once...", null, async cancellationToken =>
        {
            var result = await turnRunner.RunAgentTurnAsync(session.Id, agent.Id, shouldEnforceVoiceDrift(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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

        await runCancelableArenaBusyAsync($"Retrying turn {message.Turn} with {message.Speaker}...", null, async cancellationToken =>
        {
            var result = await turnRunner.RetryTurnAsync(
                session.Id,
                message.Turn,
                message.SpeakerId,
                message.CreatedAt,
                shouldEnforceVoiceDrift(),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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

    private void SetBothStatuses(string status)
    {
        setArenaRunStatus(status);
        setLoadStatus(status);
    }
}
