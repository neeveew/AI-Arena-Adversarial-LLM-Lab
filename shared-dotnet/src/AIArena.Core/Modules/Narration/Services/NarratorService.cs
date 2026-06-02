using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed class NarratorService
{
    private readonly IModelProviderClient _modelClient;
    private readonly SessionStore _sessionStore;
    private readonly EventLogStore _eventLogStore;
    private readonly TranscriptService _transcriptService;

    public NarratorService(
        IModelProviderClient? modelClient = null,
        SessionStore? sessionStore = null,
        EventLogStore? eventLogStore = null,
        TranscriptService? transcriptService = null)
    {
        _modelClient = modelClient ?? new ModelProviderClient();
        _sessionStore = sessionStore ?? new SessionStore();
        _eventLogStore = eventLogStore ?? new EventLogStore(_sessionStore.DataRoot);
        _transcriptService = transcriptService ?? new TranscriptService();
    }

    public async Task<NarratorResult> NarrateNowAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await RunNarratorAsync(sessionId, operatorRequest: "", cancellationToken);
    }

    public async Task<NarratorResult> AskNarratorAsync(string sessionId, string operatorRequest, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operatorRequest))
        {
            return NarratorResult.Failed("Operator request is empty.");
        }

        return await RunNarratorAsync(sessionId, operatorRequest.Trim(), cancellationToken);
    }

    public async Task<DecisionCardResult> GenerateDecisionCardAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return DecisionCardResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var config = ModelProviderRouting.Resolve(snapshot, "narrator", out var fallbackConfig);
        if (config is null)
        {
            return DecisionCardResult.Failed("No provider config for narrator.");
        }

        snapshot.Engine.Narrator.Status = "thinking";
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(sessionId, "native_decision_card_started", new { model = config.Model }, cancellationToken);

        var result = await _modelClient.CompleteChatAsync(config, BuildDecisionCardPrompt(snapshot), cancellationToken);
        if (!result.Ok && fallbackConfig is not null)
        {
            await _eventLogStore.AppendAsync(
                sessionId,
                "native_decision_card_fallback_to_default",
                new { failedModel = config.Model, fallbackModel = fallbackConfig.Model, error = result.Error },
                cancellationToken);
            result = await _modelClient.CompleteChatAsync(fallbackConfig, BuildDecisionCardPrompt(snapshot), cancellationToken);
        }

        snapshot.Engine.DecisionCard.Text = result.Ok ? result.Text.Trim() : $"Decision card failed: {result.Error}";
        snapshot.Engine.DecisionCard.UpdatedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        snapshot.Engine.Narrator.Status = result.Ok ? "spoke" : "error";
        snapshot.Engine.Narrator.LastError = result.Ok ? "" : result.Error;
        snapshot.Engine.LastError = result.Ok ? "" : result.Error;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            result.Ok ? "native_decision_card_completed" : "native_decision_card_failed",
            new { result.Model, result.LatencyMs, error = result.Error },
            cancellationToken);
        return result.Ok
            ? DecisionCardResult.Completed(snapshot.Engine.DecisionCard.Text)
            : DecisionCardResult.Failed(result.Error, snapshot.Engine.DecisionCard.Text);
    }

    private async Task<NarratorResult> RunNarratorAsync(string sessionId, string operatorRequest, CancellationToken cancellationToken)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return NarratorResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var config = ModelProviderRouting.Resolve(snapshot, "narrator", out var fallbackConfig);
        if (config is null)
        {
            return NarratorResult.Failed("No provider config for narrator.");
        }

        snapshot.Engine.Narrator.Status = "thinking";
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            string.IsNullOrWhiteSpace(operatorRequest) ? "native_narrator_started" : "native_narrator_operator_request_started",
            new { model = config.Model },
            cancellationToken);

        var prompt = BuildNarratorPrompt(snapshot, operatorRequest);
        var result = await _modelClient.CompleteChatAsync(config, prompt, cancellationToken);
        if (!result.Ok && fallbackConfig is not null)
        {
            await _eventLogStore.AppendAsync(
                sessionId,
                "native_narrator_fallback_to_default",
                new { failedModel = config.Model, fallbackModel = fallbackConfig.Model, error = result.Error },
                cancellationToken);
            result = await _modelClient.CompleteChatAsync(fallbackConfig, prompt, cancellationToken);
        }
        var text = result.Ok
            ? result.Text
            : $"Narrator call failed: {result.Error}";
        var narratorAgent = new DialogueAgent
        {
            Id = "narrator",
            Name = "Narrator",
            Persona = snapshot.Engine.Narrator.Persona,
            VoiceStyle = snapshot.Engine.Narrator.VoiceStyle,
            Active = false,
            Status = result.Ok ? "spoke" : "error"
        };
        var message = _transcriptService.CreateAssistantMessage(narratorAgent, text, result, snapshot.Engine.TurnCount + 1);
        snapshot.Engine.Messages.Add(message);
        snapshot.Engine.TurnCount = message.Turn;
        snapshot.Engine.Narrator.Status = result.Ok ? "spoke" : "error";
        snapshot.Engine.Narrator.LastError = result.Ok ? "" : result.Error;
        snapshot.Engine.LastError = result.Ok ? "" : result.Error;

        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            result.Ok
                ? string.IsNullOrWhiteSpace(operatorRequest) ? "native_narrator_completed" : "native_narrator_operator_request_completed"
                : string.IsNullOrWhiteSpace(operatorRequest) ? "native_narrator_failed" : "native_narrator_operator_request_failed",
            new { message.Turn, message.Status, message.Model.Model, message.Model.LatencyMs, error = result.Error },
            cancellationToken);
        return result.Ok
            ? NarratorResult.Completed(message)
            : NarratorResult.Failed(result.Error, message);
    }

    private static IReadOnlyList<ModelChatMessage> BuildNarratorPrompt(ArenaSnapshot snapshot, string operatorRequest)
    {
        var transcript = string.Join(
            Environment.NewLine,
            snapshot.Engine.Messages
                .Where(item => item.Kind is "message" or "")
                .OrderBy(item => item.Turn)
                .TakeLast(Math.Clamp(snapshot.Engine.TranscriptWindow, 1, 60))
                .Select(item => $"Turn {item.Turn} {item.Speaker}: {item.Text}"));
        var topic = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic) ? "Open arena discussion" : snapshot.Engine.Steering.Topic;
        var persona = string.IsNullOrWhiteSpace(snapshot.Engine.Narrator.Persona)
            ? "Careful observer. Concise, concrete, and useful."
            : snapshot.Engine.Narrator.Persona;
        var voiceInstruction = VoiceStyleInstructions.Instruction(snapshot.Engine.Narrator.VoiceStyle);
        var voiceReminder = VoiceStyleInstructions.TurnReminder(snapshot.Engine.Narrator.VoiceStyle);

        return
        [
            new ModelChatMessage(
                "system",
                string.Join(
                    Environment.NewLine,
                    "You are the non-participating narrator for AI Arena.",
                    "Write one concise narrator note for the public transcript.",
                    "Do not write as Alpha, Beta, or Gamma.",
                    "Do not fetch live news or external data unless the operator explicitly provided it.",
                    string.IsNullOrWhiteSpace(operatorRequest)
                        ? "Use your own judgment about what the arena needs next."
                        : "Answer the operator request directly, then add only the context needed for the arena.",
                    $"Narrator persona: {persona}",
                    voiceInstruction)),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    $"Topic: {topic}",
                    string.IsNullOrWhiteSpace(transcript) ? "Transcript: No public transcript yet." : $"Transcript:{Environment.NewLine}{transcript}",
                    voiceReminder,
                    string.IsNullOrWhiteSpace(operatorRequest)
                        ? "Write the narrator note now."
                        : $"Operator request for narrator:{Environment.NewLine}{operatorRequest}"))
        ];
    }

    private static IReadOnlyList<ModelChatMessage> BuildDecisionCardPrompt(ArenaSnapshot snapshot)
    {
        var transcript = string.Join(
            Environment.NewLine,
            snapshot.Engine.Messages
                .Where(item => item.Kind is "message" or "")
                .OrderBy(item => item.Turn)
                .TakeLast(Math.Clamp(snapshot.Engine.TranscriptWindow, 1, 60))
                .Select(item => $"Turn {item.Turn} {item.Speaker}: {item.Text}"));
        var topic = string.IsNullOrWhiteSpace(snapshot.Engine.Steering.Topic) ? "Open arena discussion" : snapshot.Engine.Steering.Topic;
        return
        [
            new ModelChatMessage(
                "system",
                string.Join(
                    Environment.NewLine,
                    "You are the decision-card narrator for AI Arena.",
                    "Produce a compact operator-facing decision card.",
                    "Use exactly these headings: Agreed, Conflict, Risk, Next operator move.",
                    "Use short bullet fragments. Do not claim certainty that is not supported by the transcript.")),
            new ModelChatMessage(
                "user",
                string.Join(
                    Environment.NewLine + Environment.NewLine,
                    $"Topic: {topic}",
                    string.IsNullOrWhiteSpace(transcript) ? "Transcript: No public transcript yet." : $"Transcript:{Environment.NewLine}{transcript}",
                    "Write the decision card now."))
        ];
    }

}

public sealed record NarratorResult(bool Ok, DialogueMessage? Message, string Error)
{
    public static NarratorResult Completed(DialogueMessage message) => new(true, message, "");
    public static NarratorResult Failed(string error, DialogueMessage? message = null) => new(false, message, error);
}

public sealed record DecisionCardResult(bool Ok, string Text, string Error)
{
    public static DecisionCardResult Completed(string text) => new(true, text, "");
    public static DecisionCardResult Failed(string error, string text = "") => new(false, text, error);
}
