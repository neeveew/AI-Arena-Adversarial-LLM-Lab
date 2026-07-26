namespace AIArena.Core.Services;

/// <summary>
/// Pure, shared policy for scenario audit semantics. Core generation, WPF presentation,
/// and automation must use this authority instead of interpreting seed or instruction
/// strings independently.
/// </summary>
public static class ScenarioAuditPolicy
{
    public const string QualityContract =
        "Quality contract: define what a good outcome means, name at least one unacceptable failure, test one edge case, and finish with an actionable output plus unresolved uncertainty.";

    public static bool HasCompleteQualityContract(string? globalInstruction)
    {
        if (string.IsNullOrWhiteSpace(globalInstruction))
        {
            return false;
        }

        // The auditable state is intentionally tied to the canonical instruction,
        // not to a bag of keywords that can also appear in a negated or incomplete note.
        var normalizedInstruction = NormalizeWhitespace(globalInstruction);
        var normalizedContract = NormalizeWhitespace(QualityContract);
        return normalizedInstruction.Contains(normalizedContract, StringComparison.OrdinalIgnoreCase);
    }

    public static string EnsureCompleteQualityContract(string? globalInstruction)
    {
        var clean = (globalInstruction ?? "").Trim();
        if (HasCompleteQualityContract(clean))
        {
            return clean;
        }

        var preservedInstruction = clean.Replace(
            "Quality contract:",
            "Incomplete quality note:",
            StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(preservedInstruction)
            ? QualityContract
            : $"{preservedInstruction} {QualityContract}";
    }

    public static ScenarioGenerationDeterminism ClassifyDeterminism(string? kind, string? scenarioSeed = null)
    {
        var normalizedKind = NormalizeKind(kind);
        if (normalizedKind is "random" or "wild" or "yolo")
        {
            return ScenarioGenerationDeterminism.SeedDeterministic;
        }

        if (normalizedKind is "ai" or "ai_choice" or "choice" or "current" or "current_topics" or "topics")
        {
            return ScenarioGenerationDeterminism.CapturedOutputReplayable;
        }

        var normalizedSeed = NormalizeKind(scenarioSeed);
        return normalizedSeed is "ai_choice" or "current_topics"
            ? ScenarioGenerationDeterminism.CapturedOutputReplayable
            : ScenarioGenerationDeterminism.SeedDeterministic;
    }

    public static bool IsSeedDeterministic(string? kind, string? scenarioSeed = null) =>
        ClassifyDeterminism(kind, scenarioSeed) == ScenarioGenerationDeterminism.SeedDeterministic;

    public static string ReplayMode(string? kind, string? scenarioSeed = null) =>
        ReplayMode(ClassifyDeterminism(kind, scenarioSeed));

    public static string ReplayMode(ScenarioGenerationDeterminism determinism) => determinism switch
    {
        ScenarioGenerationDeterminism.SeedDeterministic => "seed_deterministic",
        _ => "captured_output_replayable"
    };

    private static string NormalizeKind(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public enum ScenarioGenerationDeterminism
{
    SeedDeterministic,
    CapturedOutputReplayable
}
