using AIArena.Core.Models;

namespace AIArena.Core.Providers;

public static class ModelProviderRouting
{
    public const string SharedConfigKey = "shared";

    public static ModelProviderConfig? Resolve(
        ArenaSnapshot snapshot,
        string agentId,
        out ModelProviderConfig? fallbackConfig)
    {
        fallbackConfig = snapshot.Configs.TryGetValue(SharedConfigKey, out var shared) ? shared : null;
        if (snapshot.Configs.TryGetValue(agentId, out var specific) && !string.IsNullOrWhiteSpace(specific.Model))
        {
            if (fallbackConfig is not null && string.Equals(specific.Model, fallbackConfig.Model, StringComparison.OrdinalIgnoreCase))
            {
                fallbackConfig = null;
            }

            return specific;
        }

        fallbackConfig = null;
        return snapshot.Configs.TryGetValue(SharedConfigKey, out shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
    }
}
