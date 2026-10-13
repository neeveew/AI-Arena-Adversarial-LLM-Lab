using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static partial class ArenaTurnStreamingTests
{
    private static readonly string[] ActivityWorkflowKinds = ["turn", "retry", "narrator", "ask-narrator", "decision-card", "continue"];

    public static void ActivityLogFailuresPreserveCausalProviderWorkflows()
    {
        var cases = ActivityWorkflowKinds.Select(kind => (Kind: kind, Phase: "start", FailAt: 1)).ToList();
        cases.AddRange(new[] { "turn", "retry", "narrator", "decision-card" }.Select(kind => (kind, "fallback", 2)));
        cases.AddRange(new[] { "turn", "retry" }.Select(kind => (kind, "repair", 2)));
        foreach (var kind in new[] { "turn", "narrator", "decision-card" })
        {
            cases.Add((kind, "internet", 2));
            cases.Add((kind, "internet", 3));
        }
        cases.Add(("turn", "proactive", 2));
        cases.Add(("turn", "proactive", 3));
        var failed = new List<string>();
        foreach (var item in cases)
        {
            WithFixture((store, _, root) =>
            {
                var writes = 0;
                var log = new EventLogStore(root, new EventLogWriteObserver
                {
                    BeforeWrite = _ => { if (++writes == item.FailAt) throw new IOException("private evidence path sk-log-fixture-1234567890123456789"); }
                }, requireExistingSession: true);
                var priorCount = PrepareActivityWorkflow(store, item.Kind, item.Phase);
                var client = new ActivityWorkflowClient(item.Phase);
                using var internet = new InternetToolService(new ActivityInternetProvider(), log);
                var runner = new TurnRunnerService(client, store, log, internetToolService: internet);
                using var narrator = new NarratorService(client, store, log, internetToolService: internet);
                ActivityWorkflowOutcome? outcome = null;
                try { outcome = RunActivityWorkflowAsync(item.Kind, store, runner, narrator, new ContextRecoveryService(store, client, eventLogStore: log)).GetAwaiter().GetResult(); }
                catch (Exception) { }
                var saved = Load(store);
                var expectedCalls = item.Phase is "fallback" or "repair" or "internet" ? 2 : 1;
                var expectedCount = item.Kind is "retry" or "continue" or "decision-card" ? priorCount : priorCount + 1;
                var ok = outcome is { Ok: true, Committed: true } && client.Calls == expectedCalls
                    && saved.Engine.Messages.Count == expectedCount
                    && outcome.Warning == PostCommitEvidence.ActivityLogWarning
                    && !saved.Engine.LastError.Contains("private", StringComparison.OrdinalIgnoreCase)
                    && saved.Engine.Agents.All(agent => agent.Status != "thinking") && saved.Engine.Narrator.Status != "thinking";
                Console.WriteLine($"activity workflow receipt: kind={item.Kind}; phase={item.Phase}; failing_write={item.FailAt}; "
                    + $"provider_calls={client.Calls}; committed={outcome?.Committed == true}; warning={outcome?.Warning.Length > 0}");
                if (!ok) failed.Add($"{item.Kind}/{item.Phase}/{item.FailAt}");
            });
        }
        Require(failed.Count == 0, "Optional activity-log failure interrupted or concealed these causal workflows: " + string.Join(", ", failed));
    }

    public static void ActivityLogHeldLeasesDoNotStallProviderWorkflows()
    {
        var failed = new List<string>();
        foreach (var kind in ActivityWorkflowKinds)
        {
            WithFixture((store, _, root) =>
            {
                PrepareActivityWorkflow(store, kind, "start");
                var log = new EventLogStore(root, requireExistingSession: true);
                var client = new ActivityWorkflowClient("start");
                var runner = new TurnRunnerService(client, store, log);
                using var narrator = new NarratorService(client, store, log);
                var held = CrossProcessWriteLease.AcquireAsync(log.EventPath(), TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                Task<ActivityWorkflowOutcome>? operation = null;
                try
                {
                    operation = RunActivityWorkflowAsync(kind, store, runner, narrator, new ContextRecoveryService(store, client, eventLogStore: log));
                    var reached = Task.WhenAny(client.Started.Task, operation, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
                    if (!ReferenceEquals(reached, client.Started.Task)) failed.Add(kind);
                    else
                    {
                        Require(ReferenceEquals(Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(), operation),
                            $"{kind}: a failed diagnostic lease must release the snapshot guard and not delay the saved result.");
                        Require(operation.Result.Warning == PostCommitEvidence.ActivityLogWarning,
                            $"{kind}: a timed-out diagnostic write must remain visible as a generic saved-result warning.");
                    }
                }
                finally { held.Dispose(); }
                var result = operation!.GetAwaiter().GetResult();
                Require(result.Ok && result.Committed && client.Calls == 1,
                    $"{kind}: releasing a held diagnostic lease must never replay or lose the requested completion.");
            });
        }
        Require(failed.Count == 0, "A held diagnostic-log lease prevented provider requests from starting: " + string.Join(", ", failed));
    }

    public static void ActivityLogWaitsPreserveExplicitCallerCancellation()
    {
        foreach (var kind in ActivityWorkflowKinds)
        {
            WithFixture((store, _, root) =>
            {
                var priorCount = PrepareActivityWorkflow(store, kind, "start");
                using var caller = new CancellationTokenSource();
                var reachedLog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var log = new EventLogStore(root, new EventLogWriteObserver { BeforeSessionGuard = () => reachedLog.TrySetResult() },
                    requireExistingSession: true);
                var client = new ActivityWorkflowClient("start");
                var runner = new TurnRunnerService(client, store, log);
                using var narrator = new NarratorService(client, store, log);
                using var held = CrossProcessWriteLease.AcquireAsync(log.EventPath(), TimeSpan.FromSeconds(5), CancellationToken.None)
                    .GetAwaiter().GetResult();
                var operation = RunActivityWorkflowAsync(kind, store, runner, narrator,
                    new ContextRecoveryService(store, client, eventLogStore: log), caller.Token);
                try
                {
                    Require(ReferenceEquals(Task.WhenAny(reachedLog.Task, operation, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(), reachedLog.Task),
                        $"{kind}: the fixture did not reach the admitted diagnostic write.");
                }
                finally { caller.Cancel(); }
                Require(ReferenceEquals(Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(), operation),
                    $"{kind}: caller cancellation did not release the waiting workflow and session guard.");
                var canceled = false;
                try { operation.GetAwaiter().GetResult(); }
                catch (OperationCanceledException exception) { canceled = exception.CancellationToken == caller.Token; }
                var saved = Load(store);
                Require(canceled && client.Calls == 0 && saved.Engine.Messages.Count == priorCount
                        && saved.Engine.Agents.All(agent => agent.Status != "thinking") && saved.Engine.Narrator.Status != "thinking",
                    $"{kind}: diagnostic containment must not consume caller cancellation, call a model, or strand busy state.");
                if (kind == "continue") Require(saved.Engine.Messages.Single().Metadata["output_continuation_state"].GetString() != "in_progress",
                    "Cancelled continuation must release its durable attempt marker.");
            });
        }
    }

    public static void ActivityLogFailureCannotConcealAuthoritativeSaveFailure()
    {
        foreach (var kind in ActivityWorkflowKinds)
        {
            WithFixture((store, _, root) =>
            {
                PrepareActivityWorkflow(store, kind, "start");
                var log = new EventLogStore(root, new EventLogWriteObserver { BeforeWrite = _ => throw new IOException("private diagnostic failure") });
                var client = new ActivityWorkflowClient("start")
                {
                    BeforeReply = () =>
                    {
                        var snapshotPath = Path.GetFullPath(store.SnapshotPath("default"));
                        Require(snapshotPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                            "The save-failure fixture must stay within its owned root.");
                        File.Delete(snapshotPath);
                        Directory.CreateDirectory(snapshotPath);
                    }
                };
                var runner = new TurnRunnerService(client, store, log);
                using var narrator = new NarratorService(client, store, log);
                ActivityWorkflowOutcome? outcome = null;
                Exception? failure = null;
                try { outcome = RunActivityWorkflowAsync(kind, store, runner, narrator,
                    new ContextRecoveryService(store, client, eventLogStore: log)).GetAwaiter().GetResult(); }
                catch (Exception exception) { failure = exception; }
                Require(client.Calls == 1 && (failure is not null || outcome is { Ok: false, Committed: false })
                        && outcome is not { Committed: true },
                    $"{kind}: a diagnostic warning must not turn an authoritative snapshot failure into a saved result or replay a model call.");
            });
        }
    }

    public static void ActivityLogLateAtomicCompletionIsObservedWithoutReplay()
    {
        WithFixture((store, _, root) =>
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writes = 0;
            var log = new EventLogStore(root, new EventLogWriteObserver
            {
                BeforeWrite = _ => { writes++; entered.Set(); Require(release.Wait(TimeSpan.FromSeconds(5)), "Held atomic write was not released."); },
                Completion = (_, _) => completed.TrySetResult()
            }, requireExistingSession: true);
            var client = new ActivityWorkflowClient("start");
            var runner = new TurnRunnerService(client, store, log);
            Task<OneTurnResult>? operation = null;
            try
            {
                operation = runner.RunOneTurnAsync();
                Require(entered.Wait(TimeSpan.FromSeconds(2)), "The atomic diagnostic write did not start.");
                Require(ReferenceEquals(Task.WhenAny(client.Started.Task, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult(), client.Started.Task),
                    "A diagnostic write already inside its atomic mutation must not keep the model call waiting.");
            }
            finally { release.Set(); }
            var result = operation!.GetAwaiter().GetResult();
            Require(completed.Task.Wait(TimeSpan.FromSeconds(2)) && result.Executed && result.Completion?.Ok == true
                    && result.EvidenceWarning == PostCommitEvidence.ActivityLogWarning && client.Calls == 1 && writes == 1
                    && Load(store).Engine.Messages.Count == 1 && File.ReadAllLines(log.EventPath()).Length == 1,
                "Late diagnostic completion must stay atomic and observed without replaying the write or the saved model response.");
        });
    }

    public static void ActivityLogHealthyWorkflowsRetainOrderedEvidence()
    {
        foreach (var kind in ActivityWorkflowKinds)
        {
            WithFixture((store, log, _) =>
            {
                PrepareActivityWorkflow(store, kind, "start");
                var client = new ActivityWorkflowClient("start");
                var runner = new TurnRunnerService(client, store, log);
                using var narrator = new NarratorService(client, store, log);
                var result = RunActivityWorkflowAsync(kind, store, runner, narrator,
                    new ContextRecoveryService(store, client, eventLogStore: log)).GetAwaiter().GetResult();
                var records = File.ReadAllLines(log.EventPath()).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    Require(result.Ok && result.Committed && result.Warning.Length == 0 && client.Calls == 1 && records.Length == 2
                            && records[0].RootElement.GetProperty("type").GetString()!.EndsWith("started", StringComparison.Ordinal)
                            && !records[1].RootElement.GetProperty("type").GetString()!.EndsWith("started", StringComparison.Ordinal),
                        $"{kind}: ordinary start and completion evidence must remain ordered and durable without a warning.");
                }
                finally { foreach (var record in records) record.Dispose(); }
            });
        }
    }

    private static int PrepareActivityWorkflow(SessionStore store, string kind, string phase)
    {
        var snapshot = Load(store);
        snapshot.Engine.Steering.Topic = "Discuss a local fictional scenario.";
        snapshot.Engine.Internet.UseInternet = phase is "internet" or "proactive";
        if (phase == "fallback") snapshot.Configs[kind is "narrator" or "decision-card" ? "narrator" : "alpha"] = Config("primary-model");
        if (kind is "retry" or "continue" || phase == "proactive")
        {
            var original = new DialogueMessage
            {
                MessageId = "original", Turn = 1, SpeakerId = phase == "proactive" ? "operator" : "alpha",
                Speaker = phase == "proactive" ? "Operator" : "Alpha", Kind = "message", Status = "ok", CreatedAt = 100,
                Text = phase == "proactive" ? "Search the web for current solar research." : Answer
            };
            if (kind == "continue")
            {
                original.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
                CompletionRouteReceipt.Stamp(original, CompletionRouteReceipt.Create(snapshot.Configs["shared"], CompletionRouteReceipt.PrimaryPhase));
            }
            snapshot.Engine.Messages.Add(original);
            snapshot.Engine.TurnCount = 1;
        }
        store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        return snapshot.Engine.Messages.Count;
    }

    private static async Task<ActivityWorkflowOutcome> RunActivityWorkflowAsync(string kind, SessionStore store,
        TurnRunnerService runner, NarratorService narrator, ContextRecoveryService recovery, CancellationToken cancellationToken = default)
    {
        if (kind == "decision-card")
        {
            var result = await narrator.GenerateDecisionCardAsync("default", cancellationToken);
            return new(result.Ok, result.Committed, result.EvidenceWarning);
        }
        if (kind is "narrator" or "ask-narrator")
        {
            var result = kind == "narrator" ? await narrator.NarrateNowAsync("default", cancellationToken)
                : await narrator.AskNarratorAsync("default", "Explain the decision.", cancellationToken);
            return new(result.Ok, result.Message is not null, result.EvidenceWarning);
        }
        var original = kind is "retry" or "continue" ? Load(store).Engine.Messages.Single() : null;
        if (kind == "continue")
        {
            var result = await recovery.ContinueOutputAsync("default", original!.Turn, original.SpeakerId, original.CreatedAt, cancellationToken);
            return new(result.Ok, result.Message is not null, result.EvidenceWarning);
        }
        var turn = kind == "retry" ? await runner.RetryTurnAsync("default", original!.Turn, original.SpeakerId, original.CreatedAt, cancellationToken)
            : await runner.RunOneTurnAsync("default", cancellationToken);
        return new(turn.Ok && turn.Completion?.Ok == true, turn.Executed, turn.EvidenceWarning);
    }

    private sealed record ActivityWorkflowOutcome(bool Ok, bool Committed, string Warning);

    private sealed class ActivityWorkflowClient(string phase) : IModelProviderClient
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal Action? BeforeReply;
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Started.TrySetResult();
            BeforeReply?.Invoke();
            if (Calls == 1 && phase == "fallback") return Task.FromResult(Success(config, "") with
            {
                Ok = false, FailureKind = ModelCompletionFailureKind.Transport, Error = "Fixture primary unavailable."
            });
            var text = Calls == 1 && phase == "repair" ? "Lookup status: no external factual context."
                : Calls == 1 && phase == "internet" ? "{\"tool\":\"web_search\",\"query\":\"compare public transport\",\"max_results\":2}" : Answer;
            return Task.FromResult(Success(config, text));
        }
    }

    private sealed class ActivityInternetProvider : IInternetToolProvider
    {
        public Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, InternetSettings settings,
            CancellationToken cancellationToken = default) => Task.FromResult(new InternetToolResult
            {
                Ok = true, Tool = request.Tool, Query = request.Query, Summary = "A public source offers a useful comparison.",
                Sources = [new InternetToolSource { Title = "Public source", Url = "https://example.test/comparison", Snippet = "A useful comparison." }]
            });
    }
}
