using System.IO;
using AIArena.Core.Persistence;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void SavedStateCheckpointSaveRetainsCommittedOutcome()
    {
        WithSavedCommitFixture((store, unavailableEvents, directory) =>
        {
            var consumed = 0;
            var refreshed = 0;
            var result = SavedStateWorkflowCoordinator.SaveCheckpointAndReportAsync(
                store, unavailableEvents, "default", "Evidence failure fixture",
                () => consumed++,
                (checkpointId, token) =>
                {
                    refreshed++;
                    Require(!token.CanBeCanceled && checkpointId is not null, "Committed checkpoint refresh must retain identity and ignore caller cancellation");
                    throw new IOException("private refresh path must not appear in status");
                }).GetAwaiter().GetResult();
            Require(consumed == 1 && refreshed == 1 && !result.EventRecorded
                && File.Exists(result.Checkpoint.Path)
                && store.ListCheckpointsAsync("default").GetAwaiter().GetResult().Count == 1,
                "An unavailable audit log or picker must not hide, repeat, or discard the checkpoint commit");
            Require(result.Outcome.StartsWith("Saved checkpoint:", StringComparison.Ordinal)
                && result.Outcome.Contains("checkpoint list could not be refreshed", StringComparison.Ordinal)
                && result.Outcome.Contains("activity-log evidence could not be recorded", StringComparison.Ordinal)
                && !result.Outcome.Contains(directory, StringComparison.OrdinalIgnoreCase)
                && !result.Outcome.Contains("private refresh path", StringComparison.Ordinal),
                "Saved outcome must retain separate bounded refresh and audit warnings without raw exception details");
        });
    }

    static void SavedStateSessionCopyRetainsCommittedOutcome()
    {
        WithSavedCommitFixture((store, unavailableEvents, _) =>
        {
            var consumed = false;
            var refreshed = false;
            var result = SavedStateWorkflowCoordinator.CreateSessionCopyAndReportAsync(
                store, unavailableEvents, "default", "saved-copy", () => consumed = true,
                (sessionId, token) =>
                {
                    refreshed = sessionId == "saved-copy" && !token.CanBeCanceled;
                    throw new IOException("session picker fixture failed");
                }).GetAwaiter().GetResult();
            var source = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
            var copy = store.LoadSnapshotAsync("saved-copy").GetAwaiter().GetResult();
            Require(consumed && refreshed && !result.EventRecorded && copy is not null
                && copy.SessionInstanceId != source!.SessionInstanceId,
                "A secondary failure must preserve the new session and its distinct stable identity");
            Require(result.Outcome.StartsWith("Saved session: saved-copy.", StringComparison.Ordinal)
                && result.Outcome.Contains("session list could not be refreshed", StringComparison.Ordinal)
                && result.Outcome.Contains("activity-log evidence could not be recorded", StringComparison.Ordinal),
                "Session creation must remain reported as committed when both secondary steps fail");
        });
    }

    static void SavedStateTemplateMutationsRetainCommittedOutcome()
    {
        WithSavedCommitFixture((store, unavailableEvents, directory) =>
        {
            var templates = new ScenarioTemplateStore(Path.Combine(directory, "templates.json"));
            var consumed = 0;
            var saveRefresh = 0;
            var saved = SavedStateWorkflowCoordinator.SaveTemplateAndReportAsync(
                store, unavailableEvents, templates, "default", "Committed template", () => consumed++,
                (templateId, token) =>
                {
                    saveRefresh++;
                    Require(!token.CanBeCanceled && templates.Load().Any(item => item.Id == templateId),
                        "Template refresh must see the committed template before audit evidence is attempted");
                    return Task.CompletedTask;
                }).GetAwaiter().GetResult();
            Require(consumed == 1 && saveRefresh == 1 && !saved.EventRecorded
                && saved.Outcome.StartsWith("Saved template:", StringComparison.Ordinal),
                "Template save must consume its draft and refresh despite unavailable evidence");
            var deleteRefresh = 0;
            var deleted = SavedStateWorkflowCoordinator.DeleteTemplateAndReportAsync(
                templates, unavailableEvents, "default", saved.Template,
                (_, token) =>
                {
                    deleteRefresh++;
                    Require(!token.CanBeCanceled && templates.Load().All(item => item.Id != saved.Template.Id),
                        "Template delete refresh must observe durable removal");
                    throw new IOException("template picker fixture failed");
                }).GetAwaiter().GetResult();
            Require(deleted.Deleted && !deleted.EventRecorded && deleteRefresh == 1
                && deleted.Outcome.StartsWith("Deleted template:", StringComparison.Ordinal)
                && deleted.Outcome.Contains("template list could not be refreshed", StringComparison.Ordinal)
                && deleted.Outcome.Contains("activity-log evidence could not be recorded", StringComparison.Ordinal),
                "Committed template deletion must survive independent refresh and event failures");
        });
    }

    static void SavedStatePostCommitCompletionIgnoresCallerCancellation()
    {
        WithSavedCommitFixture((store, unavailableEvents, directory) =>
        {
            var events = EventLogStore.ForSessionStore(store);
            using var cancellation = new CancellationTokenSource();
            var refreshed = false;
            var result = SavedStateWorkflowCoordinator.SaveCheckpointAndReportAsync(
                store, events, "default", "Cancel after commit", cancellation.Cancel,
                (_, token) =>
                {
                    refreshed = true;
                    Require(cancellation.IsCancellationRequested && !token.CanBeCanceled,
                        "Cancellation after a save commits must not cancel required completion work");
                    return Task.CompletedTask;
                }, cancellation.Token).GetAwaiter().GetResult();
            Require(refreshed && result.EventRecorded && File.Exists(result.Checkpoint.Path)
                && !result.Outcome.Contains("Warning:", StringComparison.Ordinal),
                "A completed save must retain its successful outcome, refresh and audit even after the caller stops waiting");
        });
    }

    static void SavedStateFailedCommitSkipsSecondaryCompletion()
    {
        WithSavedCommitFixture((store, unavailableEvents, _) =>
        {
            var consumed = false;
            var refreshed = false;
            var rejected = false;
            try
            {
                SavedStateWorkflowCoordinator.SaveCheckpointAndReportAsync(
                    store, unavailableEvents, "missing", "Uncommitted draft", () => consumed = true,
                    (_, _) => { refreshed = true; return Task.CompletedTask; }).GetAwaiter().GetResult();

            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Require(rejected && !consumed && !refreshed, "A failed primary save must preserve its draft and skip success-side completion work");
        });
    }

    private static void WithSavedCommitFixture(Action<SessionStore, EventLogStore, string> action)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-wpf-commit-report-tests"));
        var directory = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SessionStore(directory);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "default").GetAwaiter().GetResult();
            // Evidence has no live session in this isolated root and fails immediately, without locking real files.
            var unavailableEvents = new EventLogStore(Path.Combine(directory, "unavailable-evidence"), requireExistingSession: true);
            action(store, unavailableEvents, directory);
        }
        finally
        {
            if (!directory.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Commit-report fixture cleanup escaped its owned test directory.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
