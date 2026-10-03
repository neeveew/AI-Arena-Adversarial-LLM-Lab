using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;

internal static class LegacyMemoryConflictTests
{
    internal static void ResolvesCurrentTurnWithoutRewritingLegacyHistory()
    {
        foreach (var reverse in new[] { false, true })
        foreach (var state in new[] { "current", "expired", "unknown", "manual" })
        {
            var snapshot = CreateLegacySnapshot(reverse);
            var agent = snapshot.Engine.Agents[0];
            if (state == "expired") agent.MemoryEntries.Single(e => e.MemoryId == "memory:c").ExpiresAt = 300;
            if (state == "unknown") ReplaceSourceText(snapshot, "Unrecorded answer");
            var originals = agent.MemoryEntries.Select(e => JsonSerializer.Serialize(e)).ToArray();
            if (state == "manual")
                StructuredMemoryService.CorrectMemory(snapshot, agent, "memory:c", "Operator correction", "private", DateTimeOffset.FromUnixTimeSeconds(400));
            var selected = StructuredMemoryService.SelectForPrompt(snapshot, agent, DateTimeOffset.FromUnixTimeSeconds(500));
            var expected = state switch { "current" => "Turn 2: C", "manual" => "Operator correction", _ => "" };
            Require(selected.Count == (expected.Length == 0 ? 0 : 1)
                    && (expected.Length == 0 || selected[0].Text == expected),
                $"{reverse}/{state}: ambiguous legacy siblings leaked into current prompt memory");
            Require(agent.PrivateNotes.SequenceEqual(expected.Length == 0 ? Array.Empty<string>() : new[] { expected }),
                "The visible compatibility mirror disagreed with current prompt memory.");
            Require(agent.MemoryEntries.Take(originals.Length).Select(e => JsonSerializer.Serialize(e)).SequenceEqual(originals),
                "Resolving a current legacy conflict rewrote historical evidence.");
            Require(StructuredMemoryService.SelectForPrompt(snapshot, agent, DateTimeOffset.FromUnixTimeSeconds(500), beforeTurn: 2)
                    .All(e => e.Origin == StructuredMemoryOrigins.Manual),
                "Conflicted turn memories crossed the retry cursor.");
        }

        var fixture = CreateLegacySnapshot(false);
        var owner = fixture.Engine.Agents[0];
        owner.MemoryEntries.Add(new StructuredMemoryEntry
        {
            MemoryId = "memory:independent", Text = "An independent observation", Origin = StructuredMemoryOrigins.Turn,
            SourceMessageId = "message:source", SourceTurn = 2, CreatedAt = 200, RevisedAt = 200
        });
        var independent = StructuredMemoryService.SelectForPrompt(fixture, owner, DateTimeOffset.FromUnixTimeSeconds(500));
        Require(independent.Count == 2 && independent.Any(e => e.MemoryId == "memory:independent"),
            "Unrelated memories from the same source were mistaken for conflicting correction siblings.");

        ReplaceSourceText(fixture, "D");
        TurnRunnerService.UpdatePrivateMemory(fixture, owner, fixture.Engine.Messages[1]);
        var afterRetry = StructuredMemoryService.SelectForPrompt(fixture, owner, DateTimeOffset.UtcNow);
        Require(afterRetry.Count == 2 && afterRetry.Any(e => e.MemoryId == "memory:independent")
                && afterRetry.Any(e => e.Text == "Turn 2: D"),
            "A retry retired an unrelated same-source observation.");

        var migrated = CreateLegacySnapshot(false);
        var migratedOwner = migrated.Engine.Agents[0];
        migratedOwner.MemoryEntries.Clear();
        migratedOwner.PrivateNotes.Clear();
        migratedOwner.PrivateNotes.Add("Turn 2: A");
        ReplaceSourceText(migrated, "A");
        StructuredMemoryService.NormalizeSnapshot(migrated);
        var legacy = migratedOwner.MemoryEntries.Single();
        var legacyEvidence = JsonSerializer.Serialize(legacy);
        Require(legacy.Origin == StructuredMemoryOrigins.LegacyUnknown
                && legacy.SourceMessageId == "message:source" && legacy.SourceTurn == 2,
            "The retry fixture must begin with a migrated legacy summary whose source is resolved.");
        ReplaceSourceText(migrated, "B");
        TurnRunnerService.UpdatePrivateMemory(migrated, migratedOwner, migrated.Engine.Messages[1]);
        var afterMigratedRetry = StructuredMemoryService.SelectForPrompt(migrated, migratedOwner, DateTimeOffset.UtcNow);
        Require(afterMigratedRetry.Count == 1 && afterMigratedRetry[0].Text == "Turn 2: B"
                && afterMigratedRetry[0].SupersedesMemoryId == legacy.MemoryId
                && afterMigratedRetry[0].CorrectionOfMemoryId == legacy.MemoryId
                && JsonSerializer.Serialize(migratedOwner.MemoryEntries.Single(e => e.MemoryId == legacy.MemoryId)) == legacyEvidence,
            "Retrying a proven migrated turn summary must retire its old value and retain its original evidence.");

        var manual = CreateLegacySnapshot(false);
        var manualOwner = manual.Engine.Agents[0];
        StructuredMemoryService.CorrectMemory(manual, manualOwner, "memory:b", "Explicit operator note", "private", DateTimeOffset.FromUnixTimeSeconds(400));
        var corrected = StructuredMemoryService.SelectForPrompt(manual, manualOwner, DateTimeOffset.UtcNow);
        Require(corrected.Count == 2 && corrected.Any(e => e.Text == "Turn 2: C") && corrected.Any(e => e.Text == "Explicit operator note"),
            "Resolving automatic turn siblings discarded explicit operator-authored memory.");
        var protectedHistory = manualOwner.MemoryEntries.Where(e => e.MemoryId is "memory:a" or "memory:b")
            .Select(e => JsonSerializer.Serialize(e)).ToArray();
        manualOwner.PrivateNotes.Remove("Turn 2: C");
        StructuredMemoryService.NormalizeSnapshot(manual);
        Require(StructuredMemoryService.SelectForPrompt(manual, manualOwner, DateTimeOffset.UtcNow).Single().Text == "Explicit operator note"
                && manualOwner.MemoryEntries.Where(e => e.MemoryId is "memory:a" or "memory:b").Select(e => JsonSerializer.Serialize(e)).SequenceEqual(protectedHistory),
            "Removing a parallel note orphaned a retained manual correction's provenance.");

        var boundedMirror = CreateLegacySnapshot(false);
        var boundedOwner = boundedMirror.Engine.Agents[0];
        // Persisted retries can put a later automatic sibling after an operator
        // correction even though the retry retains its old source timestamp.
        var current = boundedOwner.MemoryEntries.Single(e => e.MemoryId == "memory:c");
        boundedOwner.MemoryEntries.Remove(current);
        boundedOwner.MemoryEntries.Add(new StructuredMemoryEntry
        {
            MemoryId = "memory:manual", Text = "An older explicit operator correction", Origin = StructuredMemoryOrigins.Manual,
            SupersedesMemoryId = "memory:b", CorrectionOfMemoryId = "memory:b", IsCorrection = true,
            CreatedAt = 400, RevisedAt = 400
        });
        for (var index = 0; index < StructuredMemoryService.LegacyMirrorLimit; index++)
            boundedOwner.MemoryEntries.Add(new StructuredMemoryEntry
            {
                MemoryId = $"memory:filler:{index}", Text = $"Independent note {index}", Origin = StructuredMemoryOrigins.Manual,
                CreatedAt = 500 + index, RevisedAt = 500 + index
            });
        boundedOwner.MemoryEntries.Add(current);
        boundedOwner.PrivateNotes.Clear();
        StructuredMemoryService.NormalizeSnapshot(boundedMirror);
        Require(boundedOwner.PrivateNotes.Count == StructuredMemoryService.LegacyMirrorLimit
                && boundedOwner.PrivateNotes.Contains(current.Text)
                && !boundedOwner.PrivateNotes.Contains("An older explicit operator correction"),
            "The bounded-mirror fixture must retain the current automatic note while the manual correction is outside the editor.");
        var retainedEvidence = boundedOwner.MemoryEntries.Where(e => e.MemoryId != current.MemoryId)
            .Select(e => JsonSerializer.Serialize(e)).ToArray();
        boundedOwner.PrivateNotes.Remove(current.Text);
        StructuredMemoryService.NormalizeSnapshot(boundedMirror);
        Require(boundedOwner.MemoryEntries.Select(e => JsonSerializer.Serialize(e)).SequenceEqual(retainedEvidence)
                && StructuredMemoryService.SelectForPrompt(boundedMirror, boundedOwner, DateTimeOffset.UtcNow)
                    .Any(e => e.MemoryId == "memory:manual"),
            "Deleting the visible automatic note erased an aged-out manual correction, its ancestry, or independent historical evidence.");

        var crossOwner = CreateLegacySnapshot(false);
        var beta = crossOwner.Engine.Agents[1];
        beta.MemoryEntries.Add(new StructuredMemoryEntry { MemoryId = "memory:b", Text = "Beta independent private fact", Origin = StructuredMemoryOrigins.Manual });
        Require(StructuredMemoryService.SelectForPrompt(crossOwner, beta, DateTimeOffset.UtcNow).Single().Text == "Beta independent private fact",
            "Another agent's private conflict suppressed an unrelated same-ID memory.");

        var edited = CreateLegacySnapshot(false);
        var editedOwner = edited.Engine.Agents[0];
        StructuredMemoryService.NormalizeSnapshot(edited);
        editedOwner.PrivateNotes.Clear();
        editedOwner.PrivateNotes.Add("A replacement manual note");
        StructuredMemoryService.NormalizeSnapshot(edited);
        Require(editedOwner.MemoryEntries.All(e => !e.Text.StartsWith("Turn 2:", StringComparison.Ordinal))
                && StructuredMemoryService.SelectForPrompt(edited, editedOwner, DateTimeOffset.UtcNow).Single().Text == "A replacement manual note",
            "Editing away the visible current note revived a hidden legacy sibling.");

        var unknownSource = CreateLegacySnapshot(false);
        foreach (var entry in unknownSource.Engine.Agents[0].MemoryEntries)
        {
            entry.SourceMessageId = null!;
            entry.BranchId = null!;
        }
        Require(StructuredMemoryService.SelectForPrompt(unknownSource, unknownSource.Engine.Agents[0], DateTimeOffset.UtcNow).Count == 0,
            "Unknown legacy source fields crashed or selected an arbitrary conflicting head.");
    }

    internal static void PersistsAndProjectsLegacyConflictsWithoutInventingHistory()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temp, "aiarena-legacy-memory-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            var original = CreateLegacySnapshot(false);
            var evidence = original.Engine.Agents[0].MemoryEntries.Select(e => JsonSerializer.Serialize(e)).ToArray();
            store.SaveSnapshotAsync(original, "source").GetAwaiter().GetResult();
            var reloaded = store.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            var agent = reloaded.Engine.Agents[0];
            Require(StructuredMemoryService.SelectForPrompt(reloaded, agent, DateTimeOffset.UtcNow).Single().Text == "Turn 2: C",
                "Persistence revived a legacy sibling.");
            Require(agent.MemoryEntries.Select(e => JsonSerializer.Serialize(e)).SequenceEqual(evidence),
                "Persistence fabricated legacy correction history.");
            foreach (var cursor in new[] { "message:before", "message:source" })
            {
                var fork = store.ForkSessionAtCursorAsync("source", cursor, "child").GetAwaiter().GetResult();
                var child = store.LoadSnapshotAsync(fork.TargetSessionId).GetAwaiter().GetResult()!;
                var selected = StructuredMemoryService.SelectForPrompt(child, child.Engine.Agents[0], DateTimeOffset.UtcNow);
                Require(cursor == "message:before" ? selected.Count == 0 : selected.Count == 1 && selected[0].Text == "Turn 2: C",
                    "Historical projection leaked a sibling or invented a missing ancestor.");
            }
            ReplaceSourceText(reloaded, "D");
            TurnRunnerService.UpdatePrivateMemory(reloaded, agent, reloaded.Engine.Messages[1]);
            Require(StructuredMemoryService.SelectForPrompt(reloaded, agent, DateTimeOffset.UtcNow).Single().Text == "Turn 2: D",
                "The next retry left an old sibling active.");
        }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(root), temp.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("aiarena-legacy-memory-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected memory fixture cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplaceSourceText(ArenaSnapshot snapshot, string text)
    {
        var message = snapshot.Engine.Messages[1];
        snapshot.Engine.Messages[1] = new DialogueMessage
        {
            MessageId = message.MessageId, Turn = message.Turn, SpeakerId = message.SpeakerId,
            Speaker = message.Speaker, Text = text, CreatedAt = message.CreatedAt,
            Status = message.Status, Kind = message.Kind, Model = message.Model,
            Metadata = message.Metadata, Extra = message.Extra
        };
    }

    private static ArenaSnapshot CreateLegacySnapshot(bool reverse)
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var agent = snapshot.Engine.Agents[0];
        snapshot.Engine.Messages.Add(new DialogueMessage { MessageId = "message:before", Turn = 1, SpeakerId = "operator", Text = "Start", CreatedAt = 100 });
        snapshot.Engine.Messages.Add(new DialogueMessage { MessageId = "message:source", Turn = 2, SpeakerId = agent.Id, Text = "C", CreatedAt = 200 });
        snapshot.Engine.Messages.Add(new DialogueMessage { MessageId = "message:after", Turn = 3, SpeakerId = "operator", Text = "Next", CreatedAt = 300 });
        foreach (var value in reverse ? new[] { "C", "A", "B" } : new[] { "A", "B", "C" })
            agent.MemoryEntries.Add(new StructuredMemoryEntry
            {
                MemoryId = "memory:" + value.ToLowerInvariant(), Text = "Turn 2: " + value,
                Origin = StructuredMemoryOrigins.Turn, Visibility = StructuredMemoryVisibilities.Private,
                SourceMessageId = "message:source", SourceTurn = 2, CreatedAt = 200, RevisedAt = 200,
                IsCorrection = value != "A", SupersedesMemoryId = value == "A" ? "" : "memory:a",
                CorrectionOfMemoryId = value == "A" ? "" : "memory:a"
            });
        agent.PrivateNotes.Add("Turn 2: C");
        return snapshot;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
