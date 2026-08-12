using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

public sealed class ArenaSnapshot
{
    [JsonPropertyName("persistence_revision")]
    public long PersistenceRevision { get; set; }

    [JsonPropertyName("fork_lineage")]
    public SessionForkLineage? ForkLineage { get; set; }

    [JsonPropertyName("branch_receipt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArenaBranchContract? BranchReceipt { get; set; }

    [JsonPropertyName("configs")]
    public Dictionary<string, ModelProviderConfig> Configs { get; init; } = new();

    /// <summary>
    /// Canonical per-model behavior settings. Unlike role routes, this registry
    /// can retain settings for catalog models that are not currently assigned.
    /// Keys are opaque provider/model identities produced by
    /// <see cref="ModelRuntimeSettingsRegistry.Identity(ModelProviderConfig)"/>.
    /// </summary>
    [JsonPropertyName("model_settings")]
    public Dictionary<string, ModelRuntimeSettings> ModelSettings { get; init; } = new();

    /// <summary>
    /// Zero identifies snapshots created before canonical model settings existed.
    /// Current clean sessions explicitly use the current registry schema so their
    /// first newly selected model can adopt the Rolling 80 default safely.
    /// </summary>
    [JsonPropertyName("model_settings_version")]
    public int ModelSettingsVersion { get; set; }

    /// <summary>
    /// Opaque model-settings identities whose saved context configuration has
    /// not yet been authoritatively applied to a loaded provider instance.
    /// This is durable configuration intent, never residency evidence.
    /// </summary>
    [JsonPropertyName("pending_model_configuration_applies")]
    public HashSet<string> PendingModelConfigurationApplies { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("engine")]
    public EngineSnapshot Engine { get; init; } = new();

    [JsonPropertyName("match_type")]
    public string MatchType { get; set; } = "balanced";

    [JsonPropertyName("match_locks")]
    public Dictionary<string, bool> MatchLocks { get; init; } = new();

    [JsonPropertyName("scenario_generator")]
    public GeneratorState ScenarioGenerator { get; init; } = new();

    [JsonPropertyName("persona_randomizer")]
    public GeneratorState PersonaRandomizer { get; init; } = new();

    [JsonPropertyName("generation_history")]
    public List<GenerationHistoryEntry> GenerationHistory { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class SessionForkLineage
{
    [JsonPropertyName("parent_session_id")]
    public string ParentSessionId { get; init; } = "";

    [JsonPropertyName("parent_persistence_revision")]
    public long ParentPersistenceRevision { get; init; }

    [JsonPropertyName("parent_turn_count")]
    public int ParentTurnCount { get; init; }

    [JsonPropertyName("parent_message_count")]
    public int ParentMessageCount { get; init; }

    [JsonPropertyName("forked_at")]
    public long ForkedAt { get; init; }
}

public sealed class EngineSnapshot
{
    [JsonPropertyName("factory_mode")]
    public bool FactoryMode { get; set; }

    /// <summary>
    /// Controls whether Arena roles without an explicit provider assignment may
    /// use the shared provider configuration. The shared configuration itself
    /// remains available to connection diagnostics and Agent Workspace.
    /// </summary>
    [JsonPropertyName("default_for_unassigned_agents_enabled")]
    public bool DefaultForUnassignedAgentsEnabled { get; set; } = true;

    [JsonPropertyName("agents")]
    public List<DialogueAgent> Agents { get; init; } = new();

    [JsonPropertyName("messages")]
    public List<DialogueMessage> Messages { get; set; } = new();

    [JsonPropertyName("narration")]
    public List<NarrationEntry> Narration { get; init; } = new();

    [JsonPropertyName("attachments")]
    public List<AttachmentSnapshot> Attachments { get; init; } = new();

    [JsonPropertyName("research_items")]
    public List<ResearchItemSnapshot> ResearchItems { get; init; } = new();

    [JsonPropertyName("narrator")]
    public NarratorState Narrator { get; init; } = new();

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("decision_card")]
    public DecisionCardState DecisionCard { get; init; } = new();

    [JsonPropertyName("turn_count")]
    public int TurnCount { get; set; }

    [JsonPropertyName("turn_index")]
    public int TurnIndex { get; set; }

    [JsonPropertyName("match_ended")]
    public bool MatchEnded { get; set; }

    [JsonPropertyName("match_ended_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MatchEndedAt { get; set; }

    [JsonPropertyName("match_end_reason")]
    public string MatchEndReason { get; set; } = "";

    [JsonPropertyName("steering")]
    public Steering Steering { get; init; } = new();

    [JsonPropertyName("internet")]
    public InternetSettings Internet { get; init; } = new();

    [JsonPropertyName("rivalry_matrix")]
    public RivalryMatrixState RivalryMatrix { get; init; } = new();

    [JsonPropertyName("transcript_window")]
    public int TranscriptWindow { get; set; } = 30;

    [JsonPropertyName("private_window")]
    public int PrivateWindow { get; set; } = 12;

    [JsonPropertyName("notes_window")]
    public int NotesWindow { get; set; } = 8;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class DialogueAgent
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("persona")]
    public string Persona { get; set; } = "";

    [JsonPropertyName("voice_style")]
    public string VoiceStyle { get; set; } = "";

    [JsonPropertyName("pressure_profile")]
    public string PressureProfile { get; set; } = "";

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "";

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "waiting";

    [JsonPropertyName("private_notes")]
    public List<string> PrivateNotes { get; init; } = new();

    /// <summary>
    /// Provenance-bearing memory used by the turn runner. PrivateNotes remains a
    /// compatibility mirror for older AI Arena builds and external snapshot tools.
    /// </summary>
    [JsonPropertyName("memory_entries")]
    public List<StructuredMemoryEntry> MemoryEntries { get; init; } = new();

    [JsonPropertyName("private_notes_mirror_fingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivateNotesMirrorFingerprint { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class RivalryMatrixState
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("links")]
    public List<RivalryLink> Links { get; init; } = new();
}

public sealed class RivalryLink
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("stance")]
    public string Stance { get; set; } = "neutral";
}

public sealed class GenerationHistoryEntry
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("style")]
    public string Style { get; init; } = "";

    [JsonPropertyName("intensity")]
    public string Intensity { get; init; } = "";

    [JsonPropertyName("role_pack")]
    public string RolePack { get; init; } = "";

    [JsonPropertyName("absurdity")]
    public string Absurdity { get; init; } = "";

    [JsonPropertyName("scenario_seed")]
    public string ScenarioSeed { get; init; } = "";

    [JsonPropertyName("persona_seed")]
    public string PersonaSeed { get; init; } = "";

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; init; }

    [JsonPropertyName("match")]
    public GeneratedMatchSnapshot Match { get; init; } = new();
}

public sealed class GeneratedMatchSnapshot
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("style")]
    public string Style { get; init; } = "balanced";

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = "";

    [JsonPropertyName("global")]
    public string Global { get; init; } = "";

    [JsonPropertyName("narrator_brief")]
    public string NarratorBrief { get; init; } = "";

    [JsonPropertyName("personas")]
    public List<GeneratedPersonaSnapshot> Personas { get; init; } = new();
}

public sealed class GeneratedPersonaSnapshot
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("persona")]
    public string Persona { get; init; } = "";

    [JsonPropertyName("voice_style")]
    public string VoiceStyle { get; init; } = "default";
}

public sealed class DecisionCardState
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; set; }

    [JsonPropertyName("internet_request")]
    public InternetToolRequest? InternetRequest { get; set; }

    [JsonPropertyName("internet_result")]
    public InternetToolResult? InternetResult { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement> Metadata { get; init; } = new();
}

public sealed class DialogueMessage
{
    [JsonPropertyName("message_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageId { get; set; }

    [JsonPropertyName("turn")]
    public int Turn { get; init; }

    [JsonPropertyName("speaker")]
    public string Speaker { get; init; } = "";

    [JsonPropertyName("speaker_id")]
    public string SpeakerId { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("pinned")]
    public bool Pinned { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "message";

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; init; }

    [JsonPropertyName("model")]
    public ModelMetadata Model { get; init; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement> Metadata { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class AttachmentSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; init; } = "";

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "global";

    [JsonPropertyName("chars")]
    public int Chars { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class ResearchItemSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class NarrationEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "Narrator";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("from_turn")]
    public int FromTurn { get; init; }

    [JsonPropertyName("to_turn")]
    public int ToTurn { get; init; }

    [JsonPropertyName("model")]
    public ModelMetadata Model { get; init; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement> Metadata { get; init; } = new();
}

public sealed class NarratorState
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "narrator";

    [JsonPropertyName("persona")]
    public string Persona { get; set; } = "";

    [JsonPropertyName("voice_style")]
    public string VoiceStyle { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("last_error")]
    public string LastError { get; set; } = "";

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "";

    [JsonPropertyName("cadence")]
    public int Cadence { get; set; }

    [JsonPropertyName("inspect_private_notes")]
    public bool InspectPrivateNotes { get; set; }
}

public sealed class Steering
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "freeform";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("global")]
    public string Global { get; set; } = "";
}

public sealed class InternetSettings
{
    [JsonPropertyName("use_internet")]
    public bool UseInternet { get; set; }

    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 5;

    [JsonPropertyName("source_freshness_minutes")]
    public int SourceFreshnessMinutes { get; set; } = 20;
}

public sealed class GeneratorState
{
    [JsonPropertyName("style")]
    public string Style { get; set; } = "";

    [JsonPropertyName("seed")]
    public string Seed { get; set; } = "";

    [JsonPropertyName("intensity")]
    public string Intensity { get; set; } = "";

    [JsonPropertyName("role_pack")]
    public string RolePack { get; set; } = "";

    [JsonPropertyName("absurdity")]
    public string Absurdity { get; set; } = "";

    [JsonPropertyName("apply_on_reset")]
    public bool ApplyOnReset { get; set; }
}
