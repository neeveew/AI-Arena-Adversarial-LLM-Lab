using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

internal static partial class Program
{
    static void PostCommitWindowRefreshPreservesSavedOutcomeAndStopsAutoChat() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            foreach (var failureAtRead in new[] { 1, 2 })
            {
                using var fixture = await PostCommitRefreshFixture.CreateAsync(failureAtRead);
                Exception? observed = null;
                try { await fixture.Refresh("Saved turn."); }
                catch (Exception exception) { observed = exception; }
                Require(ReferenceEquals(observed, fixture.Failure),
                    $"Refresh failure at read {failureAtRead} must survive the actual window load and refresh catches unchanged");
                fixture.RequireLastProjectionRetained();

                fixture.ResetReads();
                var presentation = await AppPostCommitEvidence.RefreshAsync(
                    "Saved turn.", committed: true, fixture.Refresh, AppErrorContext.Arena);
                Require(!presentation.Complete && presentation.Status.StartsWith("Saved turn.", StringComparison.Ordinal)
                        && presentation.Status.Contains("Warning:", StringComparison.Ordinal)
                        && presentation.Status.Contains("current view could not be refreshed", StringComparison.Ordinal)
                        && !presentation.Status.Contains("private-refresh-marker", StringComparison.Ordinal),
                    "The real window refresh failure must preserve the saved outcome with a safe secondary warning");
                fixture.RequireLastProjectionRetained();

                fixture.ResetReads();
                var provider = new CommittedOutcomeModelClient();
                var events = new EventLogStore(fixture.Store.DataRoot);
                using var internet = new InternetToolService(eventLogStore: events);
                var runner = new TurnRunnerService(provider, fixture.Store, events, internetToolService: internet);
                using var narrator = new NarratorService(provider, fixture.Store, events, internetToolService: internet);
                using var gate = new SemaphoreSlim(1, 1);
                var busy = false;
                var status = "";
                var coordinator = new ArenaRunCoordinator(
                    runner, narrator, gate, new Button(), new Button(), new Button(),
                    () => fixture.Session, () => busy, () => false, () => TimeSpan.Zero,
                    (value, text, _, _) => { busy = value; status = text; },
                    async (_, _, action, _) => { await action(); return true; },
                    fixture.Refresh, text => status = text, text => status = text,
                    speaker => speaker.Equals("alpha", StringComparison.OrdinalIgnoreCase));
                var running = coordinator.StartAutoChatAsync();
                try { await running.WaitAsync(TimeSpan.FromSeconds(8)); }
                finally
                {
                    coordinator.StopAutoChat();
                    await running.WaitAsync(TimeSpan.FromSeconds(3));
                }
                var saved = (await fixture.Store.LoadSnapshotAsync())!;
                Require(provider.Calls == 1 && saved.Engine.Messages.Count == 1
                        && saved.Engine.Messages[0].Text == CommittedOutcomeModelClient.Reply
                        && saved.Engine.LastError.Length == 0,
                    "A real refresh failure must stop Auto Chat after its one committed provider response without rewriting the result");
                Require(!coordinator.IsAutoChatRunning && !busy
                        && status.StartsWith("Auto Chat stopped.", StringComparison.Ordinal)
                        && status.Contains("spoke", StringComparison.Ordinal)
                        && status.Contains("Warning:", StringComparison.Ordinal)
                        && !status.Contains("private-refresh-marker", StringComparison.Ordinal),
                    "Auto Chat must retain saved-success status and the refresh warning when the production refresh delegate fails");
                fixture.RequireLastProjectionRetained();
            }

            foreach (var failureAtRead in new[] { 1, 2 })
            foreach (var lateFailure in new[] { false, true })
            {
                using var stale = await PostCommitRefreshFixture.CreateAsync(failureAtRead);
                stale.OnRead = () =>
                {
                    using var selection = stale.Loads.BeginSelection("new-selection");
                    Require(selection.TryApply("new-selection", () => { }), "New selection must acquire the session lease");
                    if (lateFailure) throw stale.Failure;
                };
                var stalePresentation = await AppPostCommitEvidence.RefreshAsync(
                    "Saved old turn.", committed: true, stale.Refresh, AppErrorContext.Arena);
                Require(stalePresentation.Complete && !stalePresentation.Status.Contains("Warning:", StringComparison.Ordinal)
                        && stale.Reads == failureAtRead && stale.Loads.SelectedSessionId == "new-selection",
                    $"Superseded refresh read {failureAtRead} must suppress {(lateFailure ? "late non-cancellation failure" : "lease cancellation")} instead of publishing a stale warning");
                stale.RequireLastProjectionRetained();
            }

            foreach (var failureAtRead in new[] { 1, 2 })
            foreach (var lateFailure in new[] { false, true })
            {
                using var callerCanceled = await PostCommitRefreshFixture.CreateAsync(failureAtRead);
                using var caller = new CancellationTokenSource();
                callerCanceled.OnRead = () =>
                {
                    caller.Cancel();
                    if (lateFailure) throw callerCanceled.Failure;
                };
                var refreshMethod = typeof(MainWindow).GetMethod("RefreshActiveSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var cancellableRefresh = refreshMethod.CreateDelegate<Func<string, CancellationToken, bool, bool, Task>>(callerCanceled.Host);
                var callerCancellationObserved = false;
                try { await cancellableRefresh("Saved turn.", caller.Token, true, true); }
                catch (OperationCanceledException exception) when (exception.CancellationToken == caller.Token)
                {
                    callerCancellationObserved = true;
                }
                Require(callerCancellationObserved,
                    $"Suppressing stale read {failureAtRead} failure must preserve explicit caller cancellation (late failure: {lateFailure})");
                callerCanceled.RequireLastProjectionRetained();
            }
        }));

    private sealed class PostCommitRefreshFixture : IDisposable
    {
        private readonly string directory;
        private readonly MainWindow window;
        private readonly ArenaViewSnapshot rendered;
        private readonly SnapshotStamp previousStamp = new(10, DateTimeOffset.UnixEpoch, 1, 1, "prior", 1);
        internal MainWindow Host => window;
        internal SessionStore Store { get; }
        internal CoreSessionSummary Session { get; }
        internal SessionLoadCoordinator Loads { get; } = new();
        internal Func<string, Task> Refresh { get; }
        internal Exception Failure { get; } = new InvalidOperationException("private-refresh-marker: credential and path details");
        internal Action? OnRead { get; set; }
        internal int Reads { get; private set; }

        private PostCommitRefreshFixture(string directory, SessionStore store, ArenaSnapshot snapshot, int failureAtRead)
        {
            this.directory = directory;
            Store = store;
            Session = new CoreSessionSummary("default", store.SnapshotPath("default"), true, 0, 0, 0, DateTimeOffset.UtcNow);
            rendered = SnapshotViewMapper.FromCore(Session, snapshot);
            using (var selection = Loads.BeginSelection(Session.Id)) selection.TryApply(Session.Id, () => { });

            // Exercise the actual private window methods without starting its constructor,
            // user-data stores, model discovery, timers or native services. Every field read
            // before the deliberately injected I/O-stage failure is supplied explicitly.
            window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            SetField("_coreSessionStore", store);
            SetField("_activeSession", Session);
            SetField("_statusCenterSessionId", Session.Id);
            SetField("_sessionLoads", Loads);
            SetField("_lastRenderedSnapshot", rendered);
            SetField("_lastRenderedMessages", rendered.Messages);
            SetField("_activeSnapshotStamp", previousStamp);
            SetField("_snapshotStamps", new SnapshotStampReader(forceContentHashGeneration: true, hashChunkObserved: chunk =>
            {
                if (chunk != 1 || ++Reads != failureAtRead) return;
                if (OnRead is not null) OnRead();
                else throw Failure;
            }));
            var method = typeof(MainWindow).GetMethod("RefreshActiveSessionAfterTurnAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Production committed-turn refresh entry point is missing");
            Refresh = method.CreateDelegate<Func<string, Task>>(window);
        }

        internal static async Task<PostCommitRefreshFixture> CreateAsync(int failureAtRead)
        {
            var directory = Path.Combine(Path.GetTempPath(), "aiarena-postcommit-refresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new SessionStore(directory);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Engine.Internet.UseInternet = false;
                snapshot.Configs["shared"] = new ModelProviderConfig
                {
                    Model = "refresh-fixture-model", BaseUrl = "http://127.0.0.1:45678/v1",
                    ApiMode = ModelProviderApiModes.OpenAiCompatible, MaxOutputTokens = 256
                };
                await store.SaveSnapshotAsync(snapshot);
                return new PostCommitRefreshFixture(directory, store, (await store.LoadSnapshotAsync())!, failureAtRead);
            }
            catch
            {
                RemoveFixtureDirectory(directory);
                throw;
            }
        }

        private static void RemoveFixtureDirectory(string path)
        {
            const string prefix = "aiarena-postcommit-refresh-";
            var resolved = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(resolved);
            if (!string.Equals(Path.GetDirectoryName(resolved), temporary, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
                throw new InvalidOperationException("Refusing to remove an unexpected refresh fixture directory");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }

        internal void ResetReads() => Reads = 0;

        internal void RequireLastProjectionRetained()
        {
            Require(ReferenceEquals(GetField("_lastRenderedSnapshot"), rendered)
                    && ReferenceEquals(GetField("_lastRenderedMessages"), rendered.Messages)
                    && Equals(GetField("_activeSnapshotStamp"), previousStamp),
                "Postcommit refresh failure must retain the last good transcript and stamp instead of applying fallback state");
        }

        private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Window refresh fixture field {name} is missing");
        private void SetField(string name, object value) => Field(name).SetValue(window, value);
        private object? GetField(string name) => Field(name).GetValue(window);
        public void Dispose()
        {
            Loads.Dispose();
            RemoveFixtureDirectory(directory);
        }
    }
}
