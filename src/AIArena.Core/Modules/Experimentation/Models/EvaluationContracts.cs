using System.Collections.Immutable;

namespace AIArena.Core.Models;

/// <summary>
/// Rubric definitions use <see cref="ArenaContractSchemas.Rubric"/>. Evaluation
/// results have a separate schema because adding mutable observations to the
/// frozen definition contract would change its v1 meaning.
/// </summary>
public static class ArenaRubricResultSchemas
{
    public const string RubricResult = "ai_arena.rubric_result.v1";
}

public enum ArenaRubricJudgmentSource
{
    Deterministic,
    Human,
    ModelJudge
}

public enum ArenaPairwisePreference
{
    A,
    B,
    Tie,
    Unavailable
}

public sealed record ArenaRubricCriterionResult(
    string CriterionId,
    decimal? ScoreA,
    decimal? ScoreB,
    ArenaEvidenceAssertion Evidence);

/// <summary>
/// One evaluator's observation. Human, deterministic, and model observations
/// are persisted in separate arrays on the parent contract and are never folded
/// into a single supposedly measured score.
/// </summary>
public sealed record ArenaRubricEvaluatorResult(
    string Id,
    string EvaluatorId,
    ArenaRubricJudgmentSource Source,
    string? ReviewerId,
    string? ProfileId,
    ArenaPairwisePreference? PairwisePreference,
    ImmutableArray<ArenaRubricCriterionResult> Criteria,
    decimal? WeightedScoreA,
    decimal? WeightedScoreB,
    ArenaEvidenceAssertion Provenance);

public sealed record ArenaRubricDisagreement(
    string Id,
    string CriterionId,
    string SubjectLabel,
    decimal MinimumScore,
    decimal MaximumScore,
    ImmutableArray<string> ResultIds);

/// <summary>
/// The original identities appear only in a finalized result. Judge-facing
/// blind views contain opaque A/B tokens and never expose this mapping.
/// </summary>
public sealed record ArenaBlindPairwiseReveal(
    string LabelAReferenceId,
    string LabelBReferenceId);

public sealed record ArenaRubricEvaluationResultContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset FinalizedAtUtc,
    string RubricId,
    string RubricVersion,
    ImmutableArray<string> SubjectReferenceIds,
    bool IsBlindPairwise,
    ArenaBlindPairwiseReveal? BlindReveal,
    ImmutableArray<ArenaRubricEvaluatorResult> DeterministicResults,
    ImmutableArray<ArenaRubricEvaluatorResult> HumanResults,
    ImmutableArray<ArenaRubricEvaluatorResult> ModelJudgeResults,
    ImmutableArray<ArenaRubricDisagreement> Disagreements,
    ImmutableArray<ArenaEvidenceAssertion> Evidence) : IArenaVersionedContract;

public sealed record ArenaRubricCriterionSubmission(
    string CriterionId,
    decimal? ScoreA,
    decimal? ScoreB,
    ArenaEvidenceAssertion Evidence);

public sealed record ArenaRubricJudgmentSubmission(
    string Id,
    string EvaluatorId,
    ArenaRubricJudgmentSource Source,
    string? ReviewerId,
    ArenaPairwisePreference? PairwisePreference,
    ImmutableArray<ArenaRubricCriterionSubmission> Criteria,
    ArenaEvidenceAssertion Provenance);

/// <summary>
/// Safe judge-facing view. The opaque tokens are stable for the evaluation but
/// cannot be used to infer either original candidate reference.
/// </summary>
public sealed record ArenaBlindPairwiseView(
    string EvaluationId,
    string RubricId,
    string RubricVersion,
    string LabelAToken,
    string LabelBToken);

/// <summary>
/// Process-memory judge projection. It deliberately carries no stable subject
/// references and is not an IArenaVersionedContract or persistence artifact.
/// </summary>
public sealed record ArenaBlindPairwiseJudgeView(
    string LabelAToken,
    string LabelAContent,
    string LabelBToken,
    string LabelBContent);
