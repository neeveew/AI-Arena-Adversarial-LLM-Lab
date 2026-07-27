using System.Globalization;
using System.IO;
using AIArena.Wpf.Services;
using static AIArena.Wpf.Services.WorkspaceCommandHelpers;

using AgentArtifactSuggestion = AIArena.Wpf.AgentWorkspaceCoordinator.AgentArtifactSuggestion;
using AgentArtifactVerification = AIArena.Wpf.AgentWorkspaceCoordinator.AgentArtifactVerification;
using AgentCommandHistoryItem = AIArena.Wpf.AgentWorkspaceCoordinator.AgentCommandHistoryItem;
using AgentWorkspaceFileReceipt = AIArena.Wpf.AgentWorkspaceCoordinator.AgentWorkspaceFileReceipt;

namespace AIArena.Wpf;

/// <summary>
/// Artifact inference and receipt-oriented work summaries for the Agent workspace.
/// The coordinator keeps UI state; this service owns generated-artifact detection
/// and the command/receipt text handed back to the Agent.
/// </summary>
internal static class AgentArtifactService
{
    private const int MaxReceiptPathItems = 8;

    internal static AgentArtifactSuggestion? InferArtifactSuggestion(string workspaceRoot, AgentWorkspaceFileReceipt receipt)
    {
        var paths = receipt.Created
            .Concat(receipt.Modified)
            .Where(IsSafeGeneratedCommandPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return null;
        }

        var directHtmlPath = paths.FirstOrDefault(path => path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            ?? paths.FirstOrDefault(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
        var directSlnPath = paths.FirstOrDefault(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        var directCsprojPath = paths.FirstOrDefault(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var directPyprojectPath = paths.FirstOrDefault(path => RelativeFileNameEquals(path, "pyproject.toml")
            || RelativeFileNameEquals(path, "requirements.txt"));
        var directCargoPath = paths.FirstOrDefault(path => RelativeFileNameEquals(path, "Cargo.toml"));
        var directGoPath = paths.FirstOrDefault(path => RelativeFileNameEquals(path, "go.mod"));
        var fallbackHtmlPath = directHtmlPath is null
            ? FindNearestExistingWorkspaceFile(workspaceRoot, paths, "index.html")
            : null;
        var fallbackSlnPath = directSlnPath is null
            ? FindNearestExistingWorkspaceFileByExtension(workspaceRoot, paths, ".sln")
            : null;
        var fallbackCsprojPath = directCsprojPath is null
            ? FindNearestExistingWorkspaceFileByExtension(workspaceRoot, paths, ".csproj")
            : null;
        var fallbackPyprojectPath = directPyprojectPath is null
            ? FindNearestExistingWorkspaceFile(workspaceRoot, paths, "pyproject.toml", "requirements.txt")
            : null;
        var fallbackCargoPath = directCargoPath is null
            ? FindNearestExistingWorkspaceFile(workspaceRoot, paths, "Cargo.toml")
            : null;
        var fallbackGoPath = directGoPath is null
            ? FindNearestExistingWorkspaceFile(workspaceRoot, paths, "go.mod")
            : null;
        var htmlPath = directHtmlPath ?? fallbackHtmlPath;
        var slnPath = directSlnPath ?? fallbackSlnPath;
        var csprojPath = directCsprojPath ?? fallbackCsprojPath;
        var pyprojectPath = directPyprojectPath ?? fallbackPyprojectPath;
        var cargoPath = directCargoPath ?? fallbackCargoPath;
        var goPath = directGoPath ?? fallbackGoPath;

        var directPackagePath = paths.FirstOrDefault(path => RelativeFileNameEquals(path, "package.json"));
        var hasDirectNonPackageArtifact = directHtmlPath is not null
            || directSlnPath is not null
            || directCsprojPath is not null
            || directPyprojectPath is not null
            || directCargoPath is not null
            || directGoPath is not null;
        var packagePath = directPackagePath
            ?? (!hasDirectNonPackageArtifact ? FindNearestExistingWorkspaceFile(workspaceRoot, paths, "package.json") : null);
        var fallbackNonPackageDepth = new[]
            {
                fallbackHtmlPath,
                fallbackSlnPath,
                fallbackCsprojPath,
                fallbackPyprojectPath,
                fallbackCargoPath,
                fallbackGoPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => MarkerDirectoryDepth(path ?? ""))
            .DefaultIfEmpty(-1)
            .Max();
        if (packagePath is not null
            && directPackagePath is null
            && fallbackNonPackageDepth > MarkerDirectoryDepth(packagePath))
        {
            packagePath = null;
        }

        if (packagePath is not null)
        {
            var command = ArtifactPackageScriptCommands(Path.Combine(workspaceRoot, packagePath), packagePath)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(command))
            {
                if (AgentWorkspaceCommand.LooksLongRunningCommand(command))
                {
                    var launchCommand = DetachedTerminalPreviewCommand(command);
                    return new AgentArtifactSuggestion(
                        "Node",
                        packagePath,
                        "PowerShell",
                        launchCommand,
                        $"Node project artifact at {packagePath}; launch a detached preview terminal with `{command}`.");
                }

                return new AgentArtifactSuggestion(
                    "Node",
                    packagePath,
                    "Terminal",
                    command,
                    $"Node project artifact at {packagePath}; preview with `{command}`.");
            }

            if (htmlPath is null)
            {
                command = $"Get-Item {QuoteCommandArgument(ToWindowsRelativeCommandPath(packagePath))}";
                return new AgentArtifactSuggestion(
                    "Node",
                    packagePath,
                    "PowerShell",
                    command,
                    $"Node package artifact at {packagePath}; inspect the generated package file with `{command}` because no runnable scripts were declared.");
            }
        }

        if (slnPath is not null || csprojPath is not null)
        {
            var entry = slnPath ?? csprojPath ?? "";
            var command = string.IsNullOrWhiteSpace(entry)
                ? "dotnet build"
                : $"dotnet build .\\{entry.Replace('/', '\\')}";
            return new AgentArtifactSuggestion(
                ".NET",
                entry,
                "Terminal",
                command,
                $".NET project artifact at {entry}; preview with `{command}`.");
        }

        if (pyprojectPath is not null)
        {
            var command = PythonArtifactCommand(pyprojectPath);
            return new AgentArtifactSuggestion(
                "Python",
                pyprojectPath,
                "Terminal",
                command,
                $"Python project artifact at {pyprojectPath}; preview with `{command}`.");
        }

        if (cargoPath is not null)
        {
            var command = RustArtifactCommand(cargoPath);
            return new AgentArtifactSuggestion(
                "Rust",
                cargoPath,
                "Terminal",
                command,
                $"Rust project artifact at {cargoPath}; preview with `{command}`.");
        }

        if (goPath is not null)
        {
            var command = GoArtifactCommand(goPath);
            return new AgentArtifactSuggestion(
                "Go",
                goPath,
                "Terminal",
                command,
                $"Go project artifact at {goPath}; preview with `{command}`.");
        }

        if (htmlPath is not null)
        {
            var command = $"Start-Process {QuoteCommandArgument(ToWindowsRelativeCommandPath(htmlPath))}";
            return new AgentArtifactSuggestion(
                "Static web",
                htmlPath,
                "PowerShell",
                command,
                $"Static web artifact at {htmlPath}; preview in the default browser with `{command}`.");
        }

        return null;
    }

    internal static bool ArtifactEntryExists(string root, AgentArtifactSuggestion artifact)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(artifact.EntryPath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, artifact.EntryPath));
            return AgentWorkspaceCommand.IsInsideWorkspace(root, fullPath)
                && (File.Exists(fullPath) || Directory.Exists(fullPath));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static AgentArtifactVerification BuildVerification(AgentArtifactSuggestion suggestion, AgentCommandResult result)
    {
        return new AgentArtifactVerification(
            suggestion.Kind,
            suggestion.EntryPath,
            result.Shell,
            result.Command,
            result.Ok,
            result.Canceled,
            result.TimedOut,
            result.ExitCode,
            DateTimeOffset.Now);
    }

    internal static bool IsPreviewLaunch(string kind, string command)
    {
        var trimmed = (command ?? "").TrimStart();
        return (kind.Equals("Static web", StringComparison.OrdinalIgnoreCase)
                && trimmed.StartsWith("Start-Process", StringComparison.OrdinalIgnoreCase))
            || AgentWorkspaceCommand.LooksLongRunningCommand(trimmed);
    }

    internal static string ActionTitle(AgentArtifactVerification verification)
    {
        return verification.IsPreviewLaunch ? "Artifact preview" : "Artifact check";
    }

    internal static string EvidenceState(AgentArtifactVerification verification)
    {
        var action = verification.IsPreviewLaunch ? "preview" : "check";
        return verification.Ok
            ? $"{verification.Kind} {action} OK"
            : $"{verification.Kind} {action} failed";
    }

    internal static string Summary(AgentArtifactVerification verification)
    {
        if (verification.IsPreviewLaunch)
        {
            var previewState = verification.Ok
                ? "launched"
                : verification.Canceled
                    ? "was cancelled"
                    : verification.TimedOut
                        ? "timed out"
                        : $"failed with exit {verification.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            return $"{verification.Kind} artifact preview {previewState} for {verification.EntryPath}.";
        }

        var state = verification.Ok
            ? "succeeded"
            : verification.Canceled
                ? "was cancelled"
                : verification.TimedOut
                    ? "timed out"
                    : $"failed with exit {verification.ExitCode.ToString(CultureInfo.InvariantCulture)}";
        return $"{verification.Kind} artifact check {state} for {verification.EntryPath}.";
    }

    internal static string BuildWorkSummaryLine(
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        string nextAction,
        AgentArtifactSuggestion? artifactSuggestion = null,
        AgentArtifactVerification? artifactVerification = null)
    {
        return AgentCommandRailViewModel.BuildWorkSummaryLine(
            result,
            receipt,
            nextAction,
            artifactSuggestion?.Summary ?? "",
            artifactVerification?.Summary ?? "",
            artifactVerification?.Ok == true);
    }

    internal static string BuildWorkBrief(
        string task,
        string autonomy,
        AgentCommandResult result,
        AgentWorkspaceFileReceipt receipt,
        IReadOnlyList<AgentCommandHistoryItem> history,
        string nextAction,
        AgentArtifactSuggestion? artifactSuggestion = null,
        AgentArtifactVerification? artifactVerification = null)
    {
        var lines = new List<string>
        {
            "Agent work brief",
            "",
            $"Task: {CleanBriefValue(task, "(no original task recorded)")}",
            $"Autonomy: {CleanBriefValue(autonomy, "Manual approval mode")}",
            $"Latest command: {result.Shell} exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)} in {FormatElapsed(result.Elapsed)}",
            $"Workspace: {CleanBriefValue(result.WorkingDirectory, "(no workspace)")}",
            $"Outcome: {(result.Ok ? "Succeeded" : result.Canceled ? "Cancelled" : result.TimedOut ? "Timed out" : "Failed")}",
            $"Files: {receipt.Summary}",
            $"Next action: {CleanBriefValue(nextAction, "Review the latest command output.")}",
            "",
            "Command:",
            result.Command
        };

        if (artifactSuggestion is not null)
        {
            lines.Add("");
            lines.Add("Artifact suggestion:");
            lines.Add($"- {artifactSuggestion.Summary}");
            lines.Add($"- Suggested command ({artifactSuggestion.Shell}): {artifactSuggestion.Command}");
        }

        if (artifactVerification is not null)
        {
            lines.Add("");
            lines.Add(artifactVerification.IsPreviewLaunch ? "Artifact preview:" : "Artifact verification:");
            lines.Add($"- {artifactVerification.Summary}");
            lines.Add("- No workspace file changes are expected for successful preview or verification commands.");
        }

        AppendBriefOutput(lines, "STDOUT", result.StandardOutput);
        AppendBriefOutput(lines, "STDERR", result.StandardError);
        AppendReceiptGroup(lines, "Created", receipt.Created);
        AppendReceiptGroup(lines, "Modified", receipt.Modified);
        AppendReceiptGroup(lines, "Deleted", receipt.Deleted);

        if (history.Count > 0)
        {
            lines.Add("");
            lines.Add("Recent commands:");
            foreach (var item in history.Take(5))
            {
                lines.Add($"- {item.Status} | {item.Shell} | {ShellUiHelpers.Truncate(FirstCommandLine(item.Command), 140)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string ReceiptPreviewText(AgentWorkspaceFileReceipt receipt)
    {
        var paths = receipt.Created
            .Concat(receipt.Modified)
            .Concat(receipt.Deleted)
            .Take(4)
            .ToArray();
        if (paths.Length == 0 && WorkspaceScannerService.ReceiptScanIsLimitedWithoutTrackedChanges(receipt))
        {
            return $"{Environment.NewLine}Changed paths outside the scan limit are unknown.";
        }

        return paths.Length == 0
            ? ""
            : $"{Environment.NewLine}Changed paths: {string.Join(", ", paths)}";
    }

    internal static string ChangedPathSummary(AgentWorkspaceFileReceipt receipt, int maxPaths)
    {
        var paths = receipt.Created
            .Concat(receipt.Modified)
            .Concat(receipt.Deleted)
            .Take(Math.Max(0, maxPaths))
            .ToArray();
        if (paths.Length == 0)
        {
            return "";
        }

        var total = receipt.Created.Count + receipt.Modified.Count + receipt.Deleted.Count;
        var suffix = total > paths.Length
            ? $", +{(total - paths.Length).ToString(CultureInfo.InvariantCulture)} more"
            : "";
        return $"{string.Join(", ", paths)}{suffix}";
    }

    private static string DetachedTerminalPreviewCommand(string command)
    {
        return $"Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d','/s','/k','{AgentCommandProposalService.EscapePowerShellSingleQuoted(command)}') -WorkingDirectory (Get-Location).Path";
    }

    private static string? FindNearestExistingWorkspaceFile(string workspaceRoot, IReadOnlyList<string> changedPaths, params string[] fileNames)
    {
        return FindNearestExistingWorkspaceFile(workspaceRoot, changedPaths, directory =>
        {
            foreach (var fileName in fileNames)
            {
                var candidate = string.IsNullOrWhiteSpace(directory) ? fileName : $"{directory}/{fileName}";
                if (WorkspaceRelativeFileExists(workspaceRoot, candidate))
                {
                    return candidate;
                }
            }

            return null;
        });
    }

    private static string? FindNearestExistingWorkspaceFileByExtension(string workspaceRoot, IReadOnlyList<string> changedPaths, string extension)
    {
        return FindNearestExistingWorkspaceFile(workspaceRoot, changedPaths, directory =>
        {
            try
            {
                var fullDirectory = Path.Combine(workspaceRoot, directory.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(fullDirectory)
                    || !AgentWorkspaceCommand.IsInsideWorkspace(workspaceRoot, fullDirectory))
                {
                    return null;
                }

                var file = Directory.EnumerateFiles(fullDirectory, $"*{extension}", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                return file is null
                    ? null
                    : Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        });
    }

    private static string? FindNearestExistingWorkspaceFile(
        string workspaceRoot,
        IReadOnlyList<string> changedPaths,
        Func<string, string?> findInDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return null;
        }

        foreach (var changedPath in changedPaths)
        {
            if (!TryNormalizeWorkspaceRelativePath(changedPath, out var normalizedPath))
            {
                continue;
            }

            var directory = RelativeDirectory(normalizedPath);
            while (true)
            {
                var found = findInDirectory(directory);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }

                if (string.IsNullOrWhiteSpace(directory))
                {
                    break;
                }

                directory = RelativeDirectory(directory);
            }
        }

        return null;
    }

    private static bool WorkspaceRelativeFileExists(string workspaceRoot, string relativePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return AgentWorkspaceCommand.IsInsideWorkspace(workspaceRoot, fullPath) && File.Exists(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryNormalizeWorkspaceRelativePath(string value, out string path)
    {
        path = NormalizeRelativePath(value);
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            path = "";
            return false;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part.Equals("..", StringComparison.Ordinal) || part.Equals(".", StringComparison.Ordinal)))
        {
            path = "";
            return false;
        }

        path = string.Join("/", parts);
        return true;
    }

    private static int MarkerDirectoryDepth(string relativePath)
    {
        var directory = RelativeDirectory(relativePath);
        return string.IsNullOrWhiteSpace(directory)
            ? 0
            : directory.Count(character => character == '/') + 1;
    }

    private static void AppendBriefOutput(List<string> lines, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lines.Add("");
        lines.Add(label);
        lines.Add(ShellUiHelpers.Truncate(value.Trim(), 1200));
    }

    private static void AppendReceiptGroup(List<string> lines, string label, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        lines.Add($"{label}:");
        foreach (var path in paths.Take(MaxReceiptPathItems))
        {
            lines.Add($"- {path}");
        }

        if (paths.Count > MaxReceiptPathItems)
        {
            lines.Add($"- ... {paths.Count - MaxReceiptPathItems} more");
        }
    }

    private static string CleanBriefValue(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
    }

    private static string FirstCommandLine(string command)
    {
        return command
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 10
            ? $"{elapsed.TotalSeconds:0.0}s"
            : $"{elapsed.TotalMilliseconds:0}ms";
    }

}