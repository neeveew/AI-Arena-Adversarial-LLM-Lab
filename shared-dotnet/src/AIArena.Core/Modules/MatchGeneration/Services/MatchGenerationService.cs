using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed class MatchGenerationService
{
    private static readonly string[] Styles = ["balanced", "adversarial", "research", "technical", "philosophical"];
    private readonly IModelProviderClient _modelClient;
    private readonly SessionStore _sessionStore;
    private readonly EventLogStore _eventLogStore;

    public MatchGenerationService(IModelProviderClient? modelClient = null, SessionStore? sessionStore = null, EventLogStore? eventLogStore = null)
    {
        _modelClient = modelClient ?? new ModelProviderClient();
        _sessionStore = sessionStore ?? new SessionStore();
        _eventLogStore = eventLogStore ?? new EventLogStore(_sessionStore.DataRoot);
    }

    public async Task<MatchGenerationResult> GenerateRandomSeedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var seed = Guid.NewGuid().ToString("N")[..12];
        var style = RandomStyle(seed, snapshot.MatchType);
        var generated = GenerateTemplateMatch(style, seed, RequiredAgentIds(snapshot));
        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.ScenarioGenerator.Style = style;
        snapshot.ScenarioGenerator.Seed = seed;
        snapshot.PersonaRandomizer.Style = style is "technical" ? "technical" : style is "adversarial" ? "adversarial" : "balanced";
        snapshot.PersonaRandomizer.Seed = seed;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_random_seed_match_generated", new { seed, style, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, seed, style);
    }

    public async Task<MatchGenerationResult> GenerateAiChoiceAsync(string sessionId, CancellationToken cancellationToken = default)
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

        var prompt = BuildAiChoicePrompt(snapshot);
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
        snapshot.PersonaRandomizer.Style = snapshot.MatchType is "technical" ? "technical" : snapshot.MatchType is "adversarial" ? "adversarial" : "balanced";
        snapshot.PersonaRandomizer.Seed = "ai-choice";

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_ai_choice_match_generated", new { generated.Label, generated.Style, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, "ai-choice", generated.Style);
    }

    public async Task<MatchGenerationResult> GenerateMetaScenarioAsync(string sessionId, CancellationToken cancellationToken = default)
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

        var prompt = BuildMetaScenarioPrompt(snapshot);
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

        if (!TryParseGeneratedMatch(result.Text, RequiredAgentIds(snapshot), "Meta scenario", "meta-scenario", out var generated, out var error))
        {
            snapshot.Engine.LastError = error;
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            return MatchGenerationResult.Failed(error);
        }

        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.MatchType = NormalizeStyle(generated.Style);
        snapshot.ScenarioGenerator.Style = snapshot.MatchType;
        snapshot.ScenarioGenerator.Seed = "meta-scenario";
        snapshot.PersonaRandomizer.Style = snapshot.MatchType is "technical" ? "technical" : snapshot.MatchType is "adversarial" ? "adversarial" : "balanced";
        snapshot.PersonaRandomizer.Seed = "meta-scenario";

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_meta_scenario_generated", new { generated.Label, generated.Style, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, "meta-scenario", generated.Style);
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

    private static GeneratedMatch GenerateTemplateMatch(string style, string seed, IReadOnlyList<string> agentIds)
    {
        var rng = new Random(StableSeed($"ai-arena-wpf:{style}:{seed}:{string.Join(",", agentIds)}"));
        var pools = TemplatePools.For(style);
        var domain = Pick(rng, pools.Domains);
        var tension = Pick(rng, pools.Tensions);
        var outcome = Pick(rng, pools.Outcomes);
        var topic = $"{domain}: resolve {tension} and produce {outcome}.";
        var global = $"Stay focused on {topic} Surface assumptions, define terms, and keep the exchange concrete. Do not fetch external news or live data unless the operator explicitly provides it.";
        var personas = agentIds
            .Where(id => id is "alpha" or "beta" or "gamma" or "delta")
            .Select(id => GeneratedPersona.For(style, seed, id))
            .Append(GeneratedPersona.Narrator(style, seed))
            .ToArray();
        return new GeneratedMatch(
            $"Random {style} match",
            style,
            topic,
            global,
            $"Track how the participants handle {tension}. Highlight unresolved cruxes, evidence gaps, and the path toward {outcome}.",
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
                }
                continue;
            }

            var agent = snapshot.Engine.Agents.FirstOrDefault(item => string.Equals(item.Id, persona.AgentId, StringComparison.OrdinalIgnoreCase));
            if (agent is not null && !IsLocked(locks, agent.Id))
            {
                agent.Name = AgentLabel(agent.Id, persona.Role);
                agent.Persona = persona.Persona;
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

    private static IReadOnlyList<ModelChatMessage> BuildAiChoicePrompt(ArenaSnapshot snapshot)
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
                    $"Current topic: {snapshot.Engine.Steering.Topic}",
                    $"Current cast:{Environment.NewLine}{agents}",
                    "Return this JSON shape:",
                    $"{{\"label\":\"short label\",\"style\":\"balanced|adversarial|research|technical|philosophical\",\"scenario\":{{\"topic\":\"...\",\"global\":\"...\",\"narrator_brief\":\"...\"}},\"personas\":[{PersonaJsonShape(snapshot)},{{\"agent_id\":\"narrator\",\"role\":\"Narrator\",\"persona\":\"...\"}}]}}"))
        ];
    }

    private static IReadOnlyList<ModelChatMessage> BuildMetaScenarioPrompt(ArenaSnapshot snapshot)
    {
        var agents = string.Join(Environment.NewLine, snapshot.Engine.Agents.Select(agent => $"- {agent.Id}: {agent.Name}: {agent.Persona}"));
        var locked = snapshot.MatchLocks
            .Where(item => item.Value)
            .Select(item => item.Key)
            .OrderBy(item => item)
            .ToArray();
        return
        [
            new ModelChatMessage(
                "system",
                "You generate AI Arena meta-scenario setup JSON. Return only valid JSON. No markdown. The setup must describe the arena simulation clearly to the LLM participants without becoming theatrical roleplay."),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    "Create a meta scenario for an adversarial LLM arena.",
                    "The participants should understand they are role-bound agents inside a structured simulation.",
                    "The goal is to stress-test reasoning, assumptions, disagreement, synthesis, constraint handling, and narrator diagnostics.",
                    "Make the scenario concrete, tense, and testable. Avoid generic sci-fi framing.",
                    "The global instruction must explain the simulation rules: distinct roles, public debate, turn order, narrator observation, evidence discipline, and productive disagreement.",
                    "The narrator must track discourse quality without joining as Alpha, Beta, Gamma, or Delta.",
                    "Give every participant a sharp cognitive role that fits the meta simulation.",
                    $"Current match type: {snapshot.MatchType}",
                    $"Locked fields that must be respected by the app after generation: {(locked.Length == 0 ? "none" : string.Join(", ", locked))}",
                    $"Current topic: {snapshot.Engine.Steering.Topic}",
                    $"Current cast:{Environment.NewLine}{agents}",
                    "Return this JSON shape:",
                    $"{{\"label\":\"short label\",\"style\":\"balanced|adversarial|research|technical|philosophical\",\"scenario\":{{\"topic\":\"...\",\"global\":\"...\",\"narrator_brief\":\"...\"}},\"personas\":[{PersonaJsonShape(snapshot)},{{\"agent_id\":\"narrator\",\"role\":\"Narrator\",\"persona\":\"...\"}}]}}"))
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
                        item.GetStringOrDefault("persona", "")))
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

    private static string Pick(Random rng, IReadOnlyList<string> values) => values[rng.Next(values.Count)];

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
            RequiredAgentIds(snapshot).Select(id => $"{{\"agent_id\":\"{id}\",\"role\":\"...\",\"persona\":\"...\"}}"));
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

public sealed record MatchGenerationResult(bool Ok, string Label, string Seed, string Style, string Error)
{
    public static MatchGenerationResult Completed(string label, string seed, string style) => new(true, label, seed, style, "");
    public static MatchGenerationResult Failed(string error) => new(false, "", "", "", error);
}

internal sealed record GeneratedMatch(string Label, string Style, string Topic, string Global, string NarratorBrief, IReadOnlyList<GeneratedPersona> Personas)
{
    public static GeneratedMatch Empty { get; } = new("", "balanced", "", "", "", []);
}

internal sealed record GeneratedPersona(string AgentId, string Role, string Persona)
{
    public static GeneratedPersona For(string style, string seed, string agentId)
    {
        var rng = new Random(MatchGenerationServiceSeed($"persona:{style}:{seed}:{agentId}"));
        var pools = agentId.Equals("delta", StringComparison.OrdinalIgnoreCase)
            ? TemplatePools.ForDeltaPersona(style)
            : TemplatePools.ForPersona(style);
        var role = pools.Roles[rng.Next(pools.Roles.Length)];
        var thinking = pools.Thinking[rng.Next(pools.Thinking.Length)];
        var temperament = pools.Temperaments[rng.Next(pools.Temperaments.Length)];
        var priority = pools.Priorities[rng.Next(pools.Priorities.Length)];
        var blindSpot = pools.BlindSpots[rng.Next(pools.BlindSpots.Length)];
        return new GeneratedPersona(
            agentId,
            role,
            $"{role}. Thinking style: {thinking}. Temperament: {temperament}. Priority/bias: {priority}. Blind spot: {blindSpot}.");
    }

    public static GeneratedPersona Narrator(string style, string seed)
    {
        var persona = For(style, seed, "narrator");
        var role = style switch
        {
            "adversarial" => "Critical observer",
            "technical" => "Process auditor",
            "philosophical" => "Conceptual observer",
            _ => "Neutral observer"
        };
        return persona with { Role = role, Persona = $"{role}. Track the exchange without joining as Alpha, Beta, Gamma, or Delta. {persona.Persona}" };
    }

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
            "research" => new(["an unresolved empirical question", "a weakly understood user behavior", "a competing-hypothesis investigation"], ["signal versus noise", "exploration versus confirmation", "anecdote versus measurement"], ["testable hypotheses", "an evidence plan", "next research questions"]),
            "technical" => new(["a production architecture decision", "a reliability incident review", "a scaling bottleneck"], ["complexity versus control", "latency versus correctness", "migration risk versus technical debt"], ["an implementation plan", "explicit invariants", "a test and rollback strategy"]),
            "philosophical" => new(["a question about responsibility and agency", "a value conflict in automation", "an ethical boundary case"], ["principles versus consequences", "individual agency versus system effects", "freedom versus obligation"], ["clearer concepts", "the crux of disagreement", "a principled but usable stance"]),
            _ => new(["a difficult product decision", "a public-interest technology tradeoff", "a team strategy reset"], ["speed versus care", "autonomy versus coordination", "short-term wins versus durable value"], ["a practical recommendation", "a map of tradeoffs", "a reversible next step"])
        };
    }

    public static PersonaPools ForPersona(string style)
    {
        return style switch
        {
            "adversarial" => new(["Red-team examiner", "Failure-mode hunter", "Contrarian reviewer"], ["stress-tests claims before accepting them", "looks for hidden incentives and edge cases", "tests weak premises constructively"], ["sharp but fair", "skeptical and persistent", "direct but cooperative under uncertainty"], ["surface risks early", "clarify evidence standards", "protect against overconfidence"], ["may undervalue fragile early ideas", "may mistake caution for rigor", "may over-focus on downside scenarios"]),
            "technical" => new(["Architecture critic", "Implementation planner", "Reliability engineer"], ["models interfaces, invariants, and failure modes", "reduces problems to testable mechanisms", "tracks dependencies and state transitions"], ["precise and cooperative", "methodical under pressure", "pragmatic and detail-oriented"], ["make behavior explicit", "reduce operational risk", "keep abstractions accountable"], ["may underweight user emotion", "may ask for more structure than the operator needs", "may focus on internals before outcomes"]),
            "research" => new(["Field ethnographer", "Statistical skeptic", "Hypothesis gardener", "Evidence cartographer", "Replication hawk"], ["turns vague questions into observable claims", "separates causal evidence from correlation", "maps competing hypotheses without flattening them"], ["patient and empirical", "quietly skeptical", "curious but disciplined"], ["design tests that could change minds", "protect against overfitting anecdotes", "rank evidence by decision value"], ["may move slowly while improving the question", "may distrust useful intuition", "may over-prioritize clean measurement"]),
            "philosophical" => new(["Moral cartographer", "Boundary-case prosecutor", "Conceptual locksmith", "Consequence witness", "Principle weaver"], ["clarifies hidden definitions", "tests principles against edge cases", "tracks value conflicts across levels"], ["reflective and precise", "patient but probing", "calmly adversarial"], ["make the crux explicit", "preserve moral nuance under pressure", "connect principles to lived consequences"], ["may over-abstract practical constraints", "may linger on definitions too long", "may underestimate execution pressure"]),
            _ => new(["Systems synthesist", "Practical strategist", "Evidence mapper", "Decision facilitator", "Tradeoff architect", "Operational translator", "Risk balancer"], ["weighs tradeoffs explicitly", "compares options from first principles", "separates facts from assumptions", "turns ambiguity into testable branches"], ["calm but engaged", "patient and concrete", "curious without being credulous", "measured and candid"], ["preserve nuance while still reaching decisions", "find the smallest useful next step", "make disagreements legible", "balance speed, quality, and reversibility"], ["may over-index on consensus", "may delay bold calls while mapping context", "may underplay emotional or political friction", "may miss asymmetric opportunities"])
        };
    }

    public static PersonaPools ForDeltaPersona(string style)
    {
        return style switch
        {
            "adversarial" => new(["Boundary tester", "Escalation mapper", "Misuse-case scout", "Constraint prosecutor"], ["pushes claims against misuse cases and operating limits", "maps escalation paths before accepting closure", "tests where incentives break the proposed guardrails"], ["calm and exacting", "unblinking under pressure", "protective without becoming obstructive"], ["make failure boundaries explicit", "separate acceptable risk from avoidable exposure", "keep edge cases visible without derailing progress"], ["may over-index on rare edge cases", "may slow convergence by expanding the threat surface", "may treat fragile assumptions as failures too early"]),
            "technical" => new(["Boundary tester", "Operational risk sentinel", "Guardrail engineer", "Exception-path auditor"], ["models limits, invariants, and exception flows", "tests rollback paths, abuse cases, and operational constraints", "looks for hidden coupling at system boundaries"], ["precise and steady", "skeptical but implementation-minded", "quietly forceful"], ["define safe operating envelopes", "make escalation and rollback concrete", "prevent edge cases from becoming incidents"], ["may privilege containment over speed", "may ask for more guardrails than the first release needs", "may underweight product momentum"]),
            "research" => new(["Boundary ethnographer", "Outlier investigator", "Validity boundary mapper", "Adversarial sampling scout"], ["searches for cases where the finding stops applying", "tests sampling blind spots and boundary conditions", "separates robust patterns from context-specific artifacts"], ["patient and skeptical", "methodical but curious", "careful with generalization"], ["mark the limits of evidence", "protect against over-generalization", "turn edge cases into sharper research questions"], ["may over-prioritize exceptions", "may slow synthesis while hunting boundary cases", "may distrust useful directional signals"]),
            "philosophical" => new(["Boundary-case examiner", "Limit-condition witness", "Principle stress tester", "Obligation boundary mapper"], ["tests principles against limit cases", "tracks where obligations change under pressure", "looks for hidden exceptions in broad claims"], ["calmly adversarial", "exact about thresholds", "unmoved by elegant overreach"], ["make moral boundaries legible", "preserve exceptions that matter", "prevent universal language from swallowing context"], ["may linger at the margins", "may treat practical compromise as conceptual leakage", "may resist closure when a usable stance is enough"]),
            _ => new(["Boundary tester", "Constraint mapper", "Operational sentinel", "Misuse-case analyst"], ["identifies limits, misuse cases, and escalation paths", "tests whether a recommendation survives practical constraints", "maps the boundary between acceptable and unacceptable risk"], ["calm and exacting", "direct but non-theatrical", "protective of operational reality"], ["make constraints explicit before conclusions are accepted", "keep exception paths visible", "separate useful risk from unsafe overreach"], ["may over-index on edge cases", "may slow convergence by asking for more boundary checks", "may miss upside while guarding the downside"])
        };
    }
}

internal sealed record PersonaPools(string[] Roles, string[] Thinking, string[] Temperaments, string[] Priorities, string[] BlindSpots);

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
