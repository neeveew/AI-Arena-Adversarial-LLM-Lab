namespace AIArena.Core.Persistence;

public static class NativeDataPaths
{
    public static string DefaultDataRoot()
    {
        var overridePath = Environment.GetEnvironmentVariable("AI_ARENA_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AI Arena Alpha", "data");
    }

    public static string SessionSnapshotPath(string dataRoot, string sessionId)
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return Path.Combine(dataRoot, "sessions", safeSession, "snapshot.json");
    }
}

