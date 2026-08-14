using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ContextWindowTests
{
    public static void RollingBudgetPerformanceReceipt()
    {
        // This executable receipt deliberately uses a small, deterministic
        // prompt factory so call count is stable while allocation/time remain
        // useful before/after diagnostics rather than pass/fail thresholds.
        var baselines = new Dictionary<int, (int Calls, long AllocatedBytes, double ElapsedMs)>
        {
            [60] = (33, 299_368, 0.757),
            [500] = (263, 15_910_336, 16.295),
            [5_000] = (2_614, 1_514_873_416, 1_317.488)
        };
        _ = MeasureRollingBudget(
            16,
            ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
        foreach (var entryCount in baselines.Keys)
        {
            var baseline = baselines[entryCount];
            Console.WriteLine(
                $"BASELINE_RECEIPT rolling80 entries={entryCount} calls={baseline.Calls} " +
                $"allocated_bytes={baseline.AllocatedBytes} elapsed_ms={baseline.ElapsedMs:F3}");
            var receipt = MeasureRollingBudget(
                entryCount,
                ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT rolling80 entries={entryCount} calls={receipt.Calls} " +
                $"allocated_bytes={receipt.AllocatedBytes} elapsed_ms={receipt.Elapsed.TotalMilliseconds:F3}");
            Require(receipt.Result.Receipt is { OmittedEntryCount: > 0 },
                $"the {entryCount}-entry performance fixture must exercise rolling omission");
            Require(receipt.Calls <= 20 && receipt.Calls * 2 < baseline.Calls,
                $"the {entryCount}-entry optimized selector did not materially reduce exact prompt builds");
        }
    }

    public static void RollingBudgetOptimizedMatchesLegacyOracle()
    {
        var random = new Random(0x5A17);
        for (var scenario = 0; scenario < 160; scenario++)
        {
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.Messages.Clear();
            var entryCount = random.Next(0, 90);
            for (var index = 0; index < entryCount; index++)
            {
                var kind = index % 9 == 0 ? "internet" : index % 13 == 0 ? "" : "message";
                var status = index % 17 == 0 ? "error" : "ok";
                var text = index % 19 == 0
                    ? " "
                    : $"FUZZ_{scenario:D3}_{index:D3}_{new string((char)('a' + index % 20), random.Next(8, 180))}{(index % 7 == 0 ? " 🧪" : "")}";
                snapshot.Engine.Messages.Add(new DialogueMessage
                {
                    MessageId = index % 11 == 0 ? null : index % 7 == 0 ? "duplicate-id" : $"message-{scenario}-{index}",
                    Turn = index / 2 + 1,
                    SpeakerId = index % 10 == 4 ? "operator" : index % 3 == 0 ? "gamma" : "beta",
                    Speaker = index % 10 == 4 ? "Operator" : index % 3 == 0 ? "Gamma" : "Beta",
                    Text = text,
                    Status = status,
                    Kind = kind,
                    CreatedAt = index % 4
                });
            }

            snapshot.Engine.TurnCount = Math.Max(1, entryCount + 2);
            int? beforeTurn = scenario % 4 == 0 ? Math.Max(1, entryCount / 3) : null;
            int? afterTurn = scenario % 6 == 0 ? entryCount / 12 : null;
            var config = Config(
                $"fuzz-{scenario}",
                context: random.Next(512, 3_000),
                historyPolicy: ModelHistoryPolicies.Rolling80,
                maxOutputTokens: random.Next(0, 180));
            var eligible = LegacyEligibleIdentities(snapshot, beforeTurn, afterTurn, eligibility: null);

            IReadOnlyList<ModelChatMessage> Factory(IReadOnlySet<string>? includedIds)
            {
                var retained = includedIds is null
                    ? eligible
                    : eligible.Where(item => includedIds.Contains(item.Id)).ToList();
                return
                [
                    new ModelChatMessage("system", $"stable-system-{scenario}"),
                    new ModelChatMessage(
                        "user",
                        "stable-root\n" + string.Join('\n', retained.Select(item => $"{item.Id}|{item.Message.Text}")))
                ];
            }

            var legacy = LegacyArenaHistoryBuild(snapshot, config, beforeTurn, afterTurn, Factory);
            var optimized = ArenaHistoryBudgetService.Build(
                snapshot,
                config,
                beforeTurn,
                afterTurn,
                Factory,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
            RequireBudgetEquivalent(legacy, optimized, $"fuzz scenario {scenario}");
        }

        var oversized = SessionStore.CreateDefaultSnapshot();
        oversized.Engine.Messages.Clear();
        oversized.Engine.Messages.Add(Message(1, "gamma", "Gamma", new string('r', 4_000)));
        oversized.Engine.Messages.Add(Message(2, "operator", "Operator", new string('o', 8_000)));
        oversized.Engine.Messages.Add(Message(3, "beta", "Beta", new string('d', 8_000)));
        oversized.Engine.TurnCount = 3;
        var tiny = Config("oversized-oracle", 512, ModelHistoryPolicies.Rolling80, 64);
        var oversizedEligible = LegacyEligibleIdentities(oversized, null, null, null);
        IReadOnlyList<ModelChatMessage> OversizedFactory(IReadOnlySet<string>? ids) =>
        [
            new ModelChatMessage(
                "user",
                string.Join('\n', oversizedEligible.Where(item => ids is null || ids.Contains(item.Id)).Select(item => item.Message.Text)))
        ];
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(oversized, tiny, null, null, OversizedFactory),
            ArenaHistoryBudgetService.Build(
                oversized,
                tiny,
                null,
                null,
                OversizedFactory,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens),
            "oversized mandatory rows");

        var allMandatory = SessionStore.CreateDefaultSnapshot();
        allMandatory.Engine.Messages.Clear();
        allMandatory.Engine.Messages.Add(Message(1, "operator", "Operator", new string('m', 10_000)));
        allMandatory.Engine.TurnCount = 1;
        var mandatoryCalls = 0;
        IReadOnlyList<ModelChatMessage> AllMandatoryFactory(IReadOnlySet<string>? ids)
        {
            mandatoryCalls++;
            return [new ModelChatMessage("user", allMandatory.Engine.Messages[0].Text)];
        }
        var mandatoryResult = ArenaHistoryBudgetService.Build(
            allMandatory,
            tiny,
            null,
            null,
            AllMandatoryFactory,
            selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
        Require(!mandatoryResult.Ok && mandatoryCalls == 1,
            "an all-mandatory oversized prompt must exact-build once and fail without a redundant fallback scan");

        var retrySnapshot = SessionStore.CreateDefaultSnapshot();
        retrySnapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 12; turn++)
        {
            var row = Message(
                turn,
                turn == 11 ? "operator" : "beta",
                turn == 11 ? "Operator" : "Beta",
                $"RETRY_{turn:D2}_{new string('q', 180)}");
            row.MessageId = turn is 3 or 7 ? null : turn is 4 or 8 ? "retry-duplicate" : row.MessageId;
            retrySnapshot.Engine.Messages.Add(row);
        }

        retrySnapshot.Engine.TurnCount = 12;
        var retryConfig = Config("retry-oracle", 1_024, ModelHistoryPolicies.Rolling80, 64);
        const int retryBoundary = 13;
        IReadOnlyList<ModelChatMessage> RetryFactory(IReadOnlySet<string>? ids)
        {
            var rows = LegacyEligibleIdentities(retrySnapshot, retryBoundary, null, null)
                .Where(item => ids is null || ids.Contains(item.Id));
            return [new ModelChatMessage("user", string.Join('\n', rows.Select(item => item.Message.Text)))];
        }
        var originalLegacy = LegacyArenaHistoryBuild(
            retrySnapshot,
            retryConfig,
            retryBoundary,
            null,
            RetryFactory);
        var originalOptimized = ArenaHistoryBudgetService.Build(
            retrySnapshot,
            retryConfig,
            retryBoundary,
            null,
            RetryFactory,
            selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
        RequireBudgetEquivalent(originalLegacy, originalOptimized, "retry receipt origin");
        var frozen = originalOptimized.Receipt
            ?? throw new InvalidOperationException("retry fixture did not produce a frozen receipt");
        retrySnapshot.Engine.Messages.Add(Message(13, "gamma", "Gamma", "future row excluded by causal boundary"));
        retrySnapshot.Engine.TurnCount = 13;
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(
                retrySnapshot,
                retryConfig,
                retryBoundary,
                null,
                RetryFactory,
                frozen),
            ArenaHistoryBudgetService.Build(
                retrySnapshot,
                retryConfig,
                retryBoundary,
                null,
                RetryFactory,
                frozen,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens),
            "frozen retry IDs and fingerprint");
        var retainedId = frozen.IncludedMessageIds[0];
        var retained = LegacyEligibleIdentities(retrySnapshot, retryBoundary, null, null)
            .Single(item => item.Id == retainedId)
            .Message;
        var retainedIndex = retrySnapshot.Engine.Messages.IndexOf(retained);
        retrySnapshot.Engine.Messages[retainedIndex] = new DialogueMessage
        {
            MessageId = retained.MessageId,
            Turn = retained.Turn,
            Speaker = retained.Speaker,
            SpeakerId = retained.SpeakerId,
            Text = retained.Text + " mutated",
            Status = retained.Status,
            Pinned = retained.Pinned,
            Kind = retained.Kind,
            CreatedAt = retained.CreatedAt,
            Model = retained.Model,
            Metadata = retained.Metadata,
            Extra = retained.Extra
        };
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(
                retrySnapshot,
                retryConfig,
                retryBoundary,
                null,
                RetryFactory,
                frozen),
            ArenaHistoryBudgetService.Build(
                retrySnapshot,
                retryConfig,
                retryBoundary,
                null,
                RetryFactory,
                frozen,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens),
            "mutated frozen fingerprint rejection");

        var strictConfig = Config("strict-bypass", 2_048, ModelHistoryPolicies.Strict, 64);
        var bypassMessages = new[] { new ModelChatMessage("user", "exact bypass bytes") };
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(retrySnapshot, strictConfig, null, null, _ => bypassMessages),
            ArenaHistoryBudgetService.Build(
                retrySnapshot,
                strictConfig,
                null,
                null,
                _ => bypassMessages,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens),
            "strict policy bypass");
        retrySnapshot.Engine.FactoryMode = true;
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(retrySnapshot, retryConfig, null, null, _ => bypassMessages),
            ArenaHistoryBudgetService.Build(
                retrySnapshot,
                retryConfig,
                null,
                null,
                _ => bypassMessages,
                selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens),
            "Factory mode bypass");
    }

    public static void RollingBudgetProductionContractsPreservePromptBytes()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 24; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn is 19 or 23 ? "operator" : turn % 2 == 0 ? "beta" : "gamma",
                turn is 19 or 23 ? "Operator" : turn % 2 == 0 ? "Beta" : "Gamma",
                $"PRODUCTION_{turn:D2}_{new string((char)('a' + turn % 20), 260)}"));
        }

        snapshot.Engine.TurnCount = 24;
        snapshot.Engine.Internet.UseInternet = false;
        var config = Config(
            "production-prompt",
            2_048,
            ModelHistoryPolicies.Rolling80,
            maxOutputTokens: 128,
            responseTone: ModelResponseTones.Direct);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        IReadOnlyList<ModelChatMessage> ArenaFactory(IReadOnlySet<string>? ids) =>
            ModelResponseToneInstructions.Apply(
                config,
                TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids),
                factoryMode: false);
        var arenaContract = ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(snapshot, null, null);
        Require(arenaContract == ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens,
            "an internet-off Arena prompt with pinned dialogue must qualify for the exact monotonic selector");
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(snapshot, config, null, null, ArenaFactory),
            ArenaHistoryBudgetService.Build(snapshot, config, null, null, ArenaFactory, selectionContract: arenaContract),
            "actual Arena prompt bytes");

        const int causalBeforeTurn = 21;
        const int transcriptAfterTurn = 4;
        IReadOnlyList<ModelChatMessage> CausalArenaFactory(IReadOnlySet<string>? ids) =>
            ModelResponseToneInstructions.Apply(
                config,
                TurnRunnerService.BuildPrompt(
                    snapshot,
                    plan,
                    beforeTurn: causalBeforeTurn,
                    transcriptAfterTurn: transcriptAfterTurn,
                    includedTranscriptMessageIds: ids),
                factoryMode: false);
        var causalContract = ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(
            snapshot,
            causalBeforeTurn,
            transcriptAfterTurn);
        RequireBudgetEquivalent(
            LegacyArenaHistoryBuild(
                snapshot,
                config,
                causalBeforeTurn,
                transcriptAfterTurn,
                CausalArenaFactory),
            ArenaHistoryBudgetService.Build(
                snapshot,
                config,
                causalBeforeTurn,
                transcriptAfterTurn,
                CausalArenaFactory,
                selectionContract: causalContract),
            "actual causal Arena prompt bytes");

        var narratorSnapshot = SessionStore.CreateDefaultSnapshot();
        narratorSnapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 18; turn++)
        {
            narratorSnapshot.Engine.Messages.Add(Message(
                turn,
                turn == 17 ? "operator" : "beta",
                turn == 17 ? "Operator" : "Beta",
                $"NARRATOR_{turn:D2}_{new string('n', 220)}"));
        }

        narratorSnapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "narrator-context-19",
            Turn = 19,
            SpeakerId = "operator",
            Speaker = "Operator",
            Text = $"PINNED_CONTEXT_{new string('c', 220)}",
            Status = "ok",
            Kind = "internet",
            CreatedAt = 19
        });
        narratorSnapshot.Engine.Messages.Add(Message(20, "beta", "Beta", $"LATEST_DIALOGUE_{new string('d', 220)}"));
        narratorSnapshot.Engine.TurnCount = 20;
        var narratorContract = ArenaHistoryBudgetService.NarratorPromptSelectionContract(narratorSnapshot);
        Require(narratorContract == ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens,
            "a narrator context section pinned by a mandatory row must qualify for exact monotonic selection");
        foreach (var promptName in new[] { "BuildNarratorPrompt", "BuildDecisionCardPrompt" })
        {
            IReadOnlyList<ModelChatMessage> NarratorFactory(IReadOnlySet<string>? ids) =>
                ModelResponseToneInstructions.Apply(
                    config,
                    InvokeNarratorPrompt(promptName, narratorSnapshot, ids),
                    factoryMode: false);
            Func<DialogueMessage, bool> eligibility = message =>
                message.Kind is "message" or "internet" or "internet_tool" or "";
            RequireBudgetEquivalent(
                LegacyArenaHistoryBuild(narratorSnapshot, config, null, null, NarratorFactory, eligibility: eligibility),
                ArenaHistoryBudgetService.Build(
                    narratorSnapshot,
                    config,
                    null,
                    null,
                    NarratorFactory,
                    eligibility: eligibility,
                    selectionContract: narratorContract),
                $"actual {promptName} bytes");
        }

        snapshot.Engine.Internet.UseInternet = true;
        Require(ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(snapshot, null, null)
                == ArenaHistoryPromptSelectionContract.LegacyExact,
            "selection-dependent grounding must force the legacy exact scan");
        var groundingCalls = 0;
        IReadOnlyList<ModelChatMessage> GroundingFactory(IReadOnlySet<string>? ids)
        {
            groundingCalls++;
            return ArenaFactory(ids);
        }

        var groundedLegacy = LegacyArenaHistoryBuild(snapshot, config, null, null, GroundingFactory);
        var legacyGroundingCalls = groundingCalls;
        groundingCalls = 0;
        var grounded = ArenaHistoryBudgetService.Build(
            snapshot,
            config,
            null,
            null,
            GroundingFactory,
            selectionContract: ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(snapshot, null, null));
        RequireBudgetEquivalent(groundedLegacy, grounded, "grounding-dependent Arena bytes");
        Require(groundingCalls == legacyGroundingCalls,
            "grounding-dependent prompts must retain the exact legacy evaluation count and order");

        var unsafeOperator = SessionStore.CreateDefaultSnapshot();
        unsafeOperator.Engine.Messages.Clear();
        unsafeOperator.Engine.Messages.Add(Message(1, "beta", "Beta", "public dialogue"));
        unsafeOperator.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "operator-tool-row",
            Turn = 2,
            SpeakerId = "operator",
            Speaker = "Operator",
            Text = "non-dialogue operator row",
            Status = "ok",
            Kind = "internet",
            CreatedAt = 2
        });
        unsafeOperator.Engine.TurnCount = 2;
        unsafeOperator.Engine.Internet.UseInternet = false;
        Require(ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(unsafeOperator, null, null)
                == ArenaHistoryPromptSelectionContract.LegacyExact,
            "a latest Operator row outside public dialogue must retain the legacy exact scan");

        var ambiguousOperator = SessionStore.CreateDefaultSnapshot();
        ambiguousOperator.Engine.Messages.Clear();
        ambiguousOperator.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "operator-same-turn-early",
            Turn = 1,
            SpeakerId = "operator",
            Speaker = "Operator",
            Text = "earliest same-turn request",
            Status = "ok",
            Kind = "message",
            CreatedAt = 1
        });
        ambiguousOperator.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "operator-same-turn-late",
            Turn = 1,
            SpeakerId = "operator",
            Speaker = "Operator",
            Text = "mandatory same-turn request",
            Status = "ok",
            Kind = "message",
            CreatedAt = 2
        });
        ambiguousOperator.Engine.TurnCount = 1;
        Require(ArenaHistoryBudgetService.ArenaTurnPromptSelectionContract(ambiguousOperator, null, null)
                == ArenaHistoryPromptSelectionContract.LegacyExact,
            "same-turn Operator ordering that can change Latest Operator text must retain the legacy scan");

        var unsafeNarrator = SessionStore.CreateDefaultSnapshot();
        unsafeNarrator.Engine.Messages.Clear();
        unsafeNarrator.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "unpinned-context",
            Turn = 1,
            SpeakerId = "internet",
            Speaker = "Internet",
            Text = "context whose section can disappear",
            Status = "ok",
            Kind = "internet",
            CreatedAt = 1
        });
        unsafeNarrator.Engine.Messages.Add(Message(2, "beta", "Beta", "pinned dialogue"));
        unsafeNarrator.Engine.TurnCount = 2;
        Require(ArenaHistoryBudgetService.NarratorPromptSelectionContract(unsafeNarrator)
                == ArenaHistoryPromptSelectionContract.LegacyExact,
            "an unpinned narrator context section must retain the legacy exact scan");
    }

    public static void RollingBudgetContradictedAssertionFallsBackExactly()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 10; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn == 10 ? "operator" : "beta",
                turn == 10 ? "Operator" : "Beta",
                $"row-{turn}"));
        }

        snapshot.Engine.TurnCount = 10;
        var config = Config("nonmonotonic", 512, ModelHistoryPolicies.Rolling80, maxOutputTokens: 0);
        var legacyCalls = 0;
        IReadOnlyList<ModelChatMessage> LegacyFactory(IReadOnlySet<string>? ids)
        {
            legacyCalls++;
            return NonmonotonicPrompt(ids, snapshot.Engine.Messages.Count);
        }
        var legacy = LegacyArenaHistoryBuild(snapshot, config, null, null, LegacyFactory);

        var attemptedCalls = 0;
        IReadOnlyList<ModelChatMessage> AttemptedFactory(IReadOnlySet<string>? ids)
        {
            attemptedCalls++;
            return NonmonotonicPrompt(ids, snapshot.Engine.Messages.Count);
        }
        var attempted = ArenaHistoryBudgetService.Build(
            snapshot,
            config,
            null,
            null,
            AttemptedFactory,
            selectionContract: ArenaHistoryPromptSelectionContract.DeterministicNonIncreasingEstimatedTokens);
        RequireBudgetEquivalent(legacy, attempted, "contradicted monotonic assertion fallback");
        Require(legacyCalls == 2 && attemptedCalls > legacyCalls,
            "a contradicted assertion must abandon its binary probe and rerun the legacy sequential oracle");
    }

    public static void DistinguishesCleanAndLegacyModelDefaults()
    {
        var clean = SessionStore.CreateDefaultSnapshot();
        var config = Config("model-a");
        clean.Configs["shared"] = config;
        var cleanResolved = ModelProviderRouting.Resolve(clean, "alpha", out _)!;
        Require(clean.ModelSettingsVersion == ModelRuntimeSettingsRegistry.CurrentSchemaVersion,
            "clean snapshots must declare the canonical settings schema");
        Require(cleanResolved.HistoryPolicy == ModelHistoryPolicies.Rolling80,
            "a newly routed clean-session model must default to rolling_80");

        var legacy = new ArenaSnapshot();
        legacy.Configs["shared"] = config;
        var legacyResolved = ModelProviderRouting.Resolve(legacy, "alpha", out _)!;
        Require(legacy.ModelSettingsVersion == 0 && legacyResolved.HistoryPolicy == ModelHistoryPolicies.Strict,
            "missing legacy schema markers must preserve strict history");
    }

    public static void ResolvesCanonicalAndRawAliases()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var canonical = Config("publisher/model.gguf");
        var rawAlias = Config("model.gguf");
        var entries = ModelRuntimeSettingsRegistry.RegisterAliases(
            snapshot,
            [canonical, rawAlias],
            32_768,
            ModelHistoryPolicies.Chaptered,
            ModelResponseTones.Direct,
            "ignored");
        Require(entries.Count == 2 && snapshot.ModelSettings.Count == 2,
            "equivalent catalog and role aliases must each receive opaque registry identities");
        var resolved = ModelRuntimeSettingsRegistry.Resolve(snapshot, rawAlias);
        Require(resolved.ConfiguredContextWindow == 32_768
            && resolved.HistoryPolicy == ModelHistoryPolicies.Chaptered
            && resolved.ResponseTone == ModelResponseTones.Direct,
            "raw routed aliases must resolve settings registered from a canonical catalog row");
        Require(snapshot.ModelSettings.Keys.All(key => key.StartsWith("model-settings:", StringComparison.Ordinal)
            && !key.Contains("model.gguf", StringComparison.OrdinalIgnoreCase)),
            "registry keys must not disclose provider model names");
    }

    public static void RollingBudgetDropsOnlyWholeOldestEntries()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.TranscriptWindow = 1;
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 12; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn == 12 ? "operator" : "beta",
                turn == 12 ? "Operator" : "Beta",
                $"ENTRY_{turn:D2}_BEGIN {new string((char)('a' + turn % 20), 900)} ENTRY_{turn:D2}_END"));
        }
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "failed-message-13",
            Turn = 13,
            SpeakerId = "beta",
            Speaker = "Beta",
            Text = "FAILED_CONTEXT_ROW_MUST_NOT_LEAK",
            Status = "error",
            Kind = "message",
            CreatedAt = 13
        });
        snapshot.Engine.TurnCount = 13;
        var config = Config("rolling-model", 4_096, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot,
            config,
            beforeTurn: null,
            transcriptAfterTurn: null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));

        Require(built.Ok && built.Receipt is not null, "rolling_80 must emit a durable budget receipt");
        var receipt = built.Receipt!;
        Require(receipt.OmittedEntryCount > 0 && receipt.IncludedEntryCount > 0,
            "the fixture must exercise both retained and omitted entries");
        Require(receipt.EligibleEntryCount == 12,
            "rolling history must ignore failed rows and consider successful Arena history independently of TranscriptWindow");
        var prompt = string.Join("\n", built.Messages.Select(message => message.Content));
        Require(!prompt.Contains("FAILED_CONTEXT_ROW_MUST_NOT_LEAK", StringComparison.Ordinal),
            "context-limit and other failed transcript rows must not leak into later Arena prompts");
        foreach (var message in snapshot.Engine.Messages)
        {
            var retained = receipt.IncludedMessageIds.Contains(message.MessageId!, StringComparer.Ordinal);
            Require(retained == prompt.Contains($"ENTRY_{message.Turn:D2}_BEGIN", StringComparison.Ordinal),
                $"entry {message.Turn} must be retained or omitted as a whole");
            Require(retained == prompt.Contains($"ENTRY_{message.Turn:D2}_END", StringComparison.Ordinal),
                $"entry {message.Turn} text must never be trimmed at the budget boundary");
        }
        Require(receipt.IncludedMessageIds.Contains("message-12", StringComparer.Ordinal),
            "latest Operator direction must remain mandatory");
    }

    public static void FrozenReceiptExcludesFutureTurns()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 5; turn++)
        {
            snapshot.Engine.Messages.Add(Message(turn, turn == 1 ? "operator" : "beta", turn == 1 ? "Operator" : "Beta", $"causal-{turn}"));
        }
        snapshot.Engine.TurnCount = 5;
        var config = Config("rolling-model", 16_384, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var original = ArenaHistoryBudgetService.Build(
            snapshot, config, 6, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, 6, includedTranscriptMessageIds: ids));
        Require(original.Receipt is not null, "original request must emit a receipt");
        var originalReceipt = original.Receipt!;

        snapshot.Engine.Messages.Add(Message(6, "gamma", "Gamma", "future-six"));
        snapshot.Engine.Messages.Add(Message(7, "operator", "Operator", "future-seven"));
        snapshot.Engine.TurnCount = 7;
        var retry = ArenaHistoryBudgetService.Build(
            snapshot, config, 6, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, 6, includedTranscriptMessageIds: ids),
            originalReceipt);
        Require(retry.Ok && retry.Receipt is not null, "unchanged causal history must permit a frozen retry");
        var retryReceipt = retry.Receipt!;
        Require(retryReceipt.ContextFingerprint == originalReceipt.ContextFingerprint
            && retryReceipt.IncludedMessageIds.SequenceEqual(originalReceipt.IncludedMessageIds),
            "retry must retain the original entry selection and fingerprint");
        var prompt = string.Join("\n", retry.Messages.Select(message => message.Content));
        Require(!prompt.Contains("future-six", StringComparison.Ordinal)
            && !prompt.Contains("future-seven", StringComparison.Ordinal),
            "future turns must never leak into a causal retry");
    }

    public static void FactorySuppressesToneAndRollingBudget()
    {
        var config = Config("factory-model", 4_096, ModelHistoryPolicies.Rolling80, 128, ModelResponseTones.Custom, "sound like a pirate");
        var source = new[] { new ModelChatMessage("user", "  exact factory root\r\n") };
        var toned = ModelResponseToneInstructions.Apply(config, source, factoryMode: true);
        Require(ReferenceEquals(source, toned) && toned.Count == 1 && toned[0].Content == "  exact factory root\r\n",
            "Factory public_group_v1 payloads must receive zero tone bytes");

        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.FactoryMode = true;
        var built = ArenaHistoryBudgetService.Build(snapshot, config, null, null, _ => source);
        Require(built.Receipt is null && built.Messages[0].Content == source[0].Content,
            "Factory must bypass Arena rolling receipts and keep exact content");
    }

    public static void ClassifiesProviderOutcomes()
    {
        var context = ModelCompletionOutcomeClassifier.Normalize(new ModelCompletionResult(
            false, "http://local", "model", "", "", 1, 0, 0, 0,
            "This request exceeds the maximum context length", DateTimeOffset.Now,
            ProviderStatusCode: 400, ProviderErrorCode: "context_length_exceeded"));
        Require(context.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && context.StopReason == ModelCompletionStopReason.ProviderError,
            "provider-specific context failures must normalize to the input-context outcome");
        Require(ModelCompletionOutcomeClassifier.ClassifyStopReason("length") == ModelCompletionStopReason.OutputLimitReached
            && ModelCompletionOutcomeClassifier.ClassifyStopReason("max_tokens") == ModelCompletionStopReason.OutputLimitReached,
            "provider output ceilings must normalize independently from request failures");
        using var response = JsonDocument.Parse("{\"choices\":[{\"finish_reason\":\"length\"}]}");
        Require(ModelCompletionOutcomeClassifier.ExtractStopReason(response.RootElement) == ModelCompletionStopReason.OutputLimitReached,
            "nested OpenAI-compatible finish reasons must be recognized");
        Require(ModelCompletionOutcomeClassifier.ClassifyFailure("previous_response_id expired because conversation state was not found")
                == ModelCompletionFailureKind.NativeStateExhausted
            && ModelCompletionOutcomeClassifier.ClassifyFailure("model is loading")
                == ModelCompletionFailureKind.ProviderLoading
            && ModelCompletionOutcomeClassifier.ClassifyFailure("Provider returned no public content after retry")
                == ModelCompletionFailureKind.EmptyPublicContent,
            "native-state, loading, and empty-public-content outcomes must remain distinct");

        const string providerToken = "sk-provider-secret-0123456789";
        Require(ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode("context_length_exceeded")
                == "context_length_exceeded"
            && ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode(providerToken).Length == 0
            && ModelCompletionOutcomeClassifier.PrivacySafeProviderErrorCode("C:/private/model.gguf").Length == 0,
            "durable provider error codes must admit code-shaped evidence while rejecting credentials and paths");
        var hostileCode = ModelProviderClient.ExtractProviderErrorCode(
            "{\"error\":{\"code\":\"" + providerToken + "\"}}",
            providerToken);
        Require(hostileCode.Length == 0,
            "provider bodies must never copy the active credential into durable outcome metadata");
    }

    public static void ProviderAdaptersExposeTypedContextAndOutputLimits()
    {
        var adapters = new[]
        {
            new AdapterCase(
                "OpenAI-compatible",
                ModelProviderApiModes.OpenAiCompatible,
                "http://127.0.0.1:1234/v1",
                """{"model":"typed-model","choices":[{"finish_reason":"length","message":{"role":"assistant","content":"partial openai"}}]}"""),
            new AdapterCase(
                "llama.cpp",
                ModelProviderApiModes.LlamaCppNative,
                "http://127.0.0.1:8080/v1",
                """{"model":"typed-model","choices":[{"finish_reason":"max_tokens","message":{"role":"assistant","content":"partial llama"}}]}"""),
            new AdapterCase(
                "LM Studio native",
                ModelProviderApiModes.LmStudioNative,
                "http://127.0.0.1:1234/v1",
                """{"model_instance_id":"typed-model","finish_reason":"max_output_tokens","output":[{"type":"message","content":"partial lm studio"}]}"""),
            new AdapterCase(
                "Ollama native",
                ModelProviderApiModes.OllamaNative,
                "http://127.0.0.1:11434/v1",
                """{"model":"typed-model","done":true,"done_reason":"length","message":{"role":"assistant","content":"partial ollama"}}""")
        };

        foreach (var adapter in adapters)
        {
            const string errorBody = """{"error":{"code":"context_length_exceeded","message":"maximum context length exceeded"}}""";
            var rejectedHandler = new CaptureHandler(errorBody, HttpStatusCode.BadRequest);
            var rejected = new ModelProviderClient(new HttpClient(rejectedHandler)).CompleteChatAsync(
                AdapterConfig(adapter),
                [new ModelChatMessage("user", "oversized input")]).GetAwaiter().GetResult();
            Require(!rejected.Ok
                && rejected.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
                && rejected.StopReason == ModelCompletionStopReason.ProviderError
                && rejected.ProviderStatusCode == 400
                && rejected.ProviderErrorCode == "context_length_exceeded"
                && rejectedHandler.Calls == 1,
                $"{adapter.Name} must expose one typed input-context rejection with provider status/code evidence");

            var limitedHandler = new CaptureHandler(adapter.LimitedResponse);
            var limited = new ModelProviderClient(new HttpClient(limitedHandler)).CompleteChatAsync(
                AdapterConfig(adapter),
                [new ModelChatMessage("user", "bounded input")]).GetAwaiter().GetResult();
            Require(limited.Ok
                && limited.Text.StartsWith("partial", StringComparison.Ordinal)
                && limited.FailureKind == ModelCompletionFailureKind.None
                && limited.StopReason == ModelCompletionStopReason.OutputLimitReached
                && limitedHandler.Calls == 1,
                $"{adapter.Name} must preserve partial content while typing its provider stop reason as output_limit_reached");
        }
    }

    public static void OversizedMandatoryPromptFailsPreflight()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", new string('x', 20_000)));
        snapshot.Engine.TurnCount = 1;
        var config = Config("tiny", 512, ModelHistoryPolicies.Rolling80, maxOutputTokens: 64);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot, config, null, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));
        Require(!built.Ok
            && built.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && built.Receipt is { IncludedEntryCount: 1, OmittedEntryCount: 0 },
            "mandatory latest-Operator content must fail preflight rather than be trimmed or sent over budget");
    }

    public static void PreservesLatestOperatorAndNewestDialogue()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 8; turn++)
        {
            snapshot.Engine.Messages.Add(Message(turn, "gamma", "Gamma", $"OLD_{turn} {new string('z', 3_000)}"));
        }

        snapshot.Engine.Messages.Add(Message(9, "operator", "Operator", $"LATEST_OPERATOR {new string('o', 3_000)}"));
        snapshot.Engine.Messages.Add(Message(10, "beta", "Beta", $"NEWEST_DIALOGUE {new string('d', 3_000)}"));
        snapshot.Engine.Messages.Add(new DialogueMessage
        {
            MessageId = "internet-11",
            Turn = 11,
            SpeakerId = "internet",
            Speaker = "Internet",
            Text = $"NEWER_TOOL_ROW {new string('t', 3_000)}",
            Status = "ok",
            Kind = "internet",
            CreatedAt = 11
        });
        snapshot.Engine.TurnCount = 11;
        var config = Config("rolling", 8_192, ModelHistoryPolicies.Rolling80, maxOutputTokens: 256);
        var plan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var built = ArenaHistoryBudgetService.Build(
            snapshot, config, null, null,
            ids => TurnRunnerService.BuildPrompt(snapshot, plan, includedTranscriptMessageIds: ids));
        Require(built.Ok && built.Receipt is not null && built.Receipt.OmittedEntryCount > 0,
            "fixture must force whole-entry rolling omission without exhausting mandatory content");
        Require(built.Receipt!.IncludedMessageIds.Contains("message-9", StringComparer.Ordinal)
            && built.Receipt.IncludedMessageIds.Contains("message-10", StringComparer.Ordinal),
            "Rolling 80 must retain both the latest Operator direction and newest public dialogue");
        var prompt = string.Join("\n", built.Messages.Select(message => message.Content));
        Require(prompt.Contains("LATEST_OPERATOR", StringComparison.Ordinal)
            && prompt.Contains("NEWEST_DIALOGUE", StringComparison.Ordinal),
            "mandatory Operator and newest-dialogue text must reach the prompt intact");

        var oversized = SessionStore.CreateDefaultSnapshot();
        oversized.Engine.Messages.Clear();
        oversized.Engine.Messages.Add(Message(1, "gamma", "Gamma", $"REMOVABLE {new string('r', 4_000)}"));
        oversized.Engine.Messages.Add(Message(2, "operator", "Operator", $"MANDATORY_OPERATOR {new string('o', 8_000)}"));
        oversized.Engine.Messages.Add(Message(3, "beta", "Beta", $"MANDATORY_DIALOGUE {new string('d', 8_000)}"));
        oversized.Engine.TurnCount = 3;
        var oversizedPlan = new OneTurnPlan(true, "alpha", "Alpha", config, null, "");
        var failed = ArenaHistoryBudgetService.Build(
            oversized, config, null, null,
            ids => TurnRunnerService.BuildPrompt(oversized, oversizedPlan, includedTranscriptMessageIds: ids));
        Require(!failed.Ok
            && failed.FailureKind == ModelCompletionFailureKind.ContextLimitExceeded
            && failed.Receipt is { IncludedEntryCount: 2, OmittedEntryCount: 1 }
            && failed.Receipt.IncludedMessageIds.SequenceEqual(["message-2", "message-3"]),
            "combined oversized mandatory Operator/dialogue material must fail preflight without trimming either row");
    }

    public static void ContextFailureDoesNotCallFallback()
    {
        WithTempStore((store, snapshot) =>
        {
            snapshot.Configs["shared"] = Config("fallback", historyPolicy: ModelHistoryPolicies.Strict);
            snapshot.Configs["alpha"] = Config("primary", historyPolicy: ModelHistoryPolicies.Strict, explicitlyAssigned: true);
            ModelRuntimeSettingsRegistry.Register(snapshot, snapshot.Configs["shared"], 0, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            ModelRuntimeSettingsRegistry.Register(snapshot, snapshot.Configs["alpha"], 0, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "answer this"));
            snapshot.Engine.TurnCount = 1;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                false, "http://local", "primary", "", "", 1, 0, 0, 0,
                "maximum context length exceeded", DateTimeOffset.Now,
                FailureKind: ModelCompletionFailureKind.ContextLimitExceeded,
                StopReason: ModelCompletionStopReason.ProviderError));
            var result = new TurnRunnerService(provider, store).RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(result.Executed && result.Completion is { Ok: false } && provider.Calls == 1,
                "confirmed input-context exhaustion must not resend the same oversized prompt to fallback");
            Require(result.Message?.Metadata.TryGetValue("completion_failure_kind", out var kind) == true
                && kind.GetString() == "context_limit_exceeded",
                "context failure must persist a structured agent-owned recovery outcome");
        });
    }

    public static void RollingLmStudioReconstructsVisibleHistory()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                Model = "rolling-native",
                NativeStatefulChat = true,
                ConfiguredContextWindow = 16_384,
                ContextLength = 16_384,
                HistoryPolicy = ModelHistoryPolicies.Rolling80,
                MaxOutputTokens = 256
            };
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "visible operator root"));
            var prior = Message(2, "alpha", "Alpha", "prior alpha visible row");
            prior.Metadata["provider_response_id"] = JsonSerializer.SerializeToElement("opaque-native-response-id");
            snapshot.Engine.Messages.Add(prior);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "new response", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var result = new TurnRunnerService(provider, store).RunAgentTurnAsync("default", "alpha").GetAwaiter().GetResult();
            Require(result.Executed && provider.Calls == 1, "rolling LM Studio turn should execute exactly one reconstructed request");
            Require(result.Message is not null
                && CompletionRouteReceipt.TryRead(result.Message, out var route)
                && route.RoutePhase == CompletionRouteReceipt.PrimaryPhase,
                "ordinary Arena responses must persist a privacy-safe producing-route receipt for causal recovery");
            Require(provider.Configs[0].NativeStatefulChat == false
                && string.IsNullOrWhiteSpace(provider.Configs[0].PreviousResponseId),
                "rolling_80 must disable opaque LM Studio continuation state for the request");
            var prompt = string.Join("\n", provider.Requests[0].Select(message => message.Content));
            Require(prompt.Contains("visible operator root", StringComparison.Ordinal)
                && prompt.Contains("prior alpha visible row", StringComparison.Ordinal),
                "rolling LM Studio calls must reconstruct selected visible transcript rows instead of trusting opaque provider history");
        });
    }

    public static void ContextFailureGatesUntilRetryAndAdvancesOnce()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("gated", 8_192, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 8_192, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "answer"));
            snapshot.Engine.TurnCount = 1;
            snapshot.Engine.TurnIndex = 0;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var invocation = 0;
            var provider = new ScriptedProvider(_ => ++invocation == 1
                ? new ModelCompletionResult(false, config.BaseUrl, config.Model, "", "", 1, 0, 0, 0,
                    "maximum context length exceeded", DateTimeOffset.Now,
                    FailureKind: ModelCompletionFailureKind.ContextLimitExceeded,
                    StopReason: ModelCompletionStopReason.ProviderError)
                : new ModelCompletionResult(true, config.BaseUrl, config.Model, "recovered", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var runner = new TurnRunnerService(provider, store);
            var failed = runner.RunOneTurnAsync().GetAwaiter().GetResult();
            var blockedSnapshot = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(failed.Executed
                && blockedSnapshot.Engine.TurnIndex == 0
                && TurnRunnerService.UnresolvedContextFailure(blockedSnapshot) is not null,
                "an input-context failure must keep the failed speaker current and remain explicitly unresolved");
            Require(!runner.PlanOneTurn(blockedSnapshot).Ok
                && !runner.PlanAgentTurn(blockedSnapshot, "beta").Ok
                && !runner.RunAgentTurnAsync("default", "beta").GetAwaiter().GetResult().Executed
                && provider.Calls == 1,
                "automatic and manual planning must remain gated without another provider call");

            var original = blockedSnapshot.Engine.Messages.Single(message => message.Turn == 2);
            var retried = runner.RetryTurnAsync("default", original.Turn, original.SpeakerId, original.CreatedAt).GetAwaiter().GetResult();
            var recovered = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(retried.Completion?.Ok == true
                && provider.Calls == 2
                && recovered.Engine.TurnIndex == 1
                && TurnRunnerService.UnresolvedContextFailure(recovered) is null,
                "a successful causal retry must resolve the gate and advance past the failed speaker exactly once");
        });
    }

    public static void RecoverySkipsContinuesAndEndsCausally()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write the result"));
            var partial = Message(2, "alpha", "Alpha", "The first half");
            partial.Metadata["completion_failure_kind"] = JsonSerializer.SerializeToElement("none");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            var alpha = snapshot.Engine.Agents.First(agent => agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase));
            StructuredMemoryService.AddTurnMemory(
                snapshot,
                alpha,
                partial,
                "Turn 2: The first half",
                DateTimeOffset.FromUnixTimeSeconds(2));
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, "http://local", "recovery", " and the second half.", "", 1, 10, 5, 15, "", DateTimeOffset.Now,
                StopReason: ModelCompletionStopReason.Completed));
            var recovery = new ContextRecoveryService(store, provider);
            var continued = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(continued.Ok && provider.Calls == 1
                && continued.Message?.Text == "The first half and the second half.",
                "Continue must append one causal continuation instead of restarting or retrying the response");
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(loaded.Engine.Messages.Count == 2
                && loaded.Engine.Messages[1].Metadata["output_continuation_count"].GetInt32() == 1,
                "continuation must replace the partial response atomically and exactly once");
            var continuedMemory = StructuredMemoryService.SelectForPrompt(
                loaded,
                loaded.Engine.Agents.First(agent => agent.Id.Equals("alpha", StringComparison.OrdinalIgnoreCase)),
                DateTimeOffset.UtcNow);
            Require(continuedMemory.Any(entry => entry.Text.Contains("The first half and the second half.", StringComparison.Ordinal)),
                "continuation must correct the agent's structured private memory to match the replacement response");

            var blocked = Message(3, "beta", "Beta", "Model call failed: maximum context length exceeded");
            blocked = new DialogueMessage
            {
                MessageId = blocked.MessageId,
                Turn = blocked.Turn,
                SpeakerId = blocked.SpeakerId,
                Speaker = blocked.Speaker,
                Text = blocked.Text,
                Status = "error",
                Kind = blocked.Kind,
                CreatedAt = blocked.CreatedAt,
                Metadata = new Dictionary<string, JsonElement>
                {
                    ["completion_failure_kind"] = JsonSerializer.SerializeToElement("context_limit_exceeded"),
                    ["completion_stop_reason"] = JsonSerializer.SerializeToElement("provider_error")
                }
            };
            loaded.Engine.Messages.Add(blocked);
            loaded.Engine.TurnCount = 3;
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var skipped = recovery.SkipBlockedTurnAsync("default", 3, "beta", 3).GetAwaiter().GetResult();
            Require(skipped.Ok && skipped.Message?.Metadata[ContextRecoveryService.RecoveryDispositionMetadataKey].GetString() == "skipped",
                "Skip must mark only a confirmed causal context failure and advance once");
            Require(!recovery.SkipBlockedTurnAsync("default", 3, "beta", 3).GetAwaiter().GetResult().Ok,
                "a blocked turn must not be skipped twice");

            var ended = recovery.EndMatchAsync("default", "operator chose to end").GetAwaiter().GetResult();
            var endedSnapshot = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(ended.Ok && endedSnapshot.Engine.MatchEnded
                && endedSnapshot.Engine.MatchEndReason == "operator chose to end"
                && !new TurnRunnerService(provider, store).PlanOneTurn(endedSnapshot).Ok,
                "End match must durably block later turns until a reset clears the marker");
            Require(!recovery.EndMatchAsync("default", "duplicate end").GetAwaiter().GetResult().Ok
                && store.LoadSnapshotAsync().GetAwaiter().GetResult()!.Engine.MatchEndReason == "operator chose to end",
                "the durable end-match marker must be first-writer-wins and must not be overwritten by duplicate actions");
        });
    }

    public static void ContinuationBindsRouteRejectsDescendantsAndPreservesBytes()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("route-a", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "Prefix \n");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "\t suffix \r\n", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            var recovery = new ContextRecoveryService(store, provider);
            var continued = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(continued.Ok && continued.Message?.Text == "Prefix \n\t suffix \r\n",
                "continuation must concatenate provider text byte-for-byte without trim, inferred spaces, or newline normalization");

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            loaded.Engine.Messages[1].Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            loaded.Engine.Messages.Add(Message(3, "operator", "Operator", "later public direction"));
            loaded.Engine.TurnCount = 3;
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var calls = provider.Calls;
            var descendantRejected = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!descendantRejected.Ok && provider.Calls == calls,
                "continuation must reject a partial response once later successful public dialogue depends on it");

            loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            loaded.Engine.Messages.RemoveAll(message => message.Turn == 3);
            loaded.Engine.TurnCount = 2;
            loaded.Configs["shared"] = Config("route-b", 16_384, ModelHistoryPolicies.Strict, 256);
            ModelRuntimeSettingsRegistry.Register(loaded, loaded.Configs["shared"], 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            store.SaveSnapshotAsync(loaded).GetAwaiter().GetResult();
            var routeRejected = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!routeRejected.Ok && routeRejected.Error.Contains("route", StringComparison.OrdinalIgnoreCase)
                && provider.Calls == calls,
                "continuation must bind to the producing provider/model route and fail closed after routing changes");
        });
    }

    public static void DuplicateContinuationUsesOneProviderCall()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("single-flight", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new BlockingProvider(config);
            var recovery = new ContextRecoveryService(store, provider);
            var first = recovery.ContinueOutputAsync("default", 2, "alpha", 2);
            Require(provider.Entered.Wait(TimeSpan.FromSeconds(5)), "first continuation did not reach the provider");
            var duplicate = recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
            Require(!duplicate.Ok && provider.Calls == 1,
                "a duplicate/in-progress Continue action must fail before issuing a second provider request");
            provider.Release.TrySetResult(true);
            Require(first.GetAwaiter().GetResult().Ok && provider.Calls == 1,
                "the winning continuation should commit exactly once after the duplicate is rejected");
        });
    }

    public static void NarratorAndDecisionCardUseRollingBudget()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("narrator-rolling", 8_192, ModelHistoryPolicies.Rolling80, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 8_192, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.TranscriptWindow = 1;
            snapshot.Engine.Messages.Clear();
            for (var turn = 1; turn <= 8; turn++)
            {
                snapshot.Engine.Messages.Add(Message(turn, "beta", "Beta", $"NARRATOR_OLD_{turn} {new string('n', 4_000)}"));
            }
            snapshot.Engine.Messages.Add(Message(9, "operator", "Operator", "NARRATOR_LATEST_OPERATOR"));
            snapshot.Engine.Messages.Add(Message(10, "alpha", "Alpha", "NARRATOR_NEWEST_DIALOGUE"));
            snapshot.Engine.TurnCount = 10;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(config => new ModelCompletionResult(
                true, config.BaseUrl, config.Model, "bounded narrator output", "", 1, 1, 1, 2, "", DateTimeOffset.Now));
            using var narrator = new NarratorService(provider, store);
            var narrated = narrator.NarrateNowAsync("default").GetAwaiter().GetResult();
            var narratorReceipt = new ArenaHistoryBudgetReceipt();
            var hasReceipt = narrated.Message is not null
                && ArenaHistoryBudgetService.TryReadReceipt(narrated.Message, out narratorReceipt);
            Require(narrated.Ok && provider.Calls == 1
                && hasReceipt
                && narratorReceipt.OmittedEntryCount > 0,
                $"ordinary narrator calls must use the routed model's rolling whole-entry budget and persist its receipt (ok={narrated.Ok}, calls={provider.Calls}, receipt={hasReceipt}, omitted={(hasReceipt ? narratorReceipt.OmittedEntryCount : -1)}, error={narrated.Error})");
            var prompt = string.Join("\n", provider.Requests[0].Select(message => message.Content));
            Require(prompt.Contains("NARRATOR_LATEST_OPERATOR", StringComparison.Ordinal)
                && prompt.Contains("NARRATOR_NEWEST_DIALOGUE", StringComparison.Ordinal)
                && !prompt.Contains("NARRATOR_OLD_1", StringComparison.Ordinal),
                "narrator rolling history must ignore TranscriptWindow, retain mandatory newest rows, and omit old rows whole");

            var decision = narrator.GenerateDecisionCardAsync("default").GetAwaiter().GetResult();
            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            Require(decision.Ok && provider.Calls == 2
                && loaded.Engine.DecisionCard.Metadata.ContainsKey(ArenaHistoryBudgetReceipt.MetadataKey),
                "Decision Card calls must use and persist the same per-model rolling budget evidence");
        });
    }

    public static void NarratorOversizedMandatoryFailsPreflight()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("narrator-tiny", 512, ModelHistoryPolicies.Rolling80, 64);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 512, ModelHistoryPolicies.Rolling80, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Clear();
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", $"MANDATORY_NARRATOR_OPERATOR {new string('o', 6_000)}"));
            snapshot.Engine.Messages.Add(Message(2, "alpha", "Alpha", $"MANDATORY_NARRATOR_DIALOGUE {new string('a', 6_000)}"));
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var provider = new ScriptedProvider(_ => throw new InvalidOperationException("provider must not be called"));
            using var narrator = new NarratorService(provider, store);
            var result = narrator.NarrateNowAsync("default").GetAwaiter().GetResult();
            Require(!result.Ok && provider.Calls == 0
                && result.Message?.Metadata.TryGetValue("completion_failure_kind", out var failure) == true
                && failure.GetString() == "context_limit_exceeded"
                && ArenaHistoryBudgetService.TryReadReceipt(result.Message, out _),
                "oversized mandatory narrator input must fail with typed preflight evidence before any provider call");
        });
    }

    public static void CancelledContinuationClearsBusyMarker()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            var recovery = new ContextRecoveryService(store, new ScriptedProvider(_ => throw new OperationCanceledException("cancelled")));
            try
            {
                recovery.ContinueOutputAsync("default", 2, "alpha", 2).GetAwaiter().GetResult();
                throw new InvalidOperationException("expected continuation cancellation");
            }
            catch (OperationCanceledException)
            {
            }

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var restored = loaded.Engine.Messages.Single(message => message.Turn == 2);
            Require(!restored.Metadata.ContainsKey("output_continuation_attempt")
                && restored.Metadata["output_continuation_state"].GetString() == "failed",
                "cancelled continuation must not strand the durable response in a busy state");
        });
    }

    public static void CancellationBeforeProviderClearsBusyMarker()
    {
        WithTempStore((store, snapshot) =>
        {
            var config = Config("recovery", 16_384, ModelHistoryPolicies.Strict, 256);
            snapshot.Configs["shared"] = config;
            ModelRuntimeSettingsRegistry.Register(snapshot, config, 16_384, ModelHistoryPolicies.Strict, ModelResponseTones.Default, "");
            snapshot.Engine.Messages.Add(Message(1, "operator", "Operator", "write"));
            var partial = Message(2, "alpha", "Alpha", "partial");
            partial.Metadata["completion_stop_reason"] = JsonSerializer.SerializeToElement("output_limit_reached");
            CompletionRouteReceipt.Stamp(partial, CompletionRouteReceipt.Create(config, CompletionRouteReceipt.PrimaryPhase));
            snapshot.Engine.Messages.Add(partial);
            snapshot.Engine.TurnCount = 2;
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

            var eventStore = new EventLogStore(store.DataRoot);
            using var blockedEventLease = CrossProcessWriteLease.AcquireAsync(
                eventStore.EventPath("default"),
                TimeSpan.FromSeconds(5),
                CancellationToken.None).GetAwaiter().GetResult();
            var provider = new ScriptedProvider(_ => throw new InvalidOperationException("provider must not be reached"));
            var recovery = new ContextRecoveryService(store, provider, eventLogStore: eventStore);
            using var cancellation = new CancellationTokenSource();
            var continuation = recovery.ContinueOutputAsync("default", 2, "alpha", 2, cancellation.Token);
            Require(SpinWait.SpinUntil(
                () =>
                {
                    var current = store.LoadSnapshotAsync().GetAwaiter().GetResult();
                    var source = current?.Engine.Messages.SingleOrDefault(message => message.Turn == 2);
                    return source is not null
                        && source.Metadata.ContainsKey("output_continuation_attempt")
                        && source.Metadata.TryGetValue("output_continuation_state", out var state)
                        && state.GetString() == "in_progress";
                },
                TimeSpan.FromSeconds(5)),
                "continuation did not persist its causal in-progress marker before event logging");
            cancellation.Cancel();
            try
            {
                continuation.GetAwaiter().GetResult();
                throw new InvalidOperationException("expected pre-provider continuation cancellation");
            }
            catch (OperationCanceledException)
            {
            }

            var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
            var restored = loaded.Engine.Messages.Single(message => message.Turn == 2);
            Require(provider.Calls == 0
                && !restored.Metadata.ContainsKey("output_continuation_attempt")
                && restored.Metadata["output_continuation_state"].GetString() == "failed",
                "cancellation during started-event logging must clear the durable busy marker before any provider call");
        });
    }

    private static ModelProviderConfig Config(
        string model,
        int context = 0,
        string historyPolicy = ModelHistoryPolicies.Strict,
        int maxOutputTokens = 256,
        string responseTone = ModelResponseTones.Default,
        string customTone = "",
        bool explicitlyAssigned = false) => new()
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        Model = model,
        ExplicitModelAssignment = explicitlyAssigned,
        ContextLength = context,
        ConfiguredContextWindow = context,
        HistoryPolicy = historyPolicy,
        MaxOutputTokens = maxOutputTokens,
        ResponseTone = responseTone,
        CustomTone = customTone
    };

    private static ModelProviderConfig AdapterConfig(AdapterCase adapter) => new()
    {
        BaseUrl = adapter.BaseUrl,
        ApiMode = adapter.ApiMode,
        Model = "typed-model",
        Timeout = 5,
        Temperature = 0.2,
        MaxOutputTokens = 64,
        ConfiguredContextWindow = 8_192,
        HistoryPolicy = ModelHistoryPolicies.Strict
    };

    private static ArenaBudgetedPrompt LegacyArenaHistoryBuild(
        ArenaSnapshot snapshot,
        ModelProviderConfig config,
        int? beforeTurn,
        int? transcriptAfterTurn,
        Func<IReadOnlySet<string>?, IReadOnlyList<ModelChatMessage>> promptFactory,
        ArenaHistoryBudgetReceipt? frozenReceipt = null,
        Func<DialogueMessage, bool>? eligibility = null)
    {
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
        var identities = LegacyEligibleIdentities(snapshot, beforeTurn, transcriptAfterTurn, eligibility);
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
            if (!string.Equals(LegacyContextFingerprint(selected), frozenReceipt.ContextFingerprint, StringComparison.Ordinal))
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
        var targetTotal = Math.Max(1, (int)Math.Floor(contextWindow * (ArenaHistoryBudgetService.TargetPercent / 100d)));
        var inputBudget = Math.Max(1, targetTotal - outputReserve);
        var mandatoryIds = identities
            .Where(item => item.Message.SpeakerId.Equals("operator", StringComparison.OrdinalIgnoreCase))
            .TakeLast(1)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
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
            messages = promptFactory(mutable.ToHashSet(StringComparer.Ordinal));
            estimated = ArenaHistoryBudgetService.EstimateTokens(messages);
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
            ContextFingerprint = LegacyContextFingerprint(retained),
            BeforeTurn = causalBeforeTurn
        };
        return estimated > inputBudget
            ? new ArenaBudgetedPrompt(
                messages,
                receipt,
                $"Input context estimate {estimated} tokens exceeds the Rolling 80 input budget of {inputBudget}; retained mandatory prompt text was not truncated.",
                ModelCompletionFailureKind.ContextLimitExceeded)
            : new ArenaBudgetedPrompt(messages, receipt, "");
    }

    private static List<LegacyIdentifiedMessage> LegacyEligibleIdentities(
        ArenaSnapshot snapshot,
        int? beforeTurn,
        int? transcriptAfterTurn,
        Func<DialogueMessage, bool>? eligibility)
    {
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
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<LegacyIdentifiedMessage>(eligible.Count);
        foreach (var message in eligible)
        {
            var baseId = DialogueMessageIdentity.Resolve(message);
            occurrences.TryGetValue(baseId, out var occurrence);
            occurrences[baseId] = occurrence + 1;
            result.Add(new LegacyIdentifiedMessage(
                occurrence == 0 ? baseId : $"{baseId}:{occurrence}",
                message));
        }

        return result;
    }

    private static string LegacyContextFingerprint(IReadOnlyList<LegacyIdentifiedMessage> messages)
    {
        var canonical = string.Join(
            "\n",
            messages.Select(item => $"{item.Id}|{DialogueMessageIdentity.Fingerprint(item.Message)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void RequireBudgetEquivalent(
        ArenaBudgetedPrompt expected,
        ArenaBudgetedPrompt actual,
        string scenario)
    {
        Require(expected.Ok == actual.Ok
            && expected.Error == actual.Error
            && expected.FailureKind == actual.FailureKind,
            $"{scenario}: result/error contract diverged from the legacy oracle");
        Require(expected.Messages.Count == actual.Messages.Count
            && expected.Messages.Zip(actual.Messages).All(pair =>
                pair.First.Role == pair.Second.Role && pair.First.Content == pair.Second.Content),
            $"{scenario}: exact provider prompt bytes diverged from the legacy oracle");
        Require(JsonSerializer.Serialize(expected.Receipt) == JsonSerializer.Serialize(actual.Receipt),
            $"{scenario}: IDs, fingerprint, counts, or receipt evidence diverged from the legacy oracle");
    }

    private static IReadOnlyList<ModelChatMessage> InvokeNarratorPrompt(
        string methodName,
        ArenaSnapshot snapshot,
        IReadOnlySet<string>? includedIds)
    {
        var method = typeof(NarratorService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Narrator prompt factory {methodName} was not found.");
        var arguments = methodName == "BuildNarratorPrompt"
            ? new object?[] { snapshot, "summarize exactly", includedIds }
            : new object?[] { snapshot, includedIds };
        return method.Invoke(null, arguments) as IReadOnlyList<ModelChatMessage>
            ?? throw new InvalidOperationException($"Narrator prompt factory {methodName} returned no messages.");
    }

    private static IReadOnlyList<ModelChatMessage> NonmonotonicPrompt(
        IReadOnlySet<string>? includedIds,
        int totalEntries)
    {
        var removalCount = totalEntries - (includedIds?.Count ?? totalEntries);
        var contentLength = removalCount switch
        {
            0 => 4_000,
            1 => 100,
            4 => 5_000,
            _ => 100
        };
        return [new ModelChatMessage("user", new string('x', contentLength))];
    }

    private static DialogueMessage Message(int turn, string speakerId, string speaker, string text) => new()
    {
        MessageId = $"message-{turn}",
        Turn = turn,
        SpeakerId = speakerId,
        Speaker = speaker,
        Text = text,
        Status = "ok",
        Kind = "message",
        CreatedAt = turn
    };

    private static RollingBudgetMeasurement MeasureRollingBudget(
        int entryCount,
        ArenaHistoryPromptSelectionContract selectionContract)
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Engine.Messages.Clear();
        for (var index = 0; index < entryCount; index++)
        {
            var turn = index + 1;
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn == entryCount ? "operator" : "beta",
                turn == entryCount ? "Operator" : "Beta",
                $"ROW_{turn:D5}_{new string((char)('a' + index % 20), 56)}"));
        }

        snapshot.Engine.TurnCount = entryCount;
        var config = Config(
            "rolling-performance",
            context: Math.Max(ModelRuntimeSettingsRegistry.MinimumConfiguredContextWindow, entryCount * 10),
            historyPolicy: ModelHistoryPolicies.Rolling80,
            maxOutputTokens: 0);
        var calls = 0;
        IReadOnlyList<ModelChatMessage> PromptFactory(IReadOnlySet<string>? includedIds)
        {
            calls++;
            var content = string.Join(
                '\n',
                snapshot.Engine.Messages
                    .Where(message => includedIds is null || includedIds.Contains(message.MessageId!))
                    .Select(message => message.Text));
            return [new ModelChatMessage("user", content)];
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var result = ArenaHistoryBudgetService.Build(
            snapshot,
            config,
            beforeTurn: null,
            transcriptAfterTurn: null,
            PromptFactory,
            selectionContract: selectionContract);
        stopwatch.Stop();
        return new RollingBudgetMeasurement(
            result,
            calls,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            stopwatch.Elapsed);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WithTempStore(Action<SessionStore, ArenaSnapshot> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(new SessionStore(root), SessionStore.CreateDefaultSnapshot());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedProvider(Func<ModelProviderConfig, ModelCompletionResult> complete) : IModelProviderClient
    {
        public int Calls { get; private set; }
        public List<ModelProviderConfig> Configs { get; } = new();
        public List<IReadOnlyList<ModelChatMessage>> Requests { get; } = new();

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Configs.Add(config);
            Requests.Add(messages.ToArray());
            return Task.FromResult(complete(config));
        }
    }

    private sealed class BlockingProvider(ModelProviderConfig sourceConfig) : IModelProviderClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ManualResetEventSlim Entered { get; } = new(false);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.Set();
            await Release.Task.WaitAsync(cancellationToken);
            return new ModelCompletionResult(
                true,
                sourceConfig.BaseUrl,
                sourceConfig.Model,
                " continued",
                "",
                1,
                1,
                1,
                2,
                "",
                DateTimeOffset.Now);
        }
    }

    private sealed record AdapterCase(string Name, string ApiMode, string BaseUrl, string LimitedResponse);

    private sealed record LegacyIdentifiedMessage(string Id, DialogueMessage Message);

    private sealed record RollingBudgetMeasurement(
        ArenaBudgetedPrompt Result,
        int Calls,
        long AllocatedBytes,
        TimeSpan Elapsed);
}
