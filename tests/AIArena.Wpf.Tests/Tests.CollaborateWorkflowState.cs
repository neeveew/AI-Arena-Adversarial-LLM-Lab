using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private const string WorkflowExistingNote = "An existing durable note.";
    private const string WorkflowNewNote = "Keep this unsaved memory draft.";

    static void CollaborateMemorySaveFailuresPreserveDraftAndState()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var clear in new[] { false, true })
        foreach (var failView in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new CollaborateWorkflowFixture(virtualized);
                fixture.SeedConversationAndNote();
                fixture.Ui.Memory.Text = WorkflowNewNote;
                var saves = fixture.Store.SaveCalls;
                fixture.Store.FailSaving = true;
                fixture.FailBrushOnce = failView;
                if (clear) fixture.Coordinator.ClearMemoryNotes(); else fixture.Coordinator.SaveMemoryNote();
                Require(fixture.Ui.Memory.Text == WorkflowNewNote
                        && fixture.Coordinator.DebugMemoryNotes.SequenceEqual([WorkflowExistingNote]),
                    "A failed memory save discarded the draft or changed active memory.");
                Require(fixture.Store.Load().Single().MemoryNotes.SequenceEqual([WorkflowExistingNote])
                        && fixture.Coordinator.CaptureControlReview("").MemoryNoteCount == 1
                        && fixture.Store.SaveCalls == saves + 1 && fixture.Client.CompleteCalls == 1,
                    "A failed memory mutation changed stored/current chat state or repeated unrelated work.");
                Require(fixture.Center.VisibleEntries.Single(entry => entry.Key == "collaborate.notice").State == ApplicationStatusState.Failed
                        && fixture.Status.Text.Contains("AA-COLLAB-IO", StringComparison.Ordinal),
                    "A failed primary memory save was not presented as a coded failure.");
                fixture.Store.FailSaving = false;
                if (clear) fixture.Coordinator.ClearMemoryNotes(); else fixture.Coordinator.SaveMemoryNote();
                var expected = clear ? Array.Empty<string>() : new[] { WorkflowNewNote, WorkflowExistingNote };
                Require(fixture.Store.Load().Single().MemoryNotes.SequenceEqual(expected)
                        && fixture.Coordinator.DebugMemoryNotes.SequenceEqual(expected)
                        && fixture.Ui.Memory.Text == (clear ? WorkflowNewNote : ""),
                    "Retrying memory save/clear did not commit exactly the retained intent.");
            }));
    }

    static void CollaborateMemoryProjectionFailuresPreserveCommittedState()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var clear in new[] { false, true })
        foreach (var afterSave in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new CollaborateWorkflowFixture(virtualized);
                fixture.SeedConversationAndNote();
                fixture.Ui.Memory.Text = WorkflowNewNote;
                var saves = fixture.Store.SaveCalls;
                if (afterSave) fixture.Store.AfterSave = () => fixture.FailBrushOnce = true;
                else fixture.FailBrushOnce = true;
                Exception? escaped = null;
                try { if (clear) fixture.Coordinator.ClearMemoryNotes(); else fixture.Coordinator.SaveMemoryNote(); }
                catch (Exception exception) { escaped = exception; }
                var expected = clear ? Array.Empty<string>() : new[] { WorkflowNewNote, WorkflowExistingNote };
                Require(escaped is null && fixture.Store.SaveCalls == saves + 1
                        && fixture.Store.Load().Single().MemoryNotes.SequenceEqual(expected)
                        && fixture.Coordinator.DebugMemoryNotes.SequenceEqual(expected),
                    "A memory view failure prevented or obscured the authoritative commit.");
                Require(fixture.Ui.Memory.Text == (clear ? WorkflowNewNote : "")
                        && fixture.Center.VisibleEntries.Single(entry => entry.Key == "collaborate.notice").State == ApplicationStatusState.Warning
                        && fixture.Status.Text.Contains("saved", StringComparison.OrdinalIgnoreCase)
                        && !fixture.Status.Text.Contains("sk-test-secret", StringComparison.Ordinal),
                    "Committed memory did not retain its correct input policy and safe warning.");
            }));
    }

    static void CollaborateOptionalPreflightRefreshDoesNotBlockGeneration()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var hasHistory in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new CollaborateWorkflowFixture(virtualized);
                if (hasHistory) fixture.SeedConversation();
                var calls = fixture.Client.CompleteCalls;
                var saves = fixture.Store.SaveCalls;
                fixture.Prompt.Text = "Continue despite an optional sidebar refresh failure.";
                fixture.FailBrushOnce = true;
                Exception? escaped = null;
                try { PumpDocumentImportTask(fixture.Coordinator.SendAsync()); }
                catch (Exception exception) { escaped = exception; }
                Require(escaped is null && fixture.ControlsReady && fixture.Client.CompleteCalls == calls + 1
                        && fixture.Store.SaveCalls == saves + 1 && fixture.Prompt.Text.Length == 0,
                    "An optional preflight refresh stranded controls or blocked the requested model run.");
                Require(fixture.Center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Succeeded
                        && fixture.Center.VisibleEntries.Any(entry => entry.Key == "collaborate.notice" && entry.State == ApplicationStatusState.Warning),
                    "A preflight sidebar failure changed the completed model outcome or lost its warning.");
            }));
    }

    static void CollaborateRequiredPreflightFailureRestoresControlsAndDraft()
    {
        foreach (var hasHistory in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new CollaborateWorkflowFixture(false);
                if (hasHistory) fixture.SeedConversation();
                var calls = fixture.Client.CompleteCalls;
                var saves = fixture.Store.SaveCalls;
                fixture.Prompt.Text = "Retain this prompt when the transcript cannot be prepared.";
                ((FailingWorkflowPanel)fixture.Panel).FailNextAddedChild = true;
                Exception? escaped = null;
                try { PumpDocumentImportTask(fixture.Coordinator.SendAsync()); }
                catch (Exception exception) { escaped = exception; }
                Require(escaped is null && fixture.ControlsReady && fixture.Prompt.Text.StartsWith("Retain this prompt", StringComparison.Ordinal)
                        && fixture.Client.CompleteCalls == calls && fixture.Store.SaveCalls == saves,
                    "A required transcript setup failure stranded controls, consumed input, or started model work.");
                Require(!fixture.Center.VisibleEntries.Any(entry => entry.IsActive)
                        && fixture.Status.Text.Contains("AA-COLLAB-UNEXPECTED", StringComparison.Ordinal)
                        && !fixture.Status.Text.Contains("sk-test-secret", StringComparison.Ordinal)
                        && !LifecycleText(fixture.Panel).Contains("Retain this prompt", StringComparison.Ordinal),
                    "A failed setup left a live operation or exposed private exception details.");
                PumpDocumentImportTask(fixture.Coordinator.SendAsync());
                Require(fixture.Client.CompleteCalls == calls + 1 && fixture.Store.SaveCalls == saves + 1
                        && fixture.Prompt.Text.Length == 0 && fixture.ControlsReady,
                    "Retrying after a failed transcript setup could not complete one normal run.");
            }));
    }

    static void CollaborateEarlyContextAndNewerMemoryDraftsSurvive()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var hasHistory in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                using var fixture = new CollaborateWorkflowFixture(virtualized);
                if (hasHistory) fixture.SeedConversation();
                fixture.Ui.Memory.Text = WorkflowNewNote;
                if (hasHistory) fixture.Store.AfterSave = () => fixture.Ui.Memory.Text = "A newer memory draft.";
                fixture.Coordinator.SaveMemoryNote();
                Require(fixture.Coordinator.DebugMemoryNotes.SequenceEqual([WorkflowNewNote]),
                    "Adding a memory note changed its accepted context.");
                if (hasHistory)
                    Require(fixture.Store.Load().Single().MemoryNotes.SequenceEqual([WorkflowNewNote])
                            && fixture.Ui.Memory.Text == "A newer memory draft.",
                        "A successful memory commit discarded a newer draft.");
                else
                {
                    Require(fixture.Store.SaveCalls == 0 && fixture.Ui.Memory.Text.Length == 0
                            && fixture.Status.Text.Contains("current prompt context", StringComparison.Ordinal)
                            && !fixture.Status.Text.Contains("saved", StringComparison.OrdinalIgnoreCase),
                        "Memory before the first answer falsely promised durable storage.");
                    fixture.SeedConversation();
                    Require(fixture.Store.Load().Single().MemoryNotes.SequenceEqual([WorkflowNewNote]),
                        "The first saved answer did not include its accepted memory context.");
                }
            }));
    }

    private sealed class CollaborateWorkflowTestUi
    {
        internal TextBox Memory { get; } = new();
        internal Button Send { get; } = new();
        internal Button Stop { get; } = new();
        internal Button Clear { get; } = new();
        internal Button NewChat { get; } = new();
        internal Button Provider { get; } = new();
        internal ComboBox Mode { get; } = new();
        internal ComboBox Rounds { get; } = new();
    }

    private sealed class CollaborateWorkflowFixture : IDisposable
    {
        private readonly string root = DraftTestRoot("collaborate-workflow-state");
        internal CollaborateWorkflowTestUi Ui { get; } = new();
        internal TextBox Prompt { get; } = new();
        internal TextBlock Status { get; } = new();
        internal ApplicationStatusCenter Center { get; } = new();
        internal FixedCollaborateModelClient Client { get; } = new(PostSaveAnswer);
        internal WorkflowCollaborateHistoryStore Store { get; }
        internal Panel Panel { get; }
        internal CollaborateCoordinator Coordinator { get; }
        internal bool FailBrushOnce { get; set; }
        internal bool ControlsReady => !Coordinator.IsRunning && Prompt.IsEnabled && Ui.Send.IsEnabled && !Ui.Stop.IsEnabled
            && Ui.Clear.IsEnabled && Ui.NewChat.IsEnabled && Ui.Provider.IsEnabled && Ui.Mode.IsEnabled && !Ui.Rounds.IsEnabled;
        internal CollaborateWorkflowFixture(bool virtualized)
        {
            Store = new WorkflowCollaborateHistoryStore(Path.Combine(root, "history.json"));
            Panel = virtualized ? new VirtualizingConversationPanel() : new FailingWorkflowPanel();
            Coordinator = CreateLifecycleCollaborate(Client, Prompt, Status, Panel, Center, "fast", Store, key =>
            {
                if (FailBrushOnce) { FailBrushOnce = false; throw new InvalidOperationException(PrivateProjectionFailure); }
                return AccentResourceBrush(key);
            }, Ui);
            Coordinator.Initialize();
        }
        internal void SeedConversation()
        {
            Prompt.Text = "An existing saved conversation.";
            PumpDocumentImportTask(Coordinator.SendAsync());
        }
        internal void SeedConversationAndNote()
        {
            SeedConversation();
            Ui.Memory.Text = WorkflowExistingNote;
            Coordinator.SaveMemoryNote();
        }
        public void Dispose() { Coordinator.Stop(); DeleteDraftTestRoot(root); }
    }

    private sealed class WorkflowCollaborateHistoryStore(string path) : CollaborateHistoryStore(path)
    {
        internal int SaveCalls { get; private set; }
        internal bool FailSaving { get; set; }
        internal Action? AfterSave { get; set; }
        public override void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
        {
            SaveCalls++;
            if (FailSaving) throw new IOException(PrivateProjectionFailure);
            base.Save(conversations);
            AfterSave?.Invoke();
        }
    }

    private sealed class FailingWorkflowPanel : StackPanel
    {
        internal bool FailNextAddedChild { get; set; }
        protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
        {
            base.OnVisualChildrenChanged(visualAdded, visualRemoved);
            if (visualAdded is not null && FailNextAddedChild)
            {
                FailNextAddedChild = false;
                throw new InvalidOperationException(PrivateProjectionFailure);
            }
        }
    }
}
