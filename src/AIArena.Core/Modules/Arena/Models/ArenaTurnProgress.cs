namespace AIArena.Core.Models;

public enum ArenaTurnProgressKind
{
    Started,
    Delta,
    Completed,
    Interrupted,
    Activity
}

/// <summary>Observed work only; no reasoning, prompt, or tool contents are exposed.</summary>
public enum ArenaTurnActivity
{
    Loading,
    ReadingContext,
    Thinking,
    Writing,
    RetryingWithReducedReasoning
}

/// <summary>Ephemeral public response progress; only Completed identifies a committed transcript message.</summary>
public sealed record ArenaTurnProgress(
    string SessionId,
    string SessionInstanceId,
    string OperationId,
    string AttemptId,
    string SpeakerId,
    string SpeakerName,
    string Model,
    int Turn,
    ArenaTurnProgressKind Kind,
    string Text = "",
    string MessageId = "",
    string Status = "",
    double CreatedAt = 0,
    ArenaTurnActivity? Activity = null);
