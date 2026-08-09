using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class RubricClaimLedgerTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    internal static void VersionsRubricsImmutablyAndRestartsWithResults()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ArenaRubricStore(root, new(MaximumRubrics: 8, MaximumResults: 8, MaximumRubricBytes: 16 * 1024, MaximumResultBytes: 32 * 1024));
            var rubric = Rubric();
            Require(store.SaveRubricAsync(rubric).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Written, "rubric version was not written");
            Require(store.SaveRubricAsync(rubric).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Duplicate, "exact rubric was not deduplicated");
            var conflictingVersion = rubric with { Id = "rubric:quality-copy", CreatedAtUtc = At.AddSeconds(1) };
            Require(store.SaveRubricAsync(conflictingVersion).GetAwaiter().GetResult().Diagnostics.Any(item => item.Code == "artifact.duplicate_version"),
                "same rubric name/version was not rejected");
            var versionTwo = rubric with { Id = "rubric:quality-v2", Version = "2.0.0", CreatedAtUtc = At.AddMinutes(1) };
            Require(store.SaveRubricAsync(versionTwo).GetAwaiter().GetResult().Succeeded, "new rubric version was rejected");

            var result = PersistableSingleSubjectResult(rubric);
            Require(store.SaveResultAsync(result).GetAwaiter().GetResult().Succeeded, "finalized result was not written");
            var unboundModelJudge = SingleSubjectResult(rubric) with { Id = "evaluation:unbound-model-judge" };
            var unboundModelValidation = ArenaRubricService.ValidateFinalizedResult(rubric, unboundModelJudge);
            Require(unboundModelValidation.Issues.Any(item => item.Code == "rubric_result.model_judge_evidence_unbound")
                    && store.SaveResultAsync(unboundModelJudge).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "caller-supplied model-judge scores persisted without a provider-attempt binding");
            var restarted = new ArenaRubricStore(root, new(MaximumRubrics: 8, MaximumResults: 8, MaximumRubricBytes: 16 * 1024, MaximumResultBytes: 32 * 1024));
            var loadedRubrics = restarted.LoadRubricsAsync().GetAwaiter().GetResult();
            var loadedResults = restarted.LoadResultsAsync().GetAwaiter().GetResult();
            Require(loadedRubrics.Artifacts.Select(item => item.Version).SequenceEqual(["1.0.0", "2.0.0"]), "versioned rubrics did not restart deterministically");
            Require(ArenaRubricResultCodec.Serialize(loadedResults.Artifacts.Single()) == ArenaRubricResultCodec.Serialize(result),
                "result did not survive restart exactly");

            var resultDirectory = Path.Combine(root, "rubric-results");
            File.WriteAllText(Path.Combine(resultDirectory, "corrupt.json"), "{broken");
            File.WriteAllBytes(Path.Combine(resultDirectory, "oversize.json"), new byte[32 * 1024 + 1]);
            var resultFallback = restarted.LoadResultsAsync().GetAwaiter().GetResult();
            Require(resultFallback.Diagnostics.Any(item => item.Code == "artifact.corrupt")
                    && resultFallback.Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "invalid result records did not fail closed with diagnostics");
            var unsafeResult = result with
            {
                Id = "evaluation:unsafe",
                Evidence = [new("evidence:unsafe-result", ArenaEvidenceState.Inferred, "api_key=supersecretvalue", Basis: "local test")]
            };
            Require(store.SaveResultAsync(unsafeResult).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "private result content reached persistence");
            var danglingResult = result with { Id = "evaluation:dangling", RubricId = "rubric:missing" };
            Require(store.SaveResultAsync(danglingResult).GetAwaiter().GetResult().Diagnostics.Any(item => item.Code == "rubric_result.rubric_missing"),
                "dangling rubric result reached persistence");
            var forgedResult = result with
            {
                Id = "evaluation:forged",
                HumanResults = [result.HumanResults.Single() with { WeightedScoreA = 0.99m }]
            };
            Require(store.SaveResultAsync(forgedResult).GetAwaiter().GetResult().Diagnostics.Any(item => item.Code == "rubric_result.derivation_invalid"),
                "forged weighted result reached persistence");
            File.WriteAllText(Path.Combine(resultDirectory, "forged-canonical.json"), ArenaRubricResultCodec.Serialize(forgedResult));
            var forgedLoad = restarted.LoadResultsAsync().GetAwaiter().GetResult();
            Require(!forgedLoad.Artifacts.Any(item => item.Id == forgedResult.Id)
                    && forgedLoad.Diagnostics.Any(item => item.Code == "rubric_result.derivation_invalid"),
                "canonical forged derivatives survived cross-artifact load validation");

            var rubricDirectory = Path.Combine(root, "rubrics");
            File.WriteAllText(Path.Combine(rubricDirectory, "corrupt.json"), "{broken");
            File.WriteAllBytes(Path.Combine(rubricDirectory, "oversize.json"), new byte[16 * 1024 + 1]);
            var fallback = restarted.LoadRubricsAsync().GetAwaiter().GetResult();
            Require(fallback.Diagnostics.Any(item => item.Code == "artifact.corrupt"), "corrupt rubric did not produce safe fallback evidence");
            Require(fallback.Diagnostics.Any(item => item.Code == "artifact.oversize"), "oversize rubric did not produce safe fallback evidence");
            var unsafeRubric = rubric with { Id = "rubric:unsafe", Name = "api_key=supersecretvalue", Version = "9.0.0" };
            Require(store.SaveRubricAsync(unsafeRubric).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "private rubric content reached persistence");

            var concurrencyRoot = TemporaryRoot();
            try
            {
                var firstStore = new ArenaRubricStore(concurrencyRoot);
                var secondStore = new ArenaRubricStore(concurrencyRoot);
                Require(firstStore.SaveRubricAsync(rubric).GetAwaiter().GetResult().Succeeded,
                    "concurrent result fixture rubric was not persisted");
                var firstRubric = Rubric() with { Id = "rubric:concurrent", Name = "Concurrent first" };
                var secondRubric = firstRubric with { Name = "Concurrent second", CreatedAtUtc = At.AddSeconds(1) };
                var rubricWrites = RunConcurrently(
                    () => firstStore.SaveRubricAsync(firstRubric),
                    () => secondStore.SaveRubricAsync(secondRubric));
                Require(rubricWrites.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Written) == 1,
                    "cross-instance rubric writers both claimed the same immutable ID");
                Require(rubricWrites.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected) == 1
                        && rubricWrites.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected)
                            .Diagnostics.Any(item => item.Code == "artifact.duplicate_id"),
                    "cross-instance rubric ID conflict did not reject exactly one writer");
                var persistedRubric = firstStore.LoadRubricsAsync().GetAwaiter().GetResult().Artifacts.Single(item => item.Id == "rubric:concurrent");
                var rubricWinner = rubricWrites.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Written).Artifact!;
                Require(ArenaContractCodec.Serialize(persistedRubric) == ArenaContractCodec.Serialize(rubricWinner),
                    "persisted rubric did not match the reported concurrent winner");

                var firstResult = PersistableSingleSubjectResult(rubric) with { Id = "evaluation:concurrent" };
                var secondResult = firstResult with { FinalizedAtUtc = firstResult.FinalizedAtUtc.AddSeconds(1) };
                var resultWrites = RunConcurrently(
                    () => firstStore.SaveResultAsync(firstResult),
                    () => secondStore.SaveResultAsync(secondResult));
                Require(resultWrites.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Written) == 1,
                    "cross-instance result writers both claimed the same immutable ID");
                Require(resultWrites.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected) == 1
                        && resultWrites.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected)
                            .Diagnostics.Any(item => item.Code == "artifact.duplicate_id"),
                    "cross-instance result ID conflict did not reject exactly one writer");
                var persistedResult = firstStore.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.Single();
                var resultWinner = resultWrites.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Written).Artifact!;
                Require(ArenaRubricResultCodec.Serialize(persistedResult) == ArenaRubricResultCodec.Serialize(resultWinner),
                    "persisted result did not match the reported concurrent winner");
                Require(!Directory.EnumerateFiles(concurrencyRoot, "*.tmp", SearchOption.AllDirectories).Any()
                        && !Directory.EnumerateFiles(concurrencyRoot, "*.lock", SearchOption.AllDirectories).Any(),
                    "cross-instance rubric persistence left lease or temporary files");
            }
            finally
            {
                Directory.Delete(concurrencyRoot, recursive: true);
            }
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic rubric persistence left temporary files");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void KeepsWeightedSourcesHumanScoresAndDisagreementSeparate()
    {
        var rubric = Rubric();
        var result = SingleSubjectResult(rubric);
        Require(result.DeterministicResults.Single().WeightedScoreA == 0.72m, "deterministic weighted score was wrong");
        Require(result.HumanResults.Single().WeightedScoreA == 0.52m, "human weighted score was wrong");
        Require(result.ModelJudgeResults.Single().WeightedScoreA == 0.32m, "model weighted score was wrong");
        Require(result.HumanResults.Single().ReviewerId == "reviewer:local", "human reviewer provenance was lost");
        Require(result.ModelJudgeResults.Single().Provenance.State == ArenaEvidenceState.Inferred, "model opinion was not labelled inferred");
        Require(result.Disagreements.Length == 2 && result.Disagreements.All(item => item.ResultIds.Length == 3),
            "judge disagreement was collapsed instead of retained");
        var json = ArenaRubricResultCodec.Serialize(result);
        Require(ArenaRubricResultCodec.TryDeserialize(json, out var roundTrip, out var issues), Format(issues));
        Require(roundTrip is not null && ArenaRubricResultCodec.Serialize(roundTrip) == json, "rubric result did not round trip canonically");
        Require(json.IndexOf("deterministicResults", StringComparison.Ordinal) < json.IndexOf("humanResults", StringComparison.Ordinal),
            "result JSON was not canonical");
    }

    internal static void ConcealsBlindMappingUntilFinalization()
    {
        var rubric = BlindRubric();
        var service = new ArenaRubricService();
        var session = service.BeginBlindPairwise(rubric, "evaluation:blind", "trial:alpha", "trial:beta", "seed:stable", At);
        var viewJson = JsonSerializer.Serialize(session.View);
        Require(!viewJson.Contains("trial:alpha", StringComparison.Ordinal) && !viewJson.Contains("trial:beta", StringComparison.Ordinal),
            "judge-facing blind view exposed original candidate identity");
        Require(session.View.LabelAToken != session.View.LabelBToken, "blind labels were not opaque and distinct");

        var result = session.Finalize(
            [new(
                "result:blind-human",
                "evaluator:blind",
                ArenaRubricJudgmentSource.Human,
                "reviewer:blind",
                ArenaPairwisePreference.A,
                [
                    new("criterion:accuracy", 8m, 5m, Observed("evidence:blind-accuracy")),
                    new("criterion:clarity", 7m, 6m, Observed("evidence:blind-clarity"))
                ],
                Observed("provenance:blind-human"))],
            At.AddMinutes(1),
            [Observed("evidence:blind-final")]);
        Require(result.BlindReveal is not null, "finalized result did not reveal its mapping");
        Require(result.SubjectReferenceIds.ToHashSet(StringComparer.Ordinal).SetEquals(["trial:alpha", "trial:beta"]), "blind reveal changed candidate identities");
        Require(result.HumanResults.Single().PairwisePreference == ArenaPairwisePreference.A, "blind preference was lost");
        Require(ArenaRubricService.ValidateFinalizedResult(rubric, result).Issues.Any(item => item.Code == "rubric_result.blind_commitment_unavailable"),
            "caller-supplied reveal was treated as durable proof of pre-judgment concealment");
        RequireThrows<InvalidDataException>(() => new ArenaRubricService().BeginBlindPairwise(
            Rubric(), "evaluation:not-blind", "trial:a", "trial:b", "seed:no-blind-evaluator", At),
            "rubric without a blind_pairwise evaluator opened a blind session");
        RequireThrows<InvalidOperationException>(() => session.Finalize([], At.AddMinutes(2), []), "blind session finalized more than once");
    }

    internal static void PersistsBlindCommitmentBeforeJudgmentReceipt()
    {
        var root = TemporaryRoot();
        try
        {
            var rubric = BlindRubric();
            var store = new ArenaRubricStore(root);
            Require(store.SaveRubricAsync(rubric).GetAwaiter().GetResult().Succeeded, "blind rubric was not saved");
            var session = new ArenaRubricService().BeginBlindPairwise(
                rubric,
                "evaluator:blind",
                "evaluation:durable-blind",
                "trial:alpha",
                "trial:beta",
                "seed:durable",
                At);
            var commitmentJson = ArenaEvaluationEvidenceCodec.Serialize(session.Commitment);
            Require(!commitmentJson.Contains("trial:alpha", StringComparison.Ordinal)
                    && !commitmentJson.Contains("trial:beta", StringComparison.Ordinal),
                "pre-judgment commitment exposed the concealed mapping");
            Require(store.SaveBlindCommitmentAsync(session.Commitment).GetAwaiter().GetResult().Succeeded,
                "pre-judgment commitment was not persisted");
            var finalization = session.FinalizeWithReceipt(
                new ArenaRubricJudgmentSubmission(
                    "result:durable-blind",
                    "evaluator:blind",
                    ArenaRubricJudgmentSource.Human,
                    "reviewer:blind",
                    ArenaPairwisePreference.A,
                    [
                        new("criterion:accuracy", 8m, 5m, Observed("evidence:durable-blind:accuracy")),
                        new("criterion:clarity", 7m, 6m, Observed("evidence:durable-blind:clarity"))
                    ],
                    Observed("provenance:durable-blind")),
                At.AddMinutes(1),
                []);
            Require(store.SaveResultAsync(finalization.Result).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "blind result persisted before its post-judgment receipt");
            Require(store.SaveBlindReceiptAsync(finalization.Receipt).GetAwaiter().GetResult().Succeeded,
                "post-judgment receipt did not open the commitment");
            Require(store.SaveResultAsync(finalization.Result).GetAwaiter().GetResult().Succeeded,
                "proved blind result was rejected");
            var restarted = new ArenaRubricStore(root);
            Require(restarted.LoadResultsAsync().GetAwaiter().GetResult().Artifacts.Single().Id == finalization.Result.Id,
                "proved blind result did not survive restart");

            var forged = finalization.Result with
            {
                Id = "evaluation:forged-blind",
                HumanResults = [finalization.Result.HumanResults.Single() with { WeightedScoreA = 0.99m }]
            };
            Require(store.SaveResultAsync(forged).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "forged blind derivative reused an unrelated proof receipt");
            var badReceipt = finalization.Receipt with
            {
                Id = "blind-receipt:wrong-map",
                MappingNonce = new string('0', 64)
            };
            Require(store.SaveBlindReceiptAsync(badReceipt).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "receipt with a false mapping opening was persisted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void BindsModelScoresToTracedProviderReceipts()
    {
        var root = TemporaryRoot();
        try
        {
            var rubric = Rubric();
            var store = new ArenaRubricStore(root);
            Require(store.SaveRubricAsync(rubric).GetAwaiter().GetResult().Succeeded, "model-judge rubric was not saved");
            var traces = new ProviderRequestTraceStore();
            var provider = new TracedJudgeProvider(traces, At);
            var judge = new ArenaModelJudgeService(provider, traces, new FixedTimeProvider(At));
            var finalization = judge.JudgeSingleSubjectAsync(
                rubric,
                "evaluator:model",
                "evaluation:provider-judge",
                "trial:one",
                "Bounded candidate response.",
                new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge-model",
                    MaxOutputTokens = 256
                }).GetAwaiter().GetResult();
            Require(finalization.Result.ModelJudgeResults.Single().WeightedScoreA == 0.58m,
                "strict provider scores were not weighted by the immutable rubric");
            Require(finalization.Result.ModelJudgeResults.Single().Criteria.All(item =>
                    item.Evidence.State == ArenaEvidenceState.Inferred && item.Evidence.ReferenceId == finalization.Receipt.Id),
                "model opinion lost its inferred state or receipt binding");
            Require(store.SaveResultAsync(finalization.Result).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "model score persisted before provider-attempt receipt");
            Require(store.SaveModelJudgeReceiptAsync(finalization.Receipt).GetAwaiter().GetResult().Succeeded,
                "valid provider-attempt receipt was rejected");
            Require(store.SaveResultAsync(finalization.Result).GetAwaiter().GetResult().Succeeded,
                "provider-bound model score was rejected");

            var forged = finalization.Result with
            {
                Id = "evaluation:forged-model",
                ModelJudgeResults = [finalization.Result.ModelJudgeResults.Single() with { WeightedScoreA = 0.99m }]
            };
            Require(store.SaveResultAsync(forged).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Rejected,
                "forged model score reused an unrelated provider receipt");
            RequireThrows<InvalidDataException>(() => ArenaModelJudgeService.ParseScores(
                rubric,
                "{\"criteria\":[{\"criterionId\":\"criterion:accuracy\",\"score\":11},{\"criterionId\":\"criterion:clarity\",\"score\":2}]}"),
                "out-of-range provider score was parsed");
            RequireThrows<InvalidDataException>(() => ArenaModelJudgeService.ParseScores(
                rubric,
                "{\"criteria\":[{\"criterionId\":\"criterion:accuracy\",\"score\":6},{\"criterionId\":\"criterion:clarity\",\"score\":2}],\"explanation\":\"extra\"}"),
                "provider response with unbound extra fields was parsed");

            using var parseCancellation = new CancellationTokenSource();
            var parseTraces = new ProviderRequestTraceStore();
            var parseJudge = new ArenaModelJudgeService(
                new TracedJudgeProvider(parseTraces, At),
                parseTraces,
                new FixedTimeProvider(At),
                () => parseCancellation.Cancel());
            RequireThrows<OperationCanceledException>(() => parseJudge.JudgeSingleSubjectAsync(
                rubric,
                "evaluator:model",
                "evaluation:cancel-after-parse",
                "trial:one",
                "Bounded candidate response.",
                new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:1234/v1",
                    Model = "judge-model",
                    MaxOutputTokens = 256
                },
                parseCancellation.Token).GetAwaiter().GetResult(),
                "cancellation after strict parse still yielded a model-judge finalization");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void PreservesUnavailableEvidenceAndRejectsMeasuredModelClaims()
    {
        var rubric = Rubric();
        var service = new ArenaRubricService();
        var result = service.CreateSingleSubjectResult(
            rubric,
            "evaluation:unavailable",
            "trial:unavailable",
            [new(
                "result:model-unavailable",
                "evaluator:model",
                ArenaRubricJudgmentSource.ModelJudge,
                null,
                null,
                [
                    new("criterion:accuracy", null, null, Unavailable("evidence:model-accuracy")),
                    new("criterion:clarity", null, null, Unavailable("evidence:model-clarity"))
                ],
                Unavailable("provenance:model-unavailable"))],
            At,
            At.AddSeconds(1),
            []);
        Require(result.ModelJudgeResults.Single().WeightedScoreA is null, "unavailable criteria were converted into a score");
        Require(result.ModelJudgeResults.Single().Criteria.All(item => item.Evidence.State == ArenaEvidenceState.Unavailable),
            "unavailable evidence state was lost");

        RequireThrows<InvalidDataException>(() => service.CreateSingleSubjectResult(
            rubric,
            "evaluation:bad-model",
            "trial:one",
            [new(
                "result:model-bad",
                "evaluator:model",
                ArenaRubricJudgmentSource.ModelJudge,
                null,
                null,
                [
                    new("criterion:accuracy", 5m, null, Observed("evidence:bad-a")),
                    new("criterion:clarity", 5m, null, Observed("evidence:bad-c"))
                ],
                Observed("provenance:bad-model"))],
            At,
            At.AddSeconds(1),
            []), "model opinion was accepted as an observed measurement");
    }

    internal static void LinksStableMessagesAndLabelsModelAssertions()
    {
        var service = new ArenaClaimLedgerService();
        var ledger = service.Create("ledger:one", "experiment:one", "branch:one", At);
        var message = new DialogueMessage { MessageId = "message:stable", Turn = 1, SpeakerId = "agent:alpha", Speaker = "Alpha", Text = "A source statement.", CreatedAt = 1 };
        var source = ArenaClaimLedgerService.ToolEvidence("evidence:tool-one", "tool-result:one", "Tool source was recorded by reference.");
        ledger = service.AddModelAssertion(
            ledger,
            message,
            "agent:alpha",
            "The bounded assertion may require review.",
            0.74m,
            "extractor:local-v1",
            [source]);
        var claim = ledger.Claims.Single();
        Require(claim.MessageId == "message:stable", "stable message ID was replaced");
        Require(claim.Status == ArenaClaimStatus.Asserted, "source presence silently converted model assertion into supported fact");
        Require(claim.Provenance.State == ArenaEvidenceState.Inferred
                && claim.Provenance.Summary.Contains("not a verified fact", StringComparison.OrdinalIgnoreCase),
            "model extraction was not explicitly labelled as an assertion");
        Require(claim.SourceEvidenceIds.SequenceEqual([source.Id]), "source evidence reference was not retained");

        var legacy = new DialogueMessage { Turn = 2, SpeakerId = "agent:beta", Speaker = "Beta", Text = "Legacy text.", CreatedAt = 2 };
        var first = ArenaClaimLedgerService.ResolveMessageReference(legacy, 0);
        var second = ArenaClaimLedgerService.ResolveMessageReference(legacy, 0);
        Require(first == second && first.StartsWith("message:", StringComparison.Ordinal), "legacy message fallback was not stable");
        var json = ArenaContractCodec.Serialize(ledger);
        Require(!json.Contains(message.Text, StringComparison.Ordinal), "ledger persisted raw dialogue text");
        Require(!json.Contains("tool output", StringComparison.OrdinalIgnoreCase), "ledger persisted raw tool output");

        RequireThrows<InvalidDataException>(() => service.AddClaim(
            ledger,
            message,
            "agent:alpha",
            "api_key=supersecretvalue",
            0.5m,
            Inferred("provenance:unsafe"),
            []), "private claim text crossed the ledger boundary");

        var unavailableLedger = service.Create("ledger:unavailable-source", "experiment:one", "branch:one", At);
        unavailableLedger = service.AddModelAssertion(
            unavailableLedger,
            message,
            "agent:alpha",
            "An assertion with an unavailable source.",
            0.5m,
            "extractor:local-v1",
            [Unavailable("evidence:source-unavailable")],
            claimId: "claim:source-unavailable");
        RequireThrows<InvalidOperationException>(() => service.ReviewClaim(
            unavailableLedger,
            "claim:source-unavailable",
            ArenaClaimStatus.Supported,
            "reviewer:local",
            Observed("review:unavailable-source")), "unavailable source evidence was treated as support");
    }

    internal static void RetainsContradictionsReviewsAndOriginalProvenance()
    {
        var service = new ArenaClaimLedgerService();
        var ledger = service.Create("ledger:review", "experiment:one", "branch:one", At);
        var source = ArenaClaimLedgerService.ToolEvidence("evidence:source", "tool-result:source", "A bounded source reference exists.");
        var firstMessage = new DialogueMessage { MessageId = "message:first", SpeakerId = "agent:a", Speaker = "A", Text = "First.", CreatedAt = 1 };
        var secondMessage = new DialogueMessage { MessageId = "message:second", SpeakerId = "agent:b", Speaker = "B", Text = "Second.", CreatedAt = 2 };
        ledger = service.AddClaim(ledger, firstMessage, "agent:a", "The state is enabled.", 0.8m, Observed("provenance:first"), [source], claimId: "claim:first");
        ledger = service.AddClaim(ledger, secondMessage, "agent:b", "The state is disabled.", 0.7m, Observed("provenance:second"), [], claimId: "claim:second");
        var originalFirstProvenance = ledger.Claims.Single(item => item.Id == "claim:first").Provenance;
        ledger = service.LinkContradiction(ledger, "claim:first", "claim:second", "reviewer:local", Observed("review:contradiction"));
        var first = ledger.Claims.Single(item => item.Id == "claim:first");
        var second = ledger.Claims.Single(item => item.Id == "claim:second");
        Require(first.Status == ArenaClaimStatus.Contradicted && second.Status == ArenaClaimStatus.Contradicted, "contradiction status did not update both claims");
        Require(first.ContradictionClaimIds.SequenceEqual(["claim:second"]) && second.ContradictionClaimIds.SequenceEqual(["claim:first"]),
            "contradiction links were not reciprocal");
        Require(first.ReviewerIds.SequenceEqual(["reviewer:local"]) && ledger.Evidence.Any(item => item.Id == "review:contradiction"),
            "reviewer provenance was not retained");
        Require(first.Provenance == originalFirstProvenance, "review overwrote original claim provenance");
        ledger = service.ReviewClaim(ledger, "claim:first", ArenaClaimStatus.Supported, "reviewer:second", Observed("review:supported"));
        Require(ledger.Claims.Single(item => item.Id == "claim:first").ReviewerIds.SequenceEqual(["reviewer:local", "reviewer:second"]),
            "multiple reviewers were collapsed");
    }

    internal static void BoundsAndRestartsClaimPersistenceWithSafeFallbacks()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new ArenaClaimLedgerService(new(MaximumClaims: 2, MaximumEvidence: 8, MaximumClaimSummaryCharacters: 128));
            var ledger = service.Create("ledger:persist", "experiment:one", "branch:one", At);
            var message = new DialogueMessage { MessageId = "message:persist", SpeakerId = "agent:a", Speaker = "A", Text = "Raw text never persists.", CreatedAt = 1 };
            ledger = service.AddClaim(ledger, message, "agent:a", "A bounded persisted claim.", 0.5m, Observed("provenance:persist"), []);
            var store = new ArenaClaimLedgerStore(root, new(MaximumLedgers: 8, MaximumClaimsPerLedger: 2, MaximumLedgerBytes: 8 * 1024));
            Require(store.SaveAsync(ledger).GetAwaiter().GetResult().Succeeded, "ledger was not written");
            var destructive = ledger with { Claims = [] };
            Require(store.SaveAsync(destructive).GetAwaiter().GetResult().Diagnostics.Any(item => item.Code == "claim_ledger.stale_or_destructive"),
                "ledger persistence allowed existing audit claims to be erased");
            var restarted = new ArenaClaimLedgerStore(root, new(MaximumLedgers: 8, MaximumClaimsPerLedger: 2, MaximumLedgerBytes: 8 * 1024));
            Require(ArenaContractCodec.Serialize(restarted.LoadAllAsync().GetAwaiter().GetResult().Artifacts.Single()) == ArenaContractCodec.Serialize(ledger),
                "ledger did not survive restart exactly");

            var directory = Path.Combine(root, "claim-ledgers");
            File.WriteAllText(Path.Combine(directory, "corrupt.json"), "{broken");
            File.WriteAllBytes(Path.Combine(directory, "oversize.json"), new byte[8 * 1024 + 1]);
            File.WriteAllText(Path.Combine(directory, "old.json"), "{\"schema\":\"ai_arena.claim_ledger.v0\"}");
            var unsafeJson = ArenaContractCodec.Serialize(ledger).Replace("A bounded persisted claim.", "api_key=supersecretvalue", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(directory, "private.json"), unsafeJson);
            var loaded = restarted.LoadAllAsync().GetAwaiter().GetResult();
            var codes = loaded.Diagnostics.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            Require(codes.Contains("artifact.corrupt") && codes.Contains("artifact.oversize") && codes.Contains("artifact.migration_required"),
                "ledger fallback diagnostics did not distinguish corrupt, oversize, and migration cases");
            Require(codes.Any(code => code.Contains("privacy.secret", StringComparison.Ordinal)), "private ledger did not fail closed at load");
            Require(loaded.Diagnostics.All(item => ArenaContractPrivacyRules.IsSafeRelativePath(item.RelativePath)), "ledger diagnostic leaked an unsafe path");

            var bounded = new ArenaClaimLedgerService(new(MaximumClaims: 1));
            var one = bounded.Create("ledger:bounded", "experiment:one", "branch:one", At);
            one = bounded.AddClaim(one, message, "agent:a", "First claim.", 0.5m, Observed("provenance:one"), []);
            var secondMessage = new DialogueMessage { MessageId = "message:second", SpeakerId = "agent:a", Speaker = "A", Text = "Second raw text.", CreatedAt = 2 };
            RequireThrows<InvalidOperationException>(() => bounded.AddClaim(
                one,
                secondMessage,
                "agent:a",
                "Second claim.",
                0.5m,
                Observed("provenance:two"),
                []), "claim service exceeded its record bound");

            var concurrencyRoot = TemporaryRoot();
            try
            {
                var raceService = new ArenaClaimLedgerService(new(MaximumClaims: 8, MaximumEvidence: 16));
                var baseLedger = raceService.Create("ledger:concurrent", "experiment:one", "branch:one", At);
                var seedMessage = new DialogueMessage
                {
                    MessageId = "message:concurrent-seed",
                    SpeakerId = "agent:a",
                    Speaker = "A",
                    Text = "Seed raw text.",
                    CreatedAt = 1
                };
                baseLedger = raceService.AddClaim(
                    baseLedger,
                    seedMessage,
                    "agent:a",
                    "The persisted seed claim.",
                    0.5m,
                    Observed("provenance:concurrent-seed"),
                    [],
                    claimId: "claim:concurrent-seed");
                var firstStore = new ArenaClaimLedgerStore(concurrencyRoot);
                var secondStore = new ArenaClaimLedgerStore(concurrencyRoot);
            Require(firstStore.SaveAsync(baseLedger).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Written,
                    "concurrent ledger seed was not persisted");
                var forgedStatus = baseLedger with
                {
                    Claims = [baseLedger.Claims.Single() with { Status = ArenaClaimStatus.Withdrawn }]
                };
                Require(firstStore.SaveAsync(forgedStatus).GetAwaiter().GetResult().Diagnostics.Any(item => item.Code == "claim_ledger.stale_or_destructive"),
                    "direct persistence rewrote claim status without reviewer/provenance evidence");
                var firstBranch = raceService.AddClaim(
                    baseLedger,
                    new DialogueMessage { MessageId = "message:branch-a", SpeakerId = "agent:a", Speaker = "A", Text = "Branch A.", CreatedAt = 2 },
                    "agent:a",
                    "The first concurrent branch claim.",
                    0.6m,
                    Observed("provenance:branch-a"),
                    [],
                    claimId: "claim:branch-a");
                var secondBranch = raceService.AddClaim(
                    baseLedger,
                    new DialogueMessage { MessageId = "message:branch-b", SpeakerId = "agent:b", Speaker = "B", Text = "Branch B.", CreatedAt = 3 },
                    "agent:b",
                    "The second concurrent branch claim.",
                    0.7m,
                    Observed("provenance:branch-b"),
                    [],
                    claimId: "claim:branch-b");
                var writes = RunConcurrently(
                    () => firstStore.SaveAsync(firstBranch),
                    () => secondStore.SaveAsync(secondBranch));
                Require(writes.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Written) == 1,
                    "conflicting ledger append branches both claimed to persist");
                Require(writes.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected) == 1
                        && writes.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected)
                            .Diagnostics.Any(item => item.Code == "claim_ledger.stale_or_destructive"),
                    "the losing ledger append branch was not rejected as stale");
                var persisted = firstStore.LoadAllAsync().GetAwaiter().GetResult().Artifacts.Single();
                var winner = writes.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Written).Artifact!;
                Require(ArenaContractCodec.Serialize(persisted) == ArenaContractCodec.Serialize(winner),
                    "a losing concurrent ledger branch silently replaced the winner");
                Require(persisted.Claims.Any(item => item.Id == "claim:concurrent-seed") && persisted.Claims.Length == 2,
                    "concurrent ledger persistence lost existing audit evidence");
                Require(!Directory.EnumerateFiles(concurrencyRoot, "*.tmp", SearchOption.AllDirectories).Any()
                        && !Directory.EnumerateFiles(concurrencyRoot, "*.lock", SearchOption.AllDirectories).Any(),
                    "cross-instance ledger persistence left lease or temporary files");
            }
            finally
            {
                Directory.Delete(concurrencyRoot, recursive: true);
            }
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic ledger persistence left temporary files");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArenaRubricEvaluationResultContract SingleSubjectResult(ArenaRubricContract rubric)
    {
        var service = new ArenaRubricService();
        return service.CreateSingleSubjectResult(
            rubric,
            "evaluation:one",
            "trial:one",
            [
                Submission("result:det", "evaluator:det", ArenaRubricJudgmentSource.Deterministic, null, 8m, 6m, observed: true),
                Submission("result:human", "evaluator:human", ArenaRubricJudgmentSource.Human, "reviewer:local", 6m, 4m, observed: true),
                Submission("result:model", "evaluator:model", ArenaRubricJudgmentSource.ModelJudge, null, 4m, 2m, observed: false)
            ],
            At,
            At.AddMinutes(1),
            [Observed("evidence:evaluation")]);
    }

    private static ArenaRubricEvaluationResultContract PersistableSingleSubjectResult(ArenaRubricContract rubric)
    {
        var service = new ArenaRubricService();
        return service.CreateSingleSubjectResult(
            rubric,
            "evaluation:one",
            "trial:one",
            [
                Submission("result:det", "evaluator:det", ArenaRubricJudgmentSource.Deterministic, null, 8m, 6m, observed: true),
                Submission("result:human", "evaluator:human", ArenaRubricJudgmentSource.Human, "reviewer:local", 6m, 4m, observed: true),
                new(
                    "result:model-unavailable",
                    "evaluator:model",
                    ArenaRubricJudgmentSource.ModelJudge,
                    null,
                    null,
                    [
                        new("criterion:accuracy", null, null, Unavailable("evidence:model-unavailable:accuracy")),
                        new("criterion:clarity", null, null, Unavailable("evidence:model-unavailable:clarity"))
                    ],
                    Unavailable("provenance:model-unavailable"))
            ],
            At,
            At.AddMinutes(1),
            [Observed("evidence:evaluation")]);
    }

    private static ArenaRubricJudgmentSubmission Submission(
        string id,
        string evaluatorId,
        ArenaRubricJudgmentSource source,
        string? reviewerId,
        decimal accuracy,
        decimal clarity,
        bool observed)
    {
        ArenaEvidenceAssertion Evidence(string suffix) => observed ? Observed($"evidence:{id}:{suffix}") : Inferred($"evidence:{id}:{suffix}");
        return new(
            id,
            evaluatorId,
            source,
            reviewerId,
            null,
            [new("criterion:accuracy", accuracy, null, Evidence("accuracy")), new("criterion:clarity", clarity, null, Evidence("clarity"))],
            observed ? Observed($"provenance:{id}") : Inferred($"provenance:{id}"));
    }

    private static ArenaRubricContract Rubric() => new(
        ArenaContractSchemas.Rubric,
        "rubric:quality",
        At,
        "Quality",
        "1.0.0",
        [
            new("evaluator:det", ArenaRubricEvaluatorKind.Deterministic, "profile:det-v1"),
            new("evaluator:human", ArenaRubricEvaluatorKind.Human, null),
            new("evaluator:model", ArenaRubricEvaluatorKind.ModelJudge, "profile:model-v1")
        ],
        [
            new("criterion:accuracy", "Accuracy", "Checks bounded factual alignment.", 0.6m, 0m, 10m),
            new("criterion:clarity", "Clarity", "Checks bounded presentation quality.", 0.4m, 0m, 10m)
        ],
        [Observed("evidence:rubric")]);

    private static ArenaRubricContract BlindRubric() => Rubric() with
    {
        Id = "rubric:blind",
        Name = "Blind quality",
        Evaluators = [new("evaluator:blind", ArenaRubricEvaluatorKind.BlindPairwise, null)]
    };

    private static ArenaEvidenceAssertion Observed(string id) =>
        new(id, ArenaEvidenceState.Observed, "Recorded by deterministic local test.", "artifact:test");

    private static ArenaEvidenceAssertion Inferred(string id) =>
        new(id, ArenaEvidenceState.Inferred, "Model assertion or derived opinion.", Basis: "versioned local test profile");

    private static ArenaEvidenceAssertion Unavailable(string id) =>
        new(id, ArenaEvidenceState.Unavailable, "Evidence was unavailable.", Limitation: "Source did not provide trustworthy evidence.");

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TracedJudgeProvider(ProviderRequestTraceStore traces, DateTimeOffset observedAt) : IModelProviderClient
    {
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", observedAt));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var context = config.RequestInspectionContext ?? throw new InvalidOperationException("judge request had no inspection context");
            var requestId = $"request:{context.CorrelationId[..16]}";
            traces.ObserveRequest(new ProviderRequestTrace(
                requestId,
                observedAt,
                context.CorrelationId,
                context.Phase,
                config.ApiMode,
                "test_chat",
                config.Model,
                false,
                false,
                1,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("exact-test-payload"))),
                18,
                "{\"redacted\":true}",
                false,
                [],
                context.Explanations,
                ProviderTokenEvidence.Unavailable("not supplied"),
                ProviderTokenEvidence.Unavailable("not supplied"),
                ProviderTokenEvidence.Unavailable("not supplied"),
                "succeeded"));
            return Task.FromResult(new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                "{\"criteria\":[{\"criterionId\":\"criterion:accuracy\",\"score\":7},{\"criterionId\":\"criterion:clarity\",\"score\":4}]}" ,
                "",
                12,
                20,
                10,
                30,
                "",
                observedAt,
                ResponseId: "response:test"));
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-rubric-claim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static T[] RunConcurrently<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        Task<T> Launch(Func<Task<T>> action) => Task.Run(async () =>
        {
            ready.Signal();
            start.Wait();
            return await action().ConfigureAwait(false);
        });

        var firstTask = Launch(first);
        var secondTask = Launch(second);
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            start.Set();
            throw new InvalidOperationException("Concurrent store writers did not reach their deterministic start barrier.");
        }
        start.Set();
        return Task.WhenAll(firstTask, secondTask).GetAwaiter().GetResult();
    }

    private static string Format(ImmutableArray<ArenaContractValidationIssue> issues) =>
        string.Join(" | ", issues.Select(item => $"{item.Code}:{item.Path}"));

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
