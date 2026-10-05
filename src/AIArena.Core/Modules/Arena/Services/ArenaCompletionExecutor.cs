using AIArena.Core.Models;
using AIArena.Core.Providers;

namespace AIArena.Core.Services;

/// <summary>One bounded recovery across all calls belonging to a single causal operation.</summary>
internal sealed class ArenaCompletionExecutor(IModelProviderClient client, ArenaTurnProgressScope? progressScope)
{
    internal const string RecoveryMetadataKey = "reasoning_only_recovery";
    private readonly ModelCompletionRecoveryScope recovery = new();
    internal bool RecoveryUsed => recovery.RecoveryUsed;

    internal Task<ModelCompletionResult> CompleteAsync(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken, bool publishText = true)
        => recovery.CompleteAsync(config, messages,
            (request, prompt, token) => CallAsync(request, prompt, token, publishText),
            cancellationToken, () => progressScope?.RecoveryStarting());

    internal void StampEvidence(DialogueMessage message)
    {
        if (recovery.Evidence is { } evidence) message.Metadata[RecoveryMetadataKey] = evidence;
    }

    internal static bool IsReasoningOnly(ModelCompletionResult result) =>
        ModelCompletionRecoveryScope.IsReasoningOnly(result);

    private Task<ModelCompletionResult> CallAsync(ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken, bool publishText) =>
        progressScope is null
            ? client.CompleteChatAsync(config, messages, cancellationToken)
            : progressScope.CompleteAsync(client, config, messages, cancellationToken, publishText);

}
