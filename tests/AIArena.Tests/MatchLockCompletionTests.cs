using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;

internal static class MatchLockCompletionTests
{
    internal static void ReceiptsAndCancellationStayTruthful()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiarena-lock-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot()).GetAwaiter().GetResult();
            using var generation = new MatchGenerationService(sessionStore: store, eventLogStore: new EventLogStore(root));
            var result = generation.ToggleLockWithEvidenceAsync("default", " TOPIC ", true).GetAwaiter().GetResult();
            if (!result.Changed || !result.EventRecorded || result.Warning.Length != 0
                || !store.LoadSnapshotAsync().GetAwaiter().GetResult()!.MatchLocks.GetValueOrDefault("topic"))
                throw new InvalidOperationException("A healthy lock receipt must describe the normalized saved change and its recorded evidence.");
            var missing = generation.ToggleLockWithEvidenceAsync("missing", "topic", true).GetAwaiter().GetResult();
            if (missing.Changed || missing.EventRecorded || missing.Warning.Length != 0)
                throw new InvalidOperationException("A missing snapshot must not claim a saved lock change.");
            var before = store.LoadSnapshotAsync().GetAwaiter().GetResult()!.PersistenceRevision;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = false;
            try { generation.ToggleLockWithEvidenceAsync("default", "topic", false, cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            var after = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            if (!canceled || before != after.PersistenceRevision || !after.MatchLocks.GetValueOrDefault("topic"))
                throw new InvalidOperationException("Pre-commit cancellation must retain the original lock and snapshot revision.");
            using var unavailable = new MatchGenerationService(sessionStore: store,
                eventLogStore: new EventLogStore(Path.Combine(root, "missing-evidence"), requireExistingSession: true));
            var warning = unavailable.ToggleLockWithEvidenceAsync("default", "topic", false).GetAwaiter().GetResult();
            if (!warning.Changed || warning.EventRecorded || !warning.Warning.Contains("saved", StringComparison.OrdinalIgnoreCase)
                || System.Text.Json.JsonSerializer.Serialize(warning).Contains(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A saved-with-warning lock receipt must remain privacy-safe.");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Lock receipt cleanup escaped its temporary parent.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static void SurvivesOptionalEvidenceFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiarena-lock-completion-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot()).GetAwaiter().GetResult();
            var events = new EventLogStore(Path.Combine(root, "missing-events"), requireExistingSession: true);
            using var generation = new MatchGenerationService(sessionStore: store, eventLogStore: events);
            Exception? escaped = null;
            try { generation.ToggleLockAsync("default", "topic", true).GetAwaiter().GetResult(); }
            catch (Exception exception) { escaped = exception; }
            var saved = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            if (escaped is not null || !saved.MatchLocks.GetValueOrDefault("topic"))
                throw new InvalidOperationException("A saved lock change was misreported when optional activity evidence failed.");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Lock fixture cleanup escaped its temporary parent.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
