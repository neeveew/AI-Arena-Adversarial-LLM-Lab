namespace AIArena.Core.Persistence;

/// <summary>Bounds optional diagnostic writes for one causal operation without changing its save or cancellation outcome.</summary>
internal sealed class ActivityLogScope
{
    internal static readonly TimeSpan WriteWait = TimeSpan.FromMilliseconds(500);
    private bool incomplete;

    internal string SavedWarning => incomplete ? PostCommitEvidence.ActivityLogWarning : "";

    internal async Task AppendAsync(EventLogStore store, string sessionId, string type, object payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // One unavailable log must not add the same wait to every fallback or tool step.
        if (incomplete) return;
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? write = null;
        try
        {
            write = store.AppendAsync(sessionId, type, payload, writeCancellation.Token);
            await write.WaitAsync(WriteWait, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { writeCancellation.Cancel(); }
            catch (AggregateException) { /* The diagnostic observer is optional too. */ }
            if (write is not null) _ = ObserveCompletionAsync(write);
            cancellationToken.ThrowIfCancellationRequested();
            incomplete = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ObserveCompletionAsync(Task write)
    {
        // A write already inside its atomic mutation may finish later. Observe it once; never replay it.
        try { await write.ConfigureAwait(false); }
        catch { }
    }
}
