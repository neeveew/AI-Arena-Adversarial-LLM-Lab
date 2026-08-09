using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ExperimentExecutionTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 8, 18, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    internal static void ExpandsCartesianMatrixDeterministically()
    {
        Require(ExperimentExpander.MaximumCells == ArenaExperimentRunStoreOptions.DefaultMaximumRuns,
            "matrix and default durable-store limits diverged");
        var experiment = Experiment(repetitions: 2, maximumParallelism: 3);
        var first = ExperimentExpander.Expand(experiment);
        var second = ExperimentExpander.Expand(experiment);

        Require(first.ExperimentFingerprint == second.ExperimentFingerprint, "experiment fingerprint changed across expansion");
        Require(first.Variants.Length == 4, "provider/dimension Cartesian variant count changed");
        Require(first.Cells.Length == 8, "repetition expansion count changed");
        Require(first.Variants.Select(Descriptor).SequenceEqual(
        [
            "provider:a|temperature=cold",
            "provider:a|temperature=warm",
            "provider:b|temperature=cold",
            "provider:b|temperature=warm"
        ]), "variants were not emitted in semantic ordinal order");
        Require(first.Cells.Select(item => item.CellKey).Distinct(StringComparer.Ordinal).Count() == first.Cells.Length,
            "cell keys were not unique");
        Require(first.Cells.All(item => item.CellKey == ArenaExperimentRunPolicy.CreateCellKey(
            first.ExperimentFingerprint, item.VariantFingerprint, item.Repetition)),
            "cell keys were not derived from stable fingerprints");
        Require(first.Cells.Select(item => item.RunId).SequenceEqual(second.Cells.Select(item => item.RunId)),
            "run IDs changed across expansion");
        Require(first.Cells.Select(CellDescriptor).SequenceEqual(second.Cells.Select(CellDescriptor)),
            "cell ordering changed across expansion");

        var presentationOnly = ExperimentExpander.Expand(experiment with
        {
            CreatedAtUtc = At.AddDays(1),
            Title = "Renamed matrix",
            Status = ArenaExperimentStatus.Completed,
            Evidence = [Observed("evidence:renamed")]
        });
        Require(first.ExperimentFingerprint == presentationOnly.ExperimentFingerprint,
            "presentation or receipt metadata changed semantic experiment identity");
        Require(first.Cells.Select(item => item.CellKey).SequenceEqual(presentationOnly.Cells.Select(item => item.CellKey)),
            "presentation metadata changed cell identities");
        var behaviorChange = ExperimentExpander.Expand(experiment with { TurnBudget = experiment.TurnBudget + 1 });
        Require(first.ExperimentFingerprint != behaviorChange.ExperimentFingerprint,
            "behavior-bearing experiment input did not change semantic identity");

        var values = Enumerable.Range(0, 64)
            .Select(index => $"value-{index:D2}")
            .ToImmutableArray();
        var overCapacity = experiment with
        {
            Dimensions =
            [
                new("dimension:axis-a", "axis_a", values),
                new("dimension:axis-b", "axis_b", values)
            ],
            Repetitions = 2
        };
        Require(ArenaContractCodec.Validate(overCapacity).IsValid,
            "over-capacity regression fixture is not otherwise a legal experiment");
        var rejected = false;
        try
        {
            _ = ExperimentExpander.Expand(overCapacity);
        }
        catch (InvalidDataException exception)
        {
            rejected = exception.Message.Contains(ExperimentExpander.MaximumCells.ToString(), StringComparison.Ordinal);
        }
        Require(rejected, "matrix larger than the compatible durable ceiling was expanded");
    }

    internal static void StoresStrictVersionedPacksAndReportsFallbacks()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ArenaExperimentPackStore(root, maximumPackBytes: 16 * 1024);
            var pack = ScenarioPack();
            var written = store.SaveScenarioPackAsync(pack).GetAwaiter().GetResult();
            Require(written.Disposition == ArenaArtifactWriteDisposition.Written, Format(written.Diagnostics));
            Require(ArenaContractPrivacyRules.IsSafeRelativePath(written.RelativePath), "store exposed a non-relative path");

            var duplicate = store.SaveScenarioPackAsync(pack).GetAwaiter().GetResult();
            Require(duplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate, "exact pack save was not deduplicated");
            var contentDuplicate = store.SaveScenarioPackAsync(pack with
            {
                Id = "scenario-pack:content-copy",
                Name = "Content copy",
                Version = "2.0.0"
            }).GetAwaiter().GetResult();
            Require(contentDuplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate, "content identity duplicate was stored twice");
            Require(contentDuplicate.Diagnostics.Any(item => item.Code == "artifact.duplicate_content"), "content duplicate lacked a diagnostic");

            var conflict = store.SaveScenarioPackAsync(pack with { Name = "Conflicting payload" }).GetAwaiter().GetResult();
            Require(conflict.Disposition == ArenaArtifactWriteDisposition.Rejected, "different payload reused a pack ID");
            Require(conflict.Diagnostics.Any(item => item.Code == "artifact.duplicate_id"), "ID conflict lacked a diagnostic");

            var canonical = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(pack));
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(canonical, "imports/pack.json").Succeeded,
                "canonical pack did not decode");
            var tamperedContent = pack with
            {
                Scenarios = [pack.Scenarios[0] with { ScenarioSeed = "Changed without refreshing identity." }]
            };
            Require(ArenaContractCodec.Validate(tamperedContent).Issues.Any(item => item.Code == "scenario.content_fingerprint"),
                "caller-supplied scenario content fingerprint was trusted after content changed");
            var formatted = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(pack, indented: true));
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(formatted, "imports/formatted.json")
                .Diagnostics.Any(item => item.Code == "artifact.canonical_required"),
                "non-canonical encoding was accepted by the strict pack codec");
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                Encoding.UTF8.GetBytes("{}"), "imports/missing.json").Diagnostics.Any(item => item.Code == "artifact.schema_missing"),
                "missing schema lacked an explicit diagnostic");
            var oldSchema = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(pack)
                .Replace(ArenaContractSchemas.ScenarioPack, "ai_arena.scenario_pack.v0", StringComparison.Ordinal));
            var migration = ArenaExperimentPackCodec.DecodeScenarioPack(oldSchema, "imports/old.json");
            Require(migration.Diagnostics.Any(item => item.Code == "artifact.migration_required"
                && item.DetectedSchema == "ai_arena.scenario_pack.v0"
                && item.ExpectedSchema == ArenaContractSchemas.ScenarioPack),
                "recognized older schema did not require an explicit migration");
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                Encoding.UTF8.GetBytes("{broken"), "imports/corrupt.json").Diagnostics.Any(item => item.Code == "artifact.corrupt"),
                "corrupt pack lacked fallback diagnostics");
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                new byte[32], "imports/large.json", maximumBytes: 16).Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "oversize pack lacked fallback diagnostics");

            var migratedBase = ScenarioPack() with
            {
                Id = "scenario-pack:migrated",
                Name = "Migrated",
                Version = "2.0.0",
                Scenarios = [ScenarioPack().Scenarios[0] with { ScenarioSeed = "Migrated bounded seed." }],
                Migration = new("ai_arena.scenario_pack.v0", "0.9.0", HashA, "migrator:1", At)
            };
            var migrated = WithScenarioContentFingerprint(migratedBase);
            var migratedWrite = store.SaveScenarioPackAsync(migrated).GetAwaiter().GetResult();
            Require(migratedWrite.Succeeded, Format(migratedWrite.Diagnostics));
            var loaded = store.LoadScenarioPacksAsync().GetAwaiter().GetResult();
            Require(loaded.Artifacts.Length == 2, "valid packs were not isolated from duplicate inputs");
            Require(loaded.Diagnostics.Any(item => item.Code == "artifact.migration_provenance"),
                "stored migration provenance was not surfaced");

            var benchmark = BenchmarkPack();
            var benchmarkWrite = store.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult();
            Require(benchmarkWrite.Disposition == ArenaArtifactWriteDisposition.Written, Format(benchmarkWrite.Diagnostics));
            Require(store.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Duplicate,
                "exact benchmark save was not deduplicated");
            var benchmarkContentDuplicate = store.SaveBenchmarkPackAsync(benchmark with
            {
                Id = "benchmark-pack:content-copy",
                Name = "Renamed benchmark",
                Version = "2.0.0"
            }).GetAwaiter().GetResult();
            Require(benchmarkContentDuplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate,
                "benchmark content duplicate was stored twice");
            Require(benchmarkContentDuplicate.Diagnostics.Any(item => item.Code == "artifact.duplicate_content"),
                "benchmark content duplicate lacked a diagnostic");
            var tamperedBenchmark = benchmark with
            {
                Cases = [benchmark.Cases[0] with { Repetitions = benchmark.Cases[0].Repetitions + 1 }]
            };
            Require(ArenaContractCodec.Validate(tamperedBenchmark).Issues.Any(item => item.Code == "benchmark.content_fingerprint"),
                "caller-supplied benchmark content fingerprint was trusted after content changed");
            var benchmarkLoad = store.LoadBenchmarkPacksAsync().GetAwaiter().GetResult();
            Require(benchmarkLoad.Artifacts.Single().Id == benchmark.Id, "benchmark pack did not round trip through its strict store");
            var oldBenchmark = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(benchmark)
                .Replace(ArenaContractSchemas.BenchmarkPack, "ai_arena.benchmark_pack.v0", StringComparison.Ordinal));
            Require(ArenaExperimentPackCodec.DecodeBenchmarkPack(oldBenchmark, "imports/benchmark-old.json")
                .Diagnostics.Any(item => item.Code == "artifact.migration_required"),
                "older benchmark schema lacked explicit migration diagnostics");

            var secretBase = ScenarioPack() with
            {
                Id = "scenario-pack:secret",
                Name = "Secret",
                Version = "3.0.0",
                Scenarios = [ScenarioPack().Scenarios[0] with { ScenarioSeed = "api_key=supersecretvalue" }]
            };
            var secret = WithScenarioContentFingerprint(secretBase);
            var secretWrite = store.SaveScenarioPackAsync(secret).GetAwaiter().GetResult();
            Require(secretWrite.Disposition == ArenaArtifactWriteDisposition.Rejected, "secret-shaped pack content was persisted");
            var persisted = string.Join("\n", Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
            Require(!persisted.Contains(root, StringComparison.OrdinalIgnoreCase), "absolute store root leaked into pack JSON");
            Require(!persisted.Contains("supersecretvalue", StringComparison.Ordinal), "secret-shaped value leaked into pack JSON");

            var concurrentRoot = Path.Combine(root, "concurrent-store");
            var firstStore = new ArenaExperimentPackStore(concurrentRoot);
            var secondStore = new ArenaExperimentPackStore(concurrentRoot);
            var firstCandidate = ScenarioPack() with { Id = "scenario-pack:concurrent" };
            firstCandidate = WithScenarioContentFingerprint(firstCandidate);
            var secondCandidate = WithScenarioContentFingerprint(firstCandidate with
            {
                Scenarios =
                [
                    firstCandidate.Scenarios[0] with
                    {
                        ScenarioSeed = "A conflicting bounded seed written by the second store."
                    }
                ]
            });
            var concurrentWrites = Task.WhenAll(
                firstStore.SaveScenarioPackAsync(firstCandidate),
                secondStore.SaveScenarioPackAsync(secondCandidate)).GetAwaiter().GetResult();
            Require(concurrentWrites.Count(result => result.Disposition == ArenaArtifactWriteDisposition.Written) == 1,
                "cross-instance pack writers both claimed to create the same ID");
            Require(concurrentWrites.Count(result => result.Disposition == ArenaArtifactWriteDisposition.Rejected) == 1,
                "cross-instance conflicting pack write was not rejected");
            Require(concurrentWrites.Single(result => result.Disposition == ArenaArtifactWriteDisposition.Rejected)
                    .Diagnostics.Any(item => item.Code == "artifact.duplicate_id"),
                "cross-instance pack conflict lacked its duplicate-ID diagnostic");
            var concurrentLoad = firstStore.LoadScenarioPacksAsync().GetAwaiter().GetResult();
            var persistedWinner = concurrentLoad.Artifacts.Single();
            var reportedWinner = concurrentWrites.Single(result => result.Disposition == ArenaArtifactWriteDisposition.Written).Artifact!;
            Require(ArenaContractCodec.Serialize(persistedWinner) == ArenaContractCodec.Serialize(reportedWinner),
                "cross-instance pack serialization did not preserve the reported winner");
            Require(!Directory.EnumerateFiles(concurrentRoot, "*.tmp", SearchOption.AllDirectories).Any(),
                "cross-instance pack write left a temporary file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void PersistsRunsAtomicallyAndNormalizesRestart()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ExperimentRunStore(root, new(MaximumRuns: 16, MaximumRunBytes: 16 * 1024));
            var cell = ExperimentExpander.Expand(Experiment(repetitions: 1, maximumParallelism: 1)).Cells[0];
            var running = Run(cell, ArenaExperimentRunState.Running, 1, ["trial:one"], At);
            var first = store.SaveAsync(running).GetAwaiter().GetResult();
            Require(first.Disposition == ArenaArtifactWriteDisposition.Written, Format(first.Diagnostics));
            var duplicate = store.SaveAsync(running).GetAwaiter().GetResult();
            Require(duplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate, "byte-identical run save rewrote the cell");

            var completed = Run(cell, ArenaExperimentRunState.Completed, 2, ["trial:two"], At.AddSeconds(1));
            var merged = store.SaveAsync(completed).GetAwaiter().GetResult();
            Require(merged.Succeeded && merged.Artifact is not null, Format(merged.Diagnostics));
            var mergedArtifact = merged.Artifact ?? throw new InvalidOperationException("merged artifact missing");
            Require(mergedArtifact.TrialIds.SequenceEqual(["trial:one", "trial:two"]), "retry discarded an earlier trial reference");
            Require(mergedArtifact.Attempts == 2 && mergedArtifact.State == ArenaExperimentRunState.Completed,
                "newer repeat trial did not advance the cell");
            var json = File.ReadAllText(Path.Combine(root, merged.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Require(!json.Contains(root, StringComparison.OrdinalIgnoreCase), "absolute data root leaked into a run artifact");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "atomic write left a temporary artifact");

            var concurrencyRoot = TemporaryRoot();
            try
            {
                var firstStore = new ExperimentRunStore(concurrencyRoot);
                var secondStore = new ExperimentRunStore(concurrencyRoot);
                var firstCandidate = Run(cell, ArenaExperimentRunState.Completed, 1, ["trial:concurrent-a"], At.AddMinutes(2));
                var secondCandidate = Run(cell, ArenaExperimentRunState.Running, 1, ["trial:concurrent-b"], At.AddMinutes(2));
                var writes = RunConcurrently(
                    () => firstStore.SaveAsync(firstCandidate),
                    () => secondStore.SaveAsync(secondCandidate));
                Require(writes.All(item => item.Disposition == ArenaArtifactWriteDisposition.Written),
                    "compatible concurrent run branches were not both merged successfully");
                var persisted = firstStore.LoadAllAsync(At.AddMinutes(3)).GetAwaiter().GetResult().Runs.Single();
                Require(persisted.State == ArenaExperimentRunState.Completed,
                    "concurrent direct save regressed a terminal run to running");
                Require(persisted.TrialIds.SequenceEqual(["trial:concurrent-a", "trial:concurrent-b"]),
                    "concurrent direct save lost a trial branch");

                var conflictingEvidence = persisted with
                {
                    UpdatedAtUtc = persisted.UpdatedAtUtc.AddSeconds(1),
                    Evidence = [new("evidence:run", ArenaEvidenceState.Observed, "A conflicting observation.", "artifact:test")]
                };
                var conflict = secondStore.SaveAsync(conflictingEvidence).GetAwaiter().GetResult();
                Require(conflict.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && conflict.Diagnostics.Any(item => item.Code == "experiment_run.evidence_conflict"),
                    "run persistence silently rewrote an existing evidence identity");
                var unchanged = firstStore.LoadAllAsync(At.AddMinutes(3)).GetAwaiter().GetResult().Runs.Single();
                Require(ArenaContractCodec.Serialize(unchanged) == ArenaContractCodec.Serialize(persisted),
                    "rejected run evidence conflict changed the persisted cell");
                Require(!Directory.EnumerateFiles(concurrencyRoot, "*.tmp", SearchOption.AllDirectories).Any()
                        && !Directory.EnumerateFiles(concurrencyRoot, "*.lock", SearchOption.AllDirectories).Any(),
                    "cross-instance run persistence left lease or temporary files");
            }
            finally
            {
                Directory.Delete(concurrencyRoot, recursive: true);
            }

            var restartRoot = TemporaryRoot();
            try
            {
                var restartStore = new ExperimentRunStore(restartRoot);
                Require(restartStore.SaveAsync(running).GetAwaiter().GetResult().Succeeded, "running restart fixture was not saved");
                var passive = restartStore.LoadAllAsync(At.AddMinutes(1)).GetAwaiter().GetResult();
                Require(passive.Runs.Single().State == ArenaExperimentRunState.Running,
                    "a passive history read mutated a potentially live run");
                var restartLease = restartStore.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                ArenaExperimentRunLoadResult restarted;
                try
                {
                    restarted = restartStore.RecoverInterruptedAfterRestartAsync(restartLease, At.AddMinutes(1)).GetAwaiter().GetResult();
                }
                finally
                {
                    restartLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                Require(restarted.Runs.Single().State == ArenaExperimentRunState.Interrupted, "running cell was not interrupted on restart");
                Require(restarted.Runs.Single().InterruptionReason == ArenaExperimentRunPolicy.ProcessRestartReason,
                    "restart reason was not persisted");
                Require(restarted.Diagnostics.Any(item => item.Code == "experiment_run.restart_normalized"),
                    "restart normalization lacked evidence");
                var reread = restartStore.LoadAllAsync(At.AddMinutes(2)).GetAwaiter().GetResult();
                Require(!reread.Diagnostics.Any(item => item.Code == "experiment_run.restart_normalized"),
                    "terminal restart state was normalized repeatedly");

                var staleRunning = restarted.Runs.Single() with
                {
                    State = ArenaExperimentRunState.Running,
                    InterruptionReason = null
                };
                var staleWrite = restartStore.SaveAsync(staleRunning).GetAwaiter().GetResult();
                Require(staleWrite.Artifact?.State == ArenaExperimentRunState.Interrupted,
                    "equal-time stale running state regressed a terminal run");
            }
            finally
            {
                Directory.Delete(restartRoot, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunStoreIsolatesCorruptOversizeAndPrivateArtifacts()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ExperimentRunStore(root, new(MaximumRuns: 16, MaximumRunBytes: 2 * 1024));
            var directory = Path.Combine(root, "experiment-runs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "corrupt.json"), "{broken");
            File.WriteAllBytes(Path.Combine(directory, "oversize.json"), new byte[2 * 1024 + 1]);
            File.WriteAllText(Path.Combine(directory, "old.json"), "{\"schema\":\"ai_arena.experiment_run.v0\"}");

            var loaded = store.LoadAllAsync(At).GetAwaiter().GetResult();
            var codes = loaded.Diagnostics.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            Require(codes.Contains("artifact.corrupt"), "corrupt run did not fall back to diagnostics");
            Require(codes.Contains("artifact.oversize"), "oversize run did not fall back to diagnostics");
            Require(codes.Contains("artifact.migration_required"), "old run schema did not require migration");
            Require(loaded.Runs.IsEmpty, "invalid runs were invented during fallback");

            var cell = ExperimentExpander.Expand(Experiment(1, 1)).Cells[0];
            var unsafeRun = Run(cell, ArenaExperimentRunState.Completed, 1, ["trial:one"], At) with
            {
                Evidence = [new("evidence:unsafe", ArenaEvidenceState.Observed, "api_key=supersecretvalue", "trial:one")]
            };
            var rejected = store.SaveAsync(unsafeRun).GetAwaiter().GetResult();
            Require(rejected.Disposition == ArenaArtifactWriteDisposition.Rejected, "private run evidence was persisted");
            Require(!Directory.EnumerateFiles(directory, "*.json").Select(File.ReadAllText)
                .Any(text => text.Contains("supersecretvalue", StringComparison.Ordinal)),
                "secret-shaped run evidence reached disk");
            Require(rejected.Diagnostics.All(item => ArenaContractPrivacyRules.IsSafeRelativePath(item.RelativePath)),
                "diagnostic exposed an unsafe path");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunnerBoundsConcurrencyAndResumesApprovedCells()
    {
        var root = TemporaryRoot();
        try
        {
            var experiment = Experiment(repetitions: 1, maximumParallelism: 3);
            var expansion = ExperimentExpander.Expand(experiment);
            var executor = new ObservingExecutor(TimeSpan.FromMilliseconds(40));
            var runner = new ExperimentRunnerService(executor);
            var store = new ExperimentRunStore(root);
            var options = new ArenaExperimentRunnerOptions(
                MaximumParallelism: 3,
                ProviderConcurrencyBudgets: new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["provider:a"] = 1,
                    ["provider:b"] = 1
                });
            var first = runner.RunAsync(experiment, expansion, store, options).GetAwaiter().GetResult();
            Require(first.StartedCells == expansion.Cells.Length, "missing cells were not executed");
            Require(first.Runs.All(item => item.State == ArenaExperimentRunState.Completed), "cells did not complete");
            Require(executor.MaximumActive <= 2, "global/provider concurrency budget was exceeded");
            Require(executor.MaximumByProvider.Values.All(value => value <= 1), "per-provider concurrency budget was exceeded");

            var resumed = runner.RunAsync(experiment, expansion, store, options).GetAwaiter().GetResult();
            Require(resumed.EligibleCells == 0 && resumed.StartedCells == 0, "completed cells reran without approval");

            var retryKey = expansion.Cells[0].CellKey;
            var retryOptions = options with { RetryApproved = run => run.CellKey == retryKey };
            var retried = runner.RunAsync(experiment, expansion, store, retryOptions).GetAwaiter().GetResult();
            Require(retried.StartedCells == 1, "approved retry did not run exactly one cell");
            var retriedCell = retried.Runs.Single(item => item.CellKey == retryKey);
            Require(retriedCell.Attempts == 2 && retriedCell.TrialIds.Length == 2,
                "approved retry did not retain both trial references");

            var lease = store.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                var competingStore = new ExperimentRunStore(root);
                var rejected = false;
                try
                {
                    _ = competingStore.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
                Require(rejected, "a second store acquired a concurrent execution lease");
            }
            finally
            {
                lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunnerPreflightsBoundedDurableCapacity()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ExperimentRunStore(root, new(MaximumRuns: 2, MaximumRunBytes: 16 * 1024));
            var occupiedExperiment = CapacityExperiment("experiment:capacity-occupied", "occupied");
            var occupiedCell = ExperimentExpander.Expand(occupiedExperiment).Cells.Single();
            var occupiedWrite = store.SaveAsync(
                    Run(occupiedCell, ArenaExperimentRunState.Completed, 1, ["trial:occupied"], At))
                .GetAwaiter().GetResult();
            Require(occupiedWrite.Succeeded, Format(occupiedWrite.Diagnostics));

            var executor = new ObservingExecutor(TimeSpan.Zero);
            var runner = new ExperimentRunnerService(executor);
            var insufficientExperiment = CapacityExperiment("experiment:capacity-insufficient", "near-a") with
            {
                Dimensions = [new("dimension:capacity", "capacity_axis", ["near-a", "near-b"])]
            };
            var insufficientExpansion = ExperimentExpander.Expand(insufficientExperiment);
            var insufficient = runner.RunAsync(
                    insufficientExperiment,
                    insufficientExpansion,
                    store,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1))
                .GetAwaiter().GetResult();
            Require(insufficient.PlannedCells == 2
                    && insufficient.EligibleCells == 2
                    && insufficient.StartedCells == 0,
                "near-capacity store partially scheduled a matrix larger than its remaining capacity");
            Require(insufficient.Diagnostics.Count(item => item.Code == "experiment_run.capacity_insufficient") == 1,
                "near-capacity preflight did not collapse failure into one diagnostic");
            Require(Directory.EnumerateFiles(Path.Combine(root, "experiment-runs"), "*.json").Count() == 1,
                "failed all-or-nothing preflight consumed the final slot");

            var nearCapacityExperiment = CapacityExperiment("experiment:capacity-near", "near");
            var nearCapacityExpansion = ExperimentExpander.Expand(nearCapacityExperiment);
            var nearCapacity = runner.RunAsync(
                    nearCapacityExperiment,
                    nearCapacityExpansion,
                    store,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1))
                .GetAwaiter().GetResult();
            Require(nearCapacity.StartedCells == 1
                    && nearCapacity.Runs.Length == 2
                    && nearCapacity.Runs.Any(run => run.CellKey == nearCapacityExpansion.Cells[0].CellKey
                        && run.State == ArenaExperimentRunState.Completed),
                "the final available durable slot was not usable");
            Require(!nearCapacity.Diagnostics.Any(item => item.Code == "experiment_run.capacity_insufficient"),
                "near-capacity run reported a false capacity failure");

            var fullExperiment = CapacityExperiment("experiment:capacity-full", "full");
            var fullExpansion = ExperimentExpander.Expand(fullExperiment);
            var full = runner.RunAsync(
                    fullExperiment,
                    fullExpansion,
                    store,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1))
                .GetAwaiter().GetResult();
            Require(full.PlannedCells == 1 && full.EligibleCells == 1 && full.StartedCells == 0,
                "full store scheduled a cell that could not be durably retained");
            Require(full.Diagnostics.Count(item => item.Code == "experiment_run.capacity_insufficient") == 1,
                "full-store preflight did not produce exactly one bounded diagnostic");
            Require(full.Diagnostics.Single(item => item.Code == "experiment_run.capacity_insufficient").Message.Contains(
                    "requires 1 new durable run artifact(s)", StringComparison.Ordinal),
                "full-store diagnostic did not explain the required durable capacity");
            Require(Directory.EnumerateFiles(Path.Combine(root, "experiment-runs"), "*.json").Count() == 2,
                "capacity rejection changed the durable store");

            var retry = runner.RunAsync(
                    nearCapacityExperiment,
                    nearCapacityExpansion,
                    store,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1, RetryApproved: _ => true))
                .GetAwaiter().GetResult();
            Require(retry.StartedCells == 1
                    && retry.Runs.Single(run => run.CellKey == nearCapacityExpansion.Cells[0].CellKey).Attempts == 2,
                "full store blocked an approved retry that needed no new durable slot");
            Require(!retry.Diagnostics.Any(item => item.Code == "experiment_run.capacity_insufficient"),
                "existing-cell retry consumed a new capacity slot");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RunnerCancellationPersistsStartedCellsSafely()
    {
        var root = TemporaryRoot();
        try
        {
            var experiment = Experiment(repetitions: 3, maximumParallelism: 2);
            var expansion = ExperimentExpander.Expand(experiment);
            var executor = new CancellationExecutor();
            var runner = new ExperimentRunnerService(executor);
            var store = new ExperimentRunStore(root);
            using var cancellation = new CancellationTokenSource();
            var task = runner.RunAsync(
                experiment,
                expansion,
                store,
                new ArenaExperimentRunnerOptions(MaximumParallelism: 2),
                cancellation.Token);
            executor.Started.Task.Wait(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var result = task.GetAwaiter().GetResult();

            Require(result.WasCancelled, "runner did not report caller cancellation");
            Require(result.StartedCells is > 0 and <= 2, "cancellation ignored bounded in-flight work");
            Require(result.Runs.Length == result.StartedCells, "runner invented records for cells that never started");
            Require(result.Runs.All(item => item.State == ArenaExperimentRunState.Cancelled),
                "started cells were not durably cancelled");
            Require(result.Runs.All(item => item.TrialIds.Length == 1), "cancelled cells lost or duplicated trial identity");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void ResolvesLiveExecutionPlansStrictly()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = CreateLiveFixture(root);
            var resolver = new ArenaExperimentExecutionResolver(fixture.PackStore, fixture.SessionStore, fixture.Profiles, rubricStore: fixture.RubricStore);
            var experiment = LiveExperiment("experiment:live-plan", repetitions: 2, turnBudget: 3);
            var generationOnly = new ArenaExperimentExecutionResolver(fixture.PackStore, fixture.SessionStore, fixture.Profiles)
                .ResolveAsync(experiment, "scenario:live").GetAwaiter().GetResult();
            Require(!generationOnly.IsAvailable
                    && generationOnly.Diagnostics.Any(item => item.Code == "rubric_store_unavailable"),
                "generation was labeled executable while its post-run rubric requirements were unresolved");
            var resolved = resolver.ResolveAsync(experiment, "scenario:live").GetAwaiter().GetResult();
            Require(resolved.IsAvailable && resolved.Plan is not null, ExecutionFormat(resolved.Diagnostics));
            Require(resolved.Plan!.TurnBudget == 3, "resolved plan lost the configured bounded turn budget");
            Require(resolved.Plan.RubricIds.SequenceEqual(["rubric:quality"]), "rubric requirements were silently discarded");
            Require(resolved.Plan.ResolvedRubrics is [{ Id: "rubric:quality", Version: "1.0.0", ContentFingerprint.Length: 64 }],
                "exact immutable rubric content was not bound into the execution plan");
            Require(resolved.Diagnostics.Any(item => item.Code == "rubric_evaluation_required"),
                "post-run rubric evaluation requirement was not made explicit");
            Require(resolved.Plan.ProviderProfileIds.SequenceEqual(["provider:live"]), "provider identity changed during resolution");
            Require(resolved.Plan.PlanFingerprint.Length == 64, "execution plan lacks a stable fingerprint");

            var autoSelected = resolver.ResolveAsync(experiment).GetAwaiter().GetResult();
            Require(autoSelected.IsAvailable && autoSelected.Plan?.ScenarioId == "scenario:live",
                "single-scenario pack did not resolve deterministically");
            Require(!resolver.ResolveSessionSourceAsync("match-setup:v2").GetAwaiter().GetResult().IsAvailable,
                "legacy non-session match setup reference was accepted for live execution");

            var unsupported = experiment with
            {
                Id = "experiment:unsupported-axis",
                Dimensions = [new("dimension:top-p", "top_p", ["0.9"])]
            };
            var unsupportedResult = resolver.ResolveAsync(unsupported).GetAwaiter().GetResult();
            Require(!unsupportedResult.IsAvailable
                    && unsupportedResult.Diagnostics.Any(item => item.Code == "experiment_dimension_unsupported"),
                "unknown behavior axis did not block execution before provider calls");

            var invalid = experiment with
            {
                Id = "experiment:invalid-axis",
                Dimensions = [new("dimension:temperature", "temperature", ["NaN"])]
            };
            var invalidResult = resolver.ResolveAsync(invalid).GetAwaiter().GetResult();
            Require(!invalidResult.IsAvailable
                    && invalidResult.Diagnostics.Any(item => item.Code == "experiment_temperature_invalid"),
                "non-finite behavior value did not block execution");

            var missingProfile = experiment with
            {
                Id = "experiment:missing-profile",
                ProviderProfileIds = ["provider:missing"]
            };
            Require(!resolver.ResolveAsync(missingProfile).GetAwaiter().GetResult().IsAvailable,
                "missing process-memory provider profile was treated as available");

            var invalidModeRegistry = new ArenaExperimentProviderProfileRegistry(
                new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal)
                {
                    ["provider:live"] = LiveProvider(apiMode: "future_protocol")
                });
            var invalidModeResolver = new ArenaExperimentExecutionResolver(
                fixture.PackStore,
                fixture.SessionStore,
                invalidModeRegistry,
                rubricStore: fixture.RubricStore);
            var invalidMode = invalidModeResolver.ResolveAsync(experiment).GetAwaiter().GetResult();
            Require(!invalidMode.IsAvailable
                    && invalidMode.Diagnostics.Any(item => item.Code == "provider_profile_api_mode_invalid"),
                "unknown provider protocol silently normalized into a live experiment");

            var faultExperiment = experiment with
            {
                Id = "experiment:unbound-fault",
                FaultProfileIds = ["fault:empty"]
            };
            var faultUnavailable = resolver.ResolveAsync(faultExperiment).GetAwaiter().GetResult();
            Require(!faultUnavailable.IsAvailable
                    && faultUnavailable.Diagnostics.Any(item => item.Code == "fault_profile_unavailable"),
                "declared fault behavior was silently ignored");

            var benchmark = LiveBenchmark(fixture.ScenarioPack, requiredCapabilities: ["chat"]);
            Require(fixture.PackStore.SaveBenchmarkPackAsync(benchmark).GetAwaiter().GetResult().Succeeded,
                "benchmark requirement fixture was not saved");
            var benchmarkExperiment = experiment with
            {
                Id = "experiment:benchmark-capability",
                BenchmarkPackId = benchmark.Id
            };
            var benchmarkUnavailable = resolver.ResolveAsync(benchmarkExperiment).GetAwaiter().GetResult();
            Require(!benchmarkUnavailable.IsAvailable
                    && benchmarkUnavailable.Diagnostics.Any(item => item.Code == "benchmark_provider_capability_unavailable"),
                "unproven benchmark provider capability was silently accepted");

            var rubricDirectory = Path.Combine(root, "evaluation", "rubrics");
            var canonicalRubricPath = Directory.EnumerateFiles(rubricDirectory, "*.json", SearchOption.TopDirectoryOnly).Single();
            File.Copy(canonicalRubricPath, Path.Combine(rubricDirectory, "duplicate-rubric.json"));
            var ambiguousRubric = resolver.ResolveAsync(experiment).GetAwaiter().GetResult();
            Require(!ambiguousRubric.IsAvailable
                    && ambiguousRubric.Diagnostics.Any(item => item.Code == "rubric_store_ambiguous"),
                "duplicate immutable rubric identity bound whichever filename sorted first");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void ExecutesLiveCellsThroughIsolatedSessions()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = CreateLiveFixture(root);
            var resolver = new ArenaExperimentExecutionResolver(fixture.PackStore, fixture.SessionStore, fixture.Profiles, rubricStore: fixture.RubricStore);
            var experiment = LiveExperiment("experiment:live-run", repetitions: 2, turnBudget: 2);
            var resolution = resolver.ResolveAsync(experiment).GetAwaiter().GetResult();
            Require(resolution.IsAvailable && resolution.Plan is not null, ExecutionFormat(resolution.Diagnostics));
            var provider = new RecordingExperimentProviderClient();
            var executor = new ArenaExperimentCellExecutor(
                resolution.Plan!,
                fixture.Profiles,
                fixture.SessionStore,
                provider);
            var runner = new ExperimentRunnerService(executor);
            var runStoreRoot = Path.Combine(root, "live-runs");
            var runStore = new ExperimentRunStore(runStoreRoot);
            var first = runner.RunAsync(
                experiment,
                resolution.Plan!.Expansion,
                runStore,
                new ArenaExperimentRunnerOptions(MaximumParallelism: 1)).GetAwaiter().GetResult();

            Require(first.StartedCells == 2 && first.Runs.Length == 2, "repeat trials were not retained as separate cells");
            Require(first.Runs.All(item => item.State == ArenaExperimentRunState.Completed),
                "real session-backed cells did not complete");
            Require(provider.Configs.Count == 4, "configured turn budgets did not reach the provider runtime");
            Require(provider.Configs.All(config => config.ApiToken == LiveCredential),
                "process-memory credential was not injected only at the provider boundary");
            Require(provider.Configs.All(config =>
                    config.RequestInspectionContext is not null
                    && config.RequestInspectionContext.CorrelationId.Length > 0
                    && config.RequestInspectionContext.Phase == "primary"
                    && config.RequestInspectionContext.Explanations.Count > 0),
                "experiment credential injection discarded prompt-inspection correlation or context evidence");
            Require(provider.Configs.All(config =>
                    Math.Abs(config.Temperature - 0.25) < 0.0001
                    && config.MaxOutputTokens == 64
                    && config.ContextLength == 4096
                    && config.Reasoning == "low"
                    && config.Timeout == 5),
                "strict behavior dimensions were not applied to the selected provider profile");
            Require(first.Runs.All(run => run.Evidence.Any(item =>
                    item.Summary.Contains("Adapter-exposed aggregate telemetry", StringComparison.Ordinal))),
                "adapter telemetry provenance was not stated conservatively");

            var source = fixture.SessionStore.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            Require(source.Engine.Messages.Count == 0 && source.Configs.ContainsKey("alpha"),
                "live execution mutated the source session or its role override");
            var sessions = fixture.SessionStore.ListSessionsAsync(SessionListingDetail.Messages).GetAwaiter().GetResult();
            var children = sessions.Where(item => item.Id != "source").ToArray();
            Require(children.Length == 2, "repeat trials did not retain one isolated child each");
            foreach (var summary in children)
            {
                var child = fixture.SessionStore.LoadSnapshotAsync(summary.Id).GetAwaiter().GetResult()!;
                Require(child.Engine.Messages.Count == 2, "child did not retain the configured real turn output");
                Require(child.Configs.Keys.SequenceEqual([ModelProviderRouting.SharedConfigKey]),
                    "per-agent routing overrides survived in an experiment child");
                Require(child.Configs[ModelProviderRouting.SharedConfigKey].ApiToken.Length == 0,
                    "provider credential was persisted in an experiment child snapshot");
                Require(child.BranchReceipt?.ExperimentId == experiment.Id,
                    "child branch receipt lost experiment identity");
            }

            var persistedRunJson = string.Join(
                "\n",
                Directory.EnumerateFiles(runStoreRoot, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText));
            Require(!persistedRunJson.Contains(LiveCredential, StringComparison.Ordinal)
                    && !persistedRunJson.Contains(RecordingExperimentProviderClient.ResponseText, StringComparison.Ordinal)
                    && !persistedRunJson.Contains(RecordingExperimentProviderClient.ReasoningText, StringComparison.Ordinal)
                    && !persistedRunJson.Contains(root, StringComparison.OrdinalIgnoreCase),
                "experiment evidence persisted credentials, response content, reasoning, or an absolute path");

            var beforeResumeCalls = provider.Configs.Count;
            var resumed = runner.RunAsync(
                experiment,
                resolution.Plan.Expansion,
                runStore,
                new ArenaExperimentRunnerOptions(MaximumParallelism: 1)).GetAwaiter().GetResult();
            Require(resumed.EligibleCells == 0 && resumed.StartedCells == 0 && provider.Configs.Count == beforeResumeCalls,
                "durable resume duplicated completed provider trials");
            Require(fixture.SessionStore.ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult().Count == 3,
                "resume created duplicate trial children");

            var changedProfiles = new ArenaExperimentProviderProfileRegistry(
                new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal)
                {
                    ["provider:live"] = LiveProvider(model: "scripted-live-model-v2")
                });
            var changedResolution = new ArenaExperimentExecutionResolver(
                    fixture.PackStore,
                    fixture.SessionStore,
                    changedProfiles,
                    rubricStore: fixture.RubricStore)
                .ResolveAsync(experiment)
                .GetAwaiter().GetResult();
            Require(changedResolution.IsAvailable && changedResolution.Plan is not null,
                ExecutionFormat(changedResolution.Diagnostics));
            Require(changedResolution.Plan!.PlanFingerprint != resolution.Plan.PlanFingerprint,
                "provider behavior changed without changing resolved execution identity");
            var changedProvider = new RecordingExperimentProviderClient();
            var staleResume = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    changedResolution.Plan,
                    changedProfiles,
                    fixture.SessionStore,
                    changedProvider))
                .RunAsync(
                    experiment,
                    changedResolution.Plan.Expansion,
                    runStore,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1))
                .GetAwaiter().GetResult();
            Require(staleResume.StartedCells == 0
                    && changedProvider.Configs.Count == 0
                    && staleResume.Diagnostics.Any(item => item.Code == "experiment_run.execution_plan_mismatch"),
                "changed provider behavior silently reused terminal evidence from another execution plan");
            var approvedChangedRun = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    changedResolution.Plan,
                    changedProfiles,
                    fixture.SessionStore,
                    changedProvider))
                .RunAsync(
                    experiment,
                    changedResolution.Plan.Expansion,
                    runStore,
                    new ArenaExperimentRunnerOptions(
                        MaximumParallelism: 1,
                        RetryApproved: _ => true))
                .GetAwaiter().GetResult();
            Require(approvedChangedRun.StartedCells == 2
                    && approvedChangedRun.Runs.All(item => item.Attempts == 2),
                "explicit retry approval did not establish the changed execution plan as the latest attempt");
            var originalCallsBeforeSwitchBack = provider.Configs.Count;
            var switchBack = runner.RunAsync(
                    experiment,
                    resolution.Plan.Expansion,
                    runStore,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1))
                .GetAwaiter().GetResult();
            Require(switchBack.StartedCells == 0
                    && provider.Configs.Count == originalCallsBeforeSwitchBack
                    && switchBack.Diagnostics.Any(item => item.Code == "experiment_run.execution_plan_mismatch"),
                "an older retained plan reference authorized reuse after a newer plan attempt");

            var fault = LiveFaultProfile();
            var faultResolver = new ArenaExperimentExecutionResolver(
                fixture.PackStore,
                fixture.SessionStore,
                fixture.Profiles,
                new Dictionary<string, ArenaFaultProfileContract>(StringComparer.Ordinal) { [fault.Id] = fault },
                fixture.RubricStore);
            var faultExperiment = LiveExperiment("experiment:fault-run", 1, 1) with { FaultProfileIds = [fault.Id] };
            var faultPlan = faultResolver.ResolveAsync(faultExperiment).GetAwaiter().GetResult();
            Require(faultPlan.IsAvailable && faultPlan.Plan is not null, ExecutionFormat(faultPlan.Diagnostics));
            var callsBeforeFault = provider.Configs.Count;
            var faultResult = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    faultPlan.Plan!, fixture.Profiles, fixture.SessionStore, provider))
                .RunAsync(faultExperiment, faultPlan.Plan!.Expansion, new ExperimentRunStore(Path.Combine(root, "fault-runs")))
                .GetAwaiter().GetResult();
            Require(faultResult.Runs.Single().State == ArenaExperimentRunState.Failed,
                "bound fault profile did not affect real execution");
            Require(provider.Configs.Count == callsBeforeFault, "injected provider fault reached the underlying provider unexpectedly");
            Require(faultResult.Runs.Single().Evidence.Any(item => item.Summary.Contains("fault profile injected", StringComparison.Ordinal)),
                "bound fault observation was not retained as content-free evidence");
            Require(faultResult.Runs.Single().Evidence.Any(item =>
                    item.State == ArenaEvidenceState.Unavailable
                    && item.Summary.Contains("Recovery is not measured", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(item.Limitation)),
                "an injected fault silently implied recovery instead of retaining distinct unavailable recovery evidence");

            var driftExperiment = LiveExperiment("experiment:source-drift", 1, 1);
            var driftPlan = resolver.ResolveAsync(driftExperiment).GetAwaiter().GetResult();
            Require(driftPlan.IsAvailable && driftPlan.Plan is not null, ExecutionFormat(driftPlan.Diagnostics));
            var changedSource = fixture.SessionStore.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            changedSource.Engine.Steering.Topic = "A changed source setup.";
            fixture.SessionStore.SaveSnapshotAsync(changedSource, "source").GetAwaiter().GetResult();
            var callsBeforeDrift = provider.Configs.Count;
            var driftResult = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    driftPlan.Plan!, fixture.Profiles, fixture.SessionStore, provider))
                .RunAsync(driftExperiment, driftPlan.Plan!.Expansion, new ExperimentRunStore(Path.Combine(root, "drift-runs")))
                .GetAwaiter().GetResult();
            var driftRun = driftResult.Runs.Single();
            Require(driftRun.State == ArenaExperimentRunState.Interrupted
                    && driftRun.InterruptionReason == "source_changed"
                    && driftRun.Evidence.Any(item => item.ReferenceId?.StartsWith("session:experiment-trial-", StringComparison.Ordinal) == true),
                "source drift left an unreferenced child or proceeded to provider execution");
            Require(provider.Configs.Count == callsBeforeDrift, "source drift reached the provider runtime");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string LiveCredential = "unit-registry-credential-7f4e2d";

    private sealed record LiveExecutionFixture(
        SessionStore SessionStore,
        ArenaExperimentPackStore PackStore,
        ArenaRubricStore RubricStore,
        ArenaExperimentProviderProfileRegistry Profiles,
        ArenaScenarioPackContract ScenarioPack);

    private static LiveExecutionFixture CreateLiveFixture(string root)
    {
        var sessionStore = new SessionStore(Path.Combine(root, "data"));
        var source = SessionStore.CreateDefaultSnapshot();
        foreach (var agent in source.Engine.Agents)
        {
            agent.Active = agent.Id == "alpha";
            agent.Status = agent.Active ? "waiting" : "muted";
        }
        source.Configs.Clear();
        source.Configs[ModelProviderRouting.SharedConfigKey] = LiveProvider(apiToken: "", model: "source-shared");
        source.Configs["alpha"] = LiveProvider(apiToken: "", model: "source-role");
        sessionStore.SaveSnapshotAsync(source, "source").GetAwaiter().GetResult();

        var profiles = new ArenaExperimentProviderProfileRegistry(
            new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal)
            {
                ["provider:live"] = LiveProvider()
            });
        var packStore = new ArenaExperimentPackStore(Path.Combine(root, "packs"));
        var rubricStore = new ArenaRubricStore(Path.Combine(root, "evaluation"));
        var rubricWrite = rubricStore.SaveRubricAsync(LiveRubric()).GetAwaiter().GetResult();
        Require(rubricWrite.Succeeded, Format(rubricWrite.Diagnostics));
        var sourceResolution = new ArenaExperimentExecutionResolver(packStore, sessionStore, profiles)
            .ResolveSessionSourceAsync("session:source")
            .GetAwaiter().GetResult();
        Require(sourceResolution.IsAvailable && sourceResolution.Source is not null,
            ExecutionFormat(sourceResolution.Diagnostics));

        ImmutableArray<ArenaScenarioInvariant> invariants =
            [new("invariant:live-completion", "rule:live-completion", "The bounded trial reaches a terminal state.", true)];
        ImmutableArray<ArenaScenarioDefinition> scenarios =
        [
            new(
                "scenario:live",
                "1.0.0",
                "Live execution fixture",
                sourceResolution.Source!.MatchSetupReference,
                sourceResolution.Source.SetupFingerprint,
                "A deterministic local execution fixture.",
                4,
                ["local", "smoke"],
                ["invariant:live-completion"],
                ["evidence:live-pack"])
        ];
        var scenarioPack = new ArenaScenarioPackContract(
            ArenaContractSchemas.ScenarioPack,
            "scenario-pack:live",
            At,
            "Live scenarios",
            "1.0.0",
            ArenaExperimentFingerprints.ScenarioPackContent(invariants, scenarios),
            null,
            invariants,
            scenarios,
            [new("evidence:live-pack", ArenaEvidenceState.Observed, "Local fixture identity was recorded.", "artifact:live-pack")]);
        var saved = packStore.SaveScenarioPackAsync(scenarioPack).GetAwaiter().GetResult();
        Require(saved.Succeeded, Format(saved.Diagnostics));
        return new(sessionStore, packStore, rubricStore, profiles, scenarioPack);
    }

    private static ArenaRubricContract LiveRubric() => new(
        ArenaContractSchemas.Rubric,
        "rubric:quality",
        At,
        "Live quality",
        "1.0.0",
        [new("evaluator:human", ArenaRubricEvaluatorKind.Human, null)],
        [new("criterion:primary", "Completion", "Checks bounded trial completion.", 1m, 0m, 5m)],
        [new("evidence:live-rubric", ArenaEvidenceState.Observed, "Local rubric identity was recorded.", "artifact:live-rubric")]);

    private static ArenaExperimentContract LiveExperiment(string id, int repetitions, int turnBudget) => new(
        ArenaContractSchemas.Experiment,
        id,
        At,
        "Live execution matrix",
        ArenaExperimentStatus.Draft,
        "scenario-pack:live",
        null,
        ["provider:live"],
        ["rubric:quality"],
        [],
        [
            new("dimension:context", "context_length", ["4096"]),
            new("dimension:max-output", "max_output_tokens", ["64"]),
            new("dimension:reasoning", "reasoning", ["low"]),
            new("dimension:temperature", "temperature", ["0.25"]),
            new("dimension:timeout", "timeout_seconds", ["5"])
        ],
        repetitions,
        turnBudget,
        1,
        [],
        [new("evidence:live-experiment", ArenaEvidenceState.Observed, "Local matrix identity was recorded.", "artifact:live-experiment")]);

    private static ArenaBenchmarkPackContract LiveBenchmark(
        ArenaScenarioPackContract scenarioPack,
        ImmutableArray<string> requiredCapabilities)
    {
        ImmutableArray<ArenaBenchmarkCase> cases =
        [
            new(
                "case:live",
                "scenario:live",
                ["rubric:quality"],
                2,
                requiredCapabilities,
                ["evidence:live-benchmark"])
        ];
        return new(
            ArenaContractSchemas.BenchmarkPack,
            "benchmark-pack:live",
            At,
            "Live benchmark",
            "1.0.0",
            ArenaExperimentFingerprints.BenchmarkPackContent(scenarioPack.Id, cases),
            null,
            scenarioPack.Id,
            cases,
            [new("evidence:live-benchmark", ArenaEvidenceState.Observed, "Local benchmark identity was recorded.", "artifact:live-benchmark")]);
    }

    private static ArenaFaultProfileContract LiveFaultProfile() => new(
        ArenaContractSchemas.FaultProfile,
        "fault:empty",
        At,
        "Empty provider response",
        "deterministic-live-seed",
        [new("fault:empty:one", ArenaFaultTarget.Provider, ArenaFaultKind.EmptyResponse, 0, 0, 100, 1, "The cell fails without raw provider content.")],
        [new("evidence:live-fault", ArenaEvidenceState.Observed, "Local fault profile identity was recorded.", "artifact:live-fault")]);

    private static ModelProviderConfig LiveProvider(
        string apiMode = ModelProviderApiModes.OpenAiCompatible,
        string apiToken = LiveCredential,
        string model = "scripted-live-model") => new()
    {
        BaseUrl = "http://127.0.0.1:12345/v1",
        ApiMode = apiMode,
        ApiToken = apiToken,
        Model = model,
        Timeout = 10,
        Temperature = 0.8,
        MaxOutputTokens = 128,
        ContextLength = 8192,
        Reasoning = "off",
        NativeStatefulChat = false
    };

    private static string ExecutionFormat(ImmutableArray<ArenaExperimentExecutionDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Summary}"));

    private static ArenaExperimentContract Experiment(int repetitions, int maximumParallelism) => new(
        ArenaContractSchemas.Experiment,
        "experiment:matrix",
        At,
        "Deterministic matrix",
        ArenaExperimentStatus.Draft,
        "scenario-pack:one",
        null,
        ["provider:a", "provider:b"],
        ["rubric:quality"],
        [],
        [new("dimension:temperature", "temperature", ["cold", "warm"])],
        repetitions,
        4,
        maximumParallelism,
        [],
        [Observed("evidence:experiment")]);

    private static ArenaExperimentContract CapacityExperiment(string id, string value) => new(
        ArenaContractSchemas.Experiment,
        id,
        At,
        "Capacity matrix",
        ArenaExperimentStatus.Draft,
        "scenario-pack:one",
        null,
        ["provider:capacity"],
        ["rubric:quality"],
        [],
        [new("dimension:capacity", "capacity_axis", [value])],
        1,
        1,
        1,
        [],
        [Observed($"evidence:{value}")]);

    private static ArenaScenarioPackContract ScenarioPack()
    {
        ImmutableArray<ArenaScenarioInvariant> invariants =
            [new("invariant:complete", "rule:complete", "Trial reaches a terminal state.", true)];
        ImmutableArray<ArenaScenarioDefinition> scenarios =
        [
            new(
                "scenario:one",
                "1.0.0",
                "Smoke",
                "match-setup:v2",
                HashB,
                "A bounded deterministic seed.",
                4,
                ["smoke"],
                ["invariant:complete"],
                ["evidence:pack"])
        ];
        return new(
            ArenaContractSchemas.ScenarioPack,
            "scenario-pack:one",
            At,
            "Smoke scenarios",
            "1.0.0",
            ArenaExperimentFingerprints.ScenarioPackContent(invariants, scenarios),
            null,
            invariants,
            scenarios,
            [Observed("evidence:pack")]);
    }

    private static ArenaScenarioPackContract WithScenarioContentFingerprint(ArenaScenarioPackContract pack) =>
        pack with
        {
            ContentFingerprint = ArenaExperimentFingerprints.ScenarioPackContent(pack.Invariants, pack.Scenarios)
        };

    private static ArenaBenchmarkPackContract BenchmarkPack()
    {
        const string scenarioPackId = "scenario-pack:one";
        ImmutableArray<ArenaBenchmarkCase> cases =
            [new("case:one", "scenario:one", ["rubric:quality"], 1, ["chat"], ["evidence:benchmark"])];
        return new(
            ArenaContractSchemas.BenchmarkPack,
            "benchmark-pack:one",
            At,
            "Smoke benchmark",
            "1.0.0",
            ArenaExperimentFingerprints.BenchmarkPackContent(scenarioPackId, cases),
            null,
            scenarioPackId,
            cases,
            [Observed("evidence:benchmark")]);
    }

    private static ArenaExperimentRunContract Run(
        ArenaExperimentCellPlan cell,
        ArenaExperimentRunState state,
        int attempts,
        ImmutableArray<string> trials,
        DateTimeOffset updatedAt) => new(
        ArenaContractSchemas.ExperimentRun,
        cell.RunId,
        At,
        cell.ExperimentId,
        cell.ExperimentFingerprint,
        cell.VariantFingerprint,
        cell.Repetition,
        cell.CellKey,
        state,
        attempts,
        updatedAt,
        trials.OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray(),
        state == ArenaExperimentRunState.Interrupted ? ArenaExperimentRunPolicy.ProcessRestartReason : null,
        [Observed("evidence:run")]);

    private static ArenaEvidenceAssertion Observed(string id) =>
        new(id, ArenaEvidenceState.Observed, "Recorded by deterministic local test.", "artifact:test");

    private static string Descriptor(ArenaExperimentVariant variant) =>
        $"{variant.ProviderProfileId}|{string.Join(",", variant.Values.Select(item => $"{item.Parameter}={item.Value}"))}";

    private static string CellDescriptor(ArenaExperimentCellPlan cell) =>
        $"{cell.ProviderProfileId}|{string.Join(",", cell.Values.Select(item => $"{item.DimensionId}={item.Value}"))}|{cell.Repetition}|{cell.CellKey}";

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-arena-experiment-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static T[] RunConcurrently<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        Task<T> Launch(Func<Task<T>> action) => Task.Run(async () =>
        {
            ready.Signal();
            start.Wait();
            return await action().ConfigureAwait(false);
        });

        var firstTask = Launch(first);
        var secondTask = Launch(second);
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            start.Set();
            throw new InvalidOperationException("Concurrent store writers did not reach their deterministic start barrier.");
        }
        start.Set();
        return Task.WhenAll(firstTask, secondTask).GetAwaiter().GetResult();
    }

    private static string Format(ImmutableArray<ArenaArtifactDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ObservingExecutor(TimeSpan delay) : IArenaExperimentCellExecutor
    {
        private int _active;
        private int _maximumActive;
        private readonly ConcurrentDictionary<string, int> _activeByProvider = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _maximumByProvider = new(StringComparer.Ordinal);

        public int MaximumActive => _maximumActive;
        public IReadOnlyDictionary<string, int> MaximumByProvider => _maximumByProvider;

        public async Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
            ArenaExperimentCellExecutionContext context,
            CancellationToken cancellationToken)
        {
            var global = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximumActive, global);
            var provider = _activeByProvider.AddOrUpdate(context.Cell.ProviderProfileId, 1, (_, value) => value + 1);
            _maximumByProvider.AddOrUpdate(context.Cell.ProviderProfileId, provider, (_, value) => Math.Max(value, provider));
            try
            {
                await Task.Delay(delay, cancellationToken);
                return ArenaExperimentCellExecutionResult.Completed();
            }
            finally
            {
                _activeByProvider.AddOrUpdate(context.Cell.ProviderProfileId, 0, (_, value) => value - 1);
                Interlocked.Decrement(ref _active);
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }

    private sealed class CancellationExecutor : IArenaExperimentCellExecutor
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
            ArenaExperimentCellExecutionContext context,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ArenaExperimentCellExecutionResult.Completed();
        }
    }

    private sealed class RecordingExperimentProviderClient : IModelProviderClient
    {
        public const string ResponseText = "private-live-response-content";
        public const string ReasoningText = "private-live-reasoning-content";
        private readonly ConcurrentQueue<ModelProviderConfig> _configs = new();

        public IReadOnlyList<ModelProviderConfig> Configs => _configs.ToArray();

        public Task<ModelProviderModels> ListModelsAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", At));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configs.Enqueue(config);
            return Task.FromResult(new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                ResponseText,
                ReasoningText,
                12,
                7,
                3,
                10,
                "",
                At,
                25,
                4,
                "response:private-live",
                2));
        }
    }
}
