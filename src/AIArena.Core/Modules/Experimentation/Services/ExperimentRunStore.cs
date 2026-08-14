using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public sealed record ArenaExperimentRunStoreOptions(
    int MaximumRuns = ArenaExperimentRunStoreOptions.DefaultMaximumRuns,
    int MaximumRunBytes = 512 * 1024,
    int MaximumParallelDecodes = ArenaExperimentRunStoreOptions.DefaultMaximumParallelDecodes)
{
    public const int DefaultMaximumRuns = 10_000;
    public const int DefaultMaximumParallelDecodes = 4;
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

internal sealed record ArenaExperimentRunLoadMetrics(
    int FilesConsidered,
    int FilesRead,
    int MaximumConcurrentDecodes,
    int ConfiguredParallelDecodes);

/// <summary>
/// Bounded one-file-per-cell persistence. Re-saving a byte-identical cell is a
/// no-op. Later attempts merge trial references so repeated trials remain
/// auditable instead of replacing earlier evidence.
/// </summary>
public sealed class ExperimentRunStore
{
    private const string DirectoryName = "experiment-runs";
    private const int MaximumSinglePooledReadBytes = 8 * 1024 * 1024;
    private readonly string _root;
    private readonly ArenaExperimentRunStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ArenaExperimentRunLoadMetrics _lastLoadMetrics = new(0, 0, 0, 0);
    private Func<string, string, CancellationToken, Task>? _beforeDecodeAsync;
    private Func<string, CancellationToken, Task>? _afterInitialLengthCheckAsync;

    public ExperimentRunStore(string root, ArenaExperimentRunStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRuns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumRunBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumParallelDecodes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumParallelDecodes, 32);
    }

    internal ArenaExperimentRunLoadMetrics LastLoadMetrics => Volatile.Read(ref _lastLoadMetrics);

    internal Func<string, string, CancellationToken, Task>? BeforeDecodeAsync
    {
        get => Volatile.Read(ref _beforeDecodeAsync);
        set => Volatile.Write(ref _beforeDecodeAsync, value);
    }

    internal Func<string, CancellationToken, Task>? AfterInitialLengthCheckAsync
    {
        get => Volatile.Read(ref _afterInitialLengthCheckAsync);
        set => Volatile.Write(ref _afterInitialLengthCheckAsync, value);
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
            if (!Directory.Exists(directory))
            {
                Volatile.Write(ref _lastLoadMetrics, new(0, 0, 0, _options.MaximumParallelDecodes));
                return new([], []);
            }

            var selectedFiles = SelectBoundedFiles(directory, cancellationToken);
            if (selectedFiles.ExceededLimit)
            {
                diagnostics.Add(ArenaExperimentPackCodec.Error(
                    "artifact.count_limit",
                    $"{DirectoryName}/store.json",
                    "Experiment run store exceeds its bounded file count."));
            }

            var files = selectedFiles.Paths.Length > _options.MaximumRuns
                ? selectedFiles.Paths[.._options.MaximumRuns]
                : selectedFiles.Paths;
            var decodedFiles = new RunDecodeResult?[files.Length];
            var reads = 0;
            var activeDecodes = 0;
            var maximumConcurrentDecodes = 0;
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, files.Length),
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = _options.MaximumParallelDecodes
                    },
                    async (index, token) =>
                    {
                        Interlocked.Increment(ref reads);
                        var active = Interlocked.Increment(ref activeDecodes);
                        UpdateMaximum(ref maximumConcurrentDecodes, active);
                        try
                        {
                            var path = files[index];
                            var relativePath = $"{DirectoryName}/{Path.GetFileName(path)}";
                            var beforeDecode = BeforeDecodeAsync;
                            if (beforeDecode is not null)
                            {
                                await beforeDecode(path, relativePath, token).ConfigureAwait(false);
                            }
                            decodedFiles[index] = await DecodeFileAsync(path, relativePath, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeDecodes);
                        }
                    }).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _lastLoadMetrics, new(
                    selectedFiles.FilesConsidered,
                    Volatile.Read(ref reads),
                    Volatile.Read(ref maximumConcurrentDecodes),
                    _options.MaximumParallelDecodes));
            }

            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = files[index];
                var relativePath = $"{DirectoryName}/{Path.GetFileName(path)}";
                var decoded = decodedFiles[index]
                    ?? throw new InvalidOperationException("A completed experiment-run decode produced no result.");
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
        byte[] bytes;
        try
        {
            bytes = ArenaContractCodec.SerializeToUtf8Bytes(run);
        }
        catch (InvalidDataException)
        {
            return Rejected(relativePath, "artifact.contract_invalid", "Experiment run does not satisfy its frozen v1 contract.");
        }

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
            var mergedBytes = ArenaContractCodec.SerializeToUtf8Bytes(artifactToWrite);
            var persistedBytes = ArenaContractCodec.SerializeToUtf8Bytes(decoded.Run);
            if (mergedBytes.AsSpan().SequenceEqual(persistedBytes))
            {
                return new(ArenaArtifactWriteDisposition.Duplicate, relativePath, decoded.Run, []);
            }

            bytes = mergedBytes;
            if (bytes.Length > _options.MaximumRunBytes)
            {
                return Rejected(relativePath, "artifact.oversize", "Merged experiment run exceeds its bounded byte limit.");
            }
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

            var afterLengthCheck = AfterInitialLengthCheckAsync;
            if (afterLengthCheck is not null)
            {
                await afterLengthCheck(path, cancellationToken).ConfigureAwait(false);
            }

            var bytes = await ReadBoundedBytesAsync(
                path,
                _options.MaximumRunBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return Failed("artifact.oversize", relativePath, "Experiment run exceeds its bounded byte limit.");
            }
            JsonDocument document;
            try
            {
                if (bytes.Length >= 3
                    && bytes[0] == 0xEF
                    && bytes[1] == 0xBB
                    && bytes[2] == 0xBF)
                {
                    return Failed("artifact.corrupt", relativePath, "Experiment run is not strict UTF-8 JSON.");
                }

                document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            }
            catch (JsonException)
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

            if (!ArenaContractCodec.TryDeserialize<ArenaExperimentRunContract>(bytes, out var run, out var issues)
                || run is null)
            {
                return new(null,
                    [.. issues.Select(issue => ArenaExperimentPackCodec.Error(
                        $"artifact.contract.{issue.Code}",
                        relativePath,
                        $"Experiment run validation failed at {issue.Path}."))]);
            }

            var canonicalBytes = ArenaContractCodec.SerializeToUtf8Bytes(run);
            if (!bytes.AsSpan().SequenceEqual(canonicalBytes))
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

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var limit = (long)maximumBytes + 1;
        var useSingleBuffer = limit <= MaximumSinglePooledReadBytes;
        var bufferLength = useSingleBuffer
            ? (int)limit
            : (int)Math.Min(64 * 1024L, limit);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferLength);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (useSingleBuffer)
            {
                var total = 0;
                while (total < limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(
                        buffer.AsMemory(total, (int)limit - total),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return total > maximumBytes
                    ? null
                    : buffer.AsSpan(0, total).ToArray();
            }

            using var bytes = new MemoryStream(Math.Min(maximumBytes, bufferLength));
            long streamedTotal = 0;
            while (streamedTotal < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - streamedTotal)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                streamedTotal += read;
                if (streamedTotal > maximumBytes)
                {
                    return null;
                }

                bytes.Write(buffer, 0, read);
            }

            return bytes.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan());
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteRunAsync(
        string path,
        ArenaExperimentRunContract run,
        CancellationToken cancellationToken)
    {
        var bytes = ArenaContractCodec.SerializeToUtf8Bytes(run);
        await ArenaExperimentPackStore.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    private BoundedRunFiles SelectBoundedFiles(string directory, CancellationToken cancellationToken)
    {
        var limit = checked(_options.MaximumRuns + 1);
        var descendingOrdinal = Comparer<string>.Create(static (left, right) =>
            StringComparer.Ordinal.Compare(right, left));
        var selected = new PriorityQueue<string, string>(limit, descendingOrdinal);
        var filesConsidered = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesConsidered++;
            var name = Path.GetFileName(path);
            if (selected.Count < limit)
            {
                selected.Enqueue(path, name);
                continue;
            }

            selected.TryPeek(out _, out var largestSelectedName);
            if (StringComparer.Ordinal.Compare(name, largestSelectedName) < 0)
            {
                selected.Dequeue();
                selected.Enqueue(path, name);
            }
        }

        var paths = selected.UnorderedItems
            .Select(item => item.Element)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
        return new(paths, filesConsidered, filesConsidered > _options.MaximumRuns);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (prior == observed) return;
            observed = prior;
        }
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

    private sealed record BoundedRunFiles(
        string[] Paths,
        int FilesConsidered,
        bool ExceededLimit);

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
