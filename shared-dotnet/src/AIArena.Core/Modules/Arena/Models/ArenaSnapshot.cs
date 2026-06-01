using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

public sealed class ArenaSnapshot
{
    [JsonPropertyName("configs")]
    public Dictionary<string, ModelProviderConfig> Configs { get; init; } = new();

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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class EngineSnapshot
{
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
    public string Summary { get; init; } = "";

    [JsonPropertyName("decision_card")]
    public DecisionCardState DecisionCard { get; init; } = new();

    [JsonPropertyName("turn_count")]
    public int TurnCount { get; set; }

    [JsonPropertyName("turn_index")]
    public int TurnIndex { get; set; }

    [JsonPropertyName("steering")]
    public Steering Steering { get; init; } = new();

    [JsonPropertyName("model_rss")]
    public ModelRssSettings ModelRss { get; init; } = new();

    [JsonPropertyName("news_automation")]
    public NewsAutomationSettings NewsAutomation { get; init; } = new();

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

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "waiting";

    [JsonPropertyName("private_notes")]
    public List<string> PrivateNotes { get; init; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed class DecisionCardState
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; set; }
}

public sealed class DialogueMessage
{
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

public sealed class ModelRssSettings
{
    [JsonPropertyName("use_internet")]
    public bool UseInternet { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "manual";

    [JsonPropertyName("allow_model_rss")]
    public bool AllowModelRss { get; set; }

    [JsonPropertyName("allow_participant_requests")]
    public bool AllowParticipantRequests { get; set; }

    [JsonPropertyName("allow_narrator_requests")]
    public bool AllowNarratorRequests { get; set; } = true;

    [JsonPropertyName("require_approval")]
    public bool RequireApproval { get; set; }

    [JsonPropertyName("source_scope")]
    public string SourceScope { get; set; } = "trusted";

    [JsonPropertyName("allow_random_internet_assist")]
    public bool AllowRandomInternetAssist { get; set; }

    [JsonPropertyName("max_results")]
    public int MaxResults { get; set; } = 1;

    [JsonPropertyName("allowed_sources")]
    public List<string> AllowedSources { get; init; } = new();
}

public sealed class NewsAutomationSettings
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "manual";

    [JsonPropertyName("cadence")]
    public int Cadence { get; init; }

    [JsonPropertyName("max_items")]
    public int MaxItems { get; init; } = 1;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
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
