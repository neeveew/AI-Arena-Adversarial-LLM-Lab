using System.Text.Json;
using AIArena.Core.Models;
using AgentState = AIArena.Wpf.Models.AgentState;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using CoreSnapshot = AIArena.Core.Models.ArenaSnapshot;
using RenderSnapshot = AIArena.Wpf.Models.ArenaSnapshot;
using TranscriptMessage = AIArena.Wpf.Models.TranscriptMessage;

namespace AIArena.Wpf.Services;

public static class SnapshotViewMapper
{
    public static RenderSnapshot FromCore(CoreSessionSummary session, CoreSnapshot snapshot)
    {
        var sharedConfig = Config(snapshot, "shared");
        var modelRss = snapshot.Engine.ModelRss;
        return new RenderSnapshot(
            session.Id,
            session.SnapshotPath,
            session.LastModified.UtcDateTime,
            DisplayValue(snapshot.MatchType),
            snapshot.Engine.Steering.Topic,
            snapshot.Engine.Steering.Global,
            Locked(snapshot, "topic") || Locked(snapshot, "scenario"),
            Locked(snapshot, "global") || Locked(snapshot, "scenario"),
            DisplayValue(snapshot.ScenarioGenerator.Style),
            DisplayValue(snapshot.ScenarioGenerator.Intensity),
            DisplayValue(snapshot.ScenarioGenerator.Seed),
            DisplayValue(snapshot.PersonaRandomizer.Style),
            DisplayValue(snapshot.PersonaRandomizer.Seed),
            snapshot.Engine.TurnCount,
            snapshot.Engine.TurnIndex,
            DisplayValue(sharedConfig.Model),
            Config(snapshot, "alpha").Model,
            Config(snapshot, "beta").Model,
            Config(snapshot, "gamma").Model,
            Config(snapshot, "delta").Model,
            Config(snapshot, "narrator").Model,
            string.IsNullOrWhiteSpace(snapshot.Engine.Narrator.Status) ? "idle" : snapshot.Engine.Narrator.Status,
            snapshot.Engine.Narrator.Persona,
            snapshot.Engine.Narrator.VoiceStyle,
            Locked(snapshot, "narrator"),
            string.IsNullOrWhiteSpace(sharedConfig.BaseUrl) ? "http://127.0.0.1:1234/v1" : sharedConfig.BaseUrl,
            sharedConfig.Timeout,
            sharedConfig.Temperature,
            sharedConfig.MaxOutputTokens,
            snapshot.Engine.TranscriptWindow,
            snapshot.Engine.PrivateWindow,
            snapshot.Engine.NotesWindow,
            snapshot.Engine.Summary,
            snapshot.Engine.DecisionCard.Text,
            snapshot.Engine.DecisionCard.UpdatedAt,
            sharedConfig.LastError,
            modelRss.UseInternet,
            string.IsNullOrWhiteSpace(modelRss.Mode) ? "manual" : modelRss.Mode,
            string.IsNullOrWhiteSpace(modelRss.SourceScope) ? "trusted" : modelRss.SourceScope,
            modelRss.MaxResults,
            modelRss.AllowParticipantRequests || modelRss.AllowModelRss,
            modelRss.AllowNarratorRequests,
            modelRss.RequireApproval,
            string.IsNullOrWhiteSpace(snapshot.Engine.NewsAutomation.Mode) ? "manual" : snapshot.Engine.NewsAutomation.Mode,
            sharedConfig.LastTestOk,
            ParseMessages(snapshot.Engine.Messages),
            ParseAgents(snapshot.Engine.Agents, snapshot));
    }

    public static RenderSnapshot Empty(CoreSessionSummary session, string message)
    {
        return new RenderSnapshot(
            session.Id,
            session.SnapshotPath,
            session.LastModified.UtcDateTime,
            "-",
            "",
            "",
            false,
            false,
            "-",
            "-",
            "-",
            "-",
            "-",
            0,
            0,
            "-",
            "",
            "",
            "",
            "",
            "",
            "idle",
            "",
            "",
            false,
            "http://127.0.0.1:1234/v1",
            300,
            0.8,
            1024,
            30,
            12,
            8,
            "",
            "",
            0,
            "",
            false,
            "manual",
            "trusted",
            1,
            false,
            true,
            false,
            "manual",
            false,
            [new TranscriptMessage(0, "Transcript", "transcript", 0, "-", 0, 0, 0, 0, "empty", false, "message", message, "", "", "", "", "", "", "", "", false, [])],
            []);
    }

    private static ModelProviderConfig Config(CoreSnapshot snapshot, string key)
    {
        return snapshot.Configs.TryGetValue(key, out var config) ? config : new ModelProviderConfig();
    }

    private static bool Locked(CoreSnapshot snapshot, string key)
    {
        return snapshot.MatchLocks.TryGetValue(key, out var locked) && locked;
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static IReadOnlyList<TranscriptMessage> ParseMessages(IReadOnlyList<DialogueMessage> messages)
    {
        return messages
            .Select(message =>
            {
                var request = MetadataObject(message, "tool_request");
                var result = MetadataObject(message, "tool_result");
                return new TranscriptMessage(
                    message.Turn,
                    DisplayValue(string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker),
                    DisplayValue(string.IsNullOrWhiteSpace(message.SpeakerId) ? message.Speaker : message.SpeakerId),
                    message.CreatedAt,
                    DisplayValue(message.Model.Model),
                    message.Model.LatencyMs,
                    message.Model.PromptTokens,
                    message.Model.CompletionTokens,
                    message.Model.TotalTokens,
                    string.IsNullOrWhiteSpace(message.Status) ? "ok" : message.Status,
                    message.Pinned,
                    string.IsNullOrWhiteSpace(message.Kind) ? "message" : message.Kind,
                    message.Text,
                    MetadataString(message, "reasoning_content"),
                    JsonString(request, "requester_id"),
                    JsonString(request, "tool"),
                    JsonString(request, "query", JsonString(result, "query")),
                    JsonString(request, "url", JsonString(result, "url")),
                    JsonString(request, "reason"),
                    JsonString(result, "summary"),
                    FormatCheckedAt(JsonProperty(result, "checked_at")),
                    JsonBool(result, "cached"),
                    ParseInternetSources(JsonProperty(result, "sources")));
            })
            .ToArray();
    }

    private static IReadOnlyList<AgentState> ParseAgents(IReadOnlyList<DialogueAgent> agents, CoreSnapshot snapshot)
    {
        var sharedModel = DisplayValue(Config(snapshot, "shared").Model);
        return agents
            .Select(agent =>
            {
                var id = agent.Id;
                return new AgentState(
                    id,
                    string.IsNullOrWhiteSpace(agent.Name) ? id : agent.Name,
                    string.IsNullOrWhiteSpace(agent.Status) ? "waiting" : agent.Status,
                    agent.Persona,
                    agent.VoiceStyle,
                    DisplayValue(Config(snapshot, id).Model is { Length: > 0 } model ? model : sharedModel),
                    agent.Active,
                    Locked(snapshot, id),
                    agent.PrivateNotes.Where(note => !string.IsNullOrWhiteSpace(note)).ToArray());
            })
            .ToArray();
    }

    private static JsonElement MetadataObject(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    private static string MetadataString(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static JsonElement JsonProperty(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value
            : default;
    }

    private static string JsonString(JsonElement element, string key, string fallback = "")
    {
        var value = JsonProperty(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static bool JsonBool(JsonElement element, string key)
    {
        var value = JsonProperty(element, key);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
    }

    private static string FormatCheckedAt(JsonElement checkedAt)
    {
        if (checkedAt.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var value = checkedAt.GetString();
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
            : value ?? "";
    }

    private static IReadOnlyList<string> ParseInternetSources(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sources.EnumerateArray()
            .Select(source =>
            {
                var title = JsonString(source, "title");
                var url = JsonString(source, "url");
                var name = JsonString(source, "source");
                var snippet = JsonString(source, "snippet");
                return string.Join(" - ", new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }
}
