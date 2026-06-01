using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed class TurnRunnerService
{
    private const int MaxPrivateMemoryNotes = 60;

    private readonly IModelProviderClient _modelClient;
    private readonly SessionStore _sessionStore;
    private readonly EventLogStore _eventLogStore;
    private readonly TranscriptService _transcriptService;
    private readonly InternetToolService _internetToolService;

    public TurnRunnerService(
        IModelProviderClient? modelClient = null,
        SessionStore? sessionStore = null,
        EventLogStore? eventLogStore = null,
        TranscriptService? transcriptService = null,
        InternetToolService? internetToolService = null)
    {
        _modelClient = modelClient ?? new ModelProviderClient();
        _sessionStore = sessionStore ?? new SessionStore();
        _eventLogStore = eventLogStore ?? new EventLogStore(_sessionStore.DataRoot);
        _transcriptService = transcriptService ?? new TranscriptService();
        _internetToolService = internetToolService ?? new InternetToolService(eventLogStore: _eventLogStore);
    }

    public OneTurnPlan PlanOneTurn(ArenaSnapshot snapshot)
    {
        var agent = _transcriptService.NextActiveAgent(snapshot);
        if (agent is null)
        {
            return new OneTurnPlan(false, "", "", null, null, "No active agents.");
        }

        var config = ResolveProviderConfig(snapshot, agent.Id, out var fallbackConfig);
        if (config is null)
        {
            return new OneTurnPlan(false, agent.Id, agent.Name, null, null, $"No provider config for {agent.Name}.");
        }

        return new OneTurnPlan(true, agent.Id, agent.Name, config, fallbackConfig, "");
    }

    public OneTurnPlan PlanAgentTurn(ArenaSnapshot snapshot, string agentId)
    {
        var agent = snapshot.Engine.Agents.FirstOrDefault(item => string.Equals(item.Id, agentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return new OneTurnPlan(false, agentId, agentId, null, null, $"No agent found for {agentId}.");
        }

        var config = ResolveProviderConfig(snapshot, agent.Id, out var fallbackConfig);
        if (config is null)
        {
            return new OneTurnPlan(false, agent.Id, agent.Name, null, null, $"No provider config for {agent.Name}.");
        }

        return new OneTurnPlan(true, agent.Id, agent.Name, config, fallbackConfig, "");
    }

    public async Task<OneTurnResult> RunOneTurnAsync(string sessionId = "default", CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return OneTurnResult.Failed($"No snapshot found for session {sessionId}.");
        }

        if (HasPendingInternetApproval(snapshot))
        {
            return OneTurnResult.Failed("Internet approval is pending. Approve, reject, or delete the pending request before running another turn.");
        }

        var plan = PlanOneTurn(snapshot);
        if (!plan.Ok || plan.Config is null)
        {
            return OneTurnResult.Failed(plan.Error);
        }

        return await RunPlannedTurnAsync(sessionId, snapshot, plan, advanceTurnIndex: true, "native_one_turn", cancellationToken);
    }

    public async Task<OneTurnResult> RunAgentTurnAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return OneTurnResult.Failed($"No snapshot found for session {sessionId}.");
        }

        if (HasPendingInternetApproval(snapshot))
        {
            return OneTurnResult.Failed("Internet approval is pending. Approve, reject, or delete the pending request before running another turn.");
        }

        var plan = PlanAgentTurn(snapshot, agentId);
        if (!plan.Ok || plan.Config is null)
        {
            return OneTurnResult.Failed(plan.Error);
        }

        return await RunPlannedTurnAsync(sessionId, snapshot, plan, advanceTurnIndex: false, "native_agent_turn", cancellationToken);
    }

    public async Task<OneTurnResult> RetryTurnAsync(string sessionId, int turn, string speakerId, double createdAt, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return OneTurnResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var original = TranscriptService.FindMessage(snapshot, turn, speakerId, createdAt);
        if (original is null)
        {
            return OneTurnResult.Failed($"No transcript message found for turn {turn}.");
        }

        var plan = PlanAgentTurn(snapshot, original.SpeakerId);
        if (!plan.Ok || plan.Config is null)
        {
            return OneTurnResult.Failed(plan.Error);
        }

        return await ReplaceMessageWithRetryAsync(sessionId, snapshot, original, plan, cancellationToken);
    }

    private static bool CanRequestInternetTool(ArenaSnapshot snapshot, string requesterId)
    {
        return InternetToolService.CanExecute(snapshot.Engine.ModelRss, requesterId, out _);
    }

    private async Task<OneTurnResult> RunPlannedTurnAsync(
        string sessionId,
        ArenaSnapshot snapshot,
        OneTurnPlan plan,
        bool advanceTurnIndex,
        string eventPrefix,
        CancellationToken cancellationToken)
    {
        var agent = snapshot.Engine.Agents.FirstOrDefault(item => string.Equals(item.Id, plan.AgentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return OneTurnResult.Failed($"No agent found for {plan.AgentId}.");
        }

        await MarkAgentThinkingAsync(snapshot, sessionId, agent, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, $"{eventPrefix}_started", new { speaker = plan.AgentId, model = plan.Config!.Model }, cancellationToken);

        var result = await CompleteWithFallbackAsync(
            sessionId,
            plan,
            BuildPrompt(snapshot, plan, allowInternetTool: CanRequestInternetTool(snapshot, plan.AgentId)),
            $"{eventPrefix}_fallback_to_default",
            cancellationToken);
        if (result.Ok && InternetToolContract.TryParseRequest(result.Text, out var toolRequest, out _))
        {
            var requestedByAgent = WithRequester(toolRequest, plan.AgentId);
            if (snapshot.Engine.ModelRss.RequireApproval
                && InternetToolService.CanExecute(snapshot.Engine.ModelRss, requestedByAgent.RequesterId, out _))
            {
                var approvalMessage = _transcriptService.CreateInternetApprovalMessage(requestedByAgent, snapshot.Engine.TurnCount + 1);
                snapshot.Engine.Messages.Add(approvalMessage);
                snapshot.Engine.TurnCount = approvalMessage.Turn;
                agent.Status = "waiting";
                snapshot.Engine.LastError = "";
                await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
                await _eventLogStore.AppendAsync(
                    sessionId,
                    $"{eventPrefix}_internet_tool_pending_approval",
                    new { speaker = plan.AgentId, requestedByAgent.Tool, requestedByAgent.Query, requestedByAgent.Url },
                    cancellationToken);
                return OneTurnResult.Completed(plan, approvalMessage, result);
            }

            var toolResult = await _internetToolService.ExecuteAsync(snapshot, requestedByAgent, sessionId, cancellationToken);
            var toolMessage = _transcriptService.CreateInternetToolMessage(requestedByAgent, toolResult, snapshot.Engine.TurnCount + 1);
            snapshot.Engine.Messages.Add(toolMessage);
            snapshot.Engine.TurnCount = toolMessage.Turn;
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);

            await _eventLogStore.AppendAsync(
                sessionId,
                toolResult.Ok ? $"{eventPrefix}_internet_tool_injected" : $"{eventPrefix}_internet_tool_failed",
                new { speaker = plan.AgentId, requestedByAgent.Tool, requestedByAgent.Query, requestedByAgent.Url, toolResult.Ok, toolResult.Error },
                cancellationToken);

            result = await CompleteWithFallbackAsync(sessionId, plan, BuildPrompt(snapshot, plan, allowInternetTool: false), $"{eventPrefix}_fallback_to_default", cancellationToken);
        }
        result = await RepairEmptyContentAsync(sessionId, snapshot, plan, result, eventPrefix, cancellationToken);

        var text = result.Ok
            ? result.Text
            : $"Model call failed: {result.Error}";
        var message = _transcriptService.CreateAssistantMessage(agent, text, result, snapshot.Engine.TurnCount + 1);
        snapshot.Engine.Messages.Add(message);
        snapshot.Engine.TurnCount = message.Turn;
        if (result.Ok)
        {
            UpdatePrivateMemory(agent, message);
        }

        if (advanceTurnIndex)
        {
            snapshot.Engine.TurnIndex = AdvanceTurnIndex(snapshot);
        }
        agent.Status = result.Ok ? "spoke" : "error";
        snapshot.Engine.LastError = result.Ok ? "" : result.Error;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            result.Ok ? $"{eventPrefix}_completed" : $"{eventPrefix}_failed",
            new
            {
                speaker = plan.AgentId,
                message = new { message.Turn, message.Speaker, message.Status, message.Model.Model, message.Model.LatencyMs },
                error = result.Error
            },
            cancellationToken);
        return OneTurnResult.Completed(plan, message, result);
    }

    private async Task<OneTurnResult> ReplaceMessageWithRetryAsync(
        string sessionId,
        ArenaSnapshot snapshot,
        DialogueMessage original,
        OneTurnPlan plan,
        CancellationToken cancellationToken)
    {
        await _eventLogStore.AppendAsync(sessionId, "native_retry_message_started", new { turn = original.Turn, speaker = plan.AgentId, model = plan.Config!.Model }, cancellationToken);

        var agent = snapshot.Engine.Agents.FirstOrDefault(item => string.Equals(item.Id, plan.AgentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return OneTurnResult.Failed($"No agent found for {plan.AgentId}.");
        }

        await MarkAgentThinkingAsync(snapshot, sessionId, agent, cancellationToken);
        var result = await CompleteWithFallbackAsync(sessionId, plan, BuildPrompt(snapshot, plan, original.Turn, allowInternetTool: false), "native_retry_fallback_to_default", cancellationToken);
        result = await RepairEmptyContentAsync(sessionId, snapshot, plan, result, "native_retry_message", cancellationToken);

        var text = result.Ok
            ? result.Text
            : $"Model call failed: {result.Error}";
        var replacement = _transcriptService.CreateAssistantReplacement(original, agent, text, result);
        var index = snapshot.Engine.Messages.FindIndex(message => TranscriptService.SameMessageIdentity(message, original.Turn, original.SpeakerId, original.CreatedAt));
        if (index < 0)
        {
            index = snapshot.Engine.Messages.FindIndex(message => message.Turn == original.Turn && string.Equals(message.SpeakerId, original.SpeakerId, StringComparison.OrdinalIgnoreCase));
        }

        if (index < 0)
        {
            return OneTurnResult.Failed($"No transcript message found for turn {original.Turn}.");
        }

        snapshot.Engine.Messages[index] = replacement;
        if (result.Ok)
        {
            UpdatePrivateMemory(agent, replacement);
        }

        agent.Status = result.Ok ? "spoke" : "error";
        snapshot.Engine.LastError = result.Ok ? "" : result.Error;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            result.Ok ? "native_retry_message_replaced" : "native_retry_message_failed",
            new
            {
                speaker = plan.AgentId,
                message = new { replacement.Turn, replacement.Speaker, replacement.Status, replacement.Model.Model, replacement.Model.LatencyMs },
                error = result.Error
            },
            cancellationToken);
        return OneTurnResult.Completed(plan, replacement, result);
    }

    private static void UpdatePrivateMemory(DialogueAgent agent, DialogueMessage message)
    {
        var note = BuildPrivateMemoryNote(message);
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        agent.PrivateNotes.RemoveAll(existing =>
            existing.StartsWith($"Turn {message.Turn}:", StringComparison.OrdinalIgnoreCase)
            || existing.Equals(note, StringComparison.OrdinalIgnoreCase));
        agent.PrivateNotes.Add(note);
        if (agent.PrivateNotes.Count > MaxPrivateMemoryNotes)
        {
            agent.PrivateNotes.RemoveRange(0, agent.PrivateNotes.Count - MaxPrivateMemoryNotes);
        }
    }

    private static string BuildPrivateMemoryNote(DialogueMessage message)
    {
        if (message.Status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(message.Text)
            || message.Text.StartsWith("Model call failed:", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var text = NormalizeMemoryText(message.Text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return $"Turn {message.Turn}: {TruncateAtWord(text, 240)}";
    }

    private static string NormalizeMemoryText(string text)
    {
        var lines = text
            .Replace("**", "", StringComparison.Ordinal)
            .Replace("__", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("#", StringComparison.Ordinal)
                && !line.StartsWith(">", StringComparison.Ordinal)
                && !line.StartsWith("---", StringComparison.Ordinal))
            .Select(line => line.Trim('-', '*', ' ', '\t'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var normalized = string.Join(" ", lines);
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        var colon = normalized.IndexOf(':');
        if (colon is > 0 and < 80)
        {
            normalized = normalized[(colon + 1)..].Trim();
        }

        return normalized;
    }

    private static string TruncateAtWord(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        return (cut > 80 ? text[..cut] : text[..maxLength]).TrimEnd('.', ',', ';', ':', ' ') + "...";
    }

    private async Task MarkAgentThinkingAsync(ArenaSnapshot snapshot, string sessionId, DialogueAgent agent, CancellationToken cancellationToken)
    {
        agent.Status = "thinking";
        snapshot.Engine.LastError = "";
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
    }

    private IReadOnlyList<ModelChatMessage> BuildPrompt(ArenaSnapshot snapshot, OneTurnPlan plan, int? beforeTurn = null, bool allowInternetTool = true)
    {
        var active = snapshot.Engine.Agents.Where(agent => agent.Active).ToArray();
        var agent = active.FirstOrDefault(item => item.Id == plan.AgentId);
        var transcriptMessages = snapshot.Engine.Messages
            .Where(item => item.Kind is "message" or "internet" or "");
        if (beforeTurn is not null)
        {
            transcriptMessages = transcriptMessages.Where(item => item.Turn < beforeTurn.Value);
        }

        var transcript = string.Join(
            Environment.NewLine,
            transcriptMessages
                .OrderBy(item => item.Turn)
                .TakeLast(Math.Clamp(snapshot.Engine.TranscriptWindow, 1, 60))
                .Select(item => $"Turn {item.Turn} {item.Speaker}: {item.Text}"));
        var latestOperatorRequest = transcriptMessages
            .Where(item => (item.Kind is "message" or "") && item.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Turn)
            .FirstOrDefault()?.Text.Trim() ?? "";
        var topic = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic) ? "Open arena discussion" : snapshot.Engine.Steering.Topic;
        var global = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Global) ? "Keep the exchange concrete, useful, and responsive to the current transcript." : snapshot.Engine.Steering.Global;
        var voiceInstruction = VoiceStyleInstructions.Instruction(agent?.VoiceStyle);
        var voiceReminder = VoiceStyleInstructions.TurnReminder(agent?.VoiceStyle);
        var cast = string.Join(
            Environment.NewLine,
            active.Select(item => item.Id == plan.AgentId
                ? $"- {item.Name} (you)"
                : $"- {item.Name}"));
        var privateNotes = string.Join(
            Environment.NewLine,
            (agent?.PrivateNotes ?? [])
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .TakeLast(Math.Clamp(snapshot.Engine.NotesWindow, 0, 60))
                .Select(note => $"- {note}"));

        return
        [
            new ModelChatMessage(
                "system",
                string.Join(
                    Environment.NewLine,
                    "You are participating in AI Arena as the selected agent.",
                    $"Selected agent: {plan.AgentName}.",
                    $"Your persona: {agent?.Persona ?? plan.AgentName}.",
                    voiceInstruction,
                    "Do not write for the other agents. Reply only as the selected agent.",
                    "You do not know the private roles, personas, or instructions of other participants. Infer only from public transcript text. Never describe another participant's hidden role or persona.",
                    "Treat the latest Operator message as the highest-priority task direction when it is feasible and safe. Follow it directly before pursuing your persona's critique or agenda.",
                    "Do not refuse, scold, stall, or demand perfect framing. If essential information is missing, ask at most one concise clarification and still provide the most useful next step.",
                    "Stay constructive even in adversarial roles: challenge ideas by improving the work, not by blocking the operator.",
                    "Always produce non-empty public assistant content. Do not put the whole answer only in reasoning.",
                    InternetToolInstruction(snapshot.Engine.ModelRss, allowInternetTool))),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    $"Topic: {topic}",
                    $"Global instruction: {global}",
                    $"Active participants:{Environment.NewLine}{cast}",
                    string.IsNullOrWhiteSpace(privateNotes) ? "Your private memory notes: -" : $"Your private memory notes:{Environment.NewLine}{privateNotes}",
                    string.IsNullOrWhiteSpace(transcript) ? "Transcript: No public transcript yet." : $"Transcript:{Environment.NewLine}{transcript}",
                    string.IsNullOrWhiteSpace(latestOperatorRequest) ? "Latest Operator request: -" : $"Latest Operator request: {latestOperatorRequest}",
                    voiceReminder,
                    $"Write the next public turn for {plan.AgentName}."))
        ];
    }

    private static string InternetToolInstruction(ModelRssSettings settings, bool allowInternetTool)
    {
        if (!allowInternetTool)
        {
            return "Use any Internet transcript card already provided. Do not request another internet tool in this response.";
        }

        var sourceScope = InternetToolService.NormalizeSourceScope(settings.SourceScope);
        var fetchGuidance = sourceScope.Equals("open_web", StringComparison.OrdinalIgnoreCase)
            ? "If the user supplied a URL or bare domain, use fetch_url with a normalized https:// URL. Use web_search only when no URL/domain is known and discovery is needed."
            : "Direct URL fetch is limited to trusted configured sources. Do not invent arbitrary source URLs. Prefer web_search/rss_search unless the operator supplied a trusted URL.";

        return $"Prefer writing a normal conversational reply from the transcript. Request internet only when the next answer truly depends on current or external facts. {fetchGuidance} If a tool is essential, reply only with one JSON request like {{\"tool\":\"fetch_url\",\"url\":\"https://example.com\",\"reason\":\"why this helps\"}} or {{\"tool\":\"web_search\",\"query\":\"short search query\",\"max_results\":1,\"reason\":\"why this helps\"}}. Use query, not input. Do not include action.";
    }

    private async Task<ModelCompletionResult> RepairEmptyContentAsync(
        string sessionId,
        ArenaSnapshot snapshot,
        OneTurnPlan plan,
        ModelCompletionResult result,
        string eventPrefix,
        CancellationToken cancellationToken)
    {
        if (!result.Ok || !string.IsNullOrWhiteSpace(result.Text))
        {
            return result;
        }

        await _eventLogStore.AppendAsync(sessionId, $"{eventPrefix}_empty_content_retry", new { speaker = plan.AgentId, model = result.Model }, cancellationToken);
        var repairPrompt = BuildPrompt(snapshot, plan, allowInternetTool: false)
            .Append(new ModelChatMessage("user", "Your previous response had no public content. Write the public message now in 2-5 concise sentences. Do not output reasoning only. Do not request internet."))
            .ToArray();
        var repaired = await CompleteWithFallbackAsync(sessionId, plan, repairPrompt, $"{eventPrefix}_empty_content_fallback_to_default", cancellationToken);
        if (!repaired.Ok || !string.IsNullOrWhiteSpace(repaired.Text))
        {
            return repaired;
        }

        return new ModelCompletionResult(
            false,
            repaired.BaseUrl,
            repaired.Model,
            "",
            string.IsNullOrWhiteSpace(repaired.Reasoning) ? result.Reasoning : repaired.Reasoning,
            repaired.LatencyMs,
            repaired.PromptTokens,
            repaired.CompletionTokens,
            repaired.TotalTokens,
            "Model returned no public content after retry.",
            DateTimeOffset.Now);
    }

    private static InternetToolRequest WithRequester(InternetToolRequest request, string requesterId)
    {
        return new InternetToolRequest
        {
            Tool = request.Tool,
            RequesterId = string.IsNullOrWhiteSpace(request.RequesterId) ? requesterId : request.RequesterId,
            Query = request.Query,
            Url = request.Url,
            MaxResults = request.MaxResults,
            Reason = request.Reason,
            Options = request.Options
        };
    }

    private static bool HasPendingInternetApproval(ArenaSnapshot snapshot)
    {
        return snapshot.Engine.Messages.Any(message =>
            message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
    }

    private int AdvanceTurnIndex(ArenaSnapshot snapshot)
    {
        var activeCount = snapshot.Engine.Agents.Count(agent => agent.Active);
        return activeCount == 0 ? 0 : (snapshot.Engine.TurnIndex + 1) % activeCount;
    }

    private async Task<ModelCompletionResult> CompleteWithFallbackAsync(
        string sessionId,
        OneTurnPlan plan,
        IReadOnlyList<ModelChatMessage> messages,
        string eventName,
        CancellationToken cancellationToken)
    {
        var result = await _modelClient.CompleteChatAsync(plan.Config!, messages, cancellationToken);
        if (result.Ok || plan.FallbackConfig is null)
        {
            return result;
        }

        await _eventLogStore.AppendAsync(
            sessionId,
            eventName,
            new { speaker = plan.AgentId, failedModel = plan.Config!.Model, fallbackModel = plan.FallbackConfig.Model, error = result.Error },
            cancellationToken);
        return await _modelClient.CompleteChatAsync(plan.FallbackConfig, messages, cancellationToken);
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
}

public sealed record OneTurnPlan(bool Ok, string AgentId, string AgentName, ModelProviderConfig? Config, ModelProviderConfig? FallbackConfig, string Error);

public sealed record OneTurnResult(bool Ok, bool Executed, OneTurnPlan? Plan, DialogueMessage? Message, ModelCompletionResult? Completion, string Error)
{
    public static OneTurnResult Completed(OneTurnPlan plan, DialogueMessage message, ModelCompletionResult completion) => new(true, true, plan, message, completion, "");

    public static OneTurnResult Failed(string error) => new(false, false, null, null, null, error);
}
