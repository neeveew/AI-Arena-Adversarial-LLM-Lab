using System.IO;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
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
        var resolvedSharedConfig = ModelRuntimeSettingsRegistry.Resolve(snapshot, sharedConfig);
        var factoryGroup = new FactoryConversationService().Inspect(snapshot);
        var transcriptProjection = ProjectTranscript(snapshot);
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
            ExplicitRoleModel(snapshot, "alpha", sharedConfig),
            ExplicitRoleModel(snapshot, "beta", sharedConfig),
            ExplicitRoleModel(snapshot, "gamma", sharedConfig),
            ExplicitRoleModel(snapshot, "delta", sharedConfig),
            ExplicitRoleModel(snapshot, "narrator", sharedConfig),
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
            transcriptProjection.Messages,
            ParseAgents(snapshot.Engine.Agents, snapshot, transcriptProjection.LatestInternetByAgent))
        {
            SessionInstanceId = SessionStore.IsValidSessionInstanceId(snapshot.SessionInstanceId)
                ? snapshot.SessionInstanceId
                : "",
            FactoryMode = snapshot.Engine.FactoryMode,
            DefaultForUnassignedAgentsEnabled = snapshot.Engine.DefaultForUnassignedAgentsEnabled,
            ExplicitRoleModels = ExplicitRoleModelsFrom(snapshot, sharedConfig),
            HasFactoryConversationRoot = factoryGroup.IsAnchored && factoryGroup.HasUsableRoot,
            FactoryConversationRootAssigned = factoryGroup.IsAnchored,
            FactoryConversationEntryCount = factoryGroup.EligibleEntryCount,
            FactoryConversationOmittedCount = factoryGroup.OmittedEntryCount,
            MatchEnded = snapshot.Engine.MatchEnded,
            HasUnresolvedContextFailure = TurnRunnerService.UnresolvedContextFailure(snapshot) is not null,
            MatchEndReason = PrivacySafeText(snapshot.Engine.MatchEndReason, 240),
            RoleOverrides = RoleOverridesFrom(snapshot, sharedConfig),
            ProviderLastLatencyMs = sharedConfig.LastLatencyMs,
            ModelSettings = snapshot.ModelSettings.Values
                .Where(setting => !string.IsNullOrWhiteSpace(setting.ModelIdentity))
                .OrderBy(setting => setting.ModelIdentity, StringComparer.Ordinal)
                .Select(setting => new Models.ProviderModelRuntimeSettingsView(
                    setting.ModelIdentity,
                    NormalizeConfiguredContextWindow(setting.ConfiguredContextWindow),
                    ModelHistoryPolicies.NormalizeHistoryPolicy(setting.HistoryPolicy),
                    ModelResponseTones.NormalizeResponseTone(setting.ResponseTone),
                    ModelResponseTones.NormalizeCustomTone(setting.CustomTone),
                    SafeModelForIdentity(snapshot, setting.ModelIdentity),
                    snapshot.PendingModelConfigurationApplies.Contains(setting.ModelIdentity)))
                .ToArray(),
            ProviderConfiguredContextWindow = NormalizeConfiguredContextWindow(
                resolvedSharedConfig.ConfiguredContextWindow),
            ProviderHistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(resolvedSharedConfig.HistoryPolicy),
            ProviderResponseTone = ModelResponseTones.NormalizeResponseTone(resolvedSharedConfig.ResponseTone),
            ProviderCustomTone = ModelResponseTones.NormalizeCustomTone(resolvedSharedConfig.CustomTone)
        };
    }

    private static IReadOnlyDictionary<string, Models.RoleGenerationOverride> RoleOverridesFrom(
        CoreSnapshot snapshot,
        ModelProviderConfig sharedConfig)
    {
        var overrides = new Dictionary<string, Models.RoleGenerationOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in snapshot.Engine.Agents.Select(agent => agent.Id).Append("narrator").Distinct(StringComparer.OrdinalIgnoreCase))
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

    private static IReadOnlyDictionary<string, string> ExplicitRoleModelsFrom(
        CoreSnapshot snapshot,
        ModelProviderConfig sharedConfig)
    {
        return snapshot.Engine.Agents
            .Select(agent => agent.Id)
            .Append("narrator")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => new { Key = key, Model = ExplicitRoleModel(snapshot, key, sharedConfig) })
            .Where(item => item.Model.Length > 0)
            .ToDictionary(item => item.Key, item => item.Model, StringComparer.OrdinalIgnoreCase);
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

    private static string ExplicitRoleModel(
        CoreSnapshot snapshot,
        string key,
        ModelProviderConfig shared)
    {
        if (!snapshot.Configs.TryGetValue(key, out var config)
            || string.IsNullOrWhiteSpace(config.Model))
        {
            return "";
        }

        var model = config.Model.Trim();
        return config.ExplicitModelAssignment
            || !model.Equals(shared.Model.Trim(), StringComparison.Ordinal)
                ? model
                : "";
    }

    private static bool Locked(CoreSnapshot snapshot, string key)
    {
        return snapshot.MatchLocks.TryGetValue(key, out var locked) && locked;
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static TranscriptProjection ProjectTranscript(CoreSnapshot snapshot)
    {
        var messages = snapshot.Engine.Messages;
        if (messages.Count == 0)
        {
            return new TranscriptProjection([], new Dictionary<string, AgentInternetSourceSummary>(StringComparer.OrdinalIgnoreCase));
        }

        var voiceStylesByAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in snapshot.Engine.Agents)
        {
            voiceStylesByAgent.TryAdd(agent.Id, agent.VoiceStyle ?? "");
        }

        var projectedMessages = new TranscriptMessage[messages.Count];
        var latestInternetByAgent = new Dictionary<string, AgentInternetSourceSummary>(StringComparer.OrdinalIgnoreCase);
        if (MessagesAreOrderedByTurn(messages))
        {
            for (var index = messages.Count - 1; index >= 0; index--)
            {
                ProjectMessage(
                    messages[index],
                    index,
                    snapshot.Engine.Narrator.VoiceStyle,
                    voiceStylesByAgent,
                    projectedMessages,
                    latestInternetByAgent);
            }
        }
        else
        {
            var orderedIndices = Enumerable.Range(0, messages.Count).ToArray();
            Array.Sort(orderedIndices, (left, right) =>
            {
                var byTurn = messages[right].Turn.CompareTo(messages[left].Turn);
                return byTurn != 0 ? byTurn : right.CompareTo(left);
            });
            foreach (var index in orderedIndices)
            {
                ProjectMessage(
                    messages[index],
                    index,
                    snapshot.Engine.Narrator.VoiceStyle,
                    voiceStylesByAgent,
                    projectedMessages,
                    latestInternetByAgent);
            }
        }

        return new TranscriptProjection(projectedMessages, latestInternetByAgent);
    }

    private static bool MessagesAreOrderedByTurn(IReadOnlyList<DialogueMessage> messages)
    {
        for (var index = 1; index < messages.Count; index++)
        {
            if (messages[index - 1].Turn > messages[index].Turn)
            {
                return false;
            }
        }

        return true;
    }

    private static void ProjectMessage(
        DialogueMessage message,
        int index,
        string narratorVoiceStyle,
        IReadOnlyDictionary<string, string> voiceStylesByAgent,
        TranscriptMessage[] projectedMessages,
        Dictionary<string, AgentInternetSourceSummary> latestInternetByAgent)
    {
        var request = MetadataObject(message, "tool_request");
        var result = MetadataObject(message, "tool_result");
        var requesterId = JsonString(request, "requester_id");
        var latestRequesterId = string.IsNullOrWhiteSpace(requesterId) ? message.SpeakerId : requesterId;
        var latestRequesterKey = string.IsNullOrWhiteSpace(latestRequesterId) ? "" : latestRequesterId.Trim();
        var canAttachLatestSources = latestRequesterKey.Length > 0
            && !latestInternetByAgent.ContainsKey(latestRequesterKey);
        var checkedAt = FormatCheckedAt(JsonProperty(result, "checked_at"));
        var query = JsonString(request, "query", JsonString(result, "query"));
        var sources = ParseInternetSources(JsonProperty(result, "sources"), canAttachLatestSources);
        var receipt = ParseHistoryBudgetReceipt(MetadataObject(message, "arena_history_budget_receipt"));

        projectedMessages[index] = new TranscriptMessage(
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
            VoiceStyleForMessage(message, narratorVoiceStyle, voiceStylesByAgent),
            message.Pinned,
            string.IsNullOrWhiteSpace(message.Kind) ? "message" : message.Kind,
            message.Text,
            MetadataString(message, "reasoning_content"),
            requesterId,
            JsonString(request, "tool"),
            query,
            JsonString(request, "url", JsonString(result, "url")),
            JsonString(request, "reason"),
            JsonString(result, "summary"),
            checkedAt,
            JsonBool(result, "cached"),
            sources.DisplaySources,
            message.Model.TokensPerSecond,
            message.Model.TimeToFirstTokenMs,
            MetadataString(message, "provider_response_id"),
            message.Model.ModelLoadTimeMs)
        {
            CompletionFailureKind = NormalizeCompletionFailureKind(
                MetadataString(message, "completion_failure_kind")),
            CompletionStopReason = NormalizeCompletionStopReason(
                MetadataString(message, "completion_stop_reason")),
            ProviderStatusCode = MetadataInt(message, "provider_status_code"),
            ProviderErrorCode = ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode(
                MetadataString(message, "provider_error_code")),
            HistoryBudgetReceipt = receipt
        };

        if (canAttachLatestSources && sources.DisplaySources.Count > 0)
        {
            latestInternetByAgent[latestRequesterKey] = new AgentInternetSourceSummary(
                query,
                checkedAt,
                sources.DisplaySources,
                sources.Items);
        }
    }

    private static int NormalizeConfiguredContextWindow(int value) => value == 0
        ? 0
        : Math.Clamp(value, 512, ModelRuntimeSettingsRegistry.MaximumConfiguredContextWindow);

    private static string PrivacySafeText(string? value, int maximumLength)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
        {
            return "";
        }

        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string SafeModelForIdentity(CoreSnapshot snapshot, string identity)
    {
        var raw = snapshot.Configs.Values.FirstOrDefault(config =>
            !string.IsNullOrWhiteSpace(config.Model)
            && ModelRuntimeSettingsRegistry.Identity(config).Equals(identity, StringComparison.Ordinal))?.Model ?? "";
        if (raw.Length == 0)
        {
            return "";
        }
        if (Path.IsPathRooted(raw) || Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return Path.GetFileName(raw.Replace('/', Path.DirectorySeparatorChar));
        }
        return raw.Length <= 1024 && !raw.Any(char.IsControl) ? raw.Trim() : "";
    }

    private static string NormalizeCompletionFailureKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "context_limit_exceeded" => "context_limit_exceeded",
        "timeout" => "timeout",
        "capacity" => "capacity",
        "transport" => "transport",
        "provider_rejected" => "provider_rejected",
        "invalid_response" => "invalid_response",
        "empty_public_content" => "empty_public_content",
        "native_state_exhausted" => "native_state_exhausted",
        "provider_loading" => "provider_loading",
        "cancelled" => "cancelled",
        "unknown" => "unknown",
        _ => "none"
    };

    private static string NormalizeCompletionStopReason(string value) => value.Trim().ToLowerInvariant() switch
    {
        "completed" => "completed",
        "output_limit_reached" => "output_limit_reached",
        "content_filtered" => "content_filtered",
        "tool_call" => "tool_call",
        "provider_error" => "provider_error",
        _ => "unknown"
    };

    private static Models.ArenaHistoryBudgetReceiptView? ParseHistoryBudgetReceipt(JsonElement receipt)
    {
        if (receipt.ValueKind != JsonValueKind.Object
            || !JsonString(receipt, "contract").Equals("arena_history_budget_v1", StringComparison.Ordinal))
        {
            return null;
        }

        var fingerprint = PrivacySafeFingerprint(JsonString(receipt, "context_fingerprint"));
        return new Models.ArenaHistoryBudgetReceiptView(
            "arena_history_budget_v1",
            ModelHistoryPolicies.NormalizeHistoryPolicy(JsonString(receipt, "history_policy")),
            NormalizeConfiguredContextWindow(JsonInt(receipt, "configured_context_window")),
            Math.Clamp(JsonInt(receipt, "target_percent"), 0, 100),
            Math.Max(0, JsonInt(receipt, "input_token_budget")),
            Math.Max(0, JsonInt(receipt, "output_token_reserve")),
            Math.Max(0, JsonInt(receipt, "estimated_prompt_tokens")),
            Math.Max(0, JsonInt(receipt, "eligible_entry_count")),
            Math.Max(0, JsonInt(receipt, "included_entry_count")),
            Math.Max(0, JsonInt(receipt, "omitted_entry_count")),
            fingerprint,
            Math.Max(0, JsonInt(receipt, "before_turn")),
            JsonString(receipt, "token_evidence").Equals("estimated_v1", StringComparison.Ordinal)
                ? "estimated_v1"
                : "unknown");
    }

    private static string VoiceStyleForMessage(
        DialogueMessage message,
        string narratorVoiceStyle,
        IReadOnlyDictionary<string, string> voiceStylesByAgent)
    {
        if (MetadataString(message, "prompt_mode").Equals("factory", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var stored = MetadataString(message, "voice_style");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (message.SpeakerId.Equals("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return narratorVoiceStyle;
        }

        return voiceStylesByAgent.TryGetValue(message.SpeakerId, out var voiceStyle)
            ? voiceStyle
            : "";
    }

    private static IReadOnlyList<AgentState> ParseAgents(
        IReadOnlyList<DialogueAgent> agents,
        CoreSnapshot snapshot,
        IReadOnlyDictionary<string, AgentInternetSourceSummary> latestInternetByAgent)
    {
        var sharedConfig = Config(snapshot, "shared");
        var sharedModel = sharedConfig.Model.Trim();
        return agents
            .Select(agent =>
            {
                var id = agent.Id;
                var explicitModel = ExplicitRoleModel(snapshot, id, sharedConfig);
                var agentModel = explicitModel.Length > 0
                    ? explicitModel
                    : snapshot.Engine.DefaultForUnassignedAgentsEnabled
                        ? sharedModel
                        : "";
                latestInternetByAgent.TryGetValue(id, out var internetSources);
                return new AgentState(
                    id,
                    string.IsNullOrWhiteSpace(agent.Name) ? id : agent.Name,
                    string.IsNullOrWhiteSpace(agent.Status) ? "waiting" : agent.Status,
                    agent.Persona,
                    agent.VoiceStyle,
                    agent.PressureProfile,
                    AgentAccentService.NormalizeColor(agent.AccentColor),
                    DisplayValue(agentModel),
                    agent.Active,
                    Locked(snapshot, id),
                    agent.PrivateNotes.Where(note => !string.IsNullOrWhiteSpace(note)).ToArray(),
                    internetSources);
            })
            .ToArray();
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

    private static int? MetadataInt(DialogueMessage message, string key)
    {
        if (!message.Metadata.TryGetValue(key, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed))
        {
            return null;
        }

        return parsed is >= 100 and <= 999 ? parsed : null;
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

    private static int JsonInt(JsonElement element, string key)
    {
        var value = JsonProperty(element, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
    }

    private static string PrivacySafeFingerprint(string value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized.Length is >= 16 and <= 128 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : "";
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

    private static ParsedInternetSources ParseInternetSources(JsonElement sources, bool includeItems)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return new ParsedInternetSources([], []);
        }

        var displaySources = new List<string>();
        List<AgentInternetSourceItem>? items = includeItems ? [] : null;
        foreach (var source in sources.EnumerateArray())
        {
            var title = JsonString(source, "title");
            var url = JsonString(source, "url");
            var name = JsonString(source, "source");
            var snippet = JsonString(source, "snippet");
            var display = string.Join(
                " - ",
                new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            displaySources.Add(display);
            items?.Add(new AgentInternetSourceItem(
                title,
                DomainLabel(url),
                url,
                snippet,
                FormatCheckedAt(JsonProperty(source, "published_at")),
                display));
        }

        return new ParsedInternetSources(displaySources.ToArray(), items?.ToArray() ?? []);
    }

    private static string DomainLabel(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "";
    }

    private sealed record TranscriptProjection(
        IReadOnlyList<TranscriptMessage> Messages,
        IReadOnlyDictionary<string, AgentInternetSourceSummary> LatestInternetByAgent);

    private readonly record struct ParsedInternetSources(
        IReadOnlyList<string> DisplaySources,
        IReadOnlyList<AgentInternetSourceItem> Items);
}
