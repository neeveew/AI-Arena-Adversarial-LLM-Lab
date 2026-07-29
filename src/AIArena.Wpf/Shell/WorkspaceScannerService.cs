using System.Globalization;
using System.IO;
using AIArena.Wpf.Services;
using static AIArena.Wpf.Services.WorkspaceCommandHelpers;

namespace AIArena.Wpf;

/// <summary>
/// Filesystem-only workspace inspection for the Agent workspace. The coordinator
/// keeps UI state; this service owns bounded scans, profile hints, and file receipts.
/// </summary>
internal static class WorkspaceScannerService
{
    internal const int MaxWorkspaceFilesInReceipt = 1500;
    internal const int MaxWorkspaceDirectoriesInReceipt = 2048;
    internal const int MaxWorkspaceProfileFiles = 24;
    internal const int MaxWorkspaceProfileDirectories = 12;
    internal const int MaxWorkspaceProfileDirectoryCandidates = 512;
    internal const int MaxWorkspaceProfileFileCandidatesPerDirectory = 512;
    internal const long MaxWorkspaceProfileTextFileBytes = 256 * 1024;

    private static readonly string[] WorkspaceProfileExactFileNames =
    [
        "package.json",
        "vite.config.js",
        "vite.config.ts",
        "tsconfig.json",
        "pyproject.toml",
        "requirements.txt",
        "Cargo.toml",
        "go.mod",
        "README.md"
    ];

    private static readonly string[] WorkspaceProfileExtensionFileNames =
    [
        ".sln",
        ".slnx",
        ".csproj",
        ".html"
    ];

    internal static string BuildWorkspaceProfile(string root)
    {
        return BuildWorkspaceProfile(root, CancellationToken.None);
    }

    internal static Task<string> BuildWorkspaceProfileAsync(string root, CancellationToken cancellationToken)
    {
        return Task.Run(() => BuildWorkspaceProfile(root, cancellationToken), cancellationToken);
    }

    private static string BuildWorkspaceProfile(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return "No workspace profile yet.";
        }

        var keyFiles = DiscoverWorkspaceProfileFiles(root, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var signals = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var verifyCommands = new List<string>();
        if (keyFiles.Any(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add(".NET");
        }

        var packagePath = keyFiles.FirstOrDefault(path => RelativeFileNameEquals(path, "package.json"));
        if (packagePath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            signals.Add("Node");
            verifyCommands.AddRange(WorkspacePackageScriptHints(Path.Combine(root, packagePath), packagePath));
        }

        var pythonPath = keyFiles.FirstOrDefault(path => RelativeFileNameEquals(path, "pyproject.toml")
            || RelativeFileNameEquals(path, "requirements.txt"));
        if (pythonPath is not null)
        {
            signals.Add("Python");
            verifyCommands.Add(PythonArtifactCommand(pythonPath));
        }

        var cargoPath = keyFiles.FirstOrDefault(path => RelativeFileNameEquals(path, "Cargo.toml"));
        if (cargoPath is not null)
        {
            signals.Add("Rust");
            verifyCommands.Add(RustArtifactCommand(cargoPath));
        }

        var goPath = keyFiles.FirstOrDefault(path => RelativeFileNameEquals(path, "go.mod"));
        if (goPath is not null)
        {
            signals.Add("Go");
            verifyCommands.Add(GoArtifactCommand(goPath));
        }

        if (keyFiles.Any(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
        {
            signals.Add("Static web");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var boundedVerifyCommands = verifyCommands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        var lines = new List<string>
        {
            $"Project signals: {(signals.Count == 0 ? "General workspace" : string.Join(", ", signals))}",
            $"Key files: {(keyFiles.Count == 0 ? "No common project files detected" : string.Join(", ", keyFiles.Take(12)))}",
            $"Likely verify commands: {(boundedVerifyCommands.Length == 0 ? "Ask Builder for a read-only inspection first" : string.Join("; ", boundedVerifyCommands))}",
            Directory.Exists(Path.Combine(root, ".git")) ? "Git: repository detected" : "Git: no .git folder detected"
        };
        if (keyFiles.Count > 12)
        {
            lines[1] += $", +{(keyFiles.Count - 12).ToString(CultureInfo.InvariantCulture)} more";
        }

        return ShellUiHelpers.Truncate(string.Join(Environment.NewLine, lines), 1600);
    }

    internal static IReadOnlyList<string> DiscoverWorkspaceProfileDirectories(string root)
    {
        return DiscoverWorkspaceProfileDirectories(root, CancellationToken.None);
    }

    private static IReadOnlyList<string> DiscoverWorkspaceProfileDirectories(
        string root,
        CancellationToken cancellationToken)
    {
        var childDirectories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var inspected = 0;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inspected >= MaxWorkspaceProfileDirectoryCandidates)
                {
                    break;
                }

                inspected++;
                if (!ShouldSkipWorkspaceReceiptDirectory(directory))
                {
                    childDirectories.Add(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return childDirectories.Take(MaxWorkspaceProfileDirectories).ToArray();
        }

        return childDirectories.Take(MaxWorkspaceProfileDirectories).ToArray();
    }

    internal static Task<AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot> CaptureWorkspaceFilesAsync(string root, CancellationToken cancellationToken)
    {
        return Task.Run(() => CaptureWorkspaceFiles(root, cancellationToken), cancellationToken);
    }

    internal static AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot CaptureWorkspaceFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        var files = new SortedDictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot(files, ScannedLimit: false);
        }

        var directoriesVisited = 0;
        var fileCandidatesInspected = 0;
        var scannedLimit = false;
        CaptureWorkspaceFiles(
            root,
            root,
            files,
            cancellationToken,
            ref directoriesVisited,
            ref fileCandidatesInspected,
            ref scannedLimit,
            countCurrentDirectory: true);
        return new AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot(files, scannedLimit);
    }

    internal static bool ShouldSkipWorkspaceReceiptDirectory(string directory, FileAttributes attributes)
    {
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }

    internal static AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt BuildFileReceipt(
        IReadOnlyDictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp> before,
        IReadOnlyDictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp> after)
    {
        return BuildFileReceipt(
            new AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot(before, before.Count >= MaxWorkspaceFilesInReceipt),
            new AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot(after, after.Count >= MaxWorkspaceFilesInReceipt));
    }

    internal static AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt BuildFileReceipt(
        AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot before,
        AgentWorkspaceCoordinator.AgentWorkspaceFileSnapshot after)
    {
        var beforeFiles = before.Files;
        var afterFiles = after.Files;
        var created = afterFiles.Keys.Except(beforeFiles.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var deleted = beforeFiles.Keys.Except(afterFiles.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var modified = beforeFiles.Keys
            .Intersect(afterFiles.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => beforeFiles[path] != afterFiles[path])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scannedLimit = before.ScannedLimit
            || after.ScannedLimit
            || beforeFiles.Count >= MaxWorkspaceFilesInReceipt
            || afterFiles.Count >= MaxWorkspaceFilesInReceipt;
        var summary = $"Files: +{created.Length.ToString(CultureInfo.InvariantCulture)} created, ~{modified.Length.ToString(CultureInfo.InvariantCulture)} modified, -{deleted.Length.ToString(CultureInfo.InvariantCulture)} deleted";
        if (scannedLimit)
        {
            summary += " (scan limited)";
        }

        return new AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt(summary, created, modified, deleted, scannedLimit);
    }

    internal static string FormatFileReceipt(AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt receipt)
    {
        var lines = new List<string>
        {
            "FILES",
            receipt.Summary
        };
        AppendReceiptGroup(lines, "Created", receipt.Created);
        AppendReceiptGroup(lines, "Modified", receipt.Modified);
        AppendReceiptGroup(lines, "Deleted", receipt.Deleted);
        if (receipt.ScannedLimit)
        {
            lines.Add($"Receipt scanned up to {MaxWorkspaceFilesInReceipt.ToString(CultureInfo.InvariantCulture)} files and {MaxWorkspaceDirectoriesInReceipt.ToString(CultureInfo.InvariantCulture)} directories outside ignored build/cache folders.");
        }

        if (ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            lines.Add("No changes detected inside the scanned file window; changes outside the scan limit are unknown.");
        }
        else if (ReceiptHasKnownNoChanges(receipt))
        {
            lines.Add("No tracked file changes detected.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static bool ReceiptHasChanges(AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt receipt)
    {
        return receipt.Created.Count > 0 || receipt.Modified.Count > 0 || receipt.Deleted.Count > 0;
    }

    internal static bool ReceiptHasKnownNoChanges(AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt receipt)
    {
        return !receipt.ScannedLimit && !ReceiptHasChanges(receipt);
    }

    internal static bool ReceiptScanIsLimitedWithoutTrackedChanges(AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt receipt)
    {
        return receipt.ScannedLimit && !ReceiptHasChanges(receipt);
    }

    internal static bool IsWorkspaceProfileFile(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        return WorkspaceProfileExactFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || WorkspaceProfileExtensionFileNames.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> DiscoverWorkspaceProfileFiles(
        string root,
        CancellationToken cancellationToken)
    {
        var keyFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        AddWorkspaceProfileFiles(root, root, keyFiles, cancellationToken);

        foreach (var directory in DiscoverWorkspaceProfileDirectories(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keyFiles.Count >= MaxWorkspaceProfileFiles)
            {
                break;
            }

            AddWorkspaceProfileFiles(root, directory, keyFiles, cancellationToken);
        }

        return keyFiles.Take(MaxWorkspaceProfileFiles).ToArray();
    }

    private static void AddWorkspaceProfileFiles(
        string root,
        string directory,
        ISet<string> keyFiles,
        CancellationToken cancellationToken)
    {
        foreach (var exactFileName in WorkspaceProfileExactFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keyFiles.Count >= MaxWorkspaceProfileFiles)
            {
                return;
            }

            var path = Path.Combine(directory, exactFileName);
            try
            {
                if (File.Exists(path))
                {
                    keyFiles.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }
        }

        var inspected = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (keyFiles.Count >= MaxWorkspaceProfileFiles
                    || inspected >= MaxWorkspaceProfileFileCandidatesPerDirectory)
                {
                    return;
                }

                inspected++;
                var extension = Path.GetExtension(file);
                if (WorkspaceProfileExtensionFileNames.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    keyFiles.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }
    }

    private static void CaptureWorkspaceFiles(
        string root,
        string currentDirectory,
        IDictionary<string, AgentWorkspaceCoordinator.AgentWorkspaceFileStamp> files,
        CancellationToken cancellationToken,
        ref int directoriesVisited,
        ref int fileCandidatesInspected,
        ref bool scannedLimit,
        bool countCurrentDirectory)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scannedLimit)
        {
            return;
        }

        if (files.Count >= MaxWorkspaceFilesInReceipt)
        {
            scannedLimit = true;
            return;
        }

        if (countCurrentDirectory
            && !TryConsumeScanCandidate(ref directoriesVisited, MaxWorkspaceDirectoriesInReceipt))
        {
            scannedLimit = true;
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryConsumeScanCandidate(ref fileCandidatesInspected, MaxWorkspaceFilesInReceipt))
                {
                    scannedLimit = true;
                    return;
                }

                if (files.Count >= MaxWorkspaceFilesInReceipt)
                {
                    scannedLimit = true;
                    return;
                }

                try
                {
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    files[relative] = new AgentWorkspaceCoordinator.AgentWorkspaceFileStamp(info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    continue;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryConsumeScanCandidate(ref directoriesVisited, MaxWorkspaceDirectoriesInReceipt))
                {
                    scannedLimit = true;
                    return;
                }

                if (files.Count >= MaxWorkspaceFilesInReceipt)
                {
                    scannedLimit = true;
                    return;
                }

                if (ShouldSkipWorkspaceReceiptDirectory(directory))
                {
                    continue;
                }

                CaptureWorkspaceFiles(
                    root,
                    directory,
                    files,
                    cancellationToken,
                    ref directoriesVisited,
                    ref fileCandidatesInspected,
                    ref scannedLimit,
                    countCurrentDirectory: false);
                if (scannedLimit)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }
    }

    internal static bool TryConsumeScanCandidate(ref int inspected, int maximum)
    {
        if (inspected >= maximum)
        {
            return false;
        }

        inspected++;
        return true;
    }

    private static bool ShouldSkipWorkspaceReceiptDirectory(string directory)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return true;
        }

        return ShouldSkipWorkspaceReceiptDirectory(directory, attributes);
    }

    private static void AppendReceiptGroup(ICollection<string> lines, string label, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        lines.Add($"{label}:");
        foreach (var path in paths.Take(8))
        {
            lines.Add($"- {path}");
        }

        if (paths.Count > 8)
        {
            lines.Add($"- +{(paths.Count - 8).ToString(CultureInfo.InvariantCulture)} more");
        }
    }

}
