using System.Reflection;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

internal static partial class Program
{
    static void ArenaLifecycleSubscribersFailIndependently()
    {
        var hub = new AIArenaControlPlaneEventHub();
        var fail = true;
        var faultyCalls = 0;
        var disposedCalls = 0;
        var delivered = new List<string>();
        using var faulty = hub.Subscribe(_ => { faultyCalls++; if (fail) throw new IOException("private subscriber failure"); });
        using var disposed = hub.Subscribe(_ => { disposedCalls++; throw new ObjectDisposedException("private subscriber"); });
        using var healthy = hub.Subscribe(value => delivered.Add(value.Type));
        hub.Publish("arena.run.started", "Starting");
        fail = false;
        hub.Publish("arena.run.stopped", "Stopping");
        Require(delivered.SequenceEqual(new[] { "arena.run.started", "arena.run.stopped" })
                && faultyCalls == 2 && disposedCalls == 1,
            "A failed observer must not suppress healthy observers; disposed observers retire and temporary failures can recover.");
    }

    static void ArenaLifecycleStartupPresentationFailureReleasesRun() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        foreach (var failingStatus in new[] { "none", "arena", "load", "both" })
        {
            using var fixture = new ArenaLifecycleFixture();
            fixture.FailBusyStart = true;
            fixture.FailingStatus = failingStatus;
            fixture.Running = fixture.Coordinator.StartAutoChatAsync();
            Require(fixture.Running.IsCompleted && !fixture.Coordinator.IsAutoChatRunning && !fixture.Busy
                    && fixture.Coordinator.StopAutoChatAsync().IsCompleted && fixture.Client.Calls == 0,
                $"Startup presentation failure ({failingStatus}) must release run ownership and the shutdown barrier before any provider call.");
            Require(!fixture.Running.IsFaulted && !fixture.Statuses.Any(value => value.Contains("private", StringComparison.Ordinal)),
                "A startup fault must reach privacy-safe feedback without a second view fault escaping cleanup.");
            fixture.FailBusyStart = false;
            fixture.FailingStatus = "none";
            fixture.Running = fixture.Coordinator.StartAutoChatAsync();
            fixture.WaitForProvider();
            fixture.StopAndDrain();
        }
    }));

    static void ArenaLifecycleFailedStartNotificationPreservesProviderCall() => RunFaultyArenaLifecycle("started");
    static void ArenaLifecycleFailedStopNotificationStillCancels() => RunFaultyArenaLifecycle("stopped");

    private static void RunFaultyArenaLifecycle(string failedTransition) => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        using var fixture = new ArenaLifecycleFixture();
        var delivered = new List<string>();
        fixture.Coordinator.RunLifecycle = transition =>
        {
            if (transition == failedTransition) throw new IOException("private lifecycle notification");
        };
        fixture.Coordinator.RunLifecycle += delivered.Add;
        fixture.Running = fixture.Coordinator.StartAutoChatAsync();
        fixture.WaitForProvider();
        fixture.StopAndDrain();
        Require(delivered.SequenceEqual(new[] { "started", "stopped" }) && fixture.Client.Calls == 1,
            "Lifecycle observers must fail independently without preventing the provider call, cancellation, or normal observers.");
    }));

    static void ArenaLifecycleFailedStoppingStatusStillDrains() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        using var fixture = new ArenaLifecycleFixture();
        fixture.FailStoppingStatus = true;
        fixture.Running = fixture.Coordinator.StartAutoChatAsync();
        fixture.WaitForProvider();
        fixture.StopAndDrain();
    }));

    static void ArenaLifecycleRepeatedAndReentrantStopsNotifyOnce() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        using var fixture = new ArenaLifecycleFixture();
        var stops = 0;
        fixture.Coordinator.RunLifecycle = transition =>
        {
            if (transition == "stopped" && ++stops == 1) fixture.Coordinator.StopAutoChat();
        };
        fixture.Running = fixture.Coordinator.StartAutoChatAsync();
        fixture.WaitForProvider();
        var stopping = fixture.Coordinator.StopAutoChatAsync();
        fixture.WaitForCancellation(stopping);
        var repeated = fixture.Coordinator.StopAutoChatAsync();
        Require(stops == 1 && !stopping.IsCompleted && !repeated.IsCompleted,
            "Repeated or reentrant stops must join one shutdown barrier and announce one actual cancellation transition.");
        fixture.Client.FinishCancellation.TrySetResult();
        WaitArenaLifecycleTask(stopping);
        WaitArenaLifecycleTask(repeated);
        WaitArenaLifecycleTask(fixture.Running);
        fixture.RequireStopped();
    }));

    static void ArenaLifecycleStopDuringStartupReportsStopped() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        using var fixture = new ArenaLifecycleFixture();
        fixture.Coordinator.RunLifecycle = transition =>
        {
            if (transition == "started") fixture.Coordinator.StopAutoChat();
        };
        fixture.Running = fixture.Coordinator.StartAutoChatAsync();
        WaitArenaLifecycleTask(fixture.Running);
        Require(fixture.Client.Calls == 0 && !fixture.Busy && !fixture.Coordinator.IsAutoChatRunning
                && fixture.BusyStatuses.Last() == "Auto Chat stopped.",
            "Stopping inside the start notification must not call a provider or finish with a misleading running status.");
    }));

    static void ArenaLifecycleCommittedRequestsSurviveObserverFailures() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        var wrong = new List<string>();
        foreach (var route in new[] { "turn", "narration" })
        foreach (var failure in new[] { "none", "lifecycle", "arena", "load", "both" })
        {
            using var fixture = new ArenaLifecycleFixture();
            fixture.Client.Hold = false;
            fixture.FailingStatus = failure;
            var healthyNotifications = new List<string>();
            fixture.Coordinator.RunLifecycle = _ =>
            {
                if (failure == "lifecycle") throw new IOException("private completion notification");
            };
            fixture.Coordinator.RunLifecycle += healthyNotifications.Add;
            var request = route == "turn" ? fixture.Coordinator.RunOneTurnAsync() : fixture.Coordinator.NarrateNowAsync();
            fixture.Running = request;
            Require(ReferenceEquals(PumpDocumentImportTask(Task.WhenAny(request, Task.Delay(TimeSpan.FromSeconds(5)))), request),
                "The saved request did not finish within its bounded fixture wait.");
            _ = request.Exception;
            var saved = PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!;
            Require(saved.Engine.Messages.Count == 1 && saved.Engine.Messages.Single().Status == "ok"
                    && fixture.Client.Calls == 1 && !fixture.Busy && fixture.Gate.CurrentCount == 1,
                $"{route}/{failure}: the real request must save exactly once and release its busy state.");
            Console.WriteLine($"arena lifecycle saved receipt: route={route}; fault={failure}; provider_calls={fixture.Client.Calls}; "
                + $"committed_messages={saved.Engine.Messages.Count}; request_success={request.IsCompletedSuccessfully}; "
                + $"load_status_calls={fixture.LoadStatusCalls}; arena_status_calls={fixture.ArenaStatusCalls}; notifications={healthyNotifications.Count}");
            if (!request.IsCompletedSuccessfully || !request.Result
                || !(route == "turn" ? fixture.Coordinator.LastTurnSucceeded : fixture.Coordinator.LastNarrationSucceeded)
                || !healthyNotifications.SequenceEqual(new[] { route })
                || fixture.LoadStatusCalls != 1 || fixture.ArenaStatusCalls < 1)
                wrong.Add($"{route}/{failure}");
        }
        Require(wrong.Count == 0, "Saved requests lost successful completion, independent feedback, or concise narration status: " + string.Join(", ", wrong));
    }));

    static void ArenaLifecycleCleanupFailureReleasesShutdownBarrier() => RunStaTest(() => RunWithDispatcherContext(() =>
    {
        using var fixture = new ArenaLifecycleFixture();
        fixture.FailBusyFinish = true;
        fixture.Running = fixture.Coordinator.StartAutoChatAsync();
        fixture.WaitForProvider();
        var stopping = fixture.Coordinator.StopAutoChatAsync();
        fixture.WaitForCancellation(stopping);
        fixture.Client.FinishCancellation.TrySetResult();
        WaitArenaLifecycleTask(stopping);
        var finished = PumpDocumentImportTask(Task.WhenAny(fixture.Running, Task.Delay(TimeSpan.FromSeconds(5))));
        Require(ReferenceEquals(finished, fixture.Running) && !fixture.Coordinator.IsAutoChatRunning
                && !fixture.Busy && fixture.Coordinator.StopAutoChatAsync().IsCompleted,
            "A failed final busy-state projection must still release shutdown waiters and run ownership.");
        _ = fixture.Running.Exception;
    }));

    private static void WaitArenaLifecycleTask(Task task)
    {
        Require(ReferenceEquals(PumpDocumentImportTask(Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)))), task),
            "Arena lifecycle operation did not finish within its bounded fixture wait.");
        PumpDocumentImportTask(task);
    }

    private sealed class ArenaLifecycleFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ai-arena-run-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly InternetToolService internet;
        private readonly NarratorService narrator;
        internal readonly SessionStore Store;
        internal readonly ArenaLifecycleClient Client = new();
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal readonly ArenaRunCoordinator Coordinator;
        internal readonly List<string> Statuses = [];
        internal readonly List<string> BusyStatuses = [];
        internal Task? Running;
        internal bool Busy, FailBusyStart, FailBusyFinish, FailStoppingStatus;
        internal string FailingStatus = "none";
        internal int LoadStatusCalls, ArenaStatusCalls;

        internal ArenaLifecycleFixture()
        {
            Store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.Internet.UseInternet = false;
            snapshot.Configs["shared"] = new ModelProviderConfig
            {
                Model = "lifecycle-model", BaseUrl = "http://127.0.0.1:45678/v1",
                ApiMode = ModelProviderApiModes.OpenAiCompatible, MaxOutputTokens = 256
            };
            PumpDocumentImportTask(Store.SaveSnapshotAsync(snapshot));
            var events = new EventLogStore(root);
            internet = new InternetToolService(eventLogStore: events);
            var transcript = new TranscriptService();
            var runner = new TurnRunnerService(Client, Store, events, transcript, internet);
            narrator = new NarratorService(Client, Store, events, transcript, internet);
            Coordinator = new ArenaRunCoordinator(runner, narrator, Gate, new Button(), new Button(), new Button(),
                () => new CoreSessionSummary("default", Store.SnapshotPath("default"), true, 0, 0, 0, DateTimeOffset.UtcNow),
                () => Busy, () => false, () => TimeSpan.FromSeconds(30),
                (busy, status, _, _) =>
                {
                    Busy = busy;
                    BusyStatuses.Add(status);
                    if (busy && FailBusyStart || !busy && FailBusyFinish) throw new IOException("private busy projection");
                },
                async (_, _, action, _) =>
                {
                    Busy = true;
                    try { await action(); return true; }
                    finally { Busy = false; }
                }, _ => Task.CompletedTask,
                status =>
                {
                    LoadStatusCalls++;
                    if (FailingStatus is "load" or "both") throw new IOException("private load status");
                    Statuses.Add(status);
                },
                status =>
                {
                    ArenaStatusCalls++;
                    if (FailingStatus is "arena" or "both" || FailStoppingStatus && status == "Stopping Auto Chat...")
                        throw new IOException("private arena status");
                    Statuses.Add(status);
                }, id => id != "operator");
        }

        internal void WaitForProvider()
        {
            var first = PumpDocumentImportTask(Task.WhenAny(Client.Started.Task, Running!, Task.Delay(TimeSpan.FromSeconds(5))));
            Require(ReferenceEquals(first, Client.Started.Task), "The lifecycle callback aborted Auto Chat before its provider call.");
        }

        internal void WaitForCancellation(Task stopping)
        {
            var first = PumpDocumentImportTask(Task.WhenAny(Client.Canceled.Task, stopping, Task.Delay(TimeSpan.FromSeconds(5))));
            Require(ReferenceEquals(first, Client.Canceled.Task) && !stopping.IsCompleted,
                "Stop must request provider cancellation and remain pending until that provider has finished cleanup.");
        }

        internal void StopAndDrain()
        {
            var stopping = Coordinator.StopAutoChatAsync();
            WaitForCancellation(stopping);
            Client.FinishCancellation.TrySetResult();
            WaitArenaLifecycleTask(stopping);
            WaitArenaLifecycleTask(Running!);
            RequireStopped();
        }

        internal void RequireStopped()
        {
            var snapshot = PumpDocumentImportTask(Store.LoadSnapshotAsync())!;
            Require(!Coordinator.IsAutoChatRunning && !Busy && Gate.CurrentCount == 1
                    && snapshot.Engine.Messages.Count == 0
                    && snapshot.Engine.Agents.All(agent => agent.Status != "thinking"),
                "A stopped provider call must leave no completed turn, thinking agent, held operation lock, or busy run.");
        }

        public void Dispose()
        {
            Coordinator.RunLifecycle = null;
            FailBusyStart = FailBusyFinish = FailStoppingStatus = false;
            FailingStatus = "none";
            Client.FinishCancellation.TrySetResult();
            Coordinator.StopAutoChat();
            Client.Reply.TrySetResult();
            if (Running is not null)
            {
                Require(ReferenceEquals(PumpDocumentImportTask(Task.WhenAny(Running, Task.Delay(TimeSpan.FromSeconds(5)))), Running),
                    "Owned lifecycle fixture could not drain its provider call.");
                _ = Running.Exception;
            }
            // The baseline startup bug strands this owned source before the loop's finally block.
            (typeof(ArenaRunCoordinator).GetField("autoChatCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Coordinator) as CancellationTokenSource)?.Dispose();
            narrator.Dispose();
            internet.Dispose();
            Gate.Dispose();
            var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Require(Path.GetDirectoryName(Path.GetFullPath(root))!.Equals(temporaryParent, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(root).StartsWith("ai-arena-run-lifecycle-", StringComparison.Ordinal),
                "Refusing to remove a lifecycle fixture outside its owned temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ArenaLifecycleClient : IModelProviderClient
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FinishCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Hold = true;
        internal int Calls;

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));

        public async Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            Started.TrySetResult();
            if (Hold)
            {
                try { await Reply.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    Canceled.TrySetResult();
                    await FinishCancellation.Task;
                    throw;
                }
            }
            return new ModelCompletionResult(true, config.BaseUrl, config.Model, CommittedOutcomeModelClient.Reply, "",
                1, 1, 1, 2, "", DateTimeOffset.UtcNow, StopReason: ModelCompletionStopReason.Completed);
        }
    }
}
