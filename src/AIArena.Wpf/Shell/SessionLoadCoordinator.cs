using AIArena.Core.Models;

namespace AIArena.Wpf;

internal enum SessionLoadKind { Initialization, Selection, Refresh }

internal sealed record SessionSelectionOutcome(bool Loaded, string ErrorCode, string Message);

/// <summary>Owns read/projection causality. Start a lease before any session-list or snapshot await.</summary>
internal sealed class SessionLoadCoordinator : IDisposable
{
    private readonly object gate = new();
    private SessionLoadLease? active;
    private long generation;
    private string? selectedSessionId;
    private bool disposed;

    internal static SessionSummary? ResolveSession(
        IReadOnlyList<SessionSummary> sessions,
        string? requestedSessionId,
        string? activeSessionId,
        string? lastSessionId)
    {
        SessionSummary? Find(string? id) => string.IsNullOrWhiteSpace(id)
            ? null
            : sessions.FirstOrDefault(session => session.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

        return Find(requestedSessionId)
            ?? Find(activeSessionId)
            ?? Find(lastSessionId)
            ?? Find("default")
            ?? sessions.FirstOrDefault();
    }

    public bool CanInitialize { get { lock (gate) return !disposed && generation == 0; } }
    public bool IsSelectionPending { get { lock (gate) return active?.Kind == SessionLoadKind.Selection; } }
    public string? SelectedSessionId { get { lock (gate) return selectedSessionId; } }

    public SessionLoadLease BeginSelection(string? requestedSessionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionLoadLease? previous;
        SessionLoadLease current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = active;
            current = active = new(this, ++generation, SessionLoadKind.Selection, requestedSessionId, cancellationToken);
        }
        // Cancellation callbacks may re-enter this owner. Never invoke them while holding its lock.
        previous?.Cancel();
        return current;
    }

    public SessionLoadLease? TryBeginInitialization(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed || generation != 0) return null;
            return active = new(this, ++generation, SessionLoadKind.Initialization, null, cancellationToken);
        }
    }

    public SessionLoadLease? TryBeginRefresh(string? expectedSessionId, CancellationToken cancellationToken = default,
        bool supersedeRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionLoadLease? previous;
        SessionLoadLease current;
        lock (gate)
        {
            if (disposed || !string.Equals(expectedSessionId, selectedSessionId, StringComparison.Ordinal)
                || active is not null && (!supersedeRefresh || active.Kind != SessionLoadKind.Refresh)) return null;
            previous = active;
            current = active = new(this, ++generation, SessionLoadKind.Refresh, expectedSessionId, cancellationToken);
        }
        previous?.Cancel();
        return current;
    }
    internal bool IsCurrent(SessionLoadLease lease)
    {
        lock (gate) return CurrentUnderLock(lease);
    }

    internal bool TryApply(SessionLoadLease lease, string? resolvedSessionId, Action apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (gate)
        {
            if (!CurrentUnderLock(lease)) return false;
            // A background rescan may resolve a fallback when the selected session was removed.
            // Its lease must still own the original selection before that fallback is applied.
            selectedSessionId = resolvedSessionId;
            apply();
            return true;
        }
    }

    internal void Complete(SessionLoadLease lease)
    {
        lock (gate)
        {
            if (ReferenceEquals(active, lease)) active = null;
        }
    }

    private bool CurrentUnderLock(SessionLoadLease lease) => !disposed && !lease.IsDisposed
        && !lease.Token.IsCancellationRequested && ReferenceEquals(active, lease) && generation == lease.Generation;

    public void Dispose()
    {
        SessionLoadLease? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            previous = active;
            active = null;
        }
        // Each asynchronous caller owns disposal of its source after it has unwound.
        previous?.Cancel();
    }
}

internal sealed class SessionLoadLease : IDisposable
{
    private readonly SessionLoadCoordinator owner;
    private readonly CancellationTokenSource cancellation;
    private int disposed;

    internal SessionLoadLease(SessionLoadCoordinator owner, long generation, SessionLoadKind kind,
        string? requestedSessionId, CancellationToken callerCancellation)
    {
        this.owner = owner;
        Generation = generation;
        Kind = kind;
        RequestedSessionId = requestedSessionId;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        Token = cancellation.Token;
    }

    public long Generation { get; }
    public SessionLoadKind Kind { get; }
    public string? RequestedSessionId { get; }
    public CancellationToken Token { get; }
    public bool IsCurrent => owner.IsCurrent(this);
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>Use for success and failure alike; callbacks are synchronous UI projection only.</summary>
    public bool TryApply(string? resolvedSessionId, Action apply) => owner.TryApply(this, resolvedSessionId, apply);

    internal void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A failed callback must not revive an obsolete load. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Complete(this);
        Cancel();
        cancellation.Dispose();
    }
}
