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
        return CreateBatch(sessionId, snapshot).Project(model);
    }

    public static ProviderModelAssignmentProjectionBatch CreateBatch(
        string sessionId,
        ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
            ? configured
            : new ModelProviderConfig();
        var defaultEnabled = snapshot.Engine.DefaultForUnassignedAgentsEnabled
            && !string.IsNullOrWhiteSpace(shared.Model);
        var targets = new List<ProviderModelAssignmentTargetTemplate>();
        var normalizedSessionId = (sessionId ?? "").Trim();
        targets.Add(new ProviderModelAssignmentTargetTemplate(
            ModelProviderRouting.SharedConfigKey,
            "Default",
            IsDefault: true,
            IsNarrator: false,
            ConfiguredModel: defaultEnabled ? shared.Model.Trim() : "",
            InheritsDefault: false,
            AssignmentFingerprint(shared.Model, defaultEnabled),
            ServerIdentity(shared)));

        foreach (var id in ActiveParticipantIds(snapshot))
        {
            var agent = snapshot.Engine.Agents.First(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var configuredModel = ConfiguredRoleModel(snapshot.Configs, id, shared);
            var inheritsDefault = defaultEnabled && configuredModel.Length == 0;
            targets.Add(new ProviderModelAssignmentTargetTemplate(
                id,
                string.IsNullOrWhiteSpace(agent.Name) ? AgentRosterService.DisplayName(id) : agent.Name.Trim(),
                IsDefault: false,
                IsNarrator: false,
                ConfiguredModel: configuredModel,
                InheritsDefault: inheritsDefault,
                AssignmentFingerprint(configuredModel, configuredModel.Length > 0,
                    snapshot.Configs.GetValueOrDefault(id)),
                ServerIdentity(snapshot.Configs.GetValueOrDefault(id) ?? shared)));
        }

        var narratorModel = ConfiguredRoleModel(snapshot.Configs, "narrator", shared);
        var narratorInheritsDefault = defaultEnabled && narratorModel.Length == 0;
        targets.Add(new ProviderModelAssignmentTargetTemplate(
            "narrator",
            "Narrator",
            IsDefault: false,
            IsNarrator: true,
            ConfiguredModel: narratorModel,
            InheritsDefault: narratorInheritsDefault,
            AssignmentFingerprint(narratorModel, narratorModel.Length > 0,
                snapshot.Configs.GetValueOrDefault("narrator")),
            ServerIdentity(snapshot.Configs.GetValueOrDefault("narrator") ?? shared)));

        return new ProviderModelAssignmentProjectionBatch(
            normalizedSessionId,
            ProviderModelCatalogProjectionService.ProviderFingerprint(normalizedSessionId, snapshot),
            snapshot.PersistenceRevision,
            targets);
    }

    public static IReadOnlyList<ProviderModelAssignmentProjection> ProjectMany(
        string sessionId,
        ArenaSnapshot snapshot,
        IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return CreateBatch(sessionId, snapshot).ProjectMany(models);
    }

    public static IReadOnlyList<string> ActiveTargetIds(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new[] { ModelProviderRouting.SharedConfigKey }
            .Concat(ActiveParticipantIds(snapshot))
            .Append("narrator")
            .ToArray();
    }

    internal static string AssignmentFingerprint(string configuredModel, bool explicitlyAssigned, ModelProviderConfig? config = null)
    {
        var identity = $"{(explicitlyAssigned ? "explicit" : "inherit")}\n{(configuredModel ?? "").Trim()}";
        if (explicitlyAssigned && config is not null)
            identity += "\n" + ProviderModelCatalogProjectionService.ConnectionFingerprint("", config);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(bytes);
    }

    internal static string AssignmentFingerprint(string configuredModel) =>
        AssignmentFingerprint(configuredModel, !string.IsNullOrWhiteSpace(configuredModel));

    internal static string ConfiguredRoleModel(
        IReadOnlyDictionary<string, ModelProviderConfig> configs,
        string role,
        ModelProviderConfig shared)
    {
        if (!configs.TryGetValue(role, out var config)
            || string.IsNullOrWhiteSpace(config.Model))
        {
            return "";
        }

        var model = config.Model.Trim();
        return config.ExplicitModelAssignment
            || !model.Equals(shared.Model.Trim(), StringComparison.Ordinal)
            || !ServerIdentity(config).Equals(ServerIdentity(shared), StringComparison.Ordinal)
                ? model
                : "";
    }

    internal static string ServerIdentity(ModelProviderConfig config) =>
        ProviderServerInventory.ServerIdentity(config);

    private static IReadOnlyList<string> ActiveParticipantIds(ArenaSnapshot snapshot)
    {
        var active = snapshot.Engine.Agents
            .Where(agent => agent.Active && AgentRosterService.IsParticipantId(agent.Id))
            .Select(agent => agent.Id.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AgentRosterService.ParticipantIds.Where(active.Contains).ToArray();
    }
}

internal sealed class ProviderModelAssignmentProjectionBatch(
    string sessionId,
    string providerFingerprint,
    long persistenceRevision,
    IReadOnlyList<ProviderModelAssignmentTargetTemplate> targetTemplates)
{
    public string SessionId { get; } = sessionId;

    public string ProviderFingerprint { get; } = providerFingerprint;

    public long PersistenceRevision { get; } = persistenceRevision;

    public ProviderModelAssignmentProjection Project(
        string model,
        IEnumerable<string>? aliases = null,
        ModelProviderConfig? sourceConfig = null)
    {
        var requestedModel = (model ?? "").Trim();
        var equivalentModels = (aliases ?? [])
            .Append(requestedModel)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => new[]
            {
                value.Trim(),
                ProviderModelCatalogProjectionService.SafeModelIdentifier(value)
            })
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = targetTemplates.Select(target => new ProviderModelAssignmentTarget(
            target.Id,
            target.DisplayName,
            target.IsDefault,
            target.IsNarrator,
            Assigned: (sourceConfig is null || target.ServerIdentity.Equals(
                ProviderModelAssignmentProjectionService.ServerIdentity(sourceConfig), StringComparison.Ordinal))
                && target.ConfiguredModel.Length > 0
                && (equivalentModels.Contains(target.ConfiguredModel)
                    || equivalentModels.Contains(ProviderModelCatalogProjectionService.SafeModelIdentifier(
                        target.ConfiguredModel))),
            target.InheritsDefault,
            target.AssignmentFingerprint)).ToArray();
        return new ProviderModelAssignmentProjection(
            SessionId,
            ProviderFingerprint,
            PersistenceRevision,
            ProviderModelCatalogProjectionService.SafeModelIdentifier(requestedModel),
            targets);
    }

    public IReadOnlyList<ProviderModelAssignmentProjection> ProjectMany(IEnumerable<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.Select(model => Project(model)).ToArray();
    }
}

internal sealed record ProviderModelAssignmentTargetTemplate(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsNarrator,
    string ConfiguredModel,
    bool InheritsDefault,
    string AssignmentFingerprint,
    string ServerIdentity);
