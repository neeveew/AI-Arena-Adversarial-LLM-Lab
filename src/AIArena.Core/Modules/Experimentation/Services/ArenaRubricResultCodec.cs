using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Strict canonical codec for finalized rubric observations. It intentionally
/// lives beside, rather than inside, the frozen v1 definition codec.
/// </summary>
public static class ArenaRubricResultCodec
{
    private const int MaximumResultsPerSource = 256;
    private const int MaximumCriteria = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    public static ArenaContractValidationResult Validate(ArenaRubricEvaluationResultContract? result)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (result is null)
        {
            issues.Add(new("contract.null", "$", "Result is required."));
            return new(issues.ToImmutable());
        }

        if (!string.Equals(result.Schema, ArenaRubricResultSchemas.RubricResult, StringComparison.Ordinal))
        {
            Add(issues, "schema.mismatch", "$.schema", $"Expected schema '{ArenaRubricResultSchemas.RubricResult}'.");
        }

        RequireId(result.Id, "$.id", issues);
        RequireUtc(result.CreatedAtUtc, "$.createdAtUtc", issues);
        RequireUtc(result.FinalizedAtUtc, "$.finalizedAtUtc", issues);
        if (result.FinalizedAtUtc < result.CreatedAtUtc)
        {
            Add(issues, "rubric_result.time_order", "$.finalizedAtUtc", "Finalization cannot precede creation.");
        }

        RequireId(result.RubricId, "$.rubricId", issues);
        RequireReference(result.RubricVersion, "$.rubricVersion", issues);
        ValidateIds(result.SubjectReferenceIds, "$.subjectReferenceIds", result.IsBlindPairwise ? 2 : 1, issues);
        var expectedSubjectCount = result.IsBlindPairwise ? 2 : 1;
        if (!result.SubjectReferenceIds.IsDefault && result.SubjectReferenceIds.Length != expectedSubjectCount)
        {
            Add(issues, "rubric_result.subject_count", "$.subjectReferenceIds", $"Result requires exactly {expectedSubjectCount} subject reference(s).");
        }
        if (result.IsBlindPairwise)
        {
            if (result.BlindReveal is null)
            {
                Add(issues, "rubric_result.blind_reveal", "$.blindReveal", "A finalized blind result requires its A/B reveal.");
            }
            else
            {
                RequireId(result.BlindReveal.LabelAReferenceId, "$.blindReveal.labelAReferenceId", issues);
                RequireId(result.BlindReveal.LabelBReferenceId, "$.blindReveal.labelBReferenceId", issues);
                if (result.BlindReveal.LabelAReferenceId == result.BlindReveal.LabelBReferenceId)
                {
                    Add(issues, "rubric_result.same_subject", "$.blindReveal", "Blind candidates must differ.");
                }
                var subjects = result.SubjectReferenceIds.ToHashSet(StringComparer.Ordinal);
                if (!subjects.SetEquals([result.BlindReveal.LabelAReferenceId, result.BlindReveal.LabelBReferenceId]))
                {
                    Add(issues, "rubric_result.blind_subjects", "$.blindReveal", "Reveal identities must match the result subjects.");
                }
            }
        }
        else if (result.BlindReveal is not null)
        {
            Add(issues, "rubric_result.unexpected_reveal", "$.blindReveal", "A non-pairwise result cannot contain a blind reveal.");
        }

        ValidateEvaluatorResults(result.DeterministicResults, ArenaRubricJudgmentSource.Deterministic, result.IsBlindPairwise, "$.deterministicResults", issues);
        ValidateEvaluatorResults(result.HumanResults, ArenaRubricJudgmentSource.Human, result.IsBlindPairwise, "$.humanResults", issues);
        ValidateEvaluatorResults(result.ModelJudgeResults, ArenaRubricJudgmentSource.ModelJudge, result.IsBlindPairwise, "$.modelJudgeResults", issues);

        var allIds = SafeClass(result.DeterministicResults)
            .Concat(SafeClass(result.HumanResults))
            .Concat(SafeClass(result.ModelJudgeResults))
            .Select(item => item.Id)
            .ToArray();
        if (allIds.Length == 0)
        {
            Add(issues, "rubric_result.empty", "$.results", "A finalized result requires at least one evaluator observation.");
        }
        if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Length)
        {
            Add(issues, "id.duplicate", "$.results", "Evaluator result IDs must be unique across all sources.");
        }

        ValidateDisagreements(result.Disagreements, allIds.ToHashSet(StringComparer.Ordinal), issues);
        ValidateEvidence(result.Evidence, "$.evidence", issues);

        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            issues.AddRange(ArenaContractPrivacyRules.InspectJson(json));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            Add(issues, "contract.serialization", "$", exception.Message);
        }

        return new([.. issues.Distinct()
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)]);
    }

    public static string Serialize(ArenaRubricEvaluationResultContract result, bool indented = false)
    {
        var validation = Validate(result);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
        }

        var element = JsonSerializer.SerializeToElement(result, JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteCanonical(element, writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryDeserialize(
        string json,
        out ArenaRubricEvaluationResultContract? result,
        out ImmutableArray<ArenaContractValidationIssue> issues)
    {
        result = null;
        try
        {
            result = JsonSerializer.Deserialize<ArenaRubricEvaluationResultContract>(json, JsonOptions);
            var validation = Validate(result);
            issues = validation.Issues;
            if (!validation.IsValid)
            {
                result = null;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            issues = [new("json.invalid", "$", exception.Message)];
            return false;
        }
    }

    private static void ValidateEvaluatorResults(
        ImmutableArray<ArenaRubricEvaluatorResult> results,
        ArenaRubricJudgmentSource expectedSource,
        bool pairwise,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(results, path, MaximumResultsPerSource, issues)) return;
        if (!results.SequenceEqual(results.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", path, "Evaluator results must be sorted by ID.");
        }
        if (results.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != results.Length)
        {
            Add(issues, "id.duplicate", path, "Evaluator result IDs must be unique.");
        }

        for (var index = 0; index < results.Length; index++)
        {
            var value = results[index];
            var itemPath = $"{path}[{index}]";
            RequireId(value.Id, $"{itemPath}.id", issues);
            RequireId(value.EvaluatorId, $"{itemPath}.evaluatorId", issues);
            if (value.Source != expectedSource)
            {
                Add(issues, "rubric_result.source_partition", $"{itemPath}.source", "Result is stored in the wrong source partition.");
            }
            if (expectedSource == ArenaRubricJudgmentSource.Human)
            {
                RequireId(value.ReviewerId, $"{itemPath}.reviewerId", issues);
            }
            else if (value.ReviewerId is not null)
            {
                Add(issues, "rubric_result.unexpected_reviewer", $"{itemPath}.reviewerId", "Only human observations carry reviewer identity.");
            }
            if (value.ProfileId is not null) RequireId(value.ProfileId, $"{itemPath}.profileId", issues);
            if (expectedSource is ArenaRubricJudgmentSource.Deterministic or ArenaRubricJudgmentSource.ModelJudge
                && value.ProfileId is null)
            {
                Add(issues, "rubric_result.profile", $"{itemPath}.profileId", "Deterministic and model-judge observations require a versioned profile ID.");
            }

            if (pairwise && value.PairwisePreference is null)
            {
                Add(issues, "rubric_result.preference", $"{itemPath}.pairwisePreference", "Pairwise observations require a preference or unavailable state.");
            }
            if (!pairwise && value.PairwisePreference is not null)
            {
                Add(issues, "rubric_result.unexpected_preference", $"{itemPath}.pairwisePreference", "Single-subject observations cannot have a pairwise preference.");
            }

            ValidateCriteria(value.Criteria, expectedSource, pairwise, $"{itemPath}.criteria", issues);
            ValidateNormalizedScore(value.WeightedScoreA, $"{itemPath}.weightedScoreA", issues);
            if (pairwise) ValidateNormalizedScore(value.WeightedScoreB, $"{itemPath}.weightedScoreB", issues);
            else if (value.WeightedScoreB is not null)
            {
                Add(issues, "rubric_result.unexpected_score", $"{itemPath}.weightedScoreB", "Single-subject observations cannot score subject B.");
            }
            ValidateSourceEvidence(value.Provenance, expectedSource, $"{itemPath}.provenance", issues);
        }
    }

    private static void ValidateCriteria(
        ImmutableArray<ArenaRubricCriterionResult> criteria,
        ArenaRubricJudgmentSource source,
        bool pairwise,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(criteria, path, MaximumCriteria, issues) || criteria.IsEmpty)
        {
            if (criteria.IsEmpty) Add(issues, "collection.required", path, "At least one criterion result is required.");
            return;
        }
        if (!criteria.SequenceEqual(criteria.OrderBy(item => item.CriterionId, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", path, "Criterion results must be sorted by criterion ID.");
        }
        if (criteria.Select(item => item.CriterionId).Distinct(StringComparer.Ordinal).Count() != criteria.Length)
        {
            Add(issues, "id.duplicate", path, "Criterion results must be unique.");
        }
        for (var index = 0; index < criteria.Length; index++)
        {
            var value = criteria[index];
            var itemPath = $"{path}[{index}]";
            RequireId(value.CriterionId, $"{itemPath}.criterionId", issues);
            ValidateFiniteScore(value.ScoreA, $"{itemPath}.scoreA", issues);
            if (pairwise) ValidateFiniteScore(value.ScoreB, $"{itemPath}.scoreB", issues);
            else if (value.ScoreB is not null)
            {
                Add(issues, "rubric_result.unexpected_score", $"{itemPath}.scoreB", "Single-subject observations cannot score subject B.");
            }
            ValidateSourceEvidence(value.Evidence, source, $"{itemPath}.evidence", issues);
            var missingRequiredScore = value.ScoreA is null || (pairwise && value.ScoreB is null);
            if (value.Evidence.State == ArenaEvidenceState.Unavailable && (value.ScoreA is not null || value.ScoreB is not null))
            {
                Add(issues, "rubric_result.unavailable_score", itemPath, "Unavailable evidence cannot carry a score.");
            }
            else if (missingRequiredScore && value.Evidence.State != ArenaEvidenceState.Unavailable)
            {
                Add(issues, "rubric_result.missing_score", itemPath, "A missing score must be represented as unavailable evidence.");
            }
        }
    }

    private static void ValidateSourceEvidence(
        ArenaEvidenceAssertion value,
        ArenaRubricJudgmentSource source,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        ValidateEvidenceItem(value, path, issues);
        var allowed = source == ArenaRubricJudgmentSource.ModelJudge
            ? value.State is ArenaEvidenceState.Inferred or ArenaEvidenceState.Unavailable
            : value.State is ArenaEvidenceState.Observed or ArenaEvidenceState.Unavailable;
        if (!allowed)
        {
            Add(
                issues,
                source == ArenaRubricJudgmentSource.ModelJudge ? "rubric_result.model_not_measured" : "rubric_result.source_evidence",
                path,
                source == ArenaRubricJudgmentSource.ModelJudge
                    ? "Model-judge opinion must be inferred or unavailable, never labelled as an observed measurement."
                    : "Deterministic and human result capture must be observed or unavailable.");
        }
    }

    private static void ValidateDisagreements(
        ImmutableArray<ArenaRubricDisagreement> values,
        IReadOnlySet<string> resultIds,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        const string path = "$.disagreements";
        if (!ValidateCollection(values, path, 512, issues)) return;
        if (!values.SequenceEqual(values.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", path, "Disagreements must be sorted by ID.");
        }
        if (values.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            Add(issues, "id.duplicate", path, "Disagreement IDs must be unique.");
        }
        foreach (var value in values)
        {
            RequireId(value.Id, "$.disagreements.id", issues);
            RequireId(value.CriterionId, "$.disagreements.criterionId", issues);
            if (value.SubjectLabel is not "a" and not "b")
            {
                Add(issues, "rubric_result.subject_label", "$.disagreements.subjectLabel", "Subject label must be 'a' or 'b'.");
            }
            if (value.MinimumScore >= value.MaximumScore)
            {
                Add(issues, "rubric_result.disagreement_range", "$.disagreements", "A disagreement requires distinct minimum and maximum scores.");
            }
            ValidateIds(value.ResultIds, "$.disagreements.resultIds", minimumCount: 2, issues);
            foreach (var id in Safe(value.ResultIds))
            {
                if (!resultIds.Contains(id)) Add(issues, "reference.dangling", "$.disagreements.resultIds", "Disagreement references an absent evaluator result.");
            }
        }
    }

    private static void ValidateEvidence(
        ImmutableArray<ArenaEvidenceAssertion> values,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, path, 2_000, issues)) return;
        if (!values.SequenceEqual(values.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", path, "Evidence must be sorted by ID.");
        }
        if (values.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            Add(issues, "id.duplicate", path, "Evidence IDs must be unique.");
        }
        for (var index = 0; index < values.Length; index++) ValidateEvidenceItem(values[index], $"{path}[{index}]", issues);
    }

    private static void ValidateEvidenceItem(
        ArenaEvidenceAssertion value,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is null)
        {
            Add(issues, "collection.null_item", path, "Evidence cannot be null.");
            return;
        }
        RequireId(value.Id, $"{path}.id", issues);
        RequireText(value.Summary, $"{path}.summary", issues);
        if (value.ReferenceId is not null) RequireId(value.ReferenceId, $"{path}.referenceId", issues);
        if (value.State == ArenaEvidenceState.Observed && string.IsNullOrWhiteSpace(value.ReferenceId))
            Add(issues, "evidence.observed_reference", $"{path}.referenceId", "Observed evidence requires a reference ID.");
        if (value.State == ArenaEvidenceState.Inferred && string.IsNullOrWhiteSpace(value.Basis))
            Add(issues, "evidence.inferred_basis", $"{path}.basis", "Inferred evidence requires an explicit basis.");
        if (value.State == ArenaEvidenceState.Unavailable && string.IsNullOrWhiteSpace(value.Limitation))
            Add(issues, "evidence.unavailable_limitation", $"{path}.limitation", "Unavailable evidence requires a limitation.");
        if (value.Basis is not null) RequireText(value.Basis, $"{path}.basis", issues);
        if (value.Limitation is not null) RequireText(value.Limitation, $"{path}.limitation", issues);
    }

    private static bool ValidateCollection<T>(
        ImmutableArray<T> values,
        string path,
        int maximum,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (values.IsDefault)
        {
            Add(issues, "collection.default", path, "Collection must be initialized.");
            return false;
        }
        if (values.Length > maximum)
        {
            Add(issues, "collection.limit", path, $"Collection cannot exceed {maximum} items.");
            return false;
        }
        if (default(T) is null && values.Any(static item => item is null))
        {
            Add(issues, "collection.null_item", path, "Collection cannot contain null items.");
            return false;
        }
        return true;
    }

    private static void ValidateIds(
        ImmutableArray<string> values,
        string path,
        int minimumCount,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, path, 512, issues)) return;
        if (values.Length < minimumCount) Add(issues, "collection.required", path, $"At least {minimumCount} value(s) are required.");
        if (!values.SequenceEqual(values.OrderBy(item => item, StringComparer.Ordinal))) Add(issues, "order.nondeterministic", path, "IDs must be sorted ordinally.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) Add(issues, "id.duplicate", path, "IDs must be unique.");
        for (var index = 0; index < values.Length; index++) RequireId(values[index], $"{path}[{index}]", issues);
    }

    private static void ValidateNormalizedScore(decimal? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is < 0 or > 1) Add(issues, "rubric_result.weighted_score", path, "Weighted normalized score must be from zero through one.");
    }

    private static void ValidateFiniteScore(decimal? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is < -1_000_000m or > 1_000_000m) Add(issues, "rubric_result.score_bounds", path, "Score exceeds the bounded numeric range.");
    }

    private static void RequireId(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !Regex.IsMatch(value, "^[a-z0-9][a-z0-9._:-]{0,159}$", RegexOptions.CultureInvariant))
            Add(issues, "id.invalid", path, "ID must be a bounded lowercase identifier.");
    }

    private static void RequireReference(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            Add(issues, "reference.invalid", path, "Reference must contain 1-512 printable characters.");
    }

    private static void RequireText(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096 || value.Contains('\0'))
            Add(issues, "text.invalid", path, "Text must contain 1-4096 bounded characters.");
    }

    private static void RequireUtc(DateTimeOffset value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value == default || value.Offset != TimeSpan.Zero) Add(issues, "time.utc", path, "Timestamp must be non-default UTC.");
    }

    private static IEnumerable<T> Safe<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;

    private static IEnumerable<T> SafeClass<T>(ImmutableArray<T> values) where T : class =>
        values.IsDefault ? [] : values.Where(static item => item is not null);

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static void Add(ImmutableArray<ArenaContractValidationIssue>.Builder issues, string code, string path, string message) =>
        issues.Add(new(code, path, message));
}
