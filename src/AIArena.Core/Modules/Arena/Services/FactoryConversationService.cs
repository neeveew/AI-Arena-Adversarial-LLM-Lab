using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Owns the durable, public-only conversation contract used by Factory mode.
/// Conversation state is carried by transcript-message metadata so it survives
/// session persistence, forks, and Arena/Factory mode changes without a second
/// mutable transcript.
/// </summary>
public sealed class FactoryConversationService
{
    public const string ContractVersion = "public_group_v1";
    public const int MaxContextEntries = 50;

    public const string ContractMetadataKey = "factory_conversation_contract";
    public const string ConversationIdMetadataKey = "factory_conversation_id";
    public const string RootMessageIdMetadataKey = "factory_conversation_root_message_id";
    public const string IsRootMetadataKey = "factory_conversation_is_root";
    public const string ContextFingerprintMetadataKey = "factory_context_fingerprint";
    public const string ContextEntryCountMetadataKey = "factory_context_entry_count";
    public const string ContextOmittedCountMetadataKey = "factory_context_omitted_count";
    public const string RetainedMessageIdsMetadataKey = "factory_retained_message_ids";
    public const string BudgetReceiptMetadataKey = "factory_request_budget";
    public const string PromptEncodingMetadataKey = "factory_prompt_encoding";
    public const string AlternatingRunsPromptEncoding = "alternating_runs_v1";
    internal const string LegacyPerEntryPromptEncoding = "legacy_per_entry_v1";

    public const string MissingRootError =
        "Factory mode needs a public Operator turn. Send the raw prompt as Public, then run the model again.";
    public const string OrphanedRootError =
        "Factory group history cannot run because its initiating Operator turn is missing. Restore that turn or start a clean session.";
    public const string RetryContextValidationError =
        "Factory retry cannot verify the original prompt encoding and causal context. Keep the original response as evidence and run a new turn instead.";

    private const string ConversationIdPrefix = "factory-group:";
    private const string OperatorEnvelope = "[Public Operator]";
    private const string ParticipantEnvelopePrefix = "[Public participant: ";
    private static readonly string ProviderEntryBoundary = Environment.NewLine + Environment.NewLine;

    public bool HasUsableRoot(ArenaSnapshot snapshot, int? beforeTurn = null)
    {
        return Inspect(snapshot, beforeTurn).HasUsableRoot;
    }

    /// <summary>
    /// Returns a privacy-safe view of the current group without changing the
    /// snapshot. Before the first Factory request, a usable Operator candidate
    /// is reported but no conversation id is invented until Resolve is called.
    /// Its context fingerprint is target-independent and hashes only the exact
    /// retained public speaker/content sequence, never opaque lineage ids.
    /// </summary>
    public FactoryConversationInspection Inspect(ArenaSnapshot snapshot, int? beforeTurn = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return InspectCore(snapshot, beforeTurn, establishIfMissing: false);
    }

    /// <summary>
    /// Resolves or establishes the durable root and stamps every eligible public
    /// group entry. Existing orphaned contracts never silently re-anchor.
    /// </summary>
    public FactoryConversationInspection Resolve(ArenaSnapshot snapshot, int? beforeTurn = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return InspectCore(snapshot, beforeTurn, establishIfMissing: true);
    }

    public FactoryPromptContext BuildPromptContext(
        ArenaSnapshot snapshot,
        string targetAgentId,
        int? beforeTurn = null,
        string promptEncoding = AlternatingRunsPromptEncoding,
        IReadOnlyList<string>? retainedMessageIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalizedEncoding = NormalizePromptEncoding(promptEncoding);
        if (string.IsNullOrEmpty(normalizedEncoding))
        {
            return FactoryPromptContext.Failed(
                "Factory prompt encoding is unsupported.",
                promptEncoding);
        }

        var inspection = Resolve(snapshot, beforeTurn);
        if (!inspection.HasUsableRoot || inspection.RootMessage is null)
        {
            return FactoryPromptContext.Failed(inspection.Error, normalizedEncoding);
        }

        var selected = retainedMessageIds is null
            ? inspection.IncludedMessages
            : inspection.AllEligibleMessages.Where(entry => retainedMessageIds.Contains(
                DialogueMessageIdentity.Resolve(entry), StringComparer.Ordinal)).ToArray();
        if (retainedMessageIds is not null && (selected.Count != retainedMessageIds.Count
            || !selected.Any(entry => SameIdentity(entry, inspection.RootMessage))))
            return FactoryPromptContext.Failed(RetryContextValidationError, normalizedEncoding);
        var messages = new List<ModelChatMessage>(selected.Count);
        foreach (var entry in selected)
        {
            if (ReferenceEquals(entry, inspection.RootMessage)
                || SameIdentity(entry, inspection.RootMessage))
            {
                messages.Add(new ModelChatMessage("user", entry.Text));
            }
            else if (entry.SpeakerId.Equals(targetAgentId, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ModelChatMessage("assistant", entry.Text));
            }
            else if (IsOperator(entry))
            {
                messages.Add(new ModelChatMessage("user", $"{OperatorEnvelope}{Environment.NewLine}{entry.Text}"));
            }
            else
            {
                messages.Add(new ModelChatMessage(
                    "user",
                    $"{ParticipantEnvelopePrefix}{SafeSpeakerId(entry.SpeakerId)}]{Environment.NewLine}{entry.Text}"));
            }
        }

        var providerMessages = ProviderMessagesForEncoding(messages, normalizedEncoding);
        // Unlike the target-independent inspection fingerprint, this value is
        // the exact target-relative transport contract after lossless adjacent-
        // role batching. Logical retained/omitted counts remain entry based.
        return new FactoryPromptContext(
            true,
            "",
            ContractVersion,
            inspection.ConversationId,
            inspection.RootMessageId,
            normalizedEncoding,
            PromptContextFingerprint(providerMessages, normalizedEncoding),
            inspection.EligibleEntryCount,
            selected.Count,
            Math.Max(0, inspection.EligibleEntryCount - selected.Count),
            messages,
            providerMessages)
        {
            RetainedMessageIds = selected.Select(entry => DialogueMessageIdentity.Resolve(entry)).ToArray()
        };
    }

    /// <summary>
    /// Reconstructs a retry using the original output's declared encoding. An
    /// unmarked Factory output is treated as legacy per-entry only when its
    /// stored fingerprint validates those exact causal bytes.
    /// </summary>
    public FactoryPromptContext BuildRetryPromptContext(
        ArenaSnapshot snapshot,
        string targetAgentId,
        DialogueMessage original)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(original);
        var storedEncoding = MetadataString(original, PromptEncodingMetadataKey);
        var retryEncoding = string.IsNullOrWhiteSpace(storedEncoding)
            ? LegacyPerEntryPromptEncoding
            : NormalizePromptEncoding(storedEncoding);
        if (string.IsNullOrEmpty(retryEncoding))
        {
            return FactoryPromptContext.Failed(RetryContextValidationError, storedEncoding);
        }

        IReadOnlyList<string>? retainedIds = null;
        if (original.Metadata.TryGetValue(RetainedMessageIdsMetadataKey, out var retainedElement))
        {
            try { retainedIds = retainedElement.Deserialize<string[]>(); }
            catch (JsonException) { return FactoryPromptContext.Failed(RetryContextValidationError, retryEncoding); }
            if (retainedIds is null || retainedIds.Count == 0 || retainedIds.Any(string.IsNullOrWhiteSpace)
                || retainedIds.Distinct(StringComparer.Ordinal).Count() != retainedIds.Count)
                return FactoryPromptContext.Failed(RetryContextValidationError, retryEncoding);
        }
        var context = BuildPromptContext(snapshot, targetAgentId, original.Turn, retryEncoding, retainedIds);
        if (!context.Ok)
        {
            return context;
        }

        var storedFingerprint = MetadataString(original, ContextFingerprintMetadataKey);
        if (string.IsNullOrWhiteSpace(storedFingerprint)
            || !storedFingerprint.Equals(context.ContextFingerprint, StringComparison.Ordinal))
        {
            return FactoryPromptContext.Failed(RetryContextValidationError, retryEncoding);
        }

        return context;
    }

    /// <summary>Fits only whole public entries; the root and latest directions remain byte-exact.</summary>
    internal FactoryPromptContext FitToActiveContext(ArenaSnapshot snapshot, string targetAgentId,
        ModelProviderConfig config, FactoryPromptContext context, int? beforeTurn = null, bool frozen = false)
    {
        if (!context.Ok || config.RuntimeEvidence?.ContextWindow is not > 0) return context;
        var inspection = Resolve(snapshot, beforeTurn);
        if (!inspection.HasUsableRoot || inspection.RootMessage is null)
            return FactoryPromptContext.Failed(inspection.Error, context.PromptEncoding);
        var rolling = ModelHistoryPolicies.NormalizeHistoryPolicy(config.HistoryPolicy) == ModelHistoryPolicies.Rolling80;
        var selectedIds = context.RetainedMessageIds.ToList();
        var mandatoryIds = new HashSet<string>(StringComparer.Ordinal) { inspection.RootMessageId };
        foreach (var required in new[]
        {
            inspection.AllEligibleMessages.LastOrDefault(IsOperator),
            inspection.AllEligibleMessages.LastOrDefault(entry => !IsOperator(entry))
        })
            if (required is not null) mandatoryIds.Add(DialogueMessageIdentity.Resolve(required));
        if (rolling && !frozen)
        {
            selectedIds = inspection.AllEligibleMessages
                .Where(entry => selectedIds.Contains(DialogueMessageIdentity.Resolve(entry), StringComparer.Ordinal)
                    || mandatoryIds.Contains(DialogueMessageIdentity.Resolve(entry)))
                .Select(entry => DialogueMessageIdentity.Resolve(entry)).ToList();
            while (selectedIds.Count > MaxContextEntries)
            {
                var removable = selectedIds.FindIndex(id => !mandatoryIds.Contains(id));
                if (removable < 0) break;
                selectedIds.RemoveAt(removable);
            }
            context = BuildPromptContext(snapshot, targetAgentId, beforeTurn, context.PromptEncoding, selectedIds);
            var inputBudget = Math.Max(1, ArenaRequestBudget.TargetTokens(ArenaRequestBudget.ContextWindow(config))
                - Math.Max(0, config.MaxOutputTokens));
            while (ArenaHistoryBudgetService.EstimateTokens(context.ProviderMessages) > inputBudget)
            {
                var removable = selectedIds.FindIndex(id => !mandatoryIds.Contains(id));
                if (removable < 0) break;
                selectedIds.RemoveAt(removable);
                context = BuildPromptContext(snapshot, targetAgentId, beforeTurn, context.PromptEncoding, selectedIds);
            }
        }
        var fit = ArenaRequestBudget.FitUnchanged(config, context.ProviderMessages);
        var receipt = new ArenaHistoryBudgetReceipt
        {
            HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(config.HistoryPolicy),
            ConfiguredContextWindow = ArenaRequestBudget.ContextWindow(config),
            InputTokenBudget = Math.Max(0, ArenaRequestBudget.TargetTokens(ArenaRequestBudget.ContextWindow(config)) - fit.OutputTokenLimit),
            OutputTokenReserve = fit.OutputTokenLimit,
            EstimatedPromptTokens = ArenaHistoryBudgetService.EstimateTokens(context.ProviderMessages),
            EligibleEntryCount = inspection.EligibleEntryCount,
            IncludedEntryCount = context.IncludedEntryCount,
            OmittedEntryCount = context.OmittedEntryCount,
            IncludedMessageIds = context.RetainedMessageIds.ToList(),
            ContextFingerprint = context.ContextFingerprint,
            BeforeTurn = beforeTurn ?? snapshot.Engine.TurnCount + 1
        };
        return context with { Ok = fit.Ok, Error = fit.Error, BudgetReceipt = receipt, OutputTokenLimit = fit.OutputTokenLimit };
    }

    /// <summary>
    /// Produces an alternating Factory request for providers whose chat
    /// templates reject adjacent messages with the same role. Public entries
    /// keep their chronological order, exact text, attribution envelopes, and
    /// self/peer role mapping; only adjacent wire blocks with an equal role are
    /// joined with a fixed boundary. Logical entry counts and any active-context
    /// selection remain represented by <see cref="FactoryPromptContext"/>.
    /// </summary>
    internal static IReadOnlyList<ModelChatMessage> NormalizeProviderRequestMessages(
        IReadOnlyList<ModelChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count < 2)
        {
            return messages;
        }

        var normalized = new List<ModelChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (normalized.Count > 0
                && normalized[^1].Role.Equals(message.Role, StringComparison.OrdinalIgnoreCase))
            {
                var previous = normalized[^1];
                normalized[^1] = new ModelChatMessage(
                    previous.Role,
                    string.Concat(previous.Content, ProviderEntryBoundary, message.Content));
                continue;
            }

            normalized.Add(message);
        }

        return normalized;
    }

    internal static IReadOnlyList<ModelChatMessage> ProviderMessagesForEncoding(
        IReadOnlyList<ModelChatMessage> logicalMessages,
        string promptEncoding)
    {
        ArgumentNullException.ThrowIfNull(logicalMessages);
        return NormalizePromptEncoding(promptEncoding) switch
        {
            AlternatingRunsPromptEncoding => NormalizeProviderRequestMessages(logicalMessages),
            LegacyPerEntryPromptEncoding => logicalMessages.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(promptEncoding), promptEncoding, "Unsupported Factory prompt encoding.")
        };
    }

    /// <summary>
    /// Called from the shared public-Operator path after the message has been
    /// added to the snapshot. Arena-only sessions remain unanchored; an existing
    /// group continues to collect public Operator turns across mode changes.
    /// </summary>
    public void StampPublicOperator(ArenaSnapshot snapshot, DialogueMessage message)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEligibleOperator(message))
        {
            return;
        }

        var existing = Inspect(snapshot);
        if (existing.IsOrphaned)
        {
            return;
        }

        if (!existing.IsAnchored && !snapshot.Engine.FactoryMode)
        {
            return;
        }

        Resolve(snapshot);
    }

    /// <summary>Marks a successful Arena-mode interlude reply when a group already exists.</summary>
    public void StampPublicParticipant(ArenaSnapshot snapshot, DialogueMessage message)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEligibleParticipant(snapshot, message))
        {
            return;
        }

        var inspection = Inspect(snapshot);
        if (!inspection.IsAnchored || inspection.IsOrphaned)
        {
            return;
        }

        StampConversationMetadata(message, inspection.ConversationId, inspection.RootMessageId, isRoot: false);
    }

    /// <summary>Marks a Factory completion with both group and causal prompt evidence.</summary>
    public void StampPublicParticipant(DialogueMessage message, FactoryPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Ok)
        {
            return;
        }

        StampConversationMetadata(message, context.ConversationId, context.RootMessageId, isRoot: false);
        message.Metadata[PromptEncodingMetadataKey] = JsonSerializer.SerializeToElement(context.PromptEncoding);
        message.Metadata[ContextFingerprintMetadataKey] = JsonSerializer.SerializeToElement(context.ContextFingerprint);
        message.Metadata[ContextEntryCountMetadataKey] = JsonSerializer.SerializeToElement(context.IncludedEntryCount);
        message.Metadata[ContextOmittedCountMetadataKey] = JsonSerializer.SerializeToElement(context.OmittedEntryCount);
        if (context.RetainedMessageIds.Count > 0)
            message.Metadata[RetainedMessageIdsMetadataKey] = JsonSerializer.SerializeToElement(context.RetainedMessageIds);
        if (context.BudgetReceipt is not null)
            message.Metadata[BudgetReceiptMetadataKey] = JsonSerializer.SerializeToElement(context.BudgetReceipt);
    }

    public static bool HasFactoryPromptContract(DialogueMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata.TryGetValue("prompt_mode", out var promptMode)
            && promptMode.ValueKind == JsonValueKind.String
            && (promptMode.GetString() ?? "").Equals("factory", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsConversationRoot(DialogueMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata.TryGetValue(IsRootMetadataKey, out var isRoot)
            && isRoot.ValueKind is JsonValueKind.True;
    }

    private static FactoryConversationInspection InspectCore(
        ArenaSnapshot snapshot,
        int? beforeTurn,
        bool establishIfMissing)
    {
        var indexed = snapshot.Engine.Messages
            .Select((message, index) => new IndexedMessage(message, index))
            .ToArray();
        var marked = indexed
            .Where(item => HasAnyConversationMarker(item.Message))
            .ToArray();

        DialogueMessage? root;
        string rootMessageId;
        string conversationId;
        var anchored = marked.Length > 0;
        if (anchored)
        {
            var contracts = marked
                .Select(item => MetadataString(item.Message, ContractMetadataKey))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (contracts.Length != 1 || !contracts[0].Equals(ContractVersion, StringComparison.Ordinal))
            {
                return FactoryConversationInspection.Failed(
                    "Factory group history uses an unsupported or inconsistent conversation contract.",
                    isAnchored: true,
                    isOrphaned: true);
            }

            var rootIds = marked
                .Select(item => MetadataString(item.Message, RootMessageIdMetadataKey))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (rootIds.Length != 1)
            {
                return FactoryConversationInspection.Failed(OrphanedRootError, isAnchored: true, isOrphaned: true);
            }

            rootMessageId = rootIds[0];
            root = indexed
                .Select(item => item.Message)
                .FirstOrDefault(message => DialogueMessageIdentity.Resolve(message).Equals(rootMessageId, StringComparison.Ordinal));
            if (root is null || !IsEligibleOperator(root))
            {
                return FactoryConversationInspection.Failed(OrphanedRootError, isAnchored: true, isOrphaned: true);
            }

            var conversationIds = marked
                .Select(item => MetadataString(item.Message, ConversationIdMetadataKey))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (conversationIds.Length > 1)
            {
                return FactoryConversationInspection.Failed(
                    "Factory group history contains inconsistent conversation identities.",
                    isAnchored: true,
                    isOrphaned: true);
            }

            conversationId = conversationIds.SingleOrDefault() ?? "";
            if (establishIfMissing && string.IsNullOrWhiteSpace(conversationId))
            {
                conversationId = NewConversationId();
            }
        }
        else
        {
            var earliestLegacyFactoryOutput = indexed
                .Where(item => !IsOperator(item.Message))
                .Where(item => HasFactoryPromptContract(item.Message))
                .OrderBy(item => item.Message.Turn)
                .ThenBy(item => item.Message.CreatedAt)
                .ThenBy(item => item.Index)
                .FirstOrDefault();
            var candidate = indexed
                .Where(item => IsEligibleOperator(item.Message))
                .Where(item => beforeTurn is null || item.Message.Turn < beforeTurn.Value)
                .Where(item => earliestLegacyFactoryOutput is null
                    || IsStrictlyBefore(item, earliestLegacyFactoryOutput))
                .OrderByDescending(item => item.Message.Turn)
                .ThenByDescending(item => item.Message.CreatedAt)
                .ThenByDescending(item => item.Index)
                .FirstOrDefault();
            if (candidate is null)
            {
                return FactoryConversationInspection.Failed(MissingRootError);
            }

            root = candidate.Message;
            rootMessageId = DialogueMessageIdentity.Resolve(root);
            conversationId = establishIfMissing ? NewConversationId() : "";
        }

        if (beforeTurn is not null && root.Turn >= beforeTurn.Value)
        {
            return FactoryConversationInspection.Failed(
                "Factory retry cannot use a conversation root that does not precede the original response.",
                isAnchored: anchored,
                isOrphaned: anchored);
        }

        var rootIndexed = indexed.First(item => ReferenceEquals(item.Message, root));
        // Participant identity belongs to the durable arena slot, not to the
        // currently resized cast. AgentRosterService removes inactive slots
        // from Engine.Agents, but their successful public replies must remain
        // part of an already-established chronological group conversation.
        var agentIds = AgentRosterService.ParticipantIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        agentIds.UnionWith(snapshot.Engine.Agents
            .Select(agent => agent.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id)));
        var eligible = indexed
            .Where(item => IsAtOrAfter(item, rootIndexed))
            .Where(item => beforeTurn is null || item.Message.Turn < beforeTurn.Value)
            .Where(item => IsEligibleGroupMessage(item.Message, agentIds))
            .OrderBy(item => item.Message.Turn)
            .ThenBy(item => item.Message.CreatedAt)
            .ThenBy(item => item.Index)
            .Select(item => item.Message)
            .ToList();
        if (!eligible.Any(message => SameIdentity(message, root)))
        {
            return FactoryConversationInspection.Failed(OrphanedRootError, isAnchored: anchored, isOrphaned: anchored);
        }

        var retainedTail = eligible
            .Where(message => !SameIdentity(message, root))
            .TakeLast(MaxContextEntries - 1)
            .ToArray();
        var included = new List<DialogueMessage>(1 + retainedTail.Length) { root };
        included.AddRange(retainedTail);
        var omitted = Math.Max(0, eligible.Count - included.Count);

        if (establishIfMissing)
        {
            root.MessageId = rootMessageId;
            foreach (var message in eligible)
            {
                StampConversationMetadata(message, conversationId, rootMessageId, SameIdentity(message, root));
            }
        }

        var fingerprint = GroupContextFingerprint(included);
        return new FactoryConversationInspection(
            true,
            anchored || establishIfMissing,
            false,
            "",
            ContractVersion,
            conversationId,
            rootMessageId,
            root,
            eligible.Count,
            included.Count,
            omitted,
            fingerprint,
            included) { AllEligibleMessages = eligible };
    }

    private static bool IsAtOrAfter(IndexedMessage candidate, IndexedMessage root)
    {
        var turn = candidate.Message.Turn.CompareTo(root.Message.Turn);
        if (turn != 0)
        {
            return turn > 0;
        }

        var created = candidate.Message.CreatedAt.CompareTo(root.Message.CreatedAt);
        return created != 0 ? created > 0 : candidate.Index >= root.Index;
    }

    private static bool IsStrictlyBefore(IndexedMessage candidate, IndexedMessage boundary)
    {
        var turn = candidate.Message.Turn.CompareTo(boundary.Message.Turn);
        if (turn != 0)
        {
            return turn < 0;
        }

        var created = candidate.Message.CreatedAt.CompareTo(boundary.Message.CreatedAt);
        return created != 0 ? created < 0 : candidate.Index < boundary.Index;
    }

    private static bool IsEligibleGroupMessage(DialogueMessage message, HashSet<string> agentIds)
    {
        return IsEligibleOperator(message)
            || (IsPublicSuccessfulMessage(message)
                && agentIds.Contains(message.SpeakerId));
    }

    private static bool IsEligibleParticipant(ArenaSnapshot snapshot, DialogueMessage message)
    {
        return IsPublicSuccessfulMessage(message)
            && snapshot.Engine.Agents.Any(agent => agent.Id.Equals(message.SpeakerId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEligibleOperator(DialogueMessage message)
    {
        return IsPublicSuccessfulMessage(message) && IsOperator(message);
    }

    private static bool IsPublicSuccessfulMessage(DialogueMessage message)
    {
        return message.Status.Equals("ok", StringComparison.OrdinalIgnoreCase)
            && message.Kind is "message" or ""
            && !string.IsNullOrWhiteSpace(message.Text);
    }

    private static bool IsOperator(DialogueMessage message)
    {
        return message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameIdentity(DialogueMessage left, DialogueMessage right)
    {
        return DialogueMessageIdentity.Resolve(left).Equals(DialogueMessageIdentity.Resolve(right), StringComparison.Ordinal);
    }

    private static bool HasAnyConversationMarker(DialogueMessage message)
    {
        return message.Metadata.ContainsKey(ContractMetadataKey)
            || message.Metadata.ContainsKey(ConversationIdMetadataKey)
            || message.Metadata.ContainsKey(RootMessageIdMetadataKey)
            || message.Metadata.ContainsKey(IsRootMetadataKey);
    }

    private static string MetadataString(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static void StampConversationMetadata(
        DialogueMessage message,
        string conversationId,
        string rootMessageId,
        bool isRoot)
    {
        message.Metadata[ContractMetadataKey] = JsonSerializer.SerializeToElement(ContractVersion);
        message.Metadata[ConversationIdMetadataKey] = JsonSerializer.SerializeToElement(conversationId);
        message.Metadata[RootMessageIdMetadataKey] = JsonSerializer.SerializeToElement(rootMessageId);
        message.Metadata[IsRootMetadataKey] = JsonSerializer.SerializeToElement(isRoot);
    }

    private static string GroupContextFingerprint(IReadOnlyList<DialogueMessage> included)
    {
        var canonical = new StringBuilder()
            .Append(ContractVersion).Append('\n');
        foreach (var message in included)
        {
            canonical
                .Append(message.SpeakerId.Trim().ToLowerInvariant()).Append('\n')
                .Append(message.Text.Length).Append(':').Append(message.Text).Append('\n');
        }

        return Sha256(canonical);
    }

    private static string PromptContextFingerprint(
        IReadOnlyList<ModelChatMessage> messages,
        string promptEncoding)
    {
        var canonical = new StringBuilder()
            .Append(ContractVersion).Append('\n');
        if (promptEncoding.Equals(AlternatingRunsPromptEncoding, StringComparison.Ordinal))
        {
            canonical.Append(promptEncoding).Append('\n');
        }

        foreach (var message in messages)
        {
            canonical
                .Append(message.Role.Trim().ToLowerInvariant()).Append('\n')
                .Append(message.Content.Length).Append(':').Append(message.Content).Append('\n');
        }

        return Sha256(canonical);
    }

    private static string NormalizePromptEncoding(string value)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Equals(AlternatingRunsPromptEncoding, StringComparison.OrdinalIgnoreCase))
        {
            return AlternatingRunsPromptEncoding;
        }

        return normalized.Equals(LegacyPerEntryPromptEncoding, StringComparison.OrdinalIgnoreCase)
            ? LegacyPerEntryPromptEncoding
            : "";
    }

    private static string Sha256(StringBuilder canonical)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string SafeSpeakerId(string speakerId)
    {
        var normalized = new string((speakerId ?? "")
            .Where(character => !char.IsControl(character) && character is not '[' and not ']')
            .ToArray())
            .Trim()
            .ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "participant" : normalized;
    }

    private static string NewConversationId() => $"{ConversationIdPrefix}{Guid.NewGuid():N}";

    private sealed record IndexedMessage(DialogueMessage Message, int Index);
}

public sealed record FactoryConversationInspection(
    bool HasUsableRoot,
    bool IsAnchored,
    bool IsOrphaned,
    string Error,
    string ContractVersion,
    string ConversationId,
    string RootMessageId,
    DialogueMessage? RootMessage,
    int EligibleEntryCount,
    int IncludedEntryCount,
    int OmittedEntryCount,
    string ContextFingerprint,
    IReadOnlyList<DialogueMessage> IncludedMessages)
{
    internal IReadOnlyList<DialogueMessage> AllEligibleMessages { get; init; } = [];
    internal static FactoryConversationInspection Failed(
        string error,
        bool isAnchored = false,
        bool isOrphaned = false) =>
        new(
            false,
            isAnchored,
            isOrphaned,
            error,
            FactoryConversationService.ContractVersion,
            "",
            "",
            null,
            0,
            0,
            0,
            "",
            []);
}

public sealed record FactoryPromptContext(
    bool Ok,
    string Error,
    string ContractVersion,
    string ConversationId,
    string RootMessageId,
    string PromptEncoding,
    string ContextFingerprint,
    int EligibleEntryCount,
    int IncludedEntryCount,
    int OmittedEntryCount,
    IReadOnlyList<ModelChatMessage> LogicalMessages,
    IReadOnlyList<ModelChatMessage> ProviderMessages)
{
    internal IReadOnlyList<string> RetainedMessageIds { get; init; } = [];
    internal ArenaHistoryBudgetReceipt? BudgetReceipt { get; init; }
    internal int OutputTokenLimit { get; init; }
    internal static FactoryPromptContext Failed(string error, string promptEncoding = FactoryConversationService.AlternatingRunsPromptEncoding) =>
        new(false, error, FactoryConversationService.ContractVersion, "", "", promptEncoding, "", 0, 0, 0, [], []);
}
