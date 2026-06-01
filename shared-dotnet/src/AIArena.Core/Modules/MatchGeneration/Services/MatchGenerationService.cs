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
        var generated = ScenarioSeedGenerator.GenerateTemplateMatch(style, seed, intensity, rolePack, absurdity, RequiredAgentIds(snapshot));
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
        var generated = ScenarioSeedGenerator.GenerateYoloMatch(style, seed, rolePack, intensity, absurdity, RequiredAgentIds(snapshot));
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
                    $"Requested role pack: {RolePackFrames.For(rolePack).Label}.",
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
                output.Add(GeneratedPersonaFactory.For(style, seed, required));
            }
        }

        if (output.All(persona => persona.AgentId != "narrator"))
        {
            output.Add(GeneratedPersonaFactory.Narrator(style, seed));
        }

        return output.ToArray();
    }

    private static string RandomStyle(string seed, string currentStyle)
    {
        var choices = Styles.Where(style => !style.Equals(NormalizeStyle(currentStyle), StringComparison.OrdinalIgnoreCase)).ToArray();
        return choices[Math.Abs(StableSeed($"style:{seed}")) % choices.Length];
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

internal sealed record GeneratedPersona(string AgentId, string Role, string Persona, string VoiceStyle = "default");

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
