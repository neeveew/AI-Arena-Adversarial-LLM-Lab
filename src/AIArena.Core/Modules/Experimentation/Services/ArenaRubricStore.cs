using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaRubricStoreOptions(
    int MaximumRubrics = 512,
    int MaximumResults = 10_000,
    int MaximumRubricBytes = 512 * 1024,
    int MaximumResultBytes = 2 * 1024 * 1024);

/// <summary>
/// Bounded local persistence for immutable rubric versions and finalized result
/// records. Invalid files are isolated as diagnostics; no fallback observation is
/// invented and no caller path is persisted.
/// </summary>
public sealed class ArenaRubricStore
{
    private const string RubricDirectory = "rubrics";
    private const string ResultDirectory = "rubric-results";
    private const string BlindCommitmentDirectory = "blind-commitments";
    private const string BlindReceiptDirectory = "blind-receipts";
    private const string ModelJudgeReceiptDirectory = "model-judge-receipts";
    private readonly string _root;
    private readonly ArenaRubricStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArenaRubricStore(string root, ArenaRubricStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRubrics, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumResults, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRubricBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumResultBytes, 1);
    }

    public Task<ArenaArtifactWriteResult<ArenaRubricContract>> SaveRubricAsync(
        ArenaRubricContract rubric,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            rubric,
            RubricDirectory,
            _options.MaximumRubrics,
            _options.MaximumRubricBytes,
            static value => ArenaContractCodec.Serialize(value),
            DecodeRubric,
            static (existing, incoming) =>
                string.Equals(existing.Name, incoming.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Version, incoming.Version, StringComparison.Ordinal),
            cancellationToken);

    public async Task<ArenaArtifactWriteResult<ArenaRubricEvaluationResultContract>> SaveResultAsync(
        ArenaRubricEvaluationResultContract result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        var rubric = rubrics.Artifacts.FirstOrDefault(item =>
            item.Id.Equals(result.RubricId, StringComparison.Ordinal)
            && item.Version.Equals(result.RubricVersion, StringComparison.Ordinal));
        var blindCommitments = await LoadBlindCommitmentsAsync(cancellationToken).ConfigureAwait(false);
        var blindReceipts = await LoadBlindReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var modelJudgeReceipts = await LoadModelJudgeReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var relationship = ArenaRubricService.ValidateFinalizedResult(
            rubric,
            result,
            blindCommitments.Artifacts,
            blindReceipts.Artifacts,
            modelJudgeReceipts.Artifacts);
        if (!relationship.IsValid)
        {
            var code = rubric is null ? "rubric_result.rubric_missing" : "rubric_result.derivation_invalid";
            return Rejected<ArenaRubricEvaluationResultContract>(
                ResultDirectory,
                result.Id,
                code,
                rubric is null
                    ? "Result references an unavailable immutable rubric ID/version."
                    : "Result does not recompute exactly from its immutable rubric.");
        }

        return await SaveAsync(
            result,
            ResultDirectory,
            _options.MaximumResults,
            _options.MaximumResultBytes,
            static value => ArenaRubricResultCodec.Serialize(value),
            DecodeRubricResult,
            sameVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ArenaArtifactLoadResult<ArenaRubricContract>> LoadRubricsAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(RubricDirectory, _options.MaximumRubrics, _options.MaximumRubricBytes, DecodeRubric, cancellationToken);

    public async Task<ArenaArtifactWriteResult<ArenaBlindPairwiseCommitmentContract>> SaveBlindCommitmentAsync(
        ArenaBlindPairwiseCommitmentContract commitment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        var rubric = rubrics.Artifacts.SingleOrDefault(item => item.Id == commitment.RubricId && item.Version == commitment.RubricVersion);
        if (!IsBlindCommitmentRelationship(commitment, rubric))
        {
            return Rejected<ArenaBlindPairwiseCommitmentContract>(
                BlindCommitmentDirectory,
                commitment.Id,
                "blind_commitment.relationship_invalid",
                "Blind commitment does not match an immutable rubric and eligible blind_pairwise evaluator.");
        }
        return await SaveAsync(
            commitment,
            BlindCommitmentDirectory,
            _options.MaximumResults,
            _options.MaximumResultBytes,
            static value => ArenaEvaluationEvidenceCodec.Serialize(value),
            DecodeBlindCommitment,
            sameVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArenaArtifactWriteResult<ArenaBlindPairwiseReceiptContract>> SaveBlindReceiptAsync(
        ArenaBlindPairwiseReceiptContract receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var commitments = await LoadBlindCommitmentsAsync(cancellationToken).ConfigureAwait(false);
        var commitment = commitments.Artifacts.SingleOrDefault(item => item.Id == receipt.CommitmentId);
        if (!IsBlindReceiptRelationship(receipt, commitment))
        {
            return Rejected<ArenaBlindPairwiseReceiptContract>(
                BlindReceiptDirectory,
                receipt.Id,
                "blind_receipt.relationship_invalid",
                "Blind receipt does not open one persisted pre-judgment commitment.");
        }
        return await SaveAsync(
            receipt,
            BlindReceiptDirectory,
            _options.MaximumResults,
            _options.MaximumResultBytes,
            static value => ArenaEvaluationEvidenceCodec.Serialize(value),
            DecodeBlindReceipt,
            sameVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArenaArtifactWriteResult<ArenaModelJudgeReceiptContract>> SaveModelJudgeReceiptAsync(
        ArenaModelJudgeReceiptContract receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        var rubric = rubrics.Artifacts.SingleOrDefault(item => item.Id == receipt.RubricId && item.Version == receipt.RubricVersion);
        if (!IsModelJudgeReceiptRelationship(receipt, rubric))
        {
            return Rejected<ArenaModelJudgeReceiptContract>(
                ModelJudgeReceiptDirectory,
                receipt.Id,
                "model_judge_receipt.relationship_invalid",
                "Model-judge receipt does not match an immutable rubric, evaluator profile, and succeeded provider attempt.");
        }
        return await SaveAsync(
            receipt,
            ModelJudgeReceiptDirectory,
            _options.MaximumResults,
            _options.MaximumResultBytes,
            static value => ArenaEvaluationEvidenceCodec.Serialize(value),
            DecodeModelJudgeReceipt,
            sameVersion: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArenaArtifactLoadResult<ArenaBlindPairwiseCommitmentContract>> LoadBlindCommitmentsAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(BlindCommitmentDirectory, _options.MaximumResults, _options.MaximumResultBytes, DecodeBlindCommitment, cancellationToken).ConfigureAwait(false);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        return FilterRelationships(
            loaded,
            item => IsBlindCommitmentRelationship(item, rubrics.Artifacts.SingleOrDefault(rubric => rubric.Id == item.RubricId && rubric.Version == item.RubricVersion)),
            BlindCommitmentDirectory,
            "blind_commitment.relationship_invalid",
            "Blind commitment does not match an immutable rubric and eligible evaluator.");
    }

    public async Task<ArenaArtifactLoadResult<ArenaBlindPairwiseReceiptContract>> LoadBlindReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(BlindReceiptDirectory, _options.MaximumResults, _options.MaximumResultBytes, DecodeBlindReceipt, cancellationToken).ConfigureAwait(false);
        var commitments = await LoadBlindCommitmentsAsync(cancellationToken).ConfigureAwait(false);
        return FilterRelationships(
            loaded,
            item => IsBlindReceiptRelationship(item, commitments.Artifacts.SingleOrDefault(commitment => commitment.Id == item.CommitmentId)),
            BlindReceiptDirectory,
            "blind_receipt.relationship_invalid",
            "Blind receipt does not open one valid persisted commitment.");
    }

    public async Task<ArenaArtifactLoadResult<ArenaModelJudgeReceiptContract>> LoadModelJudgeReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(ModelJudgeReceiptDirectory, _options.MaximumResults, _options.MaximumResultBytes, DecodeModelJudgeReceipt, cancellationToken).ConfigureAwait(false);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        return FilterRelationships(
            loaded,
            item => IsModelJudgeReceiptRelationship(item, rubrics.Artifacts.SingleOrDefault(rubric => rubric.Id == item.RubricId && rubric.Version == item.RubricVersion)),
            ModelJudgeReceiptDirectory,
            "model_judge_receipt.relationship_invalid",
            "Model-judge receipt does not match an immutable rubric and eligible evaluator profile.");
    }

    public async Task<ArenaArtifactLoadResult<ArenaRubricEvaluationResultContract>> LoadResultsAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(
            ResultDirectory,
            _options.MaximumResults,
            _options.MaximumResultBytes,
            DecodeRubricResult,
            cancellationToken).ConfigureAwait(false);
        var rubrics = await LoadRubricsAsync(cancellationToken).ConfigureAwait(false);
        var blindCommitments = await LoadBlindCommitmentsAsync(cancellationToken).ConfigureAwait(false);
        var blindReceipts = await LoadBlindReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var modelJudgeReceipts = await LoadModelJudgeReceiptsAsync(cancellationToken).ConfigureAwait(false);
        var accepted = ImmutableArray.CreateBuilder<ArenaRubricEvaluationResultContract>();
        var diagnostics = loaded.Diagnostics.ToBuilder();
        foreach (var result in loaded.Artifacts)
        {
            var rubric = rubrics.Artifacts.FirstOrDefault(item =>
                item.Id.Equals(result.RubricId, StringComparison.Ordinal)
                && item.Version.Equals(result.RubricVersion, StringComparison.Ordinal));
            var relationship = ArenaRubricService.ValidateFinalizedResult(
                rubric,
                result,
                blindCommitments.Artifacts,
                blindReceipts.Artifacts,
                modelJudgeReceipts.Artifacts);
            if (relationship.IsValid)
            {
                accepted.Add(result);
                continue;
            }

            diagnostics.Add(ArenaExperimentPackCodec.Error(
                rubric is null ? "rubric_result.rubric_missing" : "rubric_result.derivation_invalid",
                RelativePath(ResultDirectory, result.Id),
                rubric is null
                    ? "Result references an unavailable immutable rubric ID/version."
                    : "Result does not recompute exactly from its immutable rubric."));
        }

        return new(
            [.. accepted.OrderBy(item => item.Id, StringComparer.Ordinal)],
            ArenaExperimentPackCodec.Sort(diagnostics));
    }

    private async Task<ArenaArtifactWriteResult<T>> SaveAsync<T>(
        T artifact,
        string directoryName,
        int maximumCount,
        int maximumBytes,
        Func<T, string> serialize,
        Func<string, string, DecodeResult<T>> decode,
        Func<T, T, bool>? sameVersion,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json;
            try
            {
                json = serialize(artifact);
            }
            catch (InvalidDataException)
            {
                return Rejected<T>(directoryName, artifact.Id, "artifact.contract_invalid", "Artifact does not satisfy its strict versioned contract.");
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > maximumBytes)
                return Rejected<T>(directoryName, artifact.Id, "artifact.oversize", "Artifact exceeds its bounded byte limit.");

            using var storeLease = await ArenaPathStoreLease.AcquireAsync(
                _root,
                ".rubric-store.lock",
                cancellationToken).ConfigureAwait(false);
            var loaded = await LoadCoreAsync(directoryName, maximumCount, maximumBytes, decode, cancellationToken).ConfigureAwait(false);
            var sameIdentity = loaded.Artifacts.FirstOrDefault(item => item.Id == artifact.Id);
            if (sameIdentity is not null)
            {
                if (string.Equals(serialize(sameIdentity), json, StringComparison.Ordinal))
                    return new(ArenaArtifactWriteDisposition.Duplicate, RelativePath(directoryName, artifact.Id), sameIdentity, loaded.Diagnostics);
                return Rejected<T>(directoryName, artifact.Id, "artifact.duplicate_id", "A different immutable artifact already uses this ID.");
            }
            if (sameVersion is not null && loaded.Artifacts.Any(item => sameVersion(item, artifact)))
                return Rejected<T>(directoryName, artifact.Id, "artifact.duplicate_version", "A different rubric already uses this name and version.");

            var directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(maximumCount + 1).Count() >= maximumCount)
                return Rejected<T>(directoryName, artifact.Id, "artifact.count_limit", "Artifact store has reached its bounded file count.");

            var relativePath = RelativePath(directoryName, artifact.Id);
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return Rejected<T>(directoryName, artifact.Id, "artifact.path_conflict", "Artifact path exists but could not be decoded safely.");
            await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new(ArenaArtifactWriteDisposition.Written, relativePath, artifact, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Rejected<T>(directoryName, artifact.Id, "artifact.write_failed", "Artifact could not be written atomically.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArenaArtifactLoadResult<T>> LoadAsync<T>(
        string directoryName,
        int maximumCount,
        int maximumBytes,
        Func<string, string, DecodeResult<T>> decode,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(directoryName, maximumCount, maximumBytes, decode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArenaArtifactLoadResult<T>> LoadCoreAsync<T>(
        string directoryName,
        int maximumCount,
        int maximumBytes,
        Func<string, string, DecodeResult<T>> decode,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        var artifacts = ImmutableArray.CreateBuilder<T>();
        var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
        var directory = Path.Combine(_root, directoryName);
        if (!Directory.Exists(directory)) return new([], []);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(maximumCount + 1)
            .ToArray();
        if (files.Length > maximumCount)
            diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.count_limit", $"{directoryName}/store.json", "Artifact store exceeds its bounded file count."));

        foreach (var path in files.Take(maximumCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = $"{directoryName}/{Path.GetFileName(path)}";
            try
            {
                if (new FileInfo(path).Length > maximumBytes)
                {
                    diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.oversize", relativePath, "Artifact exceeds its bounded byte limit."));
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                string json;
                try
                {
                    json = new UTF8Encoding(false, true).GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.corrupt", relativePath, "Artifact is not strict UTF-8 JSON."));
                    continue;
                }
                var decoded = decode(json, relativePath);
                diagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Artifact is not null) artifacts.Add(decoded.Artifact);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.read_failed", relativePath, "Artifact could not be read."));
            }
        }

        foreach (var duplicate in artifacts.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.duplicate_id", $"{directoryName}/store.json", $"Artifact ID '{duplicate.Key}' occurs more than once."));
        return new(
            [.. artifacts.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(item => item.Id, StringComparer.Ordinal)],
            ArenaExperimentPackCodec.Sort(diagnostics));
    }

    private static bool IsBlindCommitmentRelationship(
        ArenaBlindPairwiseCommitmentContract commitment,
        ArenaRubricContract? rubric) =>
        ArenaEvaluationEvidenceCodec.Validate(commitment).IsValid
        && rubric is not null
        && commitment.Id == ArenaRubricService.StableProofId("blind-commitment", commitment.EvaluationId, commitment.EvaluatorId)
        && rubric.Evaluators.Any(item => item.Id == commitment.EvaluatorId && item.Kind == ArenaRubricEvaluatorKind.BlindPairwise)
        && commitment.CreatedAtUtc >= rubric.CreatedAtUtc;

    private static bool IsBlindReceiptRelationship(
        ArenaBlindPairwiseReceiptContract receipt,
        ArenaBlindPairwiseCommitmentContract? commitment)
    {
        if (!ArenaEvaluationEvidenceCodec.Validate(receipt).IsValid || commitment is null) return false;
        var subjectSet = ArenaEvaluationEvidenceCodec.Sha256(string.Join(
            "\n",
            new[] { receipt.LabelAReferenceId, receipt.LabelBReferenceId }.Order(StringComparer.Ordinal)));
        var view = new ArenaBlindPairwiseView(
            commitment.EvaluationId,
            commitment.RubricId,
            commitment.RubricVersion,
            commitment.LabelAToken,
            commitment.LabelBToken);
        return receipt.EvaluationId == commitment.EvaluationId
            && receipt.Id == ArenaRubricService.StableProofId("blind-receipt", receipt.EvaluationId, receipt.EvaluatorResultId, receipt.CommitmentId)
            && receipt.CreatedAtUtc >= commitment.CreatedAtUtc
            && commitment.SubjectSetSha256 == subjectSet
            && commitment.MappingCommitmentSha256 == ArenaRubricService.BlindMappingCommitment(
                view,
                receipt.LabelAReferenceId,
                receipt.LabelBReferenceId,
                receipt.MappingNonce,
                commitment.EvaluatorId);
    }

    private static bool IsModelJudgeReceiptRelationship(
        ArenaModelJudgeReceiptContract receipt,
        ArenaRubricContract? rubric)
    {
        if (!ArenaEvaluationEvidenceCodec.Validate(receipt).IsValid || rubric is null) return false;
        var evaluator = rubric.Evaluators.SingleOrDefault(item => item.Id == receipt.EvaluatorId);
        return receipt.Id == ArenaRubricService.StableProofId("model-judge-receipt", receipt.EvaluationId, receipt.EvaluatorId, receipt.CorrelationId, receipt.RequestId)
            && evaluator is not null
            && evaluator.Kind == ArenaRubricEvaluatorKind.ModelJudge
            && evaluator.ProfileId == receipt.ProfileId
            && receipt.RequestObservedAtUtc <= receipt.CreatedAtUtc.AddSeconds(1);
    }

    private static ArenaArtifactLoadResult<T> FilterRelationships<T>(
        ArenaArtifactLoadResult<T> loaded,
        Func<T, bool> relationship,
        string directory,
        string code,
        string message)
        where T : class, IArenaVersionedContract
    {
        var accepted = ImmutableArray.CreateBuilder<T>();
        var diagnostics = loaded.Diagnostics.ToBuilder();
        foreach (var item in loaded.Artifacts)
        {
            if (relationship(item)) accepted.Add(item);
            else diagnostics.Add(ArenaExperimentPackCodec.Error(code, RelativePath(directory, item.Id), message));
        }
        return new(
            [.. accepted.OrderBy(item => item.Id, StringComparer.Ordinal)],
            ArenaExperimentPackCodec.Sort(diagnostics));
    }

    private static DecodeResult<ArenaRubricContract> DecodeRubric(string json, string relativePath) =>
        Decode(
            json,
            relativePath,
            ArenaContractSchemas.Rubric,
            static text =>
            {
                var ok = ArenaContractCodec.TryDeserialize<ArenaRubricContract>(text, out var value, out var issues);
                return (ok ? value : null, issues);
            },
            static value => ArenaContractCodec.Serialize(value));

    private static DecodeResult<ArenaRubricEvaluationResultContract> DecodeRubricResult(string json, string relativePath) =>
        Decode(
            json,
            relativePath,
            ArenaRubricResultSchemas.RubricResult,
            static text =>
            {
                var ok = ArenaRubricResultCodec.TryDeserialize(text, out var value, out var issues);
                return (ok ? value : null, issues);
            },
            static value => ArenaRubricResultCodec.Serialize(value));

    private static DecodeResult<ArenaBlindPairwiseCommitmentContract> DecodeBlindCommitment(string json, string relativePath) =>
        Decode(
            json,
            relativePath,
            ArenaEvaluationEvidenceSchemas.BlindPairwiseCommitment,
            static text =>
            {
                var ok = ArenaEvaluationEvidenceCodec.TryDeserialize(text, out ArenaBlindPairwiseCommitmentContract? value, out var issues);
                return (ok ? value : null, issues);
            },
            static value => ArenaEvaluationEvidenceCodec.Serialize(value));

    private static DecodeResult<ArenaBlindPairwiseReceiptContract> DecodeBlindReceipt(string json, string relativePath) =>
        Decode(
            json,
            relativePath,
            ArenaEvaluationEvidenceSchemas.BlindPairwiseReceipt,
            static text =>
            {
                var ok = ArenaEvaluationEvidenceCodec.TryDeserialize(text, out ArenaBlindPairwiseReceiptContract? value, out var issues);
                return (ok ? value : null, issues);
            },
            static value => ArenaEvaluationEvidenceCodec.Serialize(value));

    private static DecodeResult<ArenaModelJudgeReceiptContract> DecodeModelJudgeReceipt(string json, string relativePath) =>
        Decode(
            json,
            relativePath,
            ArenaEvaluationEvidenceSchemas.ModelJudgeReceipt,
            static text =>
            {
                var ok = ArenaEvaluationEvidenceCodec.TryDeserialize(text, out ArenaModelJudgeReceiptContract? value, out var issues);
                return (ok ? value : null, issues);
            },
            static value => ArenaEvaluationEvidenceCodec.Serialize(value));

    private static DecodeResult<T> Decode<T>(
        string json,
        string relativePath,
        string expectedSchema,
        Func<string, (T? Value, ImmutableArray<ArenaContractValidationIssue> Issues)> deserialize,
        Func<T, string> serialize)
        where T : class, IArenaVersionedContract
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Failed<T>("artifact.corrupt", relativePath, "Artifact root must be an object.");
            if (!document.RootElement.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String)
                return Failed<T>("artifact.schema_missing", relativePath, "Artifact has no schema and cannot be migrated implicitly.");
            var detected = schema.GetString() ?? "";
            if (!string.Equals(detected, expectedSchema, StringComparison.Ordinal))
            {
                var family = expectedSchema[..expectedSchema.LastIndexOf(".", StringComparison.Ordinal)];
                return new(null,
                    [ArenaExperimentPackCodec.Error(
                        detected.StartsWith($"{family}.", StringComparison.Ordinal) ? "artifact.migration_required" : "artifact.schema_unsupported",
                        relativePath,
                        "Artifact schema requires an explicit supported migration.",
                        detected,
                        expectedSchema)]);
            }
        }
        catch (JsonException)
        {
            return Failed<T>("artifact.corrupt", relativePath, "Artifact is not strict JSON.");
        }

        var (value, issues) = deserialize(json);
        if (value is null)
            return new(null,
                [.. issues.Select(item => ArenaExperimentPackCodec.Error($"artifact.contract.{item.Code}", relativePath, $"Contract validation failed at {item.Path}."))]);
        if (!string.Equals(json, serialize(value), StringComparison.Ordinal))
            return Failed<T>("artifact.canonical_required", relativePath, "Artifact must use canonical JSON encoding.");
        return new(value, []);
    }

    private static string RelativePath(string directoryName, string id)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return $"{directoryName}/{digest}.json";
    }

    private static ArenaArtifactWriteResult<T> Rejected<T>(string directoryName, string id, string code, string message)
        where T : class, IArenaVersionedContract
    {
        var relativePath = RelativePath(directoryName, string.IsNullOrWhiteSpace(id) ? "invalid" : id);
        return new(ArenaArtifactWriteDisposition.Rejected, relativePath, null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);
    }

    private static DecodeResult<T> Failed<T>(string code, string relativePath, string message)
        where T : class, IArenaVersionedContract =>
        new(null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private sealed record DecodeResult<T>(T? Artifact, ImmutableArray<ArenaArtifactDiagnostic> Diagnostics)
        where T : class, IArenaVersionedContract;
}
