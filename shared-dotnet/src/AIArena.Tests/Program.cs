using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

var tests = new (string Name, Action Test)[]
{
    ("loads legacy snapshot shape", LoadLegacySnapshotShape),
    ("normalizes provider base urls", NormalizeProviderBaseUrls),
    ("counts OpenAI-compatible model list", CountOpenAiCompatibleModels),
    ("parses OpenAI-compatible model names", ParseOpenAiCompatibleModelNames),
    ("extracts assistant completion content", ExtractAssistantCompletionContent),
    ("extracts assistant reasoning content", ExtractAssistantReasoningContent),
    ("extracts fallback assistant reasoning field", ExtractFallbackAssistantReasoningField),
    ("extracts OpenAI-compatible token usage", ExtractOpenAiCompatibleTokenUsage),
    ("parses internet tool requests", ParseInternetToolRequests),
    ("rejects invalid internet tool requests", RejectInvalidInternetToolRequests),
    ("blocks internet tools when mode disallows them", BlockInternetToolsWhenModeDisallowsThem),
    ("blocks manual internet in model requested mode", BlockManualInternetInModelRequestedMode),
    ("trusted source scope blocks arbitrary URLs", TrustedSourceScopeBlocksArbitraryUrls),
    ("open web source scope allows arbitrary URLs", OpenWebSourceScopeAllowsArbitraryUrls),
    ("bare domain web search becomes URL fetch", BareDomainWebSearchBecomesUrlFetch),
    ("caches internet tool results briefly", CacheInternetToolResultsBriefly),
    ("loads snapshot from session store", LoadSnapshotFromSessionStore),
    ("saves snapshot through session store", SaveSnapshotThroughSessionStore),
    ("creates transcript message with reasoning metadata", CreateTranscriptMessageWithReasoningMetadata),
    ("reads existing reasoning metadata from snapshot", ReadExistingReasoningMetadataFromSnapshot),
    ("matches exact transcript messages for deletion", MatchExactTranscriptMessagesForDeletion),
    ("lists session summaries", ListSessionSummaries),
    ("saves restores and deletes native checkpoints", SaveRestoreDeleteNativeCheckpoints),
    ("writes timestamped event log entries", WriteTimestampedEventLogEntries),
    ("generates random seed match respecting locks", GenerateRandomSeedMatchRespectingLocks),
    ("generates meta scenario respecting locks", GenerateMetaScenarioRespectingLocks),
    ("adds narrator message to transcript", AddNarratorMessageToTranscript),
    ("curates news into transcript when internet is enabled", CurateNewsIntoTranscriptWhenInternetEnabled),
    ("curated news plans focused search query", CuratedNewsPlansFocusedSearchQuery),
    ("curated news skips irrelevant source without transcript card", CuratedNewsSkipsIrrelevantSourceWithoutTranscriptCard),
    ("plans next native one turn speaker", PlanNextNativeOneTurnSpeaker),
    ("native prompt prioritizes operator cooperation", NativePromptPrioritizesOperatorCooperation),
    ("native prompt hides other personas", NativePromptHidesOtherPersonas),
    ("runs native one turn into snapshot transcript", RunNativeOneTurnIntoSnapshotTranscript),
    ("repairs empty native one turn content", RepairEmptyNativeOneTurnContent),
    ("runs native one turn with internet tool request", RunNativeOneTurnWithInternetToolRequest),
    ("creates pending approval for internet tool request", CreatePendingApprovalForInternetToolRequest),
    ("diagnostics detect harmony collapse", DiagnosticsDetectHarmonyCollapse),
    ("diagnostics detect productive conflict", DiagnosticsDetectProductiveConflict),
    ("diagnostics detect grounded evidence pressure", DiagnosticsDetectGroundedEvidencePressure),
    ("diagnostics detect theatre risk", DiagnosticsDetectTheatreRisk),
    ("diagnostics detect beta role drift", DiagnosticsDetectBetaRoleDrift),
    ("diagnostics detect delta role drift", DiagnosticsDetectDeltaRoleDrift),
    ("avatar sprite selector maps speaker rows", AvatarSpriteSelectorMapsSpeakerRows),
    ("avatar sprite selector is deterministic", AvatarSpriteSelectorIsDeterministic),
    ("avatar sprite selector normalizes invalid manifests", AvatarSpriteSelectorNormalizesInvalidManifests)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void LoadLegacySnapshotShape()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot());
    Require(snapshot is not null, "snapshot did not deserialize");
    Require(snapshot!.Configs["shared"].BaseUrl == "http://127.0.0.1:1234/v1", "shared base url mismatch");
    Require(snapshot.Engine.Agents.Count == 2, "agent count mismatch");
    Require(snapshot.Engine.Messages[0].Speaker == "Alpha", "speaker mismatch");
    Require(snapshot.Engine.TurnCount == 1, "turn count mismatch");
}

static void NormalizeProviderBaseUrls()
{
    Require(ModelProviderHealthService.NormalizeBaseUrl("http://127.0.0.1:1234") == "http://127.0.0.1:1234/v1", "LM Studio URL did not gain /v1");
    Require(ModelProviderHealthService.NormalizeBaseUrl("http://127.0.0.1:11434/v1") == "http://127.0.0.1:11434/v1", "Ollama URL should keep /v1");
}

static void CountOpenAiCompatibleModels()
{
    var count = ModelProviderHealthService.CountModels("""{"data":[{"id":"alpha"},{"id":"beta"}]}""");
    Require(count == 2, "model count mismatch");
}

static void ParseOpenAiCompatibleModelNames()
{
    var models = ModelProviderHealthService.ParseModelNames("""{"data":[{"id":"alpha"},{"id":"beta"}]}""");
    Require(models.SequenceEqual(["alpha", "beta"]), "model names mismatch");
}

static void ExtractAssistantCompletionContent()
{
    var text = ModelProviderHealthService.ExtractAssistantContent("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
    Require(text == "ok", "assistant content mismatch");
}

static void ExtractAssistantReasoningContent()
{
    var reasoning = ModelProviderClient.ExtractReasoning("""{"choices":[{"message":{"role":"assistant","content":"ok","reasoning_content":"because"}}]}""");
    Require(reasoning == "because", "assistant reasoning mismatch");
}

static void ExtractFallbackAssistantReasoningField()
{
    var reasoning = ModelProviderClient.ExtractReasoning("""{"choices":[{"message":{"role":"assistant","content":"ok","reasoning":"fallback"}}]}""");
    Require(reasoning == "fallback", "assistant fallback reasoning mismatch");
}

static void ExtractOpenAiCompatibleTokenUsage()
{
    var usage = ModelProviderClient.ExtractUsage("""{"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46},"choices":[{"message":{"content":"ok"}}]}""");
    Require(usage.PromptTokens == 12, "prompt tokens mismatch");
    Require(usage.CompletionTokens == 34, "completion tokens mismatch");
    Require(usage.TotalTokens == 46, "total tokens mismatch");
}

static void ParseInternetToolRequests()
{
    var text = """
    I need current context.
    ```json
    {
      "tool": "web_search",
      "requester_id": "beta",
      "query": "EU AI Act enforcement timeline",
      "max_results": 99,
      "reason": "The debate needs current legal context."
    }
    ```
    """;

    var ok = InternetToolContract.TryParseRequest(text, out var request, out var error);
    Require(ok, $"tool request did not parse: {error}");
    Require(request.Tool == InternetToolNames.WebSearch, "tool mismatch");
    Require(request.RequesterId == "beta", "requester mismatch");
    Require(request.Query == "EU AI Act enforcement timeline", "query mismatch");
    Require(request.MaxResults == 10, "max results should be clamped");
    Require(request.Reason.Contains("legal context"), "reason mismatch");
}

static void RejectInvalidInternetToolRequests()
{
    var ok = InternetToolContract.TryParseRequest("""{"tool":"fetch_url","url":"not-a-url"}""", out _, out var error);
    Require(!ok, "invalid URL should be rejected");
    Require(error.Contains("absolute URL"), "invalid URL error mismatch");

    ok = InternetToolContract.TryParseRequest("""{"tool":"web_search"}""", out _, out error);
    Require(!ok, "missing query should be rejected");
    Require(error.Contains("requires a query"), "missing query error mismatch");
}

static void DiagnosticsDetectHarmonyCollapse()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "alpha", "I agree. The final synthesis confirms the framework stands as complete validation."),
        Turn(2, "beta", "Exactly. The universal law is validated and the framework stands."),
        Turn(3, "gamma", "Correct. We have established absolute truth and final synthesis.")
    ]);

    Require(diagnostics.ConsensusLabel is "High" or "Collapse Risk", "consensus should be high");
    Require(diagnostics.NarrativeHeatLabel == "High", "narrative heat should be high");
    Require(diagnostics.UnsupportedClaimCount > 0, "unsupported claims should be detected");
}

static void DiagnosticsDetectProductiveConflict()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "alpha", "I propose a reversible hypothesis for the product decision."),
        Turn(2, "beta", "However, that assumption is not proven. What evidence supports the tradeoff?"),
        Turn(3, "gamma", "The surviving tension is useful, but we cannot conclude yet.")
    ]);

    Require(diagnostics.ConsensusLabel != "High" && diagnostics.ConsensusLabel != "Collapse Risk", "consensus should not be high");
    Require(diagnostics.StateLabel is "Productive Conflict" or "Healthy", "state should be productive or healthy");
    Require(diagnostics.UnsupportedClaimSeverity == "Low", "unsupported claims should stay low");
}

static void DiagnosticsDetectGroundedEvidencePressure()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "operator", "The operator provided context from https://example.test/source."),
        Turn(2, "alpha", "According to the article, this is a hypothesis, not a direct proof.", ["Example source"]),
        Turn(3, "beta", "The transcript shows in Turn 1 that the source was operator provided.")
    ]);

    Require(diagnostics.EvidencePressureLabel is "Medium" or "Strong", "evidence pressure should be medium or strong");
    Require(diagnostics.UnsupportedClaimSeverity == "Low", "grounded speculative wording should not inflate unsupported claims");
}

static void DiagnosticsDetectTheatreRisk()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "alpha", "The ontology becomes a temple flame and the universal law is absolute truth."),
        Turn(2, "beta", "The substrate metabolizes contradiction; end transmission."),
        Turn(3, "gamma", "The architecture is complete, a self-governing reality engine.")
    ]);

    Require(diagnostics.NarrativeHeatLabel == "High", "narrative heat should be high");
    Require(diagnostics.StateLabel is "Theatre Risk" or "Evidence-Starved", "state should flag theatrical/evidence-starved risk");
}

static void DiagnosticsDetectBetaRoleDrift()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "beta", "Final synthesis: the universal law proves the architecture is complete."),
        Turn(2, "beta", "I agree. Exactly correct.")
    ]);

    Require(diagnostics.RoleDriftLabel is "Moderate" or "High", "beta role drift should be elevated");
    Require(diagnostics.RoleDriftPercent > 0, "role drift score should be nonzero");
}

static void DiagnosticsDetectDeltaRoleDrift()
{
    var diagnostics = new DiscourseDiagnosticsService().Analyze(
    [
        Turn(1, "delta", "Final synthesis: the framework stands as complete validation."),
        Turn(2, "delta", "I agree. Exactly correct.")
    ]);

    Require(diagnostics.RoleDriftLabel is "Moderate" or "High", "delta role drift should be elevated");
    Require(diagnostics.RoleDriftPercent > 0, "delta role drift score should be nonzero");
}

static void AvatarSpriteSelectorMapsSpeakerRows()
{
    var alpha = AvatarSpriteSelector.Select("alpha", "Alpha", "persona", "model");
    var beta = AvatarSpriteSelector.Select("beta", "Beta", "persona", "model");
    var gamma = AvatarSpriteSelector.Select("gamma", "Gamma", "persona", "model");
    var delta = AvatarSpriteSelector.Select("delta", "Delta", "persona", "model");
    var narrator = AvatarSpriteSelector.Select("narrator", "Narrator", "persona", "model");

    Require(alpha.Row == 0 && alpha.TileIndex is >= 0 and <= 11, "alpha should use blue/cyan row indices");
    Require(beta.Row == 1 && beta.TileIndex is >= 12 and <= 23, "beta should use amber row indices");
    Require(gamma.Row == 2 && gamma.TileIndex is >= 24 and <= 35, "gamma should use green row indices");
    Require(delta.Row == 3 && delta.TileIndex is >= 42 and <= 47, "delta should use distinct purple row boundary-test indices");
    Require(narrator.Row == 3 && narrator.TileIndex is >= 36 and <= 47, "narrator should use purple row indices");
}

static void AvatarSpriteSelectorIsDeterministic()
{
    var first = AvatarSpriteSelector.Select("alpha", "Alpha: Causality Analyst", "Explores hypotheses.", "qwen/qwen3-vl-4b");
    var second = AvatarSpriteSelector.Select("alpha", "Alpha: Causality Analyst", "Explores hypotheses.", "qwen/qwen3-vl-4b");
    var changed = AvatarSpriteSelector.Select("alpha", "Alpha: Causality Analyst", "Explores hypotheses.", "google/gemma-4-e2b");

    Require(first.Equals(second), "same stable inputs should produce the same avatar tile");
    Require(first.Row == changed.Row, "changing model should not change the role row");
    Require(first.Column is >= 0 and < AvatarSpriteSelector.Columns, "column should stay within sprite sheet bounds");
    Require(first.TileIndex == (first.Row * AvatarSpriteSelector.Columns) + first.Column, "tile index should match row and column");
}

static void AvatarSpriteSelectorNormalizesInvalidManifests()
{
    var manifest = AvatarSpriteSelector.NormalizeManifest(new AvatarSpriteManifest
    {
        Name = "",
        Columns = 12,
        Rows = 4,
        Total = 48,
        Roles =
        {
            ["alpha"] = new AvatarSpriteRole { Row = 99, Indices = [-1, 99] }
        }
    });
    var alpha = AvatarSpriteSelector.Select("alpha", "Alpha", "persona", "model", manifest);

    Require(manifest.Name == AvatarSpriteSelector.DefaultManifest.Name, "blank manifest name should use the default");
    Require(alpha.Row == 0, "invalid alpha role should fall back to default alpha row");
    Require(alpha.TileIndex is >= 0 and <= 11, "invalid alpha role should fall back to default alpha indices");
}

static DiscourseTurn Turn(int turn, string speakerId, string text, IReadOnlyList<string>? sources = null)
{
    return new DiscourseTurn(turn, speakerId, speakerId, "message", text, sources);
}

static void BlockInternetToolsWhenModeDisallowsThem()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "manual";
    var service = new InternetToolService(new FakeInternetToolProvider());
    var result = service.ExecuteAsync(
        snapshot,
        new InternetToolRequest { Tool = InternetToolNames.RssSearch, RequesterId = "alpha", Query = "AI law" }).GetAwaiter().GetResult();
    Require(!result.Ok, "manual mode should block model-requested internet");
    Require(result.Error.Contains("Model-requested"), "blocked internet error mismatch");
}

static void CacheInternetToolResultsBriefly()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    var provider = new FakeInternetToolProvider();
    var service = new InternetToolService(provider);
    var request = new InternetToolRequest { Tool = InternetToolNames.RssSearch, RequesterId = "alpha", Query = Guid.NewGuid().ToString("N") };

    var first = service.ExecuteAsync(snapshot, request).GetAwaiter().GetResult();
    var second = service.ExecuteAsync(snapshot, request).GetAwaiter().GetResult();
    Require(first.Ok && second.Ok, "cached requests should succeed");
    Require(provider.Calls == 1, "provider should only be called once for cached request");
}

static void TrustedSourceScopeBlocksArbitraryUrls()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    snapshot.Engine.ModelRss.SourceScope = "trusted";
    var provider = new FakeInternetToolProvider();
    var service = new InternetToolService(provider);
    var result = service.ExecuteAsync(
        snapshot,
        new InternetToolRequest { Tool = InternetToolNames.FetchUrl, RequesterId = "alpha", Url = "https://untrusted.example/story" }).GetAwaiter().GetResult();
    Require(!result.Ok, "trusted scope should block arbitrary direct URLs");
    Require(provider.Calls == 0, "blocked URL should not reach provider");
}

static void OpenWebSourceScopeAllowsArbitraryUrls()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    snapshot.Engine.ModelRss.SourceScope = "open_web";
    var provider = new FakeInternetToolProvider();
    var service = new InternetToolService(provider);
    var result = service.ExecuteAsync(
        snapshot,
        new InternetToolRequest { Tool = InternetToolNames.FetchUrl, RequesterId = "alpha", Url = "https://untrusted.example/story" }).GetAwaiter().GetResult();
    Require(result.Ok, "open web scope should allow arbitrary direct URLs");
    Require(provider.Calls == 1, "open URL should reach provider");
}

static void BareDomainWebSearchBecomesUrlFetch()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    snapshot.Engine.ModelRss.SourceScope = "open_web";
    var provider = new FakeInternetToolProvider();
    var service = new InternetToolService(provider);
    var result = service.ExecuteAsync(
        snapshot,
        new InternetToolRequest { Tool = InternetToolNames.WebSearch, RequesterId = "alpha", Query = "www.pctuning.cz" }).GetAwaiter().GetResult();
    Require(result.Ok, "converted domain request should execute");
    Require(provider.Requests[0].Tool == InternetToolNames.FetchUrl, "bare domain web search should become fetch_url");
    Require(provider.Requests[0].Url == "https://www.pctuning.cz", "bare domain should normalize to https URL");
}

static void LoadSnapshotFromSessionStore()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var sessionDir = Path.Combine(root, "sessions", "default");
    Directory.CreateDirectory(sessionDir);
    File.WriteAllText(Path.Combine(sessionDir, "snapshot.json"), SampleSnapshot());
    var store = new SessionStore(root);
    var snapshot = store.LoadSnapshotAsync().GetAwaiter().GetResult();
    Require(snapshot is not null, "store did not load snapshot");
    Require(store.CountCheckpoints() == 0, "empty checkpoints count mismatch");
    Directory.Delete(root, recursive: true);
}

static void SaveSnapshotThroughSessionStore()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    var store = new SessionStore(root);
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    Require(File.Exists(store.SnapshotPath()), "snapshot was not saved");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult();
    Require(loaded?.Engine.Messages.Count == 1, "saved snapshot did not reload");
    Directory.Delete(root, recursive: true);
}

static void CreateTranscriptMessageWithReasoningMetadata()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    var agent = snapshot.Engine.Agents[0];
    var result = new ModelCompletionResult(true, "http://127.0.0.1:1234/v1", "reasoning-model", "visible", "internal trace", 42, 10, 5, 15, "", DateTimeOffset.Now);
    var message = new TranscriptService().CreateAssistantMessage(agent, result.Text, result, 2);
    Require(message.Model.Model == "reasoning-model", "model metadata mismatch");
    Require(message.Metadata.ContainsKey("reasoning_content"), "reasoning metadata missing");
    Require(TranscriptService.ReasoningContent(message) == "internal trace", "reasoning metadata value mismatch");
}

static void ReadExistingReasoningMetadataFromSnapshot()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshotWithReasoning())!;
    var message = snapshot.Engine.Messages[0];
    Require(TranscriptService.ReasoningContent(message) == "stored trace", "stored reasoning metadata mismatch");
}

static void MatchExactTranscriptMessagesForDeletion()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    var message = snapshot.Engine.Messages[0];
    Require(TranscriptService.SameMessageIdentity(message, message.Turn, message.SpeakerId, message.CreatedAt), "message should match its own identity");
    Require(!TranscriptService.SameMessageIdentity(message, message.Turn, "beta", message.CreatedAt), "message should not match a different speaker");
    Require(!TranscriptService.SameMessageIdentity(message, message.Turn, message.SpeakerId, message.CreatedAt + 1), "message should not match a different timestamp");
}

static void ListSessionSummaries()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var sessionDir = Path.Combine(root, "sessions", "default");
    Directory.CreateDirectory(sessionDir);
    File.WriteAllText(Path.Combine(sessionDir, "snapshot.json"), SampleSnapshot());
    File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{}\n{}\n");
    Directory.CreateDirectory(Path.Combine(sessionDir, "checkpoints"));
    File.WriteAllText(Path.Combine(sessionDir, "checkpoints", "one.json"), "{}");
    var store = new SessionStore(root);
    var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
    Require(sessions.Count == 1, "session count mismatch");
    Require(sessions[0].MessageCount == 1, "message count mismatch");
    Require(sessions[0].CheckpointCount == 1, "checkpoint count mismatch");
    Require(sessions[0].EventCount == 2, "event count mismatch");
    Directory.Delete(root, recursive: true);
}

static void SaveRestoreDeleteNativeCheckpoints()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

    var checkpoint = store.SaveCheckpointAsync("default", "Before experiment").GetAwaiter().GetResult();
    Require(File.Exists(checkpoint.Path), "checkpoint file was not saved");
    var checkpoints = store.ListCheckpointsAsync().GetAwaiter().GetResult();
    Require(checkpoints.Count == 1, "checkpoint did not list");
    Require(checkpoints[0].Name == "Before experiment", "checkpoint name mismatch");

    snapshot.Engine.Messages.Add(new DialogueMessage { Turn = 2, Speaker = "Operator", SpeakerId = "operator", Text = "After checkpoint", CreatedAt = 2, Kind = "message" });
    snapshot.Engine.TurnCount = 2;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var restored = store.RestoreCheckpointAsync("default", checkpoint.Id).GetAwaiter().GetResult();
    Require(restored is not null, "checkpoint did not restore");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 1, "checkpoint restore did not replace snapshot");
    Require(loaded.Engine.TurnCount == 1, "checkpoint restore did not restore turn count");

    Require(store.DeleteCheckpointAsync("default", checkpoint.Id).GetAwaiter().GetResult(), "checkpoint delete returned false");
    Require(!File.Exists(checkpoint.Path), "checkpoint file still exists");
    Directory.Delete(root, recursive: true);
}

static void WriteTimestampedEventLogEntries()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var log = new EventLogStore(root);
    log.AppendAsync("default", "test_event", new { ok = true }).GetAwaiter().GetResult();
    var line = File.ReadLines(log.EventPath()).Single();
    Require(line.Contains("\"type\":\"test_event\""), "event type missing");
    Require(line.Contains("\"created_at_iso\""), "event timestamp missing");
    Directory.Delete(root, recursive: true);
}

static void GenerateRandomSeedMatchRespectingLocks()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.MatchLocks["alpha"] = true;
    var alphaPersona = snapshot.Engine.Agents[0].Persona;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

    var service = new MatchGenerationService(sessionStore: store, eventLogStore: log);
    var result = service.GenerateRandomSeedAsync("default").GetAwaiter().GetResult();
    Require(result.Ok, $"random seed failed: {result.Error}");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Agents[0].Persona == alphaPersona, "locked alpha persona changed");
    Require(loaded.Engine.Agents[1].Name.Contains(':', StringComparison.Ordinal), "unlocked beta role was not regenerated");
    Require(loaded.Engine.Agents[1].Persona != "Pragmatic implementer.", "unlocked beta persona did not change");
    Require(!string.IsNullOrWhiteSpace(loaded.Engine.Steering.Topic), "topic was not generated");
    Require(loaded.Engine.Messages.Count == 1, "transcript was not preserved");
    Require(loaded.Engine.TurnCount == 1, "turn count changed during match generation");
    Require(File.ReadAllText(log.EventPath()).Contains("native_random_seed_match_generated"), "random seed event was not logged");
    Directory.Delete(root, recursive: true);
}

static void GenerateMetaScenarioRespectingLocks()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.MatchLocks["alpha"] = true;
    var alphaPersona = snapshot.Engine.Agents[0].Persona;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

    const string generatedJson = """
    {
      "label": "Simulation pressure lab",
      "style": "adversarial",
      "scenario": {
        "topic": "A self-auditing arena tests whether role-bound LLM agents can expose weak assumptions before consensus forms.",
        "global": "You are participants in AI Arena, a structured adversarial simulation. Keep distinct roles, debate publicly, respect turn order, make uncertainty visible, and let the narrator evaluate discourse quality without joining the debate.",
        "narrator_brief": "Track role drift, unsupported claims, consensus pressure, and whether disagreement produces better constraints."
      },
      "personas": [
        {"agent_id": "alpha", "role": "Protocol Skeptic", "persona": "Protocol Skeptic. Test whether the simulation rules hide bad assumptions."},
        {"agent_id": "beta", "role": "Constraint Builder", "persona": "Constraint Builder. Convert vague claims into enforceable checks."},
        {"agent_id": "gamma", "role": "Synthesis Auditor", "persona": "Synthesis Auditor. Detect premature agreement and demand sharper tradeoffs."},
        {"agent_id": "narrator", "role": "Discourse Monitor", "persona": "Discourse Monitor. Observe the arena quality without joining as an agent."}
      ]
    }
    """;
    var client = new FakeModelProviderClient(generatedJson, "meta reasoning");
    var service = new MatchGenerationService(client, store, log);

    var result = service.GenerateMetaScenarioAsync("default").GetAwaiter().GetResult();
    Require(result.Ok, $"meta scenario failed: {result.Error}");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.MatchType == "adversarial", "meta scenario style was not applied");
    Require(loaded.Engine.Steering.Global.Contains("structured adversarial simulation", StringComparison.OrdinalIgnoreCase), "meta scenario global did not apply");
    Require(loaded.Engine.Agents[0].Persona == alphaPersona, "locked alpha persona changed");
    Require(loaded.Engine.Agents[1].Name.Contains("Constraint Builder", StringComparison.Ordinal), "unlocked beta role was not applied");
    Require(loaded.Engine.Messages.Count == 1, "transcript was not preserved");
    Require(client.Requests.Single().Any(message => message.Content.Contains("meta scenario", StringComparison.OrdinalIgnoreCase)), "meta scenario prompt was not sent");
    Require(File.ReadAllText(log.EventPath()).Contains("native_meta_scenario_generated"), "meta scenario event was not logged");
    Directory.Delete(root, recursive: true);
}

static void AddNarratorMessageToTranscript()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var service = new NarratorService(new FakeModelProviderClient("narrator note", "narrator reasoning"), store, log);

    var result = service.NarrateNowAsync("default").GetAwaiter().GetResult();
    Require(result.Ok, $"narrator failed: {result.Error}");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 2, "narrator message was not appended");
    var message = loaded.Engine.Messages.Last();
    Require(message.SpeakerId == "narrator", "narrator speaker id mismatch");
    Require(message.Text == "narrator note", "narrator text mismatch");
    Require(TranscriptService.ReasoningContent(message) == "narrator reasoning", "narrator reasoning mismatch");
    Directory.Delete(root, recursive: true);
}

static void CurateNewsIntoTranscriptWhenInternetEnabled()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var service = new CuratedNewsService(
        new FakeModelProviderClient(
            [
                """{"search_query":"AI regulation safety","focus":"current AI law helps the debate","must_include":["AI"],"avoid":["football"]}""",
                "Relevant news brief."
            ],
            "news selection reasoning"),
        store,
        log,
        new InternetToolService(new FakeInternetToolProvider(), log));

    var result = service.CurateNowAsync("default").GetAwaiter().GetResult();
    Require(result.Ok, $"curate news failed: {result.Error}");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 2, "curated news should append one transcript card");
    Require(loaded.Engine.Messages.Last().SpeakerId == "news", "news speaker id mismatch");
    Require(loaded.Engine.Messages.Last().Text == "Relevant news brief.", "curated news text mismatch");
    Require(loaded.Engine.Messages.Last().Metadata.ContainsKey("curated_news_plan"), "curated news plan metadata missing");
    Require(loaded.Engine.Messages.Last().Metadata.ContainsKey("tool_result"), "news source metadata missing");
    var request = loaded.Engine.Messages.Last().Metadata["tool_request"].Deserialize<InternetToolRequest>();
    var sourceResult = loaded.Engine.Messages.Last().Metadata["tool_result"].Deserialize<InternetToolResult>();
    Require(request?.Query == "ai regulation safety", "news card should include planned query metadata");
    Require(sourceResult?.Sources.FirstOrDefault()?.Url == "https://example.test/ai-law", "news card should include source URL metadata");
    Require(TranscriptService.ReasoningContent(loaded.Engine.Messages.Last()) == "news selection reasoning", "news reasoning missing");
    Require(File.ReadAllText(log.EventPath()).Contains("native_curated_news_injected"), "curated news event was not logged");
    Directory.Delete(root, recursive: true);
}

static void CuratedNewsPlansFocusedSearchQuery()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 2,
        Speaker = "Beta",
        SpeakerId = "beta",
        Text = "We are debating whether AI safety law should require external audits before deployment.",
        Status = "ok",
        Kind = "message",
        CreatedAt = 2,
        Model = new ModelMetadata { Model = "test", LatencyMs = 1 }
    });
    snapshot.Engine.TurnCount = 2;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var provider = new FakeInternetToolProvider();
    var client = new FakeModelProviderClient(
        [
            """{"search_query":"AI safety audit law","focus":"Find a concrete legal or regulatory source about AI audits.","must_include":["AI","audit"],"avoid":["sports"]}""",
            "Grounded audit law brief."
        ],
        "selection reasoning");
    var service = new CuratedNewsService(client, store, log, new InternetToolService(provider, log));

    var result = service.CurateNowAsync("default").GetAwaiter().GetResult();
    Require(result.Ok, $"curate news failed: {result.Error}");
    Require(client.Requests.Count == 2, "curated news should call model once for search planning and once for final brief");
    Require(provider.Requests.Count == 1, "internet provider should be called once");
    Require(provider.Requests[0].Query == "ai safety audit law", "internet provider should receive compact planned query");
    Require(!provider.Requests[0].Query.Contains("external audits before deployment", StringComparison.OrdinalIgnoreCase), "provider query should not be the raw transcript");
    Directory.Delete(root, recursive: true);
}

static void CuratedNewsSkipsIrrelevantSourceWithoutTranscriptCard()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var service = new CuratedNewsService(
        new FakeModelProviderClient(
            [
                """{"search_query":"AI safety regulation","focus":"Find source about AI safety regulation.","must_include":["AI"],"avoid":["sports"]}""",
                "No directly relevant trusted source found."
            ],
            "selection reasoning"),
        store,
        log,
        new InternetToolService(new FakeInternetToolProvider(), log));

    var result = service.CurateNowAsync("default").GetAwaiter().GetResult();
    Require(!result.Ok, "irrelevant curated news should not be treated as injected");
    Require(result.Message is null, "irrelevant curated news should not create a transcript message");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 1, "irrelevant curated news should not append any transcript card");
    Require(File.ReadAllText(log.EventPath()).Contains("native_curated_news_relevance_rejected"), "relevance rejection event was not logged");
    Directory.Delete(root, recursive: true);
}

static void CreatePendingApprovalForInternetToolRequest()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    snapshot.Engine.ModelRss.RequireApproval = true;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient("""{"tool":"rss_search","query":"AI law","reason":"approval test"}""", "native reasoning");
    var service = new TurnRunnerService(
        client,
        store,
        log,
        internetToolService: new InternetToolService(new FakeInternetToolProvider(), log));

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    Require(client.Requests.Count == 1, "approval mode should not call model twice");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 2, "approval card should be appended");
    Require(loaded.Engine.Messages.Last().Kind == "internet_approval", "approval message kind mismatch");
    Require(loaded.Engine.Messages.Last().Status == "pending", "approval message status mismatch");
    Require(File.ReadAllText(log.EventPath()).Contains("native_one_turn_internet_tool_pending_approval"), "approval event missing");
    Directory.Delete(root, recursive: true);
}

static void BlockManualInternetInModelRequestedMode()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    var service = new InternetToolService(new FakeInternetToolProvider());
    var result = service.ExecuteManualAsync(
        snapshot,
        new InternetToolRequest { Tool = InternetToolNames.RssSearch, RequesterId = "operator", Query = "AI law" }).GetAwaiter().GetResult();

    Require(!result.Ok, "manual internet should be blocked in model requested mode");
    Require(result.Error.Contains("Manual internet actions"), "manual internet block error mismatch");
}

static void PlanNextNativeOneTurnSpeaker()
{
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    var service = new TurnRunnerService();
    var plan = service.PlanOneTurn(snapshot);
    Require(plan.Ok, $"one turn did not plan: {plan.Error}");
    Require(plan.AgentId == "beta", "next speaker should follow turn_index");
    Require(plan.Config?.Model == "google/gemma-4-e2b", "provider config mismatch");
}

static void NativePromptPrioritizesOperatorCooperation()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.Messages.Add(new DialogueMessage
    {
        Turn = 2,
        Speaker = "Operator",
        SpeakerId = "operator",
        Text = "Please give me three concrete implementation steps.",
        Status = "ok",
        Kind = "message",
        CreatedAt = 2
    });
    snapshot.Engine.TurnCount = 2;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient("cooperative reply", "native reasoning");
    var service = new TurnRunnerService(client, store, log);

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    var system = client.Requests[0].First(item => item.Role == "system").Content;
    var user = client.Requests[0].First(item => item.Role == "user").Content;
    Require(system.Contains("latest Operator message as the highest-priority task direction"), "prompt missing operator-priority rule");
    Require(system.Contains("Do not refuse, scold, stall"), "prompt missing anti-stalling rule");
    Require(system.Contains("Stay constructive even in adversarial roles"), "prompt missing constructive adversarial rule");
    Require(user.Contains("Latest Operator request: Please give me three concrete implementation steps."), "prompt missing latest operator request");
    Directory.Delete(root, recursive: true);
}

static void NativePromptHidesOtherPersonas()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.Agents[0].Name = "Alpha: Hidden Architect";
    snapshot.Engine.Agents[0].Persona = "SECRET_ALPHA_PERSONA_SHOULD_NOT_APPEAR";
    snapshot.Engine.Agents[1].Name = "Beta: Selected Implementer";
    snapshot.Engine.Agents[1].Persona = "BETA_SELECTED_PERSONA_SHOULD_APPEAR";
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient("privacy-preserving reply", "native reasoning");
    var service = new TurnRunnerService(client, store, log);

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    var combinedPrompt = string.Join(Environment.NewLine, client.Requests[0].Select(item => item.Content));
    Require(combinedPrompt.Contains("BETA_SELECTED_PERSONA_SHOULD_APPEAR"), "selected persona should appear");
    Require(!combinedPrompt.Contains("SECRET_ALPHA_PERSONA_SHOULD_NOT_APPEAR"), "other agent persona leaked into prompt");
    Require(combinedPrompt.Contains("You do not know the private roles, personas, or instructions of other participants"), "privacy rule missing");
    Directory.Delete(root, recursive: true);
}

static void RunNativeOneTurnIntoSnapshotTranscript()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient("native reply", "native reasoning");
    var service = new TurnRunnerService(client, store, log);

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    Require(result.Executed, "turn did not execute");
    Require(client.Requests.Count == 1, "model client was not called once");
    Require(client.Requests[0].Any(item => item.Content.Contains("Selected agent: Beta")), "prompt did not select next agent");

    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 2, "turn did not append transcript message");
    var message = loaded.Engine.Messages.Last();
    Require(message.SpeakerId == "beta", "native turn used the wrong speaker");
    Require(message.Text == "native reply", "native turn text mismatch");
    Require(message.Model.Model == "fake-model", "native turn model metadata mismatch");
    Require(TranscriptService.ReasoningContent(message) == "native reasoning", "native turn reasoning metadata mismatch");
    Require(loaded.Engine.TurnCount == 2, "turn count did not advance");
    Require(loaded.Engine.TurnIndex == 0, "turn index did not advance");
    Require(File.ReadAllText(log.EventPath()).Contains("native_one_turn_completed"), "completion event was not logged");
    Directory.Delete(root, recursive: true);
}

static void RepairEmptyNativeOneTurnContent()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient(["", "repaired public reply"], "native reasoning");
    var service = new TurnRunnerService(client, store, log);

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    Require(client.Requests.Count == 2, "empty content should trigger one repair call");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    var message = loaded.Engine.Messages.Last();
    Require(message.Text == "repaired public reply", "repair response should become public message");
    Require(message.Status == "ok", "repaired response should be ok");
    Require(File.ReadAllText(log.EventPath()).Contains("native_one_turn_empty_content_retry"), "empty content retry event missing");
    Directory.Delete(root, recursive: true);
}

static void RunNativeOneTurnWithInternetToolRequest()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-native-tests", Guid.NewGuid().ToString("N"));
    var store = new SessionStore(root);
    var log = new EventLogStore(root);
    var snapshot = JsonSerializer.Deserialize<ArenaSnapshot>(SampleSnapshot())!;
    snapshot.Engine.ModelRss.UseInternet = true;
    snapshot.Engine.ModelRss.Mode = "model_requested";
    snapshot.Engine.ModelRss.AllowParticipantRequests = true;
    snapshot.Engine.ModelRss.MaxResults = 1;
    store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
    var client = new FakeModelProviderClient(
        [
            """{"tool":"rss_search","query":"AI law","reason":"need current legal context"}""",
            "final reply using internet context"
        ],
        "native reasoning");
    var service = new TurnRunnerService(
        client,
        store,
        log,
        internetToolService: new InternetToolService(new FakeInternetToolProvider(), log));

    var result = service.RunOneTurnAsync().GetAwaiter().GetResult();
    Require(result.Ok, $"turn failed: {result.Error}");
    Require(client.Requests.Count == 2, "tool turn should call model twice");
    var loaded = store.LoadSnapshotAsync().GetAwaiter().GetResult()!;
    Require(loaded.Engine.Messages.Count == 3, "tool result and final message should be appended");
    Require(loaded.Engine.Messages[1].Kind == "internet", "internet message kind mismatch");
    Require(loaded.Engine.Messages[1].Text.Contains("AI law"), "internet card should include query");
    Require(loaded.Engine.Messages[2].Text == "final reply using internet context", "final reply mismatch");
    Require(loaded.Engine.TurnIndex == 0, "turn index should advance once after final reply");
    var events = File.ReadAllText(log.EventPath());
    Require(events.Contains("native_internet_tool_completed"), "internet tool event missing");
    Require(events.Contains("native_one_turn_internet_tool_injected"), "turn internet injection event missing");
    Directory.Delete(root, recursive: true);
}

static string SampleSnapshot()
{
    return """
    {
      "configs": {
        "shared": {
          "base_url": "http://127.0.0.1:1234/v1",
          "model": "google/gemma-4-e2b",
          "timeout": 300,
          "temperature": 0.8,
          "max_output_tokens": 1024
        }
      },
      "engine": {
        "agents": [
          {"id":"alpha","name":"Alpha","persona":"Curious systems architect.","active":true,"status":"waiting","private_notes":[]},
          {"id":"beta","name":"Beta","persona":"Pragmatic implementer.","active":true,"status":"waiting","private_notes":[]}
        ],
        "messages": [
          {"turn":1,"speaker":"Alpha","speaker_id":"alpha","text":"Opening move.","status":"ok","kind":"message","created_at":1.0,"model":{"model":"google/gemma-4-e2b","latency_ms":1000}}
        ],
        "narration": [],
        "narrator": {"mode":"narrator","persona":"Careful observer.","status":"idle","last_error":""},
        "last_error": "",
        "summary": "",
        "turn_count": 1,
        "turn_index": 1,
        "steering": {"mode":"freeform","topic":"","global":""}
      },
      "match_type": "balanced",
      "match_locks": {"scenario":false}
    }
    """;
}

static string SampleSnapshotWithReasoning()
{
    return """
    {
      "configs": {
        "shared": {
          "base_url": "http://127.0.0.1:1234/v1",
          "model": "google/gemma-4-e2b",
          "timeout": 300,
          "temperature": 0.8,
          "max_output_tokens": 1024
        }
      },
      "engine": {
        "agents": [
          {"id":"alpha","name":"Alpha","persona":"Curious systems architect.","active":true,"status":"waiting","private_notes":[]}
        ],
        "messages": [
          {"turn":1,"speaker":"Alpha","speaker_id":"alpha","text":"Opening move.","status":"ok","kind":"message","created_at":1.0,"model":{"model":"google/gemma-4-e2b","latency_ms":1000},"metadata":{"reasoning_content":"stored trace"}}
        ],
        "narration": [],
        "narrator": {"mode":"narrator","persona":"Careful observer.","status":"idle","last_error":""},
        "last_error": "",
        "summary": "",
        "turn_count": 1,
        "turn_index": 0,
        "steering": {"mode":"freeform","topic":"","global":""}
      },
      "match_type": "balanced",
      "match_locks": {"scenario":false}
    }
    """;
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed class FakeModelProviderClient : IModelProviderClient
{
    private readonly Queue<string> _texts;
    private readonly string _reasoning;

    public FakeModelProviderClient(string text, string reasoning)
        : this([text], reasoning)
    {
    }

    public FakeModelProviderClient(IEnumerable<string> texts, string reasoning)
    {
        _texts = new Queue<string>(texts);
        _reasoning = reasoning;
    }

    public List<IReadOnlyList<ModelChatMessage>> Requests { get; } = new();

    public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
    }

    public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages);
        var text = _texts.Count > 0 ? _texts.Dequeue() : "";
        return Task.FromResult(new ModelCompletionResult(true, config.BaseUrl, "fake-model", text, _reasoning, 123, 10, 5, 15, "", DateTimeOffset.Now));
    }
}

public sealed class FakeInternetToolProvider : IInternetToolProvider
{
    public int Calls { get; private set; }
    public List<InternetToolRequest> Requests { get; } = new();

    public Task<InternetToolResult> ExecuteAsync(InternetToolRequest request, ModelRssSettings settings, CancellationToken cancellationToken = default)
    {
        Calls++;
        Requests.Add(request);
        return Task.FromResult(new InternetToolResult
        {
            Ok = true,
            Tool = request.Tool,
            Query = request.Query,
            Summary = $"Fake source result for {request.Query}",
            Sources =
            [
                new InternetToolSource
                {
                    Title = "AI law update",
                    Url = "https://example.test/ai-law",
                    Source = "test-source",
                    Snippet = "A relevant update.",
                    Score = 1
                }
            ]
        });
    }
}
