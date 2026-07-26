using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AgentInternetSourceItem = AIArena.Wpf.Models.AgentInternetSourceItem;
using AgentInternetSourceSummary = AIArena.Wpf.Models.AgentInternetSourceSummary;
using AgentState = AIArena.Wpf.Models.AgentState;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;
using CoreSnapshot = AIArena.Core.Models.ArenaSnapshot;
using RenderSnapshot = AIArena.Wpf.Models.ArenaViewSnapshot;
using GenerationHistoryItem = AIArena.Wpf.Models.GenerationHistoryItem;
using RivalryMatrixItem = AIArena.Wpf.Models.RivalryMatrixItem;
using TranscriptMessage = AIArena.Wpf.Models.TranscriptMessage;

namespace AIArena.Wpf.Services;

public static class SnapshotViewMapper
{
    public static RenderSnapshot FromCore(CoreSessionSummary session, CoreSnapshot snapshot)
    {
        var sharedConfig = Config(snapshot, "shared");
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
            DisplayValue(snapshot.ScenarioGenerator.RolePack),
            DisplayValue(snapshot.ScenarioGenerator.Absurdity),
            DisplayValue(snapshot.ScenarioGenerator.Seed),
            DisplayValue(snapshot.PersonaRandomizer.Style),
            DisplayValue(snapshot.PersonaRandomizer.Seed),
            ParseGenerationHistory(snapshot),
            snapshot.Engine.RivalryMatrix.Enabled,
            ParseRivalryMatrix(snapshot),
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
            AgentAccentService.NormalizeColor(snapshot.Engine.Narrator.AccentColor),
            Locked(snapshot, "narrator"),
            string.IsNullOrWhiteSpace(sharedConfig.BaseUrl) ? ModelProviderDefaults.BaseUrl : sharedConfig.BaseUrl,
            ModelProviderApiModes.Normalize(sharedConfig.ApiMode),
            sharedConfig.ApiToken,
            sharedConfig.Timeout,
            sharedConfig.Temperature,
            sharedConfig.MaxOutputTokens,
            sharedConfig.ContextLength,
            sharedConfig.Reasoning,
            sharedConfig.NativeStatefulChat,
            sharedConfig.NativeIdleTtlSeconds,
            snapshot.Engine.TranscriptWindow,
            snapshot.Engine.PrivateWindow,
            snapshot.Engine.NotesWindow,
            snapshot.Engine.Summary,
            snapshot.Engine.DecisionCard.Text,
            snapshot.Engine.DecisionCard.UpdatedAt,
            sharedConfig.LastError,
            snapshot.Engine.Internet.UseInternet,
            sharedConfig.LastTestOk,
            ParseMessages(snapshot.Engine.Messages, snapshot),
            ParseAgents(snapshot.Engine.Agents, snapshot))
        {
            RoleOverrides = RoleOverridesFrom(snapshot, sharedConfig),
            ProviderLastLatencyMs = sharedConfig.LastLatencyMs
        };
    }

    private static IReadOnlyDictionary<string, Models.RoleGenerationOverride> RoleOverridesFrom(
        CoreSnapshot snapshot,
        ModelProviderConfig sharedConfig)
    {
        var overrides = new Dictionary<string, Models.RoleGenerationOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
        {
            if (!snapshot.Configs.TryGetValue(key, out var config) || config is null)
            {
                continue;
            }

            double? temperature = Math.Abs(config.Temperature - sharedConfig.Temperature) > 0.0001 ? config.Temperature : null;
            int? maxOutputTokens = config.MaxOutputTokens != sharedConfig.MaxOutputTokens ? config.MaxOutputTokens : null;
            if (temperature.HasValue || maxOutputTokens.HasValue)
            {
                overrides[key] = new Models.RoleGenerationOverride(temperature, maxOutputTokens);
            }
        }

        return overrides;
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
            "-",
            "-",
            [],
            false,
            [],
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
            "",
            false,
            ModelProviderDefaults.BaseUrl,
            ModelProviderApiModes.OpenAiCompatible,
            "",
            ModelProviderDefaults.TimeoutSeconds,
            ModelProviderDefaults.Temperature,
            ModelProviderDefaults.MaxOutputTokens,
            0,
            "",
            true,
            0,
            30,
            12,
            8,
            "",
            "",
            0,
            "",
            false,
            false,
            [new TranscriptMessage(0, "Transcript", "transcript", 0, "-", 0, 0, 0, 0, "empty", "", false, "message", message, "", "", "", "", "", "", "", "", false, [])],
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

    private static IReadOnlyList<TranscriptMessage> ParseMessages(IReadOnlyList<DialogueMessage> messages, CoreSnapshot snapshot)
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
                    VoiceStyleForMessage(message, snapshot),
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
                    ParseInternetSources(JsonProperty(result, "sources")),
                    message.Model.TokensPerSecond,
                    message.Model.TimeToFirstTokenMs,
                    MetadataString(message, "provider_response_id"),
                    message.Model.ModelLoadTimeMs);
            })
            .ToArray();
    }

    private static string VoiceStyleForMessage(DialogueMessage message, CoreSnapshot snapshot)
    {
        var stored = MetadataString(message, "voice_style");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.Engine.Narrator.VoiceStyle;
        }

        return snapshot.Engine.Agents
            .FirstOrDefault(agent => agent.Id.Equals(message.SpeakerId, StringComparison.OrdinalIgnoreCase))
            ?.VoiceStyle ?? "";
    }

    private static IReadOnlyList<AgentState> ParseAgents(IReadOnlyList<DialogueAgent> agents, CoreSnapshot snapshot)
    {
        var sharedModel = DisplayValue(Config(snapshot, "shared").Model);
        var latestInternetByAgent = LatestInternetSourcesByAgent(snapshot.Engine.Messages);
        return agents
            .Select(agent =>
            {
                var id = agent.Id;
                var agentModel = Config(snapshot, id).Model;
                latestInternetByAgent.TryGetValue(id, out var internetSources);
                return new AgentState(
                    id,
                    string.IsNullOrWhiteSpace(agent.Name) ? id : agent.Name,
                    string.IsNullOrWhiteSpace(agent.Status) ? "waiting" : agent.Status,
                    agent.Persona,
                    agent.VoiceStyle,
                    agent.PressureProfile,
                    AgentAccentService.NormalizeColor(agent.AccentColor),
                    DisplayValue(string.IsNullOrWhiteSpace(agentModel) ? sharedModel : agentModel),
                    agent.Active,
                    Locked(snapshot, id),
                    agent.PrivateNotes.Where(note => !string.IsNullOrWhiteSpace(note)).ToArray(),
                    internetSources);
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, AgentInternetSourceSummary> LatestInternetSourcesByAgent(IReadOnlyList<DialogueMessage> messages)
    {
        var latest = new Dictionary<string, AgentInternetSourceSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages.OrderBy(message => message.Turn))
        {
            var request = MetadataObject(message, "tool_request");
            var result = MetadataObject(message, "tool_result");
            var sourcesElement = JsonProperty(result, "sources");
            var sources = ParseInternetSources(sourcesElement);
            if (sources.Count == 0)
            {
                continue;
            }

            var requesterId = JsonString(request, "requester_id");
            if (string.IsNullOrWhiteSpace(requesterId))
            {
                requesterId = message.SpeakerId;
            }

            if (string.IsNullOrWhiteSpace(requesterId))
            {
                continue;
            }

            latest[requesterId.Trim()] = new AgentInternetSourceSummary(
                JsonString(request, "query", JsonString(result, "query")),
                FormatCheckedAt(JsonProperty(result, "checked_at")),
                sources,
                ParseInternetSourceItems(sourcesElement));
        }

        return latest;
    }

    private static IReadOnlyList<GenerationHistoryItem> ParseGenerationHistory(CoreSnapshot snapshot)
    {
        return snapshot.GenerationHistory
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new GenerationHistoryItem(
                item.Id,
                DisplayValue(item.Kind),
                DisplayValue(item.Label),
                DisplayValue(item.Style),
                DisplayValue(item.Intensity),
                DisplayValue(item.RolePack),
                DisplayValue(item.Absurdity),
                DisplayValue(item.ScenarioSeed),
                DisplayValue(item.PersonaSeed),
                item.CreatedAt,
                item.Match.Topic,
                item.Match.Global,
                item.Match.NarratorBrief,
                item.Match.Personas.Count(persona => !persona.AgentId.Equals("narrator", StringComparison.OrdinalIgnoreCase)),
                string.Join(
                    ", ",
                    item.Match.Personas
                        .Where(persona => !persona.AgentId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
                        .Take(4)
                        .Select(persona => string.IsNullOrWhiteSpace(persona.Role)
                            ? DisplayValue(persona.AgentId)
                            : $"{DisplayValue(persona.AgentId)}: {DisplayValue(persona.Role)}"))))
            .ToArray();
    }

    private static IReadOnlyList<RivalryMatrixItem> ParseRivalryMatrix(CoreSnapshot snapshot)
    {
        return snapshot.Engine.RivalryMatrix.Links
            .Where(link => !string.IsNullOrWhiteSpace(link.Source) && !string.IsNullOrWhiteSpace(link.Target))
            .Select(link => new RivalryMatrixItem(
                link.Source.Trim().ToLowerInvariant(),
                link.Target.Trim().ToLowerInvariant(),
                string.IsNullOrWhiteSpace(link.Stance) ? "neutral" : link.Stance.Trim().ToLowerInvariant()))
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

    private static IReadOnlyList<AgentInternetSourceItem> ParseInternetSourceItems(JsonElement sources)
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
                var display = string.Join(" - ", new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
                return new AgentInternetSourceItem(
                    title,
                    DomainLabel(url),
                    url,
                    snippet,
                    FormatCheckedAt(JsonProperty(source, "published_at")),
                    display);
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.DisplayText))
            .ToArray();
    }

    private static string DomainLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "";
    }
}
