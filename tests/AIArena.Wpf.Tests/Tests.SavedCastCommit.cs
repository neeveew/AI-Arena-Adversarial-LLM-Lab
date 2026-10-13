using System.Windows.Controls;
using AIArena.Core.Persistence;
using AIArena.Wpf;

internal static partial class Program
{
    static void SavedCastResultsSurviveEvidenceAndRefreshFailures() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var kind in new[] { "roster", "matrix" })
        foreach (var fault in new[] { "healthy", "log", "refresh", "both" })
        {
            var result = RunSavedCastCase(kind, fault);
            var warningsExpected = fault != "healthy";
            if (!result.Ok || result.ErrorCode.Length != 0 || !result.Committed || result.BusyErrors != 0
                || result.Refreshes != 1 || warningsExpected != result.Message.Contains("Warning:", StringComparison.Ordinal)
                || result.Message.Contains("PRIVATE_COMMIT_FAULT", StringComparison.Ordinal))
                failures.Add(kind + "/" + fault);
        }
        Require(failures.Count == 0, "Saved cast changes were hidden or reported as failed after secondary faults: " + string.Join(", ", failures));
    });

    static void SavedCastResultsRetainCommittedCancellation() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var kind in new[] { "roster", "matrix" })
        {
            var result = RunSavedCastCase(kind, "cancel");
            if (!result.Ok || !result.Committed || result.BusyErrors != 0 || result.RefreshTokenCanceled)
                failures.Add(kind);
        }
        Require(failures.Count == 0, "Late refresh cancellation erased a saved cast change or inherited the completed caller token: " + string.Join(", ", failures));
    });

    static void SavedCastResultsKeepTheirOriginAfterSessionSwitch() => RunStaTest(() =>
    {
        var failures = new List<string>();
        foreach (var kind in new[] { "roster", "matrix" })
        {
            var result = RunSavedCastCase(kind, "switch");
            if (!result.Ok || !result.Committed || result.ResultSession != "default" || result.BusyErrors != 0
                || result.Feedback.Length != 0 || !result.OtherSessionUnchanged)
                failures.Add(kind);
        }
        Require(failures.Count == 0, "A refresh-time session switch retargeted or concealed the committed result: " + string.Join(", ", failures));
    });

    static void SavedCastFeedbackFailureCannotEraseSuccess() => RunStaTest(() =>
    {
        var result = RunSavedCastCase("roster", "feedback");
        Require(result.Ok && result.Committed && result.BusyErrors == 0 && result.Refreshes == 1
                && result.Message.Contains("Warning:", StringComparison.Ordinal)
                && !result.Message.Contains("PRIVATE_COMMIT_FAULT", StringComparison.Ordinal),
            "Optional roster feedback failure escaped the saved-result completion boundary.");
    });

    static void SavedCastAuthoritativeFailuresRemainFailures() => RunStaTest(() =>
    {
        foreach (var kind in new[] { "roster", "matrix" })
        {
            var result = RunSavedCastCase(kind, "save");
            Require(!result.Ok && !result.Committed && result.Refreshes == 0 && result.BusyErrors == 1
                    && !result.Message.Contains("saved", StringComparison.OrdinalIgnoreCase),
                kind + ": a failed authoritative operation was converted into a saved result.");
        }
    });

    private sealed record SavedCastObservation(bool Ok, string ErrorCode, string Message, bool Committed,
        int Refreshes, int BusyErrors, string[] Feedback, bool RefreshTokenCanceled, string ResultSession,
        bool OtherSessionUnchanged);

    private static SavedCastObservation RunSavedCastCase(string kind, string fault)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-cast-commit-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        Require(root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Cast fixture cleanup escaped its parent.");
        try
        {
            var store = new SessionStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            AIArena.Core.Models.SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var log = EventLogStore.ForSessionStore(store);
            using var caller = new CancellationTokenSource();
            using var operationCancellation = new CancellationTokenSource();
            var refreshes = 0;
            var busyErrors = 0;
            var refreshTokenCanceled = false;
            var feedback = new List<string>();
            FileStream? held = null;
            if (fault is "log" or "both")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log.EventPath())!);
                held = new FileStream(log.EventPath(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }

            async Task Run(Func<Task> action)
            {
                try { await action(); }
                catch { busyErrors++; }
            }

            async Task Refresh(string outcome, CancellationToken token)
            {
                refreshes++;
                if (fault == "cancel")
                {
                    caller.Cancel();
                    refreshTokenCanceled = token.IsCancellationRequested;
                    token.ThrowIfCancellationRequested();
                    if (kind == "roster") throw new OperationCanceledException(caller.Token);
                }
                if (fault == "switch")
                {
                    await store.CreateSessionAsync("other", SessionStore.CreateDefaultSnapshot());
                    active = (await store.ListSessionsAsync()).Single(session => session.Id == "other");
                }
                if (fault is "refresh" or "both" or "switch" or "feedback")
                    throw new IOException(@"C:\Users\PRIVATE_COMMIT_FAULT\refresh.txt");
                feedback.Add(outcome);
            }

            bool ok;
            string error;
            string message;
            string resultSession;
            try
            {
                if (kind == "roster")
                {
                    var coordinator = new AgentRosterCoordinator(store, log, new ComboBox(), new ComboBox(), new Button(), new TextBlock(),
                        () => false, () => false, () => active, _ => Task.CompletedTask,
                        (_, _, action, _) => Run(action),
                        (snapshot, sessionId) => fault == "save"
                            ? Task.FromException(new IOException(@"C:\Users\PRIVATE_COMMIT_FAULT\snapshot.txt"))
                            : store.SaveSnapshotAsync(snapshot, sessionId),
                        outcome => Refresh(outcome, CancellationToken.None),
                        outcome =>
                        {
                            if (fault == "feedback") throw new IOException(@"C:\Users\PRIVATE_COMMIT_FAULT\feedback.txt");
                            feedback.Add(outcome);
                        });
                    var result = coordinator.ResizeAgentCountAsync(6).GetAwaiter().GetResult();
                    ok = result.Ok; error = result.ErrorCode; message = result.Message; resultSession = "default";
                }
                else
                {
                    var service = new RivalryMatrixControlService(store, log, () => active, () => false,
                        (_, action) =>
                        {
                            if (fault == "save") operationCancellation.Cancel();
                            return Run(() => action(operationCancellation.Token));
                        }, Refresh);
                    var result = service.ApplyPatternAsync("evidence_ladder", cancellationToken: caller.Token).GetAwaiter().GetResult();
                    ok = result.Ok; error = result.ErrorCode; message = result.Message; resultSession = result.State.SessionId;
                }
            }
            finally { held?.Dispose(); }
            var persisted = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            var committed = kind == "roster" ? persisted.Engine.Agents.Count(agent => agent.Active) == 6
                : persisted.Engine.RivalryMatrix.Enabled && persisted.Engine.RivalryMatrix.Links.Count == 4;
            var other = fault == "switch" ? store.LoadSnapshotAsync("other").GetAwaiter().GetResult() : null;
            var otherUnchanged = other is null || (!other.Engine.RivalryMatrix.Enabled && other.Engine.Agents.Count(agent => agent.Active) == 4);
            Console.WriteLine($"cast commit receipt: kind={kind}; fault={fault}; committed={committed}; returned_ok={ok}; refreshes={refreshes}; busy_errors={busyErrors}; feedback={feedback.Count}; refresh_token_canceled={refreshTokenCanceled}");
            return new SavedCastObservation(ok, error, message, committed, refreshes, busyErrors,
                feedback.ToArray(), refreshTokenCanceled, resultSession, otherUnchanged);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
