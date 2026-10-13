namespace AIArena.Core.Persistence;

/// <summary>Bounds optional diagnostic writes for one causal operation without changing its save or cancellation outcome.</summary>
internal sealed class ActivityLogScope
{
    private bool incomplete;

    internal string SavedWarning => incomplete ? PostCommitEvidence.ActivityLogWarning : "";

    internal async Task AppendAsync(EventLogStore store, string sessionId, string type, object payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // One unavailable log must not add the same wait to every fallback or tool step.
        if (incomplete) return;
        var evidenceError = await ActivityLogWrite.TryAppendAsync(store, sessionId, type, payload, cancellationToken)
            .ConfigureAwait(false);
        incomplete |= evidenceError is not null;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
