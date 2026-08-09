using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaExperimentRunStoreOptions(
    int MaximumRuns = ArenaExperimentRunStoreOptions.DefaultMaximumRuns,
    int MaximumRunBytes = 512 * 1024)
{
    public const int DefaultMaximumRuns = 10_000;
}

public sealed record ArenaExperimentRunLoadResult(
    ImmutableArray<ArenaExperimentRunContract> Runs,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

internal sealed record ArenaExperimentRunCapacityResult(
    bool IsAvailable,
    int MaximumRuns,
    int OccupiedRuns,
    int RequiredNewRuns,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

/// <summary>
/// Bounded one-file-per-cell persistence. Re-saving a byte-identical cell is a
/// no-op. Later attempts merge trial references so repeated trials remain
/// auditable instead of replacing earlier evidence.
/// </summary>
public sealed class ExperimentRunStore
{
    private const string DirectoryName = "experiment-runs";
    private readonly string _root;
    private readonly ArenaExperimentRunStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExperimentRunStore(string root, ArenaExperimentRunStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRunBytes, 1);
    }

    public async Task<ArenaArtifactWriteResult<ArenaExperimentRunContract>> SaveAsync(
        ArenaExperimentRunContract run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var executionLease = await AcquireWaitingExecutionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var capacity = await ReserveCapacityAsync(executionLease, [run.CellKey], cancellationToken).ConfigureAwait(false);
        if (!capacity.IsAvailable)
        {
            return new(
                ArenaArtifactWriteDisposition.Rejected,
                RelativePath(run.CellKey),
                null,
                capacity.Diagnostics);
        }

        return await SaveReservedAsync(executionLease, run, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ArenaArtifactWriteResult<ArenaExperimentRunContract>> SaveReservedAsync(
        ExecutionLease executionLease,
        ArenaExperimentRunContract run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        ArgumentNullException.ThrowIfNull(run);
        if (!executionLease.IsActiveFor(this))
        {
            throw new InvalidOperationException("Experiment run persistence requires this store's active execution lease.");
        }

        var relativePath = RelativePath(run.CellKey);
        if (!executionLease.IsReserved(relativePath))
        {
            return Rejected(
                relativePath,
                "experiment_run.capacity_not_reserved",
                "Experiment cell was not included in the runner's durable-capacity reservation.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = await ArenaPathStoreLease.AcquireAsync(
                _root,
                ".experiment-run-store.lock",
                cancellationToken).ConfigureAwait(false);
            return await SaveCoreAsync(run, capacityReserved: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ArenaExperimentRunLoadResult> LoadAllAsync(CancellationToken cancellationToken = default) =>
        LoadAllAsync(DateTimeOffset.UtcNow, cancellationToken);

    public Task<ArenaExperimentRunLoadResult> LoadAllAsync(
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default) =>
        LoadWithModeAsync(observedAtUtc, normalizeRunningAfterRestart: false, cancellationToken);

    internal Task<ArenaExperimentRunLoadResult> RecoverInterruptedAfterRestartAsync(
        ExecutionLease lease,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsActiveFor(this))
        {
            throw new InvalidOperationException("Restart recovery requires this store's active execution lease.");
        }
        return LoadWithModeAsync(observedAtUtc, normalizeRunningAfterRestart: true, cancellationToken);
    }

    internal ValueTask<ExecutionLease> AcquireExecutionLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_root, DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".runner.lock");
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return ValueTask.FromResult(new ExecutionLease(this, stream));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Experiment run store already has an active execution lease.",
                exception);
        }
    }

    internal async Task<ArenaExperimentRunCapacityResult> ReserveCapacityAsync(
        ExecutionLease executionLease,
        IEnumerable<string> cellKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionLease);
        ArgumentNullException.ThrowIfNull(cellKeys);
        if (!executionLease.IsActiveFor(this))
        {
            throw new InvalidOperationException("Capacity reservation requires this store's active execution lease.");
        }

        var relativePaths = cellKeys
            .Select(RelativePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = await ArenaPathStoreLease.AcquireAsync(
                _root,
                ".experiment-run-store.lock",
                cancellationToken).ConfigureAwait(false);
            var directory = Path.Combine(_root, DirectoryName);
            Directory.CreateDirectory(directory);
            var occupied = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Take(_options.MaximumRuns + 1)
                .Count();
            var required = relativePaths.Count(relativePath =>
                !File.Exists(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
            var available = Math.Max(0, _options.MaximumRuns - occupied);
            if (occupied > _options.MaximumRuns || required > available)
            {
                return new(
                    false,
                    _options.MaximumRuns,
                    occupied,
                    required,
                    [ArenaExperimentPackCodec.Error(
                        "experiment_run.capacity_insufficient",
                        $"{DirectoryName}/store.json",
                        $"Experiment requires {required} new durable run artifact(s), but the bounded store has {available} available slot(s) of {_options.MaximumRuns}; no cells were scheduled.")]);
            }

            executionLease.Reserve(relativePaths);
            return new(true, _options.MaximumRuns, occupied, required, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ExecutionLease> AcquireWaitingExecutionLeaseAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_root, DirectoryName);
        Directory.CreateDirectory(directory);
        var stream = await ArenaPathStoreLease.AcquireAsync(
            directory,
            ".runner.lock",
            cancellationToken).ConfigureAwait(false);
        return new ExecutionLease(this, stream);
    }

    private async Task<ArenaExperimentRunLoadResult> LoadWithModeAsync(
        DateTimeOffset observedAtUtc,
        bool normalizeRunningAfterRestart,
        CancellationToken cancellationToken)
    {
        RequireUtc(observedAtUtc);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = normalizeRunningAfterRestart
                ? await ArenaPathStoreLease.AcquireAsync(
                    _root,
                    ".experiment-run-store.lock",
                    cancellationToken).ConfigureAwait(false)
                : null;
            var runs = ImmutableArray.CreateBuilder<ArenaExperimentRunContract>();
            var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
            var directory = Path.Combine(_root, DirectoryName);
            if (!Directory.Exists(directory)) return new([], []);

            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Take(_options.MaximumRuns + 1)
                .ToArray();
            if (files.Length > _options.MaximumRuns)
            {
                diagnostics.Add(ArenaExperimentPackCodec.Error(
                    "artifact.count_limit",
                    $"{DirectoryName}/store.json",
                    "Experiment run store exceeds its bounded file count."));
            }

            foreach (var path in files.Take(_options.MaximumRuns))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = $"{DirectoryName}/{Path.GetFileName(path)}";
                var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Run is null) continue;

                var normalized = normalizeRunningAfterRestart
                    ? ArenaExperimentRunPolicy.NormalizeAfterRestart(decoded.Run, observedAtUtc)
                    : decoded.Run;
                if (normalizeRunningAfterRestart && !ReferenceEquals(normalized, decoded.Run))
                {
                    try
                    {
                        await WriteRunAsync(path, normalized, cancellationToken).ConfigureAwait(false);
                        diagnostics.Add(new(
                            "experiment_run.restart_normalized",
                            ArenaArtifactDiagnosticSeverity.Information,
                            relativePath,
                            "Running cell was normalized to interrupted after restart."));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(ArenaExperimentPackCodec.Error(
                            "artifact.write_failed",
                            relativePath,
                            "Restart-normalized run could not be written atomically."));
                    }
                }

                runs.Add(normalized);
            }

            var uniqueRuns = ImmutableArray.CreateBuilder<ArenaExperimentRunContract>();
            foreach (var group in runs.GroupBy(item => item.CellKey, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                uniqueRuns.Add(group.OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal).First());
                if (group.Count() > 1)
                {
                    diagnostics.Add(ArenaExperimentPackCodec.Error(
                        "experiment_run.duplicate_cell",
                        $"{DirectoryName}/store.json",
                        $"Cell '{group.Key}' occurs more than once; only the newest valid record was loaded."));
                }
            }

            return new(
                uniqueRuns.ToImmutable(),
                ArenaExperimentPackCodec.Sort(diagnostics));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArenaArtifactWriteResult<ArenaExperimentRunContract>> SaveCoreAsync(
        ArenaExperimentRunContract run,
        bool capacityReserved,
        CancellationToken cancellationToken)
    {
        var relativePath = RelativePath(run.CellKey);
        string json;
        try
        {
            json = ArenaContractCodec.Serialize(run);
        }
        catch (InvalidDataException)
        {
            return Rejected(relativePath, "artifact.contract_invalid", "Experiment run does not satisfy its frozen v1 contract.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > _options.MaximumRunBytes)
        {
            return Rejected(relativePath, "artifact.oversize", "Experiment run exceeds its bounded byte limit.");
        }

        var directory = Path.Combine(_root, DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        ArenaExperimentRunContract artifactToWrite = run;
        var disposition = ArenaArtifactWriteDisposition.Written;
        if (File.Exists(path))
        {
            var decoded = await DecodeFileAsync(path, relativePath, cancellationToken).ConfigureAwait(false);
            if (decoded.Run is null)
            {
                return new(ArenaArtifactWriteDisposition.Rejected, relativePath, null, decoded.Diagnostics);
            }

            if (!HasSameCellIdentity(decoded.Run, run))
            {
                return Rejected(relativePath, "experiment_run.identity_conflict", "Existing cell has a different immutable identity.");
            }

            if (HasConflictingEvidence(decoded.Run, run))
            {
                return Rejected(relativePath, "experiment_run.evidence_conflict", "Incoming run rewrites an existing evidence identity.");
            }

            artifactToWrite = Merge(decoded.Run, run);
            var mergedJson = ArenaContractCodec.Serialize(artifactToWrite);
            if (string.Equals(mergedJson, ArenaContractCodec.Serialize(decoded.Run), StringComparison.Ordinal))
            {
                return new(ArenaArtifactWriteDisposition.Duplicate, relativePath, decoded.Run, []);
            }

            json = mergedJson;
            bytes = Encoding.UTF8.GetBytes(json);
        }
        else if (!capacityReserved
                 && Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(_options.MaximumRuns + 1).Count()
                 >= _options.MaximumRuns)
        {
            return Rejected(relativePath, "artifact.count_limit", "Experiment run store has reached its bounded file count.");
        }

        try
        {
            await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new(disposition, relativePath, artifactToWrite, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Rejected(relativePath, "artifact.write_failed", "Experiment run could not be written atomically.");
        }
    }

    private async Task<RunDecodeResult> DecodeFileAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > _options.MaximumRunBytes)
            {
                return Failed("artifact.oversize", relativePath, "Experiment run exceeds its bounded byte limit.");
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
                return Failed("artifact.corrupt", relativePath, "Experiment run is not strict UTF-8 JSON.");
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.String)
                {
                    return Failed("artifact.schema_missing", relativePath, "Experiment run has no schema and cannot be migrated implicitly.");
                }

                var detected = schema.GetString() ?? "";
                if (!string.Equals(detected, ArenaContractSchemas.ExperimentRun, StringComparison.Ordinal))
                {
                    var family = detected.StartsWith("ai_arena.experiment_run.", StringComparison.Ordinal);
                    return new(null,
                        [ArenaExperimentPackCodec.Error(
                            family ? "artifact.migration_required" : "artifact.schema_unsupported",
                            relativePath,
                            family
                                ? "Experiment run requires an explicit schema migration."
                                : "Experiment run schema is unsupported.",
                            detected,
                            ArenaContractSchemas.ExperimentRun)]);
                }
            }

            if (!ArenaContractCodec.TryDeserialize<ArenaExperimentRunContract>(json, out var run, out var issues)
                || run is null)
            {
                return new(null,
                    [.. issues.Select(issue => ArenaExperimentPackCodec.Error(
                        $"artifact.contract.{issue.Code}",
                        relativePath,
                        $"Experiment run validation failed at {issue.Path}."))]);
            }

            if (!string.Equals(json, ArenaContractCodec.Serialize(run), StringComparison.Ordinal))
            {
                return Failed("artifact.canonical_required", relativePath, "Experiment run must use canonical v1 JSON encoding.");
            }

            return new(run, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("artifact.read_failed", relativePath, "Experiment run could not be read.");
        }
    }

    private async Task WriteRunAsync(
        string path,
        ArenaExperimentRunContract run,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(run));
        await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static ArenaExperimentRunContract Merge(
        ArenaExperimentRunContract existing,
        ArenaExperimentRunContract incoming)
    {
        var stateSource = SelectStateSource(existing, incoming);
        var evidence = existing.Evidence
            .Concat(incoming.Evidence)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        return existing with
        {
            CreatedAtUtc = existing.CreatedAtUtc <= incoming.CreatedAtUtc ? existing.CreatedAtUtc : incoming.CreatedAtUtc,
            State = stateSource.State,
            Attempts = Math.Max(existing.Attempts, incoming.Attempts),
            UpdatedAtUtc = existing.UpdatedAtUtc >= incoming.UpdatedAtUtc ? existing.UpdatedAtUtc : incoming.UpdatedAtUtc,
            TrialIds = existing.TrialIds
                .Concat(incoming.TrialIds)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToImmutableArray(),
            InterruptionReason = stateSource.InterruptionReason,
            Evidence = evidence
        };
    }

    private static ArenaExperimentRunContract SelectStateSource(
        ArenaExperimentRunContract existing,
        ArenaExperimentRunContract incoming)
    {
        if (incoming.Attempts > existing.Attempts) return incoming;
        if (incoming.Attempts < existing.Attempts) return existing;

        var existingTerminal = IsTerminal(existing.State);
        var incomingTerminal = IsTerminal(incoming.State);
        if (existingTerminal && !incomingTerminal) return existing;
        if (!existingTerminal && incomingTerminal) return incoming;
        if (incoming.UpdatedAtUtc > existing.UpdatedAtUtc) return incoming;
        if (incoming.UpdatedAtUtc < existing.UpdatedAtUtc) return existing;
        return existing;
    }

    private static bool IsTerminal(ArenaExperimentRunState state) => state is
        ArenaExperimentRunState.Completed
        or ArenaExperimentRunState.Failed
        or ArenaExperimentRunState.Cancelled
        or ArenaExperimentRunState.Interrupted;

    private static bool HasSameCellIdentity(
        ArenaExperimentRunContract left,
        ArenaExperimentRunContract right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.ExperimentId, right.ExperimentId, StringComparison.Ordinal)
        && string.Equals(left.ExperimentFingerprint, right.ExperimentFingerprint, StringComparison.Ordinal)
        && string.Equals(left.VariantFingerprint, right.VariantFingerprint, StringComparison.Ordinal)
        && left.Repetition == right.Repetition
        && string.Equals(left.CellKey, right.CellKey, StringComparison.Ordinal);

    private static bool HasConflictingEvidence(
        ArenaExperimentRunContract existing,
        ArenaExperimentRunContract incoming)
    {
        var existingById = existing.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return incoming.Evidence.Any(item =>
            existingById.TryGetValue(item.Id, out var persisted)
            && persisted != item);
    }

    private static string RelativePath(string cellKey)
    {
        var digest = cellKey.StartsWith("cell:", StringComparison.Ordinal) ? cellKey["cell:".Length..] : ExperimentExpander.Hash(cellKey);
        return $"{DirectoryName}/{digest}.json";
    }

    private static ArenaArtifactWriteResult<ArenaExperimentRunContract> Rejected(
        string relativePath,
        string code,
        string message) =>
        new(ArenaArtifactWriteDisposition.Rejected, relativePath, null,
            [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private static RunDecodeResult Failed(string code, string relativePath, string message) =>
        new(null, [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Observation time must be UTC.", nameof(value));
        }
    }

    private sealed record RunDecodeResult(
        ArenaExperimentRunContract? Run,
        ImmutableArray<ArenaArtifactDiagnostic> Diagnostics);

    internal sealed class ExecutionLease(ExperimentRunStore owner, FileStream stream) : IAsyncDisposable
    {
        private readonly ExperimentRunStore _owner = owner;
        private FileStream? _stream = stream;
        private ImmutableHashSet<string>? _reservedRelativePaths;

        internal bool IsActiveFor(ExperimentRunStore owner) =>
            ReferenceEquals(_owner, owner) && Volatile.Read(ref _stream) is not null;

        internal bool IsReserved(string relativePath) =>
            _reservedRelativePaths?.Contains(relativePath) == true;

        internal void Reserve(ImmutableArray<string> relativePaths)
        {
            if (_reservedRelativePaths is not null)
            {
                throw new InvalidOperationException("Experiment run capacity was already reserved for this execution lease.");
            }

            _reservedRelativePaths = relativePaths.ToImmutableHashSet(StringComparer.Ordinal);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
