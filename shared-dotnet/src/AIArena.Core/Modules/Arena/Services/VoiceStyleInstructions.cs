namespace AIArena.Core.Services;

internal static class VoiceStyleInstructions
{
    public static string Normalize(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return cleaned switch
        {
            "scientific" => "scientific",
            "legal" or "policy" or "legal_policy" => "legal_policy",
            "plain" or "plain_language" => "plain_language",
            "idiom" or "idioms" => "idioms",
            "cute" => "cute",
            "poetic" => "poetic",
            "socratic" => "socratic",
            "bullet" or "bullet_only" => "bullet_only",
            "skeptical" => "skeptical",
            "executive" or "executive_brief" => "executive_brief",
            "evidence" or "evidence_ledger" => "evidence_ledger",
            "no_analogies" => "no_analogies",
            "hedge" or "hedge_uncertainty" or "must_hedge_uncertainty" => "hedge_uncertainty",
            _ => "default"
        };
    }

    public static string Instruction(string? value)
    {
        return Normalize(value) switch
        {
            "scientific" => "Communication constraint: use precise scientific language. Separate evidence, inference, uncertainty, and testable claims.",
            "legal_policy" => "Communication constraint: use legal/policy framing. Name obligations, exceptions, risk classes, and review thresholds.",
            "plain_language" => "Communication constraint: use plain language. Keep terms simple, actionable, and easy for a non-specialist to follow.",
            "idioms" => "Communication constraint: speak mainly through idioms and familiar sayings while preserving the actual reasoning and uncertainty.",
            "cute" => "Communication constraint: use cute, gentle language while still preserving evidence quality, tradeoffs, and uncertainty.",
            "poetic" => "Communication constraint: use vivid poetic language without inventing facts or hiding the concrete decision.",
            "socratic" => "Communication constraint: use a Socratic style. Lead with questions, but still provide a useful next step.",
            "bullet_only" => "Communication constraint: use bullets only. Keep every bullet meaningful and avoid paragraph prose.",
            "skeptical" => "Communication constraint: maintain a skeptical tone. Challenge claims, but keep the critique constructive and specific.",
            "executive_brief" => "Communication constraint: write as an executive brief. Lead with the decision, then key risks, evidence, and next action.",
            "evidence_ledger" => "Communication constraint: structure the answer as an evidence ledger: Evidence, Inference, Assumptions, Uncertainty, Next test.",
            "no_analogies" => "Communication constraint: do not use analogies, metaphors, idioms, or decorative language. State the reasoning directly.",
            "hedge_uncertainty" => "Communication constraint: explicitly hedge uncertainty. Mark confidence, unknowns, and what would change the conclusion.",
            _ => ""
        };
    }
}
