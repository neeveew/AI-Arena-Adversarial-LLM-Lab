using System.IO;

namespace AIArena.Wpf;

internal static class ShellFileExport
{
    public static bool TryWriteAllText(string path, string contents, out string error)
    {
        string? tempPath = null;
        string? targetPath = null;
        FileAttributes? originalTargetAttributes = null;
        var replacementCompleted = false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            targetPath = fullPath;
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempDirectory = string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory;
            tempPath = Path.Combine(tempDirectory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempPath, contents);
            originalTargetAttributes = ClearReadOnly(fullPath);
            File.Move(tempPath, fullPath, overwrite: true);
            replacementCompleted = true;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!replacementCompleted && originalTargetAttributes.HasValue && !string.IsNullOrWhiteSpace(targetPath))
            {
                TryRestoreAttributes(targetPath, originalTargetAttributes.Value);
            }

            error = ex.Message;
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // A failed cleanup should not hide the original export result.
                }
            }
        }
    }

    private static FileAttributes? ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        return attributes;
    }

    private static void TryRestoreAttributes(string path, FileAttributes attributes)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, attributes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the export failure that triggered the rollback attempt.
        }
    }
}
