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
            // Legacy snapshots predate ExplicitModelAssignment. They retain
            // distinct role models as explicit assignments. The persisted bit
            // is required only to distinguish a deliberate same-as-shared
            // assignment from an inherited generation-override carrier.
            var explicitlyAssigned = specific.ExplicitModelAssignment
                || fallbackConfig is null
                || !specific.Model.Trim().Equals(fallbackConfig.Model.Trim(), StringComparison.Ordinal);
            if (!snapshot.Engine.DefaultForUnassignedAgentsEnabled && !explicitlyAssigned)
            {
                fallbackConfig = null;
                return null;
            }

            if (!snapshot.Engine.DefaultForUnassignedAgentsEnabled)
            {
                fallbackConfig = null;
                return ModelRuntimeSettingsRegistry.Resolve(snapshot, specific);
            }

            if (fallbackConfig is not null && string.Equals(specific.Model, fallbackConfig.Model, StringComparison.Ordinal))
            {
                fallbackConfig = null;
            }

            fallbackConfig = fallbackConfig is null
                ? null
                : ModelRuntimeSettingsRegistry.Resolve(snapshot, fallbackConfig);
            return ModelRuntimeSettingsRegistry.Resolve(snapshot, specific);
        }

        fallbackConfig = null;
        if (!snapshot.Engine.DefaultForUnassignedAgentsEnabled)
        {
            return null;
        }

        var resolved = snapshot.Configs.TryGetValue(SharedConfigKey, out shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
        return resolved is null ? null : ModelRuntimeSettingsRegistry.Resolve(snapshot, resolved);
    }
}
