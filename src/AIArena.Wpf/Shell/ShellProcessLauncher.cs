using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AIArena.Wpf;

internal static class ShellProcessLauncher
{
    public static bool TryStart(
        ProcessStartInfo startInfo,
        out string error,
        Func<ProcessStartInfo, Process?>? start = null)
    {
        try
        {
            using var launchedProcess = (start ?? Process.Start)(startInfo);
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException or ObjectDisposedException)
        {
            error = ex.Message;
            return false;
        }
    }
}
