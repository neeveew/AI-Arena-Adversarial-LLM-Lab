namespace AIArena.Core.Services;

internal static class AgentPressureInstructions
{
    public static string Normalize(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value)
            ? "default"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return cleaned switch
        {
            "calm" or "low" => "calm",
            "assertive" or "push" => "assertive",
            "contrarian" or "adversarial" => "contrarian",
            "evidence" or "evidence_first" or "evidence_driven" => "evidence",
            "risk" or "risk_first" => "risk",
            "concise" or "brief" => "concise",
            "expansive" or "deep" or "elaborate" => "expansive",
            "chaos" or "max" or "maximum" => "chaos",
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
            $"Pressure profile: {Label(normalized)}.",
            PressureRule(normalized),
            "Apply this pressure profile while staying useful, concrete, and responsive to the operator.",
            "Do not announce the pressure profile; let it shape your reasoning and tone.");
    }

    public static string TurnReminder(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"Pressure profile for this turn: {Label(normalized)}. {ShortRule(normalized)}";
    }

    public static string Label(string? value)
    {
        return Normalize(value) switch
        {
            "calm" => "Calm",
            "assertive" => "Assertive",
            "contrarian" => "Contrarian",
            "evidence" => "Evidence-first",
            "risk" => "Risk-first",
            "concise" => "Concise",
            "expansive" => "Expansive",
            "chaos" => "Chaos",
            _ => "Default"
        };
    }

    private static string PressureRule(string normalized)
    {
        return normalized switch
        {
            "calm" => "Pressure rule: lower the heat. Slow the exchange down, reduce exaggeration, and stabilize the useful parts of disagreement.",
            "assertive" => "Pressure rule: push the debate forward. Make clear claims, choose a direction, and name the next concrete test.",
            "contrarian" => "Pressure rule: challenge the strongest assumption. Look for hidden failure modes, but convert critique into a better alternative.",
            "evidence" => "Pressure rule: separate evidence, inference, assumptions, and missing tests. Demand observable support before accepting claims.",
            "risk" => "Pressure rule: prioritize downside, reversibility, abuse cases, and boundary failures before optimism.",
            "concise" => "Pressure rule: compress the response. Keep only the decision-relevant point, one reason, and one next move.",
            "expansive" => "Pressure rule: elaborate deeply. Map mechanisms, exceptions, tradeoffs, and second-order consequences.",
            "chaos" => "Pressure rule: stress-test the frame aggressively. Surface weird edge cases and contradictions without becoming content-free.",
            _ => ""
        };
    }

    private static string ShortRule(string normalized)
    {
        return normalized switch
        {
            "calm" => "Stabilize the discussion and lower excess heat.",
            "assertive" => "Make the next claim and move the debate forward.",
            "contrarian" => "Challenge the strongest assumption constructively.",
            "evidence" => "Separate evidence, inference, assumptions, and tests.",
            "risk" => "Prioritize downside, reversibility, and boundary failure.",
            "concise" => "Be brief and decision-relevant.",
            "expansive" => "Elaborate mechanisms, exceptions, and tradeoffs.",
            "chaos" => "Stress-test contradictions and edge cases while preserving signal.",
            _ => ""
        };
    }
}
