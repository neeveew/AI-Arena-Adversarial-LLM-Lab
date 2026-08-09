using System.Collections.Immutable;

namespace AIArena.Core.Models;

public static class ArenaEvaluationEvidenceSchemas
{
    public const string BlindPairwiseCommitment = "ai_arena.blind_pairwise_commitment.v1";
    public const string BlindPairwiseReceipt = "ai_arena.blind_pairwise_receipt.v1";
    public const string ModelJudgeReceipt = "ai_arena.model_judge_receipt.v1";
}

/// <summary>
/// Persisted before a blind judgment. It binds opaque labels to a hidden
/// mapping commitment without exposing either label's original subject.
/// </summary>
public sealed record ArenaBlindPairwiseCommitmentContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string EvaluationId,
    string RubricId,
    string RubricVersion,
    string EvaluatorId,
    string LabelAToken,
    string LabelBToken,
    string SubjectSetSha256,
    string MappingCommitmentSha256) : IArenaVersionedContract;

/// <summary>
/// Persisted after the judge submits and before the finalized result. The
/// nonce opens the earlier commitment and the evaluator fingerprint binds the
/// receipt to the exact scored observation.
/// </summary>
public sealed record ArenaBlindPairwiseReceiptContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string EvaluationId,
    string CommitmentId,
    string EvaluatorResultId,
    ArenaRubricJudgmentSource Source,
    string LabelAReferenceId,
    string LabelBReferenceId,
    string MappingNonce,
    string EvaluatorResultSha256) : IArenaVersionedContract;

/// <summary>
/// Privacy-safe immutable binding between an available model-judge score and
/// the exact physical provider attempt whose response was parsed. No prompt,
/// response content, endpoint, response ID, or credential is retained.
/// </summary>
public sealed record ArenaModelJudgeReceiptContract(
    string Schema,
    string Id,
    DateTimeOffset CreatedAtUtc,
    string EvaluationId,
    string RubricId,
    string RubricVersion,
    string EvaluatorId,
    string EvaluatorResultId,
    ImmutableArray<string> SubjectReferenceIds,
    string ProfileId,
    string ProviderProfileSha256,
    string RequestId,
    string CorrelationId,
    DateTimeOffset RequestObservedAtUtc,
    int RequestAttempt,
    string RequestPayloadSha256,
    string ProviderModelSha256,
    string ProviderOutcome,
    string CompletionSha256,
    string? ProviderResponseIdSha256,
    string EvaluatorResultSha256) : IArenaVersionedContract;

public sealed record ArenaBlindPairwiseFinalization(
    ArenaRubricEvaluationResultContract Result,
    ArenaBlindPairwiseReceiptContract Receipt);

public sealed record ArenaModelJudgeFinalization(
    ArenaRubricEvaluationResultContract Result,
    ArenaModelJudgeReceiptContract Receipt);
