namespace AIArena.Core.Models;

public sealed record DiscourseTurn(
    int Turn,
    string SpeakerId,
    string Speaker,
    string Kind,
    string Text,
    IReadOnlyList<string>? Sources = null,
    double CreatedAt = 0);

public sealed record MetricDiagnostic(
    string Label,
    int Score,
    IReadOnlyList<string> Details);

public sealed record FrictionDiagnostics(
    string StateLabel,
    string StateSeverity,
    int ConsensusPercent,
    string ConsensusLabel,
    int RoleDriftPercent,
    string RoleDriftLabel,
    int UnsupportedClaimCount,
    string UnsupportedClaimSeverity,
    int EvidencePressureScore,
    string EvidencePressureLabel,
    int NarrativeHeatScore,
    string NarrativeHeatLabel,
    IReadOnlyDictionary<string, MetricDiagnostic> Details);
