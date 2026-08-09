using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaExperimentDefinitionStoreOptions(
    int MaximumDefinitions = 512,
    int MaximumDefinitionBytes = 512 * 1024);

public sealed record ArenaExperimentDefinitionLoadResult(
    ImmutableArray<ArenaExperimentContract> Definitions,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

/// <summary>
/// Bounded canonical persistence for matrix definitions. A definition is the
/// restart authority for the matrix shape; one-file run records remain the
/// authority for attempts, child sessions, and evidence. Terminal definitions
/// can re-enter Running only through an explicitly approved retry write.
/// </summary>
public sealed class ExperimentDefinitionStore
{
    private const string DirectoryName = "experiment-definitions";
    private readonly string _root;
    private readonly ArenaExperimentDefinitionStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExperimentDefinitionStore(
        string root,
        ArenaExperimentDefinitionStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumDefinitions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumDefinitionBytes, 1);
    }

    public Task<ArenaArtifactWriteResult<ArenaExperimentContract>> SaveAsync(
        ArenaExperimentContract definition,
        bool retryApproved = false,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(definition, retryApproved, executionLease: null, cancellationToken);

    public Task<ArenaArtifactWriteResult<ArenaExperimentContract>> SaveForExecutionAsync(
        ExecutionLease executionLease,
        ArenaExperimentContract definition,
        bool retryApproved = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        if (!executionLease.IsActiveFor(this))
        {
            throw new InvalidOperationException("Experiment definition execution requires this store's active owner lease.");
        }
        return SaveCoreAsync(definition, retryApproved, executionLease, cancellationToken);
    }

    public ValueTask<ExecutionLease> AcquireExecutionLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_root, DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".coordinator.lock");
        try
        {
            return ValueTask.FromResult(new ExecutionLease(
                this,
                new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose)));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Experiment definition store already has an active coordinator owner.",
                exception);
        }
    }

    public Task<ArenaExperimentDefinitionLoadResult> RecoverInterruptedAfterRestartAsync(
        ExecutionLease executionLease,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        if (!executionLease.IsActiveFor(this))
        {
            throw new InvalidOperationException("Experiment definition restart recovery requires this store's active owner lease.");
        }
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Restart observation time must be non-default UTC.", nameof(observedAtUtc));
        }
        return LoadWithModeAsync(normalizeRunningAfterRestart: true, cancellationToken);
    }

    private async Task<ArenaArtifactWriteResult<ArenaExperimentContract>> SaveCoreAsync(
        ArenaExperimentContract definition,
        bool retryApproved,
        ExecutionLease? executionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var relativePath = RelativePath(definition.Id);
        if (definition.Status == ArenaExperimentStatus.Running
            && (executionLease is null || !executionLease.IsActiveFor(this)))
        {
            return Rejected(
                relativePath,
                "experiment_definition.execution_lease_required",
                "Running definitions require an active coordinator owner lease.");
        }
        string incomingJson;
        try
        {
            incomingJson = ArenaContractCodec.Serialize(definition);
        }
        catch (InvalidDataException)
        {
            return Rejected(relativePath, "artifact.contract_invalid", "Experiment definition does not satisfy its frozen v1 contract.");
        }

        var incomingBytes = Encoding.UTF8.GetBytes(incomingJson);
        if (incomingBytes.Length > _options.MaximumDefinitionBytes)
        {
            return Rejected(relativePath, "artifact.oversize", "Experiment definition exceeds its bounded byte limit.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = await ArenaPathStoreLease.AcquireAsync(
                _root,
                ".experiment-definition-store.lock",
                cancellationToken).ConfigureAwait(false);
            var directory = Path.Combine(_root, DirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var artifact = definition;
            if (File.Exists(path))
            {
                var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
                if (decoded.Definition is null)
                {
                    return new(ArenaArtifactWriteDisposition.Rejected, relativePath, null, decoded.Diagnostics);
                }

                var existing = decoded.Definition;
                if (!HasSameDefinitionIdentity(existing, definition))
                {
                    return Rejected(relativePath, "experiment_definition.identity_conflict", "Existing definition has different immutable matrix inputs.");
                }
                if (!HasSameEvidence(existing.Evidence, definition.Evidence))
                {
                    return Rejected(relativePath, "experiment_definition.evidence_conflict", "Incoming definition rewrites persisted evidence identities.");
                }
                if (existing.Status == ArenaExperimentStatus.Running
                    && definition.Status is ArenaExperimentStatus.Completed
                        or ArenaExperimentStatus.Cancelled
                        or ArenaExperimentStatus.Interrupted
                    && (executionLease is null || !executionLease.IsActiveFor(this)))
                {
                    return Rejected(
                        relativePath,
                        "experiment_definition.execution_lease_required",
                        "Only the active coordinator owner can terminalize a Running definition.");
                }

                if (!TryAdvanceStatus(
                        existing.Status,
                        definition.Status,
                        retryApproved,
                        out var status,
                        out var failureCode,
                        out var failureMessage))
                {
                    return Rejected(
                        relativePath,
                        failureCode,
                        failureMessage);
                }

                artifact = definition with
                {
                    CreatedAtUtc = existing.CreatedAtUtc,
                    Status = status,
                    Evidence = existing.Evidence
                };
                var mergedJson = ArenaContractCodec.Serialize(artifact);
                if (string.Equals(mergedJson, ArenaContractCodec.Serialize(existing), StringComparison.Ordinal))
                {
                    return new(ArenaArtifactWriteDisposition.Duplicate, relativePath, existing, []);
                }

                incomingJson = mergedJson;
                incomingBytes = Encoding.UTF8.GetBytes(mergedJson);
            }
            else
            {
                if (definition.Status is ArenaExperimentStatus.Completed
                    or ArenaExperimentStatus.Cancelled
                    or ArenaExperimentStatus.Interrupted)
                {
                    return Rejected(
                        relativePath,
                        "experiment_definition.initial_status",
                        "A new experiment definition must begin as Draft or enter Running under an owner lease.");
                }
                var count = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    .Take(_options.MaximumDefinitions + 1)
                    .Count();
                if (count >= _options.MaximumDefinitions)
                {
                    return Rejected(relativePath, "artifact.count_limit", "Experiment definition store has reached its bounded file count.");
                }
            }

            try
            {
                await ArenaExperimentPackStore.WriteAtomicallyAsync(path, incomingBytes, cancellationToken).ConfigureAwait(false);
                return new(ArenaArtifactWriteDisposition.Written, relativePath, artifact, []);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Rejected(relativePath, "artifact.write_failed", "Experiment definition could not be written atomically.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ArenaExperimentDefinitionLoadResult> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        LoadWithModeAsync(normalizeRunningAfterRestart: false, cancellationToken);

    private async Task<ArenaExperimentDefinitionLoadResult> LoadWithModeAsync(
        bool normalizeRunningAfterRestart,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = normalizeRunningAfterRestart
                ? await ArenaPathStoreLease.AcquireAsync(
                    _root,
                    ".experiment-definition-store.lock",
                    cancellationToken).ConfigureAwait(false)
                : null;
            var definitions = ImmutableArray.CreateBuilder<ArenaExperimentContract>();
            var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
            var directory = Path.Combine(_root, DirectoryName);
            if (!Directory.Exists(directory))
            {
                return new([], []);
            }

            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(_options.MaximumDefinitions + 1)
                .ToArray();
            if (files.Length > _options.MaximumDefinitions)
            {
                diagnostics.Add(ArenaExperimentPackCodec.Error(
                    "artifact.count_limit",
                    $"{DirectoryName}/store.json",
                    "Experiment definition store exceeds its bounded file count."));
            }

            foreach (var path in files.Take(_options.MaximumDefinitions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = $"{DirectoryName}/{Path.GetFileName(path)}";
                var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Definition is null)
                {
                    continue;
                }

                if (!string.Equals(relativePath, RelativePath(decoded.Definition.Id), StringComparison.Ordinal))
                {
                    diagnostics.Add(ArenaExperimentPackCodec.Error(
                        "experiment_definition.path_identity",
                        relativePath,
                        "Experiment definition file name does not match its stable ID."));
                    continue;
                }

                var definition = decoded.Definition;
                if (normalizeRunningAfterRestart && definition.Status == ArenaExperimentStatus.Running)
                {
                    definition = definition with { Status = ArenaExperimentStatus.Interrupted };
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(definition));
                        await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                        diagnostics.Add(new(
                            "experiment_definition.restart_normalized",
                            ArenaArtifactDiagnosticSeverity.Information,
                            relativePath,
                            "Abandoned Running definition was normalized to Interrupted after acquiring the coordinator owner lease."));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        diagnostics.Add(ArenaExperimentPackCodec.Error(
                            "artifact.write_failed",
                            relativePath,
                            "Restart-normalized experiment definition could not be written atomically."));
                        continue;
                    }
                }
                definitions.Add(definition);
            }

            return new(
                [.. definitions.OrderBy(item => item.Id, StringComparer.Ordinal)],
                ArenaExperimentPackCodec.Sort(diagnostics));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DefinitionDecodeResult> DecodeFileAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > _options.MaximumDefinitionBytes)
            {
                return Failed("artifact.oversize", relativePath, "Experiment definition exceeds its bounded byte limit.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string json;
            JsonDocument document;
            try
            {
                json = new UTF8Encoding(false, true).GetString(bytes);
                document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            {
                return Failed("artifact.corrupt", relativePath, "Experiment definition is not strict UTF-8 JSON.");
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.String)
                {
                    return Failed("artifact.schema_missing", relativePath, "Experiment definition has no schema.");
                }

                var detected = schema.GetString() ?? "";
                if (!string.Equals(detected, ArenaContractSchemas.Experiment, StringComparison.Ordinal))
                {
                    return new(
                        null,
                        [ArenaExperimentPackCodec.Error(
                            detected.StartsWith("ai_arena.experiment.", StringComparison.Ordinal)
                                ? "artifact.migration_required"
                                : "artifact.schema_unsupported",
                            relativePath,
                            "Experiment definition schema is unsupported by the v1 store.",
                            detected,
                            ArenaContractSchemas.Experiment)]);
                }
            }

            if (!ArenaContractCodec.TryDeserialize<ArenaExperimentContract>(json, out var definition, out var issues)
                || definition is null)
            {
                return new(
                    null,
                    [.. issues.Select(issue => ArenaExperimentPackCodec.Error(
                        $"artifact.contract.{issue.Code}",
                        relativePath,
                        $"Experiment definition validation failed at {issue.Path}."))]);
            }
            if (!string.Equals(json, ArenaContractCodec.Serialize(definition), StringComparison.Ordinal))
            {
                return Failed("artifact.canonical_required", relativePath, "Experiment definition must use canonical v1 JSON encoding.");
            }

            return new(definition, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("artifact.read_failed", relativePath, "Experiment definition could not be read.");
        }
    }

    private static bool HasSameDefinitionIdentity(
        ArenaExperimentContract left,
        ArenaExperimentContract right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(
            ArenaExperimentFingerprints.Experiment(left),
            ArenaExperimentFingerprints.Experiment(right),
            StringComparison.Ordinal);

    private static bool HasSameEvidence(
        ImmutableArray<ArenaEvidenceAssertion> left,
        ImmutableArray<ArenaEvidenceAssertion> right) =>
        string.Equals(
            JsonSerializer.Serialize(left),
            JsonSerializer.Serialize(right),
            StringComparison.Ordinal);

    private static bool TryAdvanceStatus(
        ArenaExperimentStatus existing,
        ArenaExperimentStatus incoming,
        bool retryApproved,
        out ArenaExperimentStatus result,
        out string failureCode,
        out string failureMessage)
    {
        result = existing;
        failureCode = "experiment_definition.lifecycle_transition";
        failureMessage = "Experiment definitions must advance through the owner-held Running lifecycle.";
        if (existing == incoming)
        {
            return true;
        }
        if (existing == ArenaExperimentStatus.Draft)
        {
            if (incoming == ArenaExperimentStatus.Running)
            {
                result = ArenaExperimentStatus.Running;
                return true;
            }
            return false;
        }
        if (existing == ArenaExperimentStatus.Running)
        {
            if (incoming == ArenaExperimentStatus.Draft)
            {
                return true;
            }
            if (incoming is ArenaExperimentStatus.Completed or ArenaExperimentStatus.Cancelled)
            {
                result = incoming;
                return true;
            }
            return false;
        }
        if (incoming == ArenaExperimentStatus.Draft)
        {
            return true;
        }
        if (incoming == ArenaExperimentStatus.Running && retryApproved)
        {
            result = ArenaExperimentStatus.Running;
            return true;
        }
        if (incoming == ArenaExperimentStatus.Running)
        {
            failureCode = "experiment_definition.retry_approval_required";
            failureMessage = "A terminal experiment definition requires explicit retry approval before it can run again.";
        }
        return false;
    }

    private static string RelativePath(string id)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return $"{DirectoryName}/{digest}.json";
    }

    private static ArenaArtifactWriteResult<ArenaExperimentContract> Rejected(
        string relativePath,
        string code,
        string message) =>
        new(
            ArenaArtifactWriteDisposition.Rejected,
            relativePath,
            null,
            [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private static DefinitionDecodeResult Failed(string code, string relativePath, string message) =>
        new(null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private sealed record DefinitionDecodeResult(
        ArenaExperimentContract? Definition,
        ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

    public sealed class ExecutionLease : IAsyncDisposable
    {
        private readonly ExperimentDefinitionStore _owner;
        private FileStream? _stream;

        internal ExecutionLease(ExperimentDefinitionStore owner, FileStream stream)
        {
            _owner = owner;
            _stream = stream;
        }

        internal bool IsActiveFor(ExperimentDefinitionStore owner) =>
            ReferenceEquals(_owner, owner) && Volatile.Read(ref _stream) is not null;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
