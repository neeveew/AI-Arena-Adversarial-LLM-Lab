using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>Strict canonical codec for evaluation proof artifacts.</summary>
public static class ArenaEvaluationEvidenceCodec
{
    private const int MaximumReferenceLength = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 48
    };

    public static ArenaContractValidationResult Validate(ArenaBlindPairwiseCommitmentContract? value)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (value is null) return Null();
        Schema(value.Schema, ArenaEvaluationEvidenceSchemas.BlindPairwiseCommitment, issues);
        Common(value.Id, value.CreatedAtUtc, issues);
        Id(value.EvaluationId, "$.evaluationId", issues);
        Id(value.RubricId, "$.rubricId", issues);
        Reference(value.RubricVersion, "$.rubricVersion", issues);
        Id(value.EvaluatorId, "$.evaluatorId", issues);
        Token(value.LabelAToken, "$.labelAToken", issues);
        Token(value.LabelBToken, "$.labelBToken", issues);
        if (string.Equals(value.LabelAToken, value.LabelBToken, StringComparison.Ordinal))
            Add(issues, "blind.token_duplicate", "$.labelBToken", "Blind label tokens must differ.");
        Hash(value.SubjectSetSha256, "$.subjectSetSha256", issues);
        Hash(value.MappingCommitmentSha256, "$.mappingCommitmentSha256", issues);
        Privacy(value, issues);
        return Result(issues);
    }

    public static ArenaContractValidationResult Validate(ArenaBlindPairwiseReceiptContract? value)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (value is null) return Null();
        Schema(value.Schema, ArenaEvaluationEvidenceSchemas.BlindPairwiseReceipt, issues);
        Common(value.Id, value.CreatedAtUtc, issues);
        Id(value.EvaluationId, "$.evaluationId", issues);
        Id(value.CommitmentId, "$.commitmentId", issues);
        Id(value.EvaluatorResultId, "$.evaluatorResultId", issues);
        if (value.Source is not (ArenaRubricJudgmentSource.Human or ArenaRubricJudgmentSource.ModelJudge))
            Add(issues, "blind.source", "$.source", "Blind receipts can bind only human or model-judge submissions.");
        Reference(value.LabelAReferenceId, "$.labelAReferenceId", issues);
        Reference(value.LabelBReferenceId, "$.labelBReferenceId", issues);
        if (string.Equals(value.LabelAReferenceId, value.LabelBReferenceId, StringComparison.Ordinal))
            Add(issues, "blind.subject_duplicate", "$.labelBReferenceId", "Blind subjects must differ.");
        Hash(value.MappingNonce, "$.mappingNonce", issues);
        Hash(value.EvaluatorResultSha256, "$.evaluatorResultSha256", issues);
        Privacy(value, issues);
        return Result(issues);
    }

    public static ArenaContractValidationResult Validate(ArenaModelJudgeReceiptContract? value)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (value is null) return Null();
        Schema(value.Schema, ArenaEvaluationEvidenceSchemas.ModelJudgeReceipt, issues);
        Common(value.Id, value.CreatedAtUtc, issues);
        Id(value.EvaluationId, "$.evaluationId", issues);
        Id(value.RubricId, "$.rubricId", issues);
        Reference(value.RubricVersion, "$.rubricVersion", issues);
        Id(value.EvaluatorId, "$.evaluatorId", issues);
        Id(value.EvaluatorResultId, "$.evaluatorResultId", issues);
        if (value.SubjectReferenceIds.IsDefault || value.SubjectReferenceIds.Length is < 1 or > 2)
            Add(issues, "collection.bounds", "$.subjectReferenceIds", "One or two subject references are required.");
        else
        {
            if (!value.SubjectReferenceIds.SequenceEqual(value.SubjectReferenceIds.Order(StringComparer.Ordinal)))
                Add(issues, "order.nondeterministic", "$.subjectReferenceIds", "Subject references must be sorted.");
            if (value.SubjectReferenceIds.Distinct(StringComparer.Ordinal).Count() != value.SubjectReferenceIds.Length)
                Add(issues, "reference.duplicate", "$.subjectReferenceIds", "Subject references must be unique.");
            for (var index = 0; index < value.SubjectReferenceIds.Length; index++)
                Reference(value.SubjectReferenceIds[index], $"$.subjectReferenceIds[{index}]", issues);
        }
        Id(value.ProfileId, "$.profileId", issues);
        Hash(value.ProviderProfileSha256, "$.providerProfileSha256", issues);
        Reference(value.RequestId, "$.requestId", issues);
        Reference(value.CorrelationId, "$.correlationId", issues);
        Utc(value.RequestObservedAtUtc, "$.requestObservedAtUtc", issues);
        if (value.RequestAttempt is < 1 or > 100)
            Add(issues, "provider.attempt", "$.requestAttempt", "Provider attempt must be between 1 and 100.");
        Hash(value.RequestPayloadSha256, "$.requestPayloadSha256", issues);
        Hash(value.ProviderModelSha256, "$.providerModelSha256", issues);
        if (!string.Equals(value.ProviderOutcome, "succeeded", StringComparison.Ordinal))
            Add(issues, "provider.outcome", "$.providerOutcome", "An available model judgment requires a succeeded physical provider attempt.");
        Hash(value.CompletionSha256, "$.completionSha256", issues);
        if (value.ProviderResponseIdSha256 is not null) Hash(value.ProviderResponseIdSha256, "$.providerResponseIdSha256", issues);
        Hash(value.EvaluatorResultSha256, "$.evaluatorResultSha256", issues);
        Privacy(value, issues);
        return Result(issues);
    }

    public static string Serialize(ArenaBlindPairwiseCommitmentContract value, bool indented = false) =>
        SerializeCore(value, Validate(value), indented);

    public static string Serialize(ArenaBlindPairwiseReceiptContract value, bool indented = false) =>
        SerializeCore(value, Validate(value), indented);

    public static string Serialize(ArenaModelJudgeReceiptContract value, bool indented = false) =>
        SerializeCore(value, Validate(value), indented);

    public static bool TryDeserialize(string json, out ArenaBlindPairwiseCommitmentContract? value, out ImmutableArray<ArenaContractValidationIssue> issues) =>
        TryDeserializeCore(json, Validate, out value, out issues);

    public static bool TryDeserialize(string json, out ArenaBlindPairwiseReceiptContract? value, out ImmutableArray<ArenaContractValidationIssue> issues) =>
        TryDeserializeCore(json, Validate, out value, out issues);

    public static bool TryDeserialize(string json, out ArenaModelJudgeReceiptContract? value, out ImmutableArray<ArenaContractValidationIssue> issues) =>
        TryDeserializeCore(json, Validate, out value, out issues);

    internal static string Fingerprint<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(element, false))));
    }

    internal static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));

    private static string SerializeCore<T>(T value, ArenaContractValidationResult validation, bool indented)
    {
        if (!validation.IsValid) Throw(validation);
        return Canonical(JsonSerializer.SerializeToElement(value, JsonOptions), indented);
    }

    private static bool TryDeserializeCore<T>(
        string json,
        Func<T?, ArenaContractValidationResult> validate,
        out T? value,
        out ImmutableArray<ArenaContractValidationIssue> issues)
        where T : class
    {
        value = null;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            var result = validate(value);
            issues = result.Issues;
            if (!result.IsValid) value = null;
            return result.IsValid;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            issues = [new("json.invalid", "$", exception.Message)];
            return false;
        }
    }

    private static string Canonical(JsonElement element, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented })) Write(element, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) Write(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private static void Common(string id, DateTimeOffset createdAtUtc, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        Id(id, "$.id", issues);
        Utc(createdAtUtc, "$.createdAtUtc", issues);
    }

    private static void Schema(string actual, string expected, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) Add(issues, "schema.mismatch", "$.schema", $"Expected schema '{expected}'.");
    }

    private static void Id(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ':' or '.' or '_' or '-')))
            Add(issues, "id.invalid", path, "ID must be a bounded ASCII identifier.");
    }

    private static void Token(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        Reference(value, path, issues);
        if (value is not null && !value.StartsWith("subject:", StringComparison.Ordinal))
            Add(issues, "blind.token", path, "Blind labels must be opaque subject tokens.");
    }

    private static void Reference(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumReferenceLength || value.Any(char.IsControl))
            Add(issues, "reference.invalid", path, "Reference must contain 1-512 printable characters.");
    }

    private static void Hash(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is null || value.Length != 64 || value.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            Add(issues, "hash.invalid", path, "Hash must be 64 lowercase hexadecimal characters.");
    }

    private static void Utc(DateTimeOffset value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value == default || value.Offset != TimeSpan.Zero) Add(issues, "time.utc", path, "Timestamp must be non-default UTC.");
    }

    private static void Privacy<T>(T value, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        try { issues.AddRange(ArenaContractPrivacyRules.InspectJson(JsonSerializer.Serialize(value, JsonOptions))); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException) { Add(issues, "contract.serialization", "$", exception.Message); }
    }

    private static ArenaContractValidationResult Result(ImmutableArray<ArenaContractValidationIssue>.Builder issues) =>
        new([.. issues.Distinct().OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal)]);

    private static ArenaContractValidationResult Null() => new([new("contract.null", "$", "Artifact is required.")]);
    private static void Add(ImmutableArray<ArenaContractValidationIssue>.Builder issues, string code, string path, string message) => issues.Add(new(code, path, message));
    private static void Throw(ArenaContractValidationResult validation) => throw new InvalidDataException(string.Join(Environment.NewLine, validation.Issues.Select(item => $"{item.Code} at {item.Path}: {item.Message}")));
}
