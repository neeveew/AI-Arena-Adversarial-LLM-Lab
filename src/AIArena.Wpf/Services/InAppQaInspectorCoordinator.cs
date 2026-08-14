using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using AIArena.Core.Models;
using AIArena.Core.Services;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf.Services;

internal enum QaInspectorState
{
    Pass,
    Partial,
    Blocked,
    Unavailable
}

internal enum QaLocalSuite
{
    Core,
    Wpf,
    CodeIntelligence,
    VerificationLab,
    DependencyXamlSecurity,
    FullSeal
}

internal sealed record QaEvidenceChoice(string RelativePath, string Label, DateTimeOffset LastWriteAtUtc);

internal sealed record QaCurrentnessResult(QaInspectorState State, string Code, string Summary)
{
    public static QaCurrentnessResult Current() => new(QaInspectorState.Pass, "current", "Evidence matches the current repository trees.");
    public static QaCurrentnessResult Stale() => new(QaInspectorState.Blocked, "stale", "Evidence does not match the current repository trees.");
    public static QaCurrentnessResult Unavailable() => new(QaInspectorState.Unavailable, "unavailable", "Current repository comparison is unavailable.");
}

internal sealed record QaArtifactVerification(
    ArenaQaArtifact Artifact,
    QaInspectorState State,
    string Code,
    bool HashMatches,
    bool PngIsSane,
    long Bytes,
    long LastWriteTimeUtcTicks)
{
    public bool IsVerified => State == QaInspectorState.Pass && HashMatches;
}

internal sealed record QaEvidenceSnapshot(
    ArenaQaEvidenceContract Contract,
    string RelativeEvidencePath,
    [property: JsonIgnore] string EvidencePath,
    [property: JsonIgnore] string BundlePath,
    string EvidenceSha256,
    QaCurrentnessResult Currentness,
    ImmutableArray<QaArtifactVerification> Artifacts,
    QaInspectorState State,
    string Code,
    string Summary)
{
    public bool BundleIsValid => State is QaInspectorState.Pass or QaInspectorState.Partial
        && Artifacts.All(item => item.IsVerified);
}

internal sealed record QaEvidenceLoadResult(
    QaInspectorState State,
    string Code,
    string Summary,
    [property: JsonIgnore] QaEvidenceSnapshot? Snapshot)
{
    public static QaEvidenceLoadResult Unavailable(string code, string summary) =>
        new(QaInspectorState.Unavailable, code, summary, null);

    public static QaEvidenceLoadResult Blocked(string code, string summary) =>
        new(QaInspectorState.Blocked, code, summary, null);
}

internal sealed record QaArtifactPreview(
    string ArtifactId,
    string RelativePath,
    byte[] CurrentPng,
    string? BaselineArtifactId,
    string? BaselineRelativePath,
    byte[]? BaselinePng,
    string AutomationStatus);

internal sealed record QaReviewedScreenshot(string ArtifactId, string Sha256, bool Reviewed);

internal sealed record QaInspectionReviewManifest(
    string Schema,
    string EvidenceSha256,
    string TreeFingerprint,
    DateTimeOffset UpdatedAtUtc,
    ImmutableArray<QaReviewedScreenshot> Screenshots);

internal sealed record QaInspectionReviewHandle(
    QaInspectionReviewManifest Manifest,
    string RelativePath,
    [property: JsonIgnore] string Path,
    string Sha256);

internal sealed record QaSuiteCommand(string Label, string FileName, ImmutableArray<string> Arguments);

internal sealed record QaSuiteDefinition(
    QaLocalSuite Id,
    string Label,
    string Description,
    ImmutableArray<QaSuiteCommand> Commands,
    bool RequiresClosedApplication);

internal sealed record QaSuiteRunResult(
    QaInspectorState State,
    string Code,
    string Summary,
    int CompletedSteps,
    int FailedSteps,
    int TotalSteps,
    long DurationMilliseconds);

internal sealed record QaAcceptanceResult(QaInspectorState State, string Code, string Summary);

internal interface IQaEvidenceCurrentnessValidator
{
    Task<QaCurrentnessResult> ValidateAsync(
        string evidencePath,
        string repositoryRoot,
        CancellationToken cancellationToken);
}

internal interface IQaSuiteProcessRunner
{
    Task<QaSuiteRunResult> RunAsync(
        QaSuiteDefinition suite,
        string repositoryRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

internal interface IQaInspectionAcceptanceRunner
{
    Task<QaAcceptanceResult> AcceptAsync(
        string evidencePath,
        string repositoryRoot,
        string reviewedManifestPath,
        string reviewedManifestSha256,
        CancellationToken cancellationToken);
}

internal interface IQaInspectorClipboard
{
    bool TrySetText(string text);
}

internal static class QaLocalSuiteCatalog
{
    internal const string PostCloseSealCommand = "pwsh -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\\scripts\\qa-seal.ps1";

    private static readonly ImmutableArray<QaSuiteDefinition> Definitions =
    [
        Harness(QaLocalSuite.Core, "Core", "Runs the fixed Release Core harness.", "tests/AIArena.Tests/AIArena.Tests.csproj"),
        Harness(QaLocalSuite.Wpf, "WPF", "Runs the fixed Release WPF harness.", "tests/AIArena.Wpf.Tests/AIArena.Wpf.Tests.csproj"),
        Harness(QaLocalSuite.CodeIntelligence, "Code Intelligence", "Runs the fixed Release code-intelligence harness.", "tests/AIArena.CodeIntelligence.Tests/AIArena.CodeIntelligence.Tests.csproj"),
        Harness(QaLocalSuite.VerificationLab, "Verification Lab", "Runs the fixed Release verification harness.", "tests/AIArena.VerificationLab/AIArena.VerificationLab.csproj"),
        new(
            QaLocalSuite.DependencyXamlSecurity,
            "Dependency / XAML / security",
            "Runs only the checked-in dependency, XAML-ratchet, and release-security gates.",
            [
                PowerShell("Dependency index", "scripts/dependency-index.ps1", "-Check"),
                PowerShell("XAML inventory", "scripts/xaml-hardcoded-values.ps1", "-Check"),
                PowerShell("XAML inventory fixtures", "scripts/tests/xaml-hardcoded-values.tests.ps1"),
                PowerShell("Release security fixtures", "scripts/tests/release-security.tests.ps1")
            ],
            false),
        new(
            QaLocalSuite.FullSeal,
            "Full two-pass seal",
            "Requires the application to be closed so a clean Release build and rendered restart matrix can run.",
            [PowerShell("Full two-pass seal", "scripts/qa-seal.ps1")],
            true)
    ];

    internal static IReadOnlyList<QaSuiteDefinition> All => Definitions;

    internal static bool TryGet(QaLocalSuite id, out QaSuiteDefinition definition)
    {
        definition = Definitions.FirstOrDefault(item => item.Id == id)!;
        return definition is not null;
    }

    private static QaSuiteDefinition Harness(QaLocalSuite id, string label, string description, string project) =>
        new(
            id,
            label,
            description,
            [new QaSuiteCommand(
                label,
                "dotnet",
                ["run", "--project", project, "--no-build", "--no-restore", "-c", "Release"])],
            false);

    private static QaSuiteCommand PowerShell(string label, string script, params string[] arguments) =>
        new(label, "pwsh", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments]);
}

/// <summary>
/// Bounded, read-only repository for privacy-safe QA evidence. It never follows
/// reparse points and never returns raw artifact text.
/// </summary>
internal sealed class QaEvidenceRepository
{
    internal const int MaximumEvidenceBytes = 4 * 1024 * 1024;
    internal const int MaximumArtifactBytes = 64 * 1024 * 1024;
    internal const int MaximumArtifacts = 1024;
    internal const int MaximumRuns = 256;
    internal const long MaximumBundleBytes = 512L * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ReviewJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    internal const string ReviewManifestRelativePath = "metadata/in-app-inspection-review.json";
    private static readonly Regex RunSegment = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly string repositoryRoot;
    private readonly string qaRoot;
    private readonly IQaEvidenceCurrentnessValidator currentnessValidator;

    internal QaEvidenceRepository(string repositoryRoot, IQaEvidenceCurrentnessValidator currentnessValidator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.currentnessValidator = currentnessValidator ?? throw new ArgumentNullException(nameof(currentnessValidator));
        this.repositoryRoot = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        qaRoot = Path.Combine(this.repositoryRoot, "artifacts", "qa");
    }

    internal string RepositoryRoot => repositoryRoot;

    internal async Task<IReadOnlyList<QaEvidenceChoice>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!IsTrustedDirectory(repositoryRoot) || !IsTrustedDirectoryChain(qaRoot, repositoryRoot) || !Directory.Exists(qaRoot))
        {
            return [];
        }

        var directories = Directory.EnumerateDirectories(qaRoot).Take(MaximumRuns + 1).ToArray();
        if (directories.Length > MaximumRuns)
        {
            throw new InvalidDataException("qa.run_limit");
        }

        var choices = new List<QaEvidenceChoice>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!RunSegment.IsMatch(name) || !IsTrustedDirectoryChain(directory, qaRoot)) continue;
            var evidence = Path.Combine(directory, "qa-evidence.json");
            if (!IsTrustedFile(evidence, directory)) continue;
            var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(evidence), TimeSpan.Zero);
            choices.Add(new($"artifacts/qa/{name}/qa-evidence.json", name, updated));
        }

        await Task.CompletedTask;
        return choices
            .OrderByDescending(item => item.LastWriteAtUtc)
            .ThenByDescending(item => item.Label, StringComparer.Ordinal)
            .ToArray();
    }

    internal async Task<QaEvidenceLoadResult> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<QaEvidenceChoice> choices;
        try
        {
            choices = await ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return QaEvidenceLoadResult.Blocked("qa.run_limit", "The QA run index exceeded its bounded limit.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return QaEvidenceLoadResult.Unavailable("qa.index_unavailable", "QA evidence could not be indexed.");
        }

        return choices.Count == 0
            ? QaEvidenceLoadResult.Unavailable("qa.evidence_absent", "No local QA evidence bundle is available.")
            : await LoadAsync(choices[0].RelativePath, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<QaEvidenceLoadResult> LoadAsync(string relativeEvidencePath, CancellationToken cancellationToken = default)
    {
        if (!TryResolveEvidence(relativeEvidencePath, out var evidencePath, out var bundlePath))
        {
            return QaEvidenceLoadResult.Blocked("qa.evidence_path", "The selected QA evidence path is not trusted.");
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(evidencePath, MaximumEvidenceBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return QaEvidenceLoadResult.Blocked(ex.Message, "The selected QA evidence file failed bounded input validation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return QaEvidenceLoadResult.Unavailable("qa.evidence_read", "The selected QA evidence file could not be read.");
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return QaEvidenceLoadResult.Blocked("qa.evidence_bom", "QA evidence must be strict UTF-8 without a byte-order mark.");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return QaEvidenceLoadResult.Blocked("qa.evidence_utf8", "QA evidence is not valid UTF-8.");
        }

        if (!ArenaContractCodec.TryDeserialize<ArenaQaEvidenceContract>(json, out var contract, out var issues))
        {
            var codes = issues.Select(item => item.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToArray();
            var diagnostic = $"The QA evidence contract is invalid ({string.Join(", ", codes)}).";
            return QaEvidenceLoadResult.Blocked(
                "qa.contract_invalid",
                diagnostic[..Math.Min(300, diagnostic.Length)]);
        }

        var artifactChecks = await ValidateArtifactsAsync(bundlePath, contract!, cancellationToken).ConfigureAwait(false);
        QaCurrentnessResult currentness;
        try
        {
            currentness = await currentnessValidator.ValidateAsync(evidencePath, repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException
            or System.ComponentModel.Win32Exception)
        {
            currentness = QaCurrentnessResult.Unavailable();
        }
        var artifactFailure = artifactChecks.FirstOrDefault(item => !item.IsVerified);
        var state = artifactFailure is not null
            || currentness.State == QaInspectorState.Blocked
            || contract!.Verdict == ArenaQaVerdict.Blocked
            ? QaInspectorState.Blocked
            : currentness.State == QaInspectorState.Unavailable || contract.Verdict == ArenaQaVerdict.Partial
                ? QaInspectorState.Partial
                : QaInspectorState.Pass;
        var code = artifactFailure?.Code ?? (currentness.State == QaInspectorState.Blocked ? currentness.Code : "qa.loaded");
        var summary = artifactFailure is not null
            ? "QA evidence loaded, but one or more artifact hashes or formats are invalid."
            : currentness.State == QaInspectorState.Blocked
                ? currentness.Summary
                : "QA evidence loaded and validated within the available local evidence boundary.";
        var snapshot = new QaEvidenceSnapshot(
            contract!,
            NormalizeRelative(relativeEvidencePath),
            evidencePath,
            bundlePath,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            currentness,
            artifactChecks,
            state,
            code,
            summary);
        return new(state, code, summary, snapshot);
    }

    internal async Task<QaArtifactPreview?> LoadPreviewAsync(
        QaEvidenceSnapshot snapshot,
        string screenshotArtifactId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = snapshot.Artifacts.FirstOrDefault(item =>
            item.Artifact.Id.Equals(screenshotArtifactId, StringComparison.Ordinal)
            && item.Artifact.Kind == "rendered-ui-screenshot");
        if (current is null || !current.IsVerified || !current.PngIsSane) return null;

        var currentBytes = await ReadVerifiedArtifactAsync(snapshot.BundlePath, current.Artifact, requirePng: true, cancellationToken).ConfigureAwait(false);
        if (currentBytes is null) return null;

        byte[]? baselineBytes = null;
        string? baselineId = current.Artifact.Provenance?.BaselineArtifactId;
        string? baselinePath = null;
        if (baselineId is not null)
        {
            var baseline = snapshot.Artifacts.FirstOrDefault(item =>
                item.Artifact.Id.Equals(baselineId, StringComparison.Ordinal)
                && item.Artifact.Kind == "render-baseline");
            if (baseline is not null && baseline.IsVerified)
            {
                baselineBytes = await ReadVerifiedArtifactAsync(snapshot.BundlePath, baseline.Artifact, requirePng: true, cancellationToken).ConfigureAwait(false);
                if (baselineBytes is not null) baselinePath = baseline.Artifact.RelativePath;
            }
        }

        var automationId = current.Artifact.Provenance?.LinkedAutomationArtifactId;
        var automation = automationId is null
            ? null
            : snapshot.Artifacts.FirstOrDefault(item => item.Artifact.Id.Equals(automationId, StringComparison.Ordinal));
        var automationStatus = automation is { IsVerified: true }
            ? $"Linked automation verified: {automation.Artifact.Id}"
            : "Linked automation is unavailable or invalid.";
        return new(
            current.Artifact.Id,
            current.Artifact.RelativePath,
            currentBytes,
            baselineId,
            baselinePath,
            baselineBytes,
            automationStatus);
    }

    internal async Task<bool> ReviewBindingIsCurrentAsync(
        QaEvidenceSnapshot snapshot,
        bool verifyArtifactHashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var evidenceBytes = await ReadBoundedAsync(snapshot.EvidencePath, MaximumEvidenceBytes, cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexStringLower(SHA256.HashData(evidenceBytes)).Equals(snapshot.EvidenceSha256, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var verification in snapshot.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryResolveArtifact(snapshot.BundlePath, verification.Artifact.RelativePath, out var path)) return false;
                var info = new FileInfo(path);
                if (info.Length != verification.Bytes || info.LastWriteTimeUtc.Ticks != verification.LastWriteTimeUtcTicks) return false;
                if (!verifyArtifactHashes) continue;
                var requiresPng = verification.Artifact.Kind is "rendered-ui-screenshot" or "render-baseline";
                if (await ReadVerifiedArtifactAsync(snapshot.BundlePath, verification.Artifact, requiresPng, cancellationToken).ConfigureAwait(false) is null)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal async Task<QaInspectionReviewHandle?> WriteReviewManifestAsync(
        QaEvidenceSnapshot snapshot,
        IReadOnlySet<string> reviewedScreenshotIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reviewedScreenshotIds);
        if (!await ReviewBindingIsCurrentAsync(snapshot, verifyArtifactHashes: false, cancellationToken).ConfigureAwait(false)) return null;

        var screenshots = snapshot.Artifacts
            .Where(item => item.IsVerified && item.PngIsSane && item.Artifact.Kind == "rendered-ui-screenshot")
            .OrderBy(item => item.Artifact.Id, StringComparer.Ordinal)
            .Select(item => new QaReviewedScreenshot(
                item.Artifact.Id,
                item.Artifact.Sha256,
                reviewedScreenshotIds.Contains(item.Artifact.Id)))
            .ToImmutableArray();
        if (screenshots.IsDefaultOrEmpty || reviewedScreenshotIds.Any(id => screenshots.All(item => !item.ArtifactId.Equals(id, StringComparison.Ordinal))))
        {
            return null;
        }

        var manifest = new QaInspectionReviewManifest(
            "ai_arena.qa_inspection_review.v1",
            snapshot.EvidenceSha256,
            snapshot.Contract.TreeFingerprint,
            DateTimeOffset.UtcNow,
            screenshots);
        var json = JsonSerializer.Serialize(manifest, ReviewJsonOptions) + "\n";
        if (!ArenaContractPrivacyRules.InspectJson(json).IsEmpty) return null;
        var bytes = Encoding.UTF8.GetBytes(json);

        if (!TryResolveReviewManifest(snapshot.BundlePath, createMetadataDirectory: true, out var path)) return null;
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, $".in-app-inspection-review.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(directory, $".in-app-inspection-review.{Guid.NewGuid():N}.bak");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) return null;
            if (File.Exists(path))
            {
                if (!IsTrustedFile(path, snapshot.BundlePath)) return null;
                File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                File.Delete(backup);
            }
            else
            {
                File.Move(temporary, path);
            }
            if (!IsTrustedFile(path, snapshot.BundlePath)) return null;
            var persisted = await ReadBoundedAsync(path, MaximumEvidenceBytes, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(persisted));
            if (!persisted.AsSpan().SequenceEqual(bytes)) return null;
            return new(manifest, ReviewManifestRelativePath, path, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            TryDeleteTemporary(temporary, directory);
            TryDeleteTemporary(backup, directory);
        }
    }

    internal bool ClearReviewManifest(QaEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryResolveReviewManifest(snapshot.BundlePath, createMetadataDirectory: false, out var path)) return false;
        if (!File.Exists(path)) return true;
        if (!IsTrustedFile(path, snapshot.BundlePath)) return false;
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryResolveReviewManifest(string bundlePath, bool createMetadataDirectory, out string path)
    {
        path = "";
        try
        {
            if (!IsTrustedDirectory(bundlePath)) return false;
            var metadata = Path.Combine(bundlePath, "metadata");
            if (createMetadataDirectory && !Directory.Exists(metadata)) Directory.CreateDirectory(metadata);
            if (!IsTrustedDirectoryChain(metadata, bundlePath)) return false;
            path = Path.Combine(metadata, "in-app-inspection-review.json");
            return IsWithin(path, bundlePath)
                && (!File.Exists(path) || IsTrustedFile(path, bundlePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            path = "";
            return false;
        }
    }

    private static void TryDeleteTemporary(string path, string trustedDirectory)
    {
        try
        {
            if (File.Exists(path) && IsTrustedFile(path, trustedDirectory)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed cleanup keeps acceptance unavailable because no handle is returned.
        }
    }

    private async Task<ImmutableArray<QaArtifactVerification>> ValidateArtifactsAsync(
        string bundlePath,
        ArenaQaEvidenceContract contract,
        CancellationToken cancellationToken)
    {
        if (contract.Artifacts.Length > MaximumArtifacts)
        {
            return [new(
                new ArenaQaArtifact("artifact.limit", "validation", "metadata/limit", new string('0', 64), null),
                QaInspectorState.Blocked,
                "qa.artifact_limit",
                false,
                false,
                0,
                0)];
        }

        var results = ImmutableArray.CreateBuilder<QaArtifactVerification>(contract.Artifacts.Length);
        long totalBytes = 0;
        foreach (var artifact in contract.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveArtifact(bundlePath, artifact.RelativePath, out var path))
            {
                results.Add(new(artifact, QaInspectorState.Blocked, "qa.artifact_path", false, false, 0, 0));
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length is < 1 or > MaximumArtifactBytes || totalBytes + info.Length > MaximumBundleBytes)
                {
                    results.Add(new(artifact, QaInspectorState.Blocked, "qa.artifact_size", false, false, info.Length, info.LastWriteTimeUtc.Ticks));
                    continue;
                }
                totalBytes += info.Length;
                var bytes = await ReadBoundedAsync(path, MaximumArtifactBytes, cancellationToken).ConfigureAwait(false);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var hashMatches = hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
                var requiresPng = artifact.Kind is "rendered-ui-screenshot" or "render-baseline";
                var pngSane = !requiresPng || IsSanePng(bytes);
                results.Add(new(
                    artifact,
                    hashMatches && pngSane ? QaInspectorState.Pass : QaInspectorState.Blocked,
                    !hashMatches ? "qa.artifact_hash" : !pngSane ? "qa.artifact_png" : "qa.artifact_valid",
                    hashMatches,
                    pngSane,
                    bytes.LongLength,
                    info.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                results.Add(new(artifact, QaInspectorState.Blocked, "qa.artifact_read", false, false, 0, 0));
            }
        }
        return results.ToImmutable();
    }

    private async Task<byte[]?> ReadVerifiedArtifactAsync(
        string bundlePath,
        ArenaQaArtifact artifact,
        bool requirePng,
        CancellationToken cancellationToken)
    {
        if (!TryResolveArtifact(bundlePath, artifact.RelativePath, out var path)) return null;
        try
        {
            var bytes = await ReadBoundedAsync(path, MaximumArtifactBytes, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                && (!requirePng || IsSanePng(bytes))
                ? bytes
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private bool TryResolveEvidence(string relativePath, out string evidencePath, out string bundlePath)
    {
        evidencePath = "";
        bundlePath = "";
        var normalized = NormalizeRelative(relativePath);
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length != 4
            || !segments[0].Equals("artifacts", StringComparison.Ordinal)
            || !segments[1].Equals("qa", StringComparison.Ordinal)
            || !RunSegment.IsMatch(segments[2])
            || !segments[3].Equals("qa-evidence.json", StringComparison.Ordinal))
        {
            return false;
        }

        bundlePath = Path.Combine(qaRoot, segments[2]);
        evidencePath = Path.Combine(bundlePath, "qa-evidence.json");
        return IsTrustedDirectory(repositoryRoot)
            && IsTrustedDirectoryChain(qaRoot, repositoryRoot)
            && IsTrustedDirectoryChain(bundlePath, qaRoot)
            && IsTrustedFile(evidencePath, bundlePath);
    }

    private static bool TryResolveArtifact(string bundlePath, string relativePath, out string path)
    {
        path = "";
        if (!ArenaContractPrivacyRules.IsSafeRelativePath(relativePath)) return false;
        var normalized = NormalizeRelative(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(bundlePath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(candidate, bundlePath) || !IsTrustedFile(candidate, bundlePath)) return false;
        path = candidate;
        return true;
    }

    private static string NormalizeRelative(string value) => (value ?? "").Replace('\\', '/');

    private static bool IsTrustedDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTrustedDirectoryChain(string path, string trustedRoot)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!(full.Equals(root, StringComparison.OrdinalIgnoreCase) || IsWithin(full, root))) return false;
            var cursor = full;
            while (true)
            {
                if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) return false;
                if (cursor.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
                var parent = Directory.GetParent(cursor)?.FullName;
                if (parent is null) return false;
                cursor = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTrustedFile(string path, string trustedRoot)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full)
                && IsWithin(full, trustedRoot)
                && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0
                && IsTrustedDirectoryChain(Path.GetDirectoryName(full)!, trustedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < 1 || stream.Length > maximumBytes) throw new InvalidDataException("qa.evidence_size");
        var result = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        if (stream.Position != stream.Length) throw new InvalidDataException("qa.read_changed");
        return result;
    }

    internal static bool IsSanePng(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(signature)) return false;
        var offset = 8;
        var sawHeader = false;
        var sawData = false;
        var sawEnd = false;
        while (offset + 12 <= bytes.Length)
        {
            var length = ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > bytes.Length) return false;
            var chunkLength = (int)length;
            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, chunkLength);
            var expectedCrc = ReadUInt32BigEndian(bytes.Slice(offset + 8 + chunkLength, 4));
            if (PngCrc(type, data) != expectedCrc) return false;

            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || chunkLength != 13) return false;
                var width = ReadUInt32BigEndian(data[..4]);
                var height = ReadUInt32BigEndian(data.Slice(4, 4));
                if (width is < 320 or > 20_000 || height is < 240 or > 20_000) return false;
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (chunkLength != 0 || offset + 12 != bytes.Length) return false;
                sawEnd = true;
                break;
            }
            offset += 12 + chunkLength;
        }
        return sawHeader && sawData && sawEnd;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    private static uint PngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type) crc = PngCrcByte(crc, value);
        foreach (var value in data) crc = PngCrcByte(crc, value);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint PngCrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (var index = 0; index < 8; index++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }
}

internal sealed class ReleaseQaEvidenceCurrentnessValidator : IQaEvidenceCurrentnessValidator
{
    private const string TemporaryPrefix = "ai-arena-qa-currentness-";

    public async Task<QaCurrentnessResult> ValidateAsync(
        string evidencePath,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var project = Path.Combine(repositoryRoot, "tests", "AIArena.VerificationLab", "AIArena.VerificationLab.csproj");
        if (!IsRegularFile(project))
        {
            return Unavailable("qa.validator_source_unavailable", "Currentness validation source is unavailable.");
        }

        var outputRoot = Path.Combine(Path.GetTempPath(), TemporaryPrefix + Guid.NewGuid().ToString("N"));
        QaCurrentnessResult result = Unavailable("qa.validator_build", "A fresh currentness validator could not be built.");
        var cancelled = false;
        try
        {
            result = await ValidateFreshAsync(
                project,
                evidencePath,
                repositoryRoot,
                outputRoot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result = Unavailable("qa.validator_unavailable", "Fresh currentness validation was unavailable.");
        }
        finally
        {
            if (!TryDeleteTemporaryRoot(outputRoot))
            {
                result = Unavailable("qa.validator_cleanup", "The isolated currentness validator could not be cleaned safely.");
                cancelled = false;
            }
        }
        if (cancelled) throw new OperationCanceledException(cancellationToken);
        return result;
    }

    private static async Task<QaCurrentnessResult> ValidateFreshAsync(
        string project,
        string evidencePath,
        string repositoryRoot,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        if (!IsSafeTemporaryRoot(outputRoot)) return Unavailable("qa.validator_output", "The isolated validator output could not be trusted.");

        var packageSource = ResolveLocalPackageSource();
        if (packageSource is null) return Unavailable("qa.validator_package_source", "The trusted local package cache is unavailable.");
        var packageRoot = Path.Combine(outputRoot, "packages");
        var configPath = Path.Combine(outputRoot, "NuGet.Config");
        WriteOfflineNuGetConfig(configPath, packageSource);
        var restore = await BoundedQaProcess.RunAsync(
            "dotnet",
            [
                "restore",
                project,
                "--artifacts-path", outputRoot,
                "--packages", packageRoot,
                "--configfile", configPath,
                "--no-cache",
                "--force",
                "--disable-parallel",
                "--nologo",
                "--verbosity", "quiet",
                "-p:NuGetAudit=false"
            ],
            repositoryRoot,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (restore == BoundedQaProcessResult.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (restore != BoundedQaProcessResult.Passed
            || !TryCreateRestoreInputManifest(outputRoot, out var restoreManifest))
        {
            return Unavailable("qa.validator_restore", "The validator could not be restored from the trusted local-only package source.");
        }

        var build = await BoundedQaProcess.RunAsync(
            "dotnet",
            [
                "build",
                project,
                "--configuration", "Release",
                "--no-restore",
                "--artifacts-path", outputRoot,
                "--disable-build-servers",
                "--no-incremental",
                "--nologo",
                "--verbosity", "quiet",
                "-p:UseAppHost=false",
                "-p:NuGetAudit=false"
            ],
            repositoryRoot,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (build == BoundedQaProcessResult.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (build != BoundedQaProcessResult.Passed) return Unavailable("qa.validator_build", "A fresh currentness validator could not be built.");
        if (!RestoreInputManifestMatches(outputRoot, restoreManifest))
        {
            return Unavailable("qa.validator_restore_tampered", "The isolated restore inputs changed during the validator build.");
        }

        if (!TryCreateManifest(outputRoot, out var manifest, out var validatorDll))
        {
            return Unavailable("qa.validator_output", "The fresh validator output was incomplete or untrusted.");
        }
        if (!RestoreInputManifestMatches(outputRoot, restoreManifest)
            || !ManifestMatches(outputRoot, manifest, validatorDll))
        {
            return Unavailable("qa.validator_tampered", "The fresh validator output changed before execution.");
        }

        var validation = await BoundedQaProcess.RunAsync(
            "dotnet",
            [validatorDll, "--validate-evidence-current", evidencePath, repositoryRoot],
            repositoryRoot,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (validation == BoundedQaProcessResult.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (!RestoreInputManifestMatches(outputRoot, restoreManifest)
            || !ManifestMatches(outputRoot, manifest, validatorDll))
        {
            return Unavailable("qa.validator_tampered", "The fresh validator output changed during execution.");
        }
        return validation switch
        {
            BoundedQaProcessResult.Passed => QaCurrentnessResult.Current(),
            BoundedQaProcessResult.Unavailable => Unavailable("qa.validator_unavailable", "The fresh currentness validator could not start."),
            _ => QaCurrentnessResult.Stale()
        };
    }

    private static string? ResolveLocalPackageSource()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
                : configured;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0
                ? full
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteOfflineNuGetConfig(string path, string packageSource)
    {
        var document = new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("clear"),
                    new XElement("add", new XAttribute("key", "local-global-packages"), new XAttribute("value", packageSource)))));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            CloseOutput = false
        });
        document.Save(writer);
    }

    private static bool TryCreateRestoreInputManifest(string outputRoot, out ImmutableDictionary<string, string> manifest)
    {
        manifest = ImmutableDictionary<string, string>.Empty;
        try
        {
            var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(entry);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) continue;
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                var fileName = Path.GetFileName(full);
                var isPackage = relative.StartsWith("packages/", StringComparison.Ordinal);
                var isRestoreInput = relative.StartsWith("obj/", StringComparison.Ordinal)
                    && (fileName.Equals("project.assets.json", StringComparison.Ordinal)
                        || fileName.EndsWith(".nuget.g.props", StringComparison.Ordinal)
                        || fileName.EndsWith(".nuget.g.targets", StringComparison.Ordinal));
                if (!isPackage && !isRestoreInput && !relative.Equals("NuGet.Config", StringComparison.Ordinal)) continue;
                if (!ArenaContractPrivacyRules.IsSafeRelativePath(relative) || !builder.TryAdd(relative, HashFile(full))) return false;
            }
            if (!builder.Keys.Any(path => path.EndsWith("project.assets.json", StringComparison.Ordinal))
                || !builder.ContainsKey("NuGet.Config"))
            {
                return false;
            }
            manifest = builder.ToImmutable();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool RestoreInputManifestMatches(string outputRoot, ImmutableDictionary<string, string> expected) =>
        TryCreateRestoreInputManifest(outputRoot, out var current)
        && current.Count == expected.Count
        && expected.All(item => current.TryGetValue(item.Key, out var hash)
            && hash.Equals(item.Value, StringComparison.Ordinal));

    private static QaCurrentnessResult Unavailable(string code, string summary) =>
        new(QaInspectorState.Unavailable, code, summary);

    private static bool TryCreateManifest(
        string outputRoot,
        out ImmutableDictionary<string, string> manifest,
        out string validatorDll)
    {
        manifest = ImmutableDictionary<string, string>.Empty;
        validatorDll = "";
        try
        {
            var root = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + Path.DirectorySeparatorChar;
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var candidates = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(entry);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) continue;
                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (!ArenaContractPrivacyRules.IsSafeRelativePath(relative) || !builder.TryAdd(relative, HashFile(full))) return false;
                if (relative.StartsWith("bin/", StringComparison.Ordinal)
                    && Path.GetFileName(full).Equals("AIArena.VerificationLab.dll", StringComparison.Ordinal))
                {
                    candidates.Add(full);
                }
            }
            if (builder.Count == 0 || candidates.Count != 1) return false;
            validatorDll = Path.GetFullPath(candidates[0]);
            manifest = builder.ToImmutable();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ManifestMatches(string outputRoot, ImmutableDictionary<string, string> expected, string validatorDll)
    {
        if (!TryCreateManifest(outputRoot, out var current, out var currentDll)
            || !Path.GetFullPath(currentDll).Equals(Path.GetFullPath(validatorDll), StringComparison.OrdinalIgnoreCase)
            || current.Count != expected.Count)
        {
            return false;
        }
        return expected.All(item => current.TryGetValue(item.Key, out var hash)
            && hash.Equals(item.Value, StringComparison.Ordinal));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeTemporaryRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(temporary, StringComparison.OrdinalIgnoreCase) == true
                && Path.GetFileName(full).StartsWith(TemporaryPrefix, StringComparison.Ordinal)
                && Guid.TryParseExact(Path.GetFileName(full)[TemporaryPrefix.Length..], "N", out _)
                && Directory.Exists(full)
                && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryDeleteTemporaryRoot(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return true;
            if (!IsSafeTemporaryRoot(path)) return false;
            DeleteDirectoryWithoutFollowingReparsePoints(path);
            return !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteDirectoryWithoutFollowingReparsePoints(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectoryWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(path);
    }
}

internal sealed class FixedQaSuiteProcessRunner : IQaSuiteProcessRunner
{
    public async Task<QaSuiteRunResult> RunAsync(
        QaSuiteDefinition suite,
        string repositoryRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suite);
        var stopwatch = Stopwatch.StartNew();
        var completed = 0;
        foreach (var command in suite.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!QaSuiteCommandSafety.IsAllowed(suite.Id, command))
            {
                return Result(QaInspectorState.Blocked, "qa.suite_not_allowlisted", "The requested suite command is not allowlisted.", completed, 1, suite.Commands.Length, stopwatch);
            }
            progress?.Report($"Running {command.Label} ({completed + 1}/{suite.Commands.Length})");
            var result = await BoundedQaProcess.RunAsync(
                command.FileName,
                command.Arguments,
                repositoryRoot,
                TimeSpan.FromMinutes(15),
                cancellationToken).ConfigureAwait(false);
            if (result == BoundedQaProcessResult.Cancelled)
            {
                return Result(QaInspectorState.Blocked, "qa.suite_cancelled", "Focused QA execution was cancelled.", completed, 0, suite.Commands.Length, stopwatch);
            }
            if (result != BoundedQaProcessResult.Passed)
            {
                return Result(
                    result == BoundedQaProcessResult.Unavailable ? QaInspectorState.Unavailable : QaInspectorState.Blocked,
                    result == BoundedQaProcessResult.Unavailable ? "qa.suite_unavailable" : "qa.suite_failed",
                    result == BoundedQaProcessResult.Unavailable ? "A required fixed local test harness is unavailable." : "A focused QA step did not pass.",
                    completed,
                    1,
                    suite.Commands.Length,
                    stopwatch);
            }
            completed++;
        }
        return Result(QaInspectorState.Pass, "qa.suite_passed", "Every selected focused QA step passed.", completed, 0, suite.Commands.Length, stopwatch);
    }

    private static QaSuiteRunResult Result(
        QaInspectorState state,
        string code,
        string summary,
        int completed,
        int failed,
        int total,
        Stopwatch stopwatch) =>
        new(state, code, summary, completed, failed, total, Math.Max(0, stopwatch.ElapsedMilliseconds));
}

internal static class QaSuiteCommandSafety
{
    internal static bool IsAllowed(QaLocalSuite suite, QaSuiteCommand command)
    {
        if (!QaLocalSuiteCatalog.TryGet(suite, out var canonical)) return false;
        return canonical.Commands.Any(item =>
            item.FileName.Equals(command.FileName, StringComparison.Ordinal)
            && item.Arguments.SequenceEqual(command.Arguments, StringComparer.Ordinal));
    }
}

internal sealed class PowerShellQaInspectionAcceptanceRunner : IQaInspectionAcceptanceRunner
{
    internal static ImmutableArray<string> BuildArguments(
        string scriptPath,
        string evidencePath,
        string repositoryRoot,
        string reviewedManifestPath,
        string reviewedManifestSha256) =>
    [
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        scriptPath,
        "-EvidencePath",
        evidencePath,
        "-RepositoryRoot",
        repositoryRoot,
        "-AcceptanceSource",
        "InAppReviewedManifest",
        "-ReviewedManifestPath",
        reviewedManifestPath,
        "-ReviewedManifestSha256",
        reviewedManifestSha256
    ];

    public async Task<QaAcceptanceResult> AcceptAsync(
        string evidencePath,
        string repositoryRoot,
        string reviewedManifestPath,
        string reviewedManifestSha256,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(repositoryRoot, "scripts", "qa-accept-inspection.ps1");
        if (!File.Exists(script) || (File.GetAttributes(script) & FileAttributes.ReparsePoint) != 0)
        {
            return new(QaInspectorState.Unavailable, "qa.acceptance_unavailable", "The fixed inspection-acceptance script is unavailable.");
        }
        var result = await BoundedQaProcess.RunAsync(
            "pwsh",
            BuildArguments(script, evidencePath, repositoryRoot, reviewedManifestPath, reviewedManifestSha256),
            repositoryRoot,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        return result switch
        {
            BoundedQaProcessResult.Passed => new(QaInspectorState.Pass, "qa.acceptance_recorded", "Inspection acceptance was recorded by the authoritative script."),
            BoundedQaProcessResult.Cancelled => new(QaInspectorState.Blocked, "qa.acceptance_cancelled", "Inspection acceptance was cancelled."),
            BoundedQaProcessResult.Unavailable => new(QaInspectorState.Unavailable, "qa.acceptance_unavailable", "Inspection acceptance could not start."),
            _ => new(QaInspectorState.Blocked, "qa.acceptance_rejected", "The authoritative script rejected inspection acceptance.")
        };
    }
}

internal enum BoundedQaProcessResult
{
    Passed,
    Failed,
    Cancelled,
    Unavailable
}

internal static class BoundedQaProcess
{
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;

    internal static async Task<BoundedQaProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        process.StartInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true";
        process.StartInfo.Environment["NUGET_XMLDOC_MODE"] = "skip";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return BoundedQaProcessResult.Unavailable;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return BoundedQaProcessResult.Unavailable;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            var stdout = DrainAsync(process.StandardOutput, bounded.Token);
            var stderr = DrainAsync(process.StandardError, bounded.Token);
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0 ? BoundedQaProcessResult.Passed : BoundedQaProcessResult.Failed;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return cancellationToken.IsCancellationRequested
                ? BoundedQaProcessResult.Cancelled
                : BoundedQaProcessResult.Failed;
        }
        catch (InvalidDataException)
        {
            TryKill(process);
            return BoundedQaProcessResult.Failed;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > MaximumOutputCharacters) throw new InvalidDataException("qa.process_output_limit");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}

internal sealed class WpfQaInspectorClipboard : IQaInspectorClipboard
{
    public bool TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ThreadStateException)
        {
            return false;
        }
    }
}

/// <summary>
/// Deterministic QA Inspector orchestration. Methods are UI-independent enough
/// to be reused by a future qa.* control-plane adapter.
/// </summary>
internal sealed class InAppQaInspectorCoordinator : IDisposable
{
    private const int MaximumReportCharacters = 16 * 1024;
    private readonly InAppQaInspectorControl view;
    private readonly QaEvidenceRepository repository;
    private readonly IQaSuiteProcessRunner suiteRunner;
    private readonly IQaInspectionAcceptanceRunner acceptanceRunner;
    private readonly IQaInspectorClipboard clipboard;
    private readonly Func<bool> isApplicationRunning;
    private readonly bool readOnlyPreserveReviews;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private readonly object suiteSync = new();
    private CancellationTokenSource? suiteCancellation;
    private QaEvidenceSnapshot? current;
    private ImmutableHashSet<string> reviewedScreenshotIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private QaInspectionReviewHandle? reviewHandle;
    private bool disposed;

    internal static bool ShouldPreserveReviewsForIsolatedCapture(string dataRoot)
    {
        if (!AIArenaUiVerificationControlService.IsIsolatedQaDataRoot(dataRoot)) return false;
        try
        {
            var marker = Path.Combine(Path.GetFullPath(dataRoot), ".ai-arena-qa-owner");
            return File.Exists(marker)
                && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal InAppQaInspectorCoordinator(
        InAppQaInspectorControl view,
        string repositoryRoot,
        IQaEvidenceCurrentnessValidator? currentnessValidator = null,
        IQaSuiteProcessRunner? suiteRunner = null,
        IQaInspectionAcceptanceRunner? acceptanceRunner = null,
        IQaInspectorClipboard? clipboard = null,
        Func<bool>? isApplicationRunning = null,
        bool readOnlyPreserveReviews = false)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        repository = new QaEvidenceRepository(repositoryRoot, currentnessValidator ?? new ReleaseQaEvidenceCurrentnessValidator());
        this.suiteRunner = suiteRunner ?? new FixedQaSuiteProcessRunner();
        this.acceptanceRunner = acceptanceRunner ?? new PowerShellQaInspectionAcceptanceRunner();
        this.clipboard = clipboard ?? new WpfQaInspectorClipboard();
        this.isApplicationRunning = isApplicationRunning ?? (() => true);
        this.readOnlyPreserveReviews = readOnlyPreserveReviews;
        view.Initialize(this);
        view.SetReadOnlyMode(readOnlyPreserveReviews);
        view.SetSuiteChoices(QaLocalSuiteCatalog.All);
        view.SetPostCloseCommand(QaLocalSuiteCatalog.PostCloseSealCommand);
    }

    internal async Task<QaEvidenceLoadResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        await RefreshAsync(null, cancellationToken).ConfigureAwait(false);

    internal async Task<QaEvidenceLoadResult> RefreshAsync(
        string? relativeEvidencePath = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await view.Dispatcher.InvokeAsync(() => view.SetEvidenceBusy(true));
            return await RefreshCoreAsync(relativeEvidencePath, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await reviewGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ResetReviewState(current, deleteManifest: !readOnlyPreserveReviews);
                current = null;
            }
            finally
            {
                reviewGate.Release();
            }
            var result = QaEvidenceLoadResult.Unavailable("qa.refresh_cancelled", "QA evidence refresh was cancelled.");
            await view.Dispatcher.InvokeAsync(() =>
            {
                view.ApplyPresentation(QaInspectorPresentation.Create(result));
                view.SetPreview(null);
                view.SetReviewCancelled();
                view.SetAcceptanceAvailable(false);
            });
            return result;
        }
        finally
        {
            await view.Dispatcher.InvokeAsync(() => view.SetEvidenceBusy(false));
            operationGate.Release();
        }
    }

    private async Task<QaEvidenceLoadResult> RefreshCoreAsync(
        string? relativeEvidencePath,
        CancellationToken cancellationToken)
    {
        var knownScreenshotCount = current is { } existing
            && (string.IsNullOrWhiteSpace(relativeEvidencePath)
                || existing.RelativeEvidencePath.Equals(relativeEvidencePath.Replace('\\', '/'), StringComparison.Ordinal))
            ? CountScreenshots(existing)
            : 0;
        await view.Dispatcher.InvokeAsync(() =>
        {
            view.SetAcceptanceAvailable(false);
            view.SetPreview(null);
            view.SetReviewRevalidating(knownScreenshotCount);
        });
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetReviewState(current, deleteManifest: !readOnlyPreserveReviews);
            current = null;
        }
        finally
        {
            reviewGate.Release();
        }
        IReadOnlyList<QaEvidenceChoice> choices;
        try
        {
            choices = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            choices = [];
        }
        var explicitlyRequested = !string.IsNullOrWhiteSpace(relativeEvidencePath);
        var requested = explicitlyRequested
            ? choices.FirstOrDefault(choice => choice.RelativePath.Equals(relativeEvidencePath, StringComparison.Ordinal))?.RelativePath
            : choices.FirstOrDefault()?.RelativePath;
        var pickerSelection = explicitlyRequested ? relativeEvidencePath : requested;
        await view.Dispatcher.InvokeAsync(() => view.SetEvidenceChoices(choices, pickerSelection));
        var result = requested is null
            ? explicitlyRequested
                ? QaEvidenceLoadResult.Blocked("qa.evidence_not_indexed", "The selected QA evidence bundle is not present in the bounded local index.")
                : QaEvidenceLoadResult.Unavailable("qa.evidence_absent", "No local QA evidence bundle is available.")
            : await repository.LoadAsync(requested, cancellationToken).ConfigureAwait(false);
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = result.Snapshot;
            ResetReviewState(current, deleteManifest: !readOnlyPreserveReviews);
        }
        finally
        {
            reviewGate.Release();
        }
        var presentation = QaInspectorPresentation.Create(result);
        await view.Dispatcher.InvokeAsync(() =>
        {
            view.ApplyPresentation(presentation);
            view.SetPreview(null);
            view.SetReviewProgress(0, CountScreenshots(result.Snapshot), CountUnacceptedLimitations(result.Snapshot));
            view.SetAcceptanceAvailable(false);
        });
        return result;
    }

    internal async Task<QaSuiteRunResult> RunSuiteAsync(QaLocalSuite suite, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (readOnlyPreserveReviews)
        {
            return SetSuiteResult(new(
                QaInspectorState.Unavailable,
                "qa.capture_read_only",
                "Focused-suite execution is disabled in the isolated read-only QA capture process.",
                0,
                0,
                0,
                0));
        }
        if (!QaLocalSuiteCatalog.TryGet(suite, out var definition))
        {
            return SetSuiteResult(new(QaInspectorState.Blocked, "qa.suite_not_allowlisted", "The requested suite is not allowlisted.", 0, 0, 0, 0));
        }
        if (definition.RequiresClosedApplication && isApplicationRunning())
        {
            return SetSuiteResult(new(
                QaInspectorState.Unavailable,
                "qa.full_seal_requires_close",
                "Full sealing is unavailable while this application instance is running. Close it and use the post-close command shown below.",
                0,
                0,
                definition.Commands.Length,
                0));
        }
        CancellationTokenSource linked;
        lock (suiteSync)
        {
            if (suiteCancellation is not null)
            {
                return SetSuiteResult(new(QaInspectorState.Blocked, "qa.suite_busy", "Another focused QA suite is already running.", 0, 0, definition.Commands.Length, 0));
            }
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            suiteCancellation = linked;
        }

        await view.Dispatcher.InvokeAsync(() => view.SetSuiteBusy(true));
        try
        {
            var progress = new Progress<string>(message => view.Dispatcher.Invoke(() => view.SetSuiteProgress(message)));
            var result = await suiteRunner.RunAsync(definition, repository.RepositoryRoot, progress, linked.Token).ConfigureAwait(false);
            return SetSuiteResult(result);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return SetSuiteResult(new(QaInspectorState.Blocked, "qa.suite_cancelled", "Focused QA execution was cancelled.", 0, 0, definition.Commands.Length, 0));
        }
        finally
        {
            lock (suiteSync)
            {
                if (ReferenceEquals(suiteCancellation, linked)) suiteCancellation = null;
            }
            linked.Dispose();
            await view.Dispatcher.InvokeAsync(() => view.SetSuiteBusy(false));
        }
    }

    internal bool CancelSuite()
    {
        CancellationTokenSource? cancellation;
        lock (suiteSync)
        {
            if (disposed || suiteCancellation is null) return false;
            cancellation = suiteCancellation;
        }
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        return true;
    }

    internal async Task<QaArtifactPreview?> SelectScreenshotAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await reviewGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var snapshot = current;
            if (snapshot is null) return null;
            var preview = await repository.LoadPreviewAsync(snapshot, artifactId, linked.Token).ConfigureAwait(false);
            var displayed = await view.Dispatcher.InvokeAsync(() => view.SetPreview(preview));
            if (preview is null || !displayed || !ReferenceEquals(snapshot, current)) return preview;
            if (readOnlyPreserveReviews)
            {
                await view.Dispatcher.InvokeAsync(() =>
                {
                    view.SetReviewProgress(0, CountScreenshots(snapshot), CountUnacceptedLimitations(snapshot));
                    view.SetAcceptanceAvailable(false);
                    view.SetStatus("Read-only QA capture previewed this artifact without changing its inspection review manifest.");
                });
                return preview;
            }
            if (!await repository.ReviewBindingIsCurrentAsync(snapshot, verifyArtifactHashes: false, linked.Token).ConfigureAwait(false))
            {
                ResetReviewState(snapshot, deleteManifest: true);
                await view.Dispatcher.InvokeAsync(() =>
                {
                    view.SetReviewProgress(0, CountScreenshots(snapshot), CountUnacceptedLimitations(snapshot));
                    view.SetAcceptanceAvailable(false);
                    view.SetStatus("Rendered evidence changed; screenshot reviews were cleared.");
                });
                return null;
            }

            reviewedScreenshotIds = reviewedScreenshotIds.Add(artifactId);
            var handle = await repository.WriteReviewManifestAsync(snapshot, reviewedScreenshotIds, linked.Token).ConfigureAwait(false);
            if (handle is null)
            {
                ResetReviewState(snapshot, deleteManifest: true);
                await view.Dispatcher.InvokeAsync(() =>
                {
                    view.SetReviewProgress(0, CountScreenshots(snapshot), CountUnacceptedLimitations(snapshot));
                    view.SetAcceptanceAvailable(false);
                    view.SetStatus("The hash-bound screenshot review manifest could not be written; reviews were cleared.");
                });
                return null;
            }
            reviewHandle = handle;
            var ready = IsInspectionAcceptanceReady(snapshot, handle.Manifest);
            await view.Dispatcher.InvokeAsync(() =>
            {
                view.SetReviewProgress(reviewedScreenshotIds.Count, CountScreenshots(snapshot), CountUnacceptedLimitations(snapshot));
                view.SetAcceptanceAvailable(ready);
            });
            return preview;
        }
        finally
        {
            reviewGate.Release();
        }
    }

    internal string BuildPrivacySafeReport()
    {
        ThrowIfDisposed();
        var snapshot = current;
        if (snapshot is null) return "AI Arena - Lite QA Inspector\nEvidence: unavailable\n";
        var contract = snapshot.Contract;
        var builder = new StringBuilder();
        builder.AppendLine("AI Arena - Lite QA Inspector");
        builder.Append("Evidence: ").AppendLine(snapshot.RelativeEvidencePath);
        builder.Append("Verdict: ").AppendLine(contract.Verdict.ToString());
        builder.Append("Currentness: ").AppendLine(snapshot.Currentness.State.ToString());
        builder.Append("Source revision: ").AppendLine(contract.SourceRevision);
        builder.Append("Tree fingerprint: ").AppendLine(contract.TreeFingerprint);
        builder.Append("Clean tree: ").AppendLine(contract.IsWorkingTreeClean ? "yes" : "no");
        builder.Append("Clean full passes: ").AppendLine(contract.CleanFullPasses.ToString(CultureInfo.InvariantCulture));
        builder.Append("Gates: pass=").Append(contract.Gates.Count(item => item.Outcome == ArenaQaGateOutcome.Pass))
            .Append(" partial=").Append(contract.Gates.Count(item => item.Outcome == ArenaQaGateOutcome.Partial))
            .Append(" blocked=").Append(contract.Gates.Count(item => item.Outcome is ArenaQaGateOutcome.Blocked or ArenaQaGateOutcome.Fail))
            .Append(" unavailable=").AppendLine(contract.Gates.Count(item => item.Outcome == ArenaQaGateOutcome.Unavailable).ToString(CultureInfo.InvariantCulture));
        var counts = contract.Gates.Aggregate(new ArenaQaTestCounts(0, 0, 0, 0), (total, gate) => new(
            total.Passed + gate.Tests.Passed,
            total.Failed + gate.Tests.Failed,
            total.Skipped + gate.Tests.Skipped,
            total.Total + gate.Tests.Total));
        builder.Append("Tests: passed=").Append(counts.Passed).Append(" failed=").Append(counts.Failed)
            .Append(" skipped=").Append(counts.Skipped).Append(" total=").AppendLine(counts.Total.ToString(CultureInfo.InvariantCulture));
        builder.Append("Schemas: pass=").Append(contract.SchemaChecks.Count(item => item.Outcome == ArenaQaGateOutcome.Pass))
            .Append(" total=").AppendLine(contract.SchemaChecks.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append("Performance: met=").Append(contract.Performance.Count(MeetsThreshold))
            .Append(" total=").AppendLine(contract.Performance.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append("Artifacts: verified=").Append(snapshot.Artifacts.Count(item => item.IsVerified))
            .Append(" total=").AppendLine(snapshot.Artifacts.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var artifact in snapshot.Artifacts.OrderBy(item => item.Artifact.Id, StringComparer.Ordinal).Take(128))
        {
            builder.Append("- ").Append(artifact.Artifact.Id).Append(" | ").Append(artifact.Artifact.Kind)
                .Append(" | ").Append(artifact.Artifact.RelativePath).Append(" | ").Append(artifact.Artifact.Sha256)
                .Append(" | ").AppendLine(artifact.State.ToString());
        }
        builder.Append("Live-provider evidence: ").AppendLine(contract.LiveProviderCoverage.State.ToString());
        builder.Append("Accepted limitations: ").Append(contract.AcceptedLimitations.Count(item => item.UserAccepted))
            .Append('/').AppendLine(contract.AcceptedLimitations.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append("Inspection accepted: ").AppendLine(contract.Inspection.UserAccepted ? "yes" : "no");
        var report = builder.ToString();
        return report.Length <= MaximumReportCharacters ? report : report[..MaximumReportCharacters];
    }

    internal bool CopyReport()
    {
        var report = BuildPrivacySafeReport();
        view.SetReport(report);
        var copied = clipboard.TrySetText(report);
        view.SetStatus(copied ? "Privacy-safe aggregate report copied." : "Report is ready below; the clipboard is currently unavailable.");
        return copied;
    }

    internal bool CopyPostCloseCommand()
    {
        var copied = clipboard.TrySetText(QaLocalSuiteCatalog.PostCloseSealCommand);
        view.SetStatus(copied ? "Post-close seal command copied." : "The post-close command is selectable below; the clipboard is currently unavailable.");
        return copied;
    }

    internal async Task<QaAcceptanceResult> AcceptInspectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (readOnlyPreserveReviews)
        {
            var readOnly = new QaAcceptanceResult(
                QaInspectorState.Unavailable,
                "qa.capture_read_only",
                "Inspection acceptance is disabled in the isolated read-only QA capture process.");
            await view.Dispatcher.InvokeAsync(() =>
            {
                view.SetAcceptanceAvailable(false);
                view.SetStatus(readOnly.Summary);
            });
            return readOnly;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await view.Dispatcher.InvokeAsync(() => view.SetEvidenceBusy(true));
            await reviewGate.WaitAsync(linked.Token).ConfigureAwait(false);
            QaEvidenceSnapshot? snapshot;
            QaInspectionReviewHandle? reviewed;
            try
            {
                snapshot = current;
                reviewed = reviewHandle;
                if (snapshot is null
                    || reviewed is null
                    || !IsInspectionAcceptanceReady(snapshot, reviewed.Manifest)
                    || !await repository.ReviewBindingIsCurrentAsync(snapshot, verifyArtifactHashes: true, linked.Token).ConfigureAwait(false))
                {
                    ResetReviewState(snapshot, deleteManifest: true);
                    var rejected = new QaAcceptanceResult(QaInspectorState.Blocked, "qa.acceptance_preconditions", "Inspection acceptance is unavailable until every current screenshot has been explicitly previewed and the exact hash-bound review manifest is complete.");
                    await view.Dispatcher.InvokeAsync(() =>
                    {
                        view.SetReviewProgress(0, CountScreenshots(snapshot), CountUnacceptedLimitations(snapshot));
                        view.SetAcceptanceAvailable(false);
                        view.SetStatus(rejected.Summary);
                    });
                    return rejected;
                }
            }
            finally
            {
                reviewGate.Release();
            }

            if (snapshot is null || reviewed is null)
            {
                throw new InvalidOperationException("qa.acceptance_state");
            }
            var result = await acceptanceRunner.AcceptAsync(
                snapshot.EvidencePath,
                repository.RepositoryRoot,
                reviewed.Path,
                reviewed.Sha256,
                linked.Token).ConfigureAwait(false);
            await view.Dispatcher.InvokeAsync(() => view.SetStatus(result.Summary));
            if (result.State == QaInspectorState.Pass)
            {
                await RefreshCoreAsync(snapshot.RelativeEvidencePath, linked.Token).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            await view.Dispatcher.InvokeAsync(() => view.SetEvidenceBusy(false));
            operationGate.Release();
        }
    }

    internal static bool IsInspectionAcceptanceReady(
        QaEvidenceSnapshot? snapshot,
        QaInspectionReviewManifest? reviewManifest)
    {
        if (snapshot is null
            || !snapshot.BundleIsValid
            || snapshot.Currentness.State != QaInspectorState.Pass
            || !snapshot.Contract.IsWorkingTreeClean
            || !snapshot.Contract.SealManifestId.Equals(ArenaQaSealManifestV2.Id, StringComparison.Ordinal)
            || snapshot.Contract.CleanFullPasses < ArenaQaSealManifestV2.RequiredCleanPasses
            || snapshot.Contract.Inspection.UserAccepted
            || !snapshot.Contract.Environment.IsReleaseBuild
            || !snapshot.Contract.Environment.Configuration.Equals("Release", StringComparison.Ordinal)
            || snapshot.Contract.NestedRepositories.Any(item => !item.IsWorkingTreeClean)
            || (snapshot.Contract.LiveProviderCoverage.Required && snapshot.Contract.LiveProviderCoverage.State != ArenaEvidenceState.Observed)
            || !HasCanonicalUnacceptedLimitations(snapshot.Contract.AcceptedLimitations))
        {
            return false;
        }

        var gates = snapshot.Contract.Gates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var requiredGateIds = ArenaQaSealManifestV2.RequiredGateIds(snapshot.Contract.CleanFullPasses)
            .Where(id => !id.Equals("inspection.user-acceptance", StringComparison.Ordinal));
        if (requiredGateIds.Any(id => !gates.TryGetValue(id, out var gate)
            || !gate.Required
            || gate.Outcome != ArenaQaGateOutcome.Pass
            || gate.Evidence.State != ArenaEvidenceState.Observed)) return false;

        var schemas = snapshot.Contract.SchemaChecks.ToDictionary(item => item.Schema, StringComparer.Ordinal);
        if (ArenaQaSealManifestV2.RequiredSchemaIds.Any(id => !schemas.TryGetValue(id, out var schema)
            || schema.Outcome != ArenaQaGateOutcome.Pass
            || schema.Evidence.State != ArenaEvidenceState.Observed)) return false;
        if (!HasV2MigrationAuthority(snapshot, gates, schemas)
            || !HasV2FeatureMatrixArtifacts(snapshot)) return false;

        var performance = snapshot.Contract.Performance.GroupBy(item => item.Metric, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (ArenaQaSealManifestV2.RequiredPerformanceMetrics.Any(id => !performance.TryGetValue(id, out var metric) || !MeetsThreshold(metric))) return false;

        var screenshots = snapshot.Artifacts.Where(item => item.Artifact.Kind == "rendered-ui-screenshot").ToDictionary(item => item.Artifact.Id, StringComparer.Ordinal);
        var automations = snapshot.Artifacts.Where(item => item.Artifact.Kind == "automation-tree").ToDictionary(item => item.Artifact.Id, StringComparer.Ordinal);
        if (snapshot.Contract.Inspection.ScreenshotArtifactIds.IsDefaultOrEmpty
            || snapshot.Contract.Inspection.AutomationArtifactIds.IsDefaultOrEmpty
            || snapshot.Contract.Inspection.ScreenshotArtifactIds.Any(id => !screenshots.ContainsKey(id))
            || snapshot.Contract.Inspection.AutomationArtifactIds.Any(id => !automations.TryGetValue(id, out var item)
                || !item.IsVerified
                || item.Artifact.Provenance?.TreeFingerprint.Equals(snapshot.Contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase) != true))
        {
            return false;
        }

        var linkedVisualsAreValid = snapshot.Contract.Inspection.ScreenshotArtifactIds
            .Select(id => screenshots[id])
            .All(item => item.IsVerified
                && item.PngIsSane
                && item.Artifact.Provenance is { } provenance
                && provenance.TreeFingerprint.Equals(snapshot.Contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase)
                && provenance.LinkedAutomationArtifactId is { } automationId
                && automations.TryGetValue(automationId, out var automation)
                && automation.IsVerified
                && automation.Artifact.Provenance?.TreeFingerprint.Equals(snapshot.Contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase) == true);
        return linkedVisualsAreValid && ReviewManifestMatches(snapshot, reviewManifest);
    }

    internal static bool ReviewManifestMatches(QaEvidenceSnapshot snapshot, QaInspectionReviewManifest? manifest)
    {
        if (manifest is null
            || !manifest.Schema.Equals("ai_arena.qa_inspection_review.v1", StringComparison.Ordinal)
            || !manifest.EvidenceSha256.Equals(snapshot.EvidenceSha256, StringComparison.Ordinal)
            || !manifest.TreeFingerprint.Equals(snapshot.Contract.TreeFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var expected = snapshot.Artifacts
            .Where(item => item.Artifact.Kind == "rendered-ui-screenshot")
            .OrderBy(item => item.Artifact.Id, StringComparer.Ordinal)
            .Select(item => (item.Artifact.Id, item.Artifact.Sha256))
            .ToArray();
        var actual = manifest.Screenshots
            .OrderBy(item => item.ArtifactId, StringComparer.Ordinal)
            .ToArray();
        return expected.Length > 0
            && actual.Length == expected.Length
            && actual.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() == actual.Length
            && expected.Zip(actual).All(pair => pair.First.Id.Equals(pair.Second.ArtifactId, StringComparison.Ordinal)
                && pair.First.Sha256.Equals(pair.Second.Sha256, StringComparison.Ordinal)
                && pair.Second.Reviewed);
    }

    private static int CountScreenshots(QaEvidenceSnapshot? snapshot) =>
        snapshot?.Artifacts.Count(item => item.Artifact.Kind == "rendered-ui-screenshot") ?? 0;

    private static int CountUnacceptedLimitations(QaEvidenceSnapshot? snapshot) =>
        snapshot?.Contract.AcceptedLimitations.Count(item => !item.UserAccepted) ?? 0;

    private static bool HasCanonicalUnacceptedLimitations(ImmutableArray<ArenaQaAcceptedLimitation> limitations)
    {
        var requirements = ArenaQaSealManifestV1.RequiredLimitations;
        if (limitations.Length != requirements.Length) return false;
        var byId = limitations
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return requirements.All(requirement => byId.TryGetValue(requirement.Id, out var matches)
            && matches.Length == 1
            && !matches[0].UserAccepted
            && matches[0].Summary.Equals(requirement.Summary, StringComparison.Ordinal)
            && matches[0].Evidence.Id.Equals(requirement.EvidenceId, StringComparison.Ordinal)
            && matches[0].Evidence.State == ArenaEvidenceState.Unavailable
            && matches[0].Evidence.Summary.Equals(requirement.EvidenceSummary, StringComparison.Ordinal)
            && matches[0].Evidence.ReferenceId?.Equals(requirement.ReferenceId, StringComparison.Ordinal) == true
            && matches[0].Evidence.Basis is null
            && matches[0].Evidence.Limitation?.Equals(requirement.EvidenceLimitation, StringComparison.Ordinal) == true);
    }

    private static bool HasV2MigrationAuthority(
        QaEvidenceSnapshot snapshot,
        IReadOnlyDictionary<string, ArenaQaGateEvidence> gates,
        IReadOnlyDictionary<string, ArenaQaSchemaCheck> schemas)
    {
        if (!gates.TryGetValue(ArenaQaSealManifestV2.ExplicitMigrationGateId, out var gate)
            || !gate.Required
            || gate.Outcome != ArenaQaGateOutcome.Pass
            || gate.Evidence.State != ArenaEvidenceState.Observed
            || gate.Evidence.ReferenceId != ArenaQaSealManifestV2.ExplicitMigrationArtifactId)
        {
            return false;
        }

        var artifacts = snapshot.Artifacts
            .Where(item => item.Artifact.Id == ArenaQaSealManifestV2.ExplicitMigrationArtifactId)
            .ToArray();
        if (artifacts.Length != 1
            || !artifacts[0].IsVerified
            || artifacts[0].Artifact.Kind != ArenaQaSealManifestV2.ExplicitMigrationArtifactKind
            || artifacts[0].Artifact.RelativePath != ArenaQaSealManifestV2.ExplicitMigrationArtifactPath)
        {
            return false;
        }

        return HasV2MigrationSchema(
                schemas,
                ArenaContractSchemas.ScenarioPack,
                ArenaQaSealManifestV2.ScenarioPackV0Schema,
                ArenaQaSealManifestV2.ScenarioMigrationEvidenceId)
            && HasV2MigrationSchema(
                schemas,
                ArenaContractSchemas.BenchmarkPack,
                ArenaQaSealManifestV2.BenchmarkPackV0Schema,
                ArenaQaSealManifestV2.BenchmarkMigrationEvidenceId);
    }

    private static bool HasV2MigrationSchema(
        IReadOnlyDictionary<string, ArenaQaSchemaCheck> schemas,
        string schema,
        string migratedFromSchema,
        string evidenceId) =>
        schemas.TryGetValue(schema, out var check)
        && check.Outcome == ArenaQaGateOutcome.Pass
        && check.MigratedFromSchema == migratedFromSchema
        && check.Evidence.State == ArenaEvidenceState.Observed
        && check.Evidence.Id == evidenceId
        && check.Evidence.ReferenceId == ArenaQaSealManifestV2.ExplicitMigrationArtifactId;

    private static bool HasV2FeatureMatrixArtifacts(QaEvidenceSnapshot snapshot)
    {
        var artifacts = snapshot.Artifacts
            .Where(item => item.Artifact.Kind == ArenaQaSealManifestV2.FeatureSurfaceMatrixArtifactKind)
            .ToDictionary(item => item.Artifact.Id, StringComparer.Ordinal);
        for (var pass = 1; pass <= snapshot.Contract.CleanFullPasses; pass++)
        {
            var id = $"artifact.pass-{pass:D2}.feature-surface-matrix";
            var path = $"metadata/pass-{pass:D2}.feature-surface-matrix.json";
            if (!artifacts.TryGetValue(id, out var artifact)
                || !artifact.IsVerified
                || artifact.Artifact.RelativePath != path)
            {
                return false;
            }
        }
        return artifacts.Count == snapshot.Contract.CleanFullPasses;
    }

    private void ResetReviewState(QaEvidenceSnapshot? snapshot, bool deleteManifest)
    {
        if (deleteManifest && !readOnlyPreserveReviews && snapshot is not null) repository.ClearReviewManifest(snapshot);
        reviewedScreenshotIds = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
        reviewHandle = null;
    }

    private static bool MeetsThreshold(ArenaQaPerformanceMeasurement item) =>
        item.ThresholdKind == ArenaQaThresholdKind.Maximum ? item.Value <= item.Threshold : item.Value >= item.Threshold;

    private QaSuiteRunResult SetSuiteResult(QaSuiteRunResult result)
    {
        if (!disposed) view.Dispatcher.Invoke(() => view.SetSuiteResult(result));
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lock (suiteSync)
        {
            try { suiteCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        lifetime.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}

internal sealed record QaGatePresentation(string Id, string State, string Tests, string Duration, string HelpText);
internal sealed record QaSchemaPresentation(string Schema, string State, string Migration, string HelpText);
internal sealed record QaPerformancePresentation(string Metric, string Measurement, string State, string HelpText);
internal sealed record QaLimitationPresentation(string Id, string State, string Summary);
internal sealed record QaArtifactPresentation(string Id, string Kind, string RelativePath, string Sha256, string State, string HelpText);
internal sealed record QaSummaryCardPresentation(string Kind, string Title, string Reason, string Details)
{
    public string AutomationName => $"{Kind} QA evidence: {Title}";
    public string DetailsAutomationName => $"{Title} evidence details";
}

internal sealed record QaInspectorPresentation(
    QaInspectorState State,
    string Status,
    string Verdict,
    string Provenance,
    string Environment,
    string TestTotals,
    string LiveProvider,
    string Inspection,
    IReadOnlyList<QaSummaryCardPresentation> SummaryCards,
    IReadOnlyList<QaGatePresentation> Gates,
    IReadOnlyList<QaSchemaPresentation> Schemas,
    IReadOnlyList<QaPerformancePresentation> Performance,
    IReadOnlyList<QaLimitationPresentation> Limitations,
    IReadOnlyList<QaArtifactPresentation> Artifacts)
{
    internal static QaInspectorPresentation Create(QaEvidenceLoadResult result)
    {
        if (result.Snapshot is not { } snapshot)
        {
            return new(
                result.State,
                result.Summary,
                StateLabel(result.State),
                "Source evidence unavailable.",
                "Environment unavailable.",
                "Tests unavailable.",
                "Live-provider evidence unavailable.",
                "Inspection unavailable.",
                [new QaSummaryCardPresentation(
                    CardKind(result.State),
                    "Evidence bundle",
                    result.Summary,
                    $"State: {StateLabel(result.State)}{System.Environment.NewLine}Source, environment, tests, and inspection evidence are unavailable for this selection.")],
                [], [], [], [], []);
        }
        var contract = snapshot.Contract;
        var totals = contract.Gates.Aggregate(new ArenaQaTestCounts(0, 0, 0, 0), (value, gate) => new(
            value.Passed + gate.Tests.Passed,
            value.Failed + gate.Tests.Failed,
            value.Skipped + gate.Tests.Skipped,
            value.Total + gate.Tests.Total));
        var summaryCards = CreateSummaryCards(result, snapshot, totals);
        return new(
            result.State,
            result.Summary,
            $"{StateLabel(result.State)} · contract {contract.Verdict} · {contract.CleanFullPasses} clean pass(es)",
            $"revision {contract.SourceRevision} · tree {contract.TreeFingerprint} · recorded clean {(contract.IsWorkingTreeClean ? "yes" : "no")} · currentness {StateLabel(snapshot.Currentness.State)}",
            $"{contract.Environment.Configuration} · {contract.Environment.OperatingSystem} · {contract.Environment.Architecture} · .NET {contract.Environment.RuntimeVersion} · SDK {contract.Environment.SdkVersion}",
            $"{totals.Passed} passed · {totals.Failed} failed · {totals.Skipped} skipped · {totals.Total} total",
            LiveCoverage(contract.LiveProviderCoverage),
            contract.Inspection.UserAccepted
                ? $"Accepted at {contract.Inspection.AcceptedAtUtc:O}; {contract.Inspection.ScreenshotArtifactIds.Length} screenshot(s), {contract.Inspection.AutomationArtifactIds.Length} automation artifact(s)."
                : $"Not accepted; {contract.Inspection.ScreenshotArtifactIds.Length} screenshot(s), {contract.Inspection.AutomationArtifactIds.Length} automation artifact(s) currently referenced.",
            summaryCards,
            contract.Gates.Select(gate => new QaGatePresentation(
                gate.Id,
                GateLabel(gate.Outcome),
                $"{gate.Tests.Passed}/{gate.Tests.Total} passed · {gate.Tests.Failed} failed · {gate.Tests.Skipped} skipped",
                $"{gate.DurationMilliseconds} ms",
                $"{gate.Id}. {GateLabel(gate.Outcome)}. Required {(gate.Required ? "yes" : "no")}. Evidence {gate.Evidence.State}.")).ToArray(),
            contract.SchemaChecks.Select(schema => new QaSchemaPresentation(
                schema.Schema,
                GateLabel(schema.Outcome),
                schema.MigratedFromSchema is null ? "Direct validation" : $"Migrated from {schema.MigratedFromSchema}",
                $"{schema.Schema}. {GateLabel(schema.Outcome)}. Evidence {schema.Evidence.State}.")).ToArray(),
            contract.Performance.Select(item =>
            {
                var met = MeetsThreshold(item);
                var comparison = item.ThresholdKind == ArenaQaThresholdKind.Maximum ? "maximum" : "minimum";
                return new QaPerformancePresentation(
                    item.Metric,
                    $"{item.Value.ToString(CultureInfo.InvariantCulture)} {item.Unit} · {comparison} {item.Threshold.ToString(CultureInfo.InvariantCulture)}",
                    met ? "PASS" : "BLOCKED",
                    $"Observed {item.Metric}; threshold {(met ? "met" : "not met")}.");
            }).ToArray(),
            contract.AcceptedLimitations.Select(item => new QaLimitationPresentation(
                item.Id,
                item.UserAccepted ? "ACCEPTED" : "NOT ACCEPTED",
                item.Summary)).ToArray(),
            snapshot.Artifacts.Select(item => new QaArtifactPresentation(
                item.Artifact.Id,
                item.Artifact.Kind,
                item.Artifact.RelativePath,
                item.Artifact.Sha256,
                StateLabel(item.State),
                $"{item.Artifact.Id}. {item.Artifact.Kind}. Hash {(item.HashMatches ? "verified" : "invalid")}. Relative path {item.Artifact.RelativePath}.")).ToArray());
    }

    internal static string CardKind(QaInspectorState state) => state switch
    {
        QaInspectorState.Pass => "Ready",
        QaInspectorState.Partial => "Partial",
        QaInspectorState.Blocked => "Blocked",
        _ => "Unavailable"
    };

    private static IReadOnlyList<QaSummaryCardPresentation> CreateSummaryCards(
        QaEvidenceLoadResult result,
        QaEvidenceSnapshot snapshot,
        ArenaQaTestCounts totals)
    {
        var contract = snapshot.Contract;
        var newline = System.Environment.NewLine;
        var cards = new List<QaSummaryCardPresentation>
        {
            new(
                CardKind(result.State),
                "Evidence readiness",
                result.Summary,
                $"Contract verdict: {contract.Verdict}{newline}Clean passes: {contract.CleanFullPasses}{newline}Tests: {totals.Passed} passed, {totals.Failed} failed, {totals.Skipped} skipped, {totals.Total} total."),
            new(
                CardKind(snapshot.Currentness.State),
                "Repository currentness",
                snapshot.Currentness.Summary,
                $"Currentness code: {snapshot.Currentness.Code}{newline}Recorded tree fingerprint: {contract.TreeFingerprint}{newline}Recorded working tree clean: {(contract.IsWorkingTreeClean ? "yes" : "no")}."),
            new(
                EvidenceKind(contract.LiveProviderCoverage.State),
                "Live-provider coverage",
                LiveCoverageReason(contract.LiveProviderCoverage),
                LiveCoverage(contract.LiveProviderCoverage)),
            new(
                InspectionKind(contract.Inspection),
                "Rendered inspection",
                contract.Inspection.UserAccepted
                    ? "The recorded inspection is explicitly accepted."
                    : "The recorded inspection has not been explicitly accepted.",
                contract.Inspection.UserAccepted
                    ? $"Accepted at: {contract.Inspection.AcceptedAtUtc:O}{newline}Screenshots: {contract.Inspection.ScreenshotArtifactIds.Length}{newline}Automation artifacts: {contract.Inspection.AutomationArtifactIds.Length}{newline}Evidence: {contract.Inspection.Evidence.State}."
                    : $"Screenshots referenced: {contract.Inspection.ScreenshotArtifactIds.Length}{newline}Automation artifacts referenced: {contract.Inspection.AutomationArtifactIds.Length}{newline}Evidence: {contract.Inspection.Evidence.State}.")
        };

        foreach (var group in contract.Gates
                     .GroupBy(gate => gate.Outcome)
                     .OrderBy(group => GateSummaryOrder(group.Key)))
        {
            var gates = group.OrderBy(gate => gate.Id, StringComparer.Ordinal).ToArray();
            var kind = GateCardKind(group.Key);
            var title = kind switch
            {
                "Ready" => "Ready gates",
                "Failed" => "Failed gates",
                "Blocked" => "Blocked gates",
                "Partial" => "Partial gates",
                _ => "Unavailable gates"
            };
            var reason = $"{gates.Length} gate{(gates.Length == 1 ? "" : "s")} reported {GateLabel(group.Key).ToLowerInvariant()}.";
            var details = string.Join(newline, gates.Select(gate =>
                $"{gate.Id}: {GateLabel(gate.Outcome)}; required {(gate.Required ? "yes" : "no")}; evidence {gate.Evidence.State}; tests {gate.Tests.Passed}/{gate.Tests.Total} passed, {gate.Tests.Failed} failed, {gate.Tests.Skipped} skipped."));
            cards.Add(new(kind, title, reason, details));
        }

        return cards;
    }

    private static string EvidenceKind(ArenaEvidenceState state) => state switch
    {
        ArenaEvidenceState.Observed => "Ready",
        ArenaEvidenceState.Inferred => "Partial",
        _ => "Unavailable"
    };

    private static string InspectionKind(ArenaQaInspectionEvidence inspection)
    {
        if (inspection.Evidence.State == ArenaEvidenceState.Unavailable) return "Unavailable";
        return inspection.UserAccepted && inspection.Evidence.State == ArenaEvidenceState.Observed
            ? "Ready"
            : "Partial";
    }

    private static string GateCardKind(ArenaQaGateOutcome outcome) => outcome switch
    {
        ArenaQaGateOutcome.Pass => "Ready",
        ArenaQaGateOutcome.Fail => "Failed",
        ArenaQaGateOutcome.Partial => "Partial",
        ArenaQaGateOutcome.Blocked => "Blocked",
        _ => "Unavailable"
    };

    private static int GateSummaryOrder(ArenaQaGateOutcome outcome) => outcome switch
    {
        ArenaQaGateOutcome.Fail => 0,
        ArenaQaGateOutcome.Blocked => 1,
        ArenaQaGateOutcome.Partial => 2,
        ArenaQaGateOutcome.Unavailable => 3,
        _ => 4
    };

    private static string LiveCoverageReason(ArenaQaLiveProviderCoverage value) => value.State switch
    {
        ArenaEvidenceState.Observed => "Live-provider coverage is observed within the recorded evidence boundary.",
        ArenaEvidenceState.Inferred => "Live-provider coverage is inferred, not directly observed.",
        _ => "Live-provider coverage is unavailable."
    };

    private static string StateLabel(QaInspectorState state) => state switch
    {
        QaInspectorState.Pass => "PASS",
        QaInspectorState.Partial => "PARTIAL",
        QaInspectorState.Blocked => "BLOCKED",
        _ => "UNAVAILABLE"
    };

    private static string GateLabel(ArenaQaGateOutcome outcome) => outcome switch
    {
        ArenaQaGateOutcome.Pass => "PASS",
        ArenaQaGateOutcome.Partial => "PARTIAL",
        ArenaQaGateOutcome.Unavailable => "UNAVAILABLE",
        ArenaQaGateOutcome.Blocked => "BLOCKED",
        _ => "FAILED"
    };

    private static string LiveCoverage(ArenaQaLiveProviderCoverage value)
    {
        var prefix = $"{value.State} · required {(value.Required ? "yes" : "no")} · {value.ProviderProfileIds.Length} profile(s) · {value.EvidenceRunIds.Length} evidence run(s)";
        return value.Limitation is null ? prefix : $"{prefix}. Limitation: {value.Limitation}";
    }

    private static bool MeetsThreshold(ArenaQaPerformanceMeasurement item) =>
        item.ThresholdKind == ArenaQaThresholdKind.Maximum ? item.Value <= item.Threshold : item.Value >= item.Threshold;
}
