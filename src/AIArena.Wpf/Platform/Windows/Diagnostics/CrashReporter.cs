using System.IO;
using System.Reflection;
using System.Text;

namespace AIArena.Wpf;

/// <summary>
/// Writes a report when the app is about to die.
///
/// Until this existed the only diagnostics were Debug.WriteLine calls, which the
/// compiler removes from a release build - the one people actually run. An
/// unhandled exception therefore closed the window and left nothing behind: no
/// dialog, no log, no way for anyone to say what happened. The data root even
/// had a logs directory that nothing had written to since June.
///
/// Every method here swallows its own failures. A crash handler that throws
/// replaces a diagnosable problem with an undiagnosable one.
/// </summary>
internal static class CrashReporter
{
    private const int KeepReports = 20;

    /// <summary>Returns the report path, or null when nothing could be written.</summary>
    public static string? Write(string source, Exception? exception)
    {
        try
        {
            var directory = ReportDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");

            var report = new StringBuilder()
                .AppendLine($"AI Arena {Version()}")
                .AppendLine($"When:   {DateTimeOffset.Now:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"OS:     {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})")
                .AppendLine($".NET:   {Environment.Version}")
                .AppendLine()
                .AppendLine(exception?.ToString() ?? "No exception object was supplied.")
                .ToString();

            File.WriteAllText(path, report);
            Prune(directory);
            return path;
        }
        catch
        {
            // Losing the report is bad; taking the process down while writing it
            // would be worse, and would hide the original failure entirely.
            return null;
        }
    }

    internal static string ReportDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AI Arena",
            "logs");
    }

    /// <summary>
    /// Keeps the newest reports only, so a crash loop cannot fill the disk.
    /// </summary>
    private static void Prune(string directory)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles("crash-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(KeepReports)
                .ToList();
            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // A locked or already-removed report is not worth reacting to.
                }
            }
        }
        catch
        {
        }
    }

    private static string Version()
    {
        try
        {
            return Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown version";
        }
        catch
        {
            return "unknown version";
        }
    }
}
