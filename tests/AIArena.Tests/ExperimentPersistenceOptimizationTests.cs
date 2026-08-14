using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Services;

internal static class ExperimentPersistenceOptimizationTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private const string GoldenRun9001Json = "{\"attempts\":0,\"cellKey\":\"cell:a423071b39b0c39e60c2b2419269136689e560d584f88104c77531e95921752f\",\"createdAtUtc\":\"2026-08-14T12:00:00\\u002B00:00\",\"evidence\":[],\"experimentFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"experimentId\":\"experiment:optimization\",\"id\":\"run:09001\",\"interruptionReason\":null,\"repetition\":9001,\"schema\":\"ai_arena.experiment_run.v1\",\"state\":\"queued\",\"trialIds\":[],\"updatedAtUtc\":\"2026-08-14T12:00:00\\u002B00:00\",\"variantFingerprint\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}";
    private const string GoldenRun9001Sha256 = "5765a946efa8d182f9a24d05db85454daf6b8fbc828ca6344121d4186fa686c1";

    internal static void CanonicalUtf8FacadesPreserveBytesPrivacyAndAllocations()
    {
        var sizes = new[]
        {
            (TargetKiB: 1, Count: 1, SummaryLength: 400, AllocationLimit: 24_000L),
            (TargetKiB: 64, Count: 16, SummaryLength: 3_900, AllocationLimit: 600_000L),
            (TargetKiB: 512, Count: 132, SummaryLength: 3_900, AllocationLimit: 4_800_000L)
        };

        foreach (var size in sizes)
        {
            var run = SizedRun(size.TargetKiB, size.Count, size.SummaryLength);
            var legacyFacade = ArenaContractCodec.Serialize(run);
            var utf8 = ArenaContractCodec.SerializeToUtf8Bytes(run);
            Require(utf8.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(legacyFacade)),
                $"{size.TargetKiB} KiB UTF-8 facade changed canonical bytes");
            Require(ArenaContractCodec.ComputeSha256(run) == Convert.ToHexStringLower(SHA256.HashData(utf8)),
                $"{size.TargetKiB} KiB hash facade did not hash the canonical bytes");
            Require(ArenaContractCodec.TryDeserialize<ArenaExperimentRunContract>(utf8, out var copy, out var issues)
                    && copy is not null
                    && ArenaContractCodec.SerializeToUtf8Bytes(copy).AsSpan().SequenceEqual(utf8),
                $"{size.TargetKiB} KiB UTF-8 round trip changed canonical bytes: {Format(issues)}");

            _ = ArenaContractCodec.SerializeToUtf8Bytes(run);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            var measured = ArenaContractCodec.SerializeToUtf8Bytes(run);
            stopwatch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(measured.AsSpan().SequenceEqual(utf8), $"{size.TargetKiB} KiB measured bytes changed");
            Require(allocated < size.AllocationLimit,
                $"{size.TargetKiB} KiB canonical UTF-8 path allocated {allocated} bytes (limit {size.AllocationLimit})");
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT experiment_contract_utf8 target_kib={size.TargetKiB} actual_bytes={utf8.Length} " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} serialization_passes=1 canonical_passes=1");
        }

        var unsafeRun = Run(9000, [new(
            "evidence:unsafe",
            ArenaEvidenceState.Observed,
            "api_key=supersecretvalue",
            "artifact:test")]);
        RequireThrows<InvalidDataException>(() => ArenaContractCodec.SerializeToUtf8Bytes(unsafeRun),
            "UTF-8 facade bypassed privacy validation");
        RequireThrows<InvalidDataException>(() => ArenaContractCodec.ComputeSha256(unsafeRun),
            "hash facade bypassed privacy validation");

        var safeJson = ArenaContractCodec.Serialize(Run(9001, []));
        Require(string.Equals(safeJson, GoldenRun9001Json, StringComparison.Ordinal),
            "canonical experiment-run UTF-8 bytes drifted from the independent golden fixture");
        Require(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(safeJson))) == GoldenRun9001Sha256,
            "canonical experiment-run hash drifted from the independent golden fixture");
        var duplicate = safeJson.Replace(
            "\"schema\":\"ai_arena.experiment_run.v1\"",
            "\"schema\":\"ai_arena.experiment_run.v1\",\"schema\":\"ai_arena.experiment_run.v1\"",
            StringComparison.Ordinal);
        Require(!ArenaContractCodec.TryDeserialize<ArenaExperimentRunContract>(Encoding.UTF8.GetBytes(duplicate), out _, out var duplicateIssues)
                && duplicateIssues.Any(issue => issue.Code == "json.duplicate_member"),
            "UTF-8 decoder accepted duplicate members");
        var unknown = safeJson[..^1] + ",\"unknown\":true}";
        Require(!ArenaContractCodec.TryDeserialize<ArenaExperimentRunContract>(Encoding.UTF8.GetBytes(unknown), out _, out _),
            "UTF-8 decoder accepted an unknown member");
    }

    internal static void RunLoadingIsBoundedDeterministicAndCancellationSafe()
    {
        var root = TemporaryRoot();
        try
        {
            const int count = 1_000;
            var directory = Path.Combine(root, "experiment-runs");
            Directory.CreateDirectory(directory);
            for (var index = count - 1; index >= 0; index--)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, $"{index:D5}.json"),
                    ArenaContractCodec.SerializeToUtf8Bytes(Run(index, [])));
            }

            // Diagnostic order must remain independent of decode completion order.
            File.WriteAllText(Path.Combine(directory, "00010.json"), "not-json", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "00990.json"), "{}", new UTF8Encoding(false));
            var expectedFileCount = count;

            var single = new ExperimentRunStore(
                root,
                new(MaximumRuns: count, MaximumRunBytes: 16 * 1024, MaximumParallelDecodes: 1));
            var parallel = new ExperimentRunStore(
                root,
                new(MaximumRuns: count, MaximumRunBytes: 16 * 1024, MaximumParallelDecodes: 4));
            var baseline = single.LoadAllAsync(At).GetAwaiter().GetResult();
            var optimized = parallel.LoadAllAsync(At).GetAwaiter().GetResult();
            Require(baseline.Runs.SequenceEqual(optimized.Runs), "parallel decode changed run order or content");
            Require(baseline.Diagnostics.SequenceEqual(optimized.Diagnostics), "parallel decode changed diagnostics or ordering");
            Require(optimized.Runs.Select(run => run.CellKey).SequenceEqual(
                    optimized.Runs.Select(run => run.CellKey).OrderBy(value => value, StringComparer.Ordinal)),
                "loaded runs are no longer ordered by cell identity");
            Require(parallel.LastLoadMetrics.FilesConsidered == expectedFileCount
                    && parallel.LastLoadMetrics.FilesRead == expectedFileCount
                    && parallel.LastLoadMetrics.MaximumConcurrentDecodes == 4,
                $"load resource receipt was not bounded: {parallel.LastLoadMetrics}");

            _ = parallel.LoadAllAsync(At).GetAwaiter().GetResult();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var measured = parallel.LoadAllAsync(At).GetAwaiter().GetResult();
            stopwatch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            Require(measured.Runs.Length == count - 2, "measured load changed valid artifact count");
            Require(allocated < 34_000_000, $"1,000-artifact parallel load allocated {allocated} bytes");
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT experiment_run_load artifacts={expectedFileCount} valid={measured.Runs.Length} " +
                $"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocated} " +
                $"file_reads={parallel.LastLoadMetrics.FilesRead} max_concurrent_decodes={parallel.LastLoadMetrics.MaximumConcurrentDecodes}");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            RequireThrows<OperationCanceledException>(
                () => parallel.LoadAllAsync(At, cancelled.Token).GetAwaiter().GetResult(),
                "pre-cancelled parallel load did not cancel");

            using var inFlightCancellation = new CancellationTokenSource();
            using var decodeStarted = new CountdownEvent(4);
            using var releaseDecodes = new ManualResetEventSlim(false);
            var cancellingStore = new ExperimentRunStore(
                root,
                new(MaximumRuns: count, MaximumRunBytes: 16 * 1024, MaximumParallelDecodes: 4))
            {
                BeforeDecodeAsync = (_, _, token) => Task.Run(() =>
                {
                    decodeStarted.Signal();
                    releaseDecodes.Wait(token);
                }, token)
            };
            var cancellingLoad = cancellingStore.LoadAllAsync(At, inFlightCancellation.Token);
            Require(decodeStarted.Wait(TimeSpan.FromSeconds(5)), "parallel decoder did not reach its bounded start barrier");
            inFlightCancellation.Cancel();
            releaseDecodes.Set();
            RequireThrows<OperationCanceledException>(
                () => cancellingLoad.GetAwaiter().GetResult(),
                "in-flight parallel decode did not honor cancellation");
            Require(cancellingStore.LastLoadMetrics.FilesRead <= 4
                    && cancellingStore.LastLoadMetrics.MaximumConcurrentDecodes == 4,
                $"cancelled load started unbounded file work: {cancellingStore.LastLoadMetrics}");

            var growthRoot = Path.Combine(root, "growing-file");
            var growthDirectory = Path.Combine(growthRoot, "experiment-runs");
            Directory.CreateDirectory(growthDirectory);
            var growthPath = Path.Combine(growthDirectory, "growing.json");
            File.WriteAllBytes(growthPath, ArenaContractCodec.SerializeToUtf8Bytes(Run(42_000, [])));
            const int growthLimit = 16 * 1024;
            var growthStore = new ExperimentRunStore(
                growthRoot,
                new(MaximumRuns: 1, MaximumRunBytes: growthLimit, MaximumParallelDecodes: 1))
            {
                AfterInitialLengthCheckAsync = (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    append.Write(new byte[growthLimit]);
                    return Task.CompletedTask;
                }
            };
            var growthResult = growthStore.LoadAllAsync(At).GetAwaiter().GetResult();
            Require(growthResult.Runs.Length == 0
                    && growthResult.Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "a run growing beyond the byte cap after metadata inspection was fully read or decoded");

            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new ExperimentRunStore(root, new(MaximumParallelDecodes: 0)),
                "zero parallel decode bound was accepted");
            RequireThrows<ArgumentOutOfRangeException>(
                () => _ = new ExperimentRunStore(root, new(MaximumParallelDecodes: 33)),
                "unbounded parallel decode configuration was accepted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunLoadingPreservesRestartNormalizationAndResourceBounds()
    {
        var root = TemporaryRoot();
        try
        {
            const int count = 100;
            var directory = Path.Combine(root, "experiment-runs");
            Directory.CreateDirectory(directory);
            for (var index = 0; index < count; index++)
            {
                var running = Run(index, []) with
                {
                    State = ArenaExperimentRunState.Running,
                    Attempts = 1,
                    UpdatedAtUtc = At.AddSeconds(1),
                    TrialIds = [ArenaExperimentRunPolicy.CreateTrialId(
                        ArenaExperimentRunPolicy.CreateCellKey(HashA, HashB, index),
                        1)]
                };
                File.WriteAllBytes(
                    Path.Combine(directory, $"{index:D5}.json"),
                    ArenaContractCodec.SerializeToUtf8Bytes(running));
            }

            var store = new ExperimentRunStore(
                root,
                new(MaximumRuns: count, MaximumRunBytes: 16 * 1024, MaximumParallelDecodes: 4));
            var lease = store.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            ArenaExperimentRunLoadResult recovered;
            try
            {
                recovered = store.RecoverInterruptedAfterRestartAsync(lease, At.AddMinutes(1)).GetAwaiter().GetResult();
            }
            finally
            {
                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            Require(recovered.Runs.Length == count
                    && recovered.Runs.All(run => run.State == ArenaExperimentRunState.Interrupted)
                    && recovered.Diagnostics.Count(item => item.Code == "experiment_run.restart_normalized") == count,
                "parallel restart recovery did not normalize every running cell deterministically");
            Require(store.LastLoadMetrics.FilesRead == count
                    && store.LastLoadMetrics.MaximumConcurrentDecodes == 4,
                $"restart recovery decode resources were not bounded: {store.LastLoadMetrics}");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any()
                    && !Directory.EnumerateFiles(root, "*.lock", SearchOption.AllDirectories).Any(),
                "parallel restart recovery leaked a temporary file or lease");

            var passive = store.LoadAllAsync(At.AddMinutes(2)).GetAwaiter().GetResult();
            Require(passive.Runs.All(run => run.State == ArenaExperimentRunState.Interrupted)
                    && passive.Diagnostics.All(item => item.Code != "experiment_run.restart_normalized"),
                "restart normalization was not durable and one-shot");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArenaExperimentRunContract SizedRun(int repetition, int evidenceCount, int summaryLength) =>
        Run(repetition, Enumerable.Range(0, evidenceCount)
            .Select(index => new ArenaEvidenceAssertion(
                $"evidence:{index:D5}",
                ArenaEvidenceState.Observed,
                new string((char)('a' + index % 20), summaryLength),
                "artifact:test"))
            .ToImmutableArray());

    private static ArenaExperimentRunContract Run(
        int repetition,
        ImmutableArray<ArenaEvidenceAssertion> evidence)
    {
        var cellKey = ArenaExperimentRunPolicy.CreateCellKey(HashA, HashB, repetition);
        return new(
            ArenaContractSchemas.ExperimentRun,
            $"run:{repetition:D5}",
            At,
            "experiment:optimization",
            HashA,
            HashB,
            repetition,
            cellKey,
            ArenaExperimentRunState.Queued,
            0,
            At,
            [],
            null,
            evidence);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-experiment-optimization-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Format(ImmutableArray<ArenaContractValidationIssue> issues) =>
        string.Join(" | ", issues.Select(issue => $"{issue.Code}:{issue.Path}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
