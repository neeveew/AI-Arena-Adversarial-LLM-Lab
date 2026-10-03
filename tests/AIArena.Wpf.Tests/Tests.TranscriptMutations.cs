using System.Windows.Controls;
using System.Reflection;
using System.Runtime.CompilerServices;
using AIArena.Wpf.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

internal static partial class Program
{
    static void TranscriptMutationReservesArenaAndDrainsShutdown() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            foreach (var shutdown in new[] { false, true })
            {
                using var fixture = await TranscriptMutationFixture.CreateAsync();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.BeforeSave = async cancellationToken =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                };
                var pin = fixture.Mutations.TogglePinMessageAsync(fixture.Message with { Pinned = true });
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var competingRan = false;
                    var competingAccepted = await fixture.Operations.RunAsync("Competing turn", () =>
                    {
                        competingRan = true;
                        return Task.CompletedTask;
                    });
                    await fixture.Mutations.DeleteMessageAsync(fixture.Message);
                    Require(fixture.Busy && !fixture.CardAction.IsEnabled && !competingAccepted && !competingRan
                            && fixture.Saves == 1 && !File.Exists(fixture.Events.EventPath("default")),
                        "A pending transcript save must disable card actions, reserve the arena, and avoid recording an uncommitted mutation");
                    var drain = shutdown ? fixture.Operations.DrainAsync() : Task.CompletedTask;
                    Require(!shutdown || !drain.IsCompleted,
                        "Shutdown must wait for the actual in-flight transcript mutation");
                    release.TrySetResult();
                    await pin.WaitAsync(TimeSpan.FromSeconds(5));
                    await drain.WaitAsync(TimeSpan.FromSeconds(5));
                    var saved = (await fixture.Store.LoadSnapshotAsync())!;
                    Require(!fixture.Busy && fixture.CardAction.IsEnabled && saved.Engine.Messages.Count == 1
                            && saved.Engine.Messages[0].Pinned == !shutdown,
                        "A competing request or shutdown produced the wrong durable pin or left actions busy");
                    if (shutdown)
                        Require(fixture.Refreshes == 0 && !File.Exists(fixture.Events.EventPath("default"))
                                && fixture.Status.Contains("AA-ARENA-CANCELLED", StringComparison.Ordinal),
                            "A canceled uncommitted transcript save must report cancellation without success evidence");
                    else
                        Require(fixture.Refreshes == 1 && fixture.Status == "Pinned turn 1.",
                            "Pin status must describe the actual saved toggle even when the rendered card's pin state was stale");
                }
                finally
                {
                    release.TrySetResult();
                    await pin.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }));

    static void TranscriptMutationFailuresPreserveDurableOutcome() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            foreach (var delete in new[] { false, true })
            foreach (var failure in new[] { "save", "conflict", "log", "refresh", "refresh-cancel", "late-cancel" })
            {
                using var fixture = await TranscriptMutationFixture.CreateAsync();
                fixture.BeforeSave = _ => failure switch
                {
                    "save" => Task.FromException(new IOException("private transcript save detail")),
                    "conflict" => Task.FromException(new SnapshotConcurrencyException("private snapshot path", 1, 2)),
                    _ => Task.CompletedTask
                };
                fixture.AfterSave = () =>
                {
                    if (failure == "log") Directory.CreateDirectory(fixture.Events.EventPath("default"));
                    if (failure == "late-cancel") fixture.Operations.RequestShutdown();
                };
                fixture.Refresh = _ => failure switch
                {
                    "refresh" => Task.FromException(new IOException("private transcript refresh detail")),
                    "refresh-cancel" => Task.FromException(new OperationCanceledException("private canceled refresh detail")),
                    _ => Task.CompletedTask
                };
                if (delete) await fixture.Mutations.DeleteMessageAsync(fixture.Message);
                else await fixture.Mutations.TogglePinMessageAsync(fixture.Message);
                var saved = (await fixture.Store.LoadSnapshotAsync())!;
                var committed = failure is not ("save" or "conflict");
                Require(saved.Engine.Messages.Count == (committed && delete ? 0 : 1)
                        && (delete || saved.Engine.Messages[0].Pinned == committed)
                        && !fixture.Busy && fixture.CardAction.IsEnabled,
                    $"{delete}/{failure}: transcript persistence or terminal controls disagree with the outcome");
                Require(!fixture.Status.Contains("private", StringComparison.OrdinalIgnoreCase),
                    $"{delete}/{failure}: private error detail escaped into the mutation status");
                if (!committed)
                    Require(fixture.Refreshes == 0 && !File.Exists(fixture.Events.EventPath("default"))
                            && !fixture.Status.StartsWith(delete ? "Deleted" : "Pinned", StringComparison.Ordinal),
                        "An unsuccessful save must not append success evidence or refresh a mutation that never happened");
                else
                {
                    Require(fixture.Refreshes == 1 && fixture.Status.StartsWith(delete ? "Deleted turn 1." : "Pinned turn 1.", StringComparison.Ordinal),
                        "A saved transcript mutation must preserve its outcome and refresh despite late errors/cancellation");
                    if (failure != "late-cancel") Require(fixture.Status.Contains("Warning:", StringComparison.Ordinal),
                        "Secondary transcript failures must appear as saved-with-warning outcomes");
                }
            }
        }));

    static void ContextRecoveryPresentationPreservesSavedWarnings() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            var source = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.Models.cs");
            foreach (var methodName in new[]
            {
                "private async Task EndMatchAfterContextLimitAsync",
                "private async Task SkipContextBlockedTurnAsync",
                "private async Task ContinueTruncatedOutputAsync"
            })
                Require(CSharpMethodBlock(source, methodName).Contains("RefreshContextRecoveryOutcomeAsync", StringComparison.Ordinal),
                    $"{methodName}: recovery must use the shared saved-outcome presentation boundary");

            foreach (var outcome in new[]
            {
                "Match ended by the operator after a context-limit stop.",
                "Skipped the blocked turn. Auto Chat remains stopped.",
                "Continued the output-limited response."
            })
            {
                using var fixture = await PostCommitRefreshFixture.CreateAsync(failureAtRead: 1);
                var load = new TextBlock();
                var arena = new TextBlock();
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                typeof(MainWindow).GetField("LoadStatus", fields)!.SetValue(fixture.Host, load);
                var topBar = (ShellTopBarControl)RuntimeHelpers.GetUninitializedObject(typeof(ShellTopBarControl));
                typeof(ShellTopBarControl).GetField("arenaRunStatus", fields)!.SetValue(topBar, arena);
                typeof(MainWindow).GetField("ShellTopBar", fields)!.SetValue(fixture.Host, topBar);
                var apply = typeof(MainWindow).GetMethod("RefreshContextRecoveryOutcomeAsync", fields)!
                    .CreateDelegate<Func<ContextRecoveryResult, string, ArenaTranscriptStreamCoordinator.StreamOperation?, Task>>(fixture.Host);
                var message = CoreMessageForTest(1, "System", "system", "event", "ok", "", 0);
                var result = ContextRecoveryResult.Completed(message) with { EvidenceWarning = PostCommitEvidence.ActivityLogWarning };
                await apply(result, outcome, null);
                Require(load.Text == arena.Text && load.Text.StartsWith(outcome, StringComparison.Ordinal)
                        && load.Text.Contains("activity-log", StringComparison.Ordinal)
                        && load.Text.Contains("current view could not be refreshed", StringComparison.Ordinal)
                        && !load.Text.Contains("private-refresh-marker", StringComparison.Ordinal),
                    "A committed recovery must publish both its saved result and secondary evidence/refresh warnings");
                fixture.RequireLastProjectionRetained();
                fixture.ResetReads();
                Exception? failure = null;
                try { await apply(ContextRecoveryResult.Failed("Provider attempt failed") with { Executed = true }, outcome, null); }
                catch (Exception exception) { failure = exception; }
                Require(failure is InvalidOperationException && fixture.Reads == 0,
                    "An executed but uncommitted continuation must not be presented as a saved recovery or refreshed as successful");
            }
        }));

    private sealed class TranscriptMutationFixture : IDisposable
    {
        private readonly string directory;
        private readonly SemaphoreSlim gate = new(1, 1);
        internal SessionStore Store { get; }
        internal EventLogStore Events { get; }
        internal TranscriptMutationCoordinator Mutations { get; }
        internal ArenaOperationCoordinator Operations { get; }
        internal TranscriptMessage Message { get; }
        internal Button CardAction { get; }
        internal bool Busy { get; private set; }
        internal int Saves { get; private set; }
        internal int Refreshes { get; private set; }
        internal string Status { get; private set; } = "";
        internal Func<CancellationToken, Task> BeforeSave { get; set; } = _ => Task.CompletedTask;
        internal Action? AfterSave { get; set; }
        internal Func<string, Task> Refresh { get; set; } = _ => Task.CompletedTask;

        private TranscriptMutationFixture(string directory, SessionStore store, DialogueMessage message)
        {
            this.directory = directory;
            Store = store;
            Events = new EventLogStore(directory);
            var load = new TextBlock();
            var arena = new TextBlock();
            var actions = new TranscriptActionCoordinator(() => false, () => Busy, AccentResourceBrush);
            CardAction = actions.CreateCardButton("Pin", null, true);
            Operations = new ArenaOperationCoordinator(
                gate, load, arena, new Button(), new Button(), new Button(), new Button(), new Button(), [],
                () => Busy, value => Busy = value, () => false,
                (_, _) => { }, (_, _) => { }, (_, _) => { }, _ => { }, () => { }, _ => { },
                actions.UpdateBusyState, _ => { }, _ => { }, animationsEnabled: () => false);
            var session = new CoreSessionSummary("default", store.SnapshotPath("default"), true, 1, 0, 0, DateTimeOffset.UtcNow);
            Message = TranscriptForTest(message.Turn, message.Speaker, message.SpeakerId, message.Kind, message.Status) with
            {
                CreatedAt = message.CreatedAt, Pinned = message.Pinned, Text = message.Text
            };
            Mutations = new TranscriptMutationCoordinator(store, Events, new TranscriptService(), () => session,
                () => Busy, (status, action) => Operations.RunAsync(status, null, action),
                async (snapshot, id, cancellationToken) =>
                {
                    Saves++;
                    await BeforeSave(cancellationToken);
                    await store.SaveSnapshotAsync(snapshot, id, cancellationToken);
                    AfterSave?.Invoke();
                },
                async status => { Refreshes++; await Refresh(status); },
                status => { Status = status; load.Text = status; }, status => Status = status);
        }

        internal static async Task<TranscriptMutationFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "ai-arena-transcript-mutations", Guid.NewGuid().ToString("N"));
            var store = new SessionStore(directory);
            try
            {
                var snapshot = SessionStore.CreateDefaultSnapshot();
                var message = new DialogueMessage
                {
                    Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", CreatedAt = 123.5,
                    Kind = "message", Status = "ok", Text = "A saved response."
                };
                snapshot.Engine.Messages.Add(message);
                snapshot.Engine.TurnCount = 1;
                await store.SaveSnapshotAsync(snapshot);
                return new TranscriptMutationFixture(directory, store, message);
            }
            catch { DeleteCommittedOutcomeFixture(directory, "ai-arena-transcript-mutations"); throw; }
        }

        public void Dispose()
        {
            gate.Dispose();
            DeleteCommittedOutcomeFixture(directory, "ai-arena-transcript-mutations");
        }
    }
}
