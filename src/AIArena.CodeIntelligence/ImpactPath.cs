using System.Security.Cryptography;
using System.Text;

namespace AIArena.CodeIntelligence;

internal sealed class ImpactPath
{
    private readonly string _root;
    private readonly string _rootPrefix;

    public ImpactPath(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root)
            && (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The analysis root cannot be a reparse point.");
        }
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    public string Root => _root;

    public bool TryRelative(string? path, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (TraversesReparsePoint(full))
        {
            return false;
        }

        var candidate = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (candidate == ".")
        {
            relative = "";
            return true;
        }
        if (Path.IsPathRooted(candidate)
            || candidate.Equals("..", StringComparison.Ordinal)
            || candidate.StartsWith("../", StringComparison.Ordinal)
            || candidate.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0))
        {
            return false;
        }

        relative = candidate;
        return true;
    }

    public string RequireRelative(string path) =>
        TryRelative(path, out var relative)
            ? relative
            : throw new ArgumentException("The path must resolve inside the analysis root.", nameof(path));

    public bool TryResolveRelative(string relativePath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\0'))
        {
            return false;
        }
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0))
        {
            return false;
        }
        return TryContainedFullPath(Path.Combine(
            _root,
            normalized.Replace('/', Path.DirectorySeparatorChar)), out fullPath);
    }

    public bool TryContainedFullPath(string path, out string fullPath)
    {
        fullPath = "";
        if (!TryRelative(path, out _))
        {
            return false;
        }
        fullPath = Path.GetFullPath(path);
        return true;
    }

    public static string StableId(string prefix, params string?[] values)
    {
        var canonical = string.Join("\n", values.Select(value => value ?? ""));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{prefix}:{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    public static string CaseInsensitiveIdentity(string? value) =>
        (value ?? "").Trim().Replace('\\', '/').ToUpperInvariant();

    private bool TraversesReparsePoint(string fullPath)
    {
        if (fullPath.Equals(_root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                return true;
            }
        }
        return false;
    }
}
