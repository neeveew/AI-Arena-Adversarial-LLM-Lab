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
/// Selects how Rolling-80 may search the caller's exact prompt sequence.
/// <see cref="DeterministicNonIncreasingEstimatedTokens"/> is an assertion by
/// the caller about the actual value returned by <see cref="ArenaHistoryBudgetService.EstimateTokens"/>
/// after each successive oldest nonmandatory entry is removed. It is not a
/// general claim that the prompt looks additive or monotonic.
/// </summary>
public enum ArenaHistoryPromptSelectionContract
{
    /// <summary>Evaluate every removal in order; valid for any prompt factory.</summary>
    LegacyExact = 0,

    /// <summary>
    /// The factory is deterministic and its estimated-token sequence is
    /// non-increasing for the service's exact oldest-first removal order.
    /// </summary>
    DeterministicNonIncreasingEstimatedTokens = 1
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
        Func<DialogueMessage, bool>? eligibility = null,
        ArenaHistoryPromptSelectionContract selectionContract = ArenaHistoryPromptSelectionContract.LegacyExact,
        IReadOnlyList<DialogueMessage>? preparedEligibleMessages = null,
        int? preparedTurnCount = null,
        bool? preparedFactoryMode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(promptFactory);

        if (preparedFactoryMode ?? snapshot.Engine.FactoryMode)
        {
            return new ArenaBudgetedPrompt(promptFactory(null), null, "");
        }

        var policy = ModelHistoryPolicies.NormalizeHistoryPolicy(config.HistoryPolicy);
        var contextWindow = ModelRuntimeSettingsRegistry.EffectiveConfiguredContextWindow(config);
        if (frozenReceipt is null && (policy != ModelHistoryPolicies.Rolling80 || contextWindow <= 0))
        {
            return new ArenaBudgetedPrompt(promptFactory(null), null, "");
        }

        var causalBeforeTurn = beforeTurn ?? checked((preparedTurnCount ?? snapshot.Engine.TurnCount) + 1);
        var eligible = preparedEligibleMessages?.ToList()
            ?? EligibleMessages(
                snapshot,
                causalBeforeTurn,
                transcriptAfterTurn,
                eligibility);
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
        PromptSelection selection;
        if (frozenReceipt is not null)
        {
            selection = BuildExactCandidate(includedIds, promptFactory);
        }
        else if (selectionContract == ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens
            && TrySelectWithAssertedMonotonicSequence(
                includedIds,
                mandatoryIds,
                inputBudget,
                promptFactory,
                out var optimized))
        {
            selection = optimized;
        }
        else
        {
            selection = SelectLegacyExact(
                includedIds,
                mandatoryIds,
                inputBudget,
                promptFactory);
        }

        var retainedSet = selection.IncludedIds.ToHashSet(StringComparer.Ordinal);
        var retained = identities.Where(item => retainedSet.Contains(item.Id)).ToList();
        var messages = selection.Messages;
        var estimated = selection.EstimatedTokens;

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

    /// <summary>
    /// Conservatively proves the concrete Arena turn prompt's estimated-token
    /// sequence is non-increasing. Internet grounding is selection-dependent,
    /// and an unpinned latest-Operator/empty-transcript branch is not asserted.
    /// </summary>
    internal static ArenaHistoryPromptSelectionContract ArenaTurnPromptSelectionContract(
        ArenaSnapshot snapshot,
        int? beforeTurn,
        int? transcriptAfterTurn,
        IReadOnlyList<DialogueMessage>? preparedEligibleMessages = null,
        bool? preparedUseInternet = null,
        int? preparedTurnCount = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (preparedUseInternet ?? snapshot.Engine.Internet.UseInternet)
        {
            return ArenaHistoryPromptSelectionContract.LegacyExact;
        }

        var causalBeforeTurn = beforeTurn ?? checked((preparedTurnCount ?? snapshot.Engine.TurnCount) + 1);
        var eligible = preparedEligibleMessages?.ToList()
            ?? EligibleMessages(snapshot, causalBeforeTurn, transcriptAfterTurn, eligibility: null);
        if (eligible.Count == 0)
        {
            return ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens;
        }

        var latestOperatorIndex = eligible.FindLastIndex(message =>
            message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase));
        var latestOperator = latestOperatorIndex < 0 ? null : eligible[latestOperatorIndex];
        if (latestOperator is not null && !IsDialogue(latestOperator))
        {
            return ArenaHistoryPromptSelectionContract.LegacyExact;
        }

        // BuildPrompt's stable OrderByDescending(Turn).First() chooses the
        // earliest row within the newest turn, while mandatory retention takes
        // the last row in full Turn/CreatedAt order. If those differ, the
        // Latest Operator request could change after a removal.
        var latestDialogueOperatorTurn = eligible
            .Where(message => IsDialogue(message)
                && message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .Select(message => (int?)message.Turn)
            .Max();
        var promptLatestOperatorIndex = latestDialogueOperatorTurn is null
            ? -1
            : eligible.FindIndex(message => message.Turn == latestDialogueOperatorTurn.Value
                && IsDialogue(message)
                && message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase));
        if (promptLatestOperatorIndex >= 0 && promptLatestOperatorIndex != latestOperatorIndex)
        {
            return ArenaHistoryPromptSelectionContract.LegacyExact;
        }

        // BuildPrompt emits every eligible row in its transcript. At least one
        // row must remain mandatory so its empty/non-empty branch cannot flip.
        var hasMandatoryTranscriptRow = latestOperator is not null || eligible.Any(IsDialogue);
        return hasMandatoryTranscriptRow
            ? ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens
            : ArenaHistoryPromptSelectionContract.LegacyExact;
    }

    /// <summary>
    /// Conservatively proves the concrete Narrator/Decision Card prompt's
    /// estimated-token sequence is non-increasing. Transcript presence is
    /// pinned by the newest dialogue row. Context presence must likewise be
    /// invariant (empty throughout or pinned by a mandatory context row).
    /// </summary>
    internal static ArenaHistoryPromptSelectionContract NarratorPromptSelectionContract(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var causalBeforeTurn = checked(snapshot.Engine.TurnCount + 1);
        var eligible = EligibleMessages(
            snapshot,
            causalBeforeTurn,
            transcriptAfterTurn: null,
            message => message.Kind is "message" or "internet" or "internet_tool" or "");
        if (eligible.Count == 0)
        {
            return ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens;
        }

        var latestOperator = eligible
            .Where(message => message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();
        var newestDialogue = eligible.Where(IsDialogue).LastOrDefault();
        var contextRows = eligible.Where(IsNarratorContextRow).ToArray();
        var contextPresenceIsPinned = contextRows.Length == 0
            || latestOperator is not null && IsNarratorContextRow(latestOperator)
            || newestDialogue is not null && IsNarratorContextRow(newestDialogue);
        return contextPresenceIsPinned
            ? ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens
            : ArenaHistoryPromptSelectionContract.LegacyExact;
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

    private static PromptSelection SelectLegacyExact(
        IReadOnlyList<string> includedIds,
        IReadOnlySet<string> mandatoryIds,
        int inputBudget,
        Func<IReadOnlySet<string>?, IReadOnlyList<ModelChatMessage>> promptFactory)
    {
        var mutable = includedIds.ToList();
        while (true)
        {
            var candidate = BuildExactCandidate(mutable, promptFactory);
            if (candidate.EstimatedTokens <= inputBudget)
            {
                return candidate;
            }

            var removableIndex = mutable.FindIndex(id => !mandatoryIds.Contains(id));
            if (removableIndex < 0)
            {
                return candidate;
            }

            mutable.RemoveAt(removableIndex);
        }
    }

    private static bool TrySelectWithAssertedMonotonicSequence(
        IReadOnlyList<string> includedIds,
        IReadOnlySet<string> mandatoryIds,
        int inputBudget,
        Func<IReadOnlySet<string>?, IReadOnlyList<ModelChatMessage>> promptFactory,
        out PromptSelection selection)
    {
        var removalOrder = includedIds.Where(id => !mandatoryIds.Contains(id)).ToArray();
        var cache = new Dictionary<int, PromptSelection>();

        PromptSelection At(int removalCount)
        {
            if (cache.TryGetValue(removalCount, out var cached))
            {
                return cached;
            }

            var removed = removalCount == 0
                ? null
                : removalOrder.Take(removalCount).ToHashSet(StringComparer.Ordinal);
            var candidateIds = removed is null
                ? includedIds.ToList()
                : includedIds.Where(id => !removed.Contains(id)).ToList();
            var candidate = BuildExactCandidate(candidateIds, promptFactory);
            cache[removalCount] = candidate;
            return candidate;
        }

        var first = At(0);
        var selectedRemovalCount = 0;
        if (first.EstimatedTokens > inputBudget)
        {
            var maximumRemovalCount = removalOrder.Length;
            var last = At(maximumRemovalCount);
            if (last.EstimatedTokens > inputBudget)
            {
                selectedRemovalCount = maximumRemovalCount;
            }
            else
            {
                var knownOver = 0;
                var knownFit = maximumRemovalCount;
                while (knownFit - knownOver > 1)
                {
                    var midpoint = knownOver + (knownFit - knownOver) / 2;
                    if (At(midpoint).EstimatedTokens <= inputBudget)
                    {
                        knownFit = midpoint;
                    }
                    else
                    {
                        knownOver = midpoint;
                    }
                }

                selectedRemovalCount = knownFit;
                // Exact boundary certification is part of the assertion
                // contract. It is intentionally cached when binary search has
                // already visited either side.
                _ = At(selectedRemovalCount - 1);
            }
        }

        selection = At(selectedRemovalCount);
        var selectedFits = selection.EstimatedTokens <= inputBudget;
        var boundaryIsCertified = selectedRemovalCount == 0
            ? selectedFits || removalOrder.Length == 0
            : selectedFits
                ? At(selectedRemovalCount - 1).EstimatedTokens > inputBudget
                : selectedRemovalCount == removalOrder.Length;
        var observedSequence = cache
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.EstimatedTokens)
            .ToArray();
        var observedMonotonic = observedSequence
            .Zip(observedSequence.Skip(1), (earlier, later) => later <= earlier)
            .All(value => value);
        if (boundaryIsCertified && observedMonotonic)
        {
            return true;
        }

        // The caller's assertion was contradicted by exact prompt bytes. Do
        // not guess: the caller will run the unchanged sequential oracle.
        selection = default!;
        return false;
    }

    private static PromptSelection BuildExactCandidate(
        IReadOnlyList<string> includedIds,
        Func<IReadOnlySet<string>?, IReadOnlyList<ModelChatMessage>> promptFactory)
    {
        var selectedSet = includedIds.ToHashSet(StringComparer.Ordinal);
        var messages = promptFactory(selectedSet);
        return new PromptSelection(includedIds, messages, EstimateTokens(messages));
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

    private static List<DialogueMessage> EligibleMessages(
        ArenaSnapshot snapshot,
        int causalBeforeTurn,
        int? transcriptAfterTurn,
        Func<DialogueMessage, bool>? eligibility) => snapshot.Engine.Messages
        .Where(message => eligibility?.Invoke(message) ?? message.Kind is "message" or "internet" or "")
        .Where(message => message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        .Where(message => !string.IsNullOrWhiteSpace(message.Text))
        .Where(message => message.Turn < causalBeforeTurn)
        .Where(message => transcriptAfterTurn is null || message.Turn > transcriptAfterTurn.Value)
        .OrderBy(message => message.Turn)
        .ThenBy(message => message.CreatedAt)
        .ToList();

    private static bool IsDialogue(DialogueMessage message) => message.Kind is "message" or "";

    private static bool IsNarratorContextRow(DialogueMessage message) =>
        message.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase)
        || message.Kind.Equals("internet_tool", StringComparison.OrdinalIgnoreCase)
        || message.SpeakerId.Equals("internet", StringComparison.OrdinalIgnoreCase);

    private static string ContextFingerprint(IReadOnlyList<IdentifiedMessage> messages)
    {
        var canonical = string.Join(
            "\n",
            messages.Select(item => $"{item.Id}|{DialogueMessageIdentity.Fingerprint(item.Message)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record IdentifiedMessage(string Id, DialogueMessage Message);

    private sealed record PromptSelection(
        IReadOnlyList<string> IncludedIds,
        IReadOnlyList<ModelChatMessage> Messages,
        int EstimatedTokens);
}
