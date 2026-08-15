using System.IO;

namespace AIArena.Wpf.Services;

internal static class JsonFileRecovery
{
    public static void WriteTextReplacing(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = TemporarySiblingPath(path);
        try
        {
            File.WriteAllText(tempPath, contents);
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    public static string BackupCorruptFile(string path, string label, Exception error)
    {
        try
        {
            var presentation = AppErrorPresenter.Present(error, AppErrorContext.Settings);
            if (!File.Exists(path))
            {
                return $"{label} file is corrupt or invalid. {presentation.DisplayText} Defaults are being used.";
            }

            var backupPath = CorruptBackupPath(path);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Move(path, backupPath);
            return $"{label} file was corrupt and was moved to a timestamped backup beside the original. {presentation.DisplayText} Defaults are being used.";
        }
        catch (Exception backupError)
        {
            var presentation = AppErrorPresenter.Present(backupError, AppErrorContext.Settings);
            return $"{label} file is corrupt and could not be backed up. {presentation.DisplayText} Defaults are being used.";
        }
    }

    private static string CorruptBackupPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var backupPath = Path.Combine(directory, $"{fileName}.corrupt.{stamp}{extension}");
        return File.Exists(backupPath)
            ? Path.Combine(directory, $"{fileName}.corrupt.{stamp}.{Guid.NewGuid():N}{extension}")
            : backupPath;
    }

    private static string TemporarySiblingPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileName(path);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
