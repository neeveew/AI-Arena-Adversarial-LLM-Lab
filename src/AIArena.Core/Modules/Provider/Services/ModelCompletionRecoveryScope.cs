using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Providers;

/// <summary>One supported reasoning reduction per causal operation, shared by all app workflows.</summary>
public sealed class ModelCompletionRecoveryScope
{
    private ModelCompletionResult? initialReasoningOnly;
    private int initialOutputLimit;
    private string reduction = "";
    public bool RecoveryUsed { get; private set; }

    public JsonElement? Evidence => initialReasoningOnly is null ? null : JsonSerializer.SerializeToElement(new
    {
        attempted = RecoveryUsed,
        reduction,
        initial_stop_reason = ModelCompletionOutcomeClassifier.StopReasonWire(initialReasoningOnly.StopReason),
        initial_provider_stop_reason = initialReasoningOnly.ProviderStopReason,
        initial_completion_tokens = initialReasoningOnly.CompletionTokens,
        initial_output_limit = initialOutputLimit,
        output_cap_reached_inferred = initialOutputLimit > 0 && initialReasoningOnly.CompletionTokens >= initialOutputLimit
    });

    public async Task<ModelCompletionResult> CompleteAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
        Func<ModelProviderConfig, IReadOnlyList<ModelChatMessage>, CancellationToken, Task<ModelCompletionResult>> complete,
        CancellationToken cancellationToken, Action? recoveryStarting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = ModelProviderRequestBudget.FitUnchanged(config, messages);
        if (!string.IsNullOrWhiteSpace(budget.Error))
            return new ModelCompletionResult(false, config.BaseUrl, config.Model, "", "", 0, 0, 0, 0,
                budget.Error, DateTimeOffset.UtcNow, FailureKind: ModelCompletionFailureKind.ContextLimitExceeded,
                StopReason: ModelCompletionStopReason.ProviderError);
        config = ModelProviderRequestBudget.Apply(config, budget);
        // Keep the caller's context: workspace completion delegates create and
        // finalize native UI stream cards; Core callers have no UI dependency.
        var result = ModelCompletionOutcomeClassifier.Normalize(await complete(config, messages, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReasoningOnly(result)) return result;
        initialReasoningOnly ??= result;
        if (initialOutputLimit == 0) initialOutputLimit = config.MaxOutputTokens;
        var currentReasoning = ModelProviderReasoningModes.Normalize(config.Reasoning);
        var reducedReasoning = config.RuntimeEvidence?.CanDisableReasoning == true
            ? "off" : config.RuntimeEvidence?.ReducedReasoning == "low" ? "low" : "";
        if (RecoveryUsed || reducedReasoning.Length == 0 || currentReasoning == "off" || currentReasoning == reducedReasoning)
            return ExplainNoPublicAnswer(result);

        RecoveryUsed = true;
        reduction = reducedReasoning;
        cancellationToken.ThrowIfCancellationRequested();
        recoveryStarting?.Invoke();
        var recoveryConfig = ModelProviderRequests.Copy(config, reasoning: reducedReasoning);
        var inspection = config.RequestInspectionContext;
        recoveryConfig.RequestInspectionContext = new ProviderRequestInspectionContext(
            inspection?.CorrelationId ?? Guid.NewGuid().ToString("N"), "reasoning_recovery",
            (inspection?.Explanations ?? []).Append(new ProviderContextExplanation("reasoning_override", "observed",
                $"Reasoning was explicitly changed to {reducedReasoning} for one supported reasoning-only recovery; the original prompt and conversation anchor were preserved.")).ToArray());
        var recovered = ModelCompletionOutcomeClassifier.Normalize(await complete(recoveryConfig, messages, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return IsReasoningOnly(recovered) ? ExplainNoPublicAnswer(recovered) : recovered;
    }

    public static bool IsReasoningOnly(ModelCompletionResult result) =>
        result.FailureKind == ModelCompletionFailureKind.EmptyPublicContent && string.IsNullOrWhiteSpace(result.Text)
        && !string.IsNullOrWhiteSpace(result.Reasoning)
        && result.StopReason is not (ModelCompletionStopReason.ContentFiltered or ModelCompletionStopReason.ToolCall or ModelCompletionStopReason.ProviderError);

    public static bool CanUseFallback(ModelCompletionResult result) => !result.Ok && string.IsNullOrWhiteSpace(result.Text)
        && !IsReasoningOnly(result) && result.FailureKind != ModelCompletionFailureKind.ContextLimitExceeded
        && result.StopReason is not (ModelCompletionStopReason.ContentFiltered or ModelCompletionStopReason.ToolCall);

    private static ModelCompletionResult ExplainNoPublicAnswer(ModelCompletionResult result) => result with
    {
        Error = "The model produced reasoning but no public answer. A supported reduced-reasoning retry did not produce an answer or was unavailable. Increase the response allowance or choose a model that can answer within it."
    };
}
