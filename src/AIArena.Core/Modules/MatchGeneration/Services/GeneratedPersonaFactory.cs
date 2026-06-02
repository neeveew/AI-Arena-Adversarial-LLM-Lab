using System.Security.Cryptography;
using System.Text;

namespace AIArena.Core.Services;

internal static class GeneratedPersonaFactory
{
    public static GeneratedPersona For(string style, string seed, string agentId, string intensity = "normal", string rolePack = "auto", string absurdity = "grounded")
    {
        var rng = new Random(StableSeed($"persona:{style}:{seed}:{agentId}:{rolePack}:{absurdity}"));
        var pools = agentId.Equals("delta", StringComparison.OrdinalIgnoreCase)
            ? TemplatePools.ForDeltaPersona(style)
            : TemplatePools.ForPersona(style);
        var absurdRole = AbsurdRoleCatalog.For(rolePack, seed, agentId);
        var role = absurdRole?.Role ?? RoleForPack(rolePack, agentId) ?? pools.Roles[rng.Next(pools.Roles.Length)];
        var thinking = pools.Thinking[rng.Next(pools.Thinking.Length)];
        var temperament = pools.Temperaments[rng.Next(pools.Temperaments.Length)];
        var priority = pools.Priorities[rng.Next(pools.Priorities.Length)];
        var blindSpot = pools.BlindSpots[rng.Next(pools.BlindSpots.Length)];
        var pressure = MatchGenerationService.PersonaPressure(intensity);
        var voice = VoiceForMixer(absurdity, agentId, rng, absurdRole);
        var distortion = DistortionForMixer(absurdity, agentId, rng, absurdRole);
        var mixer = MixerFrame(role, voice, distortion);
        return new GeneratedPersona(
            agentId,
            role,
            string.Join(" ", new[]
            {
                $"{role}. Thinking style: {thinking}. Temperament: {temperament}. Priority/bias: {priority}. Blind spot: {blindSpot}.",
                AbsurdRoleFrame(absurdRole),
                mixer,
                pressure
            }.Where(item => !string.IsNullOrWhiteSpace(item))),
            voice);
    }

    public static GeneratedPersona Narrator(string style, string seed, string intensity = "normal")
    {
        var persona = For(style, seed, "narrator", intensity);
        var role = style switch
        {
            "adversarial" or "red-team" => "Critical observer",
            "technical" => "Process auditor",
            "scientific" or "research" => "Evidence observer",
            "product" => "Decision observer",
            "safety" => "Safety observer",
            "philosophical" => "Conceptual observer",
            "legal" => "Policy observer",
            "creative" => "Narrative observer",
            "incident" => "Incident observer",
            _ => "Neutral observer"
        };
        return persona with { Role = role, Persona = $"{role}. Track the exchange without joining as Alpha, Beta, Gamma, or Delta. {persona.Persona}" };
    }

    public static GeneratedPersona Yolo(string style, string seed, string agentId, string pressure, string rolePack = "auto", string absurdity = "grounded")
    {
        var rng = new Random(StableSeed($"yolo-persona:{style}:{seed}:{agentId}:{rolePack}:{absurdity}"));
        var pools = YoloTemplatePools.ForPersona(agentId, style);
        var absurdRole = AbsurdRoleCatalog.For(rolePack, seed, agentId);
        var role = absurdRole?.Role ?? RoleForPack(rolePack, agentId) ?? pools.Roles[rng.Next(pools.Roles.Length)];
        var thinking = pools.Thinking[rng.Next(pools.Thinking.Length)];
        var temperament = pools.Temperaments[rng.Next(pools.Temperaments.Length)];
        var priority = pools.Priorities[rng.Next(pools.Priorities.Length)];
        var blindSpot = pools.BlindSpots[rng.Next(pools.BlindSpots.Length)];
        var voice = VoiceForMixer(absurdity, agentId, rng, absurdRole);
        var distortion = DistortionForMixer(absurdity, agentId, rng, absurdRole);
        var mixer = MixerFrame(role, voice, distortion);
        return new GeneratedPersona(
            agentId,
            role,
            string.Join(" ", new[]
            {
                $"{role}. Arena function: {thinking}. Pressure focus: {pressure}. Temperament: {temperament}. Priority/bias: {priority}. Blind spot: {blindSpot}.",
                AbsurdRoleFrame(absurdRole),
                mixer
            }.Where(item => !string.IsNullOrWhiteSpace(item))),
            voice);
    }

    public static GeneratedPersona YoloNarrator(string style, string seed, string pressure)
    {
        var rng = new Random(StableSeed($"yolo-narrator:{style}:{seed}"));
        var roles = new[] { "Discourse monitor", "Arena systems observer", "Diagnostic narrator", "Simulation auditor" };
        var role = roles[rng.Next(roles.Length)];
        return new GeneratedPersona(
            "narrator",
            role,
            $"{role}. Observe AI Arena as a turn-based adversarial lab. Do not join as Alpha, Beta, Gamma, or Delta. Track {pressure}, role drift, unsupported claims, evidence pressure, consensus collapse, narrative heat, and whether the exchange produces sharper constraints.");
    }

    private static string? RoleForPack(string rolePack, string agentId)
    {
        var key = rolePack.Trim().ToLowerInvariant();
        return key switch
        {
            "balanced" => RoleByParticipant(agentId, [
                "Practical strategist",
                "Critical reviewer",
                "Evidence mapper",
                "Boundary tester",
                "Counterfactual scout",
                "Systems stress tester",
                "Human impact witness",
                "Synthesis challenger"
            ]),
            "red_team" => RoleByParticipant(agentId, [
                "Proposal defender",
                "Exploit path hunter",
                "Mitigation auditor",
                "Abuse boundary mapper",
                "Countermeasure breaker",
                "Assumption infiltrator",
                "User harm witness",
                "Escalation referee"
            ]),
            "scientific_review" => RoleByParticipant(agentId, [
                "Hypothesis builder",
                "Statistical skeptic",
                "Methodology critic",
                "Validity boundary mapper",
                "Replication planner",
                "Causal inference challenger",
                "Measurement realist",
                "Publication-bias spotter"
            ]),
            "technical_architecture" => RoleByParticipant(agentId, [
                "Architecture proposer",
                "Reliability critic",
                "Implementation planner",
                "Rollback boundary tester",
                "Dependency mapper",
                "Latency realist",
                "Operational runbook editor",
                "Failure injection planner"
            ]),
            "safety_audit" => RoleByParticipant(agentId, [
                "Capability advocate",
                "Misuse analyst",
                "Verification critic",
                "Escalation boundary mapper",
                "Containment reviewer",
                "Human oversight advocate",
                "Incident consequence witness",
                "Safeguard stress tester"
            ]),
            "legal_policy" => RoleByParticipant(agentId, [
                "Rights mapper",
                "Obligation interpreter",
                "Exception reviewer",
                "Enforcement skeptic",
                "Precedent scout",
                "Compliance systems critic",
                "Public interest witness",
                "Policy synthesis challenger"
            ]),
            "incident_response" => RoleByParticipant(agentId, [
                "Incident commander",
                "Root-cause analyst",
                "Customer impact witness",
                "Prevention boundary tester",
                "Recovery coordinator",
                "Timeline auditor",
                "Communications skeptic",
                "Postmortem action owner"
            ]),
            "product_risk" => RoleByParticipant(agentId, [
                "Product champion",
                "Trust-risk critic",
                "Launch operator",
                "Rollback sentinel",
                "Growth skeptic",
                "Support burden forecaster",
                "User value witness",
                "Adoption constraint mapper"
            ]),
            _ => null
        };
    }

    private static string VoiceForMixer(string absurdity, string agentId, Random rng, AbsurdRoleSpec? absurdRole)
    {
        if (absurdRole is not null)
        {
            return VoiceStyleInstructions.Normalize(absurdRole.VoiceStyle);
        }

        return absurdity switch
        {
            "odd" => Pick(rng, ["idioms", "cute", "poetic", "executive_brief", "hedge_uncertainty"]),
            "absurd" => Pick(rng, ["bark_only", "science_gibberish", "cute", "legal_policy", "poetic"]),
            "maximum" => RoleByParticipant(agentId, [
                "bark_only",
                "science_gibberish",
                "cute",
                "idioms",
                "legal_policy",
                "poetic",
                "socratic",
                "executive_brief"
            ]) ?? "default",
            _ => "default"
        };
    }

    private static string DistortionForMixer(string absurdity, string agentId, Random rng, AbsurdRoleSpec? absurdRole)
    {
        if (absurdRole is not null)
        {
            return absurdRole.Distortion;
        }

        if (absurdity.Equals("grounded", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var options = absurdity switch
        {
            "maximum" => new[] { "evidence-obsessed", "overconfident about weak signals", "literal-minded under metaphor", "consensus-resistant", "overly cautious about tiny edge cases" },
            "absurd" => new[] { "evidence-obsessed", "overconfident but testable", "literal-minded", "excessively cautious", "contrarian about obvious claims" },
            _ => new[] { "slightly overconfident", "overly cautious", "evidence-hungry", "literal-minded" }
        };

        return agentId.Equals("alpha", StringComparison.OrdinalIgnoreCase)
            && (absurdity.Equals("absurd", StringComparison.OrdinalIgnoreCase) || absurdity.Equals("maximum", StringComparison.OrdinalIgnoreCase))
            ? "evidence-obsessed"
            : Pick(rng, options);
    }

    private static string AbsurdRoleFrame(AbsurdRoleSpec? role)
    {
        return role is null
            ? ""
            : $"Absurd function: {role.UsefulFunction}. Expertise leak: {role.Expertise}. Failure bias: {role.BlindSpot}.";
    }

    private static string MixerFrame(string role, string voiceStyle, string distortion)
    {
        var voice = VoiceStyleInstructions.Normalize(voiceStyle);
        if (voice.Equals("default", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(distortion))
        {
            return "";
        }

        var parts = new List<string> { $"Persona mixer: expertise layer is {role}." };
        if (!voice.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Expression constraint is {VoiceStyleInstructions.Label(voice)}.");
        }
        if (!string.IsNullOrWhiteSpace(distortion))
        {
            parts.Add($"Reasoning distortion is {distortion}.");
        }
        parts.Add("Preserve the assigned expertise and debate objective even when the expression constraint is strange.");
        return string.Join(" ", parts);
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> values) => values[rng.Next(values.Count)];

    private static string? RoleByParticipant(string agentId, IReadOnlyList<string> values)
    {
        var index = AgentRosterService.ParticipantOrder(agentId);
        return index >= 0 && index < values.Count ? values[index] : null;
    }

    private static int StableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }
}
