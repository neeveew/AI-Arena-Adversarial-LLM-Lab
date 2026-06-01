using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed class TranscriptService
{
    public IReadOnlyList<DialogueMessage> NewestFirst(ArenaSnapshot snapshot)
    {
        return snapshot.Engine.Messages
            .OrderByDescending(item => item.Turn)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public DialogueAgent? NextActiveAgent(ArenaSnapshot snapshot)
    {
        var active = snapshot.Engine.Agents.Where(agent => agent.Active).ToArray();
        return active.Length == 0
            ? null
            : active[snapshot.Engine.TurnIndex % active.Length];
    }

    public DialogueMessage CreateAssistantMessage(DialogueAgent agent, string text, ModelCompletionResult result, int nextTurn)
    {
        return new DialogueMessage
        {
            Turn = nextTurn,
            Speaker = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name,
            SpeakerId = agent.Id,
            Text = string.IsNullOrWhiteSpace(text) ? "(empty model response)" : text,
            Status = result.Ok ? "ok" : "error",
            Kind = "message",
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = new ModelMetadata
            {
                Model = result.Model,
                LatencyMs = result.LatencyMs,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens
            },
            Metadata = AssistantMetadata(result.Reasoning, agent.VoiceStyle)
        };
    }

    public DialogueMessage CreateOperatorMessage(string text, int nextTurn)
    {
        return new DialogueMessage
        {
            Turn = nextTurn,
            Speaker = "Operator",
            SpeakerId = "operator",
            Text = string.IsNullOrWhiteSpace(text) ? "(empty operator turn)" : text.Trim(),
            Status = "ok",
            Kind = "message",
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = new ModelMetadata
            {
                Model = "operator",
                LatencyMs = 0
            }
        };
    }

    public DialogueMessage CreateInternetToolMessage(InternetToolRequest request, InternetToolResult result, int nextTurn)
    {
        var sourceLines = result.Sources.Count == 0
            ? "Sources: none"
            : "Sources:" + Environment.NewLine + string.Join(
                Environment.NewLine,
                result.Sources.Select((source, index) => $"{index + 1}. {source.Source}: {source.Title} - {source.Url}"));
        var body = string.Join(
            Environment.NewLine,
            $"Requester: {request.RequesterId}",
            $"Tool: {request.Tool}",
            string.IsNullOrWhiteSpace(request.Query) ? "" : $"Query: {request.Query}",
            string.IsNullOrWhiteSpace(request.Url) ? "" : $"URL: {request.Url}",
            string.IsNullOrWhiteSpace(request.Reason) ? "" : $"Reason: {request.Reason}",
            $"Fetch: {(result.Cached ? "cached" : "fetched")} at {result.CheckedAt:yyyy-MM-dd HH:mm:ss zzz}",
            result.Ok ? result.Summary : $"Error: {result.Error}",
            sourceLines).Trim();

        return new DialogueMessage
        {
            Turn = nextTurn,
            Speaker = "Internet",
            SpeakerId = "internet",
            Text = body,
            Status = result.Ok ? "ok" : "error",
            Kind = "internet",
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = new ModelMetadata
            {
                Model = "internet-tool",
                LatencyMs = 0
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["tool_request"] = JsonSerializer.SerializeToElement(request),
                ["tool_result"] = JsonSerializer.SerializeToElement(result)
            }
        };
    }

    public DialogueMessage CreateInternetApprovalMessage(InternetToolRequest request, int nextTurn)
    {
        var body = string.Join(
            Environment.NewLine,
            $"Requester: {request.RequesterId}",
            $"Tool: {request.Tool}",
            string.IsNullOrWhiteSpace(request.Query) ? "" : $"Query: {request.Query}",
            string.IsNullOrWhiteSpace(request.Url) ? "" : $"URL: {request.Url}",
            string.IsNullOrWhiteSpace(request.Reason) ? "" : $"Reason: {request.Reason}",
            "Status: waiting for operator approval").Trim();

        return new DialogueMessage
        {
            Turn = nextTurn,
            Speaker = "Internet Approval",
            SpeakerId = "internet",
            Text = body,
            Status = "pending",
            Kind = "internet_approval",
            CreatedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Model = new ModelMetadata
            {
                Model = "internet-tool",
                LatencyMs = 0
            },
            Metadata = new Dictionary<string, JsonElement>
            {
                ["tool_request"] = JsonSerializer.SerializeToElement(request)
            }
        };
    }

    public DialogueMessage CreateAssistantReplacement(DialogueMessage original, DialogueAgent agent, string text, ModelCompletionResult result)
    {
        return new DialogueMessage
        {
            Turn = original.Turn,
            Speaker = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name,
            SpeakerId = agent.Id,
            Text = string.IsNullOrWhiteSpace(text) ? "(empty model response)" : text,
            Status = result.Ok ? "ok" : "error",
            Pinned = original.Pinned,
            Kind = original.Kind,
            CreatedAt = original.CreatedAt,
            Model = new ModelMetadata
            {
                Model = result.Model,
                LatencyMs = result.LatencyMs,
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                TotalTokens = result.TotalTokens
            },
            Metadata = AssistantMetadata(result.Reasoning, agent.VoiceStyle),
            Extra = original.Extra
        };
    }

    public bool DeleteMessage(ArenaSnapshot snapshot, int turn, string speakerId, double createdAt)
    {
        var message = FindMessage(snapshot, turn, speakerId, createdAt);
        return message is not null && snapshot.Engine.Messages.Remove(message);
    }

    public bool TogglePinned(ArenaSnapshot snapshot, int turn, string speakerId, double createdAt, out bool pinned)
    {
        pinned = false;
        var index = snapshot.Engine.Messages.FindIndex(item => SameMessageIdentity(item, turn, speakerId, createdAt) || SameMessageFallback(item, turn, speakerId, createdAt));
        if (index < 0)
        {
            return false;
        }

        var message = snapshot.Engine.Messages[index];
        pinned = !message.Pinned;
        snapshot.Engine.Messages[index] = CopyMessage(message, pinned);
        return true;
    }

    public InternetToolRequest? InternetRequestFor(DialogueMessage message)
    {
        if (!message.Metadata.TryGetValue("tool_request", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return value.Deserialize<InternetToolRequest>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool ReplaceMessage(ArenaSnapshot snapshot, int turn, string speakerId, double createdAt, DialogueMessage replacement)
    {
        var index = snapshot.Engine.Messages.FindIndex(item => SameMessageIdentity(item, turn, speakerId, createdAt) || SameMessageFallback(item, turn, speakerId, createdAt));
        if (index < 0)
        {
            return false;
        }

        snapshot.Engine.Messages[index] = replacement;
        return true;
    }

    public static string ReasoningContent(DialogueMessage message)
    {
        return message.Metadata.TryGetValue("reasoning_content", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    public static bool SameMessageIdentity(DialogueMessage message, int turn, string speakerId, double createdAt)
    {
        return message.Turn == turn
            && string.Equals(message.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(message.CreatedAt - createdAt) < 0.0001;
    }

    public static DialogueMessage? FindMessage(ArenaSnapshot snapshot, int turn, string speakerId, double createdAt)
    {
        return snapshot.Engine.Messages.FirstOrDefault(item => SameMessageIdentity(item, turn, speakerId, createdAt))
            ?? snapshot.Engine.Messages.FirstOrDefault(item => SameMessageFallback(item, turn, speakerId, createdAt));
    }

    private static Dictionary<string, JsonElement> ReasoningMetadata(string reasoning)
    {
        return string.IsNullOrWhiteSpace(reasoning)
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>
            {
                ["reasoning_content"] = JsonSerializer.SerializeToElement(reasoning.Trim())
            };
    }

    private static Dictionary<string, JsonElement> AssistantMetadata(string reasoning, string voiceStyle)
    {
        var metadata = ReasoningMetadata(reasoning);
        var normalizedVoiceStyle = VoiceStyleInstructions.Normalize(voiceStyle);
        if (!normalizedVoiceStyle.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            metadata["voice_style"] = JsonSerializer.SerializeToElement(normalizedVoiceStyle);
        }

        return metadata;
    }

    private static bool SameMessageFallback(DialogueMessage message, int turn, string speakerId, double createdAt)
    {
        return message.Turn == turn
            && string.Equals(message.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase)
            && (createdAt <= 0 || message.CreatedAt <= 0);
    }

    private static DialogueMessage CopyMessage(DialogueMessage message, bool pinned)
    {
        return new DialogueMessage
        {
            Turn = message.Turn,
            Speaker = message.Speaker,
            SpeakerId = message.SpeakerId,
            Text = message.Text,
            Status = message.Status,
            Pinned = pinned,
            Kind = message.Kind,
            CreatedAt = message.CreatedAt,
            Model = message.Model,
            Metadata = message.Metadata,
            Extra = message.Extra
        };
    }
}
