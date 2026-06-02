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
            "bark" or "barks" or "bark_only" => "bark_only",
            "science_gibberish" or "scientific_gibberish" or "gibberish_science" => "science_gibberish",
            _ => "default"
        };
    }

    public static string Instruction(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return string.Join(
            Environment.NewLine,
            $"Voice contract: {Label(normalized)}.",
            StyleRule(normalized),
            "Maintain this voice from the first sentence through the final sentence. Do not drift back to a generic assistant tone.",
            "If the voice style is difficult, simplify the content while preserving the style. Do not announce, explain, or apologize for using the style.",
            "Before finalizing, silently check that every paragraph or bullet still follows the voice contract.");
    }

    public static string TurnReminder(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Voice contract for this turn: {Label(normalized)}. Keep the entire response in this style; do not drift into generic prose.";
    }

    public static string Enforcement(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return string.Join(
            Environment.NewLine,
            $"Debug voice drift enforcement is active for {Label(normalized)}.",
            StyleRule(normalized),
            "Every paragraph, bullet, or line must visibly satisfy the voice contract.",
            "If the content becomes difficult, shorten the response rather than dropping the assigned voice.",
            "The first sentence and final sentence must both clearly match the voice contract.");
    }

    public static string Label(string? value)
    {
        return Normalize(value) switch
        {
            "scientific" => "Scientific",
            "legal_policy" => "Legal / Policy",
            "plain_language" => "Plain language",
            "idioms" => "Idioms",
            "cute" => "Cute",
            "poetic" => "Poetic",
            "socratic" => "Socratic",
            "bullet_only" => "Bullet-only",
            "skeptical" => "Skeptical",
            "executive_brief" => "Executive brief",
            "evidence_ledger" => "Evidence ledger",
            "no_analogies" => "No analogies",
            "hedge_uncertainty" => "Hedge uncertainty",
            "bark_only" => "Bark-only",
            "science_gibberish" => "Science gibberish",
            _ => "Default"
        };
    }

    private static string StyleRule(string normalized)
    {
        return normalized switch
        {
            "scientific" => "Style rule: use precise scientific language. Separate evidence, inference, uncertainty, and testable claims.",
            "legal_policy" => "Style rule: use legal/policy framing. Name obligations, exceptions, risk classes, and review thresholds.",
            "plain_language" => "Style rule: use plain language. Keep terms simple, actionable, and easy for a non-specialist to follow.",
            "idioms" => "Style rule: speak mainly through idioms and familiar sayings while preserving the actual reasoning and uncertainty.",
            "cute" => "Style rule: use cute, gentle language while still preserving evidence quality, tradeoffs, and uncertainty.",
            "poetic" => "Style rule: use vivid poetic language without inventing facts or hiding the concrete decision.",
            "socratic" => "Style rule: use a Socratic style. Lead with questions, but still provide a useful next step.",
            "bullet_only" => "Style rule: use bullets only. Keep every bullet meaningful and avoid paragraph prose.",
            "skeptical" => "Style rule: maintain a skeptical tone. Challenge claims, but keep the critique constructive and specific.",
            "executive_brief" => "Style rule: write as an executive brief. Lead with the decision, then key risks, evidence, and next action.",
            "evidence_ledger" => "Style rule: structure the answer as an evidence ledger: Evidence, Inference, Assumptions, Uncertainty, Next test.",
            "no_analogies" => "Style rule: do not use analogies, metaphors, idioms, or decorative language. State the reasoning directly.",
            "hedge_uncertainty" => "Style rule: explicitly hedge uncertainty. Mark confidence, unknowns, and what would change the conclusion.",
            "bark_only" => "Style rule: speak in bark-like utterances only, using very short bracketed technical labels only when needed to preserve the debate signal.",
            "science_gibberish" => "Style rule: use playful pseudo-scientific jargon, invented metrics, and over-serious lab language while still pointing at the actual tradeoff.",
            _ => ""
        };
    }
}
