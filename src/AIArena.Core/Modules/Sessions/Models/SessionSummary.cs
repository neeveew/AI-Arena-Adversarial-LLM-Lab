namespace AIArena.Core.Models;

public sealed record SessionSummary(
    string Id,
    string SnapshotPath,
    bool HasSnapshot,
    int MessageCount,
    int CheckpointCount,
    int EventCount,
    DateTimeOffset LastModified)
{
    public override string ToString()
    {
        return HasSnapshot
            ? $"{Id} ({LastModified.ToLocalTime():dd/MM/yyyy HH:mm:ss})"
            : $"{Id} (no snapshot)";
    }
}
