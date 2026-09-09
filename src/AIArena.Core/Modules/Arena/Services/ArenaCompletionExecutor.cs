using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>One bounded recovery across all calls belonging to a single causal operation.</summary>
internal sealed class ArenaCompletionExecutor(IModelProviderClient client, ArenaTurnProgressScope? progressScope)
{
    internal const string RecoveryMetadataKey = "reasoning_only_recovery";
    private bool recoveryUsed;
    private ModelCompletionResult? initialReasoningOnly;
    private int initialOutputLimit;
    private string reduction = "";
    internal bool RecoveryUsed => recoveryUsed;

    internal async Task<ModelCompletionResult> CompleteAsync(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken, bool publishText = true)
    {
        var result = ModelCompletionOutcomeClassifier.Normalize(
            await CallAsync(config, messages, cancellationToken, publishText).ConfigureAwait(false));
        if (!IsReasoningOnly(result)) return result;
        initialReasoningOnly ??= result;
        if (initialOutputLimit == 0) initialOutputLimit = config.MaxOutputTokens;
        var currentReasoning = ModelProviderReasoningModes.Normalize(config.Reasoning);
        var reducedReasoning = config.RuntimeEvidence?.CanDisableReasoning == true
            ? "off" : config.RuntimeEvidence?.ReducedReasoning == "low" ? "low" : "";
        if (recoveryUsed || reducedReasoning.Length == 0 || currentReasoning == "off"
            || currentReasoning == reducedReasoning)
            return ExplainNoPublicAnswer(result);

        recoveryUsed = true;
        reduction = reducedReasoning;
        cancellationToken.ThrowIfCancellationRequested();
        progressScope?.RecoveryStarting();
        // Preserve the exact original public conversation and request route.
        // In particular Factory mode receives no repair instruction or persona.
        var recoveryConfig = ArenaRequestBudget.Copy(config, reasoning: reducedReasoning);
        var inspection = config.RequestInspectionContext;
        recoveryConfig.RequestInspectionContext = new ProviderRequestInspectionContext(
            inspection?.CorrelationId ?? Guid.NewGuid().ToString("N"),
            "reasoning_recovery",
            (inspection?.Explanations ?? []).Append(new ProviderContextExplanation(
                "reasoning_override", "observed",
                $"Reasoning was explicitly changed to {reducedReasoning} for one supported reasoning-only recovery; the original prompt and conversation anchor were preserved.")).ToArray());
        var recovered = ModelCompletionOutcomeClassifier.Normalize(
            await CallAsync(recoveryConfig, messages, cancellationToken, publishText).ConfigureAwait(false));
        return IsReasoningOnly(recovered) ? ExplainNoPublicAnswer(recovered) : recovered;
    }

    internal void StampEvidence(DialogueMessage message)
    {
        if (initialReasoningOnly is null) return;
        message.Metadata[RecoveryMetadataKey] = JsonSerializer.SerializeToElement(new
        {
            attempted = recoveryUsed,
            reduction,
            initial_stop_reason = ModelCompletionOutcomeClassifier.StopReasonWire(initialReasoningOnly.StopReason),
            initial_provider_stop_reason = initialReasoningOnly.ProviderStopReason,
            initial_completion_tokens = initialReasoningOnly.CompletionTokens,
            initial_output_limit = initialOutputLimit,
            output_cap_reached_inferred = initialOutputLimit > 0 && initialReasoningOnly.CompletionTokens >= initialOutputLimit
        });
    }

    internal static bool IsReasoningOnly(ModelCompletionResult result) =>
        result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent
        && string.IsNullOrWhiteSpace(result.Text)
        && !string.IsNullOrWhiteSpace(result.Reasoning)
        && result.StopReason is not (ModelCompletionStopReason.ContentFiltered
            or ModelCompletionStopReason.ToolCall or ModelCompletionStopReason.ProviderError);

    private Task<ModelCompletionResult> CallAsync(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken, bool publishText) =>
        progressScope is null
            ? client.CompleteChatAsync(config, messages, cancellationToken)
            : progressScope.CompleteAsync(client, config, messages, cancellationToken, publishText);

    private static ModelCompletionResult ExplainNoPublicAnswer(ModelCompletionResult result) => result with
    {
        Error = "The model produced reasoning but no public answer. A supported reduced-reasoning retry did not produce an answer or was unavailable. Increase the response allowance or choose a model that can answer within it."
    };
}
