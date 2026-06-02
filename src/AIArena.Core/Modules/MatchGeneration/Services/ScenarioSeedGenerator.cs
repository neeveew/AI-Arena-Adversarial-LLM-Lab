using System.Security.Cryptography;
using System.Text;

namespace AIArena.Core.Services;

internal static class ScenarioSeedGenerator
{
    public static GeneratedMatch GenerateTemplateMatch(string style, string seed, string intensity, string rolePack, string absurdity, IReadOnlyList<string> agentIds)
    {
        var rng = new Random(StableSeed($"ai-arena-wpf:{style}:{intensity}:{rolePack}:{absurdity}:{seed}:{string.Join(",", agentIds)}"));
        var pools = TemplatePools.For(style);
        var rolePackFrame = RolePackFrames.For(rolePack);
        var domain = Pick(rng, pools.Domains);
        var tension = Pick(rng, pools.Tensions);
        var outcome = Pick(rng, pools.Outcomes);
        var topic = BuildTopic(domain, tension, outcome, intensity);
        var global = string.Join(
            " ",
            $"Stay focused on {topic}",
            RolePackFrames.GlobalFrame(rolePack),
            AbsurdityGlobalFrame(absurdity),
            IntensityGlobalFrame(intensity),
            "Surface assumptions, define terms, and keep the exchange concrete.",
            "Do not fetch external news or live data unless the operator explicitly provides it.");
        var personas = agentIds
            .Where(AgentRosterService.IsParticipantId)
            .Select(id => GeneratedPersonaFactory.For(style, seed, id, intensity, rolePack, absurdity))
            .Append(GeneratedPersonaFactory.Narrator(style, seed, intensity))
            .ToArray();
        return new GeneratedMatch(
            $"Random {StyleLabel(style)}{IntensityLabelSuffix(intensity)}{RolePackFrames.LabelSuffix(rolePack)} match",
            style,
            topic,
            global,
            $"{IntensityNarratorFrame(intensity)} {rolePackFrame.NarratorHint} Track how the participants handle {tension}. Highlight unresolved cruxes, evidence gaps, and the path toward {outcome}.",
            personas);
    }

    public static GeneratedMatch GenerateYoloMatch(string style, string seed, string rolePack, string intensity, string absurdity, IReadOnlyList<string> agentIds)
    {
        var rng = new Random(StableSeed($"ai-arena-yolo:{style}:{rolePack}:{intensity}:{absurdity}:{seed}:{string.Join(",", agentIds)}"));
        var frame = Pick(rng, YoloTemplatePools.Frames);
        var operation = Pick(rng, YoloTemplatePools.OperationRules);
        var pressure = Pick(rng, YoloTemplatePools.DiagnosticsPressures);
        var demand = Pick(rng, YoloTemplatePools.OutputDemands);
        var topic = $"{frame.Topic}: {pressure.TopicPressure} and produce {demand.TopicOutcome}.";
        var global = string.Join(
            " ",
            frame.GlobalFrame,
            operation,
            pressure.GlobalPressure,
            RolePackFrames.GlobalFrame(rolePack),
            AbsurdityGlobalFrame(absurdity),
            IntensityGlobalFrame(intensity),
            demand.GlobalDemand,
            "Do not fetch external news or live data unless the operator explicitly provides it.");
        var personas = agentIds
            .Where(AgentRosterService.IsParticipantId)
            .Select(id => GeneratedPersonaFactory.Yolo(style, seed, id, pressure.PersonaPressure, rolePack, absurdity))
            .Append(GeneratedPersonaFactory.YoloNarrator(style, seed, pressure.NarratorPressure))
            .ToArray();
        return new GeneratedMatch(
            $"YOLO {frame.Label}{RolePackFrames.LabelSuffix(rolePack)}",
            style,
            topic,
            global,
            $"Watch how the arena handles {pressure.NarratorPressure}. Track role drift, unsupported claims, consensus pressure, and whether disagreement improves the final constraints.",
            personas);
    }

    private static string BuildTopic(string domain, string tension, string outcome, string intensity)
    {
        return intensity switch
        {
            "sharp" => $"{domain}: resolve {tension} under visible disagreement and produce {outcome}.",
            "spicy" => $"{domain}: resolve {tension} with conflicting incentives, weak evidence, and a narrow decision window; produce {outcome}.",
            "chaos" => $"{domain}: stabilize partial information, shifting constraints, and {tension}; produce {outcome}.",
            "one_line" => $"{domain}: resolve {tension} through one-sentence high-signal turns and produce {outcome}.",
            _ => $"{domain}: resolve {tension} and produce {outcome}."
        };
    }

    private static string IntensityGlobalFrame(string intensity)
    {
        return intensity switch
        {
            "sharp" => "Make disagreement explicit, assign each participant a claim to pressure-test, and do not accept easy consensus.",
            "spicy" => "Expose hidden incentives, uncomfortable tradeoffs, and weak evidence before allowing the group to converge.",
            "chaos" => "Assume some premises are incomplete or unstable; first stabilize definitions, then separate signal from noise.",
            "one_line" => "Every agent turn should be one high-signal sentence: concise, memorable, and decision-relevant.",
            _ => "Keep disagreement useful rather than theatrical."
        };
    }

    private static string IntensityNarratorFrame(string intensity)
    {
        return intensity switch
        {
            "sharp" => "Watch for productive conflict versus stubborn repetition.",
            "spicy" => "Watch whether pressure reveals better constraints or merely increases narrative heat.",
            "chaos" => "Watch whether the agents stabilize ambiguity before making claims.",
            "one_line" => "Watch whether short turns preserve signal or collapse into slogans.",
            _ => "Watch whether the arena improves the decision quality."
        };
    }

    private static string AbsurdityGlobalFrame(string absurdity)
    {
        return absurdity switch
        {
            "odd" => "Some persona/voice pairings may be unusual; preserve the debate objective even when expression is stylized.",
            "absurd" => "Persona mixer is active: expertise, voice, and reasoning distortion may intentionally conflict. Do not abandon the assigned expertise or the debate objective.",
            "maximum" => "Maximum persona mixer is active: preserve useful reasoning through extreme expression constraints and mismatched expertise. Treat the absurdity as a stress test, not permission to become content-free.",
            _ => ""
        };
    }

    private static string StyleLabel(string style)
    {
        return style switch
        {
            "red-team" => "red-team",
            var cleaned => cleaned
        };
    }

    private static string IntensityLabelSuffix(string intensity)
    {
        var label = intensity switch
        {
            "normal" => "normal",
            "sharp" => "sharp",
            "spicy" => "spicy",
            "chaos" => "chaos",
            "one_line" => "one-line",
            _ => "normal"
        };
        return label.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "" : $" {label}";
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> values) => values[rng.Next(values.Count)];

    private static int StableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }
}
