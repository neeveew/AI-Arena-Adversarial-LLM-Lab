using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;

internal static partial class Program
{
    private static readonly string[] OwnedMatchControlKinds = ["voice", "pressure", "lock"];

    static void MatchControlOwnershipRejectsNewlySelectedSession() => RunRetiredMatchControlCases("switch");
    static void MatchControlOwnershipRejectsReplacedControls() => RunRetiredMatchControlCases("replace");
    static void MatchControlOwnershipRejectsDetachedControls() => RunRetiredMatchControlCases("detach");

    private static void RunRetiredMatchControlCases(string mode)
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var changed = new List<string>();
            foreach (var kind in OwnedMatchControlKinds)
            {
                using var fixture = new MatchControlOwnershipFixture();
                if (mode == "switch") fixture.State.Active = fixture.Other;
                else if (mode == "replace")
                {
                    fixture.State.Coordinator.ClearControls();
                    fixture.Host.Content = fixture.CreateCard();
                    fixture.Host.UpdateLayout();
                    PumpLifecycleUi();
                }
                else
                {
                    fixture.Host.Content = new Border();
                    fixture.Host.UpdateLayout();
                    PumpLifecycleUi();
                }
                fixture.Change(kind);
                var original = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!;
                var other = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!;
                if (original.PersistenceRevision != fixture.OriginalRevision || other.PersistenceRevision != fixture.OtherRevision
                    || fixture.State.Refreshes.Count != 0 || fixture.State.SaveAttempts != 0 || fixture.State.Errors.Count != 0)
                    changed.Add(kind);
            }
            Require(changed.Count == 0, $"Retained {mode} controls started an obsolete mutation: {string.Join(", ", changed)}.");
        }));
    }

    static void MatchControlOwnershipPreservesAdmittedOriginalSessionWork()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var kind in OwnedMatchControlKinds)
            {
                using var fixture = new MatchControlOwnershipFixture();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.State.BeforeOperationAsync = () => { entered.TrySetResult(); return release.Task; };
                try
                {
                    fixture.StartChange(kind);
                    PumpDocumentImportTask(entered.Task);
                    fixture.State.Active = fixture.Other;
                    fixture.State.Coordinator.ClearControls();
                    fixture.Host.Content = fixture.CreateCard();
                    fixture.Host.UpdateLayout();
                }
                finally { release.TrySetResult(); PumpDocumentImportTask(fixture.State.LastOperation); PumpLifecycleUi(); }
                var original = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!;
                var other = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!;
                Require(original.PersistenceRevision == fixture.OriginalRevision + 1 && MatchSettingSaved(original, kind)
                        && other.PersistenceRevision == fixture.OtherRevision && fixture.State.Refreshes.Count == 0,
                    "An admitted setting must finish in its original session without projecting into a newer session.");
            }
        }));
    }

    static void MatchControlOwnershipAdmitsFreshControlsForCurrentSession()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var kind in OwnedMatchControlKinds)
            {
                using var fixture = new MatchControlOwnershipFixture();
                fixture.State.Active = fixture.Other;
                fixture.State.Coordinator.ClearControls();
                fixture.Host.Content = fixture.CreateCard();
                fixture.Host.UpdateLayout();
                PumpLifecycleUi();
                var card = (DependencyObject)fixture.Host.Content;
                if (kind == "lock") CastControlDescendants<CheckBox>(card).Single().IsChecked = true;
                else
                {
                    var picker = CastControlDescendants<ComboBox>(card).Single(control => AutomationProperties.GetName(control) == "Alpha " + kind);
                    var tag = kind == "voice" ? "scientific" : "assertive";
                    picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
                }
                PumpDocumentImportTask(fixture.State.LastOperation);
                var other = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!;
                Require(other.PersistenceRevision == fixture.OtherRevision + 1 && MatchSettingSaved(other, kind)
                        && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                    "A newly rendered current-session control must remain usable and save only its owning session.");
            }
        }));
    }

    static void MatchControlOwnershipRejectsBlockedOrMissingSessionEvents()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var mode in new[] { "busy", "rendering", "missing" })
            foreach (var kind in OwnedMatchControlKinds)
            {
                using var fixture = new MatchControlOwnershipFixture();
                if (mode == "busy") { fixture.State.Busy = true; fixture.State.Coordinator.UpdateBusyState(true); }
                else if (mode == "rendering") fixture.State.Rendering = true;
                else fixture.State.Active = null!;
                fixture.Change(kind);
                Require(fixture.State.SaveAttempts == 0 && fixture.State.Refreshes.Count == 0
                        && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                    "Busy, rendering, and missing-session events must not admit a mutation.");
            }
        }));
    }

    private sealed class MatchControlOwnershipFixture : IDisposable
    {
        internal MatchSettingFixture State { get; } = new();
        internal Window Host { get; } = new() { Width = 600, Height = 400, ShowInTaskbar = false };
        internal SessionSummary Other { get; }
        internal long OriginalRevision { get; }
        internal long OtherRevision { get; }
        private readonly ComboBox voice;
        private readonly ComboBox pressure;
        private readonly CheckBox locked;

        internal MatchControlOwnershipFixture()
        {
            PumpDocumentImportTask(State.Store.CreateSessionAsync("other", SessionStore.CreateDefaultSnapshot()));
            Other = PumpDocumentImportTask(State.Store.ListSessionsAsync()).Single(session => session.Id == "other");
            OriginalRevision = PumpDocumentImportTask(State.Store.LoadSnapshotAsync())!.PersistenceRevision;
            OtherRevision = PumpDocumentImportTask(State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision;
            var card = CreateCard();
            Host.Content = card;
            AttachArenaPresentationResources(Host);
            Host.Resources[typeof(ComboBox)] = Host.FindResource("Arena.ComboBox");
            Host.Show();
            PumpLifecycleUi();
            voice = CastControlDescendants<ComboBox>(card).Single(control => AutomationProperties.GetName(control) == "Alpha voice");
            pressure = CastControlDescendants<ComboBox>(card).Single(control => AutomationProperties.GetName(control) == "Alpha pressure");
            locked = CastControlDescendants<CheckBox>(card).Single();
        }

        internal Border CreateCard() => State.Coordinator.CreateLockCard("alpha", "Alpha", "Test persona",
            Brushes.Black, Brushes.Cyan, false, "default", "default");

        internal void StartChange(string kind)
        {
            if (kind == "lock") locked.IsChecked = true;
            else
            {
                var picker = kind == "voice" ? voice : pressure;
                var tag = kind == "voice" ? "scientific" : "assertive";
                picker.SelectedItem = picker.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, tag));
            }
        }

        internal void Change(string kind) { StartChange(kind); PumpDocumentImportTask(State.LastOperation); PumpLifecycleUi(); }
        public void Dispose() { Host.Close(); State.Dispose(); }
    }
}
