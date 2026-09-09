using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static partial class ArenaTurnStreamingTests
{
    private const string Answer = "The final public answer explains the decision and the evidence supporting it.";

    public static void TurnsAndRetryReportOnlyCommittedFinalMessages()
    {
        WithFixture((store, log, _) =>
        {
            var client = new StreamingClient((config, progress, _) =>
            {
                progress?.Report("The final public answer ");
                progress?.Report("explains the decision and the evidence supporting it.");
                return Task.FromResult(Success(config, Answer));
            });
            var runner = new TurnRunnerService(client, store, log);
            var firstEvents = new Recorder(store);
            var first = runner.RunOneTurnAsync("default", progress: firstEvents).GetAwaiter().GetResult();
            Require(first.Ok && first.Message is not null, $"one-turn stream failed: {first.Error}");
            VerifyCommitted(firstEvents, first.Message!, expectedDeltas: 2);
            var secondEvents = new Recorder(store);
            var second = runner.RunAgentTurnAsync("default", "beta", progress: secondEvents).GetAwaiter().GetResult();
            Require(second.Ok && second.Message is not null, $"explicit-agent stream failed: {second.Error}");
            VerifyCommitted(secondEvents, second.Message!, expectedDeltas: 2);
            Require(firstEvents.Events[0].OperationId != secondEvents.Events[0].OperationId,
                "separate user operations must not share a live-card identity");
            var retryEvents = new Recorder(store);
            var retry = runner.RetryTurnAsync("default", second.Message!.Turn, second.Message.SpeakerId,
                second.Message.CreatedAt, progress: retryEvents).GetAwaiter().GetResult();
            Require(retry.Ok && retry.Message is not null, $"retry stream failed: {retry.Error}");
            VerifyCommitted(retryEvents, retry.Message!, expectedDeltas: 2);
            Require(retry.Message!.Turn == second.Message.Turn
                && DialogueMessageIdentity.Resolve(retry.Message) == DialogueMessageIdentity.Resolve(second.Message),
                "streaming retry must retain the original causal message identity");
            Require(Load(store).Engine.Messages.Count == 2,
                "retry must replace its original card without persisting another streaming card");
            Require(client.StreamingCalls == 3 && client.BufferedCalls == 0,
                "all three observed Arena entrypoints must use the existing streaming client contract");
        });
    }

    public static void FallbackAndRepairSeparateAttemptsAndDiscardLateDeltas()
    {
        WithFixture((store, log, _) =>
        {
            var snapshot = Load(store);
            snapshot.Configs["alpha"] = ArenaRequestBudget.Copy(Config("primary-model"), reasoning: "high");
            snapshot.Configs["shared"] = ArenaRequestBudget.Copy(Config("fallback-model"), reasoning: "high");
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var oldProgress = new List<IProgress<string>>();
            var models = new List<string>();
            var call = 0;
            var client = new StreamingClient((config, progress, _) =>
            {
                models.Add(config.Model);
                oldProgress.Add(progress!);
                switch (++call)
                {
                    case 1:
                        progress!.Report("Discarded primary partial.");
                        return Task.FromResult(Success(config, "") with
                        {
                            Ok = false, Error = "Primary unavailable.", FailureKind = ModelCompletionFailureKind.Transport
                        });
                    case 2:
                        return Task.FromResult(Success(config, "") with { Reasoning = "Internal reasoning only." });
                    case 3:
                        oldProgress[0].Report("LATE_FIRST_ATTEMPT");
                        oldProgress[1].Report("LATE_SECOND_ATTEMPT");
                        progress!.Report(Answer);
                        return Task.FromResult(Success(config, Answer));
                    default:
                        throw new InvalidOperationException("Unexpected repair request.");
                }
            });
            var events = new Recorder(store);
            var runner = new TurnRunnerService(client, store, log,
                runtimeEvidenceResolver: new FixedRuntimeEvidence(16384, canDisable: true));
            var result = runner.RunAgentTurnAsync("default", "alpha", progress: events).GetAwaiter().GetResult();
            Require(result.Ok && result.Message is not null, $"fallback/repair stream failed: {result.Error}");
            VerifyCommitted(events, result.Message!, expectedDeltas: 2);
            var starts = events.Events.Where(item => item.Kind == ArenaTurnProgressKind.Started).ToArray();
            Require(starts.Length == 3 && starts.Select(item => item.AttemptId).Distinct().Count() == 3,
                "primary, fallback, and same-model reasoning recovery must each replace the displayed attempt");
            Require(models.SequenceEqual(new[] { "primary-model", "fallback-model", "fallback-model" }),
                "reasoning-only recovery must retry the same fallback model rather than returning to the failed primary");
            for (var index = 0; index < starts.Length - 1; index++)
            {
                var previous = starts[index];
                var previousIndex = events.Events.IndexOf(previous);
                var nextIndex = events.Events.IndexOf(starts[index + 1]);
                Require(events.Events.Skip(previousIndex + 1).Take(nextIndex - previousIndex - 1).Any(item =>
                    item.AttemptId == previous.AttemptId
                    && (item.Kind == ArenaTurnProgressKind.Interrupted && item.Status == "superseded"
                        || item.Kind == ArenaTurnProgressKind.Activity && item.Activity == ArenaTurnActivity.RetryingWithReducedReasoning)),
                    "the prior attempt must close through supersession or explicit reduced-reasoning recovery before its replacement starts");
            }
            Require(events.Events.All(item => !item.Text.Contains("LATE_", StringComparison.Ordinal)
                && !item.Text.Contains("Internal reasoning", StringComparison.Ordinal)),
                "late chunks and hidden reasoning must never enter public live text");
            Require(Load(store).Engine.Messages.Single().Text == Answer,
                "only repaired final content belongs in the persisted transcript");
            var count = events.Events.Count;
            oldProgress.Last().Report("LATE_AFTER_COMMIT");
            Require(events.Events.Count == count, "callbacks arriving after commit must be ignored");
        });
    }

    public static void CancellationErrorsAndObserverFailuresPreservePersistence()
    {
        foreach (var narrator in new[] { false, true })
        {
            foreach (var cancel in new[] { false, true })
            {
                WithFixture((store, log, _) =>
                {
                    using var source = new CancellationTokenSource();
                    var client = new StreamingClient((config, progress, token) =>
                    {
                        progress!.Report("Visible but uncommitted partial answer.");
                        if (cancel)
                        {
                            source.Cancel();
                            return Task.FromCanceled<ModelCompletionResult>(token);
                        }
                        return Task.FromException<ModelCompletionResult>(new IOException("Synthetic stream disconnected."));
                    });
                    var events = new Recorder(store);
                    Exception? failure = null;
                    try
                    {
                        if (narrator)
                        {
                            using var service = new NarratorService(client, store, log);
                            service.NarrateNowAsync("default", source.Token, events).GetAwaiter().GetResult();
                        }
                        else
                        {
                            var service = new TurnRunnerService(client, store, log);
                            service.RunAgentTurnAsync("default", "alpha", source.Token, events).GetAwaiter().GetResult();
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    Require(cancel ? failure is OperationCanceledException : failure is IOException,
                        "stream interruption must preserve the original provider exception contract");
                    Require(events.Events.Count(item => item.Kind == ArenaTurnProgressKind.Delta) == 1
                        && events.Events.Count(item => item.Kind == ArenaTurnProgressKind.Interrupted) == 1
                        && events.Events.Last().Status == (cancel ? "canceled" : "failed")
                        && events.Events.All(item => item.Kind != ArenaTurnProgressKind.Completed),
                        "an interrupted provider must terminate its live attempt without claiming a commit");
                    var saved = Load(store);
                    Require(saved.Engine.Messages.Count == 0, "interrupted partial text must not be saved as public history");
                    Require(narrator ? saved.Engine.Narrator.Status != "thinking"
                        : saved.Engine.Agents.Single(agent => agent.Id == "alpha").Status != "thinking",
                        "streaming interruption must restore existing thinking-state recovery");
                });
            }
        }
        WithFixture((store, log, _) =>
        {
            var client = new StreamingClient((config, progress, _) =>
            {
                progress!.Report(Answer);
                return Task.FromResult(Success(config, Answer));
            });
            var runner = new TurnRunnerService(client, store, log);
            var observer = new ThrowingObserver();
            var result = runner.RunOneTurnAsync("default", progress: observer).GetAwaiter().GetResult();
            Require(result.Ok && Load(store).Engine.Messages.Single().Text == Answer && observer.Calls >= 3,
                "presentation observer exceptions must never fail a model call or its final commit");
        });
        WithFixture((store, log, _) =>
        {
            var client = new StreamingClient((config, progress, _) =>
            {
                progress!.Report("Provider partial is visible but not authoritative.");
                return Task.FromResult(Success(config, "Provider partial is visible but not authoritative.") with
                {
                    Ok = false, Error = "Stream ended unexpectedly.", FailureKind = ModelCompletionFailureKind.Transport
                });
            });
            var events = new Recorder(store);
            var runner = new TurnRunnerService(client, store, log);
            var result = runner.RunOneTurnAsync("default", progress: events).GetAwaiter().GetResult();
            Require(result.Ok && result.Executed && result.Completion is { Ok: false } && result.Message is not null,
                "a completed turn with a provider failure must preserve its existing error card and failed completion result");
            VerifyCommitted(events, result.Message!, expectedDeltas: 1);
            Require(events.Events.Last().Status == "error" && !result.Message!.Text.Contains("not authoritative", StringComparison.Ordinal),
                "failed streamed partials must remain transient, while Completed identifies the actual saved error card");
        });
    }

    public static void NarrationAndContinuationReportCommittedIdentity()
    {
        WithFixture((store, log, _) =>
        {
            var client = new StreamingClient((config, progress, _) =>
            {
                progress!.Report(Answer);
                return Task.FromResult(Success(config, Answer));
            });
            using var narrator = new NarratorService(client, store, log);
            var notes = new Recorder(store);
            var result = narrator.NarrateNowAsync("default", progress: notes).GetAwaiter().GetResult();
            Require(result.Ok && result.Message is not null, $"narration stream failed: {result.Error}");
            VerifyCommitted(notes, result.Message!, expectedDeltas: 1);
            var askEvents = new Recorder(store);
            var asked = narrator.AskNarratorAsync("default", "Explain the key uncertainty.", progress: askEvents).GetAwaiter().GetResult();
            Require(asked.Ok && asked.Message is not null, $"asked narration stream failed: {asked.Error}");
            VerifyCommitted(askEvents, asked.Message!, expectedDeltas: 1);
            Require(notes.Events.All(item => item.SpeakerId == "narrator")
                && askEvents.Events.All(item => item.SpeakerId == "narrator"),
                "narrator progress must keep the narrator card identity across both entrypoints");
        });
        WithFixture((store, log, _) =>
        {
            const string initial = "The first part of this public answer explains the available evidence.";
            const string tail = " The continuation resolves the remaining uncertainty.";
            var call = 0;
            var client = new StreamingClient((config, progress, _) =>
            {
                var text = ++call == 1 ? initial : tail;
                progress?.Report(text);
                return Task.FromResult(Success(config, text) with
                {
                    StopReason = call == 1 ? ModelCompletionStopReason.OutputLimitReached : ModelCompletionStopReason.Completed
                });
            });
            var runner = new TurnRunnerService(client, store, log);
            var first = runner.RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(first.Ok && first.Message is not null, $"initial bounded response failed: {first.Error}");
            var events = new Recorder(store);
            var recovery = new ContextRecoveryService(store, client, eventLogStore: log);
            var continued = recovery.ContinueOutputAsync("default", first.Message!.Turn, first.Message.SpeakerId,
                first.Message.CreatedAt, progress: events).GetAwaiter().GetResult();
            Require(continued.Ok && continued.Message is not null, $"continuation stream failed: {continued.Error}");
            VerifyCommitted(events, continued.Message!, expectedDeltas: 1);
            Require(events.Events.Single(item => item.Kind == ArenaTurnProgressKind.Delta).Text == tail,
                "continuation live text must contain only the newly generated tail");
            Require(continued.Message!.Text.Contains(initial, StringComparison.Ordinal)
                && continued.Message.Text.Contains(tail.Trim(), StringComparison.Ordinal)
                && DialogueMessageIdentity.Resolve(continued.Message) == DialogueMessageIdentity.Resolve(first.Message)
                && Load(store).Engine.Messages.Count == 1,
                "the committed continuation must atomically replace the original with its combined answer");
        });
    }

    public static void ToolRequestsStayPrivateAndBufferedClientsRemainCompatible()
    {
        const string secret = "sk-streaming-secret-1234567890";
        foreach (var narrator in new[] { false, true })
        {
            WithFixture((store, log, _) =>
            {
                var snapshot = Load(store);
                snapshot.Engine.Internet.UseInternet = true;
                store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
                var call = 0;
                var client = new StreamingClient((config, progress, _) =>
                {
                    var text = ++call == 1
                        ? $$"""{"tool":"fetch_url","url":"https://example.test/report?api_key={{secret}}"}"""
                        : Answer;
                    progress!.Report(text);
                    return Task.FromResult(Success(config, text));
                });
                var provider = new RejectUnexpectedInternetProvider();
                using var internet = new InternetToolService(provider, log);
                var events = new Recorder(store);
                DialogueMessage? message;
                if (narrator)
                {
                    using var service = new NarratorService(client, store, log, internetToolService: internet);
                    var result = service.NarrateNowAsync("default", progress: events).GetAwaiter().GetResult();
                    Require(result.Ok, $"private narrator tool continuation failed: {result.Error}");
                    message = result.Message;
                }
                else
                {
                    var service = new TurnRunnerService(client, store, log, internetToolService: internet);
                    var result = service.RunOneTurnAsync("default", progress: events).GetAwaiter().GetResult();
                    Require(result.Ok, $"private agent tool continuation failed: {result.Error}");
                    message = result.Message;
                }
                Require(message is not null, "private tool continuation did not produce a public message");
                VerifyCommitted(events, message!, expectedDeltas: 1);
                Require(call == 2 && provider.Calls == 0 && events.Events.All(item => !item.Text.Contains(secret, StringComparison.Ordinal)
                    && !item.Text.Contains("fetch_url", StringComparison.Ordinal)),
                    "internal credential-bearing tool payloads must be withheld from every public progress event");
            });
        }
        WithFixture((store, log, _) =>
        {
            var events = new Recorder(store);
            var runner = new TurnRunnerService(new BufferedClient(), store, log);
            var result = runner.RunOneTurnAsync("default", progress: events).GetAwaiter().GetResult();
            Require(result.Ok && result.Message is not null, $"buffered-client compatibility failed: {result.Error}");
            VerifyCommitted(events, result.Message!, expectedDeltas: 1);
            Require(events.Events.Single(item => item.Kind == ArenaTurnProgressKind.Delta).Text == Answer,
                "a buffered-only client must publish its returned full text without requiring a streaming implementation");
        });
        WithFixture((store, log, _) =>
        {
            var client = new StreamingClient((config, _, _) => Task.FromResult(Success(config, Answer)));
            var runner = new TurnRunnerService(client, store, log);
            var result = runner.RunOneTurnAsync("default").GetAwaiter().GetResult();
            Require(result.Ok && client.BufferedCalls == 1 && client.StreamingCalls == 0,
                "callers that do not request live progress must keep the existing buffered provider path");
        });
    }

    private static void VerifyCommitted(Recorder recorder, DialogueMessage message, int expectedDeltas)
    {
        Require(recorder.Failures.Count == 0, string.Join(Environment.NewLine, recorder.Failures));
        Require(recorder.Events.Count(item => item.Kind == ArenaTurnProgressKind.Delta) == expectedDeltas,
            $"expected {expectedDeltas} public deltas, received {recorder.Events.Count(item => item.Kind == ArenaTurnProgressKind.Delta)}");
        Require(recorder.Events.Count(item => item.Kind == ArenaTurnProgressKind.Completed) == 1
            && recorder.Events.Last().Kind == ArenaTurnProgressKind.Completed,
            "exactly one terminal completion must follow the committed response");
        var final = recorder.Events.Last();
        Require(final.Text == message.Text && final.MessageId == DialogueMessageIdentity.Resolve(message)
            && final.CreatedAt == message.CreatedAt && final.Turn == message.Turn && final.Status == message.Status,
            "completed progress must expose the exact saved message identity, content, and status");
        Require(recorder.Events.Select(item => item.OperationId).Distinct().Count() == 1
            && recorder.Events.All(item => item.SessionId == "default" && item.SessionInstanceId == recorder.InstanceId),
            "all progress must retain the invocation and original session-instance identity");
    }

    private sealed class Recorder : IProgress<ArenaTurnProgress>
    {
        private readonly SessionStore store;
        private readonly string[] originalMessages;
        public string InstanceId { get; }
        public List<ArenaTurnProgress> Events { get; } = [];
        public List<string> Failures { get; } = [];
        public Recorder(SessionStore store)
        {
            this.store = store;
            var snapshot = Load(store);
            InstanceId = snapshot.SessionInstanceId;
            originalMessages = snapshot.Engine.Messages.Select(item => item.Text).ToArray();
        }
        public void Report(ArenaTurnProgress value)
        {
            Events.Add(value);
            try
            {
                var saved = Load(store);
                if (value.Kind is ArenaTurnProgressKind.Started or ArenaTurnProgressKind.Delta)
                {
                    if (!saved.Engine.Messages.Select(item => item.Text).SequenceEqual(originalMessages))
                        Failures.Add("Provider progress observed partial or replacement text persisted before completion.");
                }
                if (value.Kind == ArenaTurnProgressKind.Completed
                    && !saved.Engine.Messages.Any(item => DialogueMessageIdentity.Resolve(item) == value.MessageId
                        && item.CreatedAt == value.CreatedAt && item.Turn == value.Turn && item.Text == value.Text))
                    Failures.Add("Completed progress arrived before its exact final message was durably saved.");
            }
            catch (Exception ex) { Failures.Add($"Progress persistence inspection failed: {ex.Message}"); }
        }
    }

    private sealed class ThrowingObserver : IProgress<ArenaTurnProgress>
    {
        public int Calls { get; private set; }
        public void Report(ArenaTurnProgress value)
        {
            Calls++;
            throw new InvalidOperationException("Synthetic presentation failure.");
        }
    }

    private sealed class StreamingClient(
        Func<ModelProviderConfig, IProgress<string>?, CancellationToken, Task<ModelCompletionResult>> complete)
        : IModelProviderClient, IStreamingModelProviderClient
    {
        public int BufferedCalls { get; private set; }
        public int StreamingCalls { get; private set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            BufferedCalls++;
            return complete(config, null, cancellationToken);
        }
        public Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            return complete(config, progress, cancellationToken);
        }
    }

    private sealed class BufferedClient : IModelProviderClient
    {
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default) => Task.FromResult(Success(config, Answer));
    }

    private sealed class RejectUnexpectedInternetProvider : IInternetToolProvider
    {
        public int Calls { get; private set; }
        public Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, InternetSettings settings,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("No external internet call is permitted in this isolated fixture.");
        }
    }

    private static ModelProviderConfig Config(string model) => new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1", Model = model, Timeout = 30,
        MaxOutputTokens = 256, ContextLength = 16384, ConfiguredContextWindow = 16384, Reasoning = "off"
    };
    private static ModelCompletionResult Success(ModelProviderConfig config, string text) =>
        new(true, config.BaseUrl, config.Model, text, "", 123, 100, 20, 120, "", DateTimeOffset.UtcNow,
            StopReason: ModelCompletionStopReason.Completed);
    private static ArenaSnapshot Load(SessionStore store) => store.LoadSnapshotAsync().GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("Fixture snapshot is missing.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void WithFixture(Action<SessionStore, EventLogStore, string> action)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-streaming-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "fixture cleanup must remain inside its dedicated temporary directory");
        try
        {
            var store = new SessionStore(root);
            var log = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.FactoryMode = false;
            snapshot.Engine.Internet.UseInternet = false;
            snapshot.Engine.Messages.Clear();
            snapshot.Engine.TurnCount = 0;
            snapshot.Engine.TurnIndex = 0;
            snapshot.Configs.Clear();
            snapshot.Configs["shared"] = Config("shared-model");
            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.Active = agent.Id is "alpha" or "beta";
                agent.Status = "waiting";
            }
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            action(store, log, root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
