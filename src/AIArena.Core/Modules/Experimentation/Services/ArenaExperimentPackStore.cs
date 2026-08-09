using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

public enum ArenaArtifactDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record ArenaArtifactDiagnostic(
    string Code,
    ArenaArtifactDiagnosticSeverity Severity,
    string RelativePath,
    string Message,
    string? DetectedSchema = null,
    string? ExpectedSchema = null);

public sealed record ArenaPackDecodeResult<T>(
    T? Pack,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics)
    where T : class, IArenaVersionedContract
{
    public bool Succeeded => Pack is not null && Diagnostics.All(item => item.Severity != ArenaArtifactDiagnosticSeverity.Error);
}

public enum ArenaArtifactWriteDisposition
{
    Written,
    Duplicate,
    Rejected
}

public sealed record ArenaArtifactWriteResult<T>(
    ArenaArtifactWriteDisposition Disposition,
    string RelativePath,
    T? Artifact,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics)
    where T : class, IArenaVersionedContract
{
    public bool Succeeded => Disposition is ArenaArtifactWriteDisposition.Written or ArenaArtifactWriteDisposition.Duplicate
        && Diagnostics.All(item => item.Severity != ArenaArtifactDiagnosticSeverity.Error);
}

public sealed record ArenaArtifactLoadResult<T>(
    ImmutableArray<T> Artifacts,
    ImmutableArray<ArenaArtifactDiagnostic> Diagnostics)
    where T : class, IArenaVersionedContract;

/// <summary>
/// Strict bounded decoder for portable scenario and benchmark packs. Ordinary
/// decoding never migrates. The two named v0-to-v1 entry points below accept a
/// deliberately small closed legacy wire shape and make migration an explicit
/// caller action.
/// </summary>
public static class ArenaExperimentPackCodec
{
    public const int DefaultMaximumPackBytes = 2 * 1024 * 1024;
    public const string ScenarioPackV0Schema = "ai_arena.scenario_pack.v0";
    public const string BenchmarkPackV0Schema = "ai_arena.benchmark_pack.v0";
    public const string ScenarioPackV0MigratorVersion = "ai_arena.scenario_pack.v0_to_v1.migrator.1";
    public const string BenchmarkPackV0MigratorVersion = "ai_arena.benchmark_pack.v0_to_v1.migrator.1";

    private static readonly JsonSerializerOptions MigrationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    // V0 intentionally has no createdAtUtc, contentFingerprint, or migration
    // fields. Those are v1 authority and cannot be asserted by legacy input.
    private sealed record ScenarioPackV0(
        string Schema,
        string Id,
        string Name,
        string Version,
        ImmutableArray<ArenaScenarioInvariant> Invariants,
        ImmutableArray<ArenaScenarioDefinition> Scenarios,
        ImmutableArray<ArenaEvidenceAssertion> Evidence);

    private sealed record BenchmarkPackV0(
        string Schema,
        string Id,
        string Name,
        string Version,
        string ScenarioPackId,
        ImmutableArray<ArenaBenchmarkCase> Cases,
        ImmutableArray<ArenaEvidenceAssertion> Evidence);

    private sealed record MigrationSourceResult<T>(
        T? Source,
        string? SourceSha256,
        ImmutableArray<ArenaArtifactDiagnostic> Diagnostics)
        where T : class;

    public static ArenaPackDecodeResult<ArenaScenarioPackContract> DecodeScenarioPack(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        int maximumBytes = DefaultMaximumPackBytes) =>
        Decode<ArenaScenarioPackContract>(utf8, relativePath, ArenaContractSchemas.ScenarioPack, maximumBytes);

    public static ArenaPackDecodeResult<ArenaBenchmarkPackContract> DecodeBenchmarkPack(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        int maximumBytes = DefaultMaximumPackBytes) =>
        Decode<ArenaBenchmarkPackContract>(utf8, relativePath, ArenaContractSchemas.BenchmarkPack, maximumBytes);

    public static ArenaPackDecodeResult<ArenaScenarioPackContract> MigrateScenarioPackV0(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        DateTimeOffset migratedAtUtc,
        int maximumBytes = DefaultMaximumPackBytes)
    {
        var source = DecodeMigrationSource<ScenarioPackV0>(
            utf8,
            relativePath,
            ScenarioPackV0Schema,
            migratedAtUtc,
            maximumBytes);
        if (source.Source is null || source.SourceSha256 is null)
        {
            return new(null, source.Diagnostics);
        }

        var legacy = source.Source;
        var provenance = new ArenaPackMigrationProvenance(
            ScenarioPackV0Schema,
            legacy.Version,
            source.SourceSha256,
            ScenarioPackV0MigratorVersion,
            migratedAtUtc);
        var candidate = new ArenaScenarioPackContract(
            ArenaContractSchemas.ScenarioPack,
            legacy.Id,
            migratedAtUtc,
            legacy.Name,
            legacy.Version,
            new string('0', 64),
            provenance,
            legacy.Invariants,
            legacy.Scenarios,
            legacy.Evidence);
        var issues = ArenaContractCodec.Validate(candidate).Issues
            .Where(issue => !string.Equals(issue.Code, "scenario.content_fingerprint", StringComparison.Ordinal))
            .ToImmutableArray();
        if (!issues.IsEmpty)
        {
            return MigrationContractFailure<ArenaScenarioPackContract>(relativePath, issues);
        }

        var migrated = candidate with
        {
            ContentFingerprint = ArenaExperimentFingerprints.ScenarioPackContent(
                candidate.Invariants,
                candidate.Scenarios)
        };
        return CompleteMigration(migrated, relativePath, ScenarioPackV0Schema);
    }

    public static ArenaPackDecodeResult<ArenaBenchmarkPackContract> MigrateBenchmarkPackV0(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        DateTimeOffset migratedAtUtc,
        int maximumBytes = DefaultMaximumPackBytes)
    {
        var source = DecodeMigrationSource<BenchmarkPackV0>(
            utf8,
            relativePath,
            BenchmarkPackV0Schema,
            migratedAtUtc,
            maximumBytes);
        if (source.Source is null || source.SourceSha256 is null)
        {
            return new(null, source.Diagnostics);
        }

        var legacy = source.Source;
        var provenance = new ArenaPackMigrationProvenance(
            BenchmarkPackV0Schema,
            legacy.Version,
            source.SourceSha256,
            BenchmarkPackV0MigratorVersion,
            migratedAtUtc);
        var candidate = new ArenaBenchmarkPackContract(
            ArenaContractSchemas.BenchmarkPack,
            legacy.Id,
            migratedAtUtc,
            legacy.Name,
            legacy.Version,
            new string('0', 64),
            provenance,
            legacy.ScenarioPackId,
            legacy.Cases,
            legacy.Evidence);
        var issues = ArenaContractCodec.Validate(candidate).Issues
            .Where(issue => !string.Equals(issue.Code, "benchmark.content_fingerprint", StringComparison.Ordinal))
            .ToImmutableArray();
        if (!issues.IsEmpty)
        {
            return MigrationContractFailure<ArenaBenchmarkPackContract>(relativePath, issues);
        }

        var migrated = candidate with
        {
            ContentFingerprint = ArenaExperimentFingerprints.BenchmarkPackContent(
                candidate.ScenarioPackId,
                candidate.Cases)
        };
        return CompleteMigration(migrated, relativePath, BenchmarkPackV0Schema);
    }

    private static MigrationSourceResult<T> DecodeMigrationSource<T>(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        string expectedSchema,
        DateTimeOffset migratedAtUtc,
        int maximumBytes)
        where T : class
    {
        var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
        if (!ArenaContractPrivacyRules.IsSafeRelativePath(relativePath))
        {
            diagnostics.Add(Error("artifact.relative_path", "artifact.json", "Artifact path is not a safe relative path."));
            return new(null, null, Sort(diagnostics));
        }
        if (maximumBytes < 1 || utf8.Length > maximumBytes)
        {
            diagnostics.Add(Error("artifact.oversize", relativePath, "Artifact exceeds its bounded byte limit."));
            return new(null, null, Sort(diagnostics));
        }
        if (migratedAtUtc == default || migratedAtUtc.Offset != TimeSpan.Zero)
        {
            diagnostics.Add(Error("artifact.migration_time", relativePath, "Migration requires a non-default UTC timestamp."));
            return new(null, null, Sort(diagnostics));
        }

        string json;
        JsonDocument document;
        try
        {
            json = new UTF8Encoding(false, true).GetString(utf8.Span);
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            diagnostics.Add(Error("artifact.corrupt", relativePath, "Migration source is not strict UTF-8 JSON."));
            return new(null, null, Sort(diagnostics));
        }

        using (document)
        {
            if (HasDuplicatePropertyNames(document.RootElement))
            {
                diagnostics.Add(Error(
                    "artifact.migration_duplicate_member",
                    relativePath,
                    "Migration source contains a duplicate JSON member and is ambiguous."));
                return new(null, null, Sort(diagnostics));
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(Error("artifact.schema_missing", relativePath, "Migration source has no supported schema.", expectedSchema: expectedSchema));
                return new(null, null, Sort(diagnostics));
            }

            var detected = schema.GetString() ?? "";
            if (!string.Equals(detected, expectedSchema, StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "artifact.migration_source_unsupported",
                    relativePath,
                    "Only the named v0 wire contract can enter this explicit migrator.",
                    detected,
                    expectedSchema));
                return new(null, null, Sort(diagnostics));
            }

            var privacyIssues = ArenaContractPrivacyRules.Inspect(document.RootElement);
            if (!privacyIssues.IsEmpty)
            {
                diagnostics.AddRange(privacyIssues.Select(issue => Error(
                    $"artifact.migration_source.{issue.Code}",
                    relativePath,
                    $"Migration source failed privacy validation at {issue.Path}.")));
                return new(null, null, Sort(diagnostics));
            }
        }

        try
        {
            var source = JsonSerializer.Deserialize<T>(json, MigrationJsonOptions);
            if (source is null)
            {
                diagnostics.Add(Error("artifact.corrupt", relativePath, "Migration source could not be decoded."));
                return new(null, null, Sort(diagnostics));
            }

            var sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(utf8.Span));
            return new(source, sourceSha256, Sort(diagnostics));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            diagnostics.Add(Error("artifact.migration_wire_invalid", relativePath, "Migration source does not match the closed v0 wire contract."));
            return new(null, null, Sort(diagnostics));
        }
    }

    private static bool HasDuplicatePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicatePropertyNames(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicatePropertyNames(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ArenaPackDecodeResult<T> CompleteMigration<T>(
        T migrated,
        string relativePath,
        string sourceSchema)
        where T : class, IArenaVersionedContract
    {
        var validation = ArenaContractCodec.Validate(migrated);
        if (!validation.IsValid)
        {
            return MigrationContractFailure<T>(relativePath, validation.Issues);
        }

        return new(
            migrated,
            [new(
                "artifact.migrated",
                ArenaArtifactDiagnosticSeverity.Information,
                relativePath,
                "Explicit migration produced a validated canonical v1 artifact.",
                sourceSchema,
                migrated.Schema)]);
    }

    private static ArenaPackDecodeResult<T> MigrationContractFailure<T>(
        string relativePath,
        IEnumerable<ArenaContractValidationIssue> issues)
        where T : class, IArenaVersionedContract =>
        new(
            null,
            [.. issues.Select(issue => Error(
                    $"artifact.migration_contract.{issue.Code}",
                    relativePath,
                    $"Migrated contract validation failed at {issue.Path}."))
                .Distinct()
                .OrderBy(item => item.Code, StringComparer.Ordinal)]);

    private static ArenaPackDecodeResult<T> Decode<T>(
        ReadOnlyMemory<byte> utf8,
        string relativePath,
        string expectedSchema,
        int maximumBytes)
        where T : class, IArenaVersionedContract
    {
        var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
        if (!ArenaContractPrivacyRules.IsSafeRelativePath(relativePath))
        {
            diagnostics.Add(Error("artifact.relative_path", "artifact.json", "Artifact path is not a safe relative path."));
            return new(null, Sort(diagnostics));
        }

        if (maximumBytes < 1 || utf8.Length > maximumBytes)
        {
            diagnostics.Add(Error("artifact.oversize", relativePath, "Artifact exceeds its bounded byte limit."));
            return new(null, Sort(diagnostics));
        }

        string json;
        JsonDocument document;
        try
        {
            json = new UTF8Encoding(false, true).GetString(utf8.Span);
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            diagnostics.Add(Error("artifact.corrupt", relativePath, "Artifact is not strict UTF-8 JSON."));
            return new(null, Sort(diagnostics));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("artifact.corrupt", relativePath, "Artifact root must be an object."));
                return new(null, Sort(diagnostics));
            }

            if (!document.RootElement.TryGetProperty("schema", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(schemaElement.GetString()))
            {
                diagnostics.Add(Error(
                    "artifact.schema_missing",
                    relativePath,
                    "Artifact has no schema and cannot be migrated implicitly.",
                    expectedSchema: expectedSchema));
                return new(null, Sort(diagnostics));
            }

            var detectedSchema = schemaElement.GetString()!;
            if (!string.Equals(detectedSchema, expectedSchema, StringComparison.Ordinal))
            {
                var schemaFamily = expectedSchema[..expectedSchema.LastIndexOf(".", StringComparison.Ordinal)];
                var recognizedFamily = detectedSchema.StartsWith($"{schemaFamily}.", StringComparison.Ordinal);
                diagnostics.Add(Error(
                    recognizedFamily ? "artifact.migration_required" : "artifact.schema_unsupported",
                    relativePath,
                    recognizedFamily
                        ? "Artifact uses an older or newer recognized schema and requires an explicit migration."
                        : "Artifact schema is not supported by this store.",
                    detectedSchema,
                    expectedSchema));
                return new(null, Sort(diagnostics));
            }
        }

        if (!ArenaContractCodec.TryDeserialize<T>(json, out var pack, out var issues) || pack is null)
        {
            diagnostics.AddRange(issues.Select(issue => Error(
                $"artifact.contract.{issue.Code}",
                relativePath,
                $"Contract validation failed at {issue.Path}.")));
            return new(null, Sort(diagnostics));
        }

        var canonical = ArenaContractCodec.Serialize(pack);
        if (!string.Equals(json, canonical, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "artifact.canonical_required",
                relativePath,
                "Artifact must use the canonical v1 JSON encoding."));
            return new(null, Sort(diagnostics));
        }

        var migration = pack switch
        {
            ArenaScenarioPackContract scenario => scenario.Migration,
            ArenaBenchmarkPackContract benchmark => benchmark.Migration,
            _ => null
        };
        if (migration is not null)
        {
            diagnostics.Add(new(
                "artifact.migration_provenance",
                ArenaArtifactDiagnosticSeverity.Information,
                relativePath,
                "Artifact records an explicit migration into the current schema.",
                migration.SourceSchema,
                expectedSchema));
        }

        return new(pack, Sort(diagnostics));
    }

    internal static ArenaArtifactDiagnostic Error(
        string code,
        string relativePath,
        string message,
        string? detectedSchema = null,
        string? expectedSchema = null) =>
        new(code, ArenaArtifactDiagnosticSeverity.Error, relativePath, message, detectedSchema, expectedSchema);

    internal static ImmutableArray<ArenaArtifactDiagnostic> Sort(
        ImmutableArray<ArenaArtifactDiagnostic>.Builder diagnostics) =>
        [.. diagnostics
            .Distinct()
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)];
}

/// <summary>
/// Local-only scenario/benchmark pack persistence. File names are hashes of
/// logical IDs; no caller path or source content is persisted.
/// </summary>
public sealed class ArenaExperimentPackStore
{
    public const int DefaultMaximumArtifactsPerKind = 512;
    private static readonly TimeSpan StoreLeaseTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly int _maximumPackBytes;
    private readonly int _maximumArtifactsPerKind;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ArenaExperimentPackStore(
        string root,
        int maximumPackBytes = ArenaExperimentPackCodec.DefaultMaximumPackBytes,
        int maximumArtifactsPerKind = DefaultMaximumArtifactsPerKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPackBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumArtifactsPerKind, 1);
        _root = Path.GetFullPath(root);
        _maximumPackBytes = maximumPackBytes;
        _maximumArtifactsPerKind = maximumArtifactsPerKind;
    }

    public Task<ArenaArtifactWriteResult<ArenaScenarioPackContract>> SaveScenarioPackAsync(
        ArenaScenarioPackContract pack,
        CancellationToken cancellationToken = default) =>
        SaveAsync(pack, "scenario-packs", ArenaExperimentPackCodec.DecodeScenarioPack, cancellationToken);

    public Task<ArenaArtifactWriteResult<ArenaBenchmarkPackContract>> SaveBenchmarkPackAsync(
        ArenaBenchmarkPackContract pack,
        CancellationToken cancellationToken = default) =>
        SaveAsync(pack, "benchmark-packs", ArenaExperimentPackCodec.DecodeBenchmarkPack, cancellationToken);

    public Task<ArenaArtifactLoadResult<ArenaScenarioPackContract>> LoadScenarioPacksAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync<ArenaScenarioPackContract>("scenario-packs", ArenaExperimentPackCodec.DecodeScenarioPack, cancellationToken);

    public Task<ArenaArtifactLoadResult<ArenaBenchmarkPackContract>> LoadBenchmarkPacksAsync(
        CancellationToken cancellationToken = default) =>
        LoadAsync<ArenaBenchmarkPackContract>("benchmark-packs", ArenaExperimentPackCodec.DecodeBenchmarkPack, cancellationToken);

    private async Task<ArenaArtifactWriteResult<T>> SaveAsync<T>(
        T pack,
        string directoryName,
        Func<ReadOnlyMemory<byte>, string, int, ArenaPackDecodeResult<T>> decode,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        ArgumentNullException.ThrowIfNull(pack);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLease = await AcquireStoreLeaseAsync(cancellationToken).ConfigureAwait(false);
            var json = ArenaContractCodec.Serialize(pack);
            var bytes = Encoding.UTF8.GetBytes(json);
            var relativePath = RelativePath(directoryName, pack.Id);
            if (bytes.Length > _maximumPackBytes)
            {
                return Rejected(pack, relativePath, "artifact.oversize", "Artifact exceeds its bounded byte limit.");
            }

            var existing = await LoadCoreAsync(directoryName, decode, cancellationToken).ConfigureAwait(false);
            var sameIdentity = existing.Artifacts.FirstOrDefault(item => string.Equals(item.Id, pack.Id, StringComparison.Ordinal));
            if (sameIdentity is not null)
            {
                if (string.Equals(ArenaContractCodec.Serialize(sameIdentity), json, StringComparison.Ordinal))
                {
                    return new(ArenaArtifactWriteDisposition.Duplicate, relativePath, sameIdentity, existing.Diagnostics);
                }
                if (IsRepeatMigration(sameIdentity, pack))
                {
                    return new(
                        ArenaArtifactWriteDisposition.Duplicate,
                        relativePath,
                        sameIdentity,
                        [new(
                            "artifact.duplicate_migration_source",
                            ArenaArtifactDiagnosticSeverity.Information,
                            relativePath,
                            "The exact legacy source and canonical content were already migrated; the original migration receipt was retained.")]);
                }

                return Rejected(pack, relativePath, "artifact.duplicate_id", "A different artifact already uses this ID.");
            }

            var sameContent = existing.Artifacts.FirstOrDefault(item => HasSameContentIdentity(item, pack));
            if (sameContent is not null)
            {
                return new(
                    ArenaArtifactWriteDisposition.Duplicate,
                    RelativePath(directoryName, sameContent.Id),
                    sameContent,
                    [new(
                        "artifact.duplicate_content",
                        ArenaArtifactDiagnosticSeverity.Information,
                        RelativePath(directoryName, sameContent.Id),
                        "Canonical pack content is already stored under its content identity.")]);
            }

            var sameVersion = existing.Artifacts.FirstOrDefault(item => HasSamePackVersion(item, pack));
            if (sameVersion is not null)
            {
                return Rejected(pack, relativePath, "artifact.duplicate_version", "A different artifact already uses this name and version.");
            }

            var directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);
            if (Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Take(_maximumArtifactsPerKind + 1).Count()
                >= _maximumArtifactsPerKind)
            {
                return Rejected(pack, relativePath, "artifact.count_limit", "Artifact store has reached its bounded file count.");
            }

            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return Rejected(pack, relativePath, "artifact.path_conflict", "Artifact path already exists but could not be decoded safely.");
            }

            await WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return new(ArenaArtifactWriteDisposition.Written, relativePath, pack, []);
        }
        catch (InvalidDataException)
        {
            return Rejected(pack, RelativePath(directoryName, pack.Id), "artifact.contract_invalid", "Artifact does not satisfy its frozen v1 contract.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Rejected(pack, RelativePath(directoryName, pack.Id), "artifact.write_failed", "Artifact could not be written atomically.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireStoreLeaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ".pack-store.lock");
        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < StoreLeaseTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Experiment pack store execution lease was unavailable before its deadline.");
    }

    private async Task<ArenaArtifactLoadResult<T>> LoadAsync<T>(
        string directoryName,
        Func<ReadOnlyMemory<byte>, string, int, ArenaPackDecodeResult<T>> decode,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(directoryName, decode, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ArenaArtifactLoadResult<T>> LoadCoreAsync<T>(
        string directoryName,
        Func<ReadOnlyMemory<byte>, string, int, ArenaPackDecodeResult<T>> decode,
        CancellationToken cancellationToken)
        where T : class, IArenaVersionedContract
    {
        var artifacts = ImmutableArray.CreateBuilder<T>();
        var diagnostics = ImmutableArray.CreateBuilder<ArenaArtifactDiagnostic>();
        var directory = Path.Combine(_root, directoryName);
        if (!Directory.Exists(directory))
        {
            return new([], []);
        }

        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(_maximumArtifactsPerKind + 1)
            .ToArray();
        if (files.Length > _maximumArtifactsPerKind)
        {
            diagnostics.Add(ArenaExperimentPackCodec.Error(
                "artifact.count_limit",
                $"{directoryName}/store.json",
                "Artifact store exceeds its bounded file count."));
        }

        foreach (var path in files.Take(_maximumArtifactsPerKind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = $"{directoryName}/{Path.GetFileName(path)}";
            try
            {
                var info = new FileInfo(path);
                if (info.Length > _maximumPackBytes)
                {
                    diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.oversize", relativePath, "Artifact exceeds its bounded byte limit."));
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var decoded = decode(bytes, relativePath, _maximumPackBytes);
                diagnostics.AddRange(decoded.Diagnostics);
                if (decoded.Pack is not null)
                {
                    artifacts.Add(decoded.Pack);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(ArenaExperimentPackCodec.Error("artifact.read_failed", relativePath, "Artifact could not be read."));
            }
        }

        var duplicateIds = artifacts.GroupBy(item => item.Id, StringComparer.Ordinal).Where(group => group.Count() > 1);
        foreach (var group in duplicateIds)
        {
            diagnostics.Add(ArenaExperimentPackCodec.Error(
                "artifact.duplicate_id",
                $"{directoryName}/store.json",
                $"Artifact ID '{group.Key}' occurs more than once."));
        }

        return new(
            [.. artifacts.OrderBy(item => item.Id, StringComparer.Ordinal)],
            ArenaExperimentPackCodec.Sort(diagnostics));
    }

    private static bool HasSamePackVersion<T>(T left, T right)
        where T : IArenaVersionedContract =>
        (left, right) switch
        {
            (ArenaScenarioPackContract a, ArenaScenarioPackContract b) =>
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Version, b.Version, StringComparison.Ordinal),
            (ArenaBenchmarkPackContract a, ArenaBenchmarkPackContract b) =>
                string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Version, b.Version, StringComparison.Ordinal),
            _ => false
        };

    private static bool HasSameContentIdentity<T>(T left, T right)
        where T : IArenaVersionedContract =>
        (left, right) switch
        {
            (ArenaScenarioPackContract a, ArenaScenarioPackContract b) =>
                string.Equals(a.ContentFingerprint, b.ContentFingerprint, StringComparison.OrdinalIgnoreCase),
            (ArenaBenchmarkPackContract a, ArenaBenchmarkPackContract b) =>
                string.Equals(a.ContentFingerprint, b.ContentFingerprint, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool IsRepeatMigration<T>(T left, T right)
        where T : IArenaVersionedContract =>
        (left, right) switch
        {
            (ArenaScenarioPackContract a, ArenaScenarioPackContract b) =>
                SameMigrationIdentity(a.Migration, b.Migration)
                && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                && string.Equals(a.Version, b.Version, StringComparison.Ordinal)
                && string.Equals(a.ContentFingerprint, b.ContentFingerprint, StringComparison.OrdinalIgnoreCase),
            (ArenaBenchmarkPackContract a, ArenaBenchmarkPackContract b) =>
                SameMigrationIdentity(a.Migration, b.Migration)
                && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                && string.Equals(a.Version, b.Version, StringComparison.Ordinal)
                && string.Equals(a.ContentFingerprint, b.ContentFingerprint, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool SameMigrationIdentity(
        ArenaPackMigrationProvenance? left,
        ArenaPackMigrationProvenance? right) =>
        left is not null
        && right is not null
        && string.Equals(left.SourceSchema, right.SourceSchema, StringComparison.Ordinal)
        && string.Equals(left.SourceVersion, right.SourceVersion, StringComparison.Ordinal)
        && string.Equals(left.SourceContentFingerprint, right.SourceContentFingerprint, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.MigratorVersion, right.MigratorVersion, StringComparison.Ordinal);

    private static string RelativePath(string directoryName, string id)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return $"{directoryName}/{digest}.json";
    }

    private static ArenaArtifactWriteResult<T> Rejected<T>(
        T artifact,
        string relativePath,
        string code,
        string message)
        where T : class, IArenaVersionedContract =>
        new(ArenaArtifactWriteDisposition.Rejected, relativePath, null,
            [ArenaExperimentPackCodec.Error(code, relativePath, message)]);

    internal static async Task WriteAtomicallyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
