using System.Security.Cryptography;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf.Services;

/// <summary>
/// Builds the model-to-target checklist without exposing provider credentials or
/// hidden configuration fields. Assignment fingerprints make one checkbox change
/// causal without serializing the configured model value.
/// </summary>
internal static class ProviderModelAssignmentProjectionService
{
    public static ProviderModelAssignmentProjection Project(
        string sessionId,
        ArenaSnapshot snapshot,
        string model)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
            ? configured
            : new ModelProviderConfig();
        var targets = new List<ProviderModelAssignmentTarget>();
        var normalizedSessionId = (sessionId ?? "").Trim();
        var requestedModel = (model ?? "").Trim();
        targets.Add(new ProviderModelAssignmentTarget(
            ModelProviderRouting.SharedConfigKey,
            "Default",
            IsDefault: true,
            IsNarrator: false,
            Assigned: requestedModel.Length > 0
                && shared.Model.Trim().Equals(requestedModel, StringComparison.Ordinal),
            InheritsDefault: false,
            AssignmentFingerprint(shared.Model)));

        foreach (var id in ActiveParticipantIds(snapshot))
        {
            var agent = snapshot.Engine.Agents.First(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var configuredModel = ConfiguredRoleModel(snapshot.Configs, id, shared);
            targets.Add(new ProviderModelAssignmentTarget(
                id,
                string.IsNullOrWhiteSpace(agent.Name) ? AgentRosterService.DisplayName(id) : agent.Name.Trim(),
                IsDefault: false,
                IsNarrator: false,
                Assigned: requestedModel.Length > 0
                    && configuredModel.Equals(requestedModel, StringComparison.Ordinal),
                InheritsDefault: configuredModel.Length == 0,
                AssignmentFingerprint(configuredModel)));
        }

        var narratorModel = ConfiguredRoleModel(snapshot.Configs, "narrator", shared);
        targets.Add(new ProviderModelAssignmentTarget(
            "narrator",
            "Narrator",
            IsDefault: false,
            IsNarrator: true,
            Assigned: requestedModel.Length > 0
                && narratorModel.Equals(requestedModel, StringComparison.Ordinal),
            InheritsDefault: narratorModel.Length == 0,
            AssignmentFingerprint(narratorModel)));
        return new ProviderModelAssignmentProjection(
            normalizedSessionId,
            ProviderModelCatalogProjectionService.ProviderFingerprint(normalizedSessionId, snapshot),
            snapshot.PersistenceRevision,
            ProviderModelCatalogProjectionService.SafeModelIdentifier(requestedModel),
            targets);
    }

    public static IReadOnlyList<string> ActiveTargetIds(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new[] { ModelProviderRouting.SharedConfigKey }
            .Concat(ActiveParticipantIds(snapshot))
            .Append("narrator")
            .ToArray();
    }

    internal static string AssignmentFingerprint(string configuredModel)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((configuredModel ?? "").Trim()));
        return Convert.ToHexString(bytes);
    }

    internal static string ConfiguredRoleModel(
        IReadOnlyDictionary<string, ModelProviderConfig> configs,
        string role,
        ModelProviderConfig shared)
    {
        return configs.TryGetValue(role, out var config)
            && !string.IsNullOrWhiteSpace(config.Model)
            && !config.Model.Trim().Equals(shared.Model.Trim(), StringComparison.Ordinal)
            ? config.Model.Trim()
            : "";
    }

    private static IReadOnlyList<string> ActiveParticipantIds(ArenaSnapshot snapshot)
    {
        var active = snapshot.Engine.Agents
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .Select(agent => agent.Id.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AgentRosterService.ParticipantIds.Where(active.Contains).ToArray();
    }
}
