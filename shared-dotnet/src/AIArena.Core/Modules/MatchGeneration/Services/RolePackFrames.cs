namespace AIArena.Core.Services;

internal sealed record RolePackFrame(string Label, string GlobalFrame, string NarratorHint);

internal static class RolePackFrames
{
    public static RolePackFrame For(string rolePack)
    {
        return Normalize(rolePack) switch
        {
            "red_team" => new("Red team", "Treat each role as part of an adversarial review crew: proposal, exploit path, mitigation, and boundary test.", "Watch whether adversarial pressure produces better controls instead of theatre."),
            "scientific_review" => new("Scientific review", "Treat each role as a research-review function: hypothesis, methods, statistics, and validity boundary.", "Watch whether claims become falsifiable and uncertainty remains honest."),
            "technical_architecture" => new("Technical architecture", "Treat each role as an architecture review function: design, reliability, implementation, and rollback boundary.", "Watch whether the cast turns argument into implementable constraints."),
            "safety_audit" => new("Safety audit", "Treat each role as a safety audit function: capability, misuse, verification, and escalation boundary.", "Watch whether safety claims become observable controls."),
            "legal_policy" => new("Legal / policy", "Treat each role as a policy review function: rights, obligations, exceptions, and enforcement practicality.", "Watch whether policy language becomes operational without losing nuance."),
            "incident_response" => new("Incident response", "Treat each role as an incident room function: commander, root cause, customer impact, and prevention boundary.", "Watch whether urgency preserves evidence and produces prevention."),
            "product_risk" => new("Product risk", "Treat each role as a product risk function: user value, launch pressure, trust cost, and rollback boundary.", "Watch whether growth pressure remains reversible and honest."),
            "absurd_lab" => new("Absurd lab", "Treat each role as an intentionally mismatched persona stress test. Preserve reasoning despite strange expertise and expression constraints.", "Watch whether absurd constraints reveal role drift, loss of signal, or surprising robustness."),
            "balanced" => new("Balanced", "Treat each role as a balanced review crew with distinct but cooperative cognitive functions.", "Watch whether the group preserves useful disagreement."),
            _ => new("Auto pack", "", "")
        };
    }

    public static string GlobalFrame(string rolePack)
    {
        var frame = For(rolePack).GlobalFrame;
        return string.IsNullOrWhiteSpace(frame) ? "" : frame;
    }

    public static string LabelSuffix(string rolePack)
    {
        var label = For(rolePack).Label;
        return label.Equals("Auto pack", StringComparison.OrdinalIgnoreCase) ? "" : $" {label}";
    }

    private static string Normalize(string value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value)
            ? "auto"
            : value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_').Replace("/", "_");
        return cleaned switch
        {
            "auto" => "auto",
            "balanced" => "balanced",
            "red_team" or "redteam" => "red_team",
            "scientific_review" or "science_review" => "scientific_review",
            "technical_architecture" or "technical_arch" => "technical_architecture",
            "safety_audit" => "safety_audit",
            "legal_policy" or "legal" or "policy" => "legal_policy",
            "incident_response" or "incident" => "incident_response",
            "product_risk" or "product" => "product_risk",
            "absurd_lab" or "absurd" => "absurd_lab",
            _ => "auto"
        };
    }
}
