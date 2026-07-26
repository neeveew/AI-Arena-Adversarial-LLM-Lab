using System.IO;
using System.Text;
using System.Text.Json;

namespace AIArena.Wpf.Services;

/// <summary>
/// Pure workspace path and command-string helpers extracted from AgentWorkspaceCoordinator.
/// Filesystem and string operations only - no UI or coordinator state - so they can be reused
/// and tested independently. The coordinator imports these via <c>using static</c>.
/// </summary>
internal static class WorkspaceCommandHelpers
{
    public static IEnumerable<string> WorkspacePackageScriptHints(string packageJsonPath, string relativePackagePath = "package.json")
    {
        return PackageScriptCommands(packageJsonPath, relativePackagePath, includeDefaults: true);
    }

    public static IReadOnlyList<string> ArtifactPackageScriptCommands(string packageJsonPath, string relativePackagePath)
    {
        return PackageScriptCommands(packageJsonPath, relativePackagePath, includeDefaults: false).ToArray();
    }

    public static IEnumerable<string> PackageScriptCommands(string packageJsonPath, string relativePackagePath, bool includeDefaults)
    {
        var packageDirectory = RelativeDirectory(relativePackagePath);
        if (!string.IsNullOrWhiteSpace(packageDirectory) && !IsSafeGeneratedCommandPath(packageDirectory))
        {
            return [];
        }

        var prefix = string.IsNullOrWhiteSpace(packageDirectory)
            ? ""
            : $" --prefix {QuoteCommandArgument(ToWindowsRelativeCommandPath(packageDirectory))}";
        if (!File.Exists(packageJsonPath))
        {
            return includeDefaults ? DefaultNpmScriptCommands(prefix) : [];
        }

        try
        {
            using var stream = new FileStream(
                packageJsonPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var json = ReadBoundedText(stream, AgentWorkspaceCoordinator.MaxWorkspaceProfileTextFileBytes);
            if (json is null)
            {
                return includeDefaults ? DefaultNpmScriptCommands(prefix) : [];
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
            {
                return includeDefaults ? DefaultNpmScriptCommands(prefix) : [];
            }

            var commands = new List<string>();
            foreach (var script in new[] { "build", "test", "lint", "preview", "serve", "dev", "start" })
            {
                if (scripts.TryGetProperty(script, out _))
                {
                    commands.Add(NpmScriptCommand(script, prefix));
                }
            }

            return commands.Count == 0 && includeDefaults ? DefaultNpmScriptCommands(prefix) : commands;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or NotSupportedException or JsonException)
        {
            return includeDefaults ? DefaultNpmScriptCommands(prefix) : [];
        }
    }

    public static string[] DefaultNpmScriptCommands(string prefix)
    {
        return [NpmScriptCommand("build", prefix), NpmScriptCommand("test", prefix)];
    }

    public static string NpmScriptCommand(string script, string prefix)
    {
        if (script.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            return $"npm{prefix} test";
        }

        return script.Equals("start", StringComparison.OrdinalIgnoreCase)
            ? $"npm{prefix} start"
            : $"npm{prefix} run {script}";
    }

    public static string PythonArtifactCommand(string markerPath)
    {
        var directory = RelativeDirectory(markerPath);
        if (!string.IsNullOrWhiteSpace(directory) && !IsSafeGeneratedCommandPath(directory))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(directory)
            ? "python -m pytest"
            : $"python -m pytest {QuoteCommandArgument(ToWindowsRelativeCommandPath(directory))}";
    }

    public static string RustArtifactCommand(string cargoPath)
    {
        if (!IsSafeGeneratedCommandPath(cargoPath))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(RelativeDirectory(cargoPath))
            ? "cargo test"
            : $"cargo test --manifest-path {QuoteCommandArgument(ToWindowsRelativeCommandPath(cargoPath))}";
    }

    public static string GoArtifactCommand(string goModPath)
    {
        var directory = RelativeDirectory(goModPath);
        if (!string.IsNullOrWhiteSpace(directory) && !IsSafeGeneratedCommandPath(directory))
        {
            return "";
        }

        return string.IsNullOrWhiteSpace(directory)
            ? "go test ./..."
            : $"go test {QuoteCommandArgument($"./{directory}/...")}";
    }

    public static bool RelativeFileNameEquals(string relativePath, string fileName)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var slashIndex = normalized.LastIndexOf('/');
        var name = slashIndex >= 0 ? normalized[(slashIndex + 1)..] : normalized;
        return name.Equals(fileName, StringComparison.OrdinalIgnoreCase);
    }

    public static string RelativeDirectory(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex <= 0 ? "" : normalized[..slashIndex];
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        var normalized = (relativePath ?? "").Trim().Trim('"', '\'').Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim('/');
    }

    public static string ToWindowsRelativeCommandPath(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath).Replace('/', '\\');
        return string.IsNullOrWhiteSpace(normalized) ? "." : $".\\{normalized}";
    }

    public static string QuoteCommandArgument(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (!IsSafeGeneratedCommandPath(trimmed))
        {
            throw new ArgumentException("Generated command paths may contain only letters, digits, spaces, dots, underscores, hyphens, and directory separators.", nameof(value));
        }

        return trimmed.Any(char.IsWhiteSpace)
            ? $"\"{trimmed.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : trimmed;
    }

    internal static bool IsSafeGeneratedCommandPath(string? value)
    {
        var normalized = (value ?? "").Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
        {
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0
            && parts.All(part => part is not "." and not "..")
            && normalized.All(character => char.IsLetterOrDigit(character)
                || character is ' ' or '.' or '_' or '-' or '/');
    }

    internal static string? ReadBoundedText(Stream stream, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || maximumBytes <= 0 || maximumBytes >= int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = new byte[(int)maximumBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maximumBytes)
        {
            return null;
        }

        using var content = new MemoryStream(buffer, 0, total, writable: false);
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
