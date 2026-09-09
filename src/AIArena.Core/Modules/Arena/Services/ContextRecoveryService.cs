using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

public sealed record ContextRecoveryResult(
    bool Ok,
    bool Executed,
    DialogueMessage? Message,
    ModelCompletionResult? Completion,
    string Error)
{
    public static ContextRecoveryResult Failed(string error) => new(false, false, null, null, error);
    public static ContextRecoveryResult Completed(DialogueMessage message, ModelCompletionResult? completion = null) =>
        new(true, true, message, completion, "");
}

/// <summary>Causal recovery actions for input-context and output-limit outcomes.</summary>
public sealed class ContextRecoveryService
{
    public const string RecoveryDispositionMetadataKey = "context_recovery_disposition";
    public const string ContinuationCountMetadataKey = "output_continuation_count";

    private readonly SessionStore _sessionStore;
    private readonly IModelProviderClient _modelClient;
    private readonly TranscriptService _transcriptService;
    private readonly EventLogStore _eventLogStore;
    private readonly IModelRuntimeEvidenceResolver? _runtimeEvidenceResolver;

    public ContextRecoveryService(
        SessionStore? sessionStore = null,
        IModelProviderClient? modelClient = null,
        TranscriptService? transcriptService = null,
        EventLogStore? eventLogStore = null,
        IModelRuntimeEvidenceResolver? runtimeEvidenceResolver = null)
    {
        _sessionStore = sessionStore ?? new SessionStore();
        _modelClient = modelClient ?? new ModelProviderClient();
        _transcriptService = transcriptService ?? new TranscriptService();
        _eventLogStore = eventLogStore ?? EventLogStore.ForSessionStore(_sessionStore);
        _runtimeEvidenceResolver = runtimeEvidenceResolver;
    }

    public async Task<ContextRecoveryResult> SkipBlockedTurnAsync(
        string sessionId,
        int turn,
        string speakerId,
        double createdAt,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return ContextRecoveryResult.Failed($"No snapshot found for session {sessionId}.");
        }

        var message = TranscriptService.FindMessage(snapshot, turn, speakerId, createdAt);
        if (message is null)
        {
            return ContextRecoveryResult.Failed($"No transcript message found for turn {turn}.");
        }

        if (!IsFailureKind(message, "context_limit_exceeded")
            || !message.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return ContextRecoveryResult.Failed("Only a confirmed input-context failure can be skipped.");
        }

        var unresolved = TurnRunnerService.UnresolvedContextFailure(snapshot);
        if (unresolved is null
            || !DialogueMessageIdentity.Resolve(unresolved).Equals(DialogueMessageIdentity.Resolve(message), StringComparison.Ordinal))
        {
            return ContextRecoveryResult.Failed("This response is not the pending input-context failure.");
        }

        if (MetadataString(message, RecoveryDispositionMetadataKey).Equals("skipped", StringComparison.Ordinal))
        {
            return ContextRecoveryResult.Failed("This blocked turn has already been skipped.");
        }

        message.Metadata[RecoveryDispositionMetadataKey] = JsonSerializer.SerializeToElement("skipped");
        message.Metadata["context_recovery_at"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        snapshot.Engine.TurnIndex = TurnRunnerService.AdvancePastAgent(snapshot, message.SpeakerId);

        snapshot.Engine.LastError = "";
        await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        await _eventLogStore.AppendAsync(
            sessionId,
            "context_recovery_turn_skipped",
            new { turn = message.Turn, speaker = message.SpeakerId, failure_kind = "context_limit_exceeded" },
            cancellationToken);
        return ContextRecoveryResult.Completed(message);
    }

    public async Task<ContextRecoveryResult> ContinueOutputAsync(
        string sessionId,
        int turn,
        string speakerId,
        double createdAt,
        CancellationToken cancellationToken = default,
        IProgress<ArenaTurnProgress>? progress = null)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return ContextRecoveryResult.Failed($"No snapshot found for session {sessionId}.");
        }

        if (snapshot.Engine.FactoryMode)
        {
            return ContextRecoveryResult.Failed("Output continuation is unavailable in Factory public_group_v1 mode.");
        }

        var original = TranscriptService.FindMessage(snapshot, turn, speakerId, createdAt);
        if (original is null)
        {
            return ContextRecoveryResult.Failed($"No transcript message found for turn {turn}.");
        }

        if (!original.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
            || !MetadataString(original, "completion_stop_reason").Equals("output_limit_reached", StringComparison.Ordinal))
        {
            return ContextRecoveryResult.Failed("Only a successful partial response stopped by the output limit can be continued.");
        }


        if (MetadataString(original, "output_continuation_state").Equals("in_progress", StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(MetadataString(original, "output_continuation_attempt")))
        {
            return ContextRecoveryResult.Failed("A continuation is already in progress for this response.");
        }

        if (HasLaterSuccessfulPublicDescendant(snapshot, original))
        {
            return ContextRecoveryResult.Failed("This partial response can no longer be continued because later public dialogue already depends on it.");
        }

        var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(original.SpeakerId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
        {
            return ContextRecoveryResult.Failed($"No agent found for {original.SpeakerId}.");
        }

        if (!CompletionRouteReceipt.TryRead(original, out var routeReceipt))
        {
            return ContextRecoveryResult.Failed("The response has no causal provider-route receipt and cannot be continued safely.");
        }

        var primaryConfig = ModelProviderRouting.Resolve(snapshot, agent.Id, out var fallbackConfig);
        var config = routeReceipt.RoutePhase.Equals(CompletionRouteReceipt.FallbackPhase, StringComparison.Ordinal)
            ? fallbackConfig
            : primaryConfig;
        if (config is null || !CompletionRouteReceipt.Matches(routeReceipt, config, routeReceipt.RoutePhase))
        {
            return ContextRecoveryResult.Failed("The provider/model route that produced this response is no longer available or has changed.");
        }

        config = await ArenaRequestBudget.ResolveAsync(config, _runtimeEvidenceResolver, cancellationToken);
        var attemptId = Guid.NewGuid().ToString("N");
        original.Metadata["output_continuation_attempt"] = JsonSerializer.SerializeToElement(attemptId);
        original.Metadata["output_continuation_state"] = JsonSerializer.SerializeToElement("in_progress");
        var originalFingerprint = DialogueMessageIdentity.Fingerprint(original);
        try
        {
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        }
        catch (SnapshotConcurrencyException)
        {
            return ContextRecoveryResult.Failed("Another continuation or session change won the causal save; no provider call was made.");
        }
        var expectedPersistenceRevision = snapshot.PersistenceRevision;
        using var progressScope = new ArenaTurnProgressScope(progress, sessionId, snapshot.SessionInstanceId,
            agent.Id, agent.Name, original.Turn);
        var completionExecutor = new ArenaCompletionExecutor(_modelClient, progressScope);
        var continuationCommitted = false;
        try
        {
            await _eventLogStore.AppendAsync(
                sessionId,
                "context_recovery_output_continue_started",
                new { turn = original.Turn, speaker = original.SpeakerId, attempt = attemptId },
                cancellationToken);

            var plan = new OneTurnPlan(true, agent.Id, agent.Name, config, null, "");
            var frozen = ArenaHistoryBudgetService.TryReadReceipt(original, out var savedReceipt) ? savedReceipt : null;
            var budgeted = ArenaHistoryBudgetService.Build(
                snapshot,
                config,
                original.Turn,
                transcriptAfterTurn: null,
                includedIds =>
                {
                    var messages = TurnRunnerService.BuildPrompt(
                        snapshot,
                        plan,
                        beforeTurn: original.Turn,
                        allowInternetTool: false,
                        enforceVoiceDrift: false,
                        transcriptAfterTurn: null,
                        includedTranscriptMessageIds: includedIds).ToList();
                    messages.Add(new ModelChatMessage("assistant", original.Text));
                    messages.Add(new ModelChatMessage(
                        "user",
                        "Continue the same public answer exactly where it stopped. Do not restart, repeat, summarize, or mention the output limit."));
                    return ModelResponseToneInstructions.Apply(config, messages, factoryMode: false);
                },
                frozen,
                selectionContract: ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(
                    snapshot,
                    original.Turn,
                    transcriptAfterTurn: null));
            if (!budgeted.Ok)
            {
                await AppendContinuationFailedEventAsync(sessionId, original, attemptId,
                    ModelCompletionOutcomeClassifier.FailureKindWire(budgeted.FailureKind));
                return ContextRecoveryResult.Failed(budgeted.Error);
            }

            var continuationConfig = ArenaRequestBudget.Apply(WithoutNativeContinuation(config), budgeted);
            var completion = await completionExecutor.CompleteAsync(continuationConfig, budgeted.Messages, cancellationToken);
            if (!completion.Ok || string.IsNullOrWhiteSpace(completion.Text))
            {
                var error = string.IsNullOrWhiteSpace(completion.Error)
                    ? "The model returned no continuation content."
                    : completion.Error;
                await AppendContinuationFailedEventAsync(sessionId, original, attemptId,
                    ModelCompletionOutcomeClassifier.FailureKindWire(completion.FailureKind));
                return new ContextRecoveryResult(false, true, original, completion, error);
            }

            var current = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
            var currentOriginal = current is null
                ? null
                : TranscriptService.FindMessage(current, original.Turn, original.SpeakerId, original.CreatedAt);
            if (current is null
                || currentOriginal is null
                || current.PersistenceRevision != expectedPersistenceRevision
                || !MetadataString(currentOriginal, "output_continuation_attempt").Equals(attemptId, StringComparison.Ordinal)
                || !DialogueMessageIdentity.Fingerprint(currentOriginal).Equals(originalFingerprint, StringComparison.Ordinal))
            {
                await AppendContinuationFailedEventAsync(sessionId, original, attemptId, "source_changed");
                return ContextRecoveryResult.Failed("The session or source response changed before the continuation finished.");
            }

            var combined = CombineContinuation(original.Text, completion.Text);
            var combinedCompletion = completion with
            {
                Text = combined,
                PromptTokens = Math.Max(0, original.Model.PromptTokens) + Math.Max(0, completion.PromptTokens),
                CompletionTokens = Math.Max(0, original.Model.CompletionTokens) + Math.Max(0, completion.CompletionTokens),
                TotalTokens = Math.Max(0, original.Model.TotalTokens) + Math.Max(0, completion.TotalTokens)
            };
            var replacement = _transcriptService.CreateAssistantReplacement(currentOriginal, agent, combined, combinedCompletion);
            foreach (var pair in currentOriginal.Metadata)
            {
                replacement.Metadata.TryAdd(pair.Key, pair.Value);
            }
            completionExecutor.StampEvidence(replacement);
            replacement.Metadata.Remove("output_continuation_attempt");
            replacement.Metadata["output_continuation_state"] = JsonSerializer.SerializeToElement("completed");
            replacement.Metadata[ContinuationCountMetadataKey] = JsonSerializer.SerializeToElement(
                Math.Max(0, MetadataInt(currentOriginal, ContinuationCountMetadataKey)) + 1);
            ArenaHistoryBudgetService.Stamp(replacement, budgeted.Receipt);
            CompletionRouteReceipt.Stamp(replacement, routeReceipt);

            var index = current.Engine.Messages.FindIndex(message =>
                DialogueMessageIdentity.Resolve(message).Equals(DialogueMessageIdentity.Resolve(currentOriginal), StringComparison.Ordinal));
            if (index < 0)
            {
                return ContextRecoveryResult.Failed("The source response disappeared before the continuation could be saved.");
            }

            current.Engine.Messages[index] = replacement;
            var currentAgent = current.Engine.Agents.First(item => item.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
            TurnRunnerService.UpdatePrivateMemory(current, currentAgent, replacement);
            current.Engine.LastError = "";
            await _sessionStore.SaveSnapshotAsync(current, sessionId, cancellationToken);
            continuationCommitted = true;
            progressScope.Complete(replacement);
            try
            {
                await _eventLogStore.AppendAsync(
                    sessionId,
                    "context_recovery_output_continued",
                    new
                    {
                        turn = replacement.Turn,
                        speaker = replacement.SpeakerId,
                        continuation_count = MetadataInt(replacement, ContinuationCountMetadataKey),
                        stop_reason = ModelCompletionOutcomeClassifier.StopReasonWire(completion.StopReason)
                    },
                    CancellationToken.None);
            }
            catch
            {
                // The transcript replacement is already committed atomically;
                // diagnostic logging must not turn success into a false failure.
            }

            return ContextRecoveryResult.Completed(replacement, completion);
        }
        catch (OperationCanceledException)
        {
            progressScope.Interrupt("canceled");
            throw;
        }
        catch
        {
            progressScope.Interrupt("failed");
            await AppendContinuationFailedEventAsync(sessionId, original, attemptId, "continuation_exception");
            throw;
        }
        finally
        {
            if (!continuationCommitted)
            {
                await ClearContinuationAttemptAsync(
                    sessionId,
                    original,
                    attemptId,
                    cancellationToken.IsCancellationRequested ? "Continuation was cancelled." : "Continuation did not commit.",
                    CancellationToken.None);
            }
        }
    }

    public async Task<ContextRecoveryResult> EndMatchAsync(
        string sessionId,
        string reason = "context_recovery",
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return ContextRecoveryResult.Failed($"No snapshot found for session {sessionId}.");
        }

        if (snapshot.Engine.MatchEnded)
        {
            return ContextRecoveryResult.Failed("This match has already ended.");
        }

        var normalizedReason = Bound(string.IsNullOrWhiteSpace(reason) ? "context_recovery" : reason.Trim(), 120);
        snapshot.Engine.MatchEnded = true;
        snapshot.Engine.MatchEndedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        snapshot.Engine.MatchEndReason = normalizedReason;
        snapshot.Engine.LastError = "";
        foreach (var agent in snapshot.Engine.Agents)
        {
            agent.Status = agent.Active ? "ended" : agent.Status;
        }

        try
        {
            await _sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
        }
        catch (SnapshotConcurrencyException)
        {
            return ContextRecoveryResult.Failed("The session changed before the end-match marker could be saved.");
        }
        try
        {
            await _eventLogStore.AppendAsync(
                sessionId,
                "context_recovery_match_ended",
                new { reason = normalizedReason },
                CancellationToken.None);
        }
        catch
        {
            // The terminal marker is already committed atomically. Diagnostic
            // logging cannot make that durable result appear to have failed.
        }
        var marker = new DialogueMessage
        {
            Turn = snapshot.Engine.TurnCount,
            Speaker = "System",
            SpeakerId = "system",
            Text = "Match ended.",
            Status = "ok",
            Kind = "system",
            CreatedAt = snapshot.Engine.MatchEndedAt ?? 0,
            Metadata = new Dictionary<string, JsonElement>
            {
                [RecoveryDispositionMetadataKey] = JsonSerializer.SerializeToElement("match_ended")
            }
        };
        return ContextRecoveryResult.Completed(marker);
    }

    private async Task ClearContinuationAttemptAsync(
        string sessionId,
        DialogueMessage original,
        string attemptId,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
            var message = current is null
                ? null
                : TranscriptService.FindMessage(current, original.Turn, original.SpeakerId, original.CreatedAt);
            if (current is null
                || message is null
                || !MetadataString(message, "output_continuation_attempt").Equals(attemptId, StringComparison.Ordinal))
            {
                return;
            }

            message.Metadata.Remove("output_continuation_attempt");
            message.Metadata["output_continuation_state"] = JsonSerializer.SerializeToElement("failed");
            message.Metadata["output_continuation_error"] = JsonSerializer.SerializeToElement(Bound(error, 240));
            await _sessionStore.SaveSnapshotAsync(current, sessionId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Best-effort recovery must not obscure the provider outcome.
        }
    }

    private async Task AppendContinuationFailedEventAsync(
        string sessionId,
        DialogueMessage original,
        string attemptId,
        string reason)
    {
        try
        {
            await _eventLogStore.AppendAsync(
                sessionId,
                "context_recovery_output_continue_failed",
                new { turn = original.Turn, speaker = original.SpeakerId, attempt = attemptId, reason },
                CancellationToken.None);
        }
        catch
        {
            // Diagnostic evidence must not replace the recovery outcome.
        }
    }

    private static ModelProviderConfig WithoutNativeContinuation(ModelProviderConfig config) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = config.ConfiguredContextWindow,
        RuntimeEvidence = config.RuntimeEvidence,
        HistoryPolicy = config.HistoryPolicy,
        ResponseTone = config.ResponseTone,
        CustomTone = config.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = false,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        PreviousResponseId = "",
        PreserveNativeInputWhitespace = false,
        Extra = config.Extra
    };

    private static bool IsFailureKind(DialogueMessage message, string expected) =>
        MetadataString(message, "completion_failure_kind").Equals(expected, StringComparison.Ordinal);

    private static string MetadataString(DialogueMessage message, string key) =>
        message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int MetadataInt(DialogueMessage message, string key) =>
        message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static bool HasLaterSuccessfulPublicDescendant(ArenaSnapshot snapshot, DialogueMessage original)
    {
        var index = snapshot.Engine.Messages.FindIndex(message =>
            DialogueMessageIdentity.Resolve(message).Equals(DialogueMessageIdentity.Resolve(original), StringComparison.Ordinal));
        return index >= 0 && snapshot.Engine.Messages
            .Skip(index + 1)
            .Any(message => message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(message.Text)
                && message.Kind is "message" or "");
    }

    private static string CombineContinuation(string original, string continuation) => original + continuation;

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
