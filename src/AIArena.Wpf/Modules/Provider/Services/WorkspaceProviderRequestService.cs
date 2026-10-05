using System.Collections.ObjectModel;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Models;

namespace AIArena.Wpf.Services;

internal sealed record WorkspaceProviderRequestPlan(ModelProviderConfig? Primary, ModelProviderConfig? Fallback);

/// <summary>Shares the Core routing policy and request settings with workspace calls.</summary>
internal static class WorkspaceProviderRequestService
{
    internal static async Task<ModelProviderConfig> ResolveRuntimeAsync(ModelProviderConfig config,
        IModelRuntimeEvidenceResolver? resolver, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resolver is null) return config;
        var evidence = await resolver.ResolveAsync(config, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ModelProviderRequests.Copy(config, runtimeEvidence: evidence, replaceRuntimeEvidence: true);
    }

    internal static IReadOnlyDictionary<string, WorkspaceProviderRequestPlan> ProjectRoutes(ArenaSnapshot snapshot)
    {
        var routes = new Dictionary<string, WorkspaceProviderRequestPlan>(StringComparer.OrdinalIgnoreCase);
        var shared = snapshot.Configs.GetValueOrDefault(ModelProviderRouting.SharedConfigKey);
        routes[ModelProviderRouting.SharedConfigKey] = new(
            shared is null ? null : ModelRuntimeSettingsRegistry.Resolve(snapshot, shared), null);
        foreach (var role in snapshot.Engine.Agents.Select(agent => agent.Id)
                     .Concat(ModelProviderProbePlan.ArenaRoles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var primary = ModelProviderRouting.Resolve(snapshot, role, out var fallback);
            routes[role] = new(primary, fallback);
        }
        return new ReadOnlyDictionary<string, WorkspaceProviderRequestPlan>(routes);
    }

    internal static WorkspaceProviderRequestPlan ForRole(ArenaViewSnapshot snapshot, string role, int outputTokens)
    {
        if (snapshot.CompletionRoutes is { } routes)
        {
            var plan = routes.GetValueOrDefault(role);
            return new(Copy(plan?.Primary, outputTokens), Copy(plan?.Fallback, outputTokens));
        }

        // Unmapped presentation snapshots remain usable by offline previews
        // and legacy in-process callers. Production mappings carry Core routes.
        var shared = snapshot.DefaultForUnassignedAgentsEnabled ? Clean(snapshot.ProviderModel) : "";
        var assigned = role.ToLowerInvariant() switch
        {
            "alpha" => snapshot.AlphaModel,
            "beta" => snapshot.BetaModel,
            "gamma" => snapshot.GammaModel,
            "delta" => snapshot.DeltaModel,
            "narrator" => snapshot.NarratorModel,
            _ => ""
        };
        var model = Clean(assigned);
        if (model.Length == 0) model = shared;
        return new(model.Length == 0 ? null : SharedConfig(snapshot, model, outputTokens),
            shared.Length > 0 && !shared.Equals(model, StringComparison.Ordinal)
                ? SharedConfig(snapshot, shared, outputTokens) : null);
    }

    internal static ModelProviderConfig SharedConfig(ArenaViewSnapshot snapshot, string model, int outputTokens)
    {
        var projected = snapshot.CompletionRoutes?.GetValueOrDefault(ModelProviderRouting.SharedConfigKey)?.Primary;
        var source = projected ?? new ModelProviderConfig
        {
            BaseUrl = string.IsNullOrWhiteSpace(snapshot.ProviderBaseUrl) || snapshot.ProviderBaseUrl == "-"
                ? ModelProviderDefaults.BaseUrl : snapshot.ProviderBaseUrl,
            ApiMode = ModelProviderApiModes.Normalize(snapshot.ProviderApiMode),
            ApiToken = snapshot.ProviderApiToken,
            Model = snapshot.ProviderModel,
            Timeout = Math.Clamp(snapshot.ProviderTimeout, 1, 3600),
            Temperature = snapshot.ProviderTemperature,
            ContextLength = snapshot.ProviderContextLength,
            Reasoning = snapshot.ProviderReasoning,
            NativeStatefulChat = snapshot.ProviderNativeStatefulChat,
            NativeIdleTtlSeconds = snapshot.ProviderNativeIdleTtlSeconds
        };
        var request = ModelProviderRequests.Copy(source, maxOutputTokens: outputTokens, model: model);
        if (projected is not null && projected.Model.Equals(model, StringComparison.Ordinal)) return request;

        var identity = ModelRuntimeSettingsRegistry.Identity(request);
        var registered = snapshot.ModelSettings.FirstOrDefault(settings => settings.ModelIdentity == identity);
        var tone = ModelResponseTones.NormalizeResponseTone(registered?.ResponseTone);
        return ModelProviderRequests.Copy(request, settings: new ModelRuntimeSettings
        {
            ModelIdentity = identity,
            ConfiguredContextWindow = registered is null
                ? projected is null ? snapshot.ProviderContextLength : 0
                : ModelRuntimeSettingsRegistry.ClampConfiguredContextWindow(registered.ConfiguredContextWindow),
            HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(registered?.HistoryPolicy),
            ResponseTone = tone,
            CustomTone = tone == ModelResponseTones.Custom
                ? ModelResponseTones.NormalizeCustomTone(registered?.CustomTone) : ""
        });
    }

    internal static IReadOnlyList<ModelChatMessage> ApplyTone(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> prompt)
    {
        var instruction = ModelResponseToneInstructions.Instruction(config.ResponseTone, config.CustomTone);
        return instruction.Length == 0 || prompt.Any(message => message.Role.Equals("system", StringComparison.OrdinalIgnoreCase)
                   && message.Content.Contains(instruction, StringComparison.Ordinal))
            ? prompt : ModelResponseToneInstructions.Apply(config, prompt, factoryMode: false);
    }

    private static ModelProviderConfig? Copy(ModelProviderConfig? config, int outputTokens) =>
        config is null ? null : ModelProviderRequests.Copy(config, maxOutputTokens: outputTokens);

    private static string Clean(string? model) => model?.Trim() is { } value && value != "-" ? value : "";
}
