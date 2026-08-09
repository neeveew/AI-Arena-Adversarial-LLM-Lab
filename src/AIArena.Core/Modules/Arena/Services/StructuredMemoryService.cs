using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Compatibility bridge between legacy private-note strings and provenance-aware
/// memory. Selection is deliberately conservative: expired, superseded, foreign-
/// branch, and post-cursor entries are never included in an outbound prompt.
/// </summary>
public static partial class StructuredMemoryService
{
    public const int MaximumEntriesPerAgent = 240;
    public const int LegacyMirrorLimit = 60;
    public const string PromptSectionHeading = "Your private memory notes:";
    public const string PromptSectionBegin = "<ai-arena-scoped-memory-v1>";
    public const string PromptSectionEnd = "</ai-arena-scoped-memory-v1>";
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;

    [GeneratedRegex(@"^Turn\s+(?<turn>\d+)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyTurnPrefixRegex();

    public static bool NormalizeSnapshot(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var ambiguousMessageIds = snapshot.Engine.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message.MessageId))
            .GroupBy(message => message.MessageId!.Trim(), IdentityComparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(IdentityComparer);
        var changed = EnsureStableMessageIds(snapshot.Engine.Messages);
        foreach (var agent in snapshot.Engine.Agents)
        {
            foreach (var entry in agent.MemoryEntries.Where(entry =>
                !string.IsNullOrWhiteSpace(entry.SourceMessageId)
                && ambiguousMessageIds.Contains(entry.SourceMessageId)))
            {
                entry.SourceMessageId = "";
                entry.SourceTurn = null;
                entry.SourceProvenanceAmbiguous = true;
                changed = true;
            }
            changed |= NormalizeAgent(snapshot, agent);
        }

        return changed;
    }

    public static bool EnsureStableMessageIds(IReadOnlyList<DialogueMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var changed = false;
        var used = new HashSet<string>(IdentityComparer);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var candidate = DialogueMessageIdentity.Resolve(message, index);
            if (!used.Add(candidate))
            {
                candidate = StableId("message", $"duplicate\n{candidate}\n{index.ToString(CultureInfo.InvariantCulture)}");
                while (!used.Add(candidate))
                {
                    candidate = StableId("message", $"duplicate\n{candidate}\n{index.ToString(CultureInfo.InvariantCulture)}");
                }
            }

            if (!string.Equals(message.MessageId, candidate, StringComparison.Ordinal))
            {
                message.MessageId = candidate;
                changed = true;
            }
        }

        return changed;
    }

    public static IReadOnlyList<StructuredMemoryEntry> SelectForPrompt(
        ArenaSnapshot snapshot,
        DialogueAgent agent,
        DateTimeOffset nowUtc,
        int? beforeTurn = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(agent);
        NormalizeSnapshot(snapshot);

        var branchId = snapshot.BranchReceipt?.Id?.Trim() ?? "";
        var scoped = snapshot.Engine.Agents
            .SelectMany(owner => owner.MemoryEntries.Select(entry => new { Owner = owner, Entry = entry }))
            .Where(item => item.Owner.Id.Equals(agent.Id, StringComparison.OrdinalIgnoreCase)
                || item.Entry.Visibility.Equals(StructuredMemoryVisibilities.Shared, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Entry)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            .Where(entry => IsBranchVisible(entry, branchId))
            .Where(entry => beforeTurn is null || entry.SourceTurn is null || entry.SourceTurn < beforeTurn.Value)
            .ToArray();

        // Corrections and superseding entries retire their target even if the new
        // value later expires. Known-wrong memory must not silently resurrect.
        var retired = scoped
            .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(IdentityComparer);
        var now = nowUtc.ToUnixTimeSeconds();

        return scoped
            .Where(entry => !retired.Contains(entry.MemoryId))
            .Where(entry => entry.ExpiresAt is null || entry.ExpiresAt.Value > now)
            .OrderBy(entry => entry.RevisedAt > 0 ? entry.RevisedAt : entry.CreatedAt)
            .ToArray();
    }

    public static string FormatPromptLine(StructuredMemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var provenance = new List<string>
        {
            $"origin={NormalizeOrigin(entry.Origin)}",
            $"visibility={NormalizeVisibility(entry.Visibility)}"
        };
        if (entry.SourceTurn is not null)
        {
            provenance.Add($"turn={entry.SourceTurn.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        if (!string.IsNullOrWhiteSpace(entry.SourceMessageId))
        {
            provenance.Add($"source={JsonSerializer.Serialize(entry.SourceMessageId.Trim())}");
        }
        if (entry.IsCorrection)
        {
            provenance.Add("correction=true");
        }

        // The value is a JSON string literal so CR/LF, quotes, and the scoped
        // section markers can never become prompt structure. This is also why
        // the source id above is encoded rather than interpolated raw.
        return $"- [{string.Join("; ", provenance)}] value={JsonSerializer.Serialize(entry.Text.Trim())}";
    }

    public static StructuredMemoryEntry AddTurnMemory(
        ArenaSnapshot snapshot,
        DialogueAgent agent,
        DialogueMessage message,
        string text,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(message);
        EnsureStableMessageIds(snapshot.Engine.Messages);

        var sourceId = DialogueMessageIdentity.Resolve(message);
        var prior = agent.MemoryEntries
            .Where(entry => IdentityComparer.Equals(entry.SourceMessageId, sourceId)
                || (entry.Origin == StructuredMemoryOrigins.Turn && entry.SourceTurn == message.Turn))
            .OrderByDescending(entry => entry.RevisedAt)
            .FirstOrDefault();
        var createdAt = nowUtc.ToUnixTimeSeconds();
        if (prior is not null && prior.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            prior.RevisedAt = createdAt;
            prior.SourceMessageId = sourceId;
            prior.SourceTurn = message.Turn;
            prior.BranchId = snapshot.BranchReceipt?.Id ?? "";
            MirrorTurnNote(agent, message.Turn, prior.Text);
            PruneAgentMemory(agent);
            agent.PrivateNotesMirrorFingerprint = LegacyMirrorFingerprint(agent.PrivateNotes);
            return prior;
        }

        var id = StableId("memory", $"{agent.Id}\n{sourceId}\n{text.Trim()}");
        var entry = new StructuredMemoryEntry
        {
            MemoryId = id,
            Text = text.Trim(),
            Origin = StructuredMemoryOrigins.Turn,
            Visibility = StructuredMemoryVisibilities.Private,
            CreatedAt = createdAt,
            RevisedAt = createdAt,
            SourceMessageId = sourceId,
            SourceTurn = message.Turn,
            BranchId = snapshot.BranchReceipt?.Id ?? "",
            SupersedesMemoryId = prior?.MemoryId ?? "",
            CorrectionOfMemoryId = prior?.MemoryId ?? "",
            IsCorrection = prior is not null
        };

        agent.MemoryEntries.RemoveAll(existing => IdentityComparer.Equals(existing.MemoryId, entry.MemoryId));
        agent.MemoryEntries.Add(entry);
        MirrorTurnNote(agent, message.Turn, entry.Text);
        PruneAgentMemory(agent);
        agent.PrivateNotesMirrorFingerprint = LegacyMirrorFingerprint(agent.PrivateNotes);
        return entry;
    }

    /// <summary>
    /// Adds an operator-authored memory without inventing transcript provenance.
    /// Manual entries are scoped to the current branch and flow through the same
    /// bounded compatibility mirror as turn-created memory.
    /// </summary>
    public static StructuredMemoryEntry AddManualMemory(
        ArenaSnapshot snapshot,
        DialogueAgent agent,
        string text,
        string visibility,
        DateTimeOffset nowUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(agent);
        EnsureAgentBelongsToSnapshot(snapshot, agent);

        var normalizedText = NormalizeOperatorText(text);
        var normalizedVisibility = NormalizeOperatorVisibility(visibility);
        var timestamp = nowUtc.ToUnixTimeSeconds();
        var expiresAt = NormalizeExpiry(expiresAtUtc, nowUtc);
        var sequence = agent.MemoryEntries.Count;
        var entry = new StructuredMemoryEntry
        {
            MemoryId = StableId(
                "memory",
                $"manual\n{agent.Id}\n{timestamp.ToString(CultureInfo.InvariantCulture)}\n{sequence.ToString(CultureInfo.InvariantCulture)}\n{normalizedText}"),
            Text = normalizedText,
            Origin = StructuredMemoryOrigins.Manual,
            Visibility = normalizedVisibility,
            CreatedAt = timestamp,
            RevisedAt = timestamp,
            ExpiresAt = expiresAt,
            BranchId = snapshot.BranchReceipt?.Id?.Trim() ?? ""
        };

        // A fixed test clock or double-click can produce the same deterministic
        // identity. Preserve idempotence instead of creating duplicate facts.
        var existing = agent.MemoryEntries.FirstOrDefault(candidate =>
            IdentityComparer.Equals(candidate.MemoryId, entry.MemoryId));
        if (existing is not null)
        {
            return existing;
        }

        agent.MemoryEntries.Add(entry);
        SynchronizeOperatorMutation(snapshot, agent);
        return entry;
    }

    /// <summary>
    /// Replaces a memory with a provenance-bearing correction. The target stays
    /// retained as retired evidence and can never silently become active again.
    /// </summary>
    public static StructuredMemoryEntry CorrectMemory(
        ArenaSnapshot snapshot,
        DialogueAgent agent,
        string targetMemoryId,
        string correctedText,
        string? visibility,
        DateTimeOffset nowUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(agent);
        EnsureAgentBelongsToSnapshot(snapshot, agent);
        NormalizeSnapshot(snapshot);

        var targetId = (targetMemoryId ?? "").Trim();
        var target = agent.MemoryEntries.FirstOrDefault(entry =>
            IdentityComparer.Equals(entry.MemoryId, targetId))
            ?? throw new InvalidOperationException("The selected memory no longer exists.");
        var retired = agent.MemoryEntries
            .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(IdentityComparer);
        if (retired.Contains(target.MemoryId))
        {
            throw new InvalidOperationException("A superseded memory cannot be corrected again; select its current correction.");
        }

        var normalizedText = NormalizeOperatorText(correctedText);
        var normalizedVisibility = string.IsNullOrWhiteSpace(visibility)
            ? NormalizeOperatorVisibility(target.Visibility)
            : NormalizeOperatorVisibility(visibility);
        var timestamp = nowUtc.ToUnixTimeSeconds();
        var sequence = agent.MemoryEntries.Count;
        var correction = new StructuredMemoryEntry
        {
            MemoryId = StableId(
                "memory",
                $"correction\n{agent.Id}\n{target.MemoryId}\n{timestamp.ToString(CultureInfo.InvariantCulture)}\n{sequence.ToString(CultureInfo.InvariantCulture)}\n{normalizedText}"),
            Text = normalizedText,
            Origin = StructuredMemoryOrigins.Manual,
            Visibility = normalizedVisibility,
            CreatedAt = timestamp,
            RevisedAt = timestamp,
            ExpiresAt = NormalizeExpiry(expiresAtUtc, nowUtc),
            BranchId = snapshot.BranchReceipt?.Id?.Trim() ?? "",
            SupersedesMemoryId = target.MemoryId,
            CorrectionOfMemoryId = target.MemoryId,
            IsCorrection = true
        };

        agent.MemoryEntries.Add(correction);
        SynchronizeOperatorMutation(snapshot, agent);
        return correction;
    }

    /// <summary>
    /// Expires a current memory in place. This is explicit lifecycle evidence;
    /// it does not erase the record or its provenance chain.
    /// </summary>
    public static StructuredMemoryEntry ExpireMemory(
        ArenaSnapshot snapshot,
        DialogueAgent agent,
        string memoryId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(agent);
        EnsureAgentBelongsToSnapshot(snapshot, agent);
        NormalizeSnapshot(snapshot);

        var id = (memoryId ?? "").Trim();
        var entry = agent.MemoryEntries.FirstOrDefault(candidate =>
            IdentityComparer.Equals(candidate.MemoryId, id))
            ?? throw new InvalidOperationException("The selected memory no longer exists.");
        var retired = agent.MemoryEntries
            .SelectMany(candidate => new[] { candidate.SupersedesMemoryId, candidate.CorrectionOfMemoryId })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(IdentityComparer);
        if (retired.Contains(entry.MemoryId))
        {
            throw new InvalidOperationException("The selected memory is already superseded.");
        }

        var timestamp = nowUtc.ToUnixTimeSeconds();
        entry.ExpiresAt = timestamp;
        entry.RevisedAt = Math.Max(entry.RevisedAt, timestamp);
        SynchronizeOperatorMutation(snapshot, agent);
        return entry;
    }

    private static void EnsureAgentBelongsToSnapshot(ArenaSnapshot snapshot, DialogueAgent agent)
    {
        if (!snapshot.Engine.Agents.Any(candidate => ReferenceEquals(candidate, agent)))
        {
            throw new InvalidOperationException("The selected agent does not belong to this session.");
        }
    }

    private static string NormalizeOperatorText(string? text)
    {
        var normalized = (text ?? "").Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Memory text is required.", nameof(text));
        }

        if (normalized.Length > 4000)
        {
            throw new ArgumentException("Memory text must be 4,000 characters or fewer.", nameof(text));
        }

        return normalized;
    }

    private static string NormalizeOperatorVisibility(string? visibility)
    {
        var normalized = (visibility ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            StructuredMemoryVisibilities.Private => StructuredMemoryVisibilities.Private,
            StructuredMemoryVisibilities.Shared => StructuredMemoryVisibilities.Shared,
            _ => throw new ArgumentException("Manual memory visibility must be private or shared.", nameof(visibility))
        };
    }

    private static double? NormalizeExpiry(DateTimeOffset? expiresAtUtc, DateTimeOffset nowUtc)
    {
        if (expiresAtUtc is null)
        {
            return null;
        }

        if (expiresAtUtc.Value <= nowUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Expiry must be later than the creation time.");
        }

        return expiresAtUtc.Value.ToUnixTimeSeconds();
    }

    private static void SynchronizeOperatorMutation(ArenaSnapshot snapshot, DialogueAgent agent)
    {
        NormalizeSnapshot(snapshot);
        PruneAgentMemory(agent);
        agent.PrivateNotesMirrorFingerprint = LegacyMirrorFingerprint(agent.PrivateNotes);
    }

    public static void PruneAgentMemory(DialogueAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.MemoryEntries.Count > MaximumEntriesPerAgent)
        {
            var retired = agent.MemoryEntries
                .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(IdentityComparer);
            var newest = agent.MemoryEntries
                .OrderByDescending(entry => entry.RevisedAt > 0 ? entry.RevisedAt : entry.CreatedAt)
                .ThenByDescending(entry => entry.MemoryId, StringComparer.Ordinal)
                .ToArray();
            var keep = new HashSet<string>(IdentityComparer);

            // Current values are most important. Correction targets are retained
            // next when space permits so provenance remains inspectable.
            foreach (var entry in newest.Where(entry => !retired.Contains(entry.MemoryId)))
            {
                if (keep.Count >= MaximumEntriesPerAgent) break;
                keep.Add(entry.MemoryId);
            }
            foreach (var targetId in newest
                .Where(entry => entry.IsCorrection)
                .SelectMany(entry => new[] { entry.CorrectionOfMemoryId, entry.SupersedesMemoryId })
                .Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                if (keep.Count >= MaximumEntriesPerAgent) break;
                keep.Add(targetId);
            }
            foreach (var entry in newest)
            {
                if (keep.Count >= MaximumEntriesPerAgent) break;
                keep.Add(entry.MemoryId);
            }

            var removedTexts = agent.MemoryEntries
                .Where(entry => !keep.Contains(entry.MemoryId))
                .Select(entry => entry.Text)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            agent.MemoryEntries.RemoveAll(entry => !keep.Contains(entry.MemoryId));
            var retainedTexts = agent.MemoryEntries.Select(entry => entry.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            agent.PrivateNotes.RemoveAll(note => removedTexts.Contains(note) && !retainedTexts.Contains(note));
        }

        if (agent.PrivateNotes.Count > LegacyMirrorLimit)
        {
            agent.PrivateNotes.RemoveRange(0, agent.PrivateNotes.Count - LegacyMirrorLimit);
        }
    }

    public static MemoryProjectionResult ProjectAtCursor(
        ArenaSnapshot snapshot,
        int cursorMessageIndex,
        DateTimeOffset forkedAtUtc,
        string childBranchId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        NormalizeSnapshot(snapshot);
        if (cursorMessageIndex < 0 || cursorMessageIndex >= snapshot.Engine.Messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorMessageIndex));
        }

        var cursor = snapshot.Engine.Messages[cursorMessageIndex];
        var sourceBranchId = snapshot.BranchReceipt?.Id ?? "";
        var retainedIds = snapshot.Engine.Messages
            .Take(cursorMessageIndex + 1)
            .Select(DialogueMessageIdentity.Resolve)
            .ToHashSet(IdentityComparer);
        var isCurrentCursor = cursorMessageIndex == snapshot.Engine.Messages.Count - 1;
        var excluded = 0;
        var unprojectable = 0;

        foreach (var agent in snapshot.Engine.Agents)
        {
            var removedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in agent.MemoryEntries.ToArray())
            {
                var belongsToForeignBranch = !string.IsNullOrWhiteSpace(entry.BranchId)
                    && !entry.BranchId.Equals(sourceBranchId, StringComparison.OrdinalIgnoreCase);
                var sourceLess = entry.SourceTurn is null && string.IsNullOrWhiteSpace(entry.SourceMessageId);
                var sourceAfterCursor = entry.SourceTurn is not null && entry.SourceTurn.Value > cursor.Turn;
                if (!sourceAfterCursor && !string.IsNullOrWhiteSpace(entry.SourceMessageId))
                {
                    sourceAfterCursor = !retainedIds.Contains(entry.SourceMessageId);
                }

                var createdAfterCursor = cursor.CreatedAt > 0 && entry.CreatedAt > cursor.CreatedAt;
                var postCursorExpiryMutation = !isCurrentCursor
                    && cursor.CreatedAt > 0
                    && entry.ExpiresAt is not null
                    && entry.ExpiresAt.Value > cursor.CreatedAt
                    && entry.RevisedAt == entry.ExpiresAt.Value;
                var ambiguousExpiryAtCursor = !isCurrentCursor
                    && cursor.CreatedAt > 0
                    && entry.ExpiresAt == cursor.CreatedAt
                    && entry.RevisedAt == entry.ExpiresAt.Value
                    && entry.CreatedAt < cursor.CreatedAt;
                var ambiguousAtCursor = !isCurrentCursor
                    && cursor.CreatedAt > 0
                    && !postCursorExpiryMutation
                    && (ambiguousExpiryAtCursor
                        || sourceLess
                            && (entry.CreatedAt == cursor.CreatedAt
                                || entry.RevisedAt >= cursor.CreatedAt && entry.RevisedAt > entry.CreatedAt));
                var lacksHistoricalProvenance = !isCurrentCursor
                    && sourceLess
                    && (entry.CreatedAt <= 0 || cursor.CreatedAt <= 0);
                var hasAmbiguousSourceProvenance = !isCurrentCursor && entry.SourceProvenanceAmbiguous;
                if (belongsToForeignBranch || sourceAfterCursor || createdAfterCursor || lacksHistoricalProvenance || ambiguousAtCursor || hasAmbiguousSourceProvenance)
                {
                    agent.MemoryEntries.Remove(entry);
                    removedTexts.Add(entry.Text);
                    excluded++;
                    if (lacksHistoricalProvenance || ambiguousAtCursor || hasAmbiguousSourceProvenance)
                    {
                        unprojectable++;
                    }
                    continue;
                }

                // ExpireMemory records an explicit retirement by setting both
                // fields to the mutation timestamp. If that mutation happened
                // after the chosen cursor, rewind it rather than importing a
                // future lifecycle decision into the historical child. A
                // creation-time scheduled expiry has ExpiresAt > RevisedAt and
                // remains intact.
                var expiryWasAppliedAfterCursor = postCursorExpiryMutation;
                if (expiryWasAppliedAfterCursor)
                {
                    entry.ExpiresAt = null;
                    entry.RevisedAt = entry.CreatedAt > 0 && entry.CreatedAt <= cursor.CreatedAt
                        ? entry.CreatedAt
                        : cursor.CreatedAt;
                    if (!string.IsNullOrWhiteSpace(entry.Text)
                        && !agent.PrivateNotes.Any(note => note.Equals(entry.Text, StringComparison.OrdinalIgnoreCase)))
                    {
                        agent.PrivateNotes.Add(entry.Text);
                    }
                }

                // The retained value is now an explicit, isolated child-branch copy.
                entry.BranchId = childBranchId;
                if (entry.RevisedAt <= 0)
                {
                    entry.RevisedAt = forkedAtUtc.ToUnixTimeSeconds();
                }
            }

            if (removedTexts.Count > 0)
            {
                agent.PrivateNotes.RemoveAll(note => removedTexts.Contains(note));
            }

            // Unknown legacy strings not represented by a retained entry cannot be
            // proven to have existed at an old cursor, so omit them from the child.
            if (!isCurrentCursor)
            {
                var retainedTexts = agent.MemoryEntries.Select(entry => entry.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var before = agent.PrivateNotes.Count;
                agent.PrivateNotes.RemoveAll(note => !retainedTexts.Contains(note));
                var additionallyRemoved = before - agent.PrivateNotes.Count;
                excluded += additionallyRemoved;
                unprojectable += additionallyRemoved;
            }
        }

        return new MemoryProjectionResult(excluded, unprojectable);
    }

    private static bool NormalizeAgent(ArenaSnapshot snapshot, DialogueAgent agent)
    {
        var changed = false;
        var incomingMirrorFingerprint = LegacyMirrorFingerprint(agent.PrivateNotes);
        if (!string.IsNullOrWhiteSpace(agent.PrivateNotesMirrorFingerprint)
            && !agent.PrivateNotesMirrorFingerprint.Equals(incomingMirrorFingerprint, StringComparison.Ordinal))
        {
            changed |= ReconcileLegacyMirrorEdits(agent);
        }

        var used = new HashSet<string>(IdentityComparer);
        for (var index = 0; index < agent.MemoryEntries.Count; index++)
        {
            var entry = agent.MemoryEntries[index];
            var origin = NormalizeOrigin(entry.Origin);
            var visibility = NormalizeVisibility(entry.Visibility);
            var id = string.IsNullOrWhiteSpace(entry.MemoryId)
                ? StableId("memory", $"{agent.Id}\n{entry.Text}\n{origin}\n{entry.SourceMessageId}\n{index}")
                : entry.MemoryId.Trim();
            if (!used.Add(id))
            {
                id = StableId("memory", $"duplicate\n{id}\n{index}");
                used.Add(id);
            }

            if (entry.MemoryId != id || entry.Origin != origin || entry.Visibility != visibility)
            {
                entry.MemoryId = id;
                entry.Origin = origin;
                entry.Visibility = visibility;
                changed = true;
            }
        }

        for (var index = 0; index < agent.PrivateNotes.Count; index++)
        {
            var note = agent.PrivateNotes[index]?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(note)
                || agent.MemoryEntries.Any(entry => entry.Text.Equals(note, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var parsedSourceTurn = ParseLegacyTurn(note);
            var sourceMessage = parsedSourceTurn is null
                ? null
                : snapshot.Engine.Messages.FirstOrDefault(message =>
                    message.Turn == parsedSourceTurn.Value
                    && message.SpeakerId.Equals(agent.Id, StringComparison.OrdinalIgnoreCase));
            var sourceId = sourceMessage is null ? "" : DialogueMessageIdentity.Resolve(sourceMessage);
            var createdAt = sourceMessage?.CreatedAt ?? 0;
            agent.MemoryEntries.Add(new StructuredMemoryEntry
            {
                MemoryId = StableId("memory", $"legacy\n{agent.Id}\n{index}\n{note}"),
                Text = note,
                Origin = StructuredMemoryOrigins.LegacyUnknown,
                Visibility = StructuredMemoryVisibilities.Private,
                CreatedAt = createdAt,
                RevisedAt = createdAt,
                SourceMessageId = sourceId,
                // A free-form "Turn N:" prefix is only a hint. Treat the turn as
                // historical provenance after it resolves to the owning agent's
                // retained message; otherwise an edited future note could claim an
                // earlier turn and leak into a historical fork.
                SourceTurn = sourceMessage?.Turn
            });
            changed = true;
        }

        var retired = agent.MemoryEntries
            .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(IdentityComparer);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var activeEntries = agent.MemoryEntries
            .Where(entry => !retired.Contains(entry.MemoryId))
            .Where(entry => entry.ExpiresAt is null || entry.ExpiresAt.Value > now)
            .ToArray();
        var activeTexts = activeEntries.Select(entry => entry.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inactiveTexts = agent.MemoryEntries
            .Where(entry => retired.Contains(entry.MemoryId) || entry.ExpiresAt is not null && entry.ExpiresAt.Value <= now)
            .Select(entry => entry.Text)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inactiveTexts.Count > 0)
        {
            var before = agent.PrivateNotes.Count;
            agent.PrivateNotes.RemoveAll(note => inactiveTexts.Contains(note) && !activeTexts.Contains(note));
            changed |= before != agent.PrivateNotes.Count;
        }

        foreach (var entry in activeEntries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Text)
                && !agent.PrivateNotes.Any(note => note.Equals(entry.Text, StringComparison.OrdinalIgnoreCase)))
            {
                agent.PrivateNotes.Add(entry.Text.Trim());
                changed = true;
            }
        }

        var memoryCount = agent.MemoryEntries.Count;
        var noteCount = agent.PrivateNotes.Count;
        PruneAgentMemory(agent);
        changed |= memoryCount != agent.MemoryEntries.Count || noteCount != agent.PrivateNotes.Count;

        var mirrorFingerprint = LegacyMirrorFingerprint(agent.PrivateNotes);
        if (!string.Equals(agent.PrivateNotesMirrorFingerprint, mirrorFingerprint, StringComparison.Ordinal))
        {
            agent.PrivateNotesMirrorFingerprint = mirrorFingerprint;
            changed = true;
        }

        return changed;
    }

    private static bool ReconcileLegacyMirrorEdits(DialogueAgent agent)
    {
        if (agent.MemoryEntries.Count == 0)
        {
            return false;
        }
        if (agent.PrivateNotes.Count == 0)
        {
            agent.MemoryEntries.Clear();
            return true;
        }

        var mirrorTexts = agent.PrivateNotes
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = agent.MemoryEntries
            .SelectMany(entry => new[] { entry.SupersedesMemoryId, entry.CorrectionOfMemoryId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(IdentityComparer);
        var expectedMirrorEntries = agent.MemoryEntries
            .Where(entry => !retired.Contains(entry.MemoryId))
            .TakeLast(LegacyMirrorLimit);
        var removeIds = expectedMirrorEntries
            .Where(entry => !mirrorTexts.Contains(entry.Text))
            .Select(entry => entry.MemoryId)
            .ToHashSet(IdentityComparer);
        if (removeIds.Count == 0)
        {
            return false;
        }

        // Removing an active correction also removes its private provenance chain;
        // otherwise an old corrected value could become active again.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var entry in agent.MemoryEntries.Where(entry => removeIds.Contains(entry.MemoryId)).ToArray())
            {
                changed |= !string.IsNullOrWhiteSpace(entry.SupersedesMemoryId) && removeIds.Add(entry.SupersedesMemoryId);
                changed |= !string.IsNullOrWhiteSpace(entry.CorrectionOfMemoryId) && removeIds.Add(entry.CorrectionOfMemoryId);
            }
        }

        agent.MemoryEntries.RemoveAll(entry => removeIds.Contains(entry.MemoryId));
        return true;
    }

    private static string LegacyMirrorFingerprint(IEnumerable<string> notes)
    {
        var canonical = string.Join("\n", notes.Select(note => note?.Trim() ?? ""));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static int? ParseLegacyTurn(string note)
    {
        var match = LegacyTurnPrefixRegex().Match(note);
        return match.Success && int.TryParse(match.Groups["turn"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var turn)
            ? turn
            : null;
    }

    private static bool IsBranchVisible(StructuredMemoryEntry entry, string branchId)
    {
        return string.IsNullOrWhiteSpace(entry.BranchId)
            || (!string.IsNullOrWhiteSpace(branchId) && entry.BranchId.Equals(branchId, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeOrigin(string? origin)
    {
        var value = (origin ?? "").Trim().ToLowerInvariant();
        return StructuredMemoryOrigins.IsKnown(value) ? value : StructuredMemoryOrigins.LegacyUnknown;
    }

    private static string NormalizeVisibility(string? visibility)
    {
        var value = (visibility ?? "").Trim().ToLowerInvariant();
        return StructuredMemoryVisibilities.IsKnown(value) ? value : StructuredMemoryVisibilities.Private;
    }

    private static void MirrorTurnNote(DialogueAgent agent, int turn, string note)
    {
        agent.PrivateNotes.RemoveAll(existing =>
            existing.StartsWith($"Turn {turn}:", StringComparison.OrdinalIgnoreCase)
            || existing.Equals(note, StringComparison.OrdinalIgnoreCase));
        agent.PrivateNotes.Add(note);
    }

    internal static string StableId(string prefix, string value)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return $"{prefix}:{digest[..24]}";
    }
}

public sealed record MemoryProjectionResult(int ExcludedEntryCount, int UnprojectableEntryCount);
