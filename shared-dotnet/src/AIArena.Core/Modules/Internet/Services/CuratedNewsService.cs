using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed partial class CuratedNewsService
{
    private static readonly HashSet<string> SearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "against", "alpha", "also", "arena", "because", "before", "being",
        "beta", "between", "could", "current", "data", "debate", "discussion", "does", "each",
        "engaging", "framework", "from", "gamma", "have", "here", "into", "model", "must", "news",
        "once", "open", "phase", "please", "proposed", "should", "since", "that", "their", "there",
        "these", "this", "those", "through", "turn", "turns", "what", "when", "where", "which",
        "while", "with", "would", "your"
    };

    private readonly IModelProviderClient _modelClient;
    private readonly SessionStore _sessionStore;
    private readonly EventLogStore _eventLogStore;
    private readonly InternetToolService _internetToolService;

    public CuratedNewsService(
        IModelProviderClient? modelClient = null,
        SessionStore? sessionStore = null,
        EventLogStore? eventLogStore = null,
        InternetToolService? internetToolService = null)
    {
        _modelClient = modelClient ?? new ModelProviderClient();
        _sessionStore = sessionStore ?? new SessionStore();
        _eventLogStore = eventLogStore ?? new EventLogStore(_sessionStore.DataRoot);
        _internetToolService = internetToolService ?? new InternetToolService(eventLogStore: _eventLogStore);
    }

    public async Task<CuratedNewsResult> CurateNowAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return CuratedNewsResult.Failed($"No snapshot found for session {sessionId}.");
        }

        if (!snapshot.Engine.ModelRss.UseInternet)
        {
            return CuratedNewsResult.Failed("Internet is off.");
        }

        var config = ResolveProviderConfig(snapshot, "narrator", out var fallbackConfig);
        if (config is null)
        {
            return CuratedNewsResult.Failed("No provider config for narrator.");
        }

        var plan = await PlanCuratedNewsQueryAsync(snapshot, config, fallbackConfig, cancellationToken);
        var internetRequest = new InternetToolRequest
            {
                Tool = InternetToolNames.RssSearch,
                RequesterId = "narrator",
                Query = plan.SearchQuery,
                MaxResults = snapshot.Engine.ModelRss.MaxResults <= 0 ? 1 : snapshot.Engine.ModelRss.MaxResults,
                Reason = $"Curate News search intent: {plan.Focus}"
            };
        var internetResult = await _internetToolService.ExecuteManualAsync(
            snapshot,
            internetRequest,
            sessionId,
            cancellationToken);

        if (!internetResult.Ok || internetResult.Sources.Count == 0)
        {
            snapshot.Engine.LastError = string.IsNullOrWhiteSpace(internetResult.Error)
                ? "Curate News found no trusted source."
                : internetResult.Error;
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            await _eventLogStore.AppendAsync(
                sessionId,
                "native_curated_news_no_source",
                new { snapshot.Engine.TurnCount, internetResult.Ok, internetResult.Error, internetRequest.Query },
                cancellationToken);
            return CuratedNewsResult.Failed(snapshot.Engine.LastError);
        }

        var prompt = BuildPrompt(snapshot, internetResult, plan);
        var result = await _modelClient.CompleteChatAsync(config, prompt, cancellationToken);
        if (!result.Ok && fallbackConfig is not null)
        {
            result = await _modelClient.CompleteChatAsync(fallbackConfig, prompt, cancellationToken);
        }
        var text = result.Ok
            ? result.Text
            : $"Curated news failed: {result.Error}";
        if (IsNoRelevantSource(text))
        {
            snapshot.Engine.LastError = "No directly relevant trusted source found.";
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
            await _eventLogStore.AppendAsync(
                sessionId,
                "native_curated_news_relevance_rejected",
                new { snapshot.Engine.TurnCount, internetRequest.Query, internetResult.Sources.Count },
                cancellationToken);
            return CuratedNewsResult.Failed(snapshot.Engine.LastError);
        }

        var message = CreateNewsMessage(text, result, internetRequest, internetResult, plan, snapshot.Engine.TurnCount + 1);
        snapshot.Engine.Messages.Add(message);
        snapshot.Engine.TurnCount = message.Turn;
        snapshot.Engine.LastError = result.Ok ? "" : result.Error;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_curated_news_injected", new { message.Turn, result.Ok, result.Model }, cancellationToken);
        return result.Ok
            ? CuratedNewsResult.Completed(message)
            : CuratedNewsResult.Failed(result.Error, message);
    }

    private async Task<CuratedNewsPlan> PlanCuratedNewsQueryAsync(ArenaSnapshot snapshot, ModelProviderConfig config, ModelProviderConfig? fallbackConfig, CancellationToken cancellationToken)
    {
        var fallback = FallbackPlan(snapshot);
        var prompt = BuildPlanningPrompt(snapshot);
        var result = await _modelClient.CompleteChatAsync(config, prompt, cancellationToken);
        if (!result.Ok && fallbackConfig is not null)
        {
            result = await _modelClient.CompleteChatAsync(fallbackConfig, prompt, cancellationToken);
        }
        if (!result.Ok)
        {
            return fallback;
        }

        return TryParsePlan(result.Text, out var planned)
            ? planned
            : fallback;
    }

    private static IReadOnlyList<ModelChatMessage> BuildPlanningPrompt(ArenaSnapshot snapshot)
    {
        var transcript = TranscriptContext(snapshot, take: 8);
        var topic = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic) ? "Open arena discussion" : snapshot.Engine.Steering.Topic;

        return
        [
            new ModelChatMessage(
                "system",
                "You plan a news search for an AI Arena discussion. Analyze the ongoing debate direction and return only strict JSON. Do not choose an article and do not write the news brief yet."),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    $"Topic: {topic}",
                    string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Global) ? "" : $"Global instruction: {snapshot.Engine.Steering.Global}",
                    string.IsNullOrWhiteSpace(transcript) ? "Transcript: No public transcript yet." : $"Transcript:{Environment.NewLine}{transcript}",
                    "Return JSON with exactly these fields:",
                    """{"search_query":"short focused query, max 8 meaningful words","focus":"one sentence explaining what kind of real-world source would help the debate","must_include":["important term"],"avoid":["off-topic term"]}"""))
        ];
    }

    private static IReadOnlyList<ModelChatMessage> BuildPrompt(ArenaSnapshot snapshot, InternetToolResult internetResult, CuratedNewsPlan plan)
    {
        var transcript = TranscriptContext(snapshot, Math.Clamp(snapshot.Engine.TranscriptWindow, 1, 60));
        var topic = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic) ? "Open arena discussion" : snapshot.Engine.Steering.Topic;

        return
        [
            new ModelChatMessage(
                "system",
                "You curate one relevant news/research brief for an AI Arena discussion. Use only the retrieved source context provided by the app. Do not use the source as a metaphor. If the source is not directly relevant to the search intent, output exactly: No directly relevant trusted source found. Otherwise return a concise transcript-ready note, explain why it matters, and include a final Source line with title and URL. No markdown table."),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine,
                    $"Topic: {topic}",
                    $"Search intent: {plan.SearchQuery}",
                    $"Relevance focus: {plan.Focus}",
                    plan.MustInclude.Count == 0 ? "" : $"Must include concepts: {string.Join(", ", plan.MustInclude)}",
                    plan.Avoid.Count == 0 ? "" : $"Avoid off-topic concepts: {string.Join(", ", plan.Avoid)}",
                    string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Global) ? "" : $"Global instruction: {snapshot.Engine.Steering.Global}",
                    string.IsNullOrWhiteSpace(transcript) ? "Transcript: No public transcript yet." : $"Transcript:{Environment.NewLine}{transcript}",
                    InternetContext(internetResult),
                    "Curate exactly one directly relevant news/research drop that would help the participants."))
        ];
    }

    private static string TranscriptContext(ArenaSnapshot snapshot, int take)
    {
        return string.Join(
            Environment.NewLine,
            snapshot.Engine.Messages
            .OrderBy(item => item.Turn)
            .TakeLast(take)
            .Where(item => item.Kind is "message" or "narration" or "news")
            .Select(item => $"T{item.Turn} {item.Speaker}: {item.Text}"));
    }

    private static CuratedNewsPlan FallbackPlan(ArenaSnapshot snapshot)
    {
        var source = !string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic)
            ? snapshot.Engine.Steering.Topic
            : string.Join(
                " ",
                snapshot.Engine.Messages
                    .OrderBy(item => item.Turn)
                    .TakeLast(6)
                    .Where(item => item.Kind is "message" or "narration")
                    .Select(item => item.Text));
        var query = CompactSearchQuery(source);
        return new CuratedNewsPlan(query, "Find a directly relevant current source for the active debate direction.", [], []);
    }

    private static bool TryParsePlan(string text, out CuratedNewsPlan plan)
    {
        plan = new CuratedNewsPlan("AI policy technology", "Find a directly relevant current source for the active debate direction.", [], []);
        var json = ExtractJsonObject(text);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var query = root.TryGetProperty("search_query", out var queryElement) ? queryElement.GetString() ?? "" : "";
            var focus = root.TryGetProperty("focus", out var focusElement) ? focusElement.GetString() ?? "" : "";
            var mustInclude = ReadStringArray(root, "must_include");
            var avoid = ReadStringArray(root, "avoid");
            plan = new CuratedNewsPlan(
                CompactSearchQuery(query),
                string.IsNullOrWhiteSpace(focus) ? "Find a directly relevant current source for the active debate direction." : CleanOneLine(focus),
                mustInclude.Select(CleanOneLine).Where(item => !string.IsNullOrWhiteSpace(item)).Take(6).ToArray(),
                avoid.Select(CleanOneLine).Where(item => !string.IsNullOrWhiteSpace(item)).Take(6).ToArray());
            return !string.IsNullOrWhiteSpace(plan.SearchQuery);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .ToArray();
    }

    private static string CompactSearchQuery(string text)
    {
        var tokens = SearchTokenRegex().Matches(text)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(token => (token.Length >= 3 || token.Equals("ai", StringComparison.OrdinalIgnoreCase)) && !SearchStopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return tokens.Length == 0 ? "AI policy technology" : string.Join(" ", tokens);
    }

    private static string CleanOneLine(string text)
    {
        return WhitespaceRegex().Replace(text.Trim(), " ");
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first
            ? text[first..(last + 1)].Trim()
            : "";
    }

    private static string InternetContext(InternetToolResult result)
    {
        if (!result.Ok || result.Sources.Count == 0)
        {
            return $"Retrieved source context: {result.Error}".Trim();
        }

        return "Retrieved source context:" + Environment.NewLine + string.Join(
            Environment.NewLine,
            result.Sources.Select((source, index) => $"{index + 1}. {source.Source}: {source.Title} - {source.Url} - {source.Snippet}"));
    }

    private static bool IsNoRelevantSource(string text)
    {
        return text.Trim().Equals("No directly relevant trusted source found.", StringComparison.OrdinalIgnoreCase);
    }

    private static DialogueMessage CreateNewsMessage(string text, ModelCompletionResult result, InternetToolRequest request, InternetToolResult internetResult, CuratedNewsPlan plan, int nextTurn)
    {
        var metadata = string.IsNullOrWhiteSpace(result.Reasoning)
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>
            {
                ["reasoning_content"] = JsonSerializer.SerializeToElement(result.Reasoning.Trim())
            };
        metadata["curated_news_plan"] = JsonSerializer.SerializeToElement(plan);
        metadata["tool_request"] = JsonSerializer.SerializeToElement(request);
        metadata["tool_result"] = JsonSerializer.SerializeToElement(internetResult);

        return new DialogueMessage
        {
            Turn = nextTurn,
            Speaker = "Curated News",
            SpeakerId = "news",
            Text = string.IsNullOrWhiteSpace(text) ? "(empty curated news response)" : text.Trim(),
            Status = result.Ok ? "ok" : "error",
            Kind = "news",
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = new ModelMetadata
            {
                Model = string.IsNullOrWhiteSpace(result.Model) ? "curated-news" : result.Model,
                LatencyMs = result.LatencyMs,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens
            },
            Metadata = metadata
        };
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

    [GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}\\-']*")]
    private static partial Regex SearchTokenRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record CuratedNewsPlan(
    string SearchQuery,
    string Focus,
    IReadOnlyList<string> MustInclude,
    IReadOnlyList<string> Avoid);

public sealed record CuratedNewsResult(bool Ok, DialogueMessage? Message, string Error)
{
    public static CuratedNewsResult Completed(DialogueMessage message) => new(true, message, "");

    public static CuratedNewsResult Failed(string error, DialogueMessage? message = null) => new(false, message, error);
}
