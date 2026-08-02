using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIArena.Core.Models;

namespace AIArena.Core.Services;

/// <summary>
/// Persists bounded repair outcomes only. Callers decide when an explicitly
/// approved repair/verification loop is complete; this store never records
/// commands, diffs, source text, or absolute paths.
/// </summary>
public sealed class DotNetSolutionDoctorHistoryStore
{
    public const string Schema = "ai-arena.solution-doctor-history.v1";
    public const string HistoryRelativePath = ".ai-arena/analysis/solution-doctor-history.json";
    private const int MaxRuns = 64;
    private const int MaxTransitions = 64;
    private const int MaxAffectedPaths = 64;
    private const int MaxTransitionPaths = 32;
    private const int MaxFailingNames = 32;
    private const int MaxObservations = 64;
    private const long MaxHistoryBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly Regex SecretLikeMetadataRegex = new(
        """
        (?:
            -----BEGIN\s+(?:RSA\s+|EC\s+|OPENSSH\s+)?PRIVATE\s+KEY-----
          | \b(?:authorization|proxy-authorization)\s*[:=]\s*["']?\S+
          | \bbearer\s+[A-Za-z0-9._~+/=-]{8,}
          | \b(?:sk-(?:proj-|live-|test-)?[A-Za-z0-9_-]{12,}
              |gh[pousr]_[A-Za-z0-9]{16,}
              |github_pat_[A-Za-z0-9_]{20,}
              |(?:AKIA|ASIA)[0-9A-Z]{16}
              |AIza[0-9A-Za-z_-]{20,}
              |xox[baprs]-[0-9A-Za-z-]{12,}
              |hf_[0-9A-Za-z]{20,})\b
          | \beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b
          | \b(?:api[_\s-]?key
              |password|passwd|pwd|secret
              |client[_\s-]?secret
              |access[_\s-]?token|refresh[_\s-]?token
              |connection[_\s-]?string
              |account[_\s-]?key
              |private[_\s-]?key
              |aws[_\s-]?(?:secret[_\s-]?access[_\s-]?key|session[_\s-]?token)
              |shared[_\s-]?access[_\s-]?signature)\s*[:=]\s*["']?\S+
        )
        """,
        RegexOptions.Compiled
        | RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase
        | RegexOptions.IgnorePatternWhitespace,
        TimeSpan.FromMilliseconds(100));

    public DotNetSolutionDoctorHistoryWriteResult Append(
        string workspaceRoot,
        DotNetSolutionDoctorHistoryRun run)
    {
        if (!TryResolveHistoryPath(workspaceRoot, createDirectories: true, out var historyPath, out var error))
        {
            return Failure(error);
        }

        if (!TryNormalizeRun(run, out var normalizedRun, out error))
        {
            return Failure(error);
        }

        if (!TryReadCore(historyPath, out var history, out error))
        {
            return Failure(error);
        }

        var runs = history.Runs
            .Where(existing => !existing.Id.Equals(normalizedRun.Id, StringComparison.Ordinal))
            .Append(normalizedRun)
            .OrderBy(item => item.CompletedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .TakeLast(MaxRuns)
            .ToArray();
        var replacement = new DotNetSolutionDoctorHistory(Schema, DateTimeOffset.UtcNow, runs);
        var tempPath = $"{historyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(replacement, JsonOptions);
            if (!IsWithinHistoryByteLimit(json))
            {
                return Failure("The bounded Solution Doctor history would exceed its size limit.");
            }

            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, historyPath, overwrite: true);
            return new(
                Succeeded: true,
                HistoryRelativePath,
                "Solution Doctor repair outcome recorded.");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Failure($"Solution Doctor history was not written ({exception.GetType().Name}).");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static bool IsWithinHistoryByteLimit(ReadOnlySpan<byte> utf8History)
    {
        return utf8History.Length <= MaxHistoryBytes;
    }

    public bool TryRead(
        string workspaceRoot,
        out DotNetSolutionDoctorHistory history,
        out string message)
    {
        history = new(Schema, DateTimeOffset.UtcNow, []);
        if (!TryResolveHistoryPath(workspaceRoot, createDirectories: false, out var historyPath, out message))
        {
            return false;
        }

        return TryReadCore(historyPath, out history, out message);
    }

    private static bool TryReadCore(
        string historyPath,
        out DotNetSolutionDoctorHistory history,
        out string message)
    {
        history = new(Schema, DateTimeOffset.UtcNow, []);
        message = "No Solution Doctor history exists yet.";
        try
        {
            if (!File.Exists(historyPath))
            {
                return true;
            }

            var info = new FileInfo(historyPath);
            if (info.Length > MaxHistoryBytes)
            {
                message = "The Solution Doctor history exceeds its bounded size limit.";
                return false;
            }

            using var stream = new FileStream(
                historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            var loaded = JsonSerializer.Deserialize<DotNetSolutionDoctorHistory>(stream, JsonOptions);
            if (loaded is null
                || !loaded.SchemaVersion.Equals(Schema, StringComparison.Ordinal)
                || loaded.GeneratedAt == default)
            {
                message = "The Solution Doctor history schema is unavailable or unsupported.";
                return false;
            }

            if (loaded.Runs.Count > MaxRuns)
            {
                message = "The Solution Doctor history contains too many runs.";
                return false;
            }

            var normalized = new List<DotNetSolutionDoctorHistoryRun>(loaded.Runs.Count);
            foreach (var run in loaded.Runs)
            {
                if (!TryNormalizeRun(run, out var normalizedRun, out message))
                {
                    return false;
                }

                normalized.Add(normalizedRun);
            }

            history = loaded with
            {
                Runs = normalized
                    .OrderBy(run => run.CompletedAt)
                    .ThenBy(run => run.Id, StringComparer.Ordinal)
                    .ToArray()
            };
            message = "Solution Doctor history loaded.";
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            message = $"Solution Doctor history is unavailable ({exception.GetType().Name}).";
            return false;
        }
    }

    private static bool TryResolveHistoryPath(
        string workspaceRoot,
        bool createDirectories,
        out string historyPath,
        out string error)
    {
        historyPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            error = "A workspace root is required for Solution Doctor history.";
            return false;
        }

        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            if (!Directory.Exists(root))
            {
                error = "The Solution Doctor workspace root is unavailable.";
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                error = "Solution Doctor history will not cross a reparse-point workspace root.";
                return false;
            }

            var analysisDirectory = Path.Combine(root, ".ai-arena", "analysis");
            if (createDirectories)
            {
                foreach (var directory in new[]
                         {
                             Path.Combine(root, ".ai-arena"),
                             analysisDirectory
                         })
                {
                    if (Directory.Exists(directory)
                        && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Solution Doctor history will not cross a reparse-point analysis directory.";
                        return false;
                    }

                    Directory.CreateDirectory(directory);
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "Solution Doctor history will not cross a reparse-point analysis directory.";
                        return false;
                    }
                }
            }
            else if (Directory.Exists(analysisDirectory)
                     && (File.GetAttributes(analysisDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                error = "Solution Doctor history will not cross a reparse-point analysis directory.";
                return false;
            }

            historyPath = Path.Combine(analysisDirectory, "solution-doctor-history.json");
            var relative = Path.GetRelativePath(root, historyPath).Replace('\\', '/');
            if (!relative.Equals(HistoryRelativePath, StringComparison.Ordinal))
            {
                error = "Solution Doctor history escaped its workspace-relative destination.";
                historyPath = "";
                return false;
            }

            if (File.Exists(historyPath)
                && (File.GetAttributes(historyPath) & FileAttributes.ReparsePoint) != 0)
            {
                error = "Solution Doctor history will not read or replace a reparse-point history file.";
                historyPath = "";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"Solution Doctor history path is unavailable ({exception.GetType().Name}).";
            historyPath = "";
            return false;
        }
    }

    private static bool TryNormalizeRun(
        DotNetSolutionDoctorHistoryRun run,
        out DotNetSolutionDoctorHistoryRun normalized,
        out string error)
    {
        normalized = run;
        error = "";
        if (run is null
            || run.AffectedRelativePaths is null
            || run.Transitions is null
            || run.TestEvidence is null
            || run.TestEvidence.Flaky is null
            || run.TestEvidence.Failing is null
            || run.TestEvidence.Observations is null
            || !Enum.IsDefined(run.Outcome)
            || !IsBoundedMetadata(run.Id, 160)
            || run.CompletedAt < run.StartedAt
            || !IsOptionalGitObjectId(run.BaselineRevision)
            || !IsOptionalGitObjectId(run.TargetRevision)
            || run.AffectedRelativePaths.Count > MaxAffectedPaths
            || run.Transitions.Count > MaxTransitions
            || run.TestEvidence.Failing.Count > MaxFailingNames
            || run.TestEvidence.Flaky.Count > MaxFailingNames
            || run.TestEvidence.Observations.Count > MaxObservations
            || run.TestEvidence.Passed < 0
            || run.TestEvidence.Failed < 0
            || !run.TestEvidence.Available
                && (run.TestEvidence.Complete
                    || run.TestEvidence.Passed != 0
                    || run.TestEvidence.Failed != 0
                    || run.TestEvidence.Flaky.Count != 0
                    || run.TestEvidence.Failing.Count != 0
                    || run.TestEvidence.Observations.Count != 0)
            || run.TestEvidence.Complete && !run.TestEvidence.Available)
        {
            error = "The Solution Doctor history run exceeds its bounded contract.";
            return false;
        }

        var affected = new List<string>();
        foreach (var path in run.AffectedRelativePaths)
        {
            if (!TryNormalizeRelativePath(path, out var normalizedPath))
            {
                error = "The Solution Doctor history contains an unsafe affected path.";
                return false;
            }

            affected.Add(normalizedPath);
        }

        var transitions = new List<DotNetSolutionDoctorFindingTransition>();
        foreach (var transition in run.Transitions)
        {
            if (transition is null
                || !Enum.IsDefined(transition.State)
                || !IsBoundedMetadata(transition.FindingId, 160)
                || !IsBoundedMetadata(transition.Code, 40)
                || transition.State == DotNetRepairFindingTransitionState.Unknown
                || transition.RelatedRelativePaths.Count > MaxTransitionPaths)
            {
                error = "The Solution Doctor history contains an invalid finding transition.";
                return false;
            }

            string? primaryPath = null;
            if (transition.PrimaryRelativePath is not null
                && !TryNormalizeRelativePath(transition.PrimaryRelativePath, out primaryPath))
            {
                error = "The Solution Doctor history contains an unsafe primary finding path.";
                return false;
            }

            var relatedPaths = new List<string>();
            foreach (var path in transition.RelatedRelativePaths)
            {
                if (!TryNormalizeRelativePath(path, out var normalizedPath))
                {
                    error = "The Solution Doctor history contains an unsafe related finding path.";
                    return false;
                }

                relatedPaths.Add(normalizedPath);
            }

            transitions.Add(transition with
            {
                PrimaryRelativePath = primaryPath,
                RelatedRelativePaths = relatedPaths
                    .Where(path => !path.Equals(primaryPath, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            });
        }

        var failing = new List<string>();
        foreach (var name in run.TestEvidence.Failing)
        {
            if (!IsBoundedMetadata(name, 240))
            {
                error = "The Solution Doctor history contains an invalid failing-test label.";
                return false;
            }

            if (!ContainsSecretLikeMetadata(name))
            {
                failing.Add(name);
            }
        }

        var flaky = new List<string>();
        foreach (var name in run.TestEvidence.Flaky)
        {
            if (!IsBoundedMetadata(name, 240))
            {
                error = "The Solution Doctor history contains an invalid flaky-test label.";
                return false;
            }

            if (!ContainsSecretLikeMetadata(name))
            {
                flaky.Add(name);
            }
        }

        var observations = new List<DotNetSolutionDoctorTestObservation>();
        foreach (var observation in run.TestEvidence.Observations)
        {
            if (observation is null
                || !Enum.IsDefined(observation.Outcome)
                || !IsOptionalBoundedMetadata(observation.TestId, 160)
                || !IsBoundedMetadata(observation.TestName, 240))
            {
                error = "The Solution Doctor history contains an invalid test observation.";
                return false;
            }

            if (!TryNormalizeRelativePath(observation.ProjectRelativePath, out var projectPath))
            {
                error = "The Solution Doctor history contains an unsafe test-project path.";
                return false;
            }

            if (ContainsSecretLikeMetadata(observation.TestName)
                || observation.TestId is not null
                && ContainsSecretLikeMetadata(observation.TestId))
            {
                continue;
            }

            observations.Add(observation with
            {
                ProjectRelativePath = projectPath
            });
        }

        normalized = run with
        {
            AffectedRelativePaths = affected
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Transitions = transitions
                .OrderBy(transition => transition.Code, StringComparer.Ordinal)
                .ThenBy(transition => transition.FindingId, StringComparer.Ordinal)
                .ToArray(),
            TestEvidence = run.TestEvidence with
            {
                Flaky = flaky.Distinct(StringComparer.Ordinal).ToArray(),
                Failing = failing.Distinct(StringComparer.Ordinal).ToArray(),
                Observations = observations
                    .OrderBy(observation => observation.ProjectRelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(observation => observation.TestName, StringComparer.Ordinal)
                    .ToArray()
            }
        };
        return true;
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in value.Replace('\\', '/')
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return normalized.Length <= 512;
    }

    private static bool IsOptionalBoundedMetadata(string? value, int maximum)
    {
        return value is null || IsBoundedMetadata(value, maximum);
    }

    private static bool IsOptionalGitObjectId(string? value)
    {
        return value is null
            || (value.Length is 40 or 64
                && value.All(character =>
                    character is >= '0' and <= '9'
                        or >= 'a' and <= 'f'
                        or >= 'A' and <= 'F'));
    }

    private static bool IsBoundedMetadata(string value, int maximum)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && !value.Any(char.IsControl);
    }

    private static bool ContainsSecretLikeMetadata(string value)
    {
        return SecretLikeMetadataRegex.IsMatch(value);
    }

    private static DotNetSolutionDoctorHistoryWriteResult Failure(string message)
    {
        return new(false, HistoryRelativePath, message);
    }
}
