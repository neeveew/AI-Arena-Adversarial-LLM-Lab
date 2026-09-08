using System.Diagnostics;

using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

/// <summary>
/// Runs provider diagnostics without depending on WPF controls. Results are safe
/// to expose through the local control plane: provider credentials are never
/// returned, free-form text is redacted and bounded, and model lists are capped.
/// </summary>
public sealed class ProviderRuntimeService
{
    private const int MaximumReplyLength = 240;
    private const int MaximumErrorLength = 480;
    private const int MaximumStatusLength = 240;
    private const int MaximumModelLength = 192;
    private const int MaximumModelCount = 256;
    private readonly SessionStore sessionStore;
    private readonly ModelProviderHealthService providerHealth;
    private readonly ProviderReachabilityService providerReachability;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public ProviderRuntimeService(
        SessionStore sessionStore,
        ModelProviderHealthService providerHealth,
        ProviderReachabilityService providerReachability)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(providerHealth);
        ArgumentNullException.ThrowIfNull(providerReachability);

        this.sessionStore = sessionStore;
        this.providerHealth = providerHealth;
        this.providerReachability = providerReachability;
    }

    public async Task<ProviderRuntimeTestResult> TestAsync(
        string? sessionId,
        bool allRoles = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ProviderRuntimeTestResult.Unavailable("No active provider session is available.");
        }

        if (!await operationGate.WaitAsync(0, cancellationToken))
        {
            return ProviderRuntimeTestResult.OperationBusy();
        }

        try
        {
            // Keep this exact snapshot for both the probe and PersistAsync. Passing
            // a freshly loaded snapshot after the await could attach a stale probe
            // result to a provider configuration that was edited in the meantime.
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId.Trim(), cancellationToken);
            if (snapshot is null
                || !snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var sharedConfig))
            {
                return ProviderRuntimeTestResult.Unavailable("No shared provider configuration is available.");
            }

            var elapsed = Stopwatch.StartNew();
            var roleResults = await ProbeSnapshotAsync(snapshot, allRoles, cancellationToken);
            var probeOk = roleResults.Count > 0 && roleResults.All(result => result.Ok);
            var reachable = roleResults.Any(result => result.Ok);
            ModelProviderHealth? health = null;
            if (!reachable)
            {
                health = await providerHealth.CheckAsync(
                    ProviderReachabilityService.HealthProbeConfig(sharedConfig),
                    cancellationToken);
                reachable = health.Ok;
            }

            elapsed.Stop();
            var safeFailure = FailureSummary(roleResults, sharedConfig.ApiToken);
            var persistenceError = probeOk
                ? ""
                : reachable
                    ? safeFailure
                    : SanitizeText(
                        string.IsNullOrWhiteSpace(health?.Error) ? safeFailure : health.Error,
                        sharedConfig.ApiToken,
                        MaximumErrorLength);
            var status = TestStatus(allRoles, probeOk, reachable);
            var primaryLatency = roleResults.Count == 0 ? 0 : roleResults[0].LatencyMs;
            var persistResult = await providerReachability.PersistAsync(
                sessionId.Trim(),
                online: probeOk,
                error: persistenceError,
                latencyMs: primaryLatency,
                status: status,
                snapshot: snapshot,
                cancellationToken: cancellationToken,
                additionalIdentityMatches: latest => ProbeConfigurationMatches(snapshot, latest, allRoles));

            var persisted = persistResult is not null
                && persistResult.Status.Equals(status, StringComparison.Ordinal);
            var stale = !persisted;
            var finalError = stale
                ? "Provider configuration changed while the test was running; the stale result was not saved."
                : persistenceError;
            var checkedAt = health?.CheckedAt
                ?? (roleResults.Count == 0 ? DateTimeOffset.Now : roleResults.Max(result => result.CheckedAt));

            return new ProviderRuntimeTestResult(
                Available: true,
                Busy: false,
                Ok: probeOk && persisted,
                Reachable: reachable,
                Persisted: persisted,
                SnapshotChanged: persistResult?.SnapshotChanged == true,
                Stale: stale,
                Status: SanitizeText(stale ? finalError : status, sharedConfig.ApiToken, MaximumStatusLength),
                BaseUrl: SafeProviderEndpoint(sharedConfig.BaseUrl, sharedConfig.ApiToken),
                ApiMode: ModelProviderApiModes.Normalize(sharedConfig.ApiMode),
                Model: SanitizeText(sharedConfig.Model, sharedConfig.ApiToken, MaximumModelLength),
                LatencyMs: (int)Math.Min(int.MaxValue, Math.Max(0, elapsed.ElapsedMilliseconds)),
                CheckedAt: checkedAt,
                ModelCount: health?.ModelCount,
                Reply: roleResults.Count == 0 ? "" : roleResults[0].Reply,
                Error: SanitizeText(finalError, sharedConfig.ApiToken, MaximumErrorLength),
                RoleResults: ReadOnly(roleResults))
            {
                CapturedSessionId = sessionId.Trim(),
                ConfigurationIdentity = ProviderConfigurationControlService.ConfigurationIdentity(snapshot)
            };
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ProviderRuntimeModelsResult> RefreshModelsAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return ProviderRuntimeModelsResult.Unavailable("No active provider session is available.");
        }

        if (!await operationGate.WaitAsync(0, cancellationToken))
        {
            return ProviderRuntimeModelsResult.OperationBusy();
        }

        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId.Trim(), cancellationToken);
            if (snapshot is null
                || !snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var sharedConfig))
            {
                return ProviderRuntimeModelsResult.Unavailable("No shared provider configuration is available.");
            }

            var elapsed = Stopwatch.StartNew();
            var discoveryConfig = new ModelProviderConfig
            {
                BaseUrl = sharedConfig.BaseUrl,
                ApiMode = sharedConfig.ApiMode,
                ApiToken = sharedConfig.ApiToken,
                Model = sharedConfig.Model,
                Timeout = Math.Clamp(Math.Min(sharedConfig.Timeout, 30), 1, 30),
                Temperature = sharedConfig.Temperature,
                MaxOutputTokens = sharedConfig.MaxOutputTokens,
                ContextLength = sharedConfig.ContextLength,
                Reasoning = sharedConfig.Reasoning,
                NativeStatefulChat = sharedConfig.NativeStatefulChat,
                NativeIdleTtlSeconds = sharedConfig.NativeIdleTtlSeconds
            };
            var result = await providerHealth.ListModelsAsync(discoveryConfig, cancellationToken);
            elapsed.Stop();

            var safeModels = result.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => SanitizeText(model, sharedConfig.ApiToken, MaximumModelLength))
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var returnedModels = safeModels.Take(MaximumModelCount).ToArray();
            var status = result.Ok
                ? $"Provider model discovery completed with {safeModels.Length} model(s)."
                : "Provider model discovery failed.";

            return new ProviderRuntimeModelsResult(
                Available: true,
                Busy: false,
                Ok: result.Ok,
                Status: status,
                BaseUrl: SafeProviderEndpoint(
                    string.IsNullOrWhiteSpace(result.BaseUrl) ? sharedConfig.BaseUrl : result.BaseUrl,
                    sharedConfig.ApiToken),
                ApiMode: ModelProviderApiModes.Normalize(sharedConfig.ApiMode),
                Model: SanitizeText(sharedConfig.Model, sharedConfig.ApiToken, MaximumModelLength),
                LatencyMs: (int)Math.Min(int.MaxValue, Math.Max(0, elapsed.ElapsedMilliseconds)),
                CheckedAt: result.CheckedAt,
                ModelCount: safeModels.Length,
                Truncated: safeModels.Length > returnedModels.Length,
                Models: ReadOnly(returnedModels),
                Error: SanitizeText(result.Error, sharedConfig.ApiToken, MaximumErrorLength))
            {
                CapturedSessionId = sessionId.Trim(),
                ConfigurationIdentity = ProviderConfigurationControlService.ConfigurationIdentity(snapshot)
            };
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<IReadOnlyList<ProviderRuntimeRoleTestResult>> ProbeSnapshotAsync(
        ArenaSnapshot snapshot, bool allRoles, CancellationToken cancellationToken = default)
    {
        var roleResults = new List<ProviderRuntimeRoleTestResult>();
        foreach (var plan in ModelProviderProbePlan.Build(snapshot, allRoles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.Config is not { } config || string.IsNullOrWhiteSpace(config.Model))
            {
                roleResults.Add(new ProviderRuntimeRoleTestResult(
                    plan.Roles, false, "", "", "", 0, DateTimeOffset.Now, "",
                    "No model is assigned to this role; default inheritance is disabled or unavailable."));
                continue;
            }
            var result = await providerHealth.TestCompletionAsync(config, cancellationToken);
            roleResults.Add(new ProviderRuntimeRoleTestResult(
                ReadOnly(plan.Roles), result.Ok, SafeProviderEndpoint(result.BaseUrl, config.ApiToken),
                ModelProviderApiModes.Normalize(config.ApiMode),
                SanitizeText(result.Model, config.ApiToken, MaximumModelLength),
                Math.Max(0, result.LatencyMs), result.CheckedAt,
                SanitizeText(result.Text, config.ApiToken, MaximumReplyLength),
                ProviderErrorSanitizer.Sanitize(result.Error, config.ApiToken)));
        }
        return ReadOnly(roleResults);
    }

    private static bool ProbeConfigurationMatches(ArenaSnapshot captured, ArenaSnapshot latest, bool allRoles)
    {
        var before = ModelProviderProbePlan.Build(captured, allRoles);
        var after = ModelProviderProbePlan.Build(latest, allRoles);
        return before.Count == after.Count && before.Zip(after).All(pair =>
            pair.First.Roles.SequenceEqual(pair.Second.Roles, StringComparer.Ordinal)
            && ModelProviderProbePlan.SameRequest(pair.First.Config, pair.Second.Config));
    }
    private static string FailureSummary(
        IReadOnlyList<ProviderRuntimeRoleTestResult> roleResults,
        string apiToken)
    {
        var failures = roleResults
            .Where(result => !result.Ok)
            .Select(result =>
            {
                var roles = result.Roles.Count == 0 ? "provider" : string.Join("/", result.Roles);
                var detail = string.IsNullOrWhiteSpace(result.Error) ? "completion test failed" : result.Error;
                return $"{roles} ({result.Model}): {detail}";
            });
        return SanitizeText(string.Join(" | ", failures), apiToken, MaximumErrorLength);
    }

    private static string TestStatus(bool allRoles, bool ok, bool reachable)
    {
        if (ok)
        {
            return allRoles
                ? "All distinct provider role models passed their completion test."
                : "Provider online.";
        }

        if (reachable)
        {
            return allRoles
                ? "Provider reachable; one or more role-model completion tests failed."
                : "Provider reachable; completion test failed.";
        }

        return "Provider offline.";
    }

    private static string SafeProviderEndpoint(string value, string apiToken) =>
        Limit(ProviderErrorSanitizer.Endpoint(value, apiToken), MaximumModelLength);

    private static string SanitizeText(string? value, string apiToken, int maximumLength) =>
        Limit(ProviderErrorSanitizer.Sanitize(value, apiToken), maximumLength);
    private static string Limit(string value, int maximumLength)
    {
        return value.Length <= maximumLength
            ? value
            : value[..(maximumLength - 3)].TrimEnd() + "...";
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
    {
        return Array.AsReadOnly(values.ToArray());
    }


}

public sealed record ProviderRuntimeRoleTestResult(
    IReadOnlyList<string> Roles,
    bool Ok,
    string BaseUrl,
    string ApiMode,
    string Model,
    int LatencyMs,
    DateTimeOffset CheckedAt,
    string Reply,
    string Error);

public sealed record ProviderRuntimeTestResult(
    bool Available,
    bool Busy,
    bool Ok,
    bool Reachable,
    bool Persisted,
    bool SnapshotChanged,
    bool Stale,
    string Status,
    string BaseUrl,
    string ApiMode,
    string Model,
    int LatencyMs,
    DateTimeOffset? CheckedAt,
    int? ModelCount,
    string Reply,
    string Error,
    IReadOnlyList<ProviderRuntimeRoleTestResult> RoleResults)
{
    internal string CapturedSessionId { get; init; } = "";

    internal string ConfigurationIdentity { get; init; } = "";

    public static ProviderRuntimeTestResult Unavailable(string message)
    {
        return Empty(available: false, busy: false, message);
    }

    public static ProviderRuntimeTestResult OperationBusy()
    {
        return Empty(available: true, busy: true, "Another provider operation is already running.");
    }

    private static ProviderRuntimeTestResult Empty(bool available, bool busy, string message)
    {
        return new ProviderRuntimeTestResult(
            available,
            busy,
            Ok: false,
            Reachable: false,
            Persisted: false,
            SnapshotChanged: false,
            Stale: false,
            Status: message,
            BaseUrl: "",
            ApiMode: "",
            Model: "",
            LatencyMs: 0,
            CheckedAt: null,
            ModelCount: null,
            Reply: "",
            Error: message,
            RoleResults: Array.AsReadOnly(Array.Empty<ProviderRuntimeRoleTestResult>()));
    }
}

public sealed record ProviderRuntimeModelsResult(
    bool Available,
    bool Busy,
    bool Ok,
    string Status,
    string BaseUrl,
    string ApiMode,
    string Model,
    int LatencyMs,
    DateTimeOffset? CheckedAt,
    int ModelCount,
    bool Truncated,
    IReadOnlyList<string> Models,
    string Error)
{
    internal string CapturedSessionId { get; init; } = "";

    internal string ConfigurationIdentity { get; init; } = "";

    public static ProviderRuntimeModelsResult Unavailable(string message)
    {
        return Empty(available: false, busy: false, message);
    }

    public static ProviderRuntimeModelsResult OperationBusy()
    {
        return Empty(available: true, busy: true, "Another provider operation is already running.");
    }

    private static ProviderRuntimeModelsResult Empty(bool available, bool busy, string message)
    {
        return new ProviderRuntimeModelsResult(
            available,
            busy,
            Ok: false,
            Status: message,
            BaseUrl: "",
            ApiMode: "",
            Model: "",
            LatencyMs: 0,
            CheckedAt: null,
            ModelCount: 0,
            Truncated: false,
            Models: Array.AsReadOnly(Array.Empty<string>()),
            Error: message);
    }
}
