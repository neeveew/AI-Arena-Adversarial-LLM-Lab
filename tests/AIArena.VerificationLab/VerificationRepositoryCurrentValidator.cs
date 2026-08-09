using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;

namespace AIArena.VerificationLab;

internal sealed record VerificationRepositorySnapshot(
    string SourceRevision,
    string TreeFingerprint,
    bool IsWorkingTreeClean);

internal static class VerificationRepositoryCurrentValidator
{
    private const int MaximumGitOutputCharacters = 32 * 1024 * 1024;
    private const long MaximumSourceFileBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ImmutableArray<VerificationEvidenceIssue>> ValidateAsync(
        string repositoryRoot,
        ArenaQaEvidenceContract contract,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<VerificationEvidenceIssue>();
        string outerRoot;
        string mapRoot;
        try
        {
            outerRoot = Path.GetFullPath(repositoryRoot);
            mapRoot = Path.Combine(outerRoot, "map");
            if (!Directory.Exists(outerRoot) || !Directory.Exists(mapRoot))
            {
                issues.Add(new("current.repository_unavailable"));
                return Sort(issues);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            issues.Add(new("current.repository_unavailable"));
            return Sort(issues);
        }

        VerificationRepositorySnapshot outer;
        VerificationRepositorySnapshot map;
        try
        {
            outer = await CaptureAsync(outerRoot, excludeArtifacts: true, cancellationToken);
            map = await CaptureAsync(mapRoot, excludeArtifacts: false, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException)
        {
            issues.Add(new("current.repository_unavailable"));
            return Sort(issues);
        }

        var expectedMap = contract.NestedRepositories
            .FirstOrDefault(item => string.Equals(item.Id, "map", StringComparison.Ordinal));
        if (expectedMap is null)
        {
            issues.Add(new("current.map_provenance"));
        }
        else
        {
            if (!string.Equals(expectedMap.SourceRevision, map.SourceRevision, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("current.map_revision"));
            }
            if (!string.Equals(expectedMap.TreeFingerprint, map.TreeFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("current.map_fingerprint"));
            }
            if (expectedMap.IsWorkingTreeClean != map.IsWorkingTreeClean)
            {
                issues.Add(new("current.map_clean_state"));
            }
        }

        if (!string.Equals(contract.SourceRevision, outer.SourceRevision, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("current.outer_revision"));
        }

        var combinedFingerprint = Sha256Text(
            $"outer={outer.TreeFingerprint}\nmap={map.TreeFingerprint}\n");
        if (!string.Equals(contract.TreeFingerprint, combinedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("current.tree_fingerprint"));
        }

        var currentClean = outer.IsWorkingTreeClean && map.IsWorkingTreeClean;
        if (contract.IsWorkingTreeClean != currentClean)
        {
            issues.Add(new("current.clean_state"));
        }

        return Sort(issues);
    }

    internal static async Task<VerificationRepositorySnapshot> CaptureAsync(
        string repositoryRoot,
        bool excludeArtifacts,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var revision = (await RunGitAsync(root, ["rev-parse", "HEAD"], cancellationToken)).Trim().ToLowerInvariant();
        if (revision.Length is < 7 or > 64 || !revision.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Git revision is unavailable.");
        }

        var status = await RunGitAsync(
            root,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            cancellationToken);
        var clean = SplitLines(status).Count == 0;

        var pathsOutput = await RunGitAsync(
            root,
            ["-c", "core.quotepath=false", "ls-files", "--cached", "--others", "--exclude-standard"],
            cancellationToken);
        var paths = OrderRepositoryPaths(SplitLines(pathsOutput));
        var manifest = new StringBuilder();
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = relativePath.Replace('\\', '/');
            if (excludeArtifacts && normalized.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!IsSafeGitPath(normalized))
            {
                throw new InvalidDataException("Git returned an unsafe source path.");
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!IsWithinRoot(fullPath, root))
            {
                throw new InvalidDataException("Git returned an unsafe source path.");
            }
            if ((File.Exists(fullPath) || Directory.Exists(fullPath))
                && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Repository fingerprinting does not follow reparse points.");
            }
            var contentHash = File.Exists(fullPath)
                ? await Sha256FileAsync(fullPath, cancellationToken)
                : "deleted";
            manifest.Append(normalized).Append('\0').Append(contentHash).Append('\n');
        }

        return new(revision, Sha256Text(manifest.ToString()), clean);
    }

    internal static string[] OrderRepositoryPaths(IEnumerable<string> paths) =>
        [.. paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

    private static async Task<string> RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Git did not start.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);
        try
        {
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumGitOutputCharacters, timeout.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, 1024 * 1024, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("Git inspection failed.");
            return stdout;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Git inspection exceeded its deadline.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[16 * 1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (result.Length + read > maximumCharacters)
            {
                throw new InvalidDataException("Git output exceeds its bounded limit.");
            }
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumSourceFileBytes)
        {
            throw new InvalidDataException("Source file exceeds the bounded fingerprint limit.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static List<string> SplitLines(string value) =>
        [.. value.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)];

    private static bool IsSafeGitPath(string value) =>
        value.Length is > 0 and <= 32_000
        && !Path.IsPathRooted(value)
        && !value.Split('/').Any(segment => segment is "" or "." or "..");

    private static bool IsWithinRoot(string path, string root)
    {
        var rootWithSeparator = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ImmutableArray<VerificationEvidenceIssue> Sort(
        ImmutableArray<VerificationEvidenceIssue>.Builder issues) =>
        [.. issues.Distinct().OrderBy(issue => issue.Code, StringComparer.Ordinal)];
}
