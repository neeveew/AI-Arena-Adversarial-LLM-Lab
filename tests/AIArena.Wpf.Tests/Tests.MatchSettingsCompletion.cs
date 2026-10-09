using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private static readonly string[] MatchSettingKinds = ["voice", "pressure", "color", "lock", "text"];

    static void MatchSettingsCompletionPreservesSavedValuesAfterEvidenceFailure() =>
        RunMatchSettingCases(evidenceFails: true, refreshFails: false);

    static void MatchSettingsCompletionPreservesSavedValuesAfterRefreshFailure() =>
        RunMatchSettingCases(evidenceFails: false, refreshFails: true);

    private static void RunMatchSettingCases(bool evidenceFails, bool refreshFails)
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var failed = new List<string>();
            foreach (var kind in MatchSettingKinds)
            {
                using var fixture = new MatchSettingFixture(evidenceFails);
                fixture.RefreshFails = refreshFails;
                fixture.Change(kind);
                var saved = PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!;
                if (fixture.Errors.Count != 0 || fixture.Refreshes.Count != 1
                    || !MatchSettingSaved(saved, kind) || !fixture.VisibleStatuses.Any(status => status.Contains("Warning:", StringComparison.Ordinal)))
                    failed.Add(kind);
                Require(!fixture.VisibleStatuses.Any(status => status.Contains("private refresh", StringComparison.Ordinal)
                        || status.Contains("sk-test-secret", StringComparison.Ordinal)), "Saved-setting warnings exposed private exception text.");
            }
            Require(failed.Count == 0, $"Saved settings were lost or misreported after secondary failure: {string.Join(", ", failed)}.");
        }));
    }

    static void MatchSettingsCompletionRetainsRealPrimaryFailures()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var kind in MatchSettingKinds.Where(kind => kind != "lock"))
            {
                using var fixture = new MatchSettingFixture();
                var before = PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!.PersistenceRevision;
                fixture.SaveFails = true;
                fixture.Change(kind);
                var after = PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!;
                Require(fixture.Errors.Count == 1 && fixture.Refreshes.Count == 0
                        && after.PersistenceRevision == before && !MatchSettingSaved(after, kind),
                    "A real primary save failure must remain failed without refresh or a fictitious saved value.");
            }
        }));
    }

    static void MatchSettingsCompletionRejectsObsoleteSessionProjection()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new MatchSettingFixture();
            fixture.AfterSave = () => fixture.Active = fixture.Active with { Id = "other" };
            fixture.Change("voice");
            Require(MatchSettingSaved(PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!, "voice")
                    && fixture.Refreshes.Count == 0 && fixture.Statuses.Count == 0,
                "An old setting completion must retain its saved session but not refresh or overwrite the newly selected session.");
            foreach (var kind in MatchSettingKinds)
            {
                using var late = new MatchSettingFixture();
                late.RefreshFails = true;
                late.AfterRefresh = () => late.Active = late.Active with { Id = "other" };
                late.Change(kind);
                Require(late.Errors.Count == 0 && late.Statuses.Count == 0
                        && MatchSettingSaved(PumpDocumentImportTask(late.Store.LoadSnapshotAsync())!, kind),
                    "A session change during refresh must suppress obsolete fallback feedback without changing the saved outcome.");
            }
        }));
    }

    static void MatchSettingsCompletionTriesFallbackStatusIndependently()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var bothFail in new[] { false, true })
            {
                using var fixture = new MatchSettingFixture();
                fixture.RefreshFails = true;
                fixture.LoadStatusFails = true;
                fixture.ArenaStatusFails = bothFail;
                fixture.Change("color");
                Require(fixture.Errors.Count == 0 && fixture.LoadStatusAttempts == 1 && fixture.ArenaStatusAttempts == 1
                        && MatchSettingSaved(PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!, "color"),
                    "A failing status view must not hide another status callback or turn a saved setting into failure.");
            }
        }));
    }

    static void MatchSettingsSaveFeedbackCannotPreventCommit()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var progressFails in new[] { true, false })
            {
                using var fixture = new MatchSettingFixture();
                var snapshot = PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!;
                snapshot.Engine.Agents[0].VoiceStyle = "scientific";
                string? warning = null;
                Exception? escaped = null;
                try
                {
                    warning = PumpDocumentImportTask(AppPostCommitEvidence.SaveWithFeedbackAsync(
                        () => fixture.Store.SaveSnapshotAsync(snapshot),
                        () => { if (progressFails) throw new IOException("private progress sk-test-secret"); },
                        () => { if (!progressFails) throw new IOException("private saved sk-test-secret"); },
                        _ => { }, AppErrorContext.SavedState));
                }
                catch (Exception exception) { escaped = exception; }
                Require(escaped is null && warning is not null && warning.Contains("saved", StringComparison.OrdinalIgnoreCase)
                        && !warning.Contains("private", StringComparison.Ordinal) && !warning.Contains("sk-test-secret", StringComparison.Ordinal)
                        && MatchSettingSaved(PumpDocumentImportTask(fixture.Store.LoadSnapshotAsync())!, "voice"),
                    "Progress/success feedback must not prevent or misreport an actual snapshot commit.");
            }
        }));
    }

    static void MatchSettingsCompletionMissingSnapshotCannotClaimSaved()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var kind in MatchSettingKinds)
            {
                using var fixture = new MatchSettingFixture();
                fixture.RemoveSnapshot();
                fixture.Change(kind);
                Require(fixture.Errors.Count == 0 && fixture.Refreshes.Count == 0
                        && fixture.Statuses.Any(status => status.StartsWith("No snapshot found", StringComparison.Ordinal)),
                    "A missing snapshot must not publish a successful setting or lock change.");
            }
        }));
    }

    static void MatchSettingsSaveFeedbackRetainsPrimaryException()
    {
        var original = new IOException("primary save failed");
        Exception? escaped = null;
        try
        {
            AppPostCommitEvidence.SaveWithFeedbackAsync(() => Task.FromException(original), () => { }, () => { },
                _ => throw new InvalidOperationException("failure view failed"), AppErrorContext.SavedState).GetAwaiter().GetResult();
        }
        catch (Exception exception) { escaped = exception; }
        Require(ReferenceEquals(escaped, original), "Failure feedback must retain the original primary exception.");
    }

    private static bool MatchSettingSaved(ArenaSnapshot snapshot, string kind) => kind switch
    {
        "voice" => snapshot.Engine.Agents.Single(agent => agent.Id == "alpha").VoiceStyle == "scientific",
        "pressure" => snapshot.Engine.Agents.Single(agent => agent.Id == "alpha").PressureProfile == "assertive",
        "color" => snapshot.Engine.Agents.Single(agent => agent.Id == "alpha").AccentColor == "#123456",
        "lock" => snapshot.MatchLocks.GetValueOrDefault("alpha"),
        "text" => snapshot.Engine.Steering.Topic == "New test topic" && snapshot.MatchLocks.GetValueOrDefault("topic"),
        _ => false
    };

    private sealed class MatchSettingFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "aiarena-match-setting-" + Guid.NewGuid().ToString("N"));
        private readonly Window owner = new();
        private readonly MatchGenerationService generation;
        internal SessionStore Store { get; }
        internal MatchLockCoordinator Coordinator { get; }
        internal SessionSummary Active { get; set; }
        internal Task LastOperation { get; private set; } = Task.CompletedTask;
        internal List<Exception> Errors { get; } = [];
        internal List<string> Refreshes { get; } = [];
        internal List<string> Statuses { get; } = [];
        internal IEnumerable<string> VisibleStatuses => Refreshes.Concat(Statuses);
        internal bool RefreshFails, SaveFails, LoadStatusFails, ArenaStatusFails;
        internal int LoadStatusAttempts, ArenaStatusAttempts;
        internal Action? AfterSave, AfterRefresh;

        internal MatchSettingFixture(bool evidenceFails = false)
        {
            Store = new SessionStore(root);
            PumpDocumentImportTask(Store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot()));
            Active = PumpDocumentImportTask(Store.ListSessionsAsync()).Single();
            var events = new EventLogStore(evidenceFails ? Path.Combine(root, "unavailable-events") : root,
                requireExistingSession: evidenceFails);
            generation = new MatchGenerationService(sessionStore: Store, eventLogStore: events);
            Coordinator = new MatchLockCoordinator(owner, Store, events, generation, () => Active,
                () => ThemePalette.Resolve("dark-blue"), () => false, () => false,
                _ => Brushes.White, (brush, _, _) => brush,
                (_, _, action, _) => LastOperation = RunOperation(action),
                async (snapshot, id) =>
                {
                    if (SaveFails) throw new IOException("primary save failed");
                    await Store.SaveSnapshotAsync(snapshot, id);
                    AfterSave?.Invoke();
                },
                status =>
                {
                    Refreshes.Add(status);
                    AfterRefresh?.Invoke();
                    if (RefreshFails) throw new IOException("private refresh sk-test-secret");
                    return Task.CompletedTask;
                },
                status => { LoadStatusAttempts++; if (LoadStatusFails) throw new IOException("private status"); Statuses.Add(status); },
                status => { ArenaStatusAttempts++; if (ArenaStatusFails) throw new IOException("private status"); Statuses.Add(status); });
        }

        private async Task RunOperation(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { Errors.Add(exception); }
        }

        internal void RemoveSnapshot()
        {
            Require(Path.GetFullPath(Active.SnapshotPath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "Snapshot deletion escaped the owned fixture.");
            File.Delete(Active.SnapshotPath);
        }

        internal void Change(string kind)
        {
            object? Call(string method, params object[] args) => typeof(MatchLockCoordinator)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Coordinator, args);
            switch (kind)
            {
                case "voice":
                case "pressure":
                    var picker = (ComboBox)Call(kind == "voice" ? "CreateVoiceStylePicker" : "CreateAgentPressurePicker", "alpha", "default")!;
                    var tag = kind == "voice" ? "scientific" : "assertive";
                    picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
                    break;
                case "color": PumpDocumentImportTask((Task)Call("UpdateAccentColorAsync", "alpha", "#123456")!); break;
                case "text": PumpDocumentImportTask((Task)Call("UpdateMatchTextAsync", Active, "topic", "New test topic")!); break;
                case "lock":
                    Call("MatchLockChanged", new CheckBox { Tag = "alpha", IsChecked = true }, new RoutedEventArgs());
                    break;
            }
            PumpDocumentImportTask(LastOperation);
        }

        public void Dispose()
        {
            generation.Dispose();
            owner.Close();
            Require(Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                "Match-setting cleanup escaped its temporary parent.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
