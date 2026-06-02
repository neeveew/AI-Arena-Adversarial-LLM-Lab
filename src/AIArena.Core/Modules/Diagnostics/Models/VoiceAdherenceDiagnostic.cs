namespace AIArena.Core.Models;

public sealed record VoiceAdherenceDiagnostic(
    string VoiceStyle,
    string Label,
    string State,
    int Score,
    string Summary,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Missing);
