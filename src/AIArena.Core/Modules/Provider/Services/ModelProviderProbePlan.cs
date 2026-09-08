using AIArena.Core.Models;

namespace AIArena.Core.Providers;

/// <summary>Uses the execution router, including unavailable assignments, for provider diagnostics.</summary>
public sealed record ModelProviderProbePlan(ModelProviderConfig? Config, IReadOnlyList<string> Roles)
{
    public static readonly IReadOnlyList<string> ArenaRoles = Array.AsReadOnly(new[] { "alpha", "beta", "gamma", "delta", "narrator" });

    public static IReadOnlyList<ModelProviderProbePlan> Build(ArenaSnapshot snapshot, bool allRoles)
    {
        var plans = new List<ModelProviderProbePlan>();
        if (!allRoles)
        {
            var shared = snapshot.Configs.GetValueOrDefault(ModelProviderRouting.SharedConfigKey);
            plans.Add(new(shared is null ? null : ModelRuntimeSettingsRegistry.Resolve(snapshot, shared), [ModelProviderRouting.SharedConfigKey]));
            return plans.AsReadOnly();
        }

        foreach (var role in ArenaRoles)
        {
            var config = ModelProviderRouting.Resolve(snapshot, role, out _);
            var existing = plans.FindIndex(plan => SameRequest(plan.Config, config));
            if (existing < 0)
                plans.Add(new(config, [role]));
            else
                plans[existing] = plans[existing] with { Roles = Array.AsReadOnly(plans[existing].Roles.Append(role).ToArray()) };
        }
        return plans.AsReadOnly();
    }

    public static bool SameRequest(ModelProviderConfig? left, ModelProviderConfig? right)
    {
        if (left is null || right is null) return left is null && right is null;
        // The health probe fixes temperature/output tokens but preserves runtime settings.
        return left.BaseUrl.Trim().TrimEnd('/').Equals(right.BaseUrl.Trim().TrimEnd('/'), StringComparison.Ordinal)
            && ModelProviderApiModes.Normalize(left.ApiMode) == ModelProviderApiModes.Normalize(right.ApiMode)
            && left.ApiToken.Trim().Equals(right.ApiToken.Trim(), StringComparison.Ordinal)
            && left.Model.Trim().Equals(right.Model.Trim(), StringComparison.Ordinal)
            && left.Timeout == right.Timeout
            && left.ContextLength == right.ContextLength
            && left.ConfiguredContextWindow == right.ConfiguredContextWindow
            && left.HistoryPolicy == right.HistoryPolicy
            && left.ResponseTone == right.ResponseTone
            && left.CustomTone == right.CustomTone
            && ModelProviderReasoningModes.Normalize(left.Reasoning) == ModelProviderReasoningModes.Normalize(right.Reasoning)
            && left.NativeStatefulChat == right.NativeStatefulChat
            && left.NativeIdleTtlSeconds == right.NativeIdleTtlSeconds;
    }
}
