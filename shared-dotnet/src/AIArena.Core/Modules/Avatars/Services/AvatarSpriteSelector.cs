using System.Text;

namespace AIArena.Core.Services;

public readonly record struct AvatarSpriteSelection(int Row, int Column, int TileIndex);

public sealed class AvatarSpriteManifest
{
    public string Name { get; init; } = "robot_heads_48";
    public int Columns { get; init; } = 12;
    public int Rows { get; init; } = 4;
    public int Total { get; init; } = 48;
    public Dictionary<string, AvatarSpriteRole> Roles { get; init; } = AvatarSpriteSelector.DefaultRoles();
}

public sealed class AvatarSpriteRole
{
    public int Row { get; init; }
    public List<int> Indices { get; init; } = [];
}

public static class AvatarSpriteSelector
{
    public const int Columns = 12;
    public const int Rows = 4;
    public const int Total = 48;

    public static AvatarSpriteManifest DefaultManifest { get; } = new()
    {
        Columns = Columns,
        Rows = Rows,
        Total = Total,
        Roles = DefaultRoles()
    };

    public static Dictionary<string, AvatarSpriteRole> DefaultRoles()
    {
        return new Dictionary<string, AvatarSpriteRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = new() { Row = 0, Indices = Enumerable.Range(0, Columns).ToList() },
            ["beta"] = new() { Row = 1, Indices = Enumerable.Range(Columns, Columns).ToList() },
            ["gamma"] = new() { Row = 2, Indices = Enumerable.Range(Columns * 2, Columns).ToList() },
            ["delta"] = new() { Row = 3, Indices = Enumerable.Range((Columns * 3) + 6, 6).ToList() },
            ["narrator"] = new() { Row = 3, Indices = Enumerable.Range(Columns * 3, Columns).ToList() }
        };
    }

    public static AvatarSpriteSelection Select(
        string? agentId,
        string? displayName,
        string? persona,
        string? model,
        AvatarSpriteManifest? manifest = null)
    {
        manifest = NormalizeManifest(manifest);
        var role = NormalizeRole(agentId, displayName);
        if (!manifest.Roles.TryGetValue(role, out var roleLayout) || roleLayout.Indices.Count == 0)
        {
            roleLayout = manifest.Roles["alpha"];
        }

        var seed = $"{agentId}|{displayName}|{persona}|{model}";
        var index = roleLayout.Indices[(int)(StableHash(seed) % (uint)roleLayout.Indices.Count)];
        var row = Math.Clamp(roleLayout.Row, 0, manifest.Rows - 1);
        var column = Math.Clamp(index % manifest.Columns, 0, manifest.Columns - 1);

        return new AvatarSpriteSelection(row, column, index);
    }

    public static AvatarSpriteManifest NormalizeManifest(AvatarSpriteManifest? manifest)
    {
        if (manifest is null || manifest.Columns <= 0 || manifest.Rows <= 0 || manifest.Total <= 0)
        {
            return DefaultManifest;
        }

        var roles = new Dictionary<string, AvatarSpriteRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, role) in manifest.Roles)
        {
            var indices = role.Indices
                .Where(index => index >= 0 && index < manifest.Total)
                .Distinct()
                .ToList();
            if (role.Row >= 0 && role.Row < manifest.Rows && indices.Count > 0)
            {
                roles[key] = new AvatarSpriteRole { Row = role.Row, Indices = indices };
            }
        }

        foreach (var (key, role) in DefaultRoles())
        {
            if (!roles.ContainsKey(key))
            {
                roles[key] = role;
            }
        }

        return new AvatarSpriteManifest
        {
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? DefaultManifest.Name : manifest.Name,
            Columns = manifest.Columns,
            Rows = manifest.Rows,
            Total = manifest.Total,
            Roles = roles
        };
    }

    private static string NormalizeRole(string? agentId, string? displayName)
    {
        var role = string.IsNullOrWhiteSpace(agentId) ? displayName : agentId;
        role = (role ?? "").Trim().ToLowerInvariant();

        if (role.Contains("alpha", StringComparison.OrdinalIgnoreCase))
        {
            return "alpha";
        }

        if (role.Contains("beta", StringComparison.OrdinalIgnoreCase))
        {
            return "beta";
        }

        if (role.Contains("gamma", StringComparison.OrdinalIgnoreCase))
        {
            return "gamma";
        }

        if (role.Contains("delta", StringComparison.OrdinalIgnoreCase))
        {
            return "delta";
        }

        if (role.Contains("narrator", StringComparison.OrdinalIgnoreCase))
        {
            return "narrator";
        }

        if (role.Contains("operator", StringComparison.OrdinalIgnoreCase))
        {
            return "operator";
        }

        return string.IsNullOrWhiteSpace(role) ? "unknown" : role;
    }

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var item in Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))
        {
            hash ^= item;
            hash *= prime;
        }

        return hash;
    }
}
