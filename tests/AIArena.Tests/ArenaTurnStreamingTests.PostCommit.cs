using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;

internal static partial class ArenaTurnStreamingTests
{
    public static void CommittedOutcomesSurviveObserverCancellation()
    {
        foreach (var operation in new[] { "turn", "retry", "narrator", "ask-narrator" })
        {
            WithFixture((store, log, _) =>
            {
                using var cancellation = new CancellationTokenSource();
                var client = new StreamingClient((config, progress, _) =>
                {
                    progress?.Report(Answer);
                    return Task.FromResult(Success(config, Answer));
                });
                var runner = new TurnRunnerService(client, store, log);
                var original = operation == "retry" ? runner.RunOneTurnAsync().GetAwaiter().GetResult().Message : null;
                using var narrator = new NarratorService(client, store, log);
                var recorder = new Recorder(store);
                var observer = new CommitCancellationObserver(recorder, cancellation);
                var outcome = RunCommittedOutcome(operation, runner, narrator, original, cancellation.Token, observer)
                    .GetAwaiter().GetResult();
                Require(cancellation.IsCancellationRequested && outcome.Ok,
                    $"{operation}: cancellation from Completed must preserve the authoritative result");
                Require(outcome.Message is not null && outcome.Committed && outcome.Warning.Length == 0,
                    $"{operation}: committed message was omitted or successful evidence produced a warning");
                VerifyCommitted(recorder, outcome.Message!, expectedDeltas: 1);
                Require(Load(store).Engine.Messages.Count == 1,
                    $"{operation}: late cancellation duplicated or removed the committed message");
                Require(File.ReadAllText(log.EventPath()).Contains(operation == "retry" ? "native_retry_message_replaced"
                    : operation == "turn" ? "native_one_turn_completed"
                    : operation == "narrator" ? "native_narrator_completed" : "native_narrator_operator_request_completed", StringComparison.Ordinal),
                    $"{operation}: caller cancellation prevented post-commit evidence");
            });
        }
    }

    public static void CommittedOutcomesPreserveProviderResultWhenEvidenceFails()
    {
        foreach (var operation in new[] { "turn", "retry", "narrator", "ask-narrator", "decision-card" })
        foreach (var providerOk in new[] { true, false })
        foreach (var cancelEvidence in new[] { true, false })
        {
            WithFixture((store, _, root) =>
            {
                using var cancellation = new CancellationTokenSource();
                var observerCalls = 0;
                var observedCommitted = false;
                var log = new EventLogStore(root, new EventLogWriteObserver
                {
                    BeforeWrite = _ =>
                    {
                        if (++observerCalls != 2) return;
                        var committed = Load(store);
                        observedCommitted = operation == "decision-card"
                            ? committed.Engine.DecisionCard.Text.Length > 0
                            : committed.Engine.Messages.Count == 1 && committed.Engine.Messages[0].Text != "Original answer";
                        if (cancelEvidence)
                        {
                            cancellation.Cancel();
                            throw new OperationCanceledException("private canceled evidence token", cancellation.Token);
                        }
                        throw new IOException("private path and API token must not appear in the public warning");
                    }
                });
                if (operation == "retry")
                {
                    var snapshot = Load(store);
                    snapshot.Engine.Messages.Add(new DialogueMessage
                    {
                        MessageId = "message:original", Turn = 1, SpeakerId = "alpha", Speaker = "Alpha",
                        Text = "Original answer", CreatedAt = 100, Status = "ok"
                    });
                    snapshot.Engine.TurnCount = 1;
                    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
                }
                var client = new StreamingClient((config, progress, _) =>
                {
                    if (providerOk) progress?.Report(Answer);
                    return Task.FromResult(providerOk ? Success(config, Answer) : Success(config, "") with
                    {
                        Ok = false, Error = "Fixture provider rejection", FailureKind = ModelCompletionFailureKind.ProviderRejected
                    });
                });
                var runner = new TurnRunnerService(client, store, log);
                using var narrator = new NarratorService(client, store, log);
                var original = operation == "retry" ? Load(store).Engine.Messages[0] : null;
                var outcome = RunCommittedOutcome(operation, runner, narrator, original, cancellation.Token, new Recorder(store))
                    .GetAwaiter().GetResult();
                Require(outcome.Ok == providerOk && outcome.Error == (providerOk ? "" : "Fixture provider rejection"),
                    $"{operation}: evidence failure changed the provider result");
                Require(observedCommitted && observerCalls == 2 && outcome.Committed,
                    $"{operation}: evidence failure must follow the authoritative save");
                Require(outcome.Warning == PostCommitEvidence.ActivityLogWarning
                    && !outcome.Warning.Contains("private", StringComparison.OrdinalIgnoreCase),
                    $"{operation}: evidence warning was missing or disclosed private exception details");
                Require(client.StreamingCalls + client.BufferedCalls == 1,
                    $"{operation}: evidence failure replayed the committed provider request");
                var saved = Load(store);
                Require(operation == "decision-card" ? saved.Engine.DecisionCard.Text == outcome.Text
                    : saved.Engine.Messages.Single().Text == outcome.Text,
                    $"{operation}: returned result differs from durable state");
                Require(saved.Engine.LastError == (providerOk ? "" : "Fixture provider rejection"),
                    $"{operation}: secondary evidence failure corrupted durable provider diagnostics");
            });
        }
    }

    private static async Task<(bool Ok, string Error, string Text, DialogueMessage? Message, string Warning, bool Committed)> RunCommittedOutcome(
        string operation, TurnRunnerService runner, NarratorService narrator, DialogueMessage? original,
        CancellationToken cancellationToken, IProgress<ArenaTurnProgress> progress)
    {
        if (operation == "decision-card")
        {
            var result = await narrator.GenerateDecisionCardAsync("default", cancellationToken);
            return (result.Ok, result.Error, result.Text, null, result.EvidenceWarning, result.Committed);
        }
        if (operation is "narrator" or "ask-narrator")
        {
            var result = operation == "narrator"
                ? await narrator.NarrateNowAsync("default", cancellationToken, progress)
                : await narrator.AskNarratorAsync("default", "Explain the decision.", cancellationToken, progress);
            return (result.Ok, result.Error, result.Message?.Text ?? "", result.Message,
                result.EvidenceWarning, result.Message is not null);
        }
        var turn = operation == "retry"
            ? await runner.RetryTurnAsync("default", original!.Turn, original.SpeakerId, original.CreatedAt, cancellationToken, progress)
            : await runner.RunOneTurnAsync("default", cancellationToken, progress);
        Require(turn.Completion is not null && turn.Completion.FailureKind == (turn.Completion.Ok
                ? ModelCompletionFailureKind.None : ModelCompletionFailureKind.ProviderRejected),
            $"{operation}: evidence reporting changed provider failure classification");
        return (turn.Ok && turn.Completion?.Ok == true, turn.Completion?.Error ?? turn.Error,
            turn.Message?.Text ?? "", turn.Message, turn.EvidenceWarning, turn.Executed);
    }

    private sealed class CommitCancellationObserver(Recorder recorder, CancellationTokenSource cancellation)
        : IProgress<ArenaTurnProgress>
    {
        public void Report(ArenaTurnProgress value)
        {
            recorder.Report(value);
            if (value.Kind == ArenaTurnProgressKind.Completed) cancellation.Cancel();
        }
    }
}
