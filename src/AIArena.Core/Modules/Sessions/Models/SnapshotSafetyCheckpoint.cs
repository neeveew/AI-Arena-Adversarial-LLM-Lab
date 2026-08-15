namespace AIArena.Core.Persistence;

/// <summary>
/// Identifies a whole-snapshot replacement that must first preserve the exact
/// authoritative state in an automatic checkpoint.
/// </summary>
public enum SnapshotSafetyCheckpointOperation
{
    ArenaReset,
    CheckpointRestore,
    TemplateApply
}

/// <summary>
/// Durable evidence that a destructive snapshot replacement first committed a
/// recoverable checkpoint of the revision it superseded.
/// </summary>
public sealed record SnapshotSafetyCheckpointReceipt(
    CheckpointSummary Checkpoint,
    SnapshotSafetyCheckpointOperation Operation,
    string Subject,
    long ProtectedRevision,
    long ReplacementRevision);

/// <summary>
/// Identifies the selected checkpoint that was loaded and, when live state was
/// superseded, the automatic checkpoint created first. Snapshot-less legacy
/// recovery has no prior live state to protect, so <see cref="SafetyCheckpoint"/>
/// is null for that compatibility path.
/// </summary>
public sealed record CheckpointRestoreWithSafetyResult(
    CheckpointSummary RestoredCheckpoint,
    SnapshotSafetyCheckpointReceipt? SafetyCheckpoint);
