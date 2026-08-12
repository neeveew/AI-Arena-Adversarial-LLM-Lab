using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

public sealed class ArenaHistoryBudgetReceipt
{
    public const string ContractVersion = "arena_history_budget_v1";
    public const string MetadataKey = "arena_history_budget_receipt";

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = ContractVersion;

    [JsonPropertyName("history_policy")]
    public string HistoryPolicy { get; init; } = ModelHistoryPolicies.Rolling80;

    [JsonPropertyName("configured_context_window")]
    public int ConfiguredContextWindow { get; init; }

    [JsonPropertyName("target_percent")]
    public int TargetPercent { get; init; } = 80;

    [JsonPropertyName("input_token_budget")]
    public int InputTokenBudget { get; init; }

    [JsonPropertyName("output_token_reserve")]
    public int OutputTokenReserve { get; init; }

    [JsonPropertyName("estimated_prompt_tokens")]
    public int EstimatedPromptTokens { get; init; }

    [JsonPropertyName("eligible_entry_count")]
    public int EligibleEntryCount { get; init; }

    [JsonPropertyName("included_entry_count")]
    public int IncludedEntryCount { get; init; }

    [JsonPropertyName("omitted_entry_count")]
    public int OmittedEntryCount { get; init; }

    [JsonPropertyName("included_message_ids")]
    public List<string> IncludedMessageIds { get; init; } = new();

    [JsonPropertyName("context_fingerprint")]
    public string ContextFingerprint { get; init; } = "";

    [JsonPropertyName("before_turn")]
    public int BeforeTurn { get; init; }

    [JsonPropertyName("token_evidence")]
    public string TokenEvidence { get; init; } = "estimated_v1";
}

public sealed record ArenaBudgetedPrompt(
    IReadOnlyList<ModelChatMessage> Messages,
    ArenaHistoryBudgetReceipt? Receipt,
    string Error,
    ModelCompletionFailureKind FailureKind = ModelCompletionFailureKind.None)
{
    public bool Ok => string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// Deterministic Arena-only whole-entry retention. Factory callers never invoke
/// this service and retain the public_group_v1 root-plus-49 contract unchanged.
/// </summary>
public static class ArenaHistoryBudgetService
{
    public const int TargetPercent = 80;
    public const string TokenEvidence = "estimated_v1";

    public static ArenaBudgetedPrompt Build(
        ArenaSnapshot snapshot,
        ModelProviderConfig config,
        int? beforeTurn,
        int? transcriptAfterTurn,
        Func<IReadOnlySet<string>?, IReadOnlyList<ModelChatMessage>> promptFactory,
        ArenaHistoryBudgetReceipt? frozenReceipt = null,
        Func<DialogueMessage, bool>? eligibility = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(promptFactory);

        if (snapshot.Engine.FactoryMode)
        {
            return new ArenaBudgetedPrompt(promptFactory(null), null, "");
        }

        var policy = ModelHistoryPolicies.NormalizeHistoryPolicy(config.HistoryPolicy);
        var contextWindow = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        if (frozenReceipt is null && (policy != ModelHistoryPolicies.Rolling80 || contextWindow <= 0))
        {
            return new ArenaBudgetedPrompt(promptFactory(null), null, "");
        }

        var causalBeforeTurn = beforeTurn ?? checked(snapshot.Engine.TurnCount + 1);
        var eligible = snapshot.Engine.Messages
            .Where(message => eligibility?.Invoke(message) ?? message.Kind is "message" or "internet" or "")
            .Where(message => message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .Where(message => message.Turn < causalBeforeTurn)
            .Where(message => transcriptAfterTurn is null || message.Turn > transcriptAfterTurn.Value)
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToList();
        var identities = EligibleIdentities(eligible);

        IReadOnlyList<string> includedIds;
        if (frozenReceipt is not null)
        {
            if (!string.Equals(frozenReceipt.Contract, ArenaHistoryBudgetReceipt.ContractVersion, StringComparison.Ordinal)
                || frozenReceipt.BeforeTurn != causalBeforeTurn)
            {
                return new ArenaBudgetedPrompt([], null, "The saved Arena history receipt does not match this causal retry boundary.");
            }

            var eligibleSet = identities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (frozenReceipt.IncludedMessageIds.Any(id => !eligibleSet.Contains(id)))
            {
                return new ArenaBudgetedPrompt([], null, "The saved Arena history receipt references transcript entries that are no longer available.");
            }

            var selected = identities
                .Where(item => frozenReceipt.IncludedMessageIds.Contains(item.Id, StringComparer.Ordinal))
                .ToList();
            if (!string.Equals(ContextFingerprint(selected), frozenReceipt.ContextFingerprint, StringComparison.Ordinal))
            {
                return new ArenaBudgetedPrompt([], null, "The saved Arena history context no longer matches the original retry evidence.");
            }

            includedIds = selected.Select(item => item.Id).ToArray();
        }
        else
        {
            includedIds = identities.Select(item => item.Id).ToList();
        }

        var outputReserve = Math.Max(0, config.MaxOutputTokens);
        var targetTotal = Math.Max(1, (int)Math.Floor(contextWindow * (TargetPercent / 100d)));
        var inputBudget = Math.Max(1, targetTotal - outputReserve);
        var mandatoryIds = identities
            .Where(item => item.Message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .TakeLast(1)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        // Preserve the newest successful dialogue entry as well as the latest
        // Operator direction. They may be the same row when Operator spoke
        // most recently. Internet/tool rows do not displace public dialogue.
        var newestDialogueId = identities
            .Where(item => item.Message.Kind is "message" or "")
            .TakeLast(1)
            .Select(item => item.Id)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(newestDialogueId))
        {
            mandatoryIds.Add(newestDialogueId);
        }
        var mutable = includedIds.ToList();
        IReadOnlyList<ModelChatMessage> messages;
        int estimated;
        while (true)
        {
            var selectedSet = mutable.ToHashSet(StringComparer.Ordinal);
            messages = promptFactory(selectedSet);
            estimated = EstimateTokens(messages);
            if (frozenReceipt is not null || estimated <= inputBudget)
            {
                break;
            }

            var removable = mutable.FirstOrDefault(id => !mandatoryIds.Contains(id));
            if (string.IsNullOrEmpty(removable))
            {
                break;
            }

            mutable.Remove(removable);
        }

        var retained = identities.Where(item => mutable.Contains(item.Id, StringComparer.Ordinal)).ToList();
        var receipt = new ArenaHistoryBudgetReceipt
        {
            ConfiguredContextWindow = contextWindow,
            InputTokenBudget = inputBudget,
            OutputTokenReserve = outputReserve,
            EstimatedPromptTokens = estimated,
            EligibleEntryCount = identities.Count,
            IncludedEntryCount = retained.Count,
            OmittedEntryCount = Math.Max(0, identities.Count - retained.Count),
            IncludedMessageIds = retained.Select(item => item.Id).ToList(),
            ContextFingerprint = ContextFingerprint(retained),
            BeforeTurn = causalBeforeTurn
        };
        if (estimated > inputBudget)
        {
            return new ArenaBudgetedPrompt(
                messages,
                receipt,
                $"Input context estimate {estimated} tokens exceeds the Rolling 80 input budget of {inputBudget}; retained mandatory prompt text was not truncated.",
                ModelCompletionFailureKind.ContextLimitExceeded);
        }

        return new ArenaBudgetedPrompt(messages, receipt, "");
    }

    public static int EstimateTokens(IReadOnlyList<ModelChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long total = 2;
        foreach (var message in messages)
        {
            var bytes = Encoding.UTF8.GetByteCount(message.Role ?? "")
                + Encoding.UTF8.GetByteCount(message.Content ?? "");
            total += 4 + (bytes + 3L) / 4L;
        }

        return (int)Math.Min(int.MaxValue, total);
    }

    public static bool TryReadReceipt(DialogueMessage message, out ArenaHistoryBudgetReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(message);
        receipt = new ArenaHistoryBudgetReceipt();
        if (!message.Metadata.TryGetValue(ArenaHistoryBudgetReceipt.MetadataKey, out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            receipt = element.Deserialize<ArenaHistoryBudgetReceipt>() ?? new ArenaHistoryBudgetReceipt();
            return string.Equals(receipt.Contract, ArenaHistoryBudgetReceipt.ContractVersion, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void Stamp(DialogueMessage message, ArenaHistoryBudgetReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (receipt is null)
        {
            message.Metadata.Remove(ArenaHistoryBudgetReceipt.MetadataKey);
            return;
        }

        message.Metadata[ArenaHistoryBudgetReceipt.MetadataKey] = JsonSerializer.SerializeToElement(receipt);
    }

    public static IReadOnlyList<DialogueMessage> SelectIncluded(
        IEnumerable<DialogueMessage> messages,
        IReadOnlySet<string> includedMessageIds)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(includedMessageIds);
        return EligibleIdentities(messages.ToList())
            .Where(item => includedMessageIds.Contains(item.Id))
            .Select(item => item.Message)
            .ToArray();
    }

    private static List<IdentifiedMessage> EligibleIdentities(IReadOnlyList<DialogueMessage> messages)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<IdentifiedMessage>(messages.Count);
        foreach (var message in messages)
        {
            var baseId = DialogueMessageIdentity.Resolve(message);
            occurrences.TryGetValue(baseId, out var occurrence);
            occurrences[baseId] = occurrence + 1;
            var id = occurrence == 0 ? baseId : $"{baseId}:{occurrence}";
            result.Add(new IdentifiedMessage(id, message));
        }

        return result;
    }

    private static string ContextFingerprint(IReadOnlyList<IdentifiedMessage> messages)
    {
        var canonical = string.Join(
            "\n",
            messages.Select(item => $"{item.Id}|{DialogueMessageIdentity.Fingerprint(item.Message)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record IdentifiedMessage(string Id, DialogueMessage Message);
}
