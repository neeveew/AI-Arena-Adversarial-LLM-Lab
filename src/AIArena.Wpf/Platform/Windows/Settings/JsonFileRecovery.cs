using System.IO;

namespace AIArena.Wpf.Services;

internal static class JsonFileRecovery
{
    public static string BackupCorruptFile(string path, string label, Exception error)
    {
        try
        {
            if (!File.Exists(path))
            {
                return $"{label} file could not be loaded: {error.Message}";
            }

            var backupPath = CorruptBackupPath(path);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Move(path, backupPath);
            return $"{label} file was corrupt and was moved to {backupPath}. Defaults are being used.";
        }
        catch (Exception backupError)
        {
            return $"{label} file is corrupt and could not be backed up: {backupError.Message}. Defaults are being used.";
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
}
