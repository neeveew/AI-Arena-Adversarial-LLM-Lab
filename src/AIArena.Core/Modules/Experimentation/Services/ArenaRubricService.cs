using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Deterministic rubric result builder. It calculates weighted normalized
/// scores, keeps evaluator sources partitioned, and retains disagreement rather
/// than collapsing observations into a synthetic consensus.
/// </summary>
public sealed class ArenaRubricService
{
    public static ArenaContractValidationResult ValidateFinalizedResult(
        ArenaRubricContract? rubric,
        ArenaRubricEvaluationResultContract? result) =>
        ValidateFinalizedResult(rubric, result, [], [], []);

    public static ArenaContractValidationResult ValidateFinalizedResult(
        ArenaRubricContract? rubric,
        ArenaRubricEvaluationResultContract? result,
        ImmutableArray<ArenaBlindPairwiseCommitmentContract> blindCommitments,
        ImmutableArray<ArenaBlindPairwiseReceiptContract> blindReceipts,
        ImmutableArray<ArenaModelJudgeReceiptContract> modelJudgeReceipts)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (rubric is null)
        {
            issues.Add(new("rubric_result.rubric_missing", "$.rubricId", "The referenced immutable rubric is unavailable."));
            return new(issues.ToImmutable());
        }
        if (result is null)
        {
            issues.Add(new("contract.null", "$", "Result is required."));
            return new(issues.ToImmutable());
        }

        var contractValidation = ArenaRubricResultCodec.Validate(result);
        issues.AddRange(contractValidation.Issues);
        if (!string.Equals(result.RubricId, rubric.Id, StringComparison.Ordinal)
            || !string.Equals(result.RubricVersion, rubric.Version, StringComparison.Ordinal))
        {
            issues.Add(new("rubric_result.rubric_reference", "$.rubricId", "Result rubric identity/version does not match the referenced immutable rubric."));
        }
        if (issues.Count > 0)
        {
            return new(issues.ToImmutable());
        }

        ValidateBlindEvidence(rubric, result, blindCommitments, blindReceipts, issues);
        ValidateModelJudgeEvidence(rubric, result, modelJudgeReceipts, issues);
        if (issues.Count > 0)
        {
            return new(issues.ToImmutable());
        }

        try
        {
            var submissions = result.DeterministicResults
                .Concat(result.HumanResults)
                .Concat(result.ModelJudgeResults)
                .Select(item => new ArenaRubricJudgmentSubmission(
                    item.Id,
                    item.EvaluatorId,
                    item.Source,
                    item.ReviewerId,
                    item.PairwisePreference,
                    [.. item.Criteria.Select(criterion => new ArenaRubricCriterionSubmission(
                        criterion.CriterionId,
                        criterion.ScoreA,
                        criterion.ScoreB,
                        criterion.Evidence))],
                    item.Provenance))
                .ToImmutableArray();
            var subjectA = result.IsBlindPairwise
                ? result.BlindReveal!.LabelAReferenceId
                : result.SubjectReferenceIds[0];
            var subjectB = result.IsBlindPairwise
                ? result.BlindReveal!.LabelBReferenceId
                : null;
            var rebuilt = new ArenaRubricService().CreateResult(
                rubric,
                result.Id,
                subjectA,
                subjectB,
                submissions,
                result.CreatedAtUtc,
                result.FinalizedAtUtc,
                result.Evidence,
                result.BlindReveal);
            if (!string.Equals(
                    ArenaRubricResultCodec.Serialize(rebuilt),
                    ArenaRubricResultCodec.Serialize(result),
                    StringComparison.Ordinal))
            {
                issues.Add(new(
                    "rubric_result.derived_mismatch",
                    "$",
                    "Stored evaluator metadata, weighted scores, or disagreements do not match deterministic derivation from the referenced rubric."));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            issues.Add(new(
                "rubric_result.derivation_invalid",
                "$",
                "Result criteria/evaluators cannot be derived from the referenced immutable rubric."));
        }

        return new(issues.ToImmutable());
    }

    private static void ValidateBlindEvidence(
        ArenaRubricContract rubric,
        ArenaRubricEvaluationResultContract result,
        ImmutableArray<ArenaBlindPairwiseCommitmentContract> commitments,
        ImmutableArray<ArenaBlindPairwiseReceiptContract> receipts,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!result.IsBlindPairwise) return;
        var evaluatorResults = result.HumanResults.Concat(result.ModelJudgeResults).ToArray();
        foreach (var evaluatorResult in evaluatorResults)
        {
            var matches = receipts.Where(item =>
                item.EvaluationId == result.Id && item.EvaluatorResultId == evaluatorResult.Id).ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new(
                    "rubric_result.blind_commitment_unavailable",
                    "$.isBlindPairwise",
                    "Each blind judgment requires exactly one persisted post-judgment receipt linked to a pre-judgment commitment."));
                continue;
            }
            var receipt = matches[0];
            var commitmentMatches = commitments.Where(item => item.Id == receipt.CommitmentId).ToArray();
            if (commitmentMatches.Length != 1)
            {
                issues.Add(new("rubric_result.blind_commitment_missing", "$.isBlindPairwise", "Blind receipt references no unique persisted commitment."));
                continue;
            }
            var commitment = commitmentMatches[0];
            var proofReference = result.Evidence.Any(item =>
                item.State == ArenaEvidenceState.Observed && item.ReferenceId == receipt.Id);
            var evaluatorEligible = rubric.Evaluators.Any(item =>
                item.Id == evaluatorResult.EvaluatorId && item.Kind == ArenaRubricEvaluatorKind.BlindPairwise);
            var reveal = result.BlindReveal!;
            var view = new ArenaBlindPairwiseView(
                result.Id,
                rubric.Id,
                rubric.Version,
                commitment.LabelAToken,
                commitment.LabelBToken);
            var relationshipValid = ArenaEvaluationEvidenceCodec.Validate(commitment).IsValid
                && ArenaEvaluationEvidenceCodec.Validate(receipt).IsValid
                && commitment.EvaluationId == result.Id
                && commitment.Id == StableProofId("blind-commitment", commitment.EvaluationId, commitment.EvaluatorId)
                && commitment.RubricId == rubric.Id
                && commitment.RubricVersion == rubric.Version
                && commitment.EvaluatorId == evaluatorResult.EvaluatorId
                && receipt.Source == evaluatorResult.Source
                && receipt.Id == StableProofId("blind-receipt", receipt.EvaluationId, receipt.EvaluatorResultId, receipt.CommitmentId)
                && receipt.CreatedAtUtc >= commitment.CreatedAtUtc
                && receipt.CreatedAtUtc <= result.FinalizedAtUtc
                && receipt.LabelAReferenceId == reveal.LabelAReferenceId
                && receipt.LabelBReferenceId == reveal.LabelBReferenceId
                && receipt.EvaluatorResultSha256 == ArenaEvaluationEvidenceCodec.Fingerprint(evaluatorResult)
                && commitment.SubjectSetSha256 == ArenaEvaluationEvidenceCodec.Sha256(string.Join("\n", result.SubjectReferenceIds.Order(StringComparer.Ordinal)))
                && commitment.MappingCommitmentSha256 == BlindMappingCommitment(
                    view,
                    receipt.LabelAReferenceId,
                    receipt.LabelBReferenceId,
                    receipt.MappingNonce,
                    evaluatorResult.EvaluatorId)
                && proofReference
                && evaluatorEligible;
            if (!relationshipValid)
            {
                issues.Add(new(
                    "rubric_result.blind_commitment_invalid",
                    "$.isBlindPairwise",
                    "Blind commitment, reveal, evaluator, timing, fingerprint, and result evidence do not form one valid two-step proof chain."));
            }
        }
        if (result.DeterministicResults.Length > 0 || evaluatorResults.Length == 0)
        {
            issues.Add(new("rubric_result.blind_source", "$.results", "Blind pairwise results require human or model-judge observations from a blind_pairwise evaluator."));
        }
    }

    private static void ValidateModelJudgeEvidence(
        ArenaRubricContract rubric,
        ArenaRubricEvaluationResultContract result,
        ImmutableArray<ArenaModelJudgeReceiptContract> receipts,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        foreach (var evaluatorResult in result.ModelJudgeResults)
        {
            var available = evaluatorResult.Criteria.Any(criterion =>
                criterion.ScoreA is not null || criterion.ScoreB is not null || criterion.Evidence.State != ArenaEvidenceState.Unavailable);
            if (!available) continue;
            var matches = receipts.Where(item =>
                item.EvaluationId == result.Id && item.EvaluatorResultId == evaluatorResult.Id).ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new(
                    "rubric_result.model_judge_evidence_unbound",
                    "$.modelJudgeResults",
                    "Each available model-judge score requires exactly one immutable succeeded provider-attempt receipt."));
                continue;
            }
            var receipt = matches[0];
            var evaluator = rubric.Evaluators.SingleOrDefault(item => item.Id == evaluatorResult.EvaluatorId);
            var receiptReferences = evaluatorResult.Criteria.All(item => item.Evidence.ReferenceId == receipt.Id)
                && evaluatorResult.Provenance.ReferenceId == receipt.Id
                && result.Evidence.Any(item => item.State == ArenaEvidenceState.Observed && item.ReferenceId == receipt.Id);
            var relationshipValid = ArenaEvaluationEvidenceCodec.Validate(receipt).IsValid
                && receipt.RubricId == rubric.Id
                && receipt.Id == StableProofId("model-judge-receipt", receipt.EvaluationId, receipt.EvaluatorId, receipt.CorrelationId, receipt.RequestId)
                && receipt.RubricVersion == rubric.Version
                && receipt.EvaluatorId == evaluatorResult.EvaluatorId
                && receipt.ProfileId == evaluatorResult.ProfileId
                && evaluator is not null
                && (evaluator.Kind == ArenaRubricEvaluatorKind.ModelJudge
                    || result.IsBlindPairwise && evaluator.Kind == ArenaRubricEvaluatorKind.BlindPairwise)
                && receipt.SubjectReferenceIds.SequenceEqual(result.SubjectReferenceIds)
                && receipt.RequestObservedAtUtc >= result.CreatedAtUtc.AddSeconds(-1)
                && receipt.RequestObservedAtUtc <= receipt.CreatedAtUtc.AddSeconds(1)
                && receipt.CreatedAtUtc <= result.FinalizedAtUtc
                && receipt.EvaluatorResultSha256 == ArenaEvaluationEvidenceCodec.Fingerprint(evaluatorResult)
                && receiptReferences;
            if (!relationshipValid)
            {
                issues.Add(new(
                    "rubric_result.model_judge_evidence_invalid",
                    "$.modelJudgeResults",
                    "Model-judge receipt does not match the immutable rubric, exact evaluator result, subject set, request timing, or result evidence references."));
            }
        }
    }

    public ArenaRubricEvaluationResultContract CreateSingleSubjectResult(
        ArenaRubricContract rubric,
        string evaluationId,
        string subjectReferenceId,
        ImmutableArray<ArenaRubricJudgmentSubmission> submissions,
        DateTimeOffset createdAtUtc,
        DateTimeOffset finalizedAtUtc,
        ImmutableArray<ArenaEvidenceAssertion> evidence) =>
        CreateResult(
            rubric,
            evaluationId,
            subjectReferenceId,
            null,
            submissions,
            createdAtUtc,
            finalizedAtUtc,
            evidence,
            blindReveal: null);

    public ArenaBlindPairwiseSession BeginBlindPairwise(
        ArenaRubricContract rubric,
        string evaluationId,
        string firstSubjectReferenceId,
        string secondSubjectReferenceId,
        string seed,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        var evaluatorId = rubric.Evaluators
            .Where(item => item.Kind == ArenaRubricEvaluatorKind.BlindPairwise)
            .Select(item => item.Id)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Blind pairwise evaluation requires a rubric evaluator explicitly declared as blind_pairwise.");
        return BeginBlindPairwise(
            rubric,
            evaluatorId,
            evaluationId,
            firstSubjectReferenceId,
            secondSubjectReferenceId,
            seed,
            createdAtUtc);
    }

    public ArenaBlindPairwiseSession BeginBlindPairwise(
        ArenaRubricContract rubric,
        string evaluatorId,
        string evaluationId,
        string firstSubjectReferenceId,
        string secondSubjectReferenceId,
        string seed,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstSubjectReferenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondSubjectReferenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        if (firstSubjectReferenceId == secondSubjectReferenceId)
            throw new ArgumentException("Blind pairwise candidates must differ.", nameof(secondSubjectReferenceId));
        if (!ArenaContractCodec.Validate(rubric).IsValid)
            throw new InvalidDataException("Rubric does not satisfy its frozen v1 contract.");
        if (!rubric.Evaluators.Any(item => item.Kind == ArenaRubricEvaluatorKind.BlindPairwise && item.Id == evaluatorId))
            throw new InvalidDataException("The selected evaluator is not declared as blind_pairwise by this rubric version.");
        RequireUtc(createdAtUtc, nameof(createdAtUtc));

        var sorted = new[] { firstSubjectReferenceId, secondSubjectReferenceId }
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{evaluationId}\n{seed}\n{sorted[0]}\n{sorted[1]}"));
        var labelA = (digest[0] & 1) == 0 ? sorted[0] : sorted[1];
        var labelB = labelA == sorted[0] ? sorted[1] : sorted[0];
        var tokenSeed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{evaluationId}\n{seed}\nblind-view")));
        var view = new ArenaBlindPairwiseView(
            evaluationId,
            rubric.Id,
            rubric.Version,
            $"subject:a:{tokenSeed[..20]}",
            $"subject:b:{tokenSeed[20..40]}");
        var mappingNonce = ArenaEvaluationEvidenceCodec.Sha256($"{seed}\n{evaluationId}\n{rubric.Id}\n{rubric.Version}\n{evaluatorId}\nblind-mapping-nonce");
        var commitment = new ArenaBlindPairwiseCommitmentContract(
            ArenaEvaluationEvidenceSchemas.BlindPairwiseCommitment,
            StableProofId("blind-commitment", evaluationId, evaluatorId),
            createdAtUtc,
            evaluationId,
            rubric.Id,
            rubric.Version,
            evaluatorId,
            view.LabelAToken,
            view.LabelBToken,
            ArenaEvaluationEvidenceCodec.Sha256(string.Join("\n", sorted)),
            BlindMappingCommitment(view, labelA, labelB, mappingNonce, evaluatorId));
        var validation = ArenaEvaluationEvidenceCodec.Validate(commitment);
        if (!validation.IsValid)
            throw new InvalidDataException("Blind commitment could not satisfy its strict evidence contract.");
        return new ArenaBlindPairwiseSession(this, rubric, view, commitment, labelA, labelB, mappingNonce, createdAtUtc);
    }

    internal static string BlindMappingCommitment(
        ArenaBlindPairwiseView view,
        string labelAReferenceId,
        string labelBReferenceId,
        string mappingNonce,
        string evaluatorId) =>
        ArenaEvaluationEvidenceCodec.Sha256(string.Join(
            "\n",
            view.EvaluationId,
            view.RubricId,
            view.RubricVersion,
            evaluatorId,
            view.LabelAToken,
            labelAReferenceId,
            view.LabelBToken,
            labelBReferenceId,
            mappingNonce));

    internal static string StableProofId(string prefix, params string[] values) =>
        $"{prefix}:{ArenaEvaluationEvidenceCodec.Sha256(string.Join("\n", values))[..24]}";

    internal ArenaRubricEvaluationResultContract CreateResult(
        ArenaRubricContract rubric,
        string evaluationId,
        string subjectAReferenceId,
        string? subjectBReferenceId,
        ImmutableArray<ArenaRubricJudgmentSubmission> submissions,
        DateTimeOffset createdAtUtc,
        DateTimeOffset finalizedAtUtc,
        ImmutableArray<ArenaEvidenceAssertion> evidence,
        ArenaBlindPairwiseReveal? blindReveal)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        if (!ArenaContractCodec.Validate(rubric).IsValid)
            throw new InvalidDataException("Rubric does not satisfy its frozen v1 contract.");
        ArgumentException.ThrowIfNullOrWhiteSpace(evaluationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectAReferenceId);
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        RequireUtc(finalizedAtUtc, nameof(finalizedAtUtc));
        if (finalizedAtUtc < createdAtUtc) throw new ArgumentOutOfRangeException(nameof(finalizedAtUtc));
        if (submissions.IsDefault) throw new ArgumentException("Submissions must be initialized.", nameof(submissions));
        if (evidence.IsDefault) throw new ArgumentException("Evidence must be initialized.", nameof(evidence));

        var pairwise = subjectBReferenceId is not null;
        if (pairwise != (blindReveal is not null))
            throw new ArgumentException("Pairwise results require a finalized blind reveal.", nameof(blindReveal));
        if (pairwise && subjectAReferenceId == subjectBReferenceId)
            throw new ArgumentException("Pairwise subjects must differ.", nameof(subjectBReferenceId));

        var evaluators = rubric.Evaluators.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var criteria = rubric.Criteria.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var expectedCriterionIds = criteria.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var results = ImmutableArray.CreateBuilder<ArenaRubricEvaluatorResult>();
        foreach (var submission in submissions.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (submission is null) throw new InvalidDataException("Submission cannot be null.");
            if (!evaluators.TryGetValue(submission.EvaluatorId, out var evaluator))
                throw new InvalidDataException($"Evaluator '{submission.EvaluatorId}' is not defined by rubric '{rubric.Id}'.");
            ValidateEvaluatorSource(evaluator, submission.Source, pairwise);
            if (submission.Source == ArenaRubricJudgmentSource.Human && string.IsNullOrWhiteSpace(submission.ReviewerId))
                throw new InvalidDataException("Human scores require reviewer provenance.");
            if (submission.Source != ArenaRubricJudgmentSource.Human && submission.ReviewerId is not null)
                throw new InvalidDataException("Only human scores carry reviewer provenance.");
            if (pairwise != (submission.PairwisePreference is not null))
                throw new InvalidDataException("Pairwise preference does not match evaluation mode.");
            if (submission.Criteria.IsDefault)
                throw new InvalidDataException("Criterion submissions must be initialized.");
            var submissionIds = submission.Criteria.Select(item => item.CriterionId).OrderBy(item => item, StringComparer.Ordinal).ToArray();
            if (!submissionIds.SequenceEqual(expectedCriterionIds, StringComparer.Ordinal))
                throw new InvalidDataException("Each evaluator must report every rubric criterion exactly once.");

            var criterionResults = ImmutableArray.CreateBuilder<ArenaRubricCriterionResult>();
            foreach (var item in submission.Criteria.OrderBy(item => item.CriterionId, StringComparer.Ordinal))
            {
                var criterion = criteria[item.CriterionId];
                ValidateCriterionSubmission(item, criterion, submission.Source, pairwise);
                criterionResults.Add(new(item.CriterionId, item.ScoreA, item.ScoreB, item.Evidence));
            }

            var resultCriteria = criterionResults.ToImmutable();
            results.Add(new(
                submission.Id,
                submission.EvaluatorId,
                submission.Source,
                submission.ReviewerId,
                evaluator.ProfileId,
                submission.PairwisePreference,
                resultCriteria,
                WeightedScore(rubric.Criteria, resultCriteria, static item => item.ScoreA),
                pairwise ? WeightedScore(rubric.Criteria, resultCriteria, static item => item.ScoreB) : null,
                submission.Provenance));
        }

        var ordered = results.ToImmutable();
        var deterministic = ordered.Where(item => item.Source == ArenaRubricJudgmentSource.Deterministic).ToImmutableArray();
        var human = ordered.Where(item => item.Source == ArenaRubricJudgmentSource.Human).ToImmutableArray();
        var model = ordered.Where(item => item.Source == ArenaRubricJudgmentSource.ModelJudge).ToImmutableArray();
        var subjects = pairwise
            ? ImmutableArray.Create(subjectAReferenceId, subjectBReferenceId!).Sort(StringComparer.Ordinal)
            : [subjectAReferenceId];
        var contract = new ArenaRubricEvaluationResultContract(
            ArenaRubricResultSchemas.RubricResult,
            evaluationId,
            createdAtUtc,
            finalizedAtUtc,
            rubric.Id,
            rubric.Version,
            subjects,
            pairwise,
            blindReveal,
            deterministic,
            human,
            model,
            BuildDisagreements(ordered, pairwise),
            [.. evidence.OrderBy(item => item.Id, StringComparer.Ordinal)]);

        var validation = ArenaRubricResultCodec.Validate(contract);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
        }
        return contract;
    }

    private static void ValidateEvaluatorSource(
        ArenaRubricEvaluator evaluator,
        ArenaRubricJudgmentSource source,
        bool pairwise)
    {
        var valid = evaluator.Kind switch
        {
            ArenaRubricEvaluatorKind.Deterministic => !pairwise && source == ArenaRubricJudgmentSource.Deterministic,
            ArenaRubricEvaluatorKind.Human => !pairwise && source == ArenaRubricJudgmentSource.Human,
            ArenaRubricEvaluatorKind.ModelJudge => !pairwise && source == ArenaRubricJudgmentSource.ModelJudge,
            ArenaRubricEvaluatorKind.BlindPairwise => pairwise && source is ArenaRubricJudgmentSource.Human or ArenaRubricJudgmentSource.ModelJudge,
            _ => false
        };
        if (!valid) throw new InvalidDataException($"Evaluator '{evaluator.Id}' cannot emit a '{source}' result in this mode.");
    }

    private static void ValidateCriterionSubmission(
        ArenaRubricCriterionSubmission value,
        ArenaRubricCriterion criterion,
        ArenaRubricJudgmentSource source,
        bool pairwise)
    {
        if (!pairwise && value.ScoreB is not null)
            throw new InvalidDataException("Single-subject results cannot score subject B.");
        var missing = value.ScoreA is null || (pairwise && value.ScoreB is null);
        if (value.Evidence.State == ArenaEvidenceState.Unavailable)
        {
            if (value.ScoreA is not null || value.ScoreB is not null)
                throw new InvalidDataException("Unavailable criterion evidence cannot carry scores.");
        }
        else if (missing)
        {
            throw new InvalidDataException("Missing criterion scores require unavailable evidence.");
        }

        ValidateScore(value.ScoreA, criterion);
        ValidateScore(value.ScoreB, criterion);
        var allowedEvidence = source == ArenaRubricJudgmentSource.ModelJudge
            ? value.Evidence.State is ArenaEvidenceState.Inferred or ArenaEvidenceState.Unavailable
            : value.Evidence.State is ArenaEvidenceState.Observed or ArenaEvidenceState.Unavailable;
        if (!allowedEvidence)
            throw new InvalidDataException(source == ArenaRubricJudgmentSource.ModelJudge
                ? "Model judgment is opinion and cannot be labelled as an observed measurement."
                : "Deterministic and human result capture must be observed or unavailable.");
    }

    private static void ValidateScore(decimal? score, ArenaRubricCriterion criterion)
    {
        if (score is not null && (score < criterion.MinimumScore || score > criterion.MaximumScore))
            throw new InvalidDataException($"Criterion '{criterion.Id}' score is outside its rubric range.");
    }

    private static decimal? WeightedScore(
        ImmutableArray<ArenaRubricCriterion> rubricCriteria,
        ImmutableArray<ArenaRubricCriterionResult> resultCriteria,
        Func<ArenaRubricCriterionResult, decimal?> selector)
    {
        var byId = resultCriteria.ToDictionary(item => item.CriterionId, StringComparer.Ordinal);
        decimal total = 0;
        foreach (var criterion in rubricCriteria)
        {
            var score = selector(byId[criterion.Id]);
            if (score is null) return null;
            var normalized = (score.Value - criterion.MinimumScore) / (criterion.MaximumScore - criterion.MinimumScore);
            total += normalized * criterion.Weight;
        }
        return decimal.Round(total, 8, MidpointRounding.ToEven);
    }

    private static ImmutableArray<ArenaRubricDisagreement> BuildDisagreements(
        ImmutableArray<ArenaRubricEvaluatorResult> results,
        bool pairwise)
    {
        var values = ImmutableArray.CreateBuilder<ArenaRubricDisagreement>();
        var criterionIds = results.SelectMany(item => item.Criteria).Select(item => item.CriterionId)
            .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal);
        foreach (var criterionId in criterionIds)
        {
            AddDisagreement(values, results, criterionId, "a", static item => item.ScoreA);
            if (pairwise) AddDisagreement(values, results, criterionId, "b", static item => item.ScoreB);
        }
        return [.. values.OrderBy(item => item.Id, StringComparer.Ordinal)];
    }

    private static void AddDisagreement(
        ImmutableArray<ArenaRubricDisagreement>.Builder values,
        ImmutableArray<ArenaRubricEvaluatorResult> results,
        string criterionId,
        string subjectLabel,
        Func<ArenaRubricCriterionResult, decimal?> selector)
    {
        var observations = results
            .Select(result => (result.Id, Score: selector(result.Criteria.Single(item => item.CriterionId == criterionId))))
            .Where(item => item.Score is not null)
            .Select(item => (item.Id, Score: item.Score!.Value))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (observations.Length < 2) return;
        var minimum = observations.Min(item => item.Score);
        var maximum = observations.Max(item => item.Score);
        if (minimum == maximum) return;
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{criterionId}\n{subjectLabel}")))[..20];
        values.Add(new(
            $"disagreement:{digest}",
            criterionId,
            subjectLabel,
            minimum,
            maximum,
            [.. observations.Select(item => item.Id).OrderBy(item => item, StringComparer.Ordinal)]));
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
    }
}

public sealed class ArenaBlindPairwiseSession
{
    private const int MaximumJudgeContentCharacters = 32 * 1024;
    private readonly ArenaRubricService _service;
    private readonly ArenaRubricContract _rubric;
    private readonly string _labelAReferenceId;
    private readonly string _labelBReferenceId;
    private readonly string _mappingNonce;
    private readonly DateTimeOffset _createdAtUtc;
    private int _finalized;

    internal ArenaBlindPairwiseSession(
        ArenaRubricService service,
        ArenaRubricContract rubric,
        ArenaBlindPairwiseView view,
        ArenaBlindPairwiseCommitmentContract commitment,
        string labelAReferenceId,
        string labelBReferenceId,
        string mappingNonce,
        DateTimeOffset createdAtUtc)
    {
        _service = service;
        _rubric = rubric;
        View = view;
        Commitment = commitment;
        _labelAReferenceId = labelAReferenceId;
        _labelBReferenceId = labelBReferenceId;
        _mappingNonce = mappingNonce;
        _createdAtUtc = createdAtUtc;
    }

    public ArenaBlindPairwiseView View { get; }

    public ArenaBlindPairwiseCommitmentContract Commitment { get; }

    public ArenaBlindPairwiseJudgeView CreateJudgeView(
        string firstSubjectReferenceId,
        string firstSubjectContent,
        string secondSubjectReferenceId,
        string secondSubjectContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstSubjectReferenceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondSubjectReferenceId);
        if (string.IsNullOrWhiteSpace(firstSubjectContent) || firstSubjectContent.Length > MaximumJudgeContentCharacters
            || string.IsNullOrWhiteSpace(secondSubjectContent) || secondSubjectContent.Length > MaximumJudgeContentCharacters)
            throw new InvalidDataException($"Blind judge subjects must each contain 1-{MaximumJudgeContentCharacters} characters.");
        var supplied = new[] { firstSubjectReferenceId, secondSubjectReferenceId }.ToHashSet(StringComparer.Ordinal);
        if (!supplied.SetEquals([_labelAReferenceId, _labelBReferenceId]))
            throw new InvalidDataException("Judge content references do not match the committed blind subjects.");
        var labelAContent = firstSubjectReferenceId == _labelAReferenceId ? firstSubjectContent : secondSubjectContent;
        var labelBContent = firstSubjectReferenceId == _labelBReferenceId ? firstSubjectContent : secondSubjectContent;
        return new(View.LabelAToken, labelAContent, View.LabelBToken, labelBContent);
    }

    public ArenaRubricEvaluationResultContract Finalize(
        ImmutableArray<ArenaRubricJudgmentSubmission> submissions,
        DateTimeOffset finalizedAtUtc,
        ImmutableArray<ArenaEvidenceAssertion> evidence)
    {
        if (Interlocked.Exchange(ref _finalized, 1) != 0)
            throw new InvalidOperationException("Blind evaluation is already finalized.");
        try
        {
            return _service.CreateResult(
                _rubric,
                View.EvaluationId,
                _labelAReferenceId,
                _labelBReferenceId,
                submissions,
                _createdAtUtc,
                finalizedAtUtc,
                evidence,
                new(_labelAReferenceId, _labelBReferenceId));
        }
        catch
        {
            Volatile.Write(ref _finalized, 0);
            throw;
        }
    }

    public ArenaBlindPairwiseFinalization FinalizeWithReceipt(
        ArenaRubricJudgmentSubmission submission,
        DateTimeOffset finalizedAtUtc,
        ImmutableArray<ArenaEvidenceAssertion> evidence)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!string.Equals(submission.EvaluatorId, Commitment.EvaluatorId, StringComparison.Ordinal))
            throw new InvalidDataException("Blind judgment does not use the evaluator bound by the persisted commitment.");
        var receiptId = ArenaRubricService.StableProofId("blind-receipt", View.EvaluationId, submission.Id, Commitment.Id);
        var proof = new ArenaEvidenceAssertion(
            ArenaRubricService.StableProofId("evidence", receiptId),
            ArenaEvidenceState.Observed,
            "A persisted blind judgment receipt opens the pre-judgment mapping commitment.",
            receiptId);
        var result = Finalize([submission], finalizedAtUtc, [.. evidence, proof]);
        var evaluatorResult = result.HumanResults.Concat(result.ModelJudgeResults).Single(item => item.Id == submission.Id);
        var receipt = new ArenaBlindPairwiseReceiptContract(
            ArenaEvaluationEvidenceSchemas.BlindPairwiseReceipt,
            receiptId,
            finalizedAtUtc,
            View.EvaluationId,
            Commitment.Id,
            evaluatorResult.Id,
            evaluatorResult.Source,
            _labelAReferenceId,
            _labelBReferenceId,
            _mappingNonce,
            ArenaEvaluationEvidenceCodec.Fingerprint(evaluatorResult));
        var validation = ArenaEvaluationEvidenceCodec.Validate(receipt);
        if (!validation.IsValid)
            throw new InvalidDataException("Blind receipt could not satisfy its strict evidence contract.");
        return new(result, receipt);
    }
}
