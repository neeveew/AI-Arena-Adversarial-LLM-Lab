namespace AIArena.Core.Models;

/// <summary>Provider-observed work; activity never carries prompt, reasoning, or tool contents.</summary>
public enum ModelProviderActivityStage
{
    Loading,
    ReadingContext,
    Thinking,
    Writing
}

/// <summary>Character counts are cumulative for this request and progress is optional in the range 0..1.</summary>
public sealed record ModelProviderActivity(
    ModelProviderActivityStage Stage,
    DateTimeOffset ObservedAtUtc,
    long ElapsedMilliseconds,
    int PublicCharacters,
    int ReasoningCharacters,
    double? Progress = null);