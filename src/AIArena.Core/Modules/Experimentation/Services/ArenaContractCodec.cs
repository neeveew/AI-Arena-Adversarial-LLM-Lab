using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaContractValidationIssue(string Code, string Path, string Message);

public sealed record ArenaContractValidationResult(ImmutableArray<ArenaContractValidationIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}

/// <summary>
/// Shared privacy boundary for persisted experiment artifacts. Contracts may
/// contain identifiers, hashes, bounded summaries, and measurements, but never
/// credentials, absolute paths, source files, raw prompts/responses, transcripts,
/// or command output.
/// </summary>
public static partial class ArenaContractPrivacyRules
{
    public const string RelativePathRule = "Paths must be workspace-relative, use '/', and contain no parent traversal.";
    public const string SecretRule = "Credentials and credential-shaped values are prohibited.";
    public const string SourceContentRule = "Raw source, prompt, response, transcript, and command output content is prohibited.";

    private static readonly IReadOnlySet<string> ForbiddenContentProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "absolutePath",
        "commandOutput",
        "fileContent",
        "promptContent",
        "rawOutput",
        "rawPrompt",
        "rawResponse",
        "rawTranscript",
        "responseContent",
        "sourceCode",
        "sourceContent",
        "sourceText",
        "transcriptContent",
        "transcriptText"
    };

    public static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Contains("\\", StringComparison.Ordinal)
            || value.Contains(":", StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return value.Split('/', StringSplitOptions.None).All(segment =>
            !string.IsNullOrWhiteSpace(segment) && segment is not "." and not "..");
    }

    public static ImmutableArray<ArenaContractValidationIssue> InspectJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            return Inspect(document.RootElement);
        }
        catch (JsonException ex)
        {
            return [new ArenaContractValidationIssue("json.invalid", "$", ex.Message)];
        }
    }

    internal static ImmutableArray<ArenaContractValidationIssue> Inspect(JsonElement root)
    {
        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        InspectElement(root, "$", issues);
        return Sort(issues);
    }

    private static void InspectElement(
        JsonElement element,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = $"{path}.{property.Name}";
                if (!propertyNames.Add(property.Name))
                {
                    issues.Add(new ArenaContractValidationIssue(
                        "json.duplicate_member",
                        $"{path}.*",
                        "Duplicate JSON members are ambiguous and prohibited."));
                }
                if (ForbiddenContentProperties.Contains(property.Name))
                {
                    issues.Add(new ArenaContractValidationIssue("privacy.source_content", propertyPath, SourceContentRule));
                }

                InspectElement(property.Value, propertyPath, issues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                InspectElement(item, $"{path}[{index++}]", issues);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString() ?? "";
            if (SecretValueRegex().IsMatch(value))
            {
                issues.Add(new ArenaContractValidationIssue("privacy.secret", path, SecretRule));
            }

            if (AbsolutePathRegex().IsMatch(value) || value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ArenaContractValidationIssue("privacy.absolute_path", path, "Absolute filesystem paths are prohibited."));
            }
        }
    }

    private static ImmutableArray<ArenaContractValidationIssue> Sort(
        ImmutableArray<ArenaContractValidationIssue>.Builder issues) =>
        [.. issues.Distinct()
            .OrderBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)];

    [GeneratedRegex(
        @"(?ix)(?:\bsk-(?:proj-)?[a-z0-9_-]{8,}|\bgh[pousr]_[a-z0-9]{8,}|\bAKIA[0-9A-Z]{16}\b|\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}|\bbearer\s+[a-z0-9._~+/=-]{8,}|\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|authorization|password|passwd|secret|client[_-]?secret|private[_-]?key)\s*[:=]\s*\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretValueRegex();

    [GeneratedRegex(
        @"(?ix)(?:^|[\s\""'=:(])(?:[a-z]:[\\/]|\\\\(?:\?\\)?[^\\\s]+[\\/]|/(?!/)[a-z0-9._-]+(?:[/\\][^\s\""'<>|]*)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePathRegex();
}

/// <summary>
/// Strict v1 validator and canonical JSON codec. Properties are ordinally sorted,
/// enums are snake-case strings, unknown or duplicate members are rejected, and array order is
/// preserved. Domain validators require deterministic ordering where order has no
/// product meaning.
/// </summary>
public static partial class ArenaContractCodec
{
    private const int MaximumCollectionSize = 10_000;

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

    private static readonly IReadOnlyDictionary<Type, string> SchemaByType = new Dictionary<Type, string>
    {
        [typeof(ArenaExperimentContract)] = ArenaContractSchemas.Experiment,
        [typeof(ArenaExperimentRunContract)] = ArenaContractSchemas.ExperimentRun,
        [typeof(ArenaBranchContract)] = ArenaContractSchemas.Branch,
        [typeof(ArenaRubricContract)] = ArenaContractSchemas.Rubric,
        [typeof(ArenaClaimLedgerContract)] = ArenaContractSchemas.ClaimLedger,
        [typeof(ArenaMemoryTraceContract)] = ArenaContractSchemas.MemoryTrace,
        [typeof(ArenaScenarioPackContract)] = ArenaContractSchemas.ScenarioPack,
        [typeof(ArenaBenchmarkPackContract)] = ArenaContractSchemas.BenchmarkPack,
        [typeof(ArenaRouteProposalContract)] = ArenaContractSchemas.RouteProposal,
        [typeof(ArenaRouteApplicationReceiptContract)] = ArenaContractSchemas.RouteApplicationReceipt,
        [typeof(ArenaFaultProfileContract)] = ArenaContractSchemas.FaultProfile,
        [typeof(ArenaQaEvidenceContract)] = ArenaContractSchemas.QaEvidence
    };

    public static ArenaContractValidationResult Validate<T>(T? contract)
        where T : class, IArenaVersionedContract
    {
        if (contract is null)
        {
            return new ArenaContractValidationResult(
                [new ArenaContractValidationIssue("contract.null", "$", "Contract is required.")]);
        }

        var issues = ImmutableArray.CreateBuilder<ArenaContractValidationIssue>();
        if (!SchemaByType.TryGetValue(contract.GetType(), out var expectedSchema))
        {
            Add(issues, "schema.unsupported", "$.schema", $"Unsupported contract type '{contract.GetType().Name}'.");
        }
        else if (!string.Equals(contract.Schema, expectedSchema, StringComparison.Ordinal))
        {
            Add(issues, "schema.mismatch", "$.schema", $"Expected schema '{expectedSchema}'.");
        }

        RequireId(contract.Id, "$.id", issues);
        RequireUtc(contract.CreatedAtUtc, "$.createdAtUtc", issues);
        switch (contract)
        {
            case ArenaExperimentContract value: ValidateExperiment(value, issues); break;
            case ArenaExperimentRunContract value: ValidateExperimentRun(value, issues); break;
            case ArenaBranchContract value: ValidateBranch(value, issues); break;
            case ArenaRubricContract value: ValidateRubric(value, issues); break;
            case ArenaClaimLedgerContract value: ValidateClaimLedger(value, issues); break;
            case ArenaMemoryTraceContract value: ValidateMemoryTrace(value, issues); break;
            case ArenaScenarioPackContract value: ValidateScenarioPack(value, issues); break;
            case ArenaBenchmarkPackContract value: ValidateBenchmarkPack(value, issues); break;
            case ArenaRouteProposalContract value: ValidateRouteProposal(value, issues); break;
            case ArenaRouteApplicationReceiptContract value: ValidateRouteApplicationReceipt(value, issues); break;
            case ArenaFaultProfileContract value: ValidateFaultProfile(value, issues); break;
            case ArenaQaEvidenceContract value: ValidateQaEvidence(value, issues); break;
        }

        try
        {
            issues.AddRange(ArenaContractPrivacyRules.Inspect(
                JsonSerializer.SerializeToElement(contract, contract.GetType(), JsonOptions)));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Add(issues, "contract.serialization", "$", ex.Message);
        }

        return new ArenaContractValidationResult(Sort(issues));
    }

    public static string Serialize<T>(T contract, bool indented = false)
        where T : class, IArenaVersionedContract
    {
        var validation = Validate(contract);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                validation.Issues.Select(issue => $"{issue.Code} at {issue.Path}: {issue.Message}")));
        }

        var element = JsonSerializer.SerializeToElement(contract, contract.GetType(), JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryDeserialize<T>(
        string json,
        out T? contract,
        out ImmutableArray<ArenaContractValidationIssue> issues)
        where T : class, IArenaVersionedContract
    {
        contract = null;
        try
        {
            var sourceIssues = ArenaContractPrivacyRules.InspectJson(json);
            if (!sourceIssues.IsEmpty)
            {
                issues = sourceIssues;
                return false;
            }

            contract = JsonSerializer.Deserialize<T>(json, JsonOptions);
            var validation = Validate(contract);
            issues = validation.Issues;
            if (!validation.IsValid)
            {
                contract = null;
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            issues = [new ArenaContractValidationIssue("json.invalid", "$", ex.Message)];
            return false;
        }
        catch (NotSupportedException ex)
        {
            issues = [new ArenaContractValidationIssue("json.unsupported", "$", ex.Message)];
            return false;
        }
    }

    private static void ValidateExperiment(
        ArenaExperimentContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireText(value.Title, "$.title", issues);
        RequireId(value.ScenarioPackId, "$.scenarioPackId", issues);
        OptionalId(value.BenchmarkPackId, "$.benchmarkPackId", issues);
        ValidateReferences(value.ProviderProfileIds, "$.providerProfileIds", false, true, issues);
        ValidateIds(value.RubricIds, "$.rubricIds", false, true, issues);
        ValidateIds(value.FaultProfileIds, "$.faultProfileIds", true, true, issues);
        ValidateIds(value.BranchIds, "$.branchIds", true, true, issues);

        if (ValidateCollection(value.Dimensions, "$.dimensions", false, 32, issues))
        {
            ValidateUnique(value.Dimensions.Select(item => item.Id), "$.dimensions", issues);
            var expectedOrder = value.Dimensions
                .OrderBy(item => item.Parameter, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal);
            if (!value.Dimensions.SequenceEqual(expectedOrder))
            {
                Add(issues, "order.nondeterministic", "$.dimensions", "Dimension axes must be sorted by parameter then ID.");
            }

            for (var index = 0; index < value.Dimensions.Length; index++)
            {
                var dimension = value.Dimensions[index];
                var path = $"$.dimensions[{index}]";
                RequireId(dimension.Id, $"{path}.id", issues);
                RequireReference(dimension.Parameter, $"{path}.parameter", issues);
                ValidateReferences(dimension.Values, $"{path}.values", false, true, issues, maximum: 64);
            }
        }

        RequireRange(value.Repetitions, 1, 100, "$.repetitions", issues);
        RequireRange(value.TurnBudget, 1, 1_000, "$.turnBudget", issues);
        RequireRange(value.MaxParallelism, 1, 32, "$.maxParallelism", issues);
        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateExperimentRun(
        ArenaExperimentRunContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.ExperimentId, "$.experimentId", issues);
        RequireHash(value.ExperimentFingerprint, "$.experimentFingerprint", false, issues);
        RequireHash(value.VariantFingerprint, "$.variantFingerprint", false, issues);
        RequireRange(value.Repetition, 0, 1_000_000, "$.repetition", issues);
        RequireId(value.CellKey, "$.cellKey", issues);
        var experimentFingerprint = value.ExperimentFingerprint ?? "";
        var variantFingerprint = value.VariantFingerprint ?? "";
        if (HexRegex().IsMatch(experimentFingerprint)
            && experimentFingerprint.Length == 64
            && HexRegex().IsMatch(variantFingerprint)
            && variantFingerprint.Length == 64)
        {
            var expected = ArenaExperimentRunPolicy.CreateCellKey(
                experimentFingerprint,
                variantFingerprint,
                value.Repetition);
            if (!string.Equals(value.CellKey, expected, StringComparison.Ordinal))
            {
                Add(issues, "experiment_run.cell_key", "$.cellKey", "Cell key must be derived from experiment fingerprint, variant fingerprint, and repetition.");
            }
        }

        RequireRange(value.Attempts, 0, 1_000, "$.attempts", issues);
        RequireUtc(value.UpdatedAtUtc, "$.updatedAtUtc", issues);
        if (value.UpdatedAtUtc < value.CreatedAtUtc)
        {
            Add(issues, "experiment_run.time_order", "$.updatedAtUtc", "Run update cannot precede creation.");
        }

        ValidateIds(value.TrialIds, "$.trialIds", value.State == ArenaExperimentRunState.Queued, true, issues);
        if (value.State != ArenaExperimentRunState.Queued && value.TrialIds.IsDefaultOrEmpty)
        {
            Add(issues, "experiment_run.trial_reference", "$.trialIds", "Started and terminal run states require at least one trial reference.");
        }
        if (value.State == ArenaExperimentRunState.Queued && !value.TrialIds.IsDefaultOrEmpty)
        {
            Add(issues, "experiment_run.premature_reference", "$.trialIds", "A queued cell cannot contain trial references.");
        }
        if (value.State == ArenaExperimentRunState.Queued && value.Attempts != 0)
        {
            Add(issues, "experiment_run.attempts", "$.attempts", "A newly queued cell has no started attempts.");
        }
        if (value.State != ArenaExperimentRunState.Queued && value.Attempts == 0)
        {
            Add(issues, "experiment_run.attempts", "$.attempts", "Started and terminal cells require at least one attempt.");
        }
        if (value.State == ArenaExperimentRunState.Interrupted && string.IsNullOrWhiteSpace(value.InterruptionReason))
        {
            Add(issues, "experiment_run.interruption", "$.interruptionReason", "Interrupted cells require a bounded reason.");
        }
        if (value.InterruptionReason is not null)
        {
            RequireReference(value.InterruptionReason, "$.interruptionReason", issues);
        }

        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateBranch(
        ArenaBranchContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        OptionalId(value.ExperimentId, "$.experimentId", issues);
        RequireSessionReference(value.ParentSessionId, "$.parentSessionId", issues);
        RequireSessionReference(value.ChildSessionId, "$.childSessionId", issues);
        if (value.ParentSessionId.Equals(value.ChildSessionId, StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "branch.same_session", "$.childSessionId", "Fork child session must differ from its parent.");
        }

        RequireNonNegative(value.ParentRevision, "$.parentRevision", issues);
        RequireNonNegative(value.MemoryRevision, "$.memoryRevision", issues);
        RequireId(value.ForkPoint.MessageId, "$.forkPoint.messageId", issues);
        RequireRange(value.ForkPoint.MessageIndex, 0, 1_000_000, "$.forkPoint.messageIndex", issues);
        RequireRange(value.ForkPoint.Turn, 0, 1_000_000, "$.forkPoint.turn", issues);
        RequireHash(value.ForkPoint.MessageFingerprint, "$.forkPoint.messageFingerprint", false, issues);
        RequireHash(value.SetupFingerprint, "$.setupFingerprint", false, issues);
        RequireUtc(value.ForkedAtUtc, "$.forkedAtUtc", issues);
        if (value.ForkedAtUtc < value.CreatedAtUtc)
        {
            Add(issues, "branch.time_order", "$.forkedAtUtc", "Fork time cannot precede contract creation.");
        }

        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateRubric(
        ArenaRubricContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireText(value.Name, "$.name", issues);
        RequireReference(value.Version, "$.version", issues);
        if (ValidateCollection(value.Evaluators, "$.evaluators", false, 32, issues))
        {
            ValidateUnique(value.Evaluators.Select(item => item.Id), "$.evaluators", issues);
            if (!value.Evaluators.SequenceEqual(value.Evaluators.OrderBy(item => item.Id, StringComparer.Ordinal)))
            {
                Add(issues, "order.nondeterministic", "$.evaluators", "Evaluators must be sorted by ID.");
            }
            for (var index = 0; index < value.Evaluators.Length; index++)
            {
                var evaluator = value.Evaluators[index];
                var path = $"$.evaluators[{index}]";
                RequireId(evaluator.Id, $"{path}.id", issues);
                OptionalId(evaluator.ProfileId, $"{path}.profileId", issues);
                if (evaluator.Kind is ArenaRubricEvaluatorKind.Deterministic or ArenaRubricEvaluatorKind.ModelJudge
                    && evaluator.ProfileId is null)
                {
                    Add(issues, "rubric.evaluator_profile", $"{path}.profileId", "Deterministic and model-judge evaluators require a versioned profile ID.");
                }
            }
        }
        if (value.Criteria.IsDefaultOrEmpty || value.Criteria.Length > 128)
        {
            Add(issues, "rubric.criteria", "$.criteria", "Rubric requires 1-128 criteria.");
        }
        else
        {
            ValidateUnique(value.Criteria.Select(item => item.Id), "$.criteria", issues);
            decimal total = 0;
            for (var index = 0; index < value.Criteria.Length; index++)
            {
                var criterion = value.Criteria[index];
                var path = $"$.criteria[{index}]";
                RequireId(criterion.Id, $"{path}.id", issues);
                RequireText(criterion.Label, $"{path}.label", issues);
                RequireText(criterion.Description, $"{path}.description", issues);
                if (criterion.Weight <= 0 || criterion.Weight > 1)
                {
                    Add(issues, "rubric.weight", $"{path}.weight", "Weight must be greater than zero and no greater than one.");
                }

                if (criterion.MinimumScore >= criterion.MaximumScore)
                {
                    Add(issues, "rubric.range", path, "Minimum score must be lower than maximum score.");
                }

                total += criterion.Weight;
            }

            if (Math.Abs(total - 1m) > 0.000001m)
            {
                Add(issues, "rubric.weight_total", "$.criteria", "Criterion weights must total exactly one.");
            }
        }

        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateClaimLedger(
        ArenaClaimLedgerContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.ExperimentId, "$.experimentId", issues);
        RequireId(value.BranchId, "$.branchId", issues);
        ValidateEvidence(value.Evidence, "$.evidence", issues);
        if (!ValidateCollection(value.Claims, "$.claims", true, MaximumCollectionSize, issues)) return;
        ValidateUnique(value.Claims.Select(item => item.Id), "$.claims", issues);
        var claimIds = value.Claims.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evidenceIds = value.Evidence.IsDefault
            ? new HashSet<string>(StringComparer.Ordinal)
            : value.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < value.Claims.Length; index++)
        {
            var claim = value.Claims[index];
            var path = $"$.claims[{index}]";
            RequireId(claim.Id, $"{path}.id", issues);
            RequireId(claim.MessageId, $"{path}.messageId", issues);
            RequireId(claim.ClaimantId, $"{path}.claimantId", issues);
            RequireText(claim.ClaimSummary, $"{path}.claimSummary", issues);
            if (claim.AssertedConfidence is < 0 or > 1)
            {
                Add(issues, "claim.confidence", $"{path}.assertedConfidence", "Asserted confidence must be from zero through one.");
            }

            ValidateIds(claim.SourceEvidenceIds, $"{path}.sourceEvidenceIds", true, true, issues);
            ValidateIds(claim.ContradictionClaimIds, $"{path}.contradictionClaimIds", true, true, issues);
            ValidateIds(claim.ReviewerIds, $"{path}.reviewerIds", true, true, issues);
            ValidateEvidenceItem(claim.Provenance, $"{path}.provenance", issues);
            foreach (var reference in Safe(claim.SourceEvidenceIds))
            {
                RequireExisting(reference, evidenceIds, $"{path}.sourceEvidenceIds", "evidence", issues);
            }

            foreach (var reference in Safe(claim.ContradictionClaimIds))
            {
                RequireExisting(reference, claimIds, $"{path}.contradictionClaimIds", "claim", issues);
            }

            if (claim.ContradictionClaimIds.Contains(claim.Id, StringComparer.Ordinal))
            {
                Add(issues, "claim.self_contradiction", $"{path}.contradictionClaimIds", "A claim cannot contradict itself.");
            }

            if (claim.Status == ArenaClaimStatus.Supported && claim.SourceEvidenceIds.IsDefaultOrEmpty)
            {
                Add(issues, "claim.support", path, "A supported claim requires source evidence.");
            }
            if (claim.Status == ArenaClaimStatus.Contradicted && claim.ContradictionClaimIds.IsDefaultOrEmpty)
            {
                Add(issues, "claim.contradiction", path, "A contradicted claim requires a contradiction reference.");
            }
        }
    }

    private static void ValidateMemoryTrace(
        ArenaMemoryTraceContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.ExperimentId, "$.experimentId", issues);
        RequireId(value.BranchId, "$.branchId", issues);
        if (!ValidateCollection(value.Entries, "$.entries", true, MaximumCollectionSize, issues)) return;
        ValidateUnique(value.Entries.Select(item => item.Id), "$.entries", issues);
        var memoryIds = value.Entries.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < value.Entries.Length; index++)
        {
            var entry = value.Entries[index];
            var path = $"$.entries[{index}]";
            RequireId(entry.Id, $"{path}.id", issues);
            RequireId(entry.AgentId, $"{path}.agentId", issues);
            RequireReference(entry.MemoryKey, $"{path}.memoryKey", issues);
            RequireText(entry.ValueSummary, $"{path}.valueSummary", issues, 1_024);
            RequireUtc(entry.CreatedAtUtc, $"{path}.createdAtUtc", issues);
            RequireUtc(entry.RevisedAtUtc, $"{path}.revisedAtUtc", issues);
            OptionalUtc(entry.ExpiresAtUtc, $"{path}.expiresAtUtc", issues);
            RequireId(entry.SourceMessageId, $"{path}.sourceMessageId", issues);
            RequireId(entry.BranchId, $"{path}.branchId", issues);
            OptionalId(entry.SupersedesMemoryId, $"{path}.supersedesMemoryId", issues);
            OptionalId(entry.CorrectionMemoryId, $"{path}.correctionMemoryId", issues);
            ValidateEvidenceItem(entry.Provenance, $"{path}.provenance", issues);
            if (entry.BranchId != value.BranchId)
            {
                Add(issues, "memory.branch", $"{path}.branchId", "Memory entry branch must match the trace branch.");
            }
            if (entry.RevisedAtUtc < entry.CreatedAtUtc || entry.ExpiresAtUtc <= entry.RevisedAtUtc)
            {
                Add(issues, "memory.time_order", path, "Revision must follow creation and expiry must follow revision.");
            }

            ValidateMemoryLink(entry.Id, entry.SupersedesMemoryId, memoryIds, $"{path}.supersedesMemoryId", issues);
            ValidateMemoryLink(entry.Id, entry.CorrectionMemoryId, memoryIds, $"{path}.correctionMemoryId", issues);
        }
    }

    private static void ValidateScenarioPack(
        ArenaScenarioPackContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireText(value.Name, "$.name", issues);
        RequireReference(value.Version, "$.version", issues);
        RequireHash(value.ContentFingerprint, "$.contentFingerprint", false, issues);
        if (value.Invariants is { IsDefault: false }
            && value.Scenarios is { IsDefault: false }
            && !value.Invariants.Any(static item => item is null)
            && !value.Scenarios.Any(static item => item is null))
        {
            var expectedFingerprint = ArenaExperimentFingerprints.ScenarioPackContent(
                value.Invariants,
                value.Scenarios);
            if (!string.Equals(value.ContentFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    issues,
                    "scenario.content_fingerprint",
                    "$.contentFingerprint",
                    "Scenario-pack content fingerprint does not match its behavior-bearing content.");
            }
        }
        if (value.Migration is not null)
        {
            if (!SchemaNameRegex().IsMatch(value.Migration.SourceSchema))
            {
                Add(issues, "schema.invalid", "$.migration.sourceSchema", "Migration source schema is malformed.");
            }
            RequireReference(value.Migration.SourceVersion, "$.migration.sourceVersion", issues);
            RequireHash(value.Migration.SourceContentFingerprint, "$.migration.sourceContentFingerprint", false, issues);
            RequireReference(value.Migration.MigratorVersion, "$.migration.migratorVersion", issues);
            RequireUtc(value.Migration.MigratedAtUtc, "$.migration.migratedAtUtc", issues);
            if (value.Migration.MigratedAtUtc > value.CreatedAtUtc)
            {
                Add(issues, "scenario.migration_time", "$.migration.migratedAtUtc", "Migration cannot occur after contract creation.");
            }
        }

        if (ValidateCollection(value.Invariants, "$.invariants", false, 1_000, issues))
        {
            ValidateUnique(value.Invariants.Select(item => item.Id), "$.invariants", issues);
            if (!value.Invariants.SequenceEqual(value.Invariants.OrderBy(item => item.Id, StringComparer.Ordinal)))
            {
                Add(issues, "order.nondeterministic", "$.invariants", "Scenario invariants must be sorted by ID.");
            }
            foreach (var invariant in value.Invariants)
            {
                RequireId(invariant.Id, "$.invariants.id", issues);
                RequireId(invariant.RuleId, "$.invariants.ruleId", issues);
                RequireText(invariant.ExpectedOutcome, "$.invariants.expectedOutcome", issues);
            }
        }

        ValidateEvidence(value.Evidence, "$.evidence", issues);
        if (!ValidateCollection(value.Scenarios, "$.scenarios", false, 1_000, issues)) return;
        ValidateUnique(value.Scenarios.Select(item => item.Id), "$.scenarios", issues);
        if (!value.Scenarios.SequenceEqual(value.Scenarios.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", "$.scenarios", "Scenarios must be sorted by ID.");
        }
        var invariantIds = Safe(value.Invariants).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var evidenceIds = Safe(value.Evidence).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < value.Scenarios.Length; index++)
        {
            var scenario = value.Scenarios[index];
            var path = $"$.scenarios[{index}]";
            RequireId(scenario.Id, $"{path}.id", issues);
            RequireReference(scenario.Version, $"{path}.version", issues);
            RequireText(scenario.Title, $"{path}.title", issues);
            RequireId(scenario.MatchSetupReference, $"{path}.matchSetupReference", issues);
            RequireHash(scenario.SetupFingerprint, $"{path}.setupFingerprint", false, issues);
            RequireText(scenario.ScenarioSeed, $"{path}.scenarioSeed", issues);
            RequireRange(scenario.TurnBudget, 1, 1_000, $"{path}.turnBudget", issues);
            ValidateReferences(scenario.Tags, $"{path}.tags", true, true, issues, 128);
            ValidateIds(scenario.InvariantIds, $"{path}.invariantIds", false, true, issues);
            foreach (var reference in Safe(scenario.InvariantIds))
            {
                RequireExisting(reference, invariantIds, $"{path}.invariantIds", "scenario invariant", issues);
            }
            ValidateIds(scenario.RequiredEvidenceIds, $"{path}.requiredEvidenceIds", true, true, issues);
            foreach (var reference in Safe(scenario.RequiredEvidenceIds))
            {
                RequireExisting(reference, evidenceIds, $"{path}.requiredEvidenceIds", "evidence", issues);
            }
        }
    }

    private static void ValidateBenchmarkPack(
        ArenaBenchmarkPackContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireText(value.Name, "$.name", issues);
        RequireReference(value.Version, "$.version", issues);
        RequireHash(value.ContentFingerprint, "$.contentFingerprint", false, issues);
        if (value.Migration is not null)
        {
            ValidatePackMigration(value.Migration, value.CreatedAtUtc, "$.migration", issues);
        }
        RequireId(value.ScenarioPackId, "$.scenarioPackId", issues);
        if (value.Cases is { IsDefault: false }
            && !value.Cases.Any(static item => item is null))
        {
            var expectedFingerprint = ArenaExperimentFingerprints.BenchmarkPackContent(
                value.ScenarioPackId,
                value.Cases);
            if (!string.Equals(value.ContentFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Add(
                    issues,
                    "benchmark.content_fingerprint",
                    "$.contentFingerprint",
                    "Benchmark-pack content fingerprint does not match its behavior-bearing content.");
            }
        }
        ValidateEvidence(value.Evidence, "$.evidence", issues);
        if (!ValidateCollection(value.Cases, "$.cases", false, 1_000, issues)) return;
        ValidateUnique(value.Cases.Select(item => item.Id), "$.cases", issues);
        var evidenceIds = Safe(value.Evidence).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < value.Cases.Length; index++)
        {
            var benchmark = value.Cases[index];
            var path = $"$.cases[{index}]";
            RequireId(benchmark.Id, $"{path}.id", issues);
            RequireId(benchmark.ScenarioId, $"{path}.scenarioId", issues);
            ValidateIds(benchmark.RubricIds, $"{path}.rubricIds", false, true, issues);
            RequireRange(benchmark.Repetitions, 1, 100, $"{path}.repetitions", issues);
            ValidateReferences(benchmark.RequiredProviderCapabilities, $"{path}.requiredProviderCapabilities", true, true, issues, 128);
            ValidateIds(benchmark.RequiredEvidenceIds, $"{path}.requiredEvidenceIds", true, true, issues);
            foreach (var reference in Safe(benchmark.RequiredEvidenceIds))
            {
                RequireExisting(reference, evidenceIds, $"{path}.requiredEvidenceIds", "evidence", issues);
            }
        }
    }

    private static void ValidatePackMigration(
        ArenaPackMigrationProvenance migration,
        DateTimeOffset createdAtUtc,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!SchemaNameRegex().IsMatch(migration.SourceSchema))
        {
            Add(issues, "schema.invalid", $"{path}.sourceSchema", "Migration source schema is malformed.");
        }
        RequireReference(migration.SourceVersion, $"{path}.sourceVersion", issues);
        RequireHash(migration.SourceContentFingerprint, $"{path}.sourceContentFingerprint", false, issues);
        RequireReference(migration.MigratorVersion, $"{path}.migratorVersion", issues);
        RequireUtc(migration.MigratedAtUtc, $"{path}.migratedAtUtc", issues);
        if (migration.MigratedAtUtc > createdAtUtc)
        {
            Add(issues, "pack.migration_time", $"{path}.migratedAtUtc", "Migration cannot occur after contract creation.");
        }
    }

    private static void ValidateRouteProposal(
        ArenaRouteProposalContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.ExperimentId, "$.experimentId", issues);
        RequireHash(value.SetupFingerprint, "$.setupFingerprint", false, issues);
        if (!ValidateCollection(value.Changes, "$.changes", false, 256, issues)) return;
        ValidateUnique(value.Changes.Select(item => item.Id), "$.changes", issues);
        if (!value.Changes.SequenceEqual(value.Changes.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", "$.changes", "Route changes must be sorted by change ID.");
        }
        for (var index = 0; index < value.Changes.Length; index++)
        {
            var change = value.Changes[index];
            var path = $"$.changes[{index}]";
            RequireId(change.Id, $"{path}.id", issues);
            RequireId(change.AgentId, $"{path}.agentId", issues);
            RequireReference(change.CurrentModelId, $"{path}.currentModelId", issues);
            RequireReference(change.ProposedModelId, $"{path}.proposedModelId", issues);
            RequireText(change.Reason, $"{path}.reason", issues);
            if (change.CurrentModelId == change.ProposedModelId)
            {
                Add(issues, "route.no_change", path, "Current and proposed models must differ.");
            }
            if (!change.RequiresExplicitApproval)
            {
                Add(issues, "route.approval_required", $"{path}.requiresExplicitApproval", "Every route proposal requires explicit approval.");
            }

            ValidateIds(change.EvidenceRunIds, $"{path}.evidenceRunIds", change.EvidenceSufficiency == ArenaEvidenceSufficiency.Insufficient, true, issues);
            RequireRange(change.SampleCount, 0, 1_000_000, $"{path}.sampleCount", issues);
            if (change.EvidenceSufficiency != ArenaEvidenceSufficiency.Insufficient && change.SampleCount == 0)
            {
                Add(issues, "route.sample_count", $"{path}.sampleCount", "Partial or sufficient evidence requires a positive sample count.");
            }

            if (!ValidateCollection(change.ScoreComponents, $"{path}.scoreComponents", false, 128, issues)) continue;
            ValidateUnique(change.ScoreComponents.Select(item => item.Id), $"{path}.scoreComponents", issues);
            if (!change.ScoreComponents.SequenceEqual(change.ScoreComponents.OrderBy(item => item.Id, StringComparer.Ordinal)))
            {
                Add(issues, "order.nondeterministic", $"{path}.scoreComponents", "Score components must be sorted by component ID.");
            }
            decimal weight = 0;
            for (var componentIndex = 0; componentIndex < change.ScoreComponents.Length; componentIndex++)
            {
                var component = change.ScoreComponents[componentIndex];
                var componentPath = $"{path}.scoreComponents[{componentIndex}]";
                RequireId(component.Id, $"{componentPath}.id", issues);
                if (component.Weight <= 0 || component.Weight > 1)
                {
                    Add(issues, "route.score_weight", $"{componentPath}.weight", "Score weights must be greater than zero and no greater than one.");
                }
                weight += component.Weight;

                if (component.Evidence is null)
                {
                    Add(issues, "evidence.required", $"{componentPath}.evidence", "Score-component evidence is required.");
                }
                else if (component.Evidence.State == ArenaEvidenceState.Unavailable)
                {
                    ValidateEvidenceItem(component.Evidence, $"{componentPath}.evidence", issues);
                    if (component.CurrentScore is not null || component.ProposedScore is not null)
                    {
                        Add(issues, "route.unavailable_score", componentPath, "Unavailable score evidence cannot carry numeric scores.");
                    }
                }
                else
                {
                    ValidateEvidenceItem(component.Evidence, $"{componentPath}.evidence", issues);
                    if (component.CurrentScore is null || component.ProposedScore is null)
                    {
                        Add(issues, "route.score_missing", componentPath, "Observed or inferred score evidence requires both comparison scores.");
                    }
                    else
                    {
                        RequireDecimalRange(component.CurrentScore.Value, 0m, 1m, $"{componentPath}.currentScore", issues);
                        RequireDecimalRange(component.ProposedScore.Value, 0m, 1m, $"{componentPath}.proposedScore", issues);
                    }
                }

                if (value.Status == ArenaRouteProposalStatus.Proposed
                    && component.Evidence?.State != ArenaEvidenceState.Observed)
                {
                    Add(issues, "route.score_evidence", $"{componentPath}.evidence", "A proposed route requires observed evidence for every score component.");
                }
            }
            if (Math.Abs(weight - 1m) > 0.000001m)
            {
                Add(issues, "route.score_weight_total", $"{path}.scoreComponents", "Score weights must total exactly one.");
            }

            if (ValidateCollection(change.Constraints, $"{path}.constraints", true, 128, issues))
            {
                ValidateUnique(change.Constraints.Select(item => item.Id), $"{path}.constraints", issues);
                if (!change.Constraints.SequenceEqual(change.Constraints
                    .OrderBy(item => item.Kind)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)))
                {
                    Add(issues, "order.nondeterministic", $"{path}.constraints", "Route constraints must be sorted by kind and constraint ID.");
                }
                for (var constraintIndex = 0; constraintIndex < change.Constraints.Length; constraintIndex++)
                {
                    var constraint = change.Constraints[constraintIndex];
                    var constraintPath = $"{path}.constraints[{constraintIndex}]";
                    RequireId(constraint.Id, $"{constraintPath}.id", issues);
                    RequireText(constraint.Description, $"{constraintPath}.description", issues);
                    if (constraint.Evidence is null)
                    {
                        Add(issues, "evidence.required", $"{constraintPath}.evidence", "Constraint evidence is required.");
                    }
                    else if (constraint.Evidence.State == ArenaEvidenceState.Unavailable)
                    {
                        ValidateEvidenceItem(constraint.Evidence, $"{constraintPath}.evidence", issues);
                        if (constraint.Satisfied is not null)
                        {
                            Add(issues, "route.unavailable_constraint", constraintPath, "Unavailable constraint evidence cannot claim a satisfaction result.");
                        }
                    }
                    else
                    {
                        ValidateEvidenceItem(constraint.Evidence, $"{constraintPath}.evidence", issues);
                        if (constraint.Satisfied is null)
                        {
                            Add(issues, "route.constraint_missing", $"{constraintPath}.satisfied", "Observed or inferred constraint evidence requires a satisfaction result.");
                        }
                    }

                    if (value.Status == ArenaRouteProposalStatus.Proposed
                        && (constraint.Satisfied != true || constraint.Evidence?.State != ArenaEvidenceState.Observed))
                    {
                        Add(issues, "route.constraint_evidence", constraintPath, "A proposed route requires an observed, satisfied result for every constraint.");
                    }
                }

                if (value.Status == ArenaRouteProposalStatus.Proposed
                    && !change.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Hardware))
                {
                    Add(issues, "route.hardware_evidence", $"{path}.constraints", "A proposed route requires explicit hardware evidence.");
                }
                if (value.Status == ArenaRouteProposalStatus.Proposed
                    && !change.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Capability))
                {
                    Add(issues, "route.capability_evidence", $"{path}.constraints", "A proposed route requires explicit capability evidence.");
                }
            }

            ValidateEvidenceItem(change.Evidence, $"{path}.evidence", issues);
            if (value.Status == ArenaRouteProposalStatus.Proposed
                && (change.EvidenceSufficiency != ArenaEvidenceSufficiency.Sufficient
                    || change.SampleCount < 2
                    || change.Constraints.Any(constraint => constraint.Satisfied != true)))
            {
                Add(issues, "route.evidence_insufficient", path, "A proposed route requires sufficient multi-sample evidence and satisfied constraints.");
            }
        }
    }

    private static void ValidateRouteApplicationReceipt(
        ArenaRouteApplicationReceiptContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.ProposalId, "$.proposalId", issues);
        RequireId(value.ExperimentId, "$.experimentId", issues);
        RequireHash(value.SetupFingerprint, "$.setupFingerprint", false, issues);
        RequireId(value.ApprovedBy, "$.approvedBy", issues);
        RequireUtc(value.ApprovedAtUtc, "$.approvedAtUtc", issues);
        RequireUtc(value.AppliedAtUtc, "$.appliedAtUtc", issues);
        if (value.AppliedAtUtc < value.ApprovedAtUtc || value.CreatedAtUtc < value.AppliedAtUtc)
        {
            Add(issues, "route.receipt_time", "$", "Application must follow approval and receipt creation must follow application.");
        }

        if (ValidateCollection(value.Changes, "$.changes", false, 256, issues))
        {
            ValidateUnique(value.Changes.Select(item => item.ProposalChangeId), "$.changes", issues);
            if (!value.Changes.SequenceEqual(value.Changes.OrderBy(item => item.ProposalChangeId, StringComparer.Ordinal)))
            {
                Add(issues, "order.nondeterministic", "$.changes", "Applied route changes must be sorted by proposal change ID.");
            }
            foreach (var change in value.Changes)
            {
                RequireId(change.ProposalChangeId, "$.changes.proposalChangeId", issues);
                RequireId(change.AgentId, "$.changes.agentId", issues);
                RequireReference(change.PreviousModelId, "$.changes.previousModelId", issues);
                RequireReference(change.AppliedModelId, "$.changes.appliedModelId", issues);
                if (change.PreviousModelId == change.AppliedModelId)
                {
                    Add(issues, "route.no_change", "$.changes", "Applied and previous models must differ.");
                }
            }
        }

        ValidateEvidenceItem(value.ApprovalEvidence, "$.approvalEvidence", issues);
        if (value.ApprovalEvidence.State != ArenaEvidenceState.Observed)
        {
            Add(issues, "route.approval_evidence", "$.approvalEvidence", "An application receipt requires observed approval evidence.");
        }
        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateFaultProfile(
        ArenaFaultProfileContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireText(value.Name, "$.name", issues);
        if (ValidateCollection(value.Injections, "$.injections", false, 1_000, issues))
        {
            ValidateUnique(value.Injections.Select(item => item.Id), "$.injections", issues);
            for (var index = 0; index < value.Injections.Length; index++)
            {
                var injection = value.Injections[index];
                var path = $"$.injections[{index}]";
                RequireId(injection.Id, $"{path}.id", issues);
                RequireRange(injection.AtSequence, 0, 1_000_000, $"{path}.atSequence", issues);
                RequireRange(injection.DurationMilliseconds, 0, 3_600_000, $"{path}.durationMilliseconds", issues);
                RequireRange(injection.Intensity, 1, 100, $"{path}.intensity", issues);
                RequireRange(injection.MaxOccurrences, 1, 1_000, $"{path}.maxOccurrences", issues);
                RequireText(injection.ExpectedBehavior, $"{path}.expectedBehavior", issues);
            }
        }
        RequireReference(value.Seed, "$.seed", issues);
        ValidateEvidence(value.Evidence, "$.evidence", issues);
    }

    private static void ValidateQaEvidence(
        ArenaQaEvidenceContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireHash(value.SourceRevision, "$.sourceRevision", true, issues);
        RequireHash(value.TreeFingerprint, "$.treeFingerprint", false, issues);
        RequireId(value.SealManifestId, "$.sealManifestId", issues);
        if (!IsKnownQaSealManifest(value.SealManifestId))
        {
            Add(issues, "qa.manifest", "$.sealManifestId", $"Expected QA seal manifest '{ArenaQaSealManifestV1.Id}' or '{ArenaQaSealManifestV2.Id}'.");
        }
        ValidateQaRepositories(value.NestedRepositories, value.Verdict == ArenaQaVerdict.Sealed, issues);
        RequireUtc(value.StartedAtUtc, "$.startedAtUtc", issues);
        RequireUtc(value.CompletedAtUtc, "$.completedAtUtc", issues);
        if (value.CompletedAtUtc < value.StartedAtUtc)
        {
            Add(issues, "qa.time_order", "$.completedAtUtc", "Completion cannot precede the start.");
        }
        RequireRange(value.CleanFullPasses, 0, 5, "$.cleanFullPasses", issues);
        ValidateQaEnvironment(value.Environment, issues);
        ValidateQaToolchain(value.Toolchain, issues);
        ValidateQaGates(value.Gates, issues);
        ValidateQaArtifacts(value, issues);
        ValidateQaPerformance(value.Performance, issues);
        ValidateQaSchemas(value.SchemaChecks, issues);
        ValidateLiveCoverage(value.LiveProviderCoverage, issues);
        ValidateLimitations(value.AcceptedLimitations, value.Verdict, issues);
        ValidateInspection(value.Inspection, value.Artifacts, value.TreeFingerprint, issues);
        ValidateEvidence(value.Evidence, "$.evidence", issues);

        if (value.Verdict == ArenaQaVerdict.Sealed)
        {
            if (!value.IsWorkingTreeClean)
            {
                Add(issues, "qa.clean_tree", "$.isWorkingTreeClean", "A sealed verdict requires a clean matching tree.");
            }
            var requiredCleanPasses = RequiredQaCleanPasses(value.SealManifestId);
            if (value.CleanFullPasses < requiredCleanPasses)
            {
                Add(issues, "qa.clean_passes", "$.cleanFullPasses", $"A sealed verdict requires {requiredCleanPasses} clean full passes.");
            }
            if (!value.Inspection.UserAccepted || value.Inspection.AcceptedAtUtc is null)
            {
                Add(issues, "qa.user_inspection", "$.inspection", "A sealed verdict requires explicit dated user inspection acceptance.");
            }
            if (value.Gates.IsDefaultOrEmpty || value.Gates.Any(gate => gate.Required && gate.Outcome != ArenaQaGateOutcome.Pass))
            {
                Add(issues, "qa.required_gate", "$.gates", "Every required gate must pass before sealing.");
            }
            if (value.SchemaChecks.IsDefaultOrEmpty || value.SchemaChecks.Any(check => check.Outcome != ArenaQaGateOutcome.Pass))
            {
                Add(issues, "qa.schema_check", "$.schemaChecks", "Every schema and migration check must pass before sealing.");
            }
            if (value.Performance.IsDefaultOrEmpty || value.Performance.Any(measurement => !MeetsThreshold(measurement)))
            {
                Add(issues, "qa.performance", "$.performance", "Observed performance/resource evidence must meet every threshold before sealing.");
            }
            if (value.LiveProviderCoverage.Required && value.LiveProviderCoverage.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.live_provider", "$.liveProviderCoverage", "Required live-provider coverage must be observed before sealing.");
            }
            if (value.AcceptedLimitations.Any(limitation => !limitation.UserAccepted))
            {
                Add(issues, "qa.limitation", "$.acceptedLimitations", "Every sealed limitation must be explicitly accepted.");
            }
            if (!value.Environment.IsReleaseBuild
                || !string.Equals(value.Environment.Configuration, "Release", StringComparison.Ordinal))
            {
                Add(issues, "qa.release", "$.environment", "A sealed verdict requires a Release build environment.");
            }

            ValidateQaSealManifest(value, issues);
        }
    }

    private static void ValidateQaRepositories(
        ImmutableArray<ArenaQaRepositoryProvenance> values,
        bool sealing,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.nestedRepositories", !sealing, 32, issues))
        {
            if (sealing && values.IsEmpty)
            {
                Add(issues, "qa.repository_manifest", "$.nestedRepositories", "Required nested repository provenance is absent.");
            }
            return;
        }
        ValidateUnique(values.Select(item => item.Id), "$.nestedRepositories", issues);
        if (!values.SequenceEqual(values.OrderBy(item => item.Id, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", "$.nestedRepositories", "Nested repositories must be sorted by ID.");
        }
        foreach (var repository in values)
        {
            RequireId(repository.Id, "$.nestedRepositories.id", issues);
            RequireHash(repository.SourceRevision, "$.nestedRepositories.sourceRevision", true, issues);
            RequireHash(repository.TreeFingerprint, "$.nestedRepositories.treeFingerprint", false, issues);
        }

        if (!sealing) return;
        var byId = values
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var id in ArenaQaSealManifestV1.RequiredNestedRepositoryIds)
        {
            if (!byId.TryGetValue(id, out var repository))
            {
                Add(issues, "qa.repository_manifest", "$.nestedRepositories", $"Required nested repository '{id}' is absent.");
            }
            else if (!repository.IsWorkingTreeClean)
            {
                Add(issues, "qa.repository_clean", "$.nestedRepositories", $"Required nested repository '{id}' must be clean before sealing.");
            }
        }
        if (values.Any(repository => !repository.IsWorkingTreeClean))
        {
            Add(issues, "qa.repository_clean", "$.nestedRepositories", "Every repository recorded by a sealed run must be clean.");
        }
    }

    private static void ValidateQaEnvironment(ArenaQaEnvironment value, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireReference(value.OperatingSystem, "$.environment.operatingSystem", issues);
        RequireReference(value.Architecture, "$.environment.architecture", issues);
        RequireReference(value.RuntimeVersion, "$.environment.runtimeVersion", issues);
        RequireReference(value.SdkVersion, "$.environment.sdkVersion", issues);
        RequireReference(value.Configuration, "$.environment.configuration", issues);
    }

    private static void ValidateQaToolchain(ImmutableArray<ArenaQaToolchainEntry> values, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.toolchain", false, 128, issues)) return;
        ValidateUnique(values.Select(item => item.Name), "$.toolchain", issues);
        if (!values.SequenceEqual(values.OrderBy(item => item.Name, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", "$.toolchain", "Toolchain entries must be sorted by name.");
        }
        foreach (var item in values)
        {
            RequireReference(item.Name, "$.toolchain.name", issues);
            RequireReference(item.Version, "$.toolchain.version", issues);
        }
    }

    private static void ValidateQaGates(ImmutableArray<ArenaQaGateEvidence> values, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.gates", false, 1_000, issues)) return;
        ValidateUnique(values.Select(item => item.Id), "$.gates", issues);
        foreach (var gate in values)
        {
            RequireId(gate.Id, "$.gates.id", issues);
            RequireNonNegative(gate.DurationMilliseconds, "$.gates.durationMilliseconds", issues);
            if (gate.Tests.Passed < 0 || gate.Tests.Failed < 0 || gate.Tests.Skipped < 0
                || gate.Tests.Total != gate.Tests.Passed + gate.Tests.Failed + gate.Tests.Skipped)
            {
                Add(issues, "qa.test_counts", "$.gates.tests", "Test counts must be non-negative and add up to total.");
            }
            if (gate.Outcome == ArenaQaGateOutcome.Pass && gate.Tests.Failed != 0)
            {
                Add(issues, "qa.gate_outcome", "$.gates", "A passing gate cannot contain failed tests.");
            }
            if (gate.Outcome == ArenaQaGateOutcome.Pass && gate.Evidence.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.gate_evidence", "$.gates.evidence", "A passing gate requires observed evidence.");
            }
            if (gate.Outcome == ArenaQaGateOutcome.Unavailable && gate.Evidence.State != ArenaEvidenceState.Unavailable)
            {
                Add(issues, "qa.gate_evidence", "$.gates.evidence", "An unavailable gate requires unavailable evidence.");
            }
            ValidateEvidenceItem(gate.Evidence, "$.gates.evidence", issues);
        }
    }

    private static void ValidateQaArtifacts(
        ArenaQaEvidenceContract contract,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        var values = contract.Artifacts;
        if (!ValidateCollection(values, "$.artifacts", true, 10_000, issues)) return;
        ValidateUnique(values.Select(item => item.Id), "$.artifacts", issues);
        var byId = values
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var artifact in values)
        {
            RequireId(artifact.Id, "$.artifacts.id", issues);
            RequireReference(artifact.Kind, "$.artifacts.kind", issues);
            if (!ArenaContractPrivacyRules.IsSafeRelativePath(artifact.RelativePath))
            {
                Add(issues, "privacy.relative_path", "$.artifacts.relativePath", ArenaContractPrivacyRules.RelativePathRule);
            }
            RequireHash(artifact.Sha256, "$.artifacts.sha256", false, issues);
            var visual = artifact.Kind is "rendered-ui-screenshot" or "automation-tree";
            if (visual && artifact.Provenance is null)
            {
                Add(issues, "qa.artifact_provenance", "$.artifacts.provenance", "Rendered screenshots and automation trees require capture provenance.");
                continue;
            }
            if (artifact.Provenance is null) continue;

            var provenance = artifact.Provenance;
            RequireHash(provenance.TreeFingerprint, "$.artifacts.provenance.treeFingerprint", false, issues);
            RequireUtc(provenance.CapturedAtUtc, "$.artifacts.provenance.capturedAtUtc", issues);
            RequireReference(provenance.Theme, "$.artifacts.provenance.theme", issues);
            RequireRange(provenance.ViewportWidthDip, 1, 20_000, "$.artifacts.provenance.viewportWidthDip", issues);
            RequireRange(provenance.ViewportHeightDip, 1, 20_000, "$.artifacts.provenance.viewportHeightDip", issues);
            if (provenance.DpiScale is < 0.5m or > 8m)
            {
                Add(issues, "qa.artifact_dpi", "$.artifacts.provenance.dpiScale", "DPI scale must be from 0.5 through 8.");
            }
            RequireReference(provenance.ExpectedState, "$.artifacts.provenance.expectedState", issues);
            OptionalId(provenance.LinkedAutomationArtifactId, "$.artifacts.provenance.linkedAutomationArtifactId", issues);
            OptionalId(provenance.BaselineArtifactId, "$.artifacts.provenance.baselineArtifactId", issues);
            if (!string.Equals(provenance.TreeFingerprint, contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, "qa.artifact_tree", "$.artifacts.provenance.treeFingerprint", "Artifact provenance must match the tested source-tree fingerprint.");
            }
            if (provenance.CapturedAtUtc < contract.StartedAtUtc || provenance.CapturedAtUtc > contract.CompletedAtUtc)
            {
                Add(issues, "qa.artifact_time", "$.artifacts.provenance.capturedAtUtc", "Artifact capture must occur during the recorded QA run.");
            }
            if (artifact.Kind == "rendered-ui-screenshot" && provenance.LinkedAutomationArtifactId is null)
            {
                Add(issues, "qa.artifact_automation", "$.artifacts.provenance.linkedAutomationArtifactId", "Every rendered screenshot requires a linked automation tree.");
            }
        }

        foreach (var artifact in values)
        {
            if (artifact.Provenance?.LinkedAutomationArtifactId is { } automationId)
            {
                if (!byId.TryGetValue(automationId, out var automation) || automation.Kind != "automation-tree")
                {
                    Add(issues, "qa.artifact_automation", "$.artifacts.provenance.linkedAutomationArtifactId", "Linked automation artifact must exist and have kind 'automation-tree'.");
                }
            }
            if (artifact.Provenance?.BaselineArtifactId is { } baselineId
                && (!byId.TryGetValue(baselineId, out var baseline) || baseline.Kind != "render-baseline"))
            {
                Add(issues, "qa.artifact_baseline", "$.artifacts.provenance.baselineArtifactId", $"Referenced baseline artifact '{baselineId}' must exist and have kind 'render-baseline'.");
            }
        }
    }

    private static void ValidateQaPerformance(ImmutableArray<ArenaQaPerformanceMeasurement> values, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.performance", true, 1_000, issues)) return;
        ValidateUnique(values.Select(item => item.Id), "$.performance", issues);
        foreach (var item in values)
        {
            RequireId(item.Id, "$.performance.id", issues);
            RequireReference(item.Metric, "$.performance.metric", issues);
            RequireReference(item.Unit, "$.performance.unit", issues);
            ValidateEvidenceItem(item.Evidence, "$.performance.evidence", issues);
            if (item.Evidence.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.performance_evidence", "$.performance.evidence", "Performance measurements must be observed, never inferred.");
            }
        }
    }

    private static void ValidateQaSchemas(ImmutableArray<ArenaQaSchemaCheck> values, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.schemaChecks", true, 1_000, issues)) return;
        ValidateUnique(values.Select(item => item.Id), "$.schemaChecks", issues);
        ValidateUnique(values.Select(item => item.Schema), "$.schemaChecks", issues);
        foreach (var item in values)
        {
            RequireId(item.Id, "$.schemaChecks.id", issues);
            if (!ArenaContractSchemas.All.Contains(item.Schema))
            {
                Add(issues, "schema.unknown", "$.schemaChecks.schema", "Schema check names an unregistered schema.");
            }
            if (item.MigratedFromSchema is not null && !SchemaNameRegex().IsMatch(item.MigratedFromSchema))
            {
                Add(issues, "schema.invalid", "$.schemaChecks.migratedFromSchema", "Migration source schema is malformed.");
            }
            ValidateEvidenceItem(item.Evidence, "$.schemaChecks.evidence", issues);
            if (item.Outcome == ArenaQaGateOutcome.Pass && item.Evidence.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.schema_evidence", "$.schemaChecks.evidence", "A passing schema check requires observed evidence.");
            }
        }
    }

    private static void ValidateLiveCoverage(ArenaQaLiveProviderCoverage value, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        ValidateReferences(value.ProviderProfileIds, "$.liveProviderCoverage.providerProfileIds", true, true, issues);
        ValidateIds(value.EvidenceRunIds, "$.liveProviderCoverage.evidenceRunIds", true, true, issues);
        if (value.State == ArenaEvidenceState.Observed && (value.ProviderProfileIds.IsDefaultOrEmpty || value.EvidenceRunIds.IsDefaultOrEmpty))
        {
            Add(issues, "qa.live_evidence", "$.liveProviderCoverage", "Observed live coverage requires provider and run references.");
        }
        if (value.State == ArenaEvidenceState.Unavailable && string.IsNullOrWhiteSpace(value.Limitation))
        {
            Add(issues, "evidence.unavailable_limitation", "$.liveProviderCoverage.limitation", "Unavailable live coverage requires a limitation.");
        }
        if (value.Limitation is not null) RequireText(value.Limitation, "$.liveProviderCoverage.limitation", issues);
    }

    private static void ValidateLimitations(
        ImmutableArray<ArenaQaAcceptedLimitation> values,
        ArenaQaVerdict verdict,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, "$.acceptedLimitations", false, 1_000, issues)) return;
        ValidateUnique(values.Select(item => item.Id), "$.acceptedLimitations", issues);
        foreach (var item in values)
        {
            RequireId(item.Id, "$.acceptedLimitations.id", issues);
            RequireText(item.Summary, "$.acceptedLimitations.summary", issues);
            ValidateEvidenceItem(item.Evidence, "$.acceptedLimitations.evidence", issues);
        }

        var requirements = ArenaQaSealManifestV1.RequiredLimitations;
        if (values.Length != requirements.Length
            || !values.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(requirements.Select(requirement => requirement.Id)))
        {
            Add(issues, "qa.limitation_manifest", "$.acceptedLimitations", "The QA manifest requires its exact closed limitation set.");
            return;
        }

        var shouldBeAccepted = verdict == ArenaQaVerdict.Sealed;
        foreach (var requirement in requirements)
        {
            var item = values.Single(value => string.Equals(value.Id, requirement.Id, StringComparison.Ordinal));
            if (!string.Equals(item.Summary, requirement.Summary, StringComparison.Ordinal)
                || item.UserAccepted != shouldBeAccepted
                || !string.Equals(item.Evidence.Id, requirement.EvidenceId, StringComparison.Ordinal)
                || item.Evidence.State != ArenaEvidenceState.Unavailable
                || !string.Equals(item.Evidence.Summary, requirement.EvidenceSummary, StringComparison.Ordinal)
                || !string.Equals(item.Evidence.ReferenceId, requirement.ReferenceId, StringComparison.Ordinal)
                || item.Evidence.Basis is not null
                || !string.Equals(item.Evidence.Limitation, requirement.EvidenceLimitation, StringComparison.Ordinal))
            {
                Add(issues, "qa.limitation_manifest", "$.acceptedLimitations", "A required QA limitation does not match the frozen manifest semantics or verdict acceptance state.");
            }
        }
    }

    private static void ValidateInspection(
        ArenaQaInspectionEvidence value,
        ImmutableArray<ArenaQaArtifact> artifacts,
        string testedTreeFingerprint,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        OptionalUtc(value.AcceptedAtUtc, "$.inspection.acceptedAtUtc", issues);
        if (value.TreeFingerprint is not null)
        {
            RequireHash(value.TreeFingerprint, "$.inspection.treeFingerprint", false, issues);
        }
        if (value.UserAccepted != (value.AcceptedAtUtc is not null))
        {
            Add(issues, "qa.inspection_time", "$.inspection", "Inspection acceptance and its timestamp must be recorded together.");
        }
        ValidateIds(value.ScreenshotArtifactIds, "$.inspection.screenshotArtifactIds", true, true, issues);
        ValidateIds(value.AutomationArtifactIds, "$.inspection.automationArtifactIds", true, true, issues);
        ValidateEvidenceItem(value.Evidence, "$.inspection.evidence", issues);
        if (value.UserAccepted && value.Evidence.State != ArenaEvidenceState.Observed)
        {
            Add(issues, "qa.inspection_evidence", "$.inspection.evidence", "Accepted user inspection requires observed evidence.");
        }
        if (value.UserAccepted
            && !string.Equals(value.TreeFingerprint, testedTreeFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "qa.inspection_tree", "$.inspection.treeFingerprint", "Accepted inspection must identify the exact tested source-tree fingerprint.");
        }
        var ids = Safe(artifacts).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in Safe(value.ScreenshotArtifactIds).Concat(Safe(value.AutomationArtifactIds)))
        {
            RequireExisting(reference, ids, "$.inspection", "artifact", issues);
        }

        if (value.UserAccepted && (value.ScreenshotArtifactIds.IsDefaultOrEmpty || value.AutomationArtifactIds.IsDefaultOrEmpty))
        {
            Add(issues, "qa.inspection_artifacts", "$.inspection", "Accepted inspection requires screenshot and automation-tree artifacts.");
        }

        var byId = Safe(artifacts)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var id in Safe(value.ScreenshotArtifactIds))
        {
            if (byId.TryGetValue(id, out var artifact) && artifact.Kind != "rendered-ui-screenshot")
            {
                Add(issues, "qa.inspection_artifact_kind", "$.inspection.screenshotArtifactIds", "Screenshot inspection references must identify rendered UI screenshots.");
            }
        }
        foreach (var id in Safe(value.AutomationArtifactIds))
        {
            if (byId.TryGetValue(id, out var artifact) && artifact.Kind != "automation-tree")
            {
                Add(issues, "qa.inspection_artifact_kind", "$.inspection.automationArtifactIds", "Automation inspection references must identify automation trees.");
            }
        }
    }

    private static void ValidateQaSealManifest(
        ArenaQaEvidenceContract value,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!IsKnownQaSealManifest(value.SealManifestId))
        {
            return;
        }

        var isV2 = string.Equals(value.SealManifestId, ArenaQaSealManifestV2.Id, StringComparison.Ordinal);

        var gates = Safe(value.Gates)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var requiredCleanPasses = isV2
            ? ArenaQaSealManifestV2.RequiredCleanPasses
            : ArenaQaSealManifestV1.RequiredCleanPasses;
        var passCount = Math.Max(requiredCleanPasses, value.CleanFullPasses);
        var requiredGateIds = isV2
            ? ArenaQaSealManifestV2.RequiredGateIds(passCount)
            : ArenaQaSealManifestV1.RequiredGateIds(passCount);
        foreach (var requiredId in requiredGateIds)
        {
            if (!gates.TryGetValue(requiredId, out var gate))
            {
                Add(issues, "qa.gate_manifest", "$.gates", $"Required {(isV2 ? "v2" : "v1")} seal gate '{requiredId}' is absent.");
                continue;
            }
            if (!gate.Required || gate.Outcome != ArenaQaGateOutcome.Pass || gate.Evidence.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.gate_manifest", "$.gates", $"Required {(isV2 ? "v2" : "v1")} seal gate '{requiredId}' must be required, passing, and observed.");
            }
        }

        var schemas = Safe(value.SchemaChecks)
            .GroupBy(item => item.Schema, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var requiredSchemaIds = isV2
            ? ArenaQaSealManifestV2.RequiredSchemaIds
            : ArenaQaSealManifestV1.RequiredSchemaIds;
        foreach (var requiredSchema in requiredSchemaIds)
        {
            if (!schemas.TryGetValue(requiredSchema, out var check)
                || check.Outcome != ArenaQaGateOutcome.Pass
                || check.Evidence.State != ArenaEvidenceState.Observed)
            {
                Add(issues, "qa.schema_manifest", "$.schemaChecks", $"Required schema '{requiredSchema}' needs an observed passing validation/migration check.");
            }
        }

        var metrics = Safe(value.Performance)
            .Select(item => item.Metric)
            .ToHashSet(StringComparer.Ordinal);
        var requiredMetrics = isV2
            ? ArenaQaSealManifestV2.RequiredPerformanceMetrics
            : ArenaQaSealManifestV1.RequiredPerformanceMetrics;
        foreach (var requiredMetric in requiredMetrics)
        {
            if (!metrics.Contains(requiredMetric))
            {
                Add(issues, "qa.performance_manifest", "$.performance", $"Required performance/resource metric '{requiredMetric}' is absent.");
            }
        }

        var artifactKinds = Safe(value.Artifacts)
            .Select(item => item.Kind)
            .ToHashSet(StringComparer.Ordinal);
        var requiredArtifactKinds = isV2
            ? ArenaQaSealManifestV2.RequiredArtifactKinds
            : ArenaQaSealManifestV1.RequiredArtifactKinds;
        foreach (var requiredKind in requiredArtifactKinds)
        {
            if (!artifactKinds.Contains(requiredKind))
            {
                Add(issues, "qa.artifact_manifest", "$.artifacts", $"Required artifact kind '{requiredKind}' is absent.");
            }
        }

        if (isV2)
        {
            ValidateQaSealManifestV2Migration(value, gates, schemas, issues);
        }
    }

    private static void ValidateQaSealManifestV2Migration(
        ArenaQaEvidenceContract value,
        IReadOnlyDictionary<string, ArenaQaGateEvidence> gates,
        IReadOnlyDictionary<string, ArenaQaSchemaCheck> schemas,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!gates.TryGetValue(ArenaQaSealManifestV2.ExplicitMigrationGateId, out var gate)
            || !gate.Required
            || gate.Outcome != ArenaQaGateOutcome.Pass
            || gate.Evidence.State != ArenaEvidenceState.Observed
            || !string.Equals(gate.Evidence.ReferenceId, ArenaQaSealManifestV2.ExplicitMigrationArtifactId, StringComparison.Ordinal))
        {
            Add(issues, "qa.v2_migration_gate", "$.gates", "The v2 explicit migration gate must be required, passing, observed, and reference its exact sanitized log artifact.");
        }

        var migrationArtifacts = Safe(value.Artifacts)
            .Where(item => string.Equals(item.Id, ArenaQaSealManifestV2.ExplicitMigrationArtifactId, StringComparison.Ordinal))
            .ToArray();
        if (migrationArtifacts.Length != 1
            || !string.Equals(migrationArtifacts[0].Kind, ArenaQaSealManifestV2.ExplicitMigrationArtifactKind, StringComparison.Ordinal)
            || !string.Equals(migrationArtifacts[0].RelativePath, ArenaQaSealManifestV2.ExplicitMigrationArtifactPath, StringComparison.Ordinal))
        {
            Add(issues, "qa.v2_migration_artifact", "$.artifacts", "The v2 migration gate requires its exact sanitized log artifact identity, kind, and relative path.");
        }

        ValidateQaSealManifestV2MigrationSchema(
            schemas,
            ArenaContractSchemas.ScenarioPack,
            ArenaQaSealManifestV2.ScenarioPackV0Schema,
            ArenaQaSealManifestV2.ScenarioMigrationEvidenceId,
            issues);
        ValidateQaSealManifestV2MigrationSchema(
            schemas,
            ArenaContractSchemas.BenchmarkPack,
            ArenaQaSealManifestV2.BenchmarkPackV0Schema,
            ArenaQaSealManifestV2.BenchmarkMigrationEvidenceId,
            issues);
    }

    private static void ValidateQaSealManifestV2MigrationSchema(
        IReadOnlyDictionary<string, ArenaQaSchemaCheck> schemas,
        string currentSchema,
        string sourceSchema,
        string evidenceId,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!schemas.TryGetValue(currentSchema, out var check)
            || check.Outcome != ArenaQaGateOutcome.Pass
            || !string.Equals(check.MigratedFromSchema, sourceSchema, StringComparison.Ordinal)
            || check.Evidence.State != ArenaEvidenceState.Observed
            || !string.Equals(check.Evidence.Id, evidenceId, StringComparison.Ordinal)
            || !string.Equals(check.Evidence.ReferenceId, ArenaQaSealManifestV2.ExplicitMigrationArtifactId, StringComparison.Ordinal))
        {
            Add(issues, "qa.v2_migration_schema", "$.schemaChecks", $"The v2 schema check for '{currentSchema}' must record the exact explicit v0 migration and observed log reference.");
        }
    }

    private static bool IsKnownQaSealManifest(string value) =>
        string.Equals(value, ArenaQaSealManifestV1.Id, StringComparison.Ordinal)
        || string.Equals(value, ArenaQaSealManifestV2.Id, StringComparison.Ordinal);

    private static int RequiredQaCleanPasses(string manifestId) =>
        string.Equals(manifestId, ArenaQaSealManifestV2.Id, StringComparison.Ordinal)
            ? ArenaQaSealManifestV2.RequiredCleanPasses
            : ArenaQaSealManifestV1.RequiredCleanPasses;

    private static bool MeetsThreshold(ArenaQaPerformanceMeasurement value) =>
        value.ThresholdKind == ArenaQaThresholdKind.Maximum
            ? value.Value <= value.Threshold
            : value.Value >= value.Threshold;

    private static void ValidateEvidence(
        ImmutableArray<ArenaEvidenceAssertion> values,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, path, true, MaximumCollectionSize, issues)) return;
        ValidateUnique(values.Select(item => item.Id), path, issues);
        for (var index = 0; index < values.Length; index++)
        {
            ValidateEvidenceItem(values[index], $"{path}[{index}]", issues);
        }
    }

    private static void ValidateEvidenceItem(
        ArenaEvidenceAssertion value,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        RequireId(value.Id, $"{path}.id", issues);
        RequireText(value.Summary, $"{path}.summary", issues);
        OptionalId(value.ReferenceId, $"{path}.referenceId", issues);
        if (value.State == ArenaEvidenceState.Observed && string.IsNullOrWhiteSpace(value.ReferenceId))
        {
            Add(issues, "evidence.observed_reference", $"{path}.referenceId", "Observed evidence requires a reference ID.");
        }
        if (value.State == ArenaEvidenceState.Inferred && string.IsNullOrWhiteSpace(value.Basis))
        {
            Add(issues, "evidence.inferred_basis", $"{path}.basis", "Inferred evidence requires an explicit basis.");
        }
        if (value.State == ArenaEvidenceState.Unavailable && string.IsNullOrWhiteSpace(value.Limitation))
        {
            Add(issues, "evidence.unavailable_limitation", $"{path}.limitation", "Unavailable evidence requires a limitation.");
        }
        if (value.Basis is not null) RequireText(value.Basis, $"{path}.basis", issues);
        if (value.Limitation is not null) RequireText(value.Limitation, $"{path}.limitation", issues);
    }

    private static bool ValidateCollection<T>(
        ImmutableArray<T> values,
        string path,
        bool allowEmpty,
        int maximum,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (values.IsDefault)
        {
            Add(issues, "collection.default", path, "Collection must be initialized.");
            return false;
        }
        if (!allowEmpty && values.IsEmpty)
        {
            Add(issues, "collection.required", path, "At least one value is required.");
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
        bool allowEmpty,
        bool requireSorted,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ValidateCollection(values, path, allowEmpty, MaximumCollectionSize, issues)) return;
        ValidateUnique(values, path, issues);
        for (var index = 0; index < values.Length; index++) RequireId(values[index], $"{path}[{index}]", issues);
        RequireSorted(values, path, requireSorted, issues);
    }

    private static void ValidateReferences(
        ImmutableArray<string> values,
        string path,
        bool allowEmpty,
        bool requireSorted,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues,
        int maximum = MaximumCollectionSize)
    {
        if (!ValidateCollection(values, path, allowEmpty, maximum, issues)) return;
        ValidateUnique(values, path, issues);
        for (var index = 0; index < values.Length; index++) RequireReference(values[index], $"{path}[{index}]", issues);
        RequireSorted(values, path, requireSorted, issues);
    }

    private static void RequireSorted(
        ImmutableArray<string> values,
        string path,
        bool required,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (required && !values.SequenceEqual(values.OrderBy(item => item, StringComparer.Ordinal)))
        {
            Add(issues, "order.nondeterministic", path, "Values must be sorted ordinally.");
        }
    }

    private static void ValidateUnique(IEnumerable<string> values, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value ?? "")) Add(issues, "id.duplicate", path, $"Duplicate ID '{value}'.");
        }
    }

    private static void ValidateMemoryLink(
        string ownId,
        string? reference,
        IReadOnlySet<string> ids,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (reference is null) return;
        if (reference == ownId) Add(issues, "memory.self_reference", path, "Memory cannot reference itself.");
        RequireExisting(reference, ids, path, "memory entry", issues);
    }

    private static void RequireExisting(
        string reference,
        IReadOnlySet<string> ids,
        string path,
        string kind,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (!ids.Contains(reference)) Add(issues, "reference.dangling", path, $"Referenced {kind} '{reference}' is absent.");
    }

    private static void RequireId(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || !IdRegex().IsMatch(value))
        {
            Add(issues, "id.invalid", path, "ID must be 1-160 lowercase letters, digits, or '._:-' and start with a letter or digit.");
        }
    }

    private static void OptionalId(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is not null) RequireId(value, path, issues);
    }

    private static void RequireReference(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
        {
            Add(issues, "reference.invalid", path, "Reference must contain 1-512 printable characters.");
        }
    }

    private static void RequireSessionReference(string? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 96 || !SessionReferenceRegex().IsMatch(value))
        {
            Add(issues, "branch.session_reference", path, "Session reference must be a safe 1-96 character local session name; legacy uppercase is preserved.");
        }
    }

    private static void RequireText(
        string? value,
        string path,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues,
        int maximum = 4_096)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(ch => ch is '\0'))
        {
            Add(issues, "text.invalid", path, $"Text must contain 1-{maximum} bounded characters.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value == default || value.Offset != TimeSpan.Zero) Add(issues, "time.utc", path, "Timestamp must be a non-default UTC value.");
    }

    private static void OptionalUtc(DateTimeOffset? value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value is not null) RequireUtc(value.Value, path, issues);
    }

    private static void RequireNonNegative(long value, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value < 0) Add(issues, "number.negative", path, "Value must be non-negative.");
    }

    private static void RequireRange(int value, int minimum, int maximum, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value < minimum || value > maximum) Add(issues, "number.range", path, $"Value must be from {minimum} through {maximum}.");
    }

    private static void RequireDecimalRange(decimal value, decimal minimum, decimal maximum, string path, ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        if (value < minimum || value > maximum) Add(issues, "number.range", path, $"Value must be from {minimum} through {maximum}.");
    }

    private static void RequireHash(
        string? value,
        string path,
        bool allowShortGitHash,
        ImmutableArray<ArenaContractValidationIssue>.Builder issues)
    {
        var minimum = allowShortGitHash ? 7 : 64;
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimum || value.Length > 64 || !HexRegex().IsMatch(value)
            || (!allowShortGitHash && value.Length != 64))
        {
            Add(issues, "hash.invalid", path, allowShortGitHash
                ? "Hash must contain 7-64 hexadecimal characters."
                : "SHA-256 must contain exactly 64 hexadecimal characters.");
        }
    }

    private static IEnumerable<T> Safe<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;

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
        else
        {
            element.WriteTo(writer);
        }
    }

    private static ImmutableArray<ArenaContractValidationIssue> Sort(ImmutableArray<ArenaContractValidationIssue>.Builder issues) =>
        [.. issues.Distinct()
            .OrderBy(issue => issue.Path, StringComparer.Ordinal)
            .ThenBy(issue => issue.Code, StringComparer.Ordinal)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal)];

    private static void Add(ImmutableArray<ArenaContractValidationIssue>.Builder issues, string code, string path, string message) =>
        issues.Add(new ArenaContractValidationIssue(code, path, message));

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:-]{0,159}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionReferenceRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^ai_arena\.[a-z0-9_]+\.v[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNameRegex();
}
