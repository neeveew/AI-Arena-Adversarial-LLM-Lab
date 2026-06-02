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
    private static readonly string[] Intensities = ["normal", "sharp", "spicy", "chaos", "one_line"];
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
        string? replaySeed = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var seed = string.IsNullOrWhiteSpace(replaySeed) ? Guid.NewGuid().ToString("N")[..12] : replaySeed.Trim();
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
        RecordGenerationHistory(snapshot, "random", generated, seed, seed, intensity, rolePack, absurdity);

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

        var config = ModelProviderRouting.Resolve(snapshot, "narrator", out var fallbackConfig);
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
        RecordGenerationHistory(snapshot, "ai_choice", generated, "ai-choice", "ai-choice", intensity, rolePack, absurdity);

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_ai_choice_match_generated", new { generated.Label, generated.Style, rolePack, absurdity, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, "ai-choice", generated.Style, intensity);
    }

    public async Task<MatchGenerationResult> GenerateYoloSeedAsync(
        string sessionId,
        string requestedRolePack = "auto",
        string requestedIntensity = "normal",
        string requestedAbsurdity = "grounded",
        string? replaySeed = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var seed = string.IsNullOrWhiteSpace(replaySeed)
            ? $"yolo-{Guid.NewGuid():N}"[..13].ToUpperInvariant()
            : replaySeed.Trim();
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
        RecordGenerationHistory(snapshot, "yolo", generated, seed, seed, intensity, rolePack, absurdity);

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_yolo_seed_match_generated", new { seed, style, generated.Label, rolePack, absurdity, locks = snapshot.MatchLocks }, cancellationToken);
        return MatchGenerationResult.Completed(generated.Label, seed, style, intensity);
    }

    public async Task<MatchGenerationResult> ReplayGenerationAsync(string sessionId, string historyId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var entry = snapshot.GenerationHistory.FirstOrDefault(item => item.Id.Equals(historyId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return MatchGenerationResult.Failed("Generation history item not found.");
        }

        var generated = FromHistory(entry.Match);
        ApplyGeneratedMatch(snapshot, generated, clearTranscript: false);
        snapshot.MatchType = NormalizeStyle(generated.Style);
        snapshot.ScenarioGenerator.Style = snapshot.MatchType;
        snapshot.ScenarioGenerator.Seed = entry.ScenarioSeed;
        snapshot.ScenarioGenerator.Intensity = NormalizeIntensity(entry.Intensity);
        snapshot.ScenarioGenerator.RolePack = NormalizeRolePack(entry.RolePack);
        snapshot.ScenarioGenerator.Absurdity = NormalizeAbsurdity(entry.Absurdity);
        snapshot.PersonaRandomizer.Style = entry.Kind.Equals("yolo", StringComparison.OrdinalIgnoreCase)
            ? "yolo"
            : PersonaStyleFor(snapshot.MatchType);
        snapshot.PersonaRandomizer.Seed = entry.PersonaSeed;
        snapshot.PersonaRandomizer.Intensity = snapshot.ScenarioGenerator.Intensity;
        snapshot.PersonaRandomizer.RolePack = snapshot.ScenarioGenerator.RolePack;
        snapshot.PersonaRandomizer.Absurdity = snapshot.ScenarioGenerator.Absurdity;
        RecordGenerationHistory(snapshot, entry.Kind, generated, entry.ScenarioSeed, entry.PersonaSeed, snapshot.ScenarioGenerator.Intensity, snapshot.ScenarioGenerator.RolePack, snapshot.ScenarioGenerator.Absurdity);

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_generation_replayed", new { entry.Id, entry.Kind, entry.Label, seed = entry.ScenarioSeed }, cancellationToken);
        return MatchGenerationResult.Completed($"Replayed {entry.Label}", entry.ScenarioSeed, generated.Style, entry.Intensity);
    }

    public async Task<MatchGenerationResult> ReplayGenerationToNewSessionAsync(string sessionId, string historyId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return MatchGenerationResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var entry = snapshot.GenerationHistory.FirstOrDefault(item => item.Id.Equals(historyId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return MatchGenerationResult.Failed("Generation history item not found.");
        }

        var generated = FromHistory(entry.Match);
        ApplyGeneratedMatch(snapshot, generated, clearTranscript: true);
        snapshot.MatchType = NormalizeStyle(generated.Style);
        snapshot.ScenarioGenerator.Style = snapshot.MatchType;
        snapshot.ScenarioGenerator.Seed = entry.ScenarioSeed;
        snapshot.ScenarioGenerator.Intensity = NormalizeIntensity(entry.Intensity);
        snapshot.ScenarioGenerator.RolePack = NormalizeRolePack(entry.RolePack);
        snapshot.ScenarioGenerator.Absurdity = NormalizeAbsurdity(entry.Absurdity);
        snapshot.PersonaRandomizer.Style = entry.Kind.Equals("yolo", StringComparison.OrdinalIgnoreCase)
            ? "yolo"
            : PersonaStyleFor(snapshot.MatchType);
        snapshot.PersonaRandomizer.Seed = entry.PersonaSeed;
        snapshot.PersonaRandomizer.Intensity = snapshot.ScenarioGenerator.Intensity;
        snapshot.PersonaRandomizer.RolePack = snapshot.ScenarioGenerator.RolePack;
        snapshot.PersonaRandomizer.Absurdity = snapshot.ScenarioGenerator.Absurdity;
        RecordGenerationHistory(snapshot, entry.Kind, generated, entry.ScenarioSeed, entry.PersonaSeed, snapshot.ScenarioGenerator.Intensity, snapshot.ScenarioGenerator.RolePack, snapshot.ScenarioGenerator.Absurdity);

        var newSessionId = await CreateUniqueReplaySessionIdAsync(entry, cancellationToken);
        await _sessionStore.CreateSessionAsync(newSessionId, snapshot, cancellationToken);
        await _eventLogStore.AppendAsync(newSessionId, "native_generation_replay_run_created", new { source_session = sessionId, entry.Id, entry.Kind, entry.Label, seed = entry.ScenarioSeed }, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_generation_replay_run_created", new { replay_session = newSessionId, entry.Id, entry.Kind, entry.Label, seed = entry.ScenarioSeed }, cancellationToken);
        return MatchGenerationResult.Completed(newSessionId, entry.ScenarioSeed, generated.Style, entry.Intensity);
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

    private async Task<string> CreateUniqueReplaySessionIdAsync(GenerationHistoryEntry entry, CancellationToken cancellationToken)
    {
        var baseName = SessionStore.SafeSessionId($"run-{entry.Style}-{entry.ScenarioSeed}");
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        }

        var sessions = await _sessionStore.ListSessionsAsync(cancellationToken);
        var existing = sessions.Select(session => session.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName}-{index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{baseName}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    private static void RecordGenerationHistory(
        ArenaSnapshot snapshot,
        string kind,
        GeneratedMatch generated,
        string scenarioSeed,
        string personaSeed,
        string intensity,
        string rolePack,
        string absurdity)
    {
        var entry = new GenerationHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Kind = kind,
            Label = generated.Label,
            Style = generated.Style,
            Intensity = NormalizeIntensity(intensity),
            RolePack = NormalizeRolePack(rolePack),
            Absurdity = NormalizeAbsurdity(absurdity),
            ScenarioSeed = scenarioSeed,
            PersonaSeed = personaSeed,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Match = ToHistory(generated)
        };

        snapshot.GenerationHistory.Insert(0, entry);
        if (snapshot.GenerationHistory.Count > 20)
        {
            snapshot.GenerationHistory.RemoveRange(20, snapshot.GenerationHistory.Count - 20);
        }
    }

    private static GeneratedMatchSnapshot ToHistory(GeneratedMatch generated)
    {
        return new GeneratedMatchSnapshot
        {
            Label = generated.Label,
            Style = generated.Style,
            Topic = generated.Topic,
            Global = generated.Global,
            NarratorBrief = generated.NarratorBrief,
            Personas = generated.Personas
                .Select(persona => new GeneratedPersonaSnapshot
                {
                    AgentId = persona.AgentId,
                    Role = persona.Role,
                    Persona = persona.Persona,
                    VoiceStyle = persona.VoiceStyle
                })
                .ToList()
        };
    }

    private static GeneratedMatch FromHistory(GeneratedMatchSnapshot match)
    {
        return new GeneratedMatch(
            string.IsNullOrWhiteSpace(match.Label) ? "Replayed match" : match.Label,
            NormalizeStyle(match.Style),
            match.Topic,
            match.Global,
            match.NarratorBrief,
            match.Personas
                .Select(persona => new GeneratedPersona(persona.AgentId, persona.Role, persona.Persona, persona.VoiceStyle))
                .ToArray());
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
        var allowed = requiredAgentIds
            .Append("narrator")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = personas
            .Select(persona => persona with { AgentId = persona.AgentId.Trim().ToLowerInvariant() })
            .Where(persona => allowed.Contains(persona.AgentId))
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
            "one_line" => "Role pressure: answer in one high-signal sentence; make it sharp, useful, and memorable without becoming vague.",
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
            "one_line" => "one-line",
            _ => "normal"
        };
    }

    private static IReadOnlyList<string> RequiredAgentIds(ArenaSnapshot snapshot)
    {
        var existing = snapshot.Engine.Agents
            .Where(agent => agent.Active)
            .Select(agent => agent.Id.ToLowerInvariant())
            .Where(AgentRosterService.IsParticipantId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existing.Count == 0)
        {
            existing.Add("alpha");
        }

        return existing.OrderBy(ParticipantOrder).ToArray();
    }

    private static string NormalizeLockKey(string key)
    {
        var cleaned = string.IsNullOrWhiteSpace(key) ? "" : key.Trim().ToLowerInvariant();
        return AgentRosterService.IsParticipantId(cleaned) || cleaned is "narrator" or "topic" or "global" or "scenario"
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
            var id when AgentRosterService.IsParticipantId(id) => AgentRosterService.DisplayName(id),
            _ => agentId
        };
        return string.IsNullOrWhiteSpace(role) ? prefix : $"{prefix}: {role}";
    }

    private static string ParticipantList(ArenaSnapshot snapshot)
    {
        var names = RequiredAgentIds(snapshot)
            .Select(id => id switch
            {
                var participant when AgentRosterService.IsParticipantId(participant) => AgentRosterService.DisplayName(participant),
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
        return AgentRosterService.ParticipantOrder(id);
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
