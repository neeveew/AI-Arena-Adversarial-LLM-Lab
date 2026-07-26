using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed record AIArenaRivalryMatrixControlLink(string Source, string Target, string Stance);

internal sealed record AIArenaRivalryMatrixControlState(
    string SessionId,
    bool Enabled,
    string Pattern,
    int ActiveAgents,
    IReadOnlyList<AIArenaRivalryMatrixControlLink> Links);

internal sealed record AIArenaRivalryMatrixControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaRivalryMatrixControlState State);

/// <summary>
/// Headless relationship-pattern boundary shared by PowerShell and Match Setup.
/// It owns validation and persistence; the host owns the normal busy/refresh UI path.
/// </summary>
internal sealed class RivalryMatrixControlService
{
    internal static readonly string[] Patterns =
    [
        "round_robin_challenge",
        "mutual_rivals",
        "evidence_ladder",
        "support_chain",
        "deescalation_ring",
        "devils_triangle",
        "skeptic_sweep",
        "paired_crossfire",
        "spotlight_defense",
        "off"
    ];

    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isBusy;
    private readonly Func<string, Func<CancellationToken, Task>, Task> runBusyAsync;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;

    public RivalryMatrixControlService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isBusy,
        Func<string, Func<CancellationToken, Task>, Task> runBusyAsync,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.activeSession = activeSession;
        this.isBusy = isBusy;
        this.runBusyAsync = runBusyAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
    }

    public async Task<AIArenaRivalryMatrixControlState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = activeSession()?.Id ?? "";
        var snapshot = string.IsNullOrWhiteSpace(sessionId)
            ? null
            : await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        return State(sessionId, snapshot, "custom");
    }

    public async Task<AIArenaRivalryMatrixControlResult> ApplyPatternAsync(
        string pattern,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePattern(pattern);
        if (normalized is null)
        {
            return Failure(
                "invalid_argument",
                $"Matrix pattern must be one of: {string.Join(", ", Patterns)}.",
                await CaptureAsync(cancellationToken));
        }

        if (isBusy())
        {
            return Failure("busy", "The arena is busy; the relationship matrix was not changed.", await CaptureAsync(cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return Failure("session_unavailable", "No active session is available for relationship changes.", await CaptureAsync(cancellationToken));
        }

        var completed = false;
        AIArenaRivalryMatrixControlState? finalState = null;
        await runBusyAsync("Applying relationship pattern...", async operationCancellationToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationCancellationToken);
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, linked.Token);
            if (snapshot is null)
            {
                return;
            }

            var activeIds = snapshot.Engine.Agents
                .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
                .Select(agent => agent.Id)
                .ToArray();
            var links = normalized == "off"
                ? []
                : MatchSetupCoordinator.BuildRivalryPatternDraft(normalized, activeIds);
            snapshot.Engine.RivalryMatrix.Enabled = enabled && normalized != "off";
            snapshot.Engine.RivalryMatrix.Links.Clear();
            snapshot.Engine.RivalryMatrix.Links.AddRange(links.Select(link => new RivalryLink
            {
                Source = link.Source,
                Target = link.Target,
                Stance = link.Stance
            }));

            await sessionStore.SaveSnapshotAsync(snapshot, session.Id, linked.Token);
            await eventLogStore.AppendAsync(session.Id, "native_rivalry_matrix_pattern_applied", new
            {
                pattern = normalized,
                snapshot.Engine.RivalryMatrix.Enabled,
                links = links.Select(link => new { link.Source, link.Target, link.Stance }).ToArray()
            }, linked.Token);
            finalState = State(session.Id, snapshot, normalized);
            await refreshActiveSessionAsync(MatrixSummary(finalState), linked.Token);
            completed = true;
        });

        if (!completed || finalState is null)
        {
            return Failure("operation_failed", "The relationship pattern did not complete.", await CaptureAsync(cancellationToken));
        }

        return new AIArenaRivalryMatrixControlResult(true, "", MatrixSummary(finalState), finalState);
    }

    private static string? NormalizePattern(string pattern)
    {
        var value = (pattern ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return Patterns.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : null;
    }

    private static AIArenaRivalryMatrixControlState State(string sessionId, ArenaSnapshot? snapshot, string pattern)
    {
        if (snapshot is null)
        {
            return new AIArenaRivalryMatrixControlState(sessionId, false, pattern, 0, []);
        }

        var activeIds = snapshot.Engine.Agents
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .Select(agent => agent.Id)
            .ToArray();
        var resolvedPattern = pattern.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? DetectPattern(snapshot, activeIds)
            : pattern;
        return new AIArenaRivalryMatrixControlState(
            sessionId,
            snapshot.Engine.RivalryMatrix.Enabled,
            resolvedPattern,
            activeIds.Length,
            snapshot.Engine.RivalryMatrix.Links
                .Select(link => new AIArenaRivalryMatrixControlLink(link.Source, link.Target, link.Stance))
                .ToArray());
    }

    private static string DetectPattern(ArenaSnapshot snapshot, IReadOnlyList<string> activeIds)
    {
        if (!snapshot.Engine.RivalryMatrix.Enabled && snapshot.Engine.RivalryMatrix.Links.Count == 0)
        {
            return "off";
        }

        var actual = snapshot.Engine.RivalryMatrix.Links
            .Select(link => new AIArenaRivalryMatrixControlLink(
                link.Source.Trim().ToLowerInvariant(),
                link.Target.Trim().ToLowerInvariant(),
                link.Stance.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_')))
            .ToArray();
        foreach (var candidate in Patterns.Where(item => item != "off"))
        {
            var expected = MatchSetupCoordinator.BuildRivalryPatternDraft(candidate, activeIds)
                .Select(link => new AIArenaRivalryMatrixControlLink(link.Source, link.Target, link.Stance))
                .ToArray();
            if (actual.SequenceEqual(expected))
            {
                return candidate;
            }
        }

        return "custom";
    }

    private static string MatrixSummary(AIArenaRivalryMatrixControlState state) => state.Enabled
        ? $"Relationship pattern {state.Pattern} applied with {state.Links.Count} link(s)."
        : "Relationship matrix disabled.";

    private static AIArenaRivalryMatrixControlResult Failure(
        string errorCode,
        string message,
        AIArenaRivalryMatrixControlState state) => new(false, errorCode, message, state);
}
