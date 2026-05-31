namespace AIArena.Core.Persistence;

public static class NativeDataPaths
{
    public const string AppDataFolderName = "AI Arena";
    private const string LegacyAppDataFolderName = "AI Arena Alpha";

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
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return Path.Combine(SessionsRoot(dataRoot), safeSession, "snapshot.json");
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
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return Path.Combine(CheckpointsRoot(dataRoot), safeSession);
    }

    public static string EventPath(string dataRoot, string sessionId)
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return Path.Combine(LogsRoot(dataRoot), "sessions", safeSession, "events.jsonl");
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

        foreach (var sessionDir in Directory.EnumerateDirectories(legacySessionsRoot))
        {
            var sessionId = Path.GetFileName(sessionDir);
            CopyDirectoryIfMissing(sessionDir, Path.Combine(SessionsRoot(dataRoot), sessionId), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "checkpoints" });
            CopyDirectoryIfMissing(
                Path.Combine(sessionDir, "checkpoints"),
                CheckpointDirectory(dataRoot, sessionId));
            CopyIfMissing(
                Path.Combine(sessionDir, "events.jsonl"),
                EventPath(dataRoot, sessionId));

            foreach (var rotatedLog in Directory.EnumerateFiles(sessionDir, "events.*.jsonl"))
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

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var directoryName = Path.GetFileName(directory);
            if (skipDirectoryNames?.Contains(directoryName) == true)
            {
                continue;
            }

            Directory.CreateDirectory(MapPath(sourceRoot, targetRoot, directory));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (IsUnderSkippedDirectory(sourceRoot, file, skipDirectoryNames))
            {
                continue;
            }

            CopyIfMissing(file, MapPath(sourceRoot, targetRoot, file));
        }
    }

    private static bool IsUnderSkippedDirectory(string sourceRoot, string file, IReadOnlySet<string>? skipDirectoryNames)
    {
        if (skipDirectoryNames is null || skipDirectoryNames.Count == 0)
        {
            return false;
        }

        var relative = Path.GetRelativePath(sourceRoot, file);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => skipDirectoryNames.Contains(part));
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
