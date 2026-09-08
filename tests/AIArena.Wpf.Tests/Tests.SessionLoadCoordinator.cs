using System.IO;
using System.Text;
using AIArena.Wpf;

internal static partial class Program
{
    static void SessionLoadSelectionRejectsLateSuccessAndFailure()
    {
        using var owner = new SessionLoadCoordinator();
        using var initialization = owner.TryBeginInitialization();
        Require(initialization is { Kind: SessionLoadKind.Initialization, IsCurrent: true }, "Startup must acquire ownership before awaiting session enumeration");
        using var selected = owner.BeginSelection("selected");
        Require(initialization!.Token.IsCancellationRequested && !initialization.IsCurrent,
            "Explicit selection must invalidate and cancel a pending startup read");
        var projected = "unchanged";
        Require(selected.TryApply("selected", () => projected = "selected"), "The latest explicit selection must publish");
        Require(!initialization.TryApply("old-default", () => projected = "stale success")
            && !initialization.TryApply("old-default", () => projected = "stale failure")
            && projected == "selected", "Late startup success and failure must both leave the chosen session intact");
        Require(owner.TryBeginInitialization() is null, "Completed startup cannot reacquire permission to select an old default");
    }

    static void SessionLoadRefreshCannotSupersedeSelection()
    {
        using var owner = new SessionLoadCoordinator();
        using (var first = owner.BeginSelection("a")) first.TryApply("a", () => { });
        using var refresh = owner.TryBeginRefresh("a");
        Require(refresh is { Kind: SessionLoadKind.Refresh, IsCurrent: true }, "Idle refresh must use the selected session's identity");
        using var selected = owner.BeginSelection("b");
        Require(owner.IsSelectionPending && selected.RequestedSessionId == "b", "Selection intent must be observable before snapshot loading completes");
        Require(owner.TryBeginRefresh("a") is null && owner.TryBeginRefresh("b") is null
            && owner.TryBeginRefresh("a", supersedeRefresh: true) is null
            && owner.TryBeginRefresh("b", supersedeRefresh: true) is null,
            "Timer ticks and mutation completions must not supersede a pending user selection");
        refresh!.Dispose();
        Require(selected.IsCurrent, "An older request's cleanup must not clear the new request's ownership");
        var published = "a";
        Require(selected.TryApply("b", () => published = "b")
            && !refresh.TryApply("a", () => published = "a") && published == "b",
            "A slow refresh must not restore the former session after the newer selection commits");
        selected.Dispose();
        Require(owner.TryBeginRefresh("a") is null, "A queued timer using the former selected identity must be rejected");
        using var fresh = owner.TryBeginRefresh("b");
        Require(fresh is { IsCurrent: true }, "The new session becomes refreshable after its selection completes");
        using var mutationRefresh = owner.TryBeginRefresh("b", supersedeRefresh: true);
        Require(mutationRefresh is { IsCurrent: true } && fresh!.Token.IsCancellationRequested,
            "Mutation completion may replace a periodic read that began before its snapshot commit");
        Require(!fresh!.TryApply("b", () => throw new InvalidOperationException()),
            "The replaced periodic read must not restore its older snapshot after mutation refresh begins");
    }

    static void SessionLoadCancellationAndDisposalRejectProjection()
    {
        using var owner = new SessionLoadCoordinator();
        using var caller = new CancellationTokenSource();
        using var canceled = owner.BeginSelection("a", caller.Token);
        caller.Cancel();
        Require(!canceled.IsCurrent && !canceled.TryApply("a", () => throw new InvalidOperationException()),
            "Caller cancellation must block both result and error projection");
        using var replacement = owner.BeginSelection("b");
        using var failedCallback = replacement.Token.Register(() => throw new InvalidOperationException("fixture callback"));
        using var newest = owner.BeginSelection("c");
        Require(newest.IsCurrent && replacement.Token.IsCancellationRequested,
            "A throwing old cancellation callback must not prevent a new selection from owning the UI");
        owner.Dispose();
        Require(newest.Token.IsCancellationRequested && !newest.TryApply("c", () => throw new InvalidOperationException()),
            "Disposal must cancel pending work and reject every late projection");
        Require(owner.TryBeginRefresh(null) is null && owner.TryBeginInitialization() is null,
            "Disposal must not admit new background work");
    }

    static void SessionLoadScanLeaseCoversEnumerationAndFallback()
    {
        using var owner = new SessionLoadCoordinator();
        using (var initial = owner.BeginSelection("removed")) initial.TryApply("removed", () => { });
        using var scan = owner.TryBeginRefresh("removed");
        Require(scan is not null && scan.TryApply("fallback", () => { }),
            "A current rescan may select a fallback when the selected session no longer exists");
        scan!.Dispose();
        using var oldScan = owner.TryBeginRefresh("fallback");
        using var explicitSelection = owner.BeginSelection("chosen");
        Require(!oldScan!.TryApply("old-default", () => throw new InvalidOperationException()),
            "An asynchronous rescan must keep its original lease through enumeration instead of starting a late selection");
        Require(explicitSelection.IsCurrent, "Discarding a stale rescan must retain the user's pending selection");
    }

    static void SnapshotStampIgnoresDirectoryChangesAndDetectsRewrites()
    {
        WithSessionStampFixture((directory, snapshotPath) =>
        {
            var reader = new SnapshotStampReader(forceContentHashGeneration: true);
            File.WriteAllText(snapshotPath, "alpha", new UTF8Encoding(false));
            var fixedWrite = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(snapshotPath, fixedWrite);
            var original = reader.Capture(snapshotPath);
            Require(original is not null && original.Value.LastWriteTimeUtc.UtcDateTime == fixedWrite,
                "The stamp must represent the snapshot file's UTC timestamp");
            File.WriteAllText(Path.Combine(directory, "auxiliary.txt"), "side artifact", new UTF8Encoding(false));
            Directory.SetLastWriteTimeUtc(directory, fixedWrite.AddDays(1));
            Require(reader.Capture(snapshotPath) == original,
                "Unrelated session-directory activity must not trigger repeated snapshot reloads");
            File.WriteAllText(snapshotPath, "bravo", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(snapshotPath, fixedWrite);
            var rewritten = reader.Capture(snapshotPath);
            Require(rewritten is not null && rewritten != original && rewritten.Value.Length == original!.Value.Length
                && rewritten.Value.LastWriteTimeUtc == original.Value.LastWriteTimeUtc,
                "Same-length rewrites with preserved modification time must still invalidate the file generation");
            var processGeneration = 3L;
            var beforeMutation = reader.Capture(snapshotPath, () => processGeneration);
            processGeneration++;
            Require(reader.Capture(snapshotPath, () => processGeneration) != beforeMutation,
                "A committed in-process snapshot mutation must invalidate cached evidence even when file metadata collides");
        });
    }

    static void SnapshotStampStableReadRetriesChangesAndRejectsUnstableInput()
    {
        WithSessionStampFixture((directory, snapshotPath) =>
        {
            var reader = new SnapshotStampReader(forceContentHashGeneration: true);
            File.WriteAllText(snapshotPath, "alpha", new UTF8Encoding(false));
            var reads = 0;
            var result = reader.ReadStableAsync(snapshotPath, _ =>
            {
                var content = File.ReadAllText(snapshotPath);
                if (++reads == 1) File.WriteAllText(snapshotPath, "bravo", new UTF8Encoding(false));
                return Task.FromResult(content);
            }).GetAwaiter().GetResult();
            Require(reads == 2 && result.Value == "bravo" && result.Stamp == reader.Capture(snapshotPath),
                "A rewrite during deserialization must trigger one bounded reload and pair the value with its own file stamp");
            reads = 0;
            try
            {
                reader.ReadStableAsync(snapshotPath, _ =>
                {
                    var content = File.ReadAllText(snapshotPath);
                    File.WriteAllText(snapshotPath, ++reads % 2 == 0 ? "bravo" : "alpha", new UTF8Encoding(false));
                    return Task.FromResult(content);
                }).GetAwaiter().GetResult();
                throw new InvalidOperationException("Continuously changing input must not return a stamped snapshot.");
            }
            catch (IOException)
            {
                Require(reads == 2, "Unstable input must stop after two attempts, not create an unbounded read loop");
            }
        });
    }

    static void SnapshotStampRetainsHashDiagnosticsAndCancellation()
    {
        WithSessionStampFixture((directory, snapshotPath) =>
        {
            File.WriteAllText(snapshotPath, new string('x', 150_000), new UTF8Encoding(false));
            using var cancellation = new CancellationTokenSource();
            var chunks = 0;
            var reader = new SnapshotStampReader(forceContentHashGeneration: true, hashChunkObserved: count =>
            {
                chunks = count;
                cancellation.Cancel();
            });
            try
            {
                reader.Capture(snapshotPath, cancellationToken: cancellation.Token);
                throw new InvalidOperationException("Canceled hashing must not return an authoritative stamp.");
            }
            catch (OperationCanceledException)
            {
                Require(chunks == 1, "Shared hashing must retain bounded chunk diagnostics and cooperative cancellation");
            }
            var now = DateTimeOffset.UtcNow;
            Require(SnapshotStampReader.ShouldHashRecentNativeChangeTime(now.UtcDateTime.ToFileTimeUtc(), now)
                && !SnapshotStampReader.ShouldHashRecentNativeChangeTime(now.AddSeconds(-3).UtcDateTime.ToFileTimeUtc(), now)
                && SnapshotStampReader.ShouldHashRecentNativeChangeTime(0, now),
                "Recent native changes and unavailable native timestamps must retain content-hash protection");
        });
    }

    private static void WithSessionStampFixture(Action<string, string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aiarena-session-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshotPath = Path.Combine(directory, "snapshot.json");
        var auxiliaryPath = Path.Combine(directory, "auxiliary.txt");
        try { action(directory, snapshotPath); }
        finally
        {
            File.Delete(snapshotPath);
            File.Delete(auxiliaryPath);
            Directory.Delete(directory);
        }
    }
}
