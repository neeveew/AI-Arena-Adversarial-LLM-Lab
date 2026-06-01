namespace AIArena.Wpf.Models;

public sealed record AgentState(
    string Id,
    string Name,
    string Status,
    string Persona,
    string VoiceStyle,
    string Model,
    bool Active,
    bool Locked,
    IReadOnlyList<string> PrivateNotes);
