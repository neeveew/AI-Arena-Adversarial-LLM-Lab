using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
            var legacyPack = WithScenarioContentFingerprint(ScenarioPack() with
            {
                Id = "scenario-pack:migrated",
                Name = "Migrated",
                Version = "0.9.0",
                Scenarios = [ScenarioPack().Scenarios[0] with { ScenarioSeed = "Migrated bounded seed." }]
            });
            var oldSchema = LegacyScenarioPackV0Bytes(legacyPack);
            var migration = ArenaExperimentPackCodec.DecodeScenarioPack(oldSchema, "imports/old.json");
            Require(migration.Diagnostics.Any(item => item.Code == "artifact.migration_required"
                && item.DetectedSchema == ArenaExperimentPackCodec.ScenarioPackV0Schema
                && item.ExpectedSchema == ArenaContractSchemas.ScenarioPack),
                "recognized older schema did not require an explicit migration");
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                Encoding.UTF8.GetBytes("{broken"), "imports/corrupt.json").Diagnostics.Any(item => item.Code == "artifact.corrupt"),
                "corrupt pack lacked fallback diagnostics");
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                new byte[32], "imports/large.json", maximumBytes: 16).Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "oversize pack lacked fallback diagnostics");

            var migratedResult = ArenaExperimentPackCodec.MigrateScenarioPackV0(oldSchema, "imports/old.json", At);
            Require(migratedResult.Succeeded && migratedResult.Pack is not null, Format(migratedResult.Diagnostics));
            var migrated = migratedResult.Pack ?? throw new InvalidOperationException("migrated scenario missing");
            var expectedSourceSha = Convert.ToHexStringLower(SHA256.HashData(oldSchema));
            Require(migrated.Migration is
                    {
                        SourceSchema: ArenaExperimentPackCodec.ScenarioPackV0Schema,
                        SourceVersion: "0.9.0",
                        MigratorVersion: ArenaExperimentPackCodec.ScenarioPackV0MigratorVersion,
                        MigratedAtUtc: var migratedAt
                    }
                    && migratedAt == At
                    && migrated.Migration.SourceContentFingerprint == expectedSourceSha,
                "scenario migration did not bind exact source bytes, schema, version, migrator, and injected UTC time");
            Require(migrated.CreatedAtUtc == At
                    && migrated.ContentFingerprint == ArenaExperimentFingerprints.ScenarioPackContent(migrated.Invariants, migrated.Scenarios),
                "scenario migration trusted legacy identity instead of recomputing canonical v1 identity");
            var repeatMigration = ArenaExperimentPackCodec.MigrateScenarioPackV0(oldSchema, "imports/old.json", At);
            Require(repeatMigration.Succeeded
                    && ArenaContractCodec.Serialize(repeatMigration.Pack!) == ArenaContractCodec.Serialize(migrated),
                "scenario migration was not deterministic for identical bytes and injected time");
            var canonicalMigratedBytes = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(migrated));
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(canonicalMigratedBytes, "imports/migrated.json").Succeeded,
                "migrated scenario did not satisfy the strict canonical v1 decoder");
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(canonicalMigratedBytes, "imports/remigrate.json", At)
                    .Diagnostics.Any(item => item.Code == "artifact.migration_source_unsupported"),
                "canonical v1 scenario was implicitly remigrated");
            var tamperedMigration = ArenaContractCodec.Serialize(migrated)
                .Replace("Migrated bounded seed.", "Tampered bounded seed.", StringComparison.Ordinal);
            Require(ArenaExperimentPackCodec.DecodeScenarioPack(
                    Encoding.UTF8.GetBytes(tamperedMigration),
                    "imports/tampered-migrated.json")
                .Diagnostics.Any(item => item.Code == "artifact.contract.scenario.content_fingerprint"),
                "tampered migrated scenario retained trusted v1 identity");
            var hostileLegacy = JsonNode.Parse(Encoding.UTF8.GetString(oldSchema))!.AsObject();
            hostileLegacy["sourceContent"] = "private payload";
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(
                    Encoding.UTF8.GetBytes(hostileLegacy.ToJsonString()),
                    "imports/hostile-v0.json",
                    At)
                .Diagnostics.Any(item => item.Code == "artifact.migration_source.privacy.source_content"),
                "hostile v0 source content crossed the migration privacy boundary");
            var unknownLegacy = JsonNode.Parse(Encoding.UTF8.GetString(oldSchema))!.AsObject();
            unknownLegacy["unexpected"] = "bounded";
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(
                    Encoding.UTF8.GetBytes(unknownLegacy.ToJsonString()),
                    "imports/unknown-v0.json",
                    At)
                .Diagnostics.Any(item => item.Code == "artifact.migration_wire_invalid"),
                "unknown v0 wire member was accepted by the closed migrator");
            var secretLegacy = JsonNode.Parse(Encoding.UTF8.GetString(oldSchema))!.AsObject();
            secretLegacy["name"] = "api_key=supersecretvalue";
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(
                    Encoding.UTF8.GetBytes(secretLegacy.ToJsonString()),
                    "imports/secret-v0.json",
                    At)
                .Diagnostics.Any(item => item.Code == "artifact.migration_source.privacy.secret"),
                "secret-shaped v0 value crossed the migration privacy boundary");
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(
                    oldSchema,
                    "imports/oversize-v0.json",
                    At,
                    maximumBytes: oldSchema.Length - 1)
                .Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "oversize v0 migration source crossed its byte bound");
            Require(ArenaExperimentPackCodec.MigrateScenarioPackV0(oldSchema, "imports/time.json", At.ToOffset(TimeSpan.FromHours(1)))
                    .Diagnostics.Any(item => item.Code == "artifact.migration_time"),
                "non-UTC injected migration time was accepted");
            var migratedWrite = store.SaveScenarioPackAsync(migrated).GetAwaiter().GetResult();
            Require(migratedWrite.Succeeded, Format(migratedWrite.Diagnostics));
            Require(store.SaveScenarioPackAsync(migrated).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Duplicate,
                "migrated scenario was persisted twice");
            var laterMigration = ArenaExperimentPackCodec.MigrateScenarioPackV0(
                oldSchema,
                "imports/old.json",
                At.AddMinutes(5));
            var laterDuplicate = store.SaveScenarioPackAsync(laterMigration.Pack!).GetAwaiter().GetResult();
            Require(laterDuplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate
                    && laterDuplicate.Diagnostics.Any(item => item.Code == "artifact.duplicate_migration_source")
                    && laterDuplicate.Artifact?.Migration?.MigratedAtUtc == At,
                "repeat migration of the exact source replaced its original durable receipt");
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
            var legacyBenchmark = benchmark with
            {
                Id = "benchmark-pack:migrated",
                Name = "Migrated benchmark",
                Version = "0.8.0",
                Cases = [benchmark.Cases[0] with { Repetitions = 2 }]
            };
            legacyBenchmark = legacyBenchmark with
            {
                ContentFingerprint = ArenaExperimentFingerprints.BenchmarkPackContent(
                    legacyBenchmark.ScenarioPackId,
                    legacyBenchmark.Cases)
            };
            var oldBenchmark = LegacyBenchmarkPackV0Bytes(legacyBenchmark);
            Require(ArenaExperimentPackCodec.DecodeBenchmarkPack(oldBenchmark, "imports/benchmark-old.json")
                .Diagnostics.Any(item => item.Code == "artifact.migration_required"),
                "older benchmark schema lacked explicit migration diagnostics");
            var migratedBenchmarkResult = ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                oldBenchmark,
                "imports/benchmark-old.json",
                At);
            Require(migratedBenchmarkResult.Succeeded && migratedBenchmarkResult.Pack is not null,
                Format(migratedBenchmarkResult.Diagnostics));
            var migratedBenchmark = migratedBenchmarkResult.Pack ?? throw new InvalidOperationException("migrated benchmark missing");
            Require(migratedBenchmark.Migration is
                    {
                        SourceSchema: ArenaExperimentPackCodec.BenchmarkPackV0Schema,
                        SourceVersion: "0.8.0",
                        MigratorVersion: ArenaExperimentPackCodec.BenchmarkPackV0MigratorVersion
                    }
                    && migratedBenchmark.Migration.SourceContentFingerprint
                        == Convert.ToHexStringLower(SHA256.HashData(oldBenchmark))
                    && migratedBenchmark.ContentFingerprint == ArenaExperimentFingerprints.BenchmarkPackContent(
                        migratedBenchmark.ScenarioPackId,
                        migratedBenchmark.Cases),
                "benchmark migration lacked exact provenance or recomputed identity");
            var repeatedBenchmarkMigration = ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                oldBenchmark,
                "imports/benchmark-old.json",
                At);
            Require(repeatedBenchmarkMigration.Succeeded
                    && ArenaContractCodec.Serialize(repeatedBenchmarkMigration.Pack!)
                        == ArenaContractCodec.Serialize(migratedBenchmark),
                "benchmark migration was not deterministic for identical bytes and injected time");
            var canonicalMigratedBenchmarkBytes = Encoding.UTF8.GetBytes(ArenaContractCodec.Serialize(migratedBenchmark));
            Require(ArenaExperimentPackCodec.DecodeBenchmarkPack(
                    canonicalMigratedBenchmarkBytes,
                    "imports/benchmark-migrated.json").Succeeded
                    && ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                        canonicalMigratedBenchmarkBytes,
                        "imports/benchmark-remigrate.json",
                        At).Diagnostics.Any(item => item.Code == "artifact.migration_source_unsupported"),
                "migrated benchmark was not canonical v1 or was implicitly remigrated");
            var tamperedMigratedBenchmark = ArenaContractCodec.Serialize(migratedBenchmark)
                .Replace("\"repetitions\":2", "\"repetitions\":3", StringComparison.Ordinal);
            Require(ArenaExperimentPackCodec.DecodeBenchmarkPack(
                    Encoding.UTF8.GetBytes(tamperedMigratedBenchmark),
                    "imports/benchmark-tampered.json")
                .Diagnostics.Any(item => item.Code == "artifact.contract.benchmark.content_fingerprint"),
                "tampered migrated benchmark retained trusted v1 identity");
            var unknownLegacyBenchmark = JsonNode.Parse(Encoding.UTF8.GetString(oldBenchmark))!.AsObject();
            unknownLegacyBenchmark["unexpected"] = "bounded";
            Require(ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                    Encoding.UTF8.GetBytes(unknownLegacyBenchmark.ToJsonString()),
                    "imports/benchmark-unknown-v0.json",
                    At).Diagnostics.Any(item => item.Code == "artifact.migration_wire_invalid"),
                "unknown benchmark v0 wire member was accepted by the closed migrator");
            var benchmarkMigrationWrite = store.SaveBenchmarkPackAsync(migratedBenchmark).GetAwaiter().GetResult();
            Require(benchmarkMigrationWrite.Disposition == ArenaArtifactWriteDisposition.Written
                    && store.SaveBenchmarkPackAsync(migratedBenchmark).GetAwaiter().GetResult().Disposition
                        == ArenaArtifactWriteDisposition.Duplicate,
                "migrated benchmark duplicate prevention failed");
            var laterBenchmarkMigration = ArenaExperimentPackCodec.MigrateBenchmarkPackV0(
                oldBenchmark,
                "imports/benchmark-old.json",
                At.AddMinutes(6));
            var laterBenchmarkDuplicate = store.SaveBenchmarkPackAsync(laterBenchmarkMigration.Pack!).GetAwaiter().GetResult();
            Require(laterBenchmarkDuplicate.Disposition == ArenaArtifactWriteDisposition.Duplicate
                    && laterBenchmarkDuplicate.Diagnostics.Any(item => item.Code == "artifact.duplicate_migration_source")
                    && laterBenchmarkDuplicate.Artifact?.Migration?.MigratedAtUtc == At,
                "repeat benchmark migration replaced its original durable receipt");

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

            var rollbackCell = ExperimentExpander.Expand(
                CapacityExperiment("experiment:clock-rollback", "rollback")).Cells.Single();
            var rollbackStore = new ExperimentRunStore(Path.Combine(root, "clock-rollback"));
            var futureAttempt = Run(
                rollbackCell,
                ArenaExperimentRunState.Completed,
                1,
                [ArenaExperimentRunPolicy.CreateTrialId(rollbackCell.CellKey, 1)],
                At.AddHours(6));
            Require(rollbackStore.SaveAsync(futureAttempt).GetAwaiter().GetResult().Succeeded,
                "clock-rollback prior attempt was not persisted");
            var rolledBackRunning = Run(
                rollbackCell,
                ArenaExperimentRunState.Running,
                2,
                [ArenaExperimentRunPolicy.CreateTrialId(rollbackCell.CellKey, 2)],
                At.AddHours(1));
            var rollbackRunningWrite = rollbackStore.SaveAsync(rolledBackRunning).GetAwaiter().GetResult();
            Require(rollbackRunningWrite.Artifact is { Attempts: 2, State: ArenaExperimentRunState.Running },
                $"wall-clock rollback hid the newer logical Running attempt ({rollbackRunningWrite.Disposition}; attempts {rollbackRunningWrite.Artifact?.Attempts}; state {rollbackRunningWrite.Artifact?.State}; {Format(rollbackRunningWrite.Diagnostics)})");
            var rolledBackTerminal = rolledBackRunning with
            {
                State = ArenaExperimentRunState.Completed,
                UpdatedAtUtc = At.AddMinutes(30)
            };
            var rollbackTerminalWrite = rollbackStore.SaveAsync(rolledBackTerminal).GetAwaiter().GetResult();
            Require(rollbackTerminalWrite.Artifact is { Attempts: 2, State: ArenaExperimentRunState.Completed },
                "wall-clock rollback regressed the terminal transition of the current logical attempt");

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

    internal static void PersistsDefinitionsAndRequiresApprovedRestart()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new ExperimentDefinitionStore(root);
            var draft = Experiment(repetitions: 1, maximumParallelism: 2);
            var first = store.SaveAsync(draft).GetAwaiter().GetResult();
            Require(first.Disposition == ArenaArtifactWriteDisposition.Written, Format(first.Diagnostics));
            Require(store.SaveAsync(draft).GetAwaiter().GetResult().Disposition == ArenaArtifactWriteDisposition.Duplicate,
                "byte-identical experiment draft was persisted twice");
            foreach (var fabricatedStatus in new[]
                     {
                         ArenaExperimentStatus.Completed,
                         ArenaExperimentStatus.Cancelled,
                         ArenaExperimentStatus.Interrupted
                     })
            {
                var fabricated = store.SaveAsync(draft with { Status = fabricatedStatus }).GetAwaiter().GetResult();
                Require(fabricated.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && fabricated.Diagnostics.Any(item => item.Code == "experiment_definition.lifecycle_transition")
                        && store.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Draft,
                    $"Draft definition fabricated a {fabricatedStatus} lifecycle without an owner-held run");
            }
            Require(store.SaveAsync(draft with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult()
                    .Diagnostics.Any(item => item.Code == "experiment_definition.execution_lease_required"),
                "Running experiment definition was persisted without an owner lease");

            var owner = store.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                var ownerFabricated = store.SaveForExecutionAsync(
                    owner,
                    draft with { Status = ArenaExperimentStatus.Completed }).GetAwaiter().GetResult();
                Require(ownerFabricated.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && ownerFabricated.Diagnostics.Any(item => item.Code == "experiment_definition.lifecycle_transition"),
                    "an owner lease skipped Draft-to-Running before terminalizing a definition");
                var running = store.SaveForExecutionAsync(
                    owner,
                    draft with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult();
                Require(running.Succeeded && running.Artifact?.Status == ArenaExperimentStatus.Running,
                    Format(running.Diagnostics));
                var ownerInterrupted = store.SaveForExecutionAsync(
                    owner,
                    draft with { Status = ArenaExperimentStatus.Interrupted }).GetAwaiter().GetResult();
                Require(ownerInterrupted.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && ownerInterrupted.Diagnostics.Any(item => item.Code == "experiment_definition.lifecycle_transition")
                        && store.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Running,
                    "a regular owner write invented Interrupted instead of using restart recovery");
                var competingStore = new ExperimentDefinitionStore(root);
                RequireThrows<InvalidOperationException>(
                    () => competingStore.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult(),
                    "a second coordinator acquired the active definition owner lease");
                var hostileTerminal = competingStore.SaveAsync(
                    draft with { Status = ArenaExperimentStatus.Completed }).GetAwaiter().GetResult();
                Require(hostileTerminal.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && hostileTerminal.Diagnostics.Any(item => item.Code == "experiment_definition.execution_lease_required")
                        && store.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                            == ArenaExperimentStatus.Running,
                    "a non-owner store terminalized the active coordinator's Running definition");
            }
            finally
            {
                owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            var restartedStore = new ExperimentDefinitionStore(root);
            var restartOwner = restartedStore.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                var recovered = restartedStore.RecoverInterruptedAfterRestartAsync(
                    restartOwner,
                    At.AddMinutes(1)).GetAwaiter().GetResult();
                Require(recovered.Definitions.Single().Status == ArenaExperimentStatus.Interrupted
                        && recovered.Diagnostics.Any(item => item.Code == "experiment_definition.restart_normalized"),
                    "abandoned Running definition was not durably normalized to Interrupted");
                var unapproved = restartedStore.SaveForExecutionAsync(
                    restartOwner,
                    draft with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult();
                Require(unapproved.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && unapproved.Diagnostics.Any(item => item.Code == "experiment_definition.retry_approval_required"),
                    "Interrupted definition resumed without explicit retry approval");
                var approved = restartedStore.SaveForExecutionAsync(
                    restartOwner,
                    draft with { Status = ArenaExperimentStatus.Running },
                    retryApproved: true).GetAwaiter().GetResult();
                Require(approved.Succeeded && approved.Artifact?.Status == ArenaExperimentStatus.Running,
                    "approved Interrupted definition did not re-enter Running");
                var completed = restartedStore.SaveForExecutionAsync(
                    restartOwner,
                    draft with { Status = ArenaExperimentStatus.Completed }).GetAwaiter().GetResult();
                Require(completed.Succeeded && completed.Artifact?.Status == ArenaExperimentStatus.Completed,
                    "completed definition lifecycle was not persisted");
            }
            finally
            {
                restartOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            var passive = restartedStore.LoadAllAsync().GetAwaiter().GetResult();
            Require(passive.Definitions is [{ Status: ArenaExperimentStatus.Completed }]
                    && passive.Definitions[0].CreatedAtUtc == draft.CreatedAtUtc
                    && ArenaExperimentFingerprints.Experiment(passive.Definitions[0])
                        == ArenaExperimentFingerprints.Experiment(draft),
                "restart persistence changed definition creation time or execution identity");
            Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                "definition atomic write left a temporary file");

            var boundedRoot = Path.Combine(root, "bounded");
            var bounded = new ExperimentDefinitionStore(
                boundedRoot,
                new(MaximumDefinitions: 1, MaximumDefinitionBytes: 64));
            var oversize = bounded.SaveAsync(draft).GetAwaiter().GetResult();
            Require(oversize.Diagnostics.Any(item => item.Code == "artifact.oversize"),
                "bounded definition store accepted an oversize artifact");
            var privateDefinition = draft with
            {
                Id = "experiment:private",
                Title = "api_key=supersecretvalue"
            };
            var privateWrite = restartedStore.SaveAsync(privateDefinition).GetAwaiter().GetResult();
            Require(privateWrite.Disposition == ArenaArtifactWriteDisposition.Rejected
                    && !Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                        .Select(File.ReadAllText)
                        .Any(text => text.Contains("supersecretvalue", StringComparison.Ordinal)),
                "secret-shaped experiment definition reached durable storage");
            foreach (var initialTerminalStatus in new[]
                     {
                         ArenaExperimentStatus.Completed,
                         ArenaExperimentStatus.Cancelled,
                         ArenaExperimentStatus.Interrupted
                     })
            {
                var inventedTerminal = restartedStore.SaveAsync(draft with
                {
                    Id = $"experiment:invented-{initialTerminalStatus.ToString().ToLowerInvariant()}",
                    Status = initialTerminalStatus
                }).GetAwaiter().GetResult();
                Require(inventedTerminal.Disposition == ArenaArtifactWriteDisposition.Rejected
                        && inventedTerminal.Diagnostics.Any(item => item.Code == "experiment_definition.initial_status"),
                    $"definition store accepted a new {initialTerminalStatus} lifecycle with no prior draft or Running owner");
            }

            var raceRoot = Path.Combine(root, "race");
            var firstStore = new ExperimentDefinitionStore(raceRoot);
            var secondStore = new ExperimentDefinitionStore(raceRoot);
            var conflict = draft with { Title = "Conflicting title" };
            var writes = Task.WhenAll(
                firstStore.SaveAsync(draft),
                secondStore.SaveAsync(conflict)).GetAwaiter().GetResult();
            Require(writes.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Written) == 1
                    && writes.Count(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected) == 1
                    && writes.Single(item => item.Disposition == ArenaArtifactWriteDisposition.Rejected)
                        .Diagnostics.Any(item => item.Code == "experiment_definition.identity_conflict"),
                "cross-instance definition writers did not serialize a conflicting ID atomically");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RecoversDefinitionAfterActualProcessExit()
    {
        var root = TemporaryRoot();
        var resultPath = Path.Combine(root, "child-result.txt");
        Process? process = null;
        try
        {
            var definition = Experiment(repetitions: 1, maximumParallelism: 1) with
            {
                Id = "experiment:process-exit"
            };
            var store = new ExperimentDefinitionStore(root);
            Require(store.SaveAsync(definition).GetAwaiter().GetResult().Succeeded,
                "process-exit definition draft could not be persisted");

            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The Core test executable path is unavailable.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add("--abandon-experiment-definition");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add(resultPath);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the experiment-definition owner process.");
            Require(process.WaitForExit(20_000), "experiment-definition owner process did not exit");
            Require(process.ExitCode == 0
                    && File.Exists(resultPath)
                    && File.ReadAllText(resultPath).Equals("running", StringComparison.Ordinal),
                $"experiment-definition owner process did not persist Running before exit (exit {process.ExitCode})");
            Require(store.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single().Status
                    == ArenaExperimentStatus.Running,
                "actual child process did not leave the definition in Running state");

            var restarted = new ExperimentDefinitionStore(root);
            var owner = restarted.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                var recovered = restarted.RecoverInterruptedAfterRestartAsync(
                    owner,
                    At.AddMinutes(1)).GetAwaiter().GetResult();
                Require(recovered.Definitions.Single().Status == ArenaExperimentStatus.Interrupted
                        && recovered.Diagnostics.Any(item => item.Code == "experiment_definition.restart_normalized"),
                    "a new process owner did not normalize the exited process's Running definition");
            }
            finally
            {
                owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    internal static int RunAbandonedDefinitionProcess(string[] processArgs)
    {
        if (processArgs.Length != 3)
        {
            return 64;
        }

        try
        {
            var store = new ExperimentDefinitionStore(processArgs[1]);
            var definition = store.LoadAllAsync().GetAwaiter().GetResult().Definitions.Single();
            var owner = store.AcquireExecutionLeaseAsync().AsTask().GetAwaiter().GetResult();
            var write = store.SaveForExecutionAsync(
                owner,
                definition with { Status = ArenaExperimentStatus.Running }).GetAwaiter().GetResult();
            if (!write.Succeeded || write.Artifact?.Status != ArenaExperimentStatus.Running)
            {
                return 1;
            }

            File.WriteAllText(processArgs[2], "running");
            // Deliberately terminate without disposing the owner lease. The OS
            // closes the file handle, exactly modelling a process exit between
            // the Running receipt and terminal definition receipt.
            Environment.Exit(0);
            return 0;
        }
        catch
        {
            return 1;
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

            var mixedRoot = Path.Combine(root, "mixed-plan-attempts");
            var mixedStore = new ExperimentRunStore(mixedRoot);
            var mixedExperiment = CapacityExperiment("experiment:mixed-plan-attempts", "p1") with
            {
                Dimensions = [new("dimension:mixed-plan", "capacity_axis", ["left", "right"])]
            };
            var mixedExpansion = ExperimentExpander.Expand(mixedExperiment);
            var planOneExecutor = new PlanEvidenceExecutor(HashA);
            var planOne = new ExperimentRunnerService(planOneExecutor)
                .RunAsync(mixedExperiment, mixedExpansion, mixedStore)
                .GetAwaiter().GetResult();
            Require(planOne.StartedCells == 2
                    && planOne.Runs.All(run => ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(run, HashA)),
                "P1 fixture did not retain current-plan evidence for every cell");
            var observedPlanRun = planOne.Runs[0];
            var observedPlanEvidence = observedPlanRun.Evidence.Single(item =>
                item.ReferenceId == $"plan:{HashA}");
            var inferredPlanRun = observedPlanRun with
            {
                Evidence = observedPlanRun.Evidence.Replace(
                    observedPlanEvidence,
                    observedPlanEvidence with
                    {
                        State = ArenaEvidenceState.Inferred,
                        Basis = "A plan identity was inferred rather than observed."
                    })
            };
            var unavailablePlanRun = observedPlanRun with
            {
                Evidence = observedPlanRun.Evidence.Replace(
                    observedPlanEvidence,
                    observedPlanEvidence with
                    {
                        State = ArenaEvidenceState.Unavailable,
                        Limitation = "The plan identity was unavailable."
                    })
            };
            Require(ArenaContractCodec.Validate(inferredPlanRun).IsValid
                    && ArenaContractCodec.Validate(unavailablePlanRun).IsValid
                    && !ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(inferredPlanRun, HashA)
                    && !ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(unavailablePlanRun, HashA),
                "inferred or unavailable execution-plan evidence authorized latest-attempt reuse");
            var missingLatestTrialId = ArenaExperimentRunPolicy.CreateTrialId(observedPlanRun.CellKey, 2);
            var missingLatestTrialRun = observedPlanRun with
            {
                Attempts = 2,
                TrialIds = [ArenaExperimentRunPolicy.CreateTrialId(observedPlanRun.CellKey, 1)],
                Evidence = observedPlanRun.Evidence.Add(new(
                    ArenaExperimentRunPolicy.CreateExecutionPlanEvidenceId(missingLatestTrialId),
                    ArenaEvidenceState.Observed,
                    "Synthetic latest plan evidence must not replace trial membership.",
                    $"plan:{HashA}"))
            };
            Require(ArenaContractCodec.Validate(missingLatestTrialRun).IsValid
                    && !ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(missingLatestTrialRun, HashA),
                "synthetic latest-attempt plan evidence authorized reuse without its canonical trial membership");
            var alreadyPlanTwo = planOne.Runs.OrderBy(run => run.CellKey, StringComparer.Ordinal).First();
            var planTwoTrial = ArenaExperimentRunPolicy.CreateTrialId(alreadyPlanTwo.CellKey, 2);
            var planTwoEvidence = new ArenaEvidenceAssertion(
                ArenaExperimentRunPolicy.CreateExecutionPlanEvidenceId(planTwoTrial),
                ArenaEvidenceState.Observed,
                "The trial used the resolved execution-plan identity.",
                $"plan:{HashB}");
            var mixedWrite = mixedStore.SaveAsync(alreadyPlanTwo with
            {
                Attempts = 2,
                UpdatedAtUtc = alreadyPlanTwo.UpdatedAtUtc.AddSeconds(1),
                TrialIds = alreadyPlanTwo.TrialIds.Add(planTwoTrial).Order(StringComparer.Ordinal).ToImmutableArray(),
                Evidence = alreadyPlanTwo.Evidence.Add(planTwoEvidence).OrderBy(item => item.Id, StringComparer.Ordinal).ToImmutableArray()
            }).GetAwaiter().GetResult();
            Require(mixedWrite.Succeeded, Format(mixedWrite.Diagnostics));

            var planTwoExecutor = new PlanEvidenceExecutor(HashB);
            var blockedMixed = new ExperimentRunnerService(planTwoExecutor)
                .RunAsync(mixedExperiment, mixedExpansion, mixedStore)
                .GetAwaiter().GetResult();
            Require(blockedMixed.StartedCells == 0
                    && planTwoExecutor.Executions == 0
                    && blockedMixed.Diagnostics.Any(item => item.Code == "experiment_run.execution_plan_mismatch"),
                "an unapproved P1/P2 mixed matrix started an otherwise eligible cell");
            var approvedMixed = new ExperimentRunnerService(planTwoExecutor)
                .RunAsync(
                    mixedExperiment,
                    mixedExpansion,
                    mixedStore,
                    new ArenaExperimentRunnerOptions(
                        RetryApproved: run => !ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(run, HashB)))
                .GetAwaiter().GetResult();
            Require(approvedMixed.StartedCells == 1
                    && planTwoExecutor.Executions == 1
                    && approvedMixed.Runs
                        .Where(run => mixedExpansion.Cells.Any(cell => cell.CellKey == run.CellKey))
                        .All(run => ArenaExperimentRunPolicy.IsTerminal(run.State)
                            && ArenaExperimentRunPolicy.LatestAttemptMatchesPlan(run, HashB)),
                "approved P1/P2 recovery did not retry every and only mismatched terminal cell");

            var oneShotRoot = Path.Combine(root, "one-shot-plan-approval");
            var oneShotStore = new ExperimentRunStore(oneShotRoot);
            var oneShotPlanOne = new PlanEvidenceExecutor(HashA);
            var oneShotInitial = new ExperimentRunnerService(oneShotPlanOne)
                .RunAsync(mixedExperiment, mixedExpansion, oneShotStore)
                .GetAwaiter().GetResult();
            Require(oneShotInitial.StartedCells == mixedExpansion.Cells.Length,
                "one-shot approval fixture did not persist every P1 cell");
            var approvalCalls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            bool ApproveOnce(ArenaExperimentRunContract run) =>
                approvalCalls.AddOrUpdate(run.CellKey, 1, static (_, count) => count + 1) == 1;
            var oneShotPlanTwo = new PlanEvidenceExecutor(HashB);
            var oneShotResult = new ExperimentRunnerService(oneShotPlanTwo)
                .RunAsync(
                    mixedExperiment,
                    mixedExpansion,
                    oneShotStore,
                    new ArenaExperimentRunnerOptions(RetryApproved: ApproveOnce))
                .GetAwaiter().GetResult();
            var mismatchDiagnostics = oneShotResult.Diagnostics
                .Where(item => item.Code == "experiment_run.execution_plan_mismatch")
                .ToArray();
            Require(oneShotResult.StartedCells == mixedExpansion.Cells.Length
                    && oneShotPlanTwo.Executions == mixedExpansion.Cells.Length
                    && mixedExpansion.Cells.All(cell => approvalCalls.TryGetValue(cell.CellKey, out var count) && count == 1),
                "stateful retry approval was evaluated more than once for an existing cell");
            Require(mismatchDiagnostics.Length == mixedExpansion.Cells.Length
                    && mixedExpansion.Cells.All(cell => mismatchDiagnostics.Any(item =>
                        item.Message.Contains(cell.CellKey, StringComparison.Ordinal))),
                "multi-cell plan mismatch diagnostics collapsed or lost cell-scoped provenance");

            var rollbackRoot = Path.Combine(root, "runner-clock-rollback");
            var rollbackStore = new ExperimentRunStore(rollbackRoot);
            var rollbackExperiment = CapacityExperiment("experiment:runner-clock-rollback", "rollback");
            var rollbackExpansion = ExperimentExpander.Expand(rollbackExperiment);
            var rollbackCell = rollbackExpansion.Cells.Single();
            Require(rollbackStore.SaveAsync(Run(
                    rollbackCell,
                    ArenaExperimentRunState.Completed,
                    1,
                    [ArenaExperimentRunPolicy.CreateTrialId(rollbackCell.CellKey, 1)],
                    At.AddDays(2))).GetAwaiter().GetResult().Succeeded,
                "runner rollback fixture could not persist its first attempt");
            var rollbackExecutor = new ObservingExecutor(TimeSpan.Zero);
            var rollbackRunner = new ExperimentRunnerService(
                rollbackExecutor,
                new SequenceExperimentTimeProvider(
                    At,
                    At.AddDays(1),
                    At.AddHours(12),
                    At.AddHours(12)));
            var rollbackResult = rollbackRunner.RunAsync(
                    rollbackExperiment,
                    rollbackExpansion,
                    rollbackStore,
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1, RetryApproved: _ => true))
                .GetAwaiter().GetResult();
            Require(rollbackResult.StartedCells == 1
                    && rollbackExecutor.Executions == 1
                    && rollbackResult.Runs.Single(item => item.CellKey == rollbackCell.CellKey) is
                        { Attempts: 2, State: ArenaExperimentRunState.Completed },
                $"runner called the executor without committing the current Running attempt or lost its terminal state under clock rollback (started {rollbackResult.StartedCells}; executions {rollbackExecutor.Executions}; runs {string.Join(" | ", rollbackResult.Runs.Select(item => $"{item.Attempts}:{item.State}:{item.UpdatedAtUtc:O}"))}; diagnostics {Format(rollbackResult.Diagnostics)})");
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
            var atomicStore = new SessionStore(Path.Combine(root, "atomic-fork-data"));
            var atomicSource = SessionStore.CreateDefaultSnapshot();
            atomicSource.Configs[ModelProviderRouting.SharedConfigKey] = LiveProvider();
            atomicStore.SaveSnapshotAsync(atomicSource, "atomic-source").GetAwaiter().GetResult();
            var persistedAtomicSource = atomicStore.LoadSnapshotAsync("atomic-source").GetAwaiter().GetResult()!;
            var atomicReplacement = ArenaExperimentProviderProfileRegistry.Copy(LiveProvider(), apiToken: "");
            var atomicFork = atomicStore.ForkExperimentSessionAsync(
                    "atomic-source",
                    "atomic-child",
                    persistedAtomicSource.PersistenceRevision,
                    SessionStore.SetupFingerprint(persistedAtomicSource),
                    "experiment:atomic-boundary",
                    atomicReplacement)
                .GetAwaiter().GetResult();
            using (var cancelledAfterBoundary = new CancellationTokenSource())
            {
                cancelledAfterBoundary.Cancel();
            }
            var recoveredAtomicChild = new SessionStore(atomicStore.DataRoot)
                .LoadSnapshotAsync(atomicFork.TargetSessionId).GetAwaiter().GetResult()!;
            Require(recoveredAtomicChild.BranchReceipt?.ExperimentId == "experiment:atomic-boundary"
                    && recoveredAtomicChild.Configs.Keys.SequenceEqual([ModelProviderRouting.SharedConfigKey])
                    && recoveredAtomicChild.Configs[ModelProviderRouting.SharedConfigKey].ApiToken.Length == 0
                    && recoveredAtomicChild.PersistenceRevision == atomicFork.TargetPersistenceRevision,
                "cancellation or process loss immediately after the experiment fork boundary exposed an unsanitized durable child");
            var atomicChildJson = File.ReadAllText(NativeDataPaths.SessionSnapshotPath(atomicStore.DataRoot, atomicFork.TargetSessionId));
            Require(!atomicChildJson.Contains(LiveCredential, StringComparison.Ordinal),
                "atomic experiment fork persisted its process-memory provider credential");

            VerifyExperimentProviderCallMutationLeases(Path.Combine(root, "provider-call-leases"));

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

            var childDriftExperiment = LiveExperiment("experiment:two-turn-child-drift", 1, 2);
            var childDriftPlan = resolver.ResolveAsync(childDriftExperiment).GetAwaiter().GetResult();
            Require(childDriftPlan.IsAvailable && childDriftPlan.Plan is not null,
                ExecutionFormat(childDriftPlan.Diagnostics));
            var childDriftProvider = new RecordingExperimentProviderClient();
            var childDriftExecutor = new ArenaExperimentCellExecutor(
                childDriftPlan.Plan!,
                fixture.Profiles,
                fixture.SessionStore,
                childDriftProvider,
                async (completedTurns, childSessionId, token) =>
                {
                    if (completedTurns != 1)
                    {
                        return;
                    }
                    var externallyChanged = await fixture.SessionStore
                        .LoadSnapshotAsync(childSessionId, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("experiment child disappeared before drift injection");
                    externallyChanged.Engine.Steering.Topic = "external mutation between experiment turns";
                    await fixture.SessionStore.SaveSnapshotAsync(
                        externallyChanged,
                        childSessionId,
                        token).ConfigureAwait(false);
                });
            var childDriftResult = new ExperimentRunnerService(childDriftExecutor)
                .RunAsync(
                    childDriftExperiment,
                    childDriftPlan.Plan!.Expansion,
                    new ExperimentRunStore(Path.Combine(root, "child-drift-runs")))
                .GetAwaiter().GetResult();
            var childDriftRun = childDriftResult.Runs.Single();
            Require(childDriftProvider.Configs.Count == 1
                    && childDriftRun.State == ArenaExperimentRunState.Interrupted
                    && childDriftRun.InterruptionReason == "child_state_changed",
                "external mutation after turn one reached the turn-two provider or was not recorded as child drift");

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

            foreach (var streamOnlyKind in new[] { ArenaFaultKind.MalformedStream, ArenaFaultKind.Interruption })
            {
                var streamFault = LiveFaultProfile(streamOnlyKind);
                var streamFaultResolver = new ArenaExperimentExecutionResolver(
                    fixture.PackStore,
                    fixture.SessionStore,
                    fixture.Profiles,
                    new Dictionary<string, ArenaFaultProfileContract>(StringComparer.Ordinal) { [streamFault.Id] = streamFault },
                    fixture.RubricStore);
                var slug = FaultSlug(streamOnlyKind);
                var streamFaultExperiment = LiveExperiment($"experiment:{slug}-unexercised", 1, 1) with
                {
                    FaultProfileIds = [streamFault.Id]
                };
                var streamFaultPlan = streamFaultResolver.ResolveAsync(streamFaultExperiment).GetAwaiter().GetResult();
                Require(streamFaultPlan.IsAvailable && streamFaultPlan.Plan is not null,
                    ExecutionFormat(streamFaultPlan.Diagnostics));
                var callsBeforeStreamFault = provider.Configs.Count;
                var streamFaultResult = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                        streamFaultPlan.Plan!, fixture.Profiles, fixture.SessionStore, provider))
                    .RunAsync(
                        streamFaultExperiment,
                        streamFaultPlan.Plan!.Expansion,
                        new ExperimentRunStore(Path.Combine(root, $"{slug}-runs")))
                    .GetAwaiter().GetResult();
                var streamFaultRun = streamFaultResult.Runs.Single();
                Require(streamFaultRun.State == ArenaExperimentRunState.Completed
                        && provider.Configs.Count == callsBeforeStreamFault + 1,
                    $"non-streaming experiment cell incorrectly exercised {streamOnlyKind}");
                Require(streamFaultRun.Evidence.Any(item =>
                            item.State == ArenaEvidenceState.Unavailable
                            && item.Summary.Contains("was not exercised", StringComparison.Ordinal)
                            && item.Limitation?.Contains("requires a streaming completion boundary", StringComparison.Ordinal) == true)
                        && !streamFaultRun.Evidence.Any(item =>
                            item.State == ArenaEvidenceState.Observed
                            && item.Summary.Contains($"injected a {slug}", StringComparison.OrdinalIgnoreCase)),
                    $"non-streaming experiment cell claimed unobserved {streamOnlyKind} injection");
            }

            var driftExperiment = LiveExperiment("experiment:source-drift", 1, 1);
            var driftPlan = resolver.ResolveAsync(driftExperiment).GetAwaiter().GetResult();
            Require(driftPlan.IsAvailable && driftPlan.Plan is not null, ExecutionFormat(driftPlan.Diagnostics));
            var changedSource = fixture.SessionStore.LoadSnapshotAsync("source").GetAwaiter().GetResult()!;
            changedSource.Engine.Steering.Topic = "A changed source setup.";
            fixture.SessionStore.SaveSnapshotAsync(changedSource, "source").GetAwaiter().GetResult();
            var callsBeforeDrift = provider.Configs.Count;
            var sessionsBeforeDrift = fixture.SessionStore.ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult().Count;
            var driftResult = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    driftPlan.Plan!, fixture.Profiles, fixture.SessionStore, provider))
                .RunAsync(driftExperiment, driftPlan.Plan!.Expansion, new ExperimentRunStore(Path.Combine(root, "drift-runs")))
                .GetAwaiter().GetResult();
            var driftRun = driftResult.Runs.Single();
            Require(driftRun.State == ArenaExperimentRunState.Interrupted
                    && driftRun.InterruptionReason == "source_changed"
                    && !driftRun.Evidence.Any(item => item.ReferenceId?.StartsWith("session:experiment-trial-", StringComparison.Ordinal) == true),
                "source drift created or referenced a child before the atomic parent revision/setup check");
            Require(provider.Configs.Count == callsBeforeDrift
                    && fixture.SessionStore.ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult().Count == sessionsBeforeDrift,
                "source drift reached the provider runtime or durably created an experiment child");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyExperimentProviderCallMutationLeases(string root)
    {
        var fixture = CreateLiveFixture(root);
        var resolver = new ArenaExperimentExecutionResolver(
            fixture.PackStore,
            fixture.SessionStore,
            fixture.Profiles,
            rubricStore: fixture.RubricStore);

        var restoreExperiment = LiveExperiment("experiment:restore-during-provider-call", 1, 1);
        var restorePlan = resolver.ResolveAsync(restoreExperiment).GetAwaiter().GetResult();
        Require(restorePlan.IsAvailable && restorePlan.Plan is not null, ExecutionFormat(restorePlan.Diagnostics));
        var restoreProvider = new BlockingExperimentProviderClient();
        var beforeRestoreSessions = fixture.SessionStore
            .ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var restoreExecution = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                restorePlan.Plan!,
                fixture.Profiles,
                fixture.SessionStore,
                restoreProvider))
            .RunAsync(
                restoreExperiment,
                restorePlan.Plan!.Expansion,
                new ExperimentRunStore(Path.Combine(root, "restore-runs")));
        Task<CheckpointSummary?>? restoreTask = null;
        bool restoreWasBlocked;
        bool restoreSnapshotStayedStable;
        try
        {
            Require(restoreProvider.Started.Task.Wait(TimeSpan.FromSeconds(5)),
                "restore lease regression did not reach the provider boundary");
            var childSessionId = fixture.SessionStore
                .ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult()
                .Select(item => item.Id)
                .Single(id => !beforeRestoreSessions.Contains(id));
            var snapshotPath = fixture.SessionStore.SnapshotPath(childSessionId);
            var checkpointSnapshot = fixture.SessionStore.LoadSnapshotAsync(childSessionId)
                .GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("restore lease fixture child snapshot was unavailable");
            var checkpointId = Guid.NewGuid().ToString("N");
            var checkpointDirectory = fixture.SessionStore.CheckpointDirectory(childSessionId);
            Directory.CreateDirectory(checkpointDirectory);
            File.WriteAllText(
                Path.Combine(checkpointDirectory, $"{checkpointId}.json"),
                System.Text.Json.JsonSerializer.Serialize(new CheckpointRecord
                {
                    Id = checkpointId,
                    Name = "provider-call restore boundary",
                    SessionId = childSessionId,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Snapshot = checkpointSnapshot
                }));
            var beforeRestoreBytes = File.ReadAllBytes(snapshotPath);
            // The fixture record is staged directly because the production
            // checkpoint writer correctly shares the same session-tree lease as
            // the provider call. The operation under test is the cooperative
            // restore, which must remain blocked until that call releases it.
            restoreTask = fixture.SessionStore.RestoreCheckpointAsync(childSessionId, checkpointId);
            restoreWasBlocked = !restoreTask.Wait(TimeSpan.FromMilliseconds(350));
            restoreSnapshotStayedStable = File.Exists(snapshotPath)
                && File.ReadAllBytes(snapshotPath).SequenceEqual(beforeRestoreBytes);
        }
        finally
        {
            restoreProvider.Release.TrySetResult(true);
        }

        var restored = restoreTask?.GetAwaiter().GetResult();
        var restoreResult = restoreExecution.GetAwaiter().GetResult();
        Require(restoreWasBlocked && restoreSnapshotStayedStable && restored is not null,
            "checkpoint restore mutated an experiment child while its provider call was in flight");
        Require(restoreProvider.Calls == 1
                && restoreResult.Runs.Single() is
                    { State: ArenaExperimentRunState.Interrupted, InterruptionReason: "child_state_changed" },
            "post-provider restore was not contained as deterministic child-state drift");

        var deleteExperiment = LiveExperiment("experiment:delete-during-provider-call", 1, 1);
        var deletePlan = resolver.ResolveAsync(deleteExperiment).GetAwaiter().GetResult();
        Require(deletePlan.IsAvailable && deletePlan.Plan is not null, ExecutionFormat(deletePlan.Diagnostics));
        var deleteProvider = new BlockingExperimentProviderClient();
        var beforeDeleteSessions = fixture.SessionStore
            .ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult()
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var deleteExecution = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                deletePlan.Plan!,
                fixture.Profiles,
                fixture.SessionStore,
                deleteProvider))
            .RunAsync(
                deleteExperiment,
                deletePlan.Plan!.Expansion,
                new ExperimentRunStore(Path.Combine(root, "delete-runs")));
        Task<bool>? deleteTask = null;
        string? deletedChildSessionId = null;
        bool deleteWasBlocked;
        bool deleteSnapshotStayedStable;
        try
        {
            Require(deleteProvider.Started.Task.Wait(TimeSpan.FromSeconds(5)),
                "delete lease regression did not reach the provider boundary");
            deletedChildSessionId = fixture.SessionStore
                .ListSessionsAsync(SessionListingDetail.Identity).GetAwaiter().GetResult()
                .Select(item => item.Id)
                .Single(id => !beforeDeleteSessions.Contains(id));
            var snapshotPath = fixture.SessionStore.SnapshotPath(deletedChildSessionId);
            var beforeDeleteBytes = File.ReadAllBytes(snapshotPath);
            deleteTask = fixture.SessionStore.DeleteSessionAsync(deletedChildSessionId);
            deleteWasBlocked = !deleteTask.Wait(TimeSpan.FromMilliseconds(350));
            deleteSnapshotStayedStable = File.Exists(snapshotPath)
                && File.ReadAllBytes(snapshotPath).SequenceEqual(beforeDeleteBytes);
        }
        finally
        {
            deleteProvider.Release.TrySetResult(true);
        }

        var deleted = deleteTask?.GetAwaiter().GetResult() == true;
        var deleteResult = deleteExecution.GetAwaiter().GetResult();
        Require(deleteWasBlocked && deleteSnapshotStayedStable && deleted,
            "session delete removed an experiment child while its provider call was in flight");
        Require(deleteProvider.Calls == 1
                && deleteResult.Runs.Single() is
                    { State: ArenaExperimentRunState.Interrupted, InterruptionReason: "child_state_changed" }
                && deletedChildSessionId is not null
                && !Directory.Exists(Path.GetDirectoryName(fixture.SessionStore.SnapshotPath(deletedChildSessionId))),
            $"post-provider delete was not durable or was not contained as deterministic child-state drift "
            + $"(calls {deleteProvider.Calls}; run {deleteResult.Runs.Single().State}; "
            + $"reason {deleteResult.Runs.Single().InterruptionReason ?? "<none>"}; "
            + $"directory exists {Directory.Exists(Path.GetDirectoryName(fixture.SessionStore.SnapshotPath(deletedChildSessionId ?? "missing")))})");
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

    private static ArenaFaultProfileContract LiveFaultProfile(ArenaFaultKind kind = ArenaFaultKind.EmptyResponse)
    {
        var slug = FaultSlug(kind);
        return new(
            ArenaContractSchemas.FaultProfile,
            $"fault:{slug}",
            At,
            $"{kind} provider condition",
            "deterministic-live-seed",
            [new($"fault:{slug}:one", ArenaFaultTarget.Provider, kind, 0, 0, 100, 1, "Exercise only at a compatible provider boundary.")],
            [new($"evidence:live-fault:{slug}", ArenaEvidenceState.Observed, "Local fault profile identity was recorded.", "artifact:live-fault")]);
    }

    private static string FaultSlug(ArenaFaultKind kind) => kind switch
    {
        ArenaFaultKind.Timeout => "timeout",
        ArenaFaultKind.Disconnect => "disconnect",
        ArenaFaultKind.MalformedStream => "malformed-stream",
        ArenaFaultKind.Saturation => "saturation",
        ArenaFaultKind.EmptyResponse => "empty",
        ArenaFaultKind.Interruption => "interruption",
        ArenaFaultKind.ContextPressure => "context-pressure",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

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

    private static byte[] LegacyScenarioPackV0Bytes(ArenaScenarioPackContract pack)
    {
        var root = JsonNode.Parse(ArenaContractCodec.Serialize(pack))!.AsObject();
        root["schema"] = ArenaExperimentPackCodec.ScenarioPackV0Schema;
        root.Remove("createdAtUtc");
        root.Remove("contentFingerprint");
        root.Remove("migration");
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static byte[] LegacyBenchmarkPackV0Bytes(ArenaBenchmarkPackContract pack)
    {
        var root = JsonNode.Parse(ArenaContractCodec.Serialize(pack))!.AsObject();
        root["schema"] = ArenaExperimentPackCodec.BenchmarkPackV0Schema;
        root.Remove("createdAtUtc");
        root.Remove("contentFingerprint");
        root.Remove("migration");
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

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

    private sealed class ObservingExecutor(TimeSpan delay) : IArenaExperimentCellExecutor
    {
        private int _active;
        private int _maximumActive;
        private int _executions;
        private readonly ConcurrentDictionary<string, int> _activeByProvider = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _maximumByProvider = new(StringComparer.Ordinal);

        public int MaximumActive => _maximumActive;
        public int Executions => Volatile.Read(ref _executions);
        public IReadOnlyDictionary<string, int> MaximumByProvider => _maximumByProvider;

        public async Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
            ArenaExperimentCellExecutionContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
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

    private sealed class SequenceExperimentTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly DateTimeOffset[] _values = values.Length == 0
            ? throw new ArgumentException("At least one UTC time is required.", nameof(values))
            : values;
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return _values[Math.Min(index, _values.Length - 1)];
        }
    }

    private sealed class PlanEvidenceExecutor(string planFingerprint) :
        IArenaExperimentCellExecutor,
        IArenaExperimentExecutionPlanIdentity
    {
        private int _executions;

        public string PlanFingerprint { get; } = planFingerprint;
        public int Executions => Volatile.Read(ref _executions);

        public Task<ArenaExperimentCellExecutionResult> ExecuteAsync(
            ArenaExperimentCellExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _executions);
            var evidenceId = ArenaExperimentRunPolicy.CreateExecutionPlanEvidenceId(context.TrialId);
            return Task.FromResult(ArenaExperimentCellExecutionResult.Completed(
                [new(
                    evidenceId,
                    ArenaEvidenceState.Observed,
                    "The trial used the resolved execution-plan identity.",
                    $"plan:{PlanFingerprint}")]));
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

    private sealed class BlockingExperimentProviderClient : IModelProviderClient
    {
        private int _calls;

        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls => Volatile.Read(ref _calls);

        public Task<ModelProviderModels> ListModelsAsync(
            ModelProviderConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", At));

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new ModelCompletionResult(
                true,
                config.BaseUrl,
                config.Model,
                RecordingExperimentProviderClient.ResponseText,
                RecordingExperimentProviderClient.ReasoningText,
                12,
                7,
                3,
                10,
                "",
                At,
                25,
                4,
                "response:blocked-live",
                2);
        }
    }
}
