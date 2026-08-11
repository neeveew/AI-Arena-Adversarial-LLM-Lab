using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;

internal static partial class Program
{
    static void MatchSetupPackageCodecRoundTripsExactPortableState()
    {
        var source = SessionStore.CreateDefaultSnapshot();
        AgentRosterService.EnsureParticipantCount(source, 6);
        source.MatchType = "scientific";
        source.Engine.FactoryMode = true;
        source.Engine.Steering.Topic = "Which evidence should decide the launch?";
        source.Engine.Steering.Global = "Quality contract: cite evidence and end with an actionable decision.";
        source.ScenarioGenerator.Style = "technical";
        source.ScenarioGenerator.Seed = "portable-seed";
        source.ScenarioGenerator.Intensity = "sharp";
        source.ScenarioGenerator.RolePack = "technical_architecture";
        source.ScenarioGenerator.Absurdity = "grounded";
        source.ScenarioGenerator.ApplyOnReset = true;
        source.PersonaRandomizer.Style = "contrasting";
        source.PersonaRandomizer.Seed = "persona-seed";
        source.PersonaRandomizer.ApplyOnReset = true;
        source.MatchLocks["topic"] = true;
        source.MatchLocks["global"] = false;
        source.MatchLocks["alpha"] = true;
        source.Engine.Agents[0].Persona = "Operational lead with exact evidence thresholds.";
        source.Engine.Agents[0].VoiceStyle = "concise";
        source.Engine.Agents[0].PressureProfile = "cross_examine";
        source.Engine.Agents[0].AccentColor = "#123456";
        source.Engine.Narrator.Persona = "Neutral auditor who tracks evidence quality.";
        source.Engine.Narrator.VoiceStyle = "measured";
        source.Engine.Narrator.Cadence = 3;
        source.Engine.Narrator.InspectPrivateNotes = true;
        source.Engine.RivalryMatrix.Enabled = true;
        source.Engine.RivalryMatrix.Links.Add(new RivalryLink { Source = "alpha", Target = "beta", Stance = "fact_check" });
        source.Engine.TranscriptWindow = 42;
        source.Engine.PrivateWindow = 9;
        source.Engine.NotesWindow = 11;
        source.Engine.Internet.UseInternet = true;
        source.Engine.Internet.MaxResults = 7;
        source.Engine.Internet.SourceFreshnessMinutes = 35;
        source.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = "http://localhost:1234/v1",
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            ApiToken = "top-secret-token",
            Model = "arena-model",
            Timeout = 88,
            Temperature = 0.4,
            MaxOutputTokens = 2048,
            ContextLength = 32768,
            Reasoning = "medium",
            NativeStatefulChat = false,
            NativeIdleTtlSeconds = 45
        };
        source.Engine.Messages.Add(new DialogueMessage { Turn = 1, Speaker = "Alpha", SpeakerId = "alpha", Text = "Runtime text" });
        source.GenerationHistory.Add(new GenerationHistoryEntry { Id = "history", Kind = "random" });

        var package = MatchSetupPackageCodec.FromSnapshot("portable-source", source);
        var json = MatchSetupPackageCodec.Serialize(package);
        Require(json.Contains(MatchSetupPackageCodec.Schema, StringComparison.Ordinal), "portable JSON should declare the v2 setup schema");
        Require(MatchSetupPackageCodec.FactoryConversationContract == "public_group_v1", "Factory setup identity should declare the public group prompt contract");
        Require(package.Setup.FactoryMode && json.Contains("\"factoryMode\": true", StringComparison.Ordinal), "portable JSON should preserve Factory mode without changing the v2 schema");
        var packageState = MatchSetupPackageCodec.ToState("portable-source", package);
        Require(packageState.FactoryMode, "portable package state should project Factory mode for control-plane consumers");
        var canonicalSetup = System.Text.Json.JsonSerializer.Serialize(
            package.Setup,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        var expectedSaltedFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"factory-conversation-contract|{FactoryConversationService.ContractVersion}\n{canonicalSetup}")))
            .ToLowerInvariant();
        Require(MatchSetupPackageCodec.Fingerprint(package) == expectedSaltedFingerprint,
            "Factory portable setup identity should be explicitly salted by the public-group prompt contract");
        Require(!json.Contains("top-secret-token", StringComparison.Ordinal), "portable JSON must never serialize provider API tokens");
        Require(!json.Contains("Runtime text", StringComparison.Ordinal), "portable JSON must not include transcript runtime state");
        Require(!json.Contains("history", StringComparison.OrdinalIgnoreCase), "portable JSON must not include generation history");

        var embeddedSecretSnapshot = SessionStore.CreateDefaultSnapshot();
        embeddedSecretSnapshot.Engine.Steering.Topic = "Credential redaction check";
        embeddedSecretSnapshot.Configs["shared"] = new ModelProviderConfig
        {
            BaseUrl = "http://embedded-user:embedded-pass@localhost:1234/v1?api_key=embedded-query#embedded-fragment",
            Model = "safe-model"
        };
        var embeddedSecretJson = MatchSetupPackageCodec.Serialize(MatchSetupPackageCodec.FromSnapshot("secret-url", embeddedSecretSnapshot));
        Require(!embeddedSecretJson.Contains("embedded-user", StringComparison.Ordinal)
            && !embeddedSecretJson.Contains("embedded-pass", StringComparison.Ordinal)
            && !embeddedSecretJson.Contains("embedded-query", StringComparison.Ordinal)
            && !embeddedSecretJson.Contains("embedded-fragment", StringComparison.Ordinal), "portable JSON should strip credentials, query strings, and fragments embedded in provider URLs");

        var parsed = MatchSetupPackageCodec.Parse(json);
        Require(parsed.Ok && parsed.Package is not null, $"portable JSON should parse: {parsed.Message}");
        var target = SessionStore.CreateDefaultSnapshot();
        var applied = MatchSetupPackageCodec.Apply(parsed.Package!, target, source.Configs);
        Require(applied.Ok, $"portable setup should apply atomically: {applied.Message}");
        Require(target.Engine.FactoryMode, "round trip should preserve Factory mode");
        Require(target.MatchType == "scientific" && target.Engine.Steering.Topic == source.Engine.Steering.Topic, "round trip should preserve scenario identity");
        Require(target.Engine.Agents.Count(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id)) == 6, "round trip should preserve dynamic cast size");
        Require(target.Engine.Agents.Single(agent => agent.Id == "alpha").Persona == source.Engine.Agents[0].Persona, "round trip should preserve exact personas");
        Require(target.Engine.RivalryMatrix.Enabled && target.Engine.RivalryMatrix.Links.Single().Stance == "fact_check", "round trip should preserve normalized relationship pressure");
        Require(target.Engine.Internet.UseInternet && target.Engine.Internet.MaxResults == 7, "round trip should preserve Internet setup policy");
        Require(target.Configs["shared"].ApiToken == "top-secret-token", "a trusted token may be reused only for an unchanged endpoint and API mode");
        Require(target.Engine.Messages.Count == 0 && target.GenerationHistory.Count == 0, "applying a setup package should leave runtime transcript and history clean");

        var recaptured = MatchSetupPackageCodec.FromSnapshot("portable-target", target);
        Require(
            MatchSetupPackageCodec.Fingerprint(recaptured) == MatchSetupPackageCodec.Fingerprint(package),
            "canonical setup fingerprints should survive an export/import round trip");

        var arenaPackage = MatchSetupPackageCodec.FromSnapshot("portable-arena", source);
        arenaPackage.Setup.FactoryMode = false;
        Require(
            MatchSetupPackageCodec.Fingerprint(arenaPackage) != MatchSetupPackageCodec.Fingerprint(package),
            "Factory mode should change the exact portable Match Setup fingerprint");
        var arenaTarget = SessionStore.CreateDefaultSnapshot();
        Require(MatchSetupPackageCodec.Apply(arenaPackage, arenaTarget, source.Configs).Ok, "Arena-mode comparison setup should apply");
        var evaluation = new ArenaEvaluationService();
        var factoryIdentity = evaluation.Capture("portable-factory", target, DateTimeOffset.UnixEpoch);
        var arenaIdentity = evaluation.Capture("portable-arena", arenaTarget, DateTimeOffset.UnixEpoch);
        Require(factoryIdentity.ExactSetupFingerprint != arenaIdentity.ExactSetupFingerprint, "Factory mode should change evaluation exact-setup identity");
        Require(factoryIdentity.ScenarioFingerprint != arenaIdentity.ScenarioFingerprint, "Factory mode should change model-neutral scenario identity");

        var blankFactorySnapshot = SessionStore.CreateDefaultSnapshot();
        blankFactorySnapshot.Engine.FactoryMode = true;
        blankFactorySnapshot.Engine.Steering.Topic = "";
        blankFactorySnapshot.Engine.Steering.Global = "";
        foreach (var agent in blankFactorySnapshot.Engine.Agents.Where(agent => agent.Active))
        {
            agent.Persona = "";
        }
        blankFactorySnapshot.Engine.Agents[0].Name = "";
        blankFactorySnapshot.Engine.Narrator.Persona = "";
        blankFactorySnapshot.Engine.RivalryMatrix.Enabled = true;
        var blankFactoryJson = MatchSetupPackageCodec.Serialize(MatchSetupPackageCodec.FromSnapshot("blank-factory", blankFactorySnapshot));
        var blankFactory = MatchSetupPackageCodec.Parse(blankFactoryJson);
        Require(blankFactory.Ok && blankFactory.Package?.Setup.FactoryMode == true, "Factory package with blank Arena-only guidance should remain valid");
        Require(blankFactory.Warnings.Any(warning => warning.Contains("Factory mode does not send Arena-only guidance", StringComparison.Ordinal)), "Factory import should explain why blank Arena guidance does not block the raw run");
        Require(!blankFactory.Warnings.Any(warning => warning.Contains("remain blocked", StringComparison.OrdinalIgnoreCase)
            || warning.StartsWith("Participant '", StringComparison.Ordinal) && warning.Contains("blank persona", StringComparison.OrdinalIgnoreCase)
            || warning.Equals("Narrator persona is blank.", StringComparison.Ordinal)), "Factory import should suppress Arena-only blocked/persona warnings");
        Require(blankFactory.Warnings.Any(warning => warning.Contains("blank name", StringComparison.OrdinalIgnoreCase)), "Factory import should retain structural cast warnings");
        Require(blankFactory.Warnings.Any(warning => warning.Contains("Relationship pressure is enabled", StringComparison.Ordinal)), "Factory import should retain relationship-matrix validation warnings");

        var legacyJson = blankFactoryJson.Replace("\"factoryMode\": true,", "", StringComparison.Ordinal);
        var legacy = MatchSetupPackageCodec.Parse(legacyJson);
        Require(legacy.Ok && legacy.Package?.Setup.FactoryMode == false, "legacy v2 packages without factoryMode should default to Arena mode");
        Require(legacy.Warnings.Any(warning => warning.Contains("Scenario topic is blank", StringComparison.Ordinal)), "legacy packages should retain Arena-mode readiness warnings");

        var invalidJson = json.Replace("\"id\": \"beta\"", "\"id\": \"alpha\"", StringComparison.Ordinal);
        var invalid = MatchSetupPackageCodec.Parse(invalidJson);
        Require(!invalid.Ok && invalid.ErrorCode == "invalid_package", "duplicate cast ids should reject the entire package before mutation");

        var incomplete = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(
            MatchSetupPackageCodec.FromSnapshot("blank-draft", SessionStore.CreateDefaultSnapshot())));
        Require(incomplete.Ok && incomplete.Warnings.Any(warning => warning.Contains("topic is blank", StringComparison.OrdinalIgnoreCase)), "an incomplete in-app draft should remain portable while reporting its readiness warning");

        var invalidProvider = MatchSetupPackageCodec.FromSnapshot("invalid-provider", source);
        invalidProvider.Setup.Providers["shared"].Temperature = 9;
        var invalidProviderResult = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(invalidProvider));
        Require(!invalidProviderResult.Ok && invalidProviderResult.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase), "out-of-range provider settings should reject the package instead of being silently clamped");

        var nullProviderModel = MatchSetupPackageCodec.FromSnapshot("null-provider-model", source);
        nullProviderModel.Setup.Providers["shared"].Model = null!;
        var nullProviderResult = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(nullProviderModel));
        Require(nullProviderResult.Ok && nullProviderResult.Package is not null, "optional null provider model should normalize safely");
        var nullProviderTarget = SessionStore.CreateDefaultSnapshot();
        Require(MatchSetupPackageCodec.Apply(nullProviderResult.Package!, nullProviderTarget, source.Configs).Ok
            && nullProviderTarget.Configs["shared"].Model == "", "normalized null provider model should apply without crashing");

        var caseVariantJson = MatchSetupPackageCodec.Serialize(MatchSetupPackageCodec.FromSnapshot("case-provider", source))
            .Replace("\"shared\":", "\"SHARED\":", StringComparison.Ordinal);
        var caseVariantResult = MatchSetupPackageCodec.Parse(caseVariantJson);
        Require(!caseVariantResult.Ok && caseVariantResult.Message.Contains("canonical lowercase", StringComparison.OrdinalIgnoreCase), "case-variant provider keys should reject instead of creating duplicate provider roles");

        var duplicateProviderJson = json.Replace("\"shared\": {", "\"SHARED\": null,\n      \"shared\": {", StringComparison.Ordinal);
        var duplicateProviderResult = MatchSetupPackageCodec.Parse(duplicateProviderJson);
        Require(!duplicateProviderResult.Ok && duplicateProviderResult.ErrorCode == "invalid_json"
            && duplicateProviderResult.Message.Contains("duplicate property", StringComparison.OrdinalIgnoreCase), "case-insensitive duplicate JSON properties should reject before deserialization");

        var unknownMemberJson = json.Insert(json.IndexOf('{') + 1, "\n  \"unexpected\": true,");
        var unknownMemberResult = MatchSetupPackageCodec.Parse(unknownMemberJson);
        Require(!unknownMemberResult.Ok && unknownMemberResult.ErrorCode == "invalid_json", "unknown v2 package members should reject instead of being silently ignored");

        var invalidRelationship = MatchSetupPackageCodec.FromSnapshot("invalid-relationship", source);
        invalidRelationship.Setup.Relationship.Links.Add(new MatchSetupRelationshipLinkPackage { Source = "beta", Target = "beta", Stance = "challenge" });
        var invalidRelationshipResult = MatchSetupPackageCodec.Parse(MatchSetupPackageCodec.Serialize(invalidRelationship));
        Require(!invalidRelationshipResult.Ok && invalidRelationshipResult.Message.Contains("cannot target", StringComparison.OrdinalIgnoreCase), "self-targeting relationship rules should reject the package instead of being silently dropped");
    }

    static void MatchSetupPortabilityCreatesCleanSessionsAndProtectsTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-portable-setup-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var events = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            var sourceSnapshot = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            sourceSnapshot.Engine.FactoryMode = true;
            sourceSnapshot.Engine.Steering.Topic = "Portable safety review";
            var runtimeMessage = new DialogueMessage { Turn = 1, Speaker = "Beta", SpeakerId = "beta", Text = "Do not copy this" };
            runtimeMessage.Metadata[FactoryConversationService.ContractMetadataKey] = System.Text.Json.JsonSerializer.SerializeToElement(FactoryConversationService.ContractVersion);
            runtimeMessage.Metadata[FactoryConversationService.ConversationIdMetadataKey] = System.Text.Json.JsonSerializer.SerializeToElement("conversation-runtime-must-not-export");
            runtimeMessage.Metadata[FactoryConversationService.ContextFingerprintMetadataKey] = System.Text.Json.JsonSerializer.SerializeToElement(new string('a', 64));
            sourceSnapshot.Engine.Messages.Add(runtimeMessage);
            sourceSnapshot.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = "http://localhost:1234/v1",
                ApiMode = ModelProviderApiModes.OpenAiCompatible,
                ApiToken = "trusted-local-token",
                Model = "local-model"
            };
            store.SaveSnapshotAsync(sourceSnapshot, "default").GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var service = new MatchSetupPortabilityService(
                store,
                events,
                () => active,
                () => false,
                async (preferredId, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
                });

            var exported = service.ExportAsync().GetAwaiter().GetResult();
            Require(exported.Ok && exported.State is not null, "active setup should export through the headless service");
            Require(exported.State!.FactoryMode, "headless export state should expose Factory mode");
            Require(!exported.State!.Json.Contains("trusted-local-token", StringComparison.Ordinal), "service export should remain secret-free");
            Require(!exported.State.Json.Contains("Do not copy this", StringComparison.Ordinal)
                && !exported.State.Json.Contains("conversation-runtime-must-not-export", StringComparison.Ordinal)
                && !exported.State.Json.Contains("factory_context_fingerprint", StringComparison.Ordinal),
                "portable Factory setup export must exclude transcript bodies and runtime group-contract metadata");
            var parsed = MatchSetupPackageCodec.Parse(exported.State.Json);
            Require(parsed.Ok && parsed.Package is not null, "service export should be accepted by the same codec");
            parsed.Package!.Setup.Providers["shared"].BaseUrl = "https://example.invalid/v1";
            var changedEndpointJson = MatchSetupPackageCodec.Serialize(parsed.Package);

            var imported = service.ImportAsync(changedEndpointJson, "review-copy").GetAwaiter().GetResult();
            Require(imported.Ok && imported.Receipt?.TargetSessionId == "review-copy", "import should create and select the requested clean session");
            Require(imported.Receipt!.Warnings.Any(warning => warning.Contains("token", StringComparison.OrdinalIgnoreCase)), "endpoint changes should produce a token-clearing warning");
            Require(imported.Receipt.Warnings.Any(warning => warning.Contains("public group history", StringComparison.OrdinalIgnoreCase)
                && warning.Contains("public Operator turn", StringComparison.OrdinalIgnoreCase)),
                "a clean Factory import should explain that runtime history was excluded and a new Operator root is required");
            var target = store.LoadSnapshotAsync("review-copy").GetAwaiter().GetResult();
            Require(target is not null && target.Engine.Steering.Topic == "Portable safety review", "imported session should carry the portable setup");
            Require(target!.Engine.FactoryMode, "service import should preserve Factory mode in the clean session");
            Require(target!.Engine.Messages.Count == 0 && target.GenerationHistory.Count == 0, "imported session should start with clean runtime state");
            var importedGroup = new FactoryConversationService().Inspect(target);
            Require(!importedGroup.IsAnchored
                && !importedGroup.HasUsableRoot
                && importedGroup.ContextFingerprint.Length == 0,
                "portable Factory import must not recreate or infer a runtime group root");
            Require(target.Configs["shared"].BaseUrl == "https://example.invalid/v1" && target.Configs["shared"].ApiToken == "", "an imported endpoint must not inherit a trusted token for another host");
            var original = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
            Require(original?.Configs["shared"].ApiToken == "trusted-local-token", "import must not mutate the source session or its trusted token");
            Require(File.ReadAllText(events.EventPath("review-copy")).Contains("control_match_setup_imported", StringComparison.Ordinal), "import should append an auditable receipt to the new session");

            var second = service.ImportAsync(exported.State.Json, "review-copy").GetAwaiter().GetResult();
            Require(second.Ok && second.Receipt?.TargetSessionId == "review-copy-2", "repeated imports should choose a collision-free session id without overwriting data");

            var concurrent = Task.WhenAll(
                service.ImportAsync(exported.State.Json, "parallel-review"),
                service.ImportAsync(exported.State.Json, "parallel-review")).GetAwaiter().GetResult();
            Require(concurrent.All(result => result.Ok), "concurrent imports should both complete through the serialized clean-session path");
            Require(concurrent.Select(result => result.Receipt!.TargetSessionId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["parallel-review", "parallel-review-2"]), "concurrent imports should reserve collision-free session ids");

            var packagePath = Path.Combine(root, "portable-package.json");
            File.WriteAllText(packagePath, exported.State.Json);
            var fromFile = service.ImportFileAsync(packagePath, "file-review").GetAwaiter().GetResult();
            Require(fromFile.Ok && fromFile.Receipt?.TargetSessionId == "file-review", "path-based PowerShell imports should use the same validated clean-session path");
            var invalidPath = service.ImportFileAsync(Path.ChangeExtension(packagePath, ".txt"), "bad-file").GetAwaiter().GetResult();
            Require(!invalidPath.Ok && invalidPath.ErrorCode == "invalid_path", "path-based imports should require an explicit JSON file");

            var blockedService = new MatchSetupPortabilityService(store, events, () => active, () => true, (_, _) => Task.CompletedTask);
            var blocked = blockedService.ImportAsync(exported.State.Json, "blocked-copy").GetAwaiter().GetResult();
            Require(!blocked.Ok && blocked.ErrorCode == "not_available", "busy imports should fail before creating a session");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void MatchSetupControlHandlerExportsAndImportsPortablePackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-portable-handler-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var eventStore = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            var source = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            source.Engine.FactoryMode = true;
            source.Engine.Steering.Topic = "Portable handler audit";
            store.SaveSnapshotAsync(source, "default").GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var portability = new MatchSetupPortabilityService(
                store,
                eventStore,
                () => active,
                () => false,
                async (preferredId, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
                });
            var overlays = new ShellOverlayControlService(
                () => new AIArenaMatchSetupControlState(false, "scenario", "arena", active!.Id, "balanced", source.Engine.Steering.Topic, 4, false),
                () => { },
                () => { },
                _ => true,
                () => new AIArenaSettingsControlState(false, "", "dark-blue", false, true, "diagnostics", false, false, false, false, false, true, false, false, false, true, false, false, true, false),
                () => { },
                () => { },
                _ => { });
            var matrix = new RivalryMatrixControlService(
                store,
                eventStore,
                () => active,
                () => false,
                (_, action) => action(CancellationToken.None),
                (_, _) => Task.CompletedTask);
            var events = new AIArenaControlPlaneEventHub();
            AIArenaControlEvent? published = null;
            using var subscription = events.Subscribe(item => published = item);
            var handler = new AIArenaMatchSetupControlHandler(
                overlays,
                _ => Task.FromResult(new AIArenaAgentRosterResizeResult(false, "not_used", "Not used.", 4)),
                matrix,
                portability,
                (factoryMode, _) => Task.FromResult(new AIArenaModelBehaviorModeResult(
                    false,
                    "not_used",
                    "Not used.",
                    factoryMode ? "factory" : "arena",
                    false)),
                events);

            Require(AIArenaControlPlaneProtocol.TryParseRequest(
                """{"id":"export","command":"match.setup.export","args":{}}""",
                out var exportRequest,
                out _), "portable export control request should parse");
            var exported = handler.ExecuteAsync(exportRequest).GetAwaiter().GetResult();
            Require(exported.Ok && published?.Type == "match.setup.exported", "handler export should return data and publish an auditable event");

            var package = portability.ExportAsync().GetAwaiter().GetResult();
            Require(package.Ok && package.State is not null, "test package should export through the shared service");
            var importEnvelope = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "import",
                command = "match.setup.import",
                args = new { json = package.State!.Json, name = "handler-import" }
            });
            Require(AIArenaControlPlaneProtocol.TryParseRequest(importEnvelope, out var importRequest, out _), "portable inline import control request should parse");
            var imported = handler.ExecuteAsync(importRequest).GetAwaiter().GetResult();
            Require(imported.Ok && published?.Type == "match.setup.imported", "handler import should return a receipt and publish an auditable event");
            Require(store.LoadSnapshotAsync("handler-import").GetAwaiter().GetResult()?.Engine.Steering.Topic == "Portable handler audit", "handler import should persist and select the clean setup session");
            Require(store.LoadSnapshotAsync("handler-import").GetAwaiter().GetResult()?.Engine.FactoryMode == true, "handler import should preserve Factory mode");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
