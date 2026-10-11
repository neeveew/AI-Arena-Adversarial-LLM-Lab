using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Services;
using AIArena.Wpf;

internal static partial class Program
{
    private static readonly string[] ColorPopupChoices = ["Apply", "Reset", "Preset"];

    static void ColorPopupPreservesOrdinarySessionChanges() => RunColorPopupChoices(switched: false);
    static void ColorPopupRejectsActionsAfterSessionSwitch() => RunColorPopupChoices(switched: true);

    private static void RunColorPopupChoices(bool switched)
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var failures = new List<string>();
            foreach (var choice in ColorPopupChoices)
            {
                using var fixture = new ColorPopupFixture();
                var popup = fixture.Open();
                Require(popup.IsOpen, "The real color popup must be visible before the action.");
                if (switched) fixture.State.Active = fixture.Other;
                fixture.Choose(choice);
                var original = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("default"))!;
                var other = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!;
                var expected = choice == "Reset" ? "" : choice == "Apply" ? "#778899" : AgentAccentService.PresetOptions[0].Hex;
                if (switched)
                {
                    if (original.PersistenceRevision != fixture.OriginalRevision || other.PersistenceRevision != fixture.OtherRevision
                        || original.Engine.Agents[0].AccentColor != "#112233" || other.Engine.Agents[0].AccentColor != "#445566")
                        failures.Add(choice);
                }
                else
                {
                    Require(original.Engine.Agents[0].AccentColor == expected
                            && other.Engine.Agents[0].AccentColor == "#445566" && fixture.State.Errors.Count == 0
                            && fixture.State.Refreshes.Count == 1 && !popup.IsOpen,
                        "An ordinary popup choice must save only its session and retain its normal completion behavior.");
                }
            }
            Require(failures.Count == 0, "An old visible popup changed a session after switching: " + string.Join(", ", failures));
        }));
    }

    static void ColorPopupRetiresWhenControlsAreReplaced()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var popup = fixture.Open();
            fixture.State.Coordinator.ClearControls();
            fixture.Host.Content = fixture.CreateCard();
            fixture.Host.UpdateLayout();
            PumpLifecycleUi();
            Require(!popup.IsOpen, "Replaced Cast controls must retire their old color popup.");
            fixture.Choose("Apply");
            Require(PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                "A retained callback from a retired popup must not save even when the same session remains active.");
        }));
    }

    static void ColorPopupRetainsAcceptedTargetThroughCloseCallbacks()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var switchBeforeCompletion in new[] { false, true })
            {
                using var fixture = new ColorPopupFixture();
                var popup = fixture.Open();
                var refreshedSessions = new List<string>();
                var closed = false;
                fixture.State.AfterRefresh = () => refreshedSessions.Add(fixture.State.Active.Id);
                if (switchBeforeCompletion) fixture.State.AfterSave = () => { fixture.State.Active = fixture.Other; PumpLifecycleUi(); };
                popup.Closed += (_, _) => { closed = true; fixture.State.Active = fixture.Other; };
                fixture.Choose("Apply");
                var original = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!;
                var other = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!;
                Require(closed && original.Engine.Agents[0].AccentColor == "#778899" && other.Engine.Agents[0].AccentColor == "#445566"
                        && original.PersistenceRevision == fixture.OriginalRevision + 1 && other.PersistenceRevision == fixture.OtherRevision
                        && refreshedSessions.All(id => id == "default"),
                    $"An accepted choice must retain its original target and never refresh a different session (original {original.Engine.Agents[0].AccentColor}; other {other.Engine.Agents[0].AccentColor}; refreshed {string.Join(",", refreshedSessions)}).");
                Console.WriteLine($"color popup ownership receipt: session_change_before_completion={switchBeforeCompletion}; original_writes=1; other_writes=0; refreshed_sessions=[{string.Join(",", refreshedSessions)}]");
            }
        }));
    }

    static void ColorPopupLifecycleKeepsOneCurrentOwner()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new ColorPopupFixture();
            var first = fixture.Open();
            var retiredApply = fixture.Action("Apply");
            var second = fixture.Open();
            Require(!first.IsOpen && second.IsOpen, "Opening a new role popup must close the previous one.");
            retiredApply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDocumentImportTask(fixture.State.LastOperation);
            PumpLifecycleUi();
            Require(second.IsOpen && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!.PersistenceRevision == fixture.OriginalRevision,
                "A retired choice must neither save nor dismiss the newer popup.");
            fixture.State.Busy = true;
            fixture.State.Coordinator.UpdateBusyState(true);
            Require(!second.IsOpen && fixture.TryOpen() is null, "Arena work must close the current popup and prevent reopening.");
            fixture.State.Busy = false;
            fixture.State.Coordinator.UpdateBusyState(false);
            fixture.State.Active = null!;
            Require(fixture.TryOpen() is null, "No active session must not open an unowned editor.");
            fixture.State.Active = fixture.Other;
            Require(fixture.TryOpen() is null, "An old card must not open a popup for a newly active session.");
            fixture.State.Coordinator.ClearControls();
            fixture.Host.Content = fixture.CreateCard();
            fixture.Host.UpdateLayout();
            PumpLifecycleUi();
            var current = fixture.Open();
            var detachedApply = fixture.Action("Apply");
            fixture.Host.Content = new Border();
            fixture.Host.UpdateLayout();
            PumpLifecycleUi();
            Require(!current.IsOpen, "Unloading the source card must close its popup.");
            detachedApply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpDocumentImportTask(fixture.State.LastOperation);
            Require(PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision == fixture.OtherRevision,
                "A detached card callback must not write even when its session remains current.");
        }));
    }

    private sealed class ColorPopupFixture : IDisposable
    {
        internal MatchSettingFixture State { get; } = new();
        internal Window Host { get; } = new() { Width = 600, Height = 400, ShowInTaskbar = false };
        internal SessionSummary Other { get; }
        internal long OriginalRevision { get; }
        internal long OtherRevision { get; }
        private Popup? popup;
        private Button? apply;
        private Button? reset;
        private Button? preset;

        internal ColorPopupFixture()
        {
            var original = PumpDocumentImportTask(State.Store.LoadSnapshotAsync())!;
            original.Engine.Agents[0].AccentColor = "#112233";
            PumpDocumentImportTask(State.Store.SaveSnapshotAsync(original));
            var other = SessionStore.CreateDefaultSnapshot();
            other.Engine.Agents[0].AccentColor = "#445566";
            PumpDocumentImportTask(State.Store.CreateSessionAsync("other", other));
            Other = PumpDocumentImportTask(State.Store.ListSessionsAsync()).Single(session => session.Id == "other");
            OriginalRevision = PumpDocumentImportTask(State.Store.LoadSnapshotAsync())!.PersistenceRevision;
            OtherRevision = PumpDocumentImportTask(State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision;
            Host.Content = CreateCard();
            AttachArenaPresentationResources(Host);
            Host.Resources[typeof(Button)] = Host.FindResource("Arena.Button.Base");
            Host.Resources[typeof(TextBox)] = Host.FindResource("Arena.TextBox");
            State.ResourceOverride = key => Host.TryFindResource(key) as Brush ?? Brushes.White;
            Host.Show();
            PumpLifecycleUi();
        }

        internal Border CreateCard() => State.Coordinator.CreateLockCard("alpha", "Alpha", "Test persona",
            Brushes.Black, Brushes.Cyan, false, "default", "default", "#112233");

        internal Popup? TryOpen()
        {
            var button = CastControlDescendants<Button>((DependencyObject)Host.Content)
                .Single(candidate => Equals(candidate.ToolTip, "Customize this role color"));
            return (Popup?)typeof(MatchLockCoordinator).GetMethod("OpenAccentColorPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(State.Coordinator, [button, button.Tag])!;
        }

        internal Popup Open()
        {
            popup = TryOpen() ?? throw new InvalidOperationException("Expected the current session's loaded card to open its color popup.");
            PumpLifecycleUi();
            var root = popup.Child;
            CastControlDescendants<TextBox>(root).Single().Text = "#778899";
            var buttons = CastControlDescendants<Button>(root).ToArray();
            apply = buttons.Single(candidate => CastControlDescendants<TextBlock>(candidate).Any(text => text.Text == "Apply"));
            reset = buttons.Single(candidate => CastControlDescendants<TextBlock>(candidate).Any(text => text.Text == "Reset"));
            preset = buttons.Single(candidate => Equals(candidate.Tag, AgentAccentService.PresetOptions[0].Hex));
            return popup;
        }

        internal void Choose(string choice)
        {
            StartChoice(choice);
            PumpDocumentImportTask(State.LastOperation);
            PumpLifecycleUi();
        }

        internal void StartChoice(string choice) => Action(choice).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        internal Button Action(string choice) => (choice == "Apply" ? apply : choice == "Reset" ? reset : preset)!;
        internal TextBox HexInput => CastControlDescendants<TextBox>(popup!.Child).Single();

        public void Dispose()
        {
            if (popup is not null) popup.IsOpen = false;
            Host.Close();
            State.Dispose();
        }
    }
}
