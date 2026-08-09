using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaClaimLedgerStoreOptions(
    int MaximumLedgers = 512,
    int MaximumClaimsPerLedger = 2_000,
    int MaximumLedgerBytes = 2 * 1024 * 1024);

/// <summary>
/// Bounded one-file-per-ledger persistence. An existing ledger may advance only
/// when its experiment and branch identity are unchanged. Writes are atomic;
/// corrupt, oversize, old-schema, and private artifacts are skipped with safe
/// relative diagnostics.
/// </summary>
public sealed class ArenaClaimLedgerStore
{
    private const string DirectoryName = "claim-ledgers";
    private readonly string _root;
    private readonly ArenaClaimLedgerStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArenaClaimLedgerStore(string root, ArenaClaimLedgerStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumLedgers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumClaimsPerLedger, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumLedgerBytes, 1);
    }

    public async Task<ArenaArtifactWriteResult<ArenaClaimLedgerContract>> SaveAsync(
        ArenaClaimLedgerContract ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ledger.Claims.IsDefault || ledger.Claims.Length > _options.MaximumClaimsPerLedger)
                return Rejected(ledger.Id, "artifact.claim_limit", "Ledger exceeds its bounded claim limit.");
            string json;
            try
            {
                json = ArenaContractCodec.Serialize(ledger);
            }
            catch (InvalidDataException)
            {
                return Rejected(ledger.Id, "artifact.contract_invalid", "Ledger does not satisfy its strict v1 contract or privacy boundary.");
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > _options.MaximumLedgerBytes)
                return Rejected(ledger.Id, "artifact.oversize", "Ledger exceeds its bounded byte limit.");

            using var storeLease = await ArenaPathStoreLease.AcquireAsync(
                _root,
                ".claim-ledger-store.lock",
                cancellationToken).ConfigureAwait(false);
            var directory = Path.Combine(_root, DirectoryName);
            Directory.CreateDirectory(directory);
            var relativePath = RelativePath(ledger.Id);
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
                if (decoded.Ledger is null)
                    return new(ArenaArtifactWriteDisposition.Rejected, relativePath, null, decoded.Diagnostics);
                if (decoded.Ledger.ExperimentId != ledger.ExperimentId || decoded.Ledger.BranchId != ledger.BranchId)
                    return Rejected(ledger.Id, "claim_ledger.identity_conflict", "Existing ledger has a different immutable experiment or branch identity.");
                if (string.Equals(ArenaContractCodec.Serialize(decoded.Ledger), json, StringComparison.Ordinal))
                    return new(ArenaArtifactWriteDisposition.Duplicate, relativePath, decoded.Ledger, []);
                if (!IsMonotonicAdvance(decoded.Ledger, ledger))
                    return Rejected(ledger.Id, "claim_ledger.stale_or_destructive", "Ledger update would remove or rewrite existing audit evidence.");
            }
            else if (Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(_options.MaximumLedgers + 1).Count()
                     >= _options.MaximumLedgers)
            {
                return Rejected(ledger.Id, "artifact.count_limit", "Ledger store has reached its bounded file count.");
            }

            await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new(ArenaArtifactWriteDisposition.Written, relativePath, ledger, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Rejected(ledger.Id, "artifact.write_failed", "Ledger could not be written atomically.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ArenaArtifactLoadResult<ArenaClaimLedgerContract>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = ImmutableArray.CreateBuilder<ArenaClaimLedgerContract>();
            var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
            var directory = Path.Combine(_root, DirectoryName);
            if (!Directory.Exists(directory)) return new([], []);
            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(_options.MaximumLedgers + 1)
                .ToArray();
            if (files.Length > _options.MaximumLedgers)
                diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.count_limit", $"{DirectoryName}/store.json", "Ledger store exceeds its bounded file count."));

            foreach (var path in files.Take(_options.MaximumLedgers))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = $"{DirectoryName}/{Path.GetFileName(path)}";
                var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Ledger is not null) values.Add(decoded.Ledger);
            }

            foreach (var duplicate in values.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
                diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.duplicate_id", $"{DirectoryName}/store.json", $"Ledger ID '{duplicate.Key}' occurs more than once."));
            return new(
                [.. values.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First()).OrderBy(item => item.Id, StringComparer.Ordinal)],
                ArenaExperimentPackCodec.Sort(diagnostics));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DecodeResult> DecodeFileAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > _options.MaximumLedgerBytes)
                return Failed("artifact.oversize", relativePath, "Ledger exceeds its bounded byte limit.");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string json;
            try
            {
                json = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Failed("artifact.corrupt", relativePath, "Ledger is not strict UTF-8 JSON.");
            }

            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
                if (!document.RootElement.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String)
                    return Failed("artifact.schema_missing", relativePath, "Ledger has no schema and cannot be migrated implicitly.");
                var detected = schema.GetString() ?? "";
                if (!string.Equals(detected, ArenaContractSchemas.ClaimLedger, StringComparison.Ordinal))
                {
                    return new(null,
                        [ArenaExperimentPackCodec.Error(
                            detected.StartsWith("ai_arena.claim_ledger.", StringComparison.Ordinal) ? "artifact.migration_required" : "artifact.schema_unsupported",
                            relativePath,
                            "Ledger schema requires an explicit supported migration.",
                            detected,
                            ArenaContractSchemas.ClaimLedger)]);
                }
            }
            catch (JsonException)
            {
                return Failed("artifact.corrupt", relativePath, "Ledger is not strict JSON.");
            }

            if (!ArenaContractCodec.TryDeserialize<ArenaClaimLedgerContract>(json, out var ledger, out var issues) || ledger is null)
                return new(null,
                    [.. issues.Select(item => ArenaExperimentPackCodec.Error($"artifact.contract.{item.Code}", relativePath, $"Ledger validation failed at {item.Path}."))]);
            if (ledger.Claims.Length > _options.MaximumClaimsPerLedger)
                return Failed("artifact.claim_limit", relativePath, "Ledger exceeds its bounded claim limit.");
            if (!string.Equals(json, ArenaContractCodec.Serialize(ledger), StringComparison.Ordinal))
                return Failed("artifact.canonical_required", relativePath, "Ledger must use canonical v1 JSON encoding.");
            return new(ledger, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("artifact.read_failed", relativePath, "Ledger could not be read.");
        }
    }

    private static string RelativePath(string id)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(id) ? "invalid" : id)));
        return $"{DirectoryName}/{digest}.json";
    }

    private static bool IsMonotonicAdvance(ArenaClaimLedgerContract existing, ArenaClaimLedgerContract incoming)
    {
        if (existing.CreatedAtUtc != incoming.CreatedAtUtc) return false;
        var incomingEvidence = incoming.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (existing.Evidence.Any(item => !incomingEvidence.TryGetValue(item.Id, out var next) || next != item)) return false;
        var existingEvidenceIds = existing.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var appendedObservedEvidence = incoming.Evidence
            .Where(item => !existingEvidenceIds.Contains(item.Id) && item.State == ArenaEvidenceState.Observed)
            .ToArray();
        var incomingClaims = incoming.Claims.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var claim in existing.Claims)
        {
            if (!incomingClaims.TryGetValue(claim.Id, out var next)) return false;
            if (claim.MessageId != next.MessageId
                || claim.ClaimantId != next.ClaimantId
                || claim.ClaimSummary != next.ClaimSummary
                || claim.AssertedConfidence != next.AssertedConfidence
                || claim.Provenance != next.Provenance)
            {
                return false;
            }
            if (!claim.SourceEvidenceIds.All(next.SourceEvidenceIds.Contains)
                || !claim.ContradictionClaimIds.All(next.ContradictionClaimIds.Contains)
                || !claim.ReviewerIds.All(next.ReviewerIds.Contains))
            {
                return false;
            }
            if (claim.Status != next.Status)
            {
                var hasNewReviewer = next.ReviewerIds.Except(claim.ReviewerIds, StringComparer.Ordinal).Any();
                var relatedReferences = next.ContradictionClaimIds.Append(claim.Id).ToHashSet(StringComparer.Ordinal);
                var hasReviewEvidence = appendedObservedEvidence.Any(item =>
                    item.ReferenceId is not null && relatedReferences.Contains(item.ReferenceId));
                if (!hasNewReviewer || !hasReviewEvidence)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static ArenaArtifactWriteResult<ArenaClaimLedgerContract> Rejected(string id, string code, string message)
    {
        var relativePath = RelativePath(id);
        return new(ArenaArtifactWriteDisposition.Rejected, relativePath, null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);
    }

    private static DecodeResult Failed(string code, string relativePath, string message) =>
        new(null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private sealed record DecodeResult(
        ArenaClaimLedgerContract? Ledger,
        ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);
}
