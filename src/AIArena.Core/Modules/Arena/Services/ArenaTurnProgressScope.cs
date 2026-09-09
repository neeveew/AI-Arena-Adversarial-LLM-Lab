using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>Keeps provider attempts separate and prevents observers or late deltas from changing turn persistence.</summary>
internal sealed class ArenaTurnProgressScope(
    IProgress<ArenaTurnProgress>? observer,
    string sessionId,
    string sessionInstanceId,
    string speakerId,
    string speakerName,
    int turn) : IDisposable
{
    private readonly object gate = new();
    private readonly string operationId = Guid.NewGuid().ToString("N");
    private string attemptId = "";
    private string model = "";
    private bool acceptingDeltas;
    private bool hadDelta;
    private bool terminal;
    private bool recoveryPending;
    private ArenaTurnActivity? lastActivity;

    internal async Task<ModelCompletionResult> CompleteAsync(
        IModelProviderClient client,
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        CancellationToken cancellationToken,
        bool publishText = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string currentAttempt;
        lock (gate)
        {
            if (attemptId.Length > 0 && !terminal)
            {
                Report(ArenaTurnProgressKind.Interrupted, status: "superseded");
            }
            attemptId = currentAttempt = Guid.NewGuid().ToString("N");
            model = config.Model;
            acceptingDeltas = true;
            hadDelta = false;
            terminal = false;
            lastActivity = recoveryPending ? ArenaTurnActivity.RetryingWithReducedReasoning : null;
            recoveryPending = false;
            Report(ArenaTurnProgressKind.Started, status: "generating", activity: lastActivity);
        }
        try
        {
            ModelCompletionResult result;
            if (observer is not null && client is IActivityStreamingModelProviderClient activityStreaming)
            {
                result = await activityStreaming.CompleteChatStreamingWithActivityAsync(
                    config, messages,
                    new InlineProgress<string>(delta => PublishDelta(currentAttempt, delta, publishText)),
                    new InlineProgress<ModelProviderActivity>(activity => PublishActivity(currentAttempt, activity, publishText)),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (observer is not null && client is IStreamingModelProviderClient streaming)
            {
                result = await streaming.CompleteChatStreamingAsync(
                    config, messages,
                    new InlineProgress<string>(delta => PublishDelta(currentAttempt, delta, publishText)),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await client.CompleteChatAsync(config, messages, cancellationToken).ConfigureAwait(false);
            }
            lock (gate)
            {
                if (string.Equals(currentAttempt, attemptId, StringComparison.Ordinal))
                {
                    if (!hadDelta && publishText && !string.IsNullOrEmpty(result.Text))
                    {
                        PublishDelta(currentAttempt, result.Text, publishText: true);
                    }
                    acceptingDeltas = false;
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interrupt("canceled");
            throw;
        }
        catch
        {
            Interrupt("failed");
            throw;
        }
    }

    private void PublishDelta(string sourceAttempt, string delta, bool publishText)
    {
        lock (gate)
        {
            if (!publishText || terminal || !acceptingDeltas
                || !string.Equals(sourceAttempt, attemptId, StringComparison.Ordinal)
                || string.IsNullOrEmpty(delta))
            {
                return;
            }
            hadDelta = true;
            lastActivity = ArenaTurnActivity.Writing;
            Report(ArenaTurnProgressKind.Delta, delta, "generating");
        }
    }

    private void PublishActivity(string sourceAttempt, ModelProviderActivity activity, bool publishText)
    {
        lock (gate)
        {
            if (activity is null || terminal || !acceptingDeltas
                || !string.Equals(sourceAttempt, attemptId, StringComparison.Ordinal)) return;
            ArenaTurnActivity? stage = activity.Stage switch
            {
                ModelProviderActivityStage.Loading => ArenaTurnActivity.Loading,
                ModelProviderActivityStage.ReadingContext => ArenaTurnActivity.ReadingContext,
                ModelProviderActivityStage.Thinking => ArenaTurnActivity.Thinking,
                ModelProviderActivityStage.Writing when publishText && hadDelta => ArenaTurnActivity.Writing,
                _ => null
            };
            // Provider public-character counts can describe withheld tool-choice output.
            // Writing is visible only after the public response itself reached the observer.
            if (stage is null || stage == lastActivity) return;
            lastActivity = stage;
            Report(ArenaTurnProgressKind.Activity, activity: stage);
        }
    }

    internal void RecoveryStarting()
    {
        lock (gate)
        {
            if (attemptId.Length == 0) return;
            acceptingDeltas = false;
            terminal = true;
            recoveryPending = true;
            lastActivity = ArenaTurnActivity.RetryingWithReducedReasoning;
            Report(ArenaTurnProgressKind.Activity, activity: lastActivity);
        }
    }

    internal void Complete(DialogueMessage message)
    {
        lock (gate)
        {
            if (terminal) return;
            acceptingDeltas = false;
            terminal = true;
            SafeReport(new ArenaTurnProgress(sessionId, sessionInstanceId, operationId, attemptId,
                message.SpeakerId, message.Speaker, message.Model.Model, message.Turn,
                ArenaTurnProgressKind.Completed, message.Text, DialogueMessageIdentity.Resolve(message),
                message.Status, message.CreatedAt));
        }
    }

    internal void Interrupt(string status)
    {
        lock (gate)
        {
            if (terminal) return;
            acceptingDeltas = false;
            terminal = true;
            Report(ArenaTurnProgressKind.Interrupted, status: status);
        }
    }

    private void Report(ArenaTurnProgressKind kind, string text = "", string status = "", ArenaTurnActivity? activity = null) =>
        SafeReport(new ArenaTurnProgress(sessionId, sessionInstanceId, operationId, attemptId,
            speakerId, speakerName, model, turn, kind, Text: text, Status: status, Activity: activity));

    private void SafeReport(ArenaTurnProgress progress)
    {
        try { observer?.Report(progress); }
        catch { /* Optional presentation observers cannot alter a model call or committed history. */ }
    }

    public void Dispose() => Interrupt("interrupted");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
