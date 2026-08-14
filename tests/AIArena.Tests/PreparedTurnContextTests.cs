using System.Diagnostics;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class PreparedTurnContextTests
{
    public static void PreservesExactPromptBytesAndFreezesCausalInputs()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.PersistenceRevision = 17;
        snapshot.Engine.Messages.Clear();
        snapshot.Engine.TurnCount = 6;
        for (var turn = 1; turn <= 6; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn % 3 == 1 ? "operator" : turn % 2 == 0 ? "alpha" : "beta",
                $"CAUSAL_{turn:D2}_{new string((char)('a' + turn), 96)}"));
        }

        snapshot.Engine.Messages[1] = Message(
            2,
            "alpha",
            $"CAUSAL_02_{new string('c', 96)}",
            "primary-native",
            "resp_primary_anchor");
        var alpha = snapshot.Engine.Agents.First(agent => agent.Id == "alpha");
        var preparedAt = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        var memory = StructuredMemoryService.AddManualMemory(
            snapshot,
            alpha,
            "FROZEN_PRIVATE_MEMORY_SENTINEL",
            StructuredMemoryVisibilities.Private,
            preparedAt,
            preparedAt.AddMinutes(5));
        var primary = NativeConfig("primary-native");
        var fallback = NativeConfig("fallback-native");
        var plan = new OneTurnPlan(true, "alpha", "Alpha", primary, fallback, "");
        var runner = new TurnRunnerService();

        var legacy = TurnRunnerService.BuildPrompt(snapshot, plan);
        var legacyPrimaryConfig = TurnRunnerService.WithNativeContinuation(
            primary,
            snapshot,
            plan.AgentId,
            beforeTurn: null);
        var legacyBudgeted = runner.BuildPromptForConfig(
            snapshot,
            plan,
            legacyPrimaryConfig,
            beforeTurn: null,
            allowInternetTool: true,
            enforceVoiceDrift: false);
        var prepared = PreparedTurnContext.Create(snapshot, plan, beforeTurn: null, preparedAt);
        var optimized = TurnRunnerService.BuildPrompt(
            snapshot,
            plan,
            preparedTurnContext: prepared);
        Require(MessagesEqual(legacy, optimized),
            "prepared prompt bytes diverged from the legacy prompt builder");
        var preparedPrimaryConfig = TurnRunnerService.WithNativeContinuation(
            prepared.PrimaryConfig,
            snapshot,
            plan.AgentId,
            beforeTurn: null,
            prepared);
        var preparedBudgeted = runner.BuildPromptForConfig(
            snapshot,
            plan,
            preparedPrimaryConfig,
            beforeTurn: null,
            allowInternetTool: true,
            enforceVoiceDrift: false,
            preparedTurnContext: prepared);
        Require(legacyBudgeted.Ok == preparedBudgeted.Ok
            && legacyBudgeted.Error == preparedBudgeted.Error
            && MessagesEqual(legacyBudgeted.Messages, preparedBudgeted.Messages)
            && JsonSerializer.Serialize(legacyBudgeted.Receipt) == JsonSerializer.Serialize(preparedBudgeted.Receipt),
            "prepared budget/tone/native-continuation bytes diverged from the legacy provider boundary");
        var preparedFallbackConfig = TurnRunnerService.WithNativeContinuation(
            prepared.FallbackConfig!,
            snapshot,
            plan.AgentId,
            beforeTurn: null,
            prepared);
        var expectedFallback = runner.BuildPromptForConfig(
            snapshot,
            plan,
            preparedFallbackConfig,
            beforeTurn: null,
            allowInternetTool: true,
            enforceVoiceDrift: false,
            preparedTurnContext: prepared);
        var evidence = new ModelChatMessage("user", "FROZEN_TOOL_CONTINUATION_EVIDENCE");
        var expectedToolContinuation = runner.BuildPromptForConfig(
            snapshot,
            plan,
            preparedPrimaryConfig,
            beforeTurn: null,
            allowInternetTool: false,
            enforceVoiceDrift: false,
            extraUserMessage: evidence,
            preparedTurnContext: prepared);
        Require(prepared.PreparedAtUtc == preparedAt
            && prepared.PersistenceRevision == 17,
            "prepared context lost its immutable revision/time scope");
        Require(prepared.NativeAnchor(primary) is { Turn: 2, ResponseId: "resp_primary_anchor" }
            && prepared.NativeAnchor(fallback) is null,
            "prepared native continuation anchors did not preserve per-model routing");
        var mismatchedPlan = plan with { AgentId = "ALPHA" };
        var mismatchedLegacy = TurnRunnerService.BuildPrompt(snapshot, mismatchedPlan);
        var mismatchedPrepared = PreparedTurnContext.Create(
            snapshot,
            mismatchedPlan,
            beforeTurn: null,
            preparedAt);
        var mismatchedOptimized = TurnRunnerService.BuildPrompt(
            snapshot,
            mismatchedPlan,
            preparedTurnContext: mismatchedPrepared);
        Require(MessagesEqual(mismatchedLegacy, mismatchedOptimized)
            && mismatchedOptimized.All(message =>
                !message.Content.Contains("FROZEN_PRIVATE_MEMORY_SENTINEL", StringComparison.Ordinal)),
            "prepared selected-agent lookup changed legacy case-sensitive prompt behavior");

        // Replace, rather than append, to simulate an in-place same-revision
        // mutation. Prepared transcript/memory are deliberate frozen values.
        alpha.Name = "MUTATED_ALPHA_NAME";
        alpha.Persona = "MUTATED_PERSONA_MUST_NOT_LEAK";
        alpha.VoiceStyle = "bullet_only";
        alpha.PressureProfile = "contrarian";
        snapshot.Engine.Agents.First(agent => agent.Id == "beta").Active = false;
        snapshot.Engine.Steering.Topic = "MUTATED_TOPIC_MUST_NOT_LEAK";
        snapshot.Engine.Steering.Global = "MUTATED_GLOBAL_MUST_NOT_LEAK";
        snapshot.Engine.Internet.UseInternet = !snapshot.Engine.Internet.UseInternet;
        snapshot.Engine.Internet.MaxResults = 1;
        snapshot.Engine.TranscriptWindow = 1;
        snapshot.Engine.NotesWindow = 0;
        snapshot.Engine.RivalryMatrix.Enabled = true;
        snapshot.Engine.RivalryMatrix.Links.Add(new RivalryLink
        {
            Source = "alpha",
            Target = "beta",
            Stance = "challenge"
        });
        snapshot.Engine.Messages[0] = Message(1, "operator", "MUTATED_TRANSCRIPT_MUST_NOT_LEAK");
        snapshot.Engine.Messages[2] = new DialogueMessage
        {
            MessageId = "prepared-message-3",
            Turn = 99,
            SpeakerId = "beta",
            Speaker = "Beta",
            Text = "MUTATED_TURN_AND_STATUS_MUST_NOT_LEAK",
            Status = "error",
            Kind = "message",
            CreatedAt = 3
        };
        snapshot.Engine.Messages[4] = Message(
            5,
            "alpha",
            "MUTATED_FALLBACK_ANCHOR",
            "fallback-native",
            "resp_late_mutation");
        memory.Text = "MUTATED_MEMORY_MUST_NOT_LEAK";
        var frozenAgain = TurnRunnerService.BuildPrompt(
            snapshot,
            plan,
            preparedTurnContext: prepared);
        Require(MessagesEqual(optimized, frozenAgain)
            && frozenAgain.All(message => !message.Content.Contains("MUTATED_", StringComparison.Ordinal)),
            "same-revision transcript, memory, agent, steering, rivalry, internet, or window mutation changed frozen provider bytes");
        var frozenFallback = runner.BuildPromptForConfig(
            snapshot,
            plan,
            preparedFallbackConfig,
            beforeTurn: null,
            allowInternetTool: true,
            enforceVoiceDrift: false,
            preparedTurnContext: prepared);
        var frozenToolContinuation = runner.BuildPromptForConfig(
            snapshot,
            plan,
            preparedPrimaryConfig,
            beforeTurn: null,
            allowInternetTool: false,
            enforceVoiceDrift: false,
            extraUserMessage: evidence,
            preparedTurnContext: prepared);
        Require(MessagesEqual(expectedFallback.Messages, frozenFallback.Messages)
            && MessagesEqual(expectedToolContinuation.Messages, frozenToolContinuation.Messages),
            "fallback or tool/repair continuation rebuilt its base prompt from mutable snapshot state");
        var preparedFallback = TurnRunnerService.WithNativeContinuation(
            fallback,
            snapshot,
            plan.AgentId,
            beforeTurn: null,
            prepared);
        var legacyFallback = TurnRunnerService.WithNativeContinuation(
            fallback,
            snapshot,
            plan.AgentId,
            beforeTurn: null);
        Require(string.IsNullOrEmpty(preparedFallback.PreviousResponseId)
            && legacyFallback.PreviousResponseId == "resp_late_mutation",
            "a prepared known-null native anchor rescanned mutable transcript state");

        snapshot.PersistenceRevision++;
        RequireThrows<InvalidOperationException>(
            () => prepared.EnsureScope(snapshot, beforeTurn: null),
            "a changed persistence revision reused stale prepared context");

        Console.WriteLine(
            "PREPARED_TURN_EQUIVALENCE exact_provider_bytes=true frozen_transcript_text_turn_status=true " +
            "frozen_memory_agent_steering_rivalry_internet_windows=true " +
            "fallback_tool_repair_base=true case_sensitive_agent_equivalence=true " +
            "known_null_anchor_rescan=false stale_revision_rejected=true");
    }

    public static void RecordsSinglePreparationPassAndFixedExpiryClock()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.PersistenceRevision = 23;
        snapshot.Engine.Messages.Clear();
        for (var turn = 1; turn <= 2_000; turn++)
        {
            snapshot.Engine.Messages.Add(Message(
                turn,
                turn % 7 == 0 ? "operator" : turn % 2 == 0 ? "alpha" : "beta",
                $"ROW_{turn:D4}_{new string('x', 32)}"));
        }

        snapshot.Engine.TurnCount = 2_000;
        var alpha = snapshot.Engine.Agents.First(agent => agent.Id == "alpha");
        var now = DateTimeOffset.Parse("2031-02-03T04:05:06Z");
        StructuredMemoryService.AddManualMemory(
            snapshot,
            alpha,
            "expires across wall-clock boundary",
            StructuredMemoryVisibilities.Private,
            now,
            now.AddSeconds(1));
        var primary = NativeConfig("primary-native");
        var fallback = NativeConfig("fallback-native");
        var plan = new OneTurnPlan(true, "alpha", "Alpha", primary, fallback, "");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var prepared = PreparedTurnContext.Create(snapshot, plan, beforeTurn: null, now);
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var receipt = prepared.Diagnostics;
        Require(receipt.MessagesVisited == 2_000
            && receipt.TranscriptSourcePasses == 1
            && receipt.MemoryNormalizationPasses == 1
            && receipt.MemorySelectionPasses == 1
            && receipt.NativeConfigurationsPrepared == 2,
            "prepared context did not project transcript, memory, and native routes in one bounded preparation");
        Require(prepared.ScopedMemory.Any(entry => entry.Text.Contains("expires across", StringComparison.Ordinal)),
            "fixed preparation clock expired memory too early");

        var afterExpiry = PreparedTurnContext.Create(snapshot, plan, beforeTurn: null, now.AddSeconds(2));
        Require(afterExpiry.ScopedMemory.All(entry => !entry.Text.Contains("expires across", StringComparison.Ordinal)),
            "a later logical completion reused memory past its expiry");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        RequireThrows<OperationCanceledException>(
            () => PreparedTurnContext.Create(snapshot, plan, beforeTurn: null, now, cancelled.Token),
            "prepared context ignored caller cancellation before projection");

        Console.WriteLine(
            "BASELINE_RECEIPT prepared_turn route=primary_fallback transcript_scans=8 " +
            "native_anchor_scans=6 memory_selections=4 memory_normalizations=4 minimum_message_traversals=22");
        Console.WriteLine(
            $"OPTIMIZED_RECEIPT prepared_turn messages={receipt.MessagesVisited} " +
            $"transcript_source_passes={receipt.TranscriptSourcePasses} " +
            $"memory_normalizations={receipt.MemoryNormalizationPasses} " +
            $"memory_selections={receipt.MemorySelectionPasses} " +
            $"native_configs={receipt.NativeConfigurationsPrepared} " +
            $"allocated_bytes={allocatedBytes} elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} " +
            "fixed_clock=true cancellation=true");
    }

    private static ModelProviderConfig NativeConfig(string model) => new()
    {
        BaseUrl = "http://127.0.0.1:1234/api/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        Model = model,
        NativeStatefulChat = true,
        HistoryPolicy = ModelHistoryPolicies.Strict,
        MaxOutputTokens = 256,
        ContextLength = 16_384,
        ConfiguredContextWindow = 16_384
    };

    private static DialogueMessage Message(
        int turn,
        string speakerId,
        string text,
        string model = "",
        string responseId = "")
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(responseId))
        {
            metadata["provider_response_id"] = JsonSerializer.SerializeToElement(responseId);
        }

        return new DialogueMessage
        {
            MessageId = $"prepared-message-{turn}",
            Turn = turn,
            SpeakerId = speakerId,
            Speaker = char.ToUpperInvariant(speakerId[0]) + speakerId[1..],
            Text = text,
            Status = "ok",
            Kind = "message",
            CreatedAt = turn,
            Model = new ModelMetadata { Model = model },
            Metadata = metadata
        };
    }

    private static bool MessagesEqual(
        IReadOnlyList<ModelChatMessage> left,
        IReadOnlyList<ModelChatMessage> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Role == pair.Second.Role
            && pair.First.Content == pair.Second.Content);

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
