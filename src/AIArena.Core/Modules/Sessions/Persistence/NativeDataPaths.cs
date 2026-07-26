using System.Security.Cryptography;
using System.Text;

namespace AIArena.Core.Persistence;

public static class NativeDataPaths
{
    public const string AppDataFolderName = "AI Arena";
    private const string LegacyAppDataFolderName = "AI Arena Alpha";
    private const int MaxSafeSessionIdLength = 96;
    private const int SafeSessionHashLength = 12;

    public static string DefaultDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            EnsureDataLayout(overridePath, migrateLegacy: false);
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRoot = Path.Combine(localAppData, AppDataFolderName);
        EnsureDataLayout(dataRoot, migrateLegacy: true);
        return dataRoot;
    }

    public static string SessionSnapshotPath(string dataRoot, string sessionId)
    {
        return Path.Combine(SessionsRoot(dataRoot), SafeSessionId(sessionId), "snapshot.json");
    }

    public static string ConfigRoot(string dataRoot) => Path.Combine(dataRoot, "configs");

    public static string SessionsRoot(string dataRoot) => Path.Combine(dataRoot, "sessions");

    public static string CheckpointsRoot(string dataRoot) => Path.Combine(dataRoot, "checkpoints");

    public static string TemplatesRoot(string dataRoot) => Path.Combine(dataRoot, "templates");

    public static string LogsRoot(string dataRoot) => Path.Combine(dataRoot, "logs");

    public static string ExportsRoot(string dataRoot) => Path.Combine(dataRoot, "exports");

    public static string CacheRoot(string dataRoot) => Path.Combine(dataRoot, "cache");

    public static string ConfigPath(string dataRoot, string fileName) => Path.Combine(ConfigRoot(dataRoot), fileName);

    public static string TemplatePath(string dataRoot, string fileName) => Path.Combine(TemplatesRoot(dataRoot), fileName);

    public static string CheckpointDirectory(string dataRoot, string sessionId)
    {
        return Path.Combine(CheckpointsRoot(dataRoot), SafeSessionId(sessionId));
    }

    public static string EventPath(string dataRoot, string sessionId)
    {
        return Path.Combine(LogsRoot(dataRoot), "sessions", SafeSessionId(sessionId), "events.jsonl");
    }

    public static string SafeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars()
            .Append(Path.DirectorySeparatorChar)
            .Append(Path.AltDirectorySeparatorChar)
            .ToHashSet();
        var cleaned = new string(sessionId
            .Trim()
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
            .ToArray())
            .Trim('-', '.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.All(ch => ch == '.'))
        {
            return "default";
        }

        return cleaned.Length <= MaxSafeSessionIdLength
            ? cleaned
            : ShortenSessionId(cleaned);
    }

    public static void EnsureDataLayout(string dataRoot, bool migrateLegacy = true)
    {
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(ConfigRoot(dataRoot));
        Directory.CreateDirectory(SessionsRoot(dataRoot));
        Directory.CreateDirectory(CheckpointsRoot(dataRoot));
        Directory.CreateDirectory(TemplatesRoot(dataRoot));
        Directory.CreateDirectory(LogsRoot(dataRoot));
        Directory.CreateDirectory(ExportsRoot(dataRoot));
        Directory.CreateDirectory(CacheRoot(dataRoot));

        if (migrateLegacy)
        {
            MigrateLegacyData(dataRoot);
        }
    }

    private static string ShortenSessionId(string cleaned)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cleaned)))
            .ToLowerInvariant()[..SafeSessionHashLength];
        var prefixLength = Math.Max(1, MaxSafeSessionIdLength - suffix.Length - 1);
        var prefix = cleaned[..Math.Min(cleaned.Length, prefixLength)].Trim('-', '.', ' ');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "session";
        }

        return $"{prefix}-{suffix}";
    }

    private static void MigrateLegacyData(string dataRoot)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacyRoot = Path.Combine(localAppData, LegacyAppDataFolderName, "data");
        if (!Directory.Exists(legacyRoot) || PathsEqual(legacyRoot, dataRoot))
        {
            return;
        }

        CopyIfMissing(Path.Combine(legacyRoot, "native-wpf-settings.json"), ConfigPath(dataRoot, "native-wpf-settings.json"));
        CopyIfMissing(Path.Combine(legacyRoot, "settings.json"), ConfigPath(dataRoot, "settings.json"));
        CopyIfMissing(Path.Combine(legacyRoot, "scenario-templates.json"), TemplatePath(dataRoot, "scenario-templates.json"));

        var legacySessionsRoot = Path.Combine(legacyRoot, "sessions");
        if (!Directory.Exists(legacySessionsRoot))
        {
            return;
        }

        foreach (var sessionDir in SafeEnumerateDirectories(legacySessionsRoot))
        {
            if (DirectoryIsReparsePoint(sessionDir))
            {
                continue;
            }

            var sessionId = Path.GetFileName(sessionDir);
            CopyDirectoryIfMissing(sessionDir, Path.Combine(SessionsRoot(dataRoot), SafeSessionId(sessionId)), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "checkpoints" });
            CopyDirectoryIfMissing(
                Path.Combine(sessionDir, "checkpoints"),
                CheckpointDirectory(dataRoot, sessionId));
            CopyIfMissing(
                Path.Combine(sessionDir, "events.jsonl"),
                EventPath(dataRoot, sessionId));

            foreach (var rotatedLog in SafeEnumerateFiles(sessionDir, "events.*.jsonl"))
            {
                CopyIfMissing(rotatedLog, Path.Combine(Path.GetDirectoryName(EventPath(dataRoot, sessionId))!, Path.GetFileName(rotatedLog)));
            }
        }
    }

    private static void CopyDirectoryIfMissing(string sourceRoot, string targetRoot, IReadOnlySet<string>? skipDirectoryNames = null)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        if (DirectoryIsReparsePoint(sourceRoot))
        {
            return;
        }

        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        while (pending.TryDequeue(out var currentDirectory))
        {
            var targetDirectory = PathsEqual(currentDirectory, sourceRoot)
                ? targetRoot
                : MapPath(sourceRoot, targetRoot, currentDirectory);
            Directory.CreateDirectory(targetDirectory);

            foreach (var file in SafeEnumerateFiles(currentDirectory))
            {
                CopyIfMissing(file, MapPath(sourceRoot, targetRoot, file));
            }

            foreach (var directory in SafeEnumerateDirectories(currentDirectory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!ShouldSkipLegacyCopyDirectory(directory, skipDirectoryNames))
                {
                    pending.Enqueue(directory);
                }
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern = "*")
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool ShouldSkipLegacyCopyDirectory(string directory, IReadOnlySet<string>? skipDirectoryNames)
    {
        try
        {
            return ShouldSkipLegacyCopyDirectory(directory, skipDirectoryNames, File.GetAttributes(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static bool ShouldSkipLegacyCopyDirectory(string directory, IReadOnlySet<string>? skipDirectoryNames, FileAttributes attributes)
    {
        var directoryName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return skipDirectoryNames?.Contains(directoryName) == true
            || (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    private static bool DirectoryIsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static string MapPath(string sourceRoot, string targetRoot, string sourcePath)
    {
        return Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, sourcePath));
    }

    private static void CopyIfMissing(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(targetPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath);
        File.SetAttributes(targetPath, File.GetAttributes(targetPath) & ~FileAttributes.ReadOnly);
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
