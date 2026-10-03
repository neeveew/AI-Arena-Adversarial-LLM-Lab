namespace AIArena.Core.Persistence;

/// <summary>
/// Contains secondary evidence and projection failures after an authoritative
/// commit. Callers retain their durable outcome and present the returned error
/// separately; raw exceptions must not be included in persisted/public results.
/// </summary>
public static class PostCommitEvidence
{
    public const string ActivityLogWarning =
        "Warning: the result was saved, but activity-log evidence could not be recorded.";

    public static Task<Exception?> TryAppendAsync(
        EventLogStore eventLogStore, string sessionId, string eventType, object payload)
    {
        ArgumentNullException.ThrowIfNull(eventLogStore);
        // Cancellation belonged to the transaction that already committed.
        // Required completion evidence now has its own bounded store lifetime.
        return TryCompleteAsync(() => eventLogStore.AppendAsync(
            sessionId, eventType, payload, CancellationToken.None));
    }

    public static async Task<Exception?> TryCompleteAsync(Func<Task> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        try
        {
            await completion().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public static string WarningFor(Exception? evidenceError) =>
        evidenceError is null ? "" : ActivityLogWarning;
}
