namespace AIArena.Core.Persistence;

/// <summary>
/// How much per-session work a listing should do. Ordered cheapest first so
/// callers can compare with &gt;=.
/// </summary>
public enum SessionListingDetail
{
    /// <summary>
    /// Identity and modification time only. Nothing is read from inside a
    /// session, so cost is one directory walk regardless of session size.
    /// </summary>
    Identity = 0,

    /// <summary>
    /// Adds the transcript message count, read by streaming the snapshot and
    /// cached against its write stamp.
    /// </summary>
    Messages = 1,

    /// <summary>
    /// Adds checkpoint and event-log counts. Each session costs a directory
    /// enumeration and a log read, so this is reserved for callers that display
    /// those numbers.
    /// </summary>
    Full = 2
}
