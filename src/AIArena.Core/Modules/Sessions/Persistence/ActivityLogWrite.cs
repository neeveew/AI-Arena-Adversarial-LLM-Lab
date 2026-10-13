namespace AIArena.Core.Persistence;

/// <summary>Shares the optional log wait between causal workflows and already-saved outcomes.</summary>
internal static class ActivityLogWrite
{
    internal static readonly TimeSpan WriteWait = TimeSpan.FromMilliseconds(500);

    internal static async Task<Exception?> TryAppendAsync(EventLogStore store, string sessionId, string type,
        object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? write = null;
        try
        {
            write = store.AppendAsync(sessionId, type, payload, writeCancellation.Token);
            await write.WaitAsync(WriteWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try { writeCancellation.Cancel(); }
            catch (AggregateException) { /* Diagnostic cancellation observers are optional too. */ }
            if (write is not null) _ = ObserveCompletionAsync(write);
            cancellationToken.ThrowIfCancellationRequested();
            return exception;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task ObserveCompletionAsync(Task write)
    {
        // A write already inside its atomic mutation may finish later. Observe it once; never replay it.
        try { await write.ConfigureAwait(false); }
        catch { }
    }
}
