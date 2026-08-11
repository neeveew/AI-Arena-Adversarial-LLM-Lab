using System.Collections.Immutable;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;

internal static class MemoryForkTests
{
    internal static void MigratesLegacyMemoryAndStableMessageIds()
    {
        WithTempRoot(root =>
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.Messages.Add(new DialogueMessage
            {
                Turn = 1,
                Speaker = "Alpha",
                SpeakerId = "alpha",
                Text = "Keep the rollback switch.",
                Kind = "message",
                CreatedAt = 100
            });
            snapshot.Engine.Agents[0].PrivateNotes.Add("Turn 1: Keep the rollback switch.");
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var message = loaded.Engine.Messages.Single();
            Require(!string.IsNullOrWhiteSpace(message.MessageId), "legacy message did not receive a stable ID");
            Require(message.MessageId == DialogueMessageIdentity.LegacyFallback(new DialogueMessage
            {
                Turn = 1,
                Speaker = "Alpha",
                SpeakerId = "alpha",
                Text = "Keep the rollback switch.",
                Kind = "message",
                CreatedAt = 100
            }), "legacy fallback ID was not deterministic");

            var agent = loaded.Engine.Agents[0];
            Require(agent.PrivateNotes.SequenceEqual(["Turn 1: Keep the rollback switch."]), "legacy notes were deleted or rewritten");
            var memory = agent.MemoryEntries.Single();
            Require(memory.Origin == StructuredMemoryOrigins.LegacyUnknown, "legacy memory origin was invented");
            Require(memory.SourceTurn == 1 && memory.SourceMessageId == message.MessageId, "legacy turn provenance was not recovered deterministically");

            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var roundTrip = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(roundTrip.Engine.Messages[0].MessageId == message.MessageId, "stable message ID changed after persistence");
            Require(roundTrip.Engine.Agents[0].PrivateNotes.SequenceEqual(agent.PrivateNotes), "legacy note compatibility changed after persistence");

            var optionalJson = JsonSerializer.Serialize(new DialogueMessage { Turn = 9, SpeakerId = "legacy", Text = "no id yet" });
            Require(!optionalJson.Contains("message_id", StringComparison.Ordinal), "optional message ID was emitted as an empty legacy field");

            roundTrip.Engine.Agents[0].PrivateNotes.Clear();
            store.SaveSnapshotAsync(roundTrip).GetAwaiter().GetResult();
            var cleared = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(cleared.Engine.Agents[0].MemoryEntries.Count == 0,
                "legacy private-note clear was not mirrored into structured memory");
        });
    }

    internal static void SelectsOnlyActiveScopedMemoryWithProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var alpha = snapshot.Engine.Agents.Single(agent => agent.Id == "alpha");
        var beta = snapshot.Engine.Agents.Single(agent => agent.Id == "beta");
        alpha.MemoryEntries.Add(Entry("memory:alpha-private", "ALPHA_PRIVATE_MUST_NOT_LEAK", now, sourceTurn: 1));
        var shared = Entry("memory:alpha-shared", "ALPHA_SHARED_MEMORY", now, sourceTurn: 1);
        shared.Visibility = StructuredMemoryVisibilities.Shared;
        alpha.MemoryEntries.Add(shared);

        beta.MemoryEntries.Add(Entry("memory:active", "ACTIVE_BETA_MEMORY", now, sourceTurn: 2));
        beta.MemoryEntries.Add(Entry("memory:expired", "EXPIRED_BETA_MEMORY", now, expiresAt: now.AddSeconds(-1), sourceTurn: 2));
        beta.MemoryEntries.Add(Entry("memory:old", "INCORRECT_OLD_MEMORY", now, sourceTurn: 2));
        beta.MemoryEntries.Add(Entry(
            "memory:correction",
            "CORRECTED_BETA_MEMORY",
            now,
            sourceTurn: 3,
            correctionOf: "memory:old"));
        beta.MemoryEntries.Add(Entry("memory:future", "POST_CURSOR_MEMORY", now, sourceTurn: 8));
        beta.MemoryEntries.Add(Entry("memory:foreign", "FOREIGN_BRANCH_MEMORY", now, sourceTurn: 2, branchId: "branch:other"));

        var plan = new OneTurnPlan(true, "beta", "Beta", new ModelProviderConfig(), null, "");
        var prompt = string.Join("\n", TurnRunnerService.BuildPrompt(snapshot, plan, beforeTurn: 5).Select(message => message.Content));
        Require(prompt.Contains("ACTIVE_BETA_MEMORY", StringComparison.Ordinal), "active selected-agent memory was omitted");
        Require(prompt.Contains("CORRECTED_BETA_MEMORY", StringComparison.Ordinal), "correction memory was omitted");
        Require(prompt.Contains("ALPHA_SHARED_MEMORY", StringComparison.Ordinal), "shared memory was not visible to another agent");
        Require(prompt.Contains("origin=turn", StringComparison.Ordinal) && prompt.Contains("source=", StringComparison.Ordinal), "memory provenance was not sent with the selected value");
        Require(!prompt.Contains("ALPHA_PRIVATE_MUST_NOT_LEAK", StringComparison.Ordinal), "another agent's private memory leaked");
        Require(!prompt.Contains("EXPIRED_BETA_MEMORY", StringComparison.Ordinal), "expired memory leaked");
        Require(!prompt.Contains("INCORRECT_OLD_MEMORY", StringComparison.Ordinal), "superseded memory leaked");
        Require(!prompt.Contains("POST_CURSOR_MEMORY", StringComparison.Ordinal), "post-cursor memory leaked into a retry prompt");
        Require(!prompt.Contains("FOREIGN_BRANCH_MEMORY", StringComparison.Ordinal), "foreign-branch memory leaked");
        Require(!beta.PrivateNotes.Contains("INCORRECT_OLD_MEMORY") && !beta.PrivateNotes.Contains("EXPIRED_BETA_MEMORY"),
            "legacy mirror reintroduced inactive memory");
    }

    internal static void BoundsStructuredMemoryAndLegacyMirror()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var agent = snapshot.Engine.Agents[0];
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var turn = 1; turn <= StructuredMemoryService.MaximumEntriesPerAgent + 80; turn++)
        {
            var message = new DialogueMessage
            {
                MessageId = $"message:bounded-{turn}",
                Turn = turn,
                Speaker = "Alpha",
                SpeakerId = "alpha",
                Text = $"Memory {turn}",
                CreatedAt = start.AddSeconds(turn).ToUnixTimeSeconds()
            };
            snapshot.Engine.Messages.Add(message);
            StructuredMemoryService.AddTurnMemory(snapshot, agent, message, $"Turn {turn}: Memory {turn}", start.AddSeconds(turn));
        }

        Require(agent.MemoryEntries.Count == StructuredMemoryService.MaximumEntriesPerAgent, "structured memory retention was unbounded");
        Require(agent.PrivateNotes.Count == StructuredMemoryService.LegacyMirrorLimit, "legacy memory mirror was unbounded");
        Require(agent.MemoryEntries.Any(entry => entry.SourceTurn == StructuredMemoryService.MaximumEntriesPerAgent + 80), "bounded retention discarded the newest active memory");
        Require(agent.MemoryEntries.All(entry => !string.IsNullOrWhiteSpace(entry.MemoryId)), "bounded retention left unstable memory identities");

        var last = snapshot.Engine.Messages[^1];
        var repeated = StructuredMemoryService.AddTurnMemory(
            snapshot,
            agent,
            last,
            $"Turn {last.Turn}: Memory {last.Turn}",
            start.AddHours(2));
        Require(agent.MemoryEntries.Count == StructuredMemoryService.MaximumEntriesPerAgent,
            "idempotent retry grew structured memory");
        Require(StructuredMemoryService.SelectForPrompt(snapshot, agent, start.AddHours(3)).Contains(repeated),
            "idempotent retry retired its own active memory");

        var persisted = JsonSerializer.Deserialize<ArenaSnapshot>(JsonSerializer.Serialize(snapshot))!;
        StructuredMemoryService.NormalizeSnapshot(persisted);
        Require(persisted.Engine.Agents[0].MemoryEntries.Count == StructuredMemoryService.MaximumEntriesPerAgent
            && persisted.Engine.Agents[0].PrivateNotes.Count == StructuredMemoryService.LegacyMirrorLimit,
            "bounded structured memory did not survive persistence normalization");
        persisted.Engine.Agents[0].PrivateNotes.RemoveAt(0);
        StructuredMemoryService.NormalizeSnapshot(persisted);
        Require(persisted.Engine.Agents[0].MemoryEntries.Count == StructuredMemoryService.MaximumEntriesPerAgent - 1,
            "one legacy mirror edit discarded non-mirrored structured history");
    }

    internal static void ForksAtExactCursorWithoutFutureState()
    {
        WithTempRoot(root =>
        {
            var store = new SessionStore(root);
            var source = HistoricalSource();
            source.Engine.Agents[0].Persona = "CURRENT_SETUP_NOT_HISTORICALLY_PROJECTABLE";
            store.SaveSnapshotAsync(source, "source").GetAwaiter().GetResult();
            var persisted = store.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            var cursorId = persisted.Engine.Messages[0].MessageId!;

            var result = store.ForkSessionAtCursorAsync("source", cursorId, "old-cursor").GetAwaiter().GetResult();
            var child = store.LoadSnapshotAsync(result.TargetSessionId).GetAwaiter().GetResult()!;

            Require(child.Engine.Messages.Count == 1 && child.Engine.Messages[0].MessageId == cursorId, "historical fork did not stop at the exact cursor");
            Require(child.ForkLineage?.ParentMessageCount == 3 && child.ForkLineage.ParentTurnCount == 3,
                "historical lineage did not capture the authoritative parent state");
            Require(child.Engine.Messages.All(message => !message.Text.Contains("FUTURE", StringComparison.Ordinal)), "future transcript leaked");
            Require(child.Engine.Agents[0].PrivateNotes.SequenceEqual(["Turn 1: RETAINED_MEMORY"]), "future or unproven legacy notes leaked");
            Require(child.Engine.Agents[0].MemoryEntries.All(entry => entry.SourceTurn is null || entry.SourceTurn <= 1), "post-cursor structured memory leaked");
            Require(child.Engine.Agents[0].MemoryEntries.All(entry => entry.BranchId == child.BranchReceipt!.Id), "foreign branch-local memory leaked or was not isolated");
            Require(child.Engine.Narration.Count == 1 && child.Engine.Narration[0].ToTurn == 1, "future narration leaked");
            Require(child.Engine.Attachments.Count == 0, "unversioned attachment leaked into an old-cursor fork");
            Require(child.Engine.ResearchItems.Count == 0, "unversioned research leaked into an old-cursor fork");
            Require(child.Engine.DecisionCard.Text.Length == 0 && child.Engine.Summary.Length == 0, "future derived decision or summary leaked");
            Require(child.Configs.Values.All(config => config.LastError.Length == 0 && config.LastLatencyMs == 0 && !config.LastTestOk),
                "future provider runtime state leaked");
            Require(child.GenerationHistory.Count == 0, "future generated state leaked");
            Require(result.UnprojectableMemoryEntryCount > 0, "unprojectable legacy memory was not reported");
            Require(child.BranchReceipt is not null && child.BranchReceipt.Schema == ArenaContractSchemas.Branch, "branch v1 receipt was not emitted");
            Require(ArenaContractCodec.Validate(child.BranchReceipt!).IsValid, "branch v1 receipt is invalid");
            var receiptJson = JsonSerializer.Serialize(child.BranchReceipt);
            Require(!receiptJson.Contains("RETAINED_MEMORY", StringComparison.Ordinal)
                && !receiptJson.Contains("FUTURE_MEMORY", StringComparison.Ordinal)
                && !receiptJson.Contains("At cursor", StringComparison.Ordinal), "branch receipt embedded transcript or private memory text");
            Require(child.BranchReceipt!.Evidence.Any(item => item.State == ArenaEvidenceState.Unavailable), "unprojectable state was not honestly marked unavailable");
            Require(result.HistoricalSetupProjectionUnavailable
                    && child.Engine.Agents[0].Persona == "CURRENT_SETUP_NOT_HISTORICALLY_PROJECTABLE"
                    && child.BranchReceipt.Evidence.Any(item => item.Id == "evidence:historical-setup-projection-unavailable"
                        && item.State == ArenaEvidenceState.Unavailable),
                "historical fork guessed cursor-scoped setup instead of labeling current-setup retention unavailable");
        });
    }

    internal static void LegacyUnknownCursorRejectsSourceLessTimedMemory()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "message:unknown-time",
            Turn = 1,
            Speaker = "Operator",
            SpeakerId = "operator",
            Text = "Cursor with no legacy timestamp",
            CreatedAt = 0
        });
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "message:future",
            Turn = 2,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Text = "Future",
            CreatedAt = 20
        });
        snapshot.Engine.Agents[0].MemoryEntries.Add(Entry(
            "memory:timed-source-less",
            "MUST_NOT_SURVIVE_UNKNOWN_CURSOR_TIME",
            DateTimeOffset.FromUnixTimeSeconds(10)));
        snapshot.Engine.Agents[0].PrivateNotes.Add("Turn 1: LEGACY_PREFIX_IS_NOT_PROVENANCE");

        var projection = StructuredMemoryService.ProjectAtCursor(snapshot, 0, DateTimeOffset.UtcNow, "branch:child");
        Require(snapshot.Engine.Agents[0].MemoryEntries.Count == 0, "source-less timed memory leaked when cursor time was unknown");
        Require(snapshot.Engine.Agents[0].PrivateNotes.Count == 0, "unresolved legacy turn prefix leaked into a historical fork");
        Require(projection.UnprojectableEntryCount == 2, "unknown-time projection did not report every unavailable provenance value");
    }

    internal static void ForkSiblingsAreIsolatedAndCreationIsAtomic()
    {
        WithTempRoot(root =>
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(HistoricalSource(), "source").GetAwaiter().GetResult();
            var source = store.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            var cursorId = source.Engine.Messages[0].MessageId!;

            var first = store.ForkSessionAtCursorAsync("source", cursorId, "sibling").GetAwaiter().GetResult();
            var second = store.ForkSessionAtCursorAsync("source", cursorId, "sibling").GetAwaiter().GetResult();
            Require(first.TargetSessionId == "sibling" && second.TargetSessionId == "sibling-2", "fork collision did not reserve a unique target atomically");

            var firstChild = store.LoadSnapshotAsync(first.TargetSessionId).GetAwaiter().GetResult()!;
            firstChild.Engine.Agents[0].PrivateNotes.Add("FIRST_SIBLING_ONLY");
            store.SaveSnapshotAsync(firstChild, first.TargetSessionId).GetAwaiter().GetResult();
            var secondChild = store.LoadSnapshotAsync(second.TargetSessionId).GetAwaiter().GetResult()!;
            var unchangedSource = store.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            Require(!secondChild.Engine.Agents[0].PrivateNotes.Contains("FIRST_SIBLING_ONLY"), "memory leaked between sibling branches");
            Require(!unchangedSource.Engine.Agents[0].PrivateNotes.Contains("FIRST_SIBLING_ONLY"), "child memory mutated the parent");
            Require(firstChild.BranchReceipt!.Id != secondChild.BranchReceipt!.Id, "sibling branches shared one identity");

            var races = Task.WhenAll(
                new SessionStore(root).ForkSessionAtCursorAsync("source", cursorId, "race-child"),
                new SessionStore(root).ForkSessionAtCursorAsync("source", cursorId, "race-child")).GetAwaiter().GetResult();
            Require(races.Select(item => item.TargetSessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                "concurrent historical forks collided on one target identity");
            Require(races.All(item => File.Exists(store.SnapshotPath(item.TargetSessionId))),
                "concurrent historical fork left a missing or partial snapshot");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = false;
            try
            {
                _ = store.ForkSessionAtCursorAsync("source", cursorId, "cancelled-child", cancellationToken: cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            Require(canceled, "pre-cancelled fork did not cancel");
            Require(!File.Exists(store.SnapshotPath("cancelled-child")), "cancelled fork left a partial snapshot");
        });
    }

    internal static void ForksLegacyUppercaseSessionsWithoutCaseCollisions()
    {
        WithTempRoot(root =>
        {
            var store = new SessionStore(root);
            store.SaveSnapshotAsync(HistoricalSource(), "MySession").GetAwaiter().GetResult();
            var first = store.ForkSessionAsync("MySession", "ChildBranch").GetAwaiter().GetResult();
            var firstChild = store.LoadSnapshotAsync(first.TargetSessionId).GetAwaiter().GetResult()!;
            Require(firstChild.BranchReceipt is not null
                    && firstChild.BranchReceipt.ParentSessionId == "MySession"
                    && firstChild.BranchReceipt.ChildSessionId == "ChildBranch"
                    && ArenaContractCodec.Validate(firstChild.BranchReceipt).IsValid,
                "uppercase legacy session produced an invalid branch receipt");

            var second = store.ForkSessionAsync("MySession", "childbranch").GetAwaiter().GetResult();
            Require(!first.TargetSessionId.Equals(second.TargetSessionId, StringComparison.OrdinalIgnoreCase),
                "case-only fork target collision replaced or aliased the existing child");
            Require(store.LoadSnapshotAsync(first.TargetSessionId).GetAwaiter().GetResult() is not null
                    && store.LoadSnapshotAsync(second.TargetSessionId).GetAwaiter().GetResult() is not null,
                "case-collision handling lost a forked session");
        });
    }

    internal static void SetupFingerprintTracksBehaviorNotRuntimeState()
    {
        var baseline = SessionStore.CreateDefaultSnapshot();
        var original = SessionStore.SetupFingerprint(baseline);
        baseline.Engine.LastError = "runtime only";
        baseline.Engine.Agents[0].Status = "thinking";
        baseline.Engine.TurnCount = 99;
        baseline.Configs["shared"] = CopyConfig(baseline.Configs["shared"], lastLatencyMs: 999);
        Require(SessionStore.SetupFingerprint(baseline) == original, "runtime/output state changed the setup fingerprint");
        baseline.Configs["shared"] = CopyConfig(baseline.Configs["shared"], apiToken: "secret-that-must-not-affect-provenance");
        Require(SessionStore.SetupFingerprint(baseline) == original, "provider token changed the setup fingerprint");

        baseline.Engine.FactoryMode = true;
        var factoryChanged = SessionStore.SetupFingerprint(baseline);
        Require(factoryChanged != original,
            "Factory public-group prompt behavior did not change the setup fingerprint");
        var conversations = new FactoryConversationService();
        var root = new DialogueMessage
        {
            MessageId = "message:factory-root",
            Turn = 1,
            Speaker = "Operator",
            SpeakerId = "operator",
            Text = "Factory conversation root",
            Kind = "message",
            CreatedAt = 100
        };
        baseline.Engine.Messages.Add(root);
        var inspection = conversations.Resolve(baseline);
        Require(inspection.HasUsableRoot, "Factory setup-fingerprint fixture did not establish a root");
        var context = conversations.BuildPromptContext(baseline, "alpha");
        var reply = new DialogueMessage
        {
            MessageId = "message:factory-reply",
            Turn = 2,
            Speaker = "Alpha",
            SpeakerId = "alpha",
            Text = "Factory runtime output",
            Kind = "message",
            CreatedAt = 101
        };
        conversations.StampPublicParticipant(reply, context);
        baseline.Engine.Messages.Add(reply);
        Require(SessionStore.SetupFingerprint(baseline) == factoryChanged,
            "Factory runtime group messages or causal metadata changed the setup fingerprint");
        baseline.Engine.FactoryMode = false;
        Require(SessionStore.SetupFingerprint(baseline) == original,
            "Factory runtime group state remained in the setup fingerprint after Factory mode was disabled");

        baseline.Engine.Steering.Global = "Changed global behavior";
        var globalChanged = SessionStore.SetupFingerprint(baseline);
        Require(globalChanged != original, "global steering did not change the setup fingerprint");
        baseline.Engine.Steering.Global = "";
        baseline.Engine.Agents[0].Persona = "Changed persona";
        var personaChanged = SessionStore.SetupFingerprint(baseline);
        Require(personaChanged != original, "persona did not change the setup fingerprint");
        baseline.Engine.Agents[0].Persona = SessionStore.CreateDefaultSnapshot().Engine.Agents[0].Persona;
        baseline.Configs["shared"] = CopyConfig(baseline.Configs["shared"], temperature: 0.91);
        Require(SessionStore.SetupFingerprint(baseline) != original, "provider temperature did not change the setup fingerprint");
    }

    internal static void HistoricalProjectionRewindsFutureExpiryMutation()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.AddRange(
        [
            new DialogueMessage { MessageId = "message:cursor", Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "Cursor", CreatedAt = 100 },
            new DialogueMessage { MessageId = "message:future", Turn = 2, Speaker = "Operator", SpeakerId = "operator", Text = "Future", CreatedAt = 200 }
        ]);
        var alpha = snapshot.Engine.Agents[0];
        var memory = StructuredMemoryService.AddManualMemory(
            snapshot,
            alpha,
            "ACTIVE_AT_CURSOR",
            StructuredMemoryVisibilities.Private,
            DateTimeOffset.FromUnixTimeSeconds(50));
        StructuredMemoryService.ExpireMemory(snapshot, alpha, memory.MemoryId, DateTimeOffset.FromUnixTimeSeconds(150));

        var projection = StructuredMemoryService.ProjectAtCursor(
            snapshot,
            0,
            DateTimeOffset.FromUnixTimeSeconds(300),
            "branch:historical");
        var retained = alpha.MemoryEntries.Single(entry => entry.MemoryId == memory.MemoryId);
        Require(retained.ExpiresAt is null, "post-cursor expiry mutation leaked into the historical child");
        Require(retained.RevisedAt <= 100, "post-cursor memory revision leaked into the historical child");
        Require(alpha.PrivateNotes.Contains("ACTIVE_AT_CURSOR"), "rewound active memory was not restored to the legacy mirror");
        Require(projection.ExcludedEntryCount == 0, "a safely rewindable expiry was reported as excluded");
    }

    internal static void HistoricalProjectionRejectsSameSecondSourceLessMutations()
    {
        static ArenaSnapshot SnapshotAtCursor()
        {
            var value = SessionStore.CreateDefaultSnapshot();
            value.Engine.Messages.AddRange(
            [
                new DialogueMessage { MessageId = "message:cursor", Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "Cursor", CreatedAt = 100 },
                new DialogueMessage { MessageId = "message:future", Turn = 2, Speaker = "Operator", SpeakerId = "operator", Text = "Future", CreatedAt = 101 }
            ]);
            return value;
        }

        var added = SnapshotAtCursor();
        StructuredMemoryService.AddManualMemory(
            added,
            added.Engine.Agents[0],
            "SAME_SECOND_ADD_MUST_NOT_SURVIVE",
            StructuredMemoryVisibilities.Private,
            DateTimeOffset.FromUnixTimeSeconds(100));
        var addedProjection = StructuredMemoryService.ProjectAtCursor(added, 0, DateTimeOffset.FromUnixTimeSeconds(200), "branch:add");
        Require(added.Engine.Agents[0].MemoryEntries.Count == 0 && addedProjection.UnprojectableEntryCount == 1,
            "same-second source-less addition leaked into the historical child");

        var corrected = SnapshotAtCursor();
        var original = StructuredMemoryService.AddManualMemory(
            corrected,
            corrected.Engine.Agents[0],
            "ORIGINAL_AT_CURSOR",
            StructuredMemoryVisibilities.Private,
            DateTimeOffset.FromUnixTimeSeconds(90));
        StructuredMemoryService.CorrectMemory(
            corrected,
            corrected.Engine.Agents[0],
            original.MemoryId,
            "SAME_SECOND_CORRECTION_MUST_NOT_SURVIVE",
            null,
            DateTimeOffset.FromUnixTimeSeconds(100));
        var correctedProjection = StructuredMemoryService.ProjectAtCursor(corrected, 0, DateTimeOffset.FromUnixTimeSeconds(200), "branch:correct");
        Require(corrected.Engine.Agents[0].MemoryEntries.All(entry => entry.Text != "SAME_SECOND_CORRECTION_MUST_NOT_SURVIVE")
                && correctedProjection.UnprojectableEntryCount == 1,
            "same-second source-less correction leaked into the historical child");

        var expired = SnapshotAtCursor();
        var expiring = StructuredMemoryService.AddManualMemory(
            expired,
            expired.Engine.Agents[0],
            "SAME_SECOND_EXPIRY_IS_AMBIGUOUS",
            StructuredMemoryVisibilities.Private,
            DateTimeOffset.FromUnixTimeSeconds(90));
        StructuredMemoryService.ExpireMemory(expired, expired.Engine.Agents[0], expiring.MemoryId, DateTimeOffset.FromUnixTimeSeconds(100));
        var expiredProjection = StructuredMemoryService.ProjectAtCursor(expired, 0, DateTimeOffset.FromUnixTimeSeconds(200), "branch:expire");
        Require(expired.Engine.Agents[0].MemoryEntries.Count == 0 && expiredProjection.UnprojectableEntryCount == 1,
            "same-second expiry mutation was guessed into the historical child");

        var sourced = SessionStore.CreateDefaultSnapshot();
        sourced.Engine.Messages.AddRange(
        [
            new DialogueMessage { MessageId = "message:source", Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "Source", CreatedAt = 90 },
            new DialogueMessage { MessageId = "message:cursor", Turn = 2, Speaker = "Operator", SpeakerId = "operator", Text = "Cursor", CreatedAt = 100 },
            new DialogueMessage { MessageId = "message:future", Turn = 3, Speaker = "Alpha", SpeakerId = "alpha", Text = "Future", CreatedAt = 101 }
        ]);
        var sourcedMemory = Entry("memory:sourced", "SOURCED_SAME_SECOND_EXPIRY_IS_AMBIGUOUS", DateTimeOffset.FromUnixTimeSeconds(90), sourceTurn: 1);
        sourcedMemory.SourceMessageId = "message:source";
        sourced.Engine.Agents[0].MemoryEntries.Add(sourcedMemory);
        sourced.Engine.Agents[0].PrivateNotes.Add(sourcedMemory.Text);
        StructuredMemoryService.ExpireMemory(sourced, sourced.Engine.Agents[0], sourcedMemory.MemoryId, DateTimeOffset.FromUnixTimeSeconds(100));
        var sourcedProjection = StructuredMemoryService.ProjectAtCursor(sourced, 1, DateTimeOffset.FromUnixTimeSeconds(200), "branch:sourced-expire");
        Require(sourced.Engine.Agents[0].MemoryEntries.Count == 0 && sourcedProjection.UnprojectableEntryCount == 1,
            "same-second source-backed expiry mutation was guessed into the historical child");
    }

    internal static void HistoricalProjectionRejectsAmbiguousDuplicateMessageProvenance()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.AddRange(
        [
            new DialogueMessage { MessageId = "message:duplicate", Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "First", CreatedAt = 10 },
            new DialogueMessage { MessageId = "message:duplicate", Turn = 2, Speaker = "Alpha", SpeakerId = "alpha", Text = "Future", CreatedAt = 20 }
        ]);
        var futureMemory = new StructuredMemoryEntry
        {
            MemoryId = "memory:duplicate-source",
            Text = "FUTURE_DUPLICATE_SOURCE_MUST_NOT_SURVIVE",
            Origin = StructuredMemoryOrigins.Turn,
            Visibility = StructuredMemoryVisibilities.Private,
            CreatedAt = 0,
            RevisedAt = 0,
            SourceMessageId = "message:duplicate",
            SourceTurn = 2
        };
        snapshot.Engine.Agents[0].MemoryEntries.Add(futureMemory);
        snapshot.Engine.Agents[0].PrivateNotes.Add(futureMemory.Text);

        var projection = StructuredMemoryService.ProjectAtCursor(
            snapshot,
            0,
            DateTimeOffset.FromUnixTimeSeconds(30),
            "branch:duplicate-source");
        Require(snapshot.Engine.Messages.Select(item => item.MessageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "duplicate transcript IDs were not normalized");
        Require(snapshot.Engine.Agents[0].MemoryEntries.Count == 0
                && snapshot.Engine.Agents[0].PrivateNotes.Count == 0
                && projection.UnprojectableEntryCount == 1,
            "future memory was rebound to the first duplicate transcript identity");
    }

    private static ArenaSnapshot HistoricalSource()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.AddRange(
        [
            new DialogueMessage { MessageId = "message:one", Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "At cursor", Kind = "message", CreatedAt = 10 },
            new DialogueMessage { MessageId = "message:two", Turn = 2, Speaker = "Operator", SpeakerId = "operator", Text = "FUTURE transcript", Kind = "message", CreatedAt = 20 },
            new DialogueMessage { MessageId = "message:three", Turn = 3, Speaker = "Alpha", SpeakerId = "alpha", Text = "FUTURE answer", Kind = "message", CreatedAt = 30 }
        ]);
        var alpha = snapshot.Engine.Agents[0];
        alpha.PrivateNotes.Add("Turn 1: RETAINED_MEMORY");
        alpha.PrivateNotes.Add("Turn 3: FUTURE_MEMORY");
        alpha.PrivateNotes.Add("UNPROVEN_LEGACY_MEMORY");
        alpha.MemoryEntries.Add(Entry("memory:foreign-fork", "FOREIGN_BRANCH_MUST_NOT_LEAK", DateTimeOffset.FromUnixTimeSeconds(5), sourceTurn: 1, branchId: "branch:sibling"));
        snapshot.Engine.Narration.Add(new NarrationEntry { Id = 1, Text = "Retained narration", FromTurn = 1, ToTurn = 1 });
        snapshot.Engine.Narration.Add(new NarrationEntry { Id = 2, Text = "Future narration", FromTurn = 2, ToTurn = 3 });
        snapshot.Engine.Attachments.Add(new AttachmentSnapshot { Id = "attachment:future", Filename = "future.txt", Chars = 10 });
        snapshot.Engine.ResearchItems.Add(new ResearchItemSnapshot { Id = "research:future", Title = "Future research", Summary = "future" });
        snapshot.Engine.DecisionCard.Text = "Future decision";
        snapshot.Engine.DecisionCard.UpdatedAt = 30;
        snapshot.Engine.Summary = "Future summary";
        snapshot.GenerationHistory.Add(new GenerationHistoryEntry { Id = "generation:future", Kind = "generated", CreatedAt = 30 });
        snapshot.Engine.TurnCount = 3;
        snapshot.Configs["shared"] = CopyConfig(snapshot.Configs["shared"], lastLatencyMs: 321, lastError: "future runtime error", lastTestOk: true);
        return snapshot;
    }

    private static StructuredMemoryEntry Entry(
        string id,
        string text,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null,
        int? sourceTurn = null,
        string correctionOf = "",
        string branchId = "") => new()
    {
        MemoryId = id,
        Text = text,
        Origin = StructuredMemoryOrigins.Turn,
        Visibility = StructuredMemoryVisibilities.Private,
        CreatedAt = createdAt.ToUnixTimeSeconds(),
        RevisedAt = createdAt.ToUnixTimeSeconds(),
        ExpiresAt = expiresAt?.ToUnixTimeSeconds(),
        SourceMessageId = sourceTurn is null ? "" : $"message:source-{sourceTurn}",
        SourceTurn = sourceTurn,
        BranchId = branchId,
        SupersedesMemoryId = correctionOf,
        CorrectionOfMemoryId = correctionOf,
        IsCorrection = !string.IsNullOrWhiteSpace(correctionOf)
    };

    private static ModelProviderConfig CopyConfig(
        ModelProviderConfig source,
        double? temperature = null,
        int? lastLatencyMs = null,
        string? apiToken = null,
        string? lastError = null,
        bool? lastTestOk = null) => new()
    {
        BaseUrl = source.BaseUrl,
        ApiMode = source.ApiMode,
        ApiToken = apiToken ?? source.ApiToken,
        Model = source.Model,
        Timeout = source.Timeout,
        Temperature = temperature ?? source.Temperature,
        MaxOutputTokens = source.MaxOutputTokens,
        ContextLength = source.ContextLength,
        Reasoning = source.Reasoning,
        NativeStatefulChat = source.NativeStatefulChat,
        NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
        LastError = lastError ?? source.LastError,
        LastLatencyMs = lastLatencyMs ?? source.LastLatencyMs,
        LastTestOk = lastTestOk ?? source.LastTestOk
    };

    private static void WithTempRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-memory-fork-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
