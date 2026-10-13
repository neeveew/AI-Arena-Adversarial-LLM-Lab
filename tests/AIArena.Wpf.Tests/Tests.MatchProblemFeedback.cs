using System.Reflection;
using AIArena.Wpf;

internal static partial class Program
{
    private static readonly string[] MatchProblemKinds = ["voice", "pressure", "lock", "text", "color"];

    static void MatchProblemFeedbackIgnoresOldMissingSnapshots() => RunMatchProblemFeedbackCases("snapshot", switched: true);
    static void MatchProblemFeedbackIgnoresOldMissingRoles() => RunMatchProblemFeedbackCases("role", switched: true);
    static void MatchProblemFeedbackRetainsCurrentErrors()
    {
        RunMatchProblemFeedbackCases("snapshot", switched: false);
        RunMatchProblemFeedbackCases("role", switched: false);
    }

    private static void RunMatchProblemFeedbackCases(string problem, bool switched)
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var wrong = new List<string>();
            foreach (var kind in MatchProblemKinds.Where(kind => problem != "role" || kind != "lock"))
            {
                using var fixture = new MatchControlOwnershipFixture();
                RunPausedMatchProblem(fixture, kind, problem, switched);
                Require(fixture.State.SaveAttempts == 0 && fixture.State.Refreshes.Count == 0
                        && PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync("other"))!.PersistenceRevision == fixture.OtherRevision,
                    "An unavailable snapshot or role must not save or refresh any mutation.");
                var statuses = fixture.State.Statuses.Where(status => status != "New session ready").ToArray();
                if (fixture.State.Errors.Count != 0 || (switched ? statuses.Length != 0 : statuses.Length == 0)) wrong.Add(kind);
            }
            Require(wrong.Count == 0, $"Wrong {problem} feedback ownership (switched {switched}): {string.Join(", ", wrong)}.");
        }));
    }

    static void MatchProblemFeedbackContainsIndependentViewFailures()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            foreach (var failBoth in new[] { false, true })
            {
                using var fixture = new MatchControlOwnershipFixture();
                fixture.State.LoadStatusFails = true;
                fixture.State.ArenaStatusFails = failBoth;
                RunPausedMatchProblem(fixture, "voice", "role", switched: false);
                Require(fixture.State.Errors.Count == 0 && fixture.State.LoadStatusAttempts == 1 && fixture.State.ArenaStatusAttempts == 1,
                    "Each current status view must be attempted independently without turning presentation failure into operation failure.");
            }
        }));
    }

    static void MatchProblemFeedbackRevalidatesBetweenStatusViews()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new MatchControlOwnershipFixture();
            fixture.State.AfterLoadStatus = () => fixture.State.Active = fixture.Other;
            RunPausedMatchProblem(fixture, "voice", "role", switched: false);
            Require(fixture.State.LoadStatusAttempts == 1 && fixture.State.ArenaStatusAttempts == 0,
                "A session change inside the first feedback callback must suppress the obsolete second view update.");
        }));
    }

    private static void RunPausedMatchProblem(MatchControlOwnershipFixture fixture, string kind, string problem, bool switched)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.BeforeOperationAsync = () => { entered.TrySetResult(); return release.Task; };
        Task? invoked = null;
        try
        {
            if (kind == "text" || kind == "color")
            {
                var method = typeof(MatchLockCoordinator).GetMethod(kind == "text" ? "UpdateMatchTextAsync" : "UpdateAccentColorAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                invoked = (Task)method.Invoke(fixture.State.Coordinator,
                    [fixture.State.Active, "alpha", kind == "text" ? "Edited role" : "#123456"])!;
            }
            else fixture.StartChange(kind);
            PumpDocumentImportTask(entered.Task);
            if (problem == "snapshot") fixture.State.RemoveSnapshot();
            else
            {
                var snapshot = PumpDocumentImportTask(fixture.State.Store.LoadSnapshotAsync())!;
                snapshot.Engine.Agents.RemoveAll(agent => agent.Id == "alpha");
                PumpDocumentImportTask(fixture.State.Store.SaveSnapshotAsync(snapshot));
            }
            if (switched)
            {
                fixture.State.Active = fixture.Other;
                fixture.State.Coordinator.ClearControls();
                fixture.Host.Content = fixture.CreateCard();
                fixture.Host.UpdateLayout();
                fixture.State.Statuses.Add("New session ready");
            }
        }
        finally
        {
            release.TrySetResult();
            PumpDocumentImportTask(invoked ?? fixture.State.LastOperation);
            PumpLifecycleUi();
        }
    }
}
