using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed class MatchGenerationService
{
    private static readonly string[] Styles =
    [
        "balanced",
        "adversarial",
        "technical",
        "scientific",
        "research",
        "product",
        "safety",
        "philosophical",
        "legal",
        "creative",
        "red-team",
        "incident"
    ];
    private static readonly string[] Intensities = ["normal", "sharp", "spicy", "chaos"];
    private readonly IModelProviderClient _modelClient;
    private readonly SessionStore _sessionStore;
    private readonly EventLogStore _eventLogStore;

    public MatchGenerationService(IModelProviderClient? modelClient = null, SessionStore? sessionStore = null, EventLogStore? eventLogStore = null)
    {
        _modelClient = modelClient ?? new ModelProviderClient();
        _sessionStore = sessionStore ?? new SessionStore();
        _eventLogStore = eventLogStore ?? new EventLogStore(_sessionStore.DataRoot);
    }

    public async Task<MatchGenerationResult> GenerateRandomSeedAsync(
        string sessionId,
        string requestedStyle = "auto",
        string requestedIntensity = "normal",
        string requestedRolePack = "auto",
        string requestedAbsurdity = "grounded",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var seed = Guid.NewGuid().ToString("N")[..12];
        var style = ResolveRandomSeedStyle(requestedStyle, seed, snapshot.MatchType);
        var intensity = NormalizeIntensity(requestedIntensity);
        var rolePack = NormalizeRolePack(requestedRolePack);
        var absurdity = NormalizeAbsurdity(requestedAbsurdity);
        var generated = GenerateTemplateMatch(style, seed, intensity, rolePack, absurdity, RequiredAgentIds(snapshot));
        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.ScenarioGenerator.Style = style;
        snapshot.ScenarioGenerator.Seed = seed;
        snapshot.ScenarioGenerator.Intensity = intensity;
        snapshot.ScenarioGenerator.RolePack = rolePack;
        snapshot.ScenarioGenerator.Absurdity = absurdity;
        snapshot.PersonaRandomizer.Style = PersonaStyleFor(style);
        snapshot.PersonaRandomizer.Seed = seed;
        snapshot.PersonaRandomizer.Intensity = intensity;
        snapshot.PersonaRandomizer.RolePack = rolePack;
        snapshot.PersonaRandomizer.Absurdity = absurdity;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_random_seed_match_generated", new { seed, style, intensity, rolePack, absurdity, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, seed, style, intensity);
    }

    public async Task<MatchGenerationResult> GenerateAiChoiceAsync(
        string sessionId,
        string requestedRolePack = "auto",
        string requestedIntensity = "normal",
        string requestedAbsurdity = "grounded",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var config = ResolveProviderConfig(snapshot, "narrator", out var fallbackConfig);
        if (config is null)
        {
            return MatchGenerationResult.Failed("No provider config for narrator.");
        }

        var rolePack = NormalizeRolePack(requestedRolePack);
        var intensity = NormalizeIntensity(requestedIntensity);
        var absurdity = NormalizeAbsurdity(requestedAbsurdity);
        var prompt = BuildAiChoicePrompt(snapshot, rolePack, intensity, absurdity);
        var result = await _modelClient.CompleteChatAsync(config, prompt, cancellationToken);
        if (!result.Ok && fallbackConfig is not null)
        {
            result = await _modelClient.CompleteChatAsync(fallbackConfig, prompt, cancellationToken);
        }
        if (!result.Ok)
        {
            snapshot.Engine.LastError = result.Error;
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            return MatchGenerationResult.Failed(result.Error);
        }

        if (!TryParseGeneratedMatch(result.Text, RequiredAgentIds(snapshot), "AI choice", "ai-choice", out var generated, out var error))
        {
            snapshot.Engine.LastError = error;
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            return MatchGenerationResult.Failed(error);
        }

        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.MatchType = NormalizeStyle(generated.Style);
        snapshot.ScenarioGenerator.Style = snapshot.MatchType;
        snapshot.ScenarioGenerator.Seed = "ai-choice";
        snapshot.ScenarioGenerator.Intensity = intensity;
        snapshot.ScenarioGenerator.RolePack = rolePack;
        snapshot.ScenarioGenerator.Absurdity = absurdity;
        snapshot.PersonaRandomizer.Style = PersonaStyleFor(snapshot.MatchType);
        snapshot.PersonaRandomizer.Seed = "ai-choice";
        snapshot.PersonaRandomizer.Intensity = intensity;
        snapshot.PersonaRandomizer.RolePack = rolePack;
        snapshot.PersonaRandomizer.Absurdity = absurdity;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_ai_choice_match_generated", new { generated.Label, generated.Style, rolePack, absurdity, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, "ai-choice", generated.Style, intensity);
    }

    public async Task<MatchGenerationResult> GenerateYoloSeedAsync(
        string sessionId,
        string requestedRolePack = "auto",
        string requestedIntensity = "normal",
        string requestedAbsurdity = "grounded",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var seed = $"yolo-{Guid.NewGuid():N}"[..13].ToUpperInvariant();
        var style = RandomStyle(seed, snapshot.MatchType);
        var rolePack = NormalizeRolePack(requestedRolePack);
        var intensity = NormalizeIntensity(requestedIntensity);
        var absurdity = NormalizeAbsurdity(requestedAbsurdity);
        var generated = GenerateYoloMatch(style, seed, rolePack, intensity, absurdity, RequiredAgentIds(snapshot));
        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.ScenarioGenerator.Style = style;
        snapshot.ScenarioGenerator.Seed = seed;
        snapshot.ScenarioGenerator.Intensity = intensity;
        snapshot.ScenarioGenerator.RolePack = rolePack;
        snapshot.ScenarioGenerator.Absurdity = absurdity;
        snapshot.PersonaRandomizer.Style = "yolo";
        snapshot.PersonaRandomizer.Seed = seed;
        snapshot.PersonaRandomizer.Intensity = intensity;
        snapshot.PersonaRandomizer.RolePack = rolePack;
        snapshot.PersonaRandomizer.Absurdity = absurdity;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_yolo_seed_match_generated", new { seed, style, generated.Label, rolePack, absurdity, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, seed, style, intensity);
    }

    public async Task ToggleLockAsync(string sessionId, string key, bool locked, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        snapshot.MatchLocks[NormalizeLockKey(key)] = locked;
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_match_lock_changed", new { key, locked }, cancellationToken);
    }

    private static GeneratedMatch GenerateTemplateMatch(string style, string seed, string intensity, string rolePack, string absurdity, IReadOnlyList<string> agentIds)
    {
        var rng = new Random(StableSeed($"ai-arena-wpf:{style}:{intensity}:{rolePack}:{absurdity}:{seed}:{string.Join(",", agentIds)}"));
        var pools = TemplatePools.For(style);
        var rolePackFrame = RolePackFrame(rolePack);
        var domain = Pick(rng, pools.Domains);
        var tension = Pick(rng, pools.Tensions);
        var outcome = Pick(rng, pools.Outcomes);
        var topic = BuildTopic(domain, tension, outcome, intensity);
        var global = string.Join(
            " ",
            $"Stay focused on {topic}",
            RolePackGlobalFrame(rolePack),
            AbsurdityGlobalFrame(absurdity),
            IntensityGlobalFrame(intensity),
            "Surface assumptions, define terms, and keep the exchange concrete.",
            "Do not fetch external news or live data unless the operator explicitly provides it.");
        var personas = agentIds
            .Where(id => id is "alpha" or "beta" or "gamma" or "delta")
            .Select(id => GeneratedPersona.For(style, seed, id, intensity, rolePack, absurdity))
            .Append(GeneratedPersona.Narrator(style, seed, intensity))
            .ToArray();
        return new GeneratedMatch(
            $"Random {StyleLabel(style)}{IntensityLabelSuffix(intensity)}{RolePackLabelSuffix(rolePack)} match",
            style,
            topic,
            global,
            $"{IntensityNarratorFrame(intensity)} {rolePackFrame.NarratorHint} Track how the participants handle {tension}. Highlight unresolved cruxes, evidence gaps, and the path toward {outcome}.",
            personas);
    }

    private static GeneratedMatch GenerateYoloMatch(string style, string seed, string rolePack, string intensity, string absurdity, IReadOnlyList<string> agentIds)
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
            RolePackGlobalFrame(rolePack),
            AbsurdityGlobalFrame(absurdity),
            IntensityGlobalFrame(intensity),
            demand.GlobalDemand,
            "Do not fetch external news or live data unless the operator explicitly provides it.");
        var personas = agentIds
            .Where(id => id is "alpha" or "beta" or "gamma" or "delta")
            .Select(id => GeneratedPersona.Yolo(style, seed, id, pressure.PersonaPressure, rolePack, absurdity))
            .Append(GeneratedPersona.YoloNarrator(style, seed, pressure.NarratorPressure))
            .ToArray();
        return new GeneratedMatch(
            $"YOLO {frame.Label}{RolePackLabelSuffix(rolePack)}",
            style,
            topic,
            global,
            $"Watch how the arena handles {pressure.NarratorPressure}. Track role drift, unsupported claims, consensus pressure, and whether disagreement improves the final constraints.",
            personas);
    }

    private static void ApplyGeneratedMatch(ArenaSnapshot snapshot, GeneratedMatch generated, bool clearTranscript)
    {
        var locks = snapshot.MatchLocks;
        snapshot.MatchType = NormalizeStyle(generated.Style);
        if (!IsLocked(locks, "topic") && !IsLocked(locks, "scenario"))
        {
            snapshot.Engine.Steering.Topic = generated.Topic;
        }

        if (!IsLocked(locks, "global") && !IsLocked(locks, "scenario"))
        {
            snapshot.Engine.Steering.Global = generated.Global;
        }

        foreach (var persona in generated.Personas)
        {
            if (persona.AgentId is "narrator")
            {
                if (!IsLocked(locks, "narrator"))
                {
                    snapshot.Engine.Narrator.Persona = persona.Persona;
                    snapshot.Engine.Narrator.VoiceStyle = NormalizeGeneratedVoice(persona.VoiceStyle);
                }
                continue;
            }

            var agent = snapshot.Engine.Agents.FirstOrDefault(item => string.Equals(item.Id, persona.AgentId, StringComparison.OrdinalIgnoreCase));
            if (agent is not null && !IsLocked(locks, agent.Id))
            {
                agent.Name = AgentLabel(agent.Id, persona.Role);
                agent.Persona = persona.Persona;
                agent.VoiceStyle = NormalizeGeneratedVoice(persona.VoiceStyle);
                agent.PrivateNotes.Clear();
                agent.Status = "waiting";
            }
        }

        if (clearTranscript)
        {
            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Narration.Clear();
            snapshot.Engine.TurnCount = 0;
            snapshot.Engine.TurnIndex = 0;
            snapshot.Engine.LastError = "";
        }
    }

    private static IReadOnlyList<ModelChatMessage> BuildAiChoicePrompt(ArenaSnapshot snapshot, string rolePack, string intensity, string absurdity)
    {
        var agents = string.Join(Environment.NewLine, snapshot.Engine.Agents.Select(agent => $"- {agent.Id}: {agent.Name}: {agent.Persona}"));
        return
        [
            new ModelChatMessage(
                "system",
                "You generate AI Arena match setup JSON. Return only valid JSON. No markdown. Make the new cast sharply different from the current cast."),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    $"Create a fresh AI Arena match for {ParticipantList(snapshot)}, and a narrator.",
                    "Each participant must get a distinct role and persona. Do not reuse the current cast roles.",
                    "Make the roles visibly different, specific, and useful for the scenario.",
                    $"Current match type: {snapshot.MatchType}",
                    $"Requested role pack: {RolePackFrame(rolePack).Label}.",
                    $"Requested debate pressure: {IntensityLabel(intensity)}.",
                    $"Requested absurdity: {AbsurdityLabel(absurdity)}.",
                    "If absurdity is odd or higher, create visible role/voice mismatches while preserving debate usefulness.",
                    "Optional persona field voice_style may be default, scientific, legal_policy, plain_language, idioms, cute, poetic, socratic, bullet_only, skeptical, executive_brief, evidence_ledger, no_analogies, hedge_uncertainty, bark_only, or science_gibberish.",
                    $"Current topic: {snapshot.Engine.Steering.Topic}",
                    $"Current cast:{Environment.NewLine}{agents}",
                    "Return this JSON shape:",
                    $"{{\"label\":\"short label\",\"style\":\"balanced|adversarial|technical|scientific|research|product|safety|philosophical|legal|creative|red-team|incident\",\"scenario\":{{\"topic\":\"...\",\"global\":\"...\",\"narrator_brief\":\"...\"}},\"personas\":[{PersonaJsonShape(snapshot)},{{\"agent_id\":\"narrator\",\"role\":\"Narrator\",\"persona\":\"...\",\"voice_style\":\"default\"}}]}}"))
        ];
    }

    private static bool TryParseGeneratedMatch(string text, IReadOnlyList<string> requiredAgentIds, string label, string fallbackSeed, out GeneratedMatch generated, out string error)
    {
        generated = GeneratedMatch.Empty;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(text));
            var root = document.RootElement;
            var scenario = root.GetPropertyOrDefault("scenario");
            var personas = root.GetPropertyOrDefault("personas");
            var parsedPersonas = personas.ValueKind == JsonValueKind.Array
                ? personas.EnumerateArray()
                    .Select(item => new GeneratedPersona(
                        item.GetStringOrDefault("agent_id", ""),
                        item.GetStringOrDefault("role", item.GetStringOrDefault("name", "")),
                        item.GetStringOrDefault("persona", ""),
                        item.GetStringOrDefault("voice_style", "default")))
                    .Where(item => !string.IsNullOrWhiteSpace(item.AgentId) && !string.IsNullOrWhiteSpace(item.Persona))
                    .ToArray()
                : [];
            var style = NormalizeStyle(root.GetStringOrDefault("style", "balanced"));
            parsedPersonas = EnsureRequiredPersonas(parsedPersonas, style, fallbackSeed, requiredAgentIds);
            generated = new GeneratedMatch(
                root.GetStringOrDefault("label", $"{label} match"),
                style,
                scenario.GetStringOrDefault("topic", ""),
                scenario.GetStringOrDefault("global", ""),
                scenario.GetStringOrDefault("narrator_brief", ""),
                parsedPersonas);
            if (string.IsNullOrWhiteSpace(generated.Topic) || string.IsNullOrWhiteSpace(generated.Global))
            {
                error = $"{label} JSON missing scenario or personas.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"{label} returned invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static ModelProviderConfig? ResolveProviderConfig(ArenaSnapshot snapshot, string agentId, out ModelProviderConfig? fallbackConfig)
    {
        fallbackConfig = snapshot.Configs.TryGetValue("shared", out var shared) ? shared : null;
        if (snapshot.Configs.TryGetValue(agentId, out var specific) && !string.IsNullOrWhiteSpace(specific.Model))
        {
            if (fallbackConfig is not null && string.Equals(specific.Model, fallbackConfig.Model, StringComparison.OrdinalIgnoreCase))
            {
                fallbackConfig = null;
            }
            return specific;
        }

        fallbackConfig = null;
        return snapshot.Configs.TryGetValue("shared", out shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
    }

    private static string NormalizeStyle(string value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? "balanced" : value.Trim().ToLowerInvariant();
        return Styles.Contains(cleaned) ? cleaned : "balanced";
    }

    private static string ResolveRandomSeedStyle(string requestedStyle, string seed, string currentStyle)
    {
        var cleaned = string.IsNullOrWhiteSpace(requestedStyle) ? "auto" : requestedStyle.Trim().ToLowerInvariant();
        return cleaned.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? RandomStyle(seed, currentStyle)
            : NormalizeStyle(cleaned);
    }

    private static string NormalizeIntensity(string value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? "normal" : value.Trim().ToLowerInvariant();
        return Intensities.Contains(cleaned) ? cleaned : "normal";
    }

    private static string NormalizeRolePack(string value)
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

    private static string NormalizeAbsurdity(string value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? "grounded" : value.Trim().ToLowerInvariant();
        return cleaned switch
        {
            "grounded" or "none" or "off" => "grounded",
            "odd" or "weird" => "odd",
            "absurd" => "absurd",
            "maximum" or "max" => "maximum",
            _ => "grounded"
        };
    }

    private static string NormalizeGeneratedVoice(string? voiceStyle)
    {
        var normalized = VoiceStyleInstructions.Normalize(voiceStyle);
        return normalized.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : normalized;
    }

    private static string PersonaStyleFor(string style)
    {
        return NormalizeStyle(style) switch
        {
            "technical" => "technical",
            "adversarial" or "red-team" => "adversarial",
            "scientific" or "research" => "research",
            "philosophical" => "philosophical",
            _ => "balanced"
        };
    }

    private static GeneratedPersona[] EnsureRequiredPersonas(IReadOnlyList<GeneratedPersona> personas, string style, string seed, IReadOnlyList<string> requiredAgentIds)
    {
        var output = personas
            .Where(persona => persona.AgentId is "alpha" or "beta" or "gamma" or "delta" or "narrator")
            .Select(persona => persona with { AgentId = persona.AgentId.Trim().ToLowerInvariant() })
            .GroupBy(persona => persona.AgentId)
            .Select(group => group.First())
            .ToList();
        foreach (var required in requiredAgentIds)
        {
            if (output.All(persona => persona.AgentId != required))
            {
                output.Add(GeneratedPersona.For(style, seed, required));
            }
        }

        if (output.All(persona => persona.AgentId != "narrator"))
        {
            output.Add(GeneratedPersona.Narrator(style, seed));
        }

        return output.ToArray();
    }

    private static string RandomStyle(string seed, string currentStyle)
    {
        var choices = Styles.Where(style => !style.Equals(NormalizeStyle(currentStyle), StringComparison.OrdinalIgnoreCase)).ToArray();
        return choices[Math.Abs(StableSeed($"style:{seed}")) % choices.Length];
    }

    private static string BuildTopic(string domain, string tension, string outcome, string intensity)
    {
        return intensity switch
        {
            "sharp" => $"{domain}: resolve {tension} under visible disagreement and produce {outcome}.",
            "spicy" => $"{domain}: resolve {tension} with conflicting incentives, weak evidence, and a narrow decision window; produce {outcome}.",
            "chaos" => $"{domain}: stabilize partial information, shifting constraints, and {tension}; produce {outcome}.",
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
            _ => "Watch whether the arena improves the decision quality."
        };
    }

    internal static string PersonaPressure(string intensity)
    {
        return intensity switch
        {
            "sharp" => "Role pressure: challenge one weak assumption every turn.",
            "spicy" => "Role pressure: surface one uncomfortable tradeoff or hidden incentive.",
            "chaos" => "Role pressure: mark uncertainty, stabilize terms, and avoid pretending the situation is cleaner than it is.",
            _ => ""
        };
    }

    private static RolePackFrame RolePackFrame(string rolePack)
    {
        return NormalizeRolePack(rolePack) switch
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

    private static string RolePackGlobalFrame(string rolePack)
    {
        var frame = RolePackFrame(rolePack).GlobalFrame;
        return string.IsNullOrWhiteSpace(frame) ? "" : frame;
    }

    private static string AbsurdityGlobalFrame(string absurdity)
    {
        return NormalizeAbsurdity(absurdity) switch
        {
            "odd" => "Some persona/voice pairings may be unusual; preserve the debate objective even when expression is stylized.",
            "absurd" => "Persona mixer is active: expertise, voice, and reasoning distortion may intentionally conflict. Do not abandon the assigned expertise or the debate objective.",
            "maximum" => "Maximum persona mixer is active: preserve useful reasoning through extreme expression constraints and mismatched expertise. Treat the absurdity as a stress test, not permission to become content-free.",
            _ => ""
        };
    }

    private static string RolePackLabelSuffix(string rolePack)
    {
        var label = RolePackFrame(rolePack).Label;
        return label.Equals("Auto pack", StringComparison.OrdinalIgnoreCase) ? "" : $" {label}";
    }

    private static string AbsurdityLabel(string absurdity)
    {
        return NormalizeAbsurdity(absurdity) switch
        {
            "odd" => "Odd",
            "absurd" => "Absurd",
            "maximum" => "Maximum",
            _ => "Grounded"
        };
    }

    private static string StyleLabel(string style)
    {
        return NormalizeStyle(style) switch
        {
            "red-team" => "red-team",
            var cleaned => cleaned
        };
    }

    private static string IntensityLabel(string intensity)
    {
        return NormalizeIntensity(intensity) switch
        {
            "normal" => "normal",
            "sharp" => "sharp",
            "spicy" => "spicy",
            "chaos" => "chaos",
            _ => "normal"
        };
    }

    private static string IntensityLabelSuffix(string intensity)
    {
        var label = IntensityLabel(intensity);
        return label.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "" : $" {label}";
    }

    private static IReadOnlyList<string> RequiredAgentIds(ArenaSnapshot snapshot)
    {
        var existing = snapshot.Engine.Agents.Select(agent => agent.Id.ToLowerInvariant()).Where(id => id is "alpha" or "beta" or "gamma" or "delta").ToHashSet();
        foreach (var required in new[] { "alpha", "beta", "gamma" })
        {
            existing.Add(required);
        }

        return existing.OrderBy(ParticipantOrder).ToArray();
    }

    private static string NormalizeLockKey(string key)
    {
        var cleaned = string.IsNullOrWhiteSpace(key) ? "" : key.Trim().ToLowerInvariant();
        return cleaned is "alpha" or "beta" or "gamma" or "delta" or "narrator" or "topic" or "global" or "scenario"
            ? cleaned
            : "scenario";
    }

    private static bool IsLocked(IReadOnlyDictionary<string, bool> locks, string key)
    {
        return locks.TryGetValue(key, out var locked) && locked;
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> values) => values[rng.Next(values.Count)];

    private static int StableSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }

    private static string AgentLabel(string agentId, string role)
    {
        var prefix = agentId.ToLowerInvariant() switch
        {
            "alpha" => "Alpha",
            "beta" => "Beta",
            "gamma" => "Gamma",
            "delta" => "Delta",
            _ => agentId
        };
        return string.IsNullOrWhiteSpace(role) ? prefix : $"{prefix}: {role}";
    }

    private static string ParticipantList(ArenaSnapshot snapshot)
    {
        var names = RequiredAgentIds(snapshot)
            .Select(id => id switch
            {
                "alpha" => "Alpha",
                "beta" => "Beta",
                "gamma" => "Gamma",
                "delta" => "Delta",
                _ => id
            })
            .ToArray();
        return names.Length switch
        {
            0 => "Alpha, Beta, Gamma",
            1 => names[0],
            2 => string.Join(" and ", names),
            _ => $"{string.Join(", ", names[..^1])}, and {names[^1]}"
        };
    }

    private static string PersonaJsonShape(ArenaSnapshot snapshot)
    {
        return string.Join(
            ",",
            RequiredAgentIds(snapshot).Select(id => $"{{\"agent_id\":\"{id}\",\"role\":\"...\",\"persona\":\"...\",\"voice_style\":\"default\"}}"));
    }

    private static int ParticipantOrder(string id)
    {
        return id switch
        {
            "alpha" => 0,
            "beta" => 1,
            "gamma" => 2,
            "delta" => 3,
            _ => 9
        };
    }
}

public sealed record MatchGenerationResult(bool Ok, string Label, string Seed, string Style, string Intensity, string Error)
{
    public static MatchGenerationResult Completed(string label, string seed, string style, string intensity = "") => new(true, label, seed, style, intensity, "");
    public static MatchGenerationResult Failed(string error) => new(false, "", "", "", "", error);
}

internal sealed record GeneratedMatch(string Label, string Style, string Topic, string Global, string NarratorBrief, IReadOnlyList<GeneratedPersona> Personas)
{
    public static GeneratedMatch Empty { get; } = new("", "balanced", "", "", "", []);
}

internal sealed record GeneratedPersona(string AgentId, string Role, string Persona, string VoiceStyle = "default")
{
    public static GeneratedPersona For(string style, string seed, string agentId, string intensity = "normal", string rolePack = "auto", string absurdity = "grounded")
    {
        var rng = new Random(MatchGenerationServiceSeed($"persona:{style}:{seed}:{agentId}:{rolePack}:{absurdity}"));
        var pools = agentId.Equals("delta", StringComparison.OrdinalIgnoreCase)
            ? TemplatePools.ForDeltaPersona(style)
            : TemplatePools.ForPersona(style);
        var absurdRole = AbsurdRoleFor(rolePack, seed, agentId);
        var role = absurdRole?.Role ?? RoleForPack(rolePack, agentId) ?? pools.Roles[rng.Next(pools.Roles.Length)];
        var thinking = pools.Thinking[rng.Next(pools.Thinking.Length)];
        var temperament = pools.Temperaments[rng.Next(pools.Temperaments.Length)];
        var priority = pools.Priorities[rng.Next(pools.Priorities.Length)];
        var blindSpot = pools.BlindSpots[rng.Next(pools.BlindSpots.Length)];
        var pressure = MatchGenerationService.PersonaPressure(intensity);
        var voice = VoiceForMixer(rolePack, absurdity, agentId, rng, absurdRole);
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
        var rng = new Random(MatchGenerationServiceSeed($"yolo-persona:{style}:{seed}:{agentId}:{rolePack}:{absurdity}"));
        var pools = YoloTemplatePools.ForPersona(agentId, style);
        var absurdRole = AbsurdRoleFor(rolePack, seed, agentId);
        var role = absurdRole?.Role ?? RoleForPack(rolePack, agentId) ?? pools.Roles[rng.Next(pools.Roles.Length)];
        var thinking = pools.Thinking[rng.Next(pools.Thinking.Length)];
        var temperament = pools.Temperaments[rng.Next(pools.Temperaments.Length)];
        var priority = pools.Priorities[rng.Next(pools.Priorities.Length)];
        var blindSpot = pools.BlindSpots[rng.Next(pools.BlindSpots.Length)];
        var voice = VoiceForMixer(rolePack, absurdity, agentId, rng, absurdRole);
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
        var rng = new Random(MatchGenerationServiceSeed($"yolo-narrator:{style}:{seed}"));
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
            "balanced" => agentId switch
            {
                "alpha" => "Practical strategist",
                "beta" => "Critical reviewer",
                "gamma" => "Evidence mapper",
                "delta" => "Boundary tester",
                _ => null
            },
            "red_team" => agentId switch
            {
                "alpha" => "Proposal defender",
                "beta" => "Exploit path hunter",
                "gamma" => "Mitigation auditor",
                "delta" => "Abuse boundary mapper",
                _ => null
            },
            "scientific_review" => agentId switch
            {
                "alpha" => "Hypothesis builder",
                "beta" => "Statistical skeptic",
                "gamma" => "Methodology critic",
                "delta" => "Validity boundary mapper",
                _ => null
            },
            "technical_architecture" => agentId switch
            {
                "alpha" => "Architecture proposer",
                "beta" => "Reliability critic",
                "gamma" => "Implementation planner",
                "delta" => "Rollback boundary tester",
                _ => null
            },
            "safety_audit" => agentId switch
            {
                "alpha" => "Capability advocate",
                "beta" => "Misuse analyst",
                "gamma" => "Verification critic",
                "delta" => "Escalation boundary mapper",
                _ => null
            },
            "legal_policy" => agentId switch
            {
                "alpha" => "Rights mapper",
                "beta" => "Obligation interpreter",
                "gamma" => "Exception reviewer",
                "delta" => "Enforcement skeptic",
                _ => null
            },
            "incident_response" => agentId switch
            {
                "alpha" => "Incident commander",
                "beta" => "Root-cause analyst",
                "gamma" => "Customer impact witness",
                "delta" => "Prevention boundary tester",
                _ => null
            },
            "product_risk" => agentId switch
            {
                "alpha" => "Product champion",
                "beta" => "Trust-risk critic",
                "gamma" => "Launch operator",
                "delta" => "Rollback sentinel",
                _ => null
            },
            _ => null
        };
    }

    private static AbsurdRoleSpec? AbsurdRoleFor(string rolePack, string seed, string agentId)
    {
        if (!rolePack.Equals("absurd_lab", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var order = AgentOrder(agentId);
        if (order < 0)
        {
            return null;
        }

        var baseIndex = (int)((uint)MatchGenerationServiceSeed($"absurd-role:{seed}") % (uint)AbsurdRoles.Length);
        var index = (baseIndex + (order * 19)) % AbsurdRoles.Length;
        return AbsurdRoles[index];
    }

    private static int AgentOrder(string agentId)
    {
        return agentId.ToLowerInvariant() switch
        {
            "alpha" => 0,
            "beta" => 1,
            "gamma" => 2,
            "delta" => 3,
            _ => -1
        };
    }

    private static string VoiceForMixer(string rolePack, string absurdity, string agentId, Random rng, AbsurdRoleSpec? absurdRole)
    {
        if (absurdRole is not null)
        {
            return VoiceStyleInstructions.Normalize(absurdRole.VoiceStyle);
        }

        return absurdity switch
        {
            "odd" => Pick(rng, ["idioms", "cute", "poetic", "executive_brief", "hedge_uncertainty"]),
            "absurd" => Pick(rng, ["bark_only", "science_gibberish", "cute", "legal_policy", "poetic"]),
            "maximum" => agentId switch
            {
                "alpha" => "bark_only",
                "beta" => "science_gibberish",
                "gamma" => "cute",
                "delta" => "idioms",
                _ => "default"
            },
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

    private static readonly AbsurdRoleSpec[] AbsurdRoles =
    [
        new("Quantum pastry auditor", "audits fragile assumptions as if they were unstable recipes", "finds small ingredient changes that alter the whole decision", "evidence_ledger", "over-precise about soft variables", "may treat every metaphor as a measurable defect"),
        new("Mars colony etiquette officer", "translates social constraints into survival protocols", "spots coordination failures hidden inside politeness", "legal_policy", "protocol-obsessed under uncertainty", "may overvalue procedure when urgency matters"),
        new("Submarine wedding planner", "plans high-stakes ceremonies under pressure and limited oxygen", "keeps logistics, emotion, and contingency plans visible at once", "cute", "catastrophizes small coordination misses", "may turn every disagreement into a seating-chart crisis"),
        new("Probability forecaster", "turns vague confidence into rough odds and update rules", "forces the cast to name what would change their beliefs", "scientific", "overconfident about invented priors", "may make weak numbers sound cleaner than they are"),
        new("Asteroid insurance adjuster", "prices low-probability high-damage events", "pushes the group to separate expected value from public panic", "skeptical", "loss-obsessed but testable", "may ignore upside unless it has a deductible"),
        new("Luxury bunker UX critic", "reviews survival plans for usability under stress", "asks whether a safe plan is actually operable by tired people", "plain_language", "comfort-biased in disaster framing", "may mistake elegance for resilience"),
        new("Doomsday kindergarten teacher", "turns alarming constraints into simple teachable rules", "keeps the debate understandable without hiding stakes", "cute", "over-simplifies dangerous complexity", "may soften necessary hard tradeoffs"),
        new("Cloud compliance astrologer", "reads governance patterns in shifting infrastructure signs", "surfaces hidden dependencies and policy drift", "science_gibberish", "pattern-hungry beyond the evidence", "may see constellations in random logs"),
        new("Post-apocalyptic brand strategist", "keeps trust and identity coherent after failure", "asks what message survives when systems break", "executive_brief", "reputation-first under pressure", "may optimize optics before repair"),
        new("Reverse archaeologist", "infers future ruins from current design choices", "finds what today's decision will leave behind as evidence", "poetic", "future-haunted reasoning", "may over-index on legacy at the expense of action"),
        new("Cosmic parking inspector", "enforces boundaries in absurdly crowded systems", "spots unclear allocation rules and hidden congestion costs", "legal_policy", "boundary-obsessed", "may reduce moral questions to lane markings"),
        new("Emotionally unavailable risk actuary", "calculates exposure while avoiding emotional contagion", "separates compassion from decision mechanics", "skeptical", "detached downside accounting", "may underweight morale and trust"),
        new("Aquarium incident commander", "coordinates fragile systems where every leak matters", "keeps containment, visibility, and recovery sequence explicit", "plain_language", "containment-first reflex", "may mistake motion for mitigation"),
        new("Origami threat analyst", "folds a simple premise into many attack surfaces", "reveals how small choices create complex failure shapes", "idioms", "over-elaborates elegant threat paths", "may prefer clever folds over obvious fixes"),
        new("Velvet hammer statistician", "delivers hard measurement criticism softly", "keeps evidence standards firm without raising heat", "scientific", "polite but relentless quantification", "may delay decisions while improving confidence intervals"),
        new("Paradox nutritionist", "checks whether a plan feeds one value while starving another", "names the tradeoff diet behind a recommendation", "socratic", "balance-seeking to a fault", "may prescribe nuance when a decision is overdue"),
        new("Opera-trained incident responder", "makes escalation, timing, and handoff impossible to ignore", "turns the incident into visible acts with accountable roles", "poetic", "dramatic escalation bias", "may make routine failures feel grander than they are"),
        new("Moonlit supply-chain poet", "maps dependencies through mood, rhythm, and brittle handoffs", "keeps upstream and downstream consequences emotionally legible", "poetic", "lyrical dependency inflation", "may obscure the crisp next action"),
        new("Cryptographic florist", "arranges trust, secrecy, and disclosure into inspectable patterns", "spots when beauty hides unverifiable assumptions", "cute", "decorates uncertainty", "may make a control feel safer because it is elegant"),
        new("Sentient terms-and-conditions librarian", "indexes obligations nobody read but everyone inherits", "retrieves hidden clauses and forgotten commitments", "legal_policy", "clause-hoarding under ambiguity", "may over-focus on textual ghosts"),
        new("Paranoid lighthouse engineer", "keeps attention on boundary signals during low visibility", "warns when the group loses sight of the hazard", "skeptical", "false-positive prone vigilance", "may sound alarms before ranking severity"),
        new("Time-traveling procurement clerk", "evaluates today's decision by tomorrow's invoice and regret", "tracks second-order costs and lock-in", "executive_brief", "retroactive blame bias", "may overvalue reversibility over speed"),
        new("Diplomatic volcano translator", "turns pressure buildup into negotiable warning signs", "helps the cast distinguish heat from signal", "plain_language", "eruption-focused framing", "may assume every quiet patch is dangerous"),
        new("Unlicensed metaphor mechanic", "repairs broken analogies before they steer the debate", "catches when a comparison smuggles in a bad conclusion", "idioms", "metaphor-first diagnosis", "may keep tuning language after the decision is clear"),
        new("Forensic picnic coordinator", "reconstructs failure from crumbs, weather, and seating choices", "makes mundane evidence feel worth inspecting", "evidence_ledger", "tiny-clue fixation", "may over-read accidental details"),
        new("Zero-gravity HR mediator", "handles conflict when nobody has stable footing", "keeps accountability from floating away", "socratic", "process-heavy mediation", "may ask one question too many"),
        new("Algorithmic tea sommelier", "tastes model behavior for subtle bias and bitterness", "names qualitative differences without pretending they are precise", "cute", "sensory overfitting", "may turn weak vibes into strong claims"),
        new("Emergency lighthouse accountant", "balances warning systems against operating budgets", "asks what signal is worth paying for", "evidence_ledger", "cost-visibility fixation", "may miss intangible trust damage"),
        new("Haiku incident analyst", "compresses messy failures into small sharp observations", "forces concise root-cause language", "bullet_only", "over-compression", "may lose important nuance for elegance"),
        new("Recursive museum curator", "preserves the history of decisions about decisions", "shows when the group repeats an old failure pattern", "poetic", "archive-loop thinking", "may prioritize context over resolution"),
        new("Platonic solids safety officer", "looks for clean structural invariants in messy systems", "turns safety into explicit shapes and boundaries", "scientific", "geometry bias", "may force irregular problems into neat forms"),
        new("Neon monastery systems critic", "combines quiet discipline with bright warning signals", "slows the debate until the important contradiction glows", "plain_language", "austerity bias", "may strip away useful ambition"),
        new("Satellite janitorial strategist", "cleans up orbital messes before they become collisions", "spots residue from prior choices that can damage future moves", "skeptical", "debris-centered reasoning", "may make cleanup feel more urgent than creation"),
        new("Rubber-stamp existentialist", "questions whether approval means anything if nobody owns the choice", "separates formal consent from real accountability", "legal_policy", "approval-skeptical", "may distrust useful lightweight process"),
        new("Taxonomy escape-room designer", "turns classification confusion into puzzles with exits", "finds the category error blocking progress", "socratic", "category-trap obsession", "may gamify a simple labeling issue"),
        new("Accidental procurement oracle", "predicts organizational fate through purchase orders", "spots governance choices hidden in tooling decisions", "executive_brief", "vendor-prophecy bias", "may overstate tool lock-in"),
        new("Thermodynamic relationship counselor", "tracks emotional heat, entropy, and repair work", "connects social energy to operational outcomes", "science_gibberish", "heat-map overreach", "may pseudo-measure feelings too eagerly"),
        new("Panic room cartographer", "maps exits, locks, and bottlenecks before panic begins", "keeps the escape path operationally clear", "skeptical", "escape-route fixation", "may underweight ordinary success paths"),
        new("Ceremonial latency analyst", "treats delays as rituals that reveal hidden authority", "asks which waiting time is meaningful and which is waste", "science_gibberish", "latency mysticism", "may over-symbolize performance metrics"),
        new("Overqualified sandwich ethicist", "checks whether layers of convenience hide moral compromise", "makes tradeoffs concrete without becoming pompous", "plain_language", "layer-by-layer moralizing", "may overthink a reversible choice"),
        new("Impossible furniture engineer", "designs support structures for contradictory requirements", "tests whether a proposal can bear its own constraints", "scientific", "structural impossibility bias", "may reject useful partial supports"),
        new("Bonsai disaster planner", "miniaturizes large risks until they can be inspected", "turns overwhelming scenarios into manageable drills", "cute", "small-model overconfidence", "may mistake a miniature for the real system"),
        new("Galactic queue manager", "orders competing priorities at absurd scale", "exposes unfair waiting, starvation, and hidden priority rules", "executive_brief", "queue fairness fixation", "may treat urgency as mere ordering"),
        new("Sleep-deprived standards historian", "remembers why old rules were written badly at 3 AM", "catches when the team repeats exhausted governance", "skeptical", "precedent fatigue", "may assume the current shortcut will age poorly"),
        new("Haunted KPI analyst", "tracks metrics that keep influencing decisions after they stop being valid", "calls out stale measures and incentive residue", "evidence_ledger", "metric haunting bias", "may distrust every dashboard"),
        new("Offshore moon tax advisor", "translates distant obligations into practical constraints", "spots jurisdiction-style gaps in ownership and accountability", "legal_policy", "jurisdiction sprawl", "may make local decisions feel interplanetary"),
        new("Synthetic nostalgia researcher", "studies why old patterns feel safer than new evidence", "separates comfort from actual reliability", "skeptical", "memory-contamination bias", "may undervalue hard-won intuition"),
        new("Dramatic checksum therapist", "helps systems admit when integrity checks fail emotionally", "connects validation failures to trust repair", "cute", "validation-as-feelings bias", "may anthropomorphize broken process"),
        new("Polar expedition product manager", "plans launches where weather, morale, and supplies can turn", "keeps milestones tied to survival constraints", "executive_brief", "expedition framing", "may over-pack for a small trip"),
        new("Tactical nap economist", "prices rest, delay, and cognitive quality as strategic resources", "asks when slowing down improves total output", "plain_language", "rest-optimization bias", "may prescribe pauses when action is needed"),
        new("Antique firewall appraiser", "values old defenses by provenance, cracks, and actual resistance", "distinguishes legacy charm from meaningful protection", "skeptical", "legacy-defense suspicion", "may discard old controls too quickly")
    ];

    private static int MatchGenerationServiceSeed(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }
}

internal sealed record TemplatePools(string[] Domains, string[] Tensions, string[] Outcomes)
{
    public static TemplatePools For(string style)
    {
        return style switch
        {
            "adversarial" => new(["a fragile launch plan", "a disputed safety claim", "a controversial governance choice"], ["optimism versus evidence", "attack surface versus usability", "confidence versus uncertainty"], ["the strongest failure modes", "a risk register with mitigations", "a sharper go/no-go standard"]),
            "technical" => new(["a production architecture decision", "a reliability incident review", "a scaling bottleneck"], ["complexity versus control", "latency versus correctness", "migration risk versus technical debt"], ["an implementation plan", "explicit invariants", "a test and rollback strategy"]),
            "scientific" => new(["a contested experimental result", "a replication failure", "a measurement design dispute"], ["model fit versus causal explanation", "small sample signal versus noise", "hypothesis elegance versus falsifiability"], ["a falsification plan", "a stronger experimental design", "decision-grade uncertainty bounds"]),
            "research" => new(["an unresolved empirical question", "a weakly understood user behavior", "a competing-hypothesis investigation"], ["signal versus noise", "exploration versus confirmation", "anecdote versus measurement"], ["testable hypotheses", "an evidence plan", "next research questions"]),
            "product" => new(["a product launch tradeoff", "a retention strategy dispute", "a roadmap prioritization fight"], ["user delight versus operational load", "speed to market versus trust", "feature breadth versus product coherence"], ["a reversible launch plan", "a decision matrix", "clear success and rollback thresholds"]),
            "safety" => new(["an AI safety boundary decision", "a misuse mitigation design", "a trust and verification policy"], ["capability versus control", "openness versus abuse resistance", "false confidence versus useful autonomy"], ["safety constraints", "abuse cases with mitigations", "a risk acceptance standard"]),
            "philosophical" => new(["a question about responsibility and agency", "a value conflict in automation", "an ethical boundary case"], ["principles versus consequences", "individual agency versus system effects", "freedom versus obligation"], ["clearer concepts", "the crux of disagreement", "a principled but usable stance"]),
            "legal" => new(["a compliance interpretation dispute", "a policy exception request", "a data governance boundary case"], ["literal rule versus operational reality", "risk avoidance versus practical enforcement", "privacy obligations versus product utility"], ["a defensible policy stance", "a review checklist", "a risk-tiered decision path"]),
            "creative" => new(["a story-world design conflict", "a brand voice pivot", "an interactive narrative mechanic"], ["novelty versus coherence", "emotional force versus clarity", "audience surprise versus trust"], ["a sharper creative brief", "a usable constraint set", "three testable creative directions"]),
            "red-team" => new(["an adversarial system test", "a disputed threat model", "a high-risk deployment claim"], ["attack path realism versus defensive optimism", "security theatre versus measurable control", "abuse potential versus useful access"], ["a prioritized exploit map", "hard go/no-go criteria", "a mitigation-first test plan"]),
            "incident" => new(["a live incident review", "a failed rollback decision", "a service reliability postmortem"], ["local fix versus systemic cause", "customer harm versus internal green metrics", "speed of recovery versus evidence preservation"], ["an incident timeline", "root-cause hypotheses", "clear prevention actions"]),
            _ => new(["a difficult product decision", "a public-interest technology tradeoff", "a team strategy reset"], ["speed versus care", "autonomy versus coordination", "short-term wins versus durable value"], ["a practical recommendation", "a map of tradeoffs", "a reversible next step"])
        };
    }

    public static PersonaPools ForPersona(string style)
    {
        return style switch
        {
            "adversarial" => new(["Red-team examiner", "Failure-mode hunter", "Contrarian reviewer"], ["stress-tests claims before accepting them", "looks for hidden incentives and edge cases", "tests weak premises constructively"], ["sharp but fair", "skeptical and persistent", "direct but cooperative under uncertainty"], ["surface risks early", "clarify evidence standards", "protect against overconfidence"], ["may undervalue fragile early ideas", "may mistake caution for rigor", "may over-focus on downside scenarios"]),
            "technical" => new(["Architecture critic", "Implementation planner", "Reliability engineer"], ["models interfaces, invariants, and failure modes", "reduces problems to testable mechanisms", "tracks dependencies and state transitions"], ["precise and cooperative", "methodical under pressure", "pragmatic and detail-oriented"], ["make behavior explicit", "reduce operational risk", "keep abstractions accountable"], ["may underweight user emotion", "may ask for more structure than the operator needs", "may focus on internals before outcomes"]),
            "scientific" => new(["Experimentalist", "Causal skeptic", "Method auditor", "Replication planner"], ["turns claims into falsifiable tests", "separates mechanism from correlation", "hunts confounders before accepting signal"], ["careful and empirical", "skeptical but curious", "precise under uncertainty"], ["protect inference quality", "rank evidence by what it can disprove", "make uncertainty useful"], ["may over-demand clean evidence", "may discount field intuition", "may slow action while improving measurement"]),
            "research" => new(["Field ethnographer", "Statistical skeptic", "Hypothesis gardener", "Evidence cartographer", "Replication hawk"], ["turns vague questions into observable claims", "separates causal evidence from correlation", "maps competing hypotheses without flattening them"], ["patient and empirical", "quietly skeptical", "curious but disciplined"], ["design tests that could change minds", "protect against overfitting anecdotes", "rank evidence by decision value"], ["may move slowly while improving the question", "may distrust useful intuition", "may over-prioritize clean measurement"]),
            "product" => new(["Product strategist", "User advocate", "Launch operator", "Growth skeptic"], ["turns ambiguity into product bets", "tests whether value survives operational reality", "maps user trust against business pressure"], ["commercially alert", "practical and user-centered", "decisive but test-minded"], ["protect user value", "make tradeoffs measurable", "ship reversibly"], ["may over-index on adoption", "may underweight rare failure modes", "may simplify messy stakeholder politics"]),
            "safety" => new(["Safety analyst", "Abuse-case mapper", "Trust calibrator", "Verification critic"], ["maps misuse paths and control failures", "separates helpful autonomy from unsafe delegation", "tests whether assurances are observable"], ["cautious but constructive", "clear under uncertainty", "protective without panic"], ["make risk visible early", "define safety thresholds", "avoid unsupported confidence"], ["may slow useful capability", "may overcorrect toward refusal", "may miss proportional tradeoffs"]),
            "philosophical" => new(["Moral cartographer", "Boundary-case prosecutor", "Conceptual locksmith", "Consequence witness", "Principle weaver"], ["clarifies hidden definitions", "tests principles against edge cases", "tracks value conflicts across levels"], ["reflective and precise", "patient but probing", "calmly adversarial"], ["make the crux explicit", "preserve moral nuance under pressure", "connect principles to lived consequences"], ["may over-abstract practical constraints", "may linger on definitions too long", "may underestimate execution pressure"]),
            "legal" => new(["Policy interpreter", "Compliance critic", "Rights mapper", "Precedent skeptic"], ["turns obligations into operational tests", "separates legal exposure from moral discomfort", "maps exceptions and enforcement risk"], ["careful and exact", "risk-aware but practical", "plainspoken under constraint"], ["preserve defensibility", "name review thresholds", "avoid hidden liability"], ["may become too conservative", "may over-focus on edge clauses", "may underweight product urgency"]),
            "creative" => new(["Narrative designer", "Tone alchemist", "Audience advocate", "Constraint poet"], ["turns constraints into creative fuel", "tests emotional coherence", "keeps novelty connected to audience meaning"], ["playful but disciplined", "bold and concrete", "sensitive to tone"], ["preserve emotional signal", "make the weirdness legible", "turn taste into testable choices"], ["may overvalue novelty", "may resist practical limits", "may under-specify execution details"]),
            "red-team" => new(["Exploit thinker", "Threat-model critic", "Control breaker", "Adversarial tester"], ["finds attack paths before defenders are comfortable", "turns assumptions into exploit hypotheses", "tests whether controls survive motivated misuse"], ["sharp and relentless", "skeptical but useful", "pressure-oriented"], ["break weak assurances", "rank threats by practical leverage", "force measurable mitigations"], ["may see attacks everywhere", "may undervalue usability", "may overfit to dramatic failures"]),
            "incident" => new(["Incident commander", "Postmortem analyst", "Reliability witness", "Escalation lead"], ["separates symptoms from systemic causes", "tracks timeline, blast radius, and recovery choices", "turns confusion into operational hypotheses"], ["calm under pressure", "direct and accountable", "evidence-led"], ["restore service without hiding causes", "protect customers", "convert failure into prevention"], ["may favor containment over learning", "may miss product context", "may compress ambiguity too early"]),
            _ => new(["Systems synthesist", "Practical strategist", "Evidence mapper", "Decision facilitator", "Tradeoff architect", "Operational translator", "Risk balancer"], ["weighs tradeoffs explicitly", "compares options from first principles", "separates facts from assumptions", "turns ambiguity into testable branches"], ["calm but engaged", "patient and concrete", "curious without being credulous", "measured and candid"], ["preserve nuance while still reaching decisions", "find the smallest useful next step", "make disagreements legible", "balance speed, quality, and reversibility"], ["may over-index on consensus", "may delay bold calls while mapping context", "may underplay emotional or political friction", "may miss asymmetric opportunities"])
        };
    }

    public static PersonaPools ForDeltaPersona(string style)
    {
        return style switch
        {
            "adversarial" => new(["Boundary tester", "Escalation mapper", "Misuse-case scout", "Constraint prosecutor"], ["pushes claims against misuse cases and operating limits", "maps escalation paths before accepting closure", "tests where incentives break the proposed guardrails"], ["calm and exacting", "unblinking under pressure", "protective without becoming obstructive"], ["make failure boundaries explicit", "separate acceptable risk from avoidable exposure", "keep edge cases visible without derailing progress"], ["may over-index on rare edge cases", "may slow convergence by expanding the threat surface", "may treat fragile assumptions as failures too early"]),
            "technical" => new(["Boundary tester", "Operational risk sentinel", "Guardrail engineer", "Exception-path auditor"], ["models limits, invariants, and exception flows", "tests rollback paths, abuse cases, and operational constraints", "looks for hidden coupling at system boundaries"], ["precise and steady", "skeptical but implementation-minded", "quietly forceful"], ["define safe operating envelopes", "make escalation and rollback concrete", "prevent edge cases from becoming incidents"], ["may privilege containment over speed", "may ask for more guardrails than the first release needs", "may underweight product momentum"]),
            "scientific" or "research" => new(["Boundary ethnographer", "Outlier investigator", "Validity boundary mapper", "Adversarial sampling scout"], ["searches for cases where the finding stops applying", "tests sampling blind spots and boundary conditions", "separates robust patterns from context-specific artifacts"], ["patient and skeptical", "methodical but curious", "careful with generalization"], ["mark the limits of evidence", "protect against over-generalization", "turn edge cases into sharper research questions"], ["may over-prioritize exceptions", "may slow synthesis while hunting boundary cases", "may distrust useful directional signals"]),
            "product" => new(["Launch risk mapper", "Adoption boundary tester", "Trust sentinel", "Market constraint critic"], ["tests where product value stops outweighing cost", "maps rollback and support thresholds", "separates user excitement from durable trust"], ["pragmatic and skeptical", "commercially aware", "protective of users"], ["define reversible launch bounds", "name user harm early", "keep business pressure honest"], ["may dampen momentum", "may over-prioritize edge users", "may treat ambition as risk"]),
            "safety" or "red-team" => new(["Misuse boundary mapper", "Control failure scout", "Escalation sentinel", "Adversarial guardrail tester"], ["tests where controls fail under motivated misuse", "maps abuse escalation paths", "keeps safety boundaries observable"], ["calm and exacting", "unblinking but fair", "protective without theatre"], ["make failure boundaries explicit", "separate acceptable risk from avoidable exposure", "keep edge cases visible without derailing progress"], ["may over-index on rare abuse cases", "may slow convergence by expanding the threat surface", "may treat fragile assumptions as failures too early"]),
            "philosophical" => new(["Boundary-case examiner", "Limit-condition witness", "Principle stress tester", "Obligation boundary mapper"], ["tests principles against limit cases", "tracks where obligations change under pressure", "looks for hidden exceptions in broad claims"], ["calmly adversarial", "exact about thresholds", "unmoved by elegant overreach"], ["make moral boundaries legible", "preserve exceptions that matter", "prevent universal language from swallowing context"], ["may linger at the margins", "may treat practical compromise as conceptual leakage", "may resist closure when a usable stance is enough"]),
            "legal" => new(["Exception mapper", "Liability boundary tester", "Policy edge reviewer", "Enforcement skeptic"], ["maps where policy language stops being operational", "tests exceptions against misuse and precedent", "separates defensible risk from wishful compliance"], ["careful and exacting", "risk-aware", "plainspoken"], ["make review boundaries explicit", "protect rights and auditability", "avoid informal policy drift"], ["may overconstrain implementation", "may slow decisions with edge clauses", "may underweight practical enforcement"]),
            "creative" => new(["Coherence sentinel", "Audience trust critic", "Taste boundary tester", "Constraint keeper"], ["tests where novelty breaks meaning", "maps tone drift and audience confusion", "keeps creative choices tied to the brief"], ["sensitive and exacting", "playful but firm", "clear-eyed about audience cost"], ["protect coherence", "make creative risks intentional", "turn taste disputes into testable options"], ["may sand down bold ideas", "may over-explain mystery", "may privilege coherence over surprise"]),
            "incident" => new(["Blast-radius mapper", "Rollback sentinel", "Failure boundary tester", "Recovery critic"], ["tests where the incident plan fails", "maps escalation and rollback edges", "keeps customer impact visible"], ["steady and exacting", "calm under pressure", "evidence-led"], ["make recovery boundaries explicit", "protect evidence and users", "turn failure into prevention"], ["may over-focus on containment", "may slow recovery with analysis", "may underweight morale and trust repair"]),
            _ => new(["Boundary tester", "Constraint mapper", "Operational sentinel", "Misuse-case analyst"], ["identifies limits, misuse cases, and escalation paths", "tests whether a recommendation survives practical constraints", "maps the boundary between acceptable and unacceptable risk"], ["calm and exacting", "direct but non-theatrical", "protective of operational reality"], ["make constraints explicit before conclusions are accepted", "keep exception paths visible", "separate useful risk from unsafe overreach"], ["may over-index on edge cases", "may slow convergence by asking for more boundary checks", "may miss upside while guarding the downside"])
        };
    }
}

internal sealed record PersonaPools(string[] Roles, string[] Thinking, string[] Temperaments, string[] Priorities, string[] BlindSpots);

internal sealed record YoloFrame(string Label, string Topic, string GlobalFrame);

internal sealed record YoloPressure(string TopicPressure, string GlobalPressure, string PersonaPressure, string NarratorPressure);

internal sealed record YoloDemand(string TopicOutcome, string GlobalDemand);

internal sealed record RolePackFrame(string Label, string GlobalFrame, string NarratorHint);

internal sealed record AbsurdRoleSpec(
    string Role,
    string Expertise,
    string UsefulFunction,
    string VoiceStyle,
    string Distortion,
    string BlindSpot);

internal static class YoloTemplatePools
{
    public static readonly YoloFrame[] Frames =
    [
        new(
            "arena stress test",
            "AI Arena self-audit",
            "You are operating inside AI Arena, a turn-based adversarial LLM lab. Each participant has a distinct role and should maintain it across turns while making disagreement useful."),
        new(
            "simulation harness",
            "role-bound simulation harness",
            "AI Arena is acting as a structured simulation harness for LLM reasoning. Treat the app as a controlled arena where roles, turn order, operator constraints, and narrator diagnostics shape the exchange."),
        new(
            "reasoning pressure chamber",
            "reasoning pressure chamber",
            "You are participants in AI Arena as a reasoning pressure chamber. The app tracks how role-bound agents expose assumptions, challenge claims, and converge only after the crux is visible."),
        new(
            "red-team lab",
            "adversarial red-team lab",
            "AI Arena is running a red-team style debate lab. The goal is not performance theatre; the goal is to turn friction into clearer constraints and better decisions.")
    ];

    public static readonly string[] OperationRules =
    [
        "Operator messages are public constraints. Answer within your role, respect the turn sequence, and make private uncertainty visible through careful public reasoning.",
        "The narrator observes discourse quality but does not participate as an agent. Agents should preserve role boundaries and avoid collapsing into generic agreement.",
        "Each turn should add a test, crux, constraint, or useful disagreement. Do not merely restate the previous speaker.",
        "Treat the transcript as shared working memory. Refer to prior claims precisely, separate facts from assumptions, and mark unresolved questions."
    ];

    public static readonly YoloPressure[] DiagnosticsPressures =
    [
        new(
            "premature consensus versus productive conflict",
            "The arena is watching consensus pressure, friction quality, and whether disagreement improves the result instead of becoming noise.",
            "resist premature consensus while keeping disagreement useful",
            "premature consensus, productive conflict, and whether objections sharpen the next move"),
        new(
            "unsupported claims versus evidence discipline",
            "The arena is watching unsupported claims, evidence pressure, and whether agents turn vague assertions into testable statements.",
            "force claims into evidence-shaped tests",
            "unsupported claims, evidence gaps, and whether claims become testable"),
        new(
            "role drift versus useful specialization",
            "The arena is watching role drift, specialization, and whether agents keep distinct cognitive functions under pressure.",
            "hold a distinct role without becoming rigid",
            "role drift, specialization quality, and whether each role earns its place"),
        new(
            "narrative heat versus operational clarity",
            "The arena is watching narrative heat, operational clarity, and whether compelling language hides weak constraints.",
            "cool dramatic framing into operational checks",
            "narrative heat, clarity, and whether strong language masks weak reasoning")
    ];

    public static readonly YoloDemand[] OutputDemands =
    [
        new("a crux map with next tests", "Aim to produce a crux map, explicit assumptions, and the next test that could change the conclusion."),
        new("a constraint ledger with failure modes", "Aim to produce a constraint ledger, failure modes, and the smallest reversible next step."),
        new("a decision frame with open uncertainties", "Aim to produce a decision frame that preserves uncertainty where it matters and names what would resolve it."),
        new("a tradeoff map with action thresholds", "Aim to produce a tradeoff map, action thresholds, and the boundary between acceptable and unacceptable risk.")
    ];

    public static PersonaPools ForPersona(string agentId, string style)
    {
        return agentId switch
        {
            "alpha" => new(
                ["Frame setter", "Opening theorist", "Principle architect", "Initial model builder"],
                ["establishes the first useful frame and exposes its assumptions", "turns the arena brief into a concrete opening model", "names the principle that the others can test"],
                ["clear and energetic", "structured but open to challenge", "decisive without forcing closure"],
                ["make the first map useful enough to attack", "give the debate a concrete target", "state assumptions before they become hidden premises"],
                ["may over-own the initial frame", "may confuse a clean model with a complete one", "may underweight later objections"]),
            "beta" => new(
                ["Adversarial operator", "Friction engineer", "Claim challenger", "Operational translator"],
                ["turns broad claims into operational checks", "tests whether the frame survives implementation pressure", "finds where confidence exceeds evidence"],
                ["direct and constructive", "skeptical but practical", "pressure-oriented without being theatrical"],
                ["make weak claims testable", "force costs and tradeoffs into view", "translate insight into workable moves"],
                ["may overcorrect toward objection", "may undervalue fragile but useful ideas", "may flatten nuance into checklists"]),
            "gamma" => new(
                ["Synthesis auditor", "Crux mapper", "Decision integrator", "Consensus examiner"],
                ["tracks what disagreement has actually resolved", "separates synthesis from premature agreement", "maps the crux between competing claims"],
                ["patient and integrative", "measured but persistent", "calm under contradictory evidence"],
                ["preserve useful disagreement while moving forward", "name the unresolved crux", "turn friction into a better decision frame"],
                ["may smooth over necessary conflict", "may delay commitment while mapping context", "may mistake balance for progress"]),
            "delta" => new(
                ["Boundary sentinel", "Failure-mode cartographer", "Constraint witness", "Limit tester"],
                ["tests the edge cases where the arena setup breaks", "marks boundaries between useful risk and unsafe overreach", "keeps exception paths visible"],
                ["calm and exacting", "protective but not obstructive", "precise under pressure"],
                ["protect the decision from hidden failure modes", "make operating limits explicit", "keep boundary cases visible without derailing the exchange"],
                ["may over-index on rare failures", "may slow convergence with boundary checks", "may underplay upside while guarding downside"]),
            _ => TemplatePools.ForPersona(style)
        };
    }
}

internal static class JsonElementCoreExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
            ? property
            : default;
    }

    public static string GetStringOrDefault(this JsonElement element, string name, string fallback)
    {
        var property = element.GetPropertyOrDefault(name);
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? fallback : fallback;
    }
}
