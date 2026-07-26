using System.Diagnostics;
using System.Net.Sockets;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public sealed class ProviderReachabilityService
{
    private const int StatusPersistAttempts = 3;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ModelProviderHealthService providerHealth;
    private int isRefreshing;

    public ProviderReachabilityService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ModelProviderHealthService providerHealth)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerHealth = providerHealth;
    }

    public async Task<ModelProviderConfig?> LoadSharedConfigAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        return snapshot.Configs.TryGetValue("shared", out var shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
    }

    public async Task<ProviderReachabilityRefreshResult?> RefreshAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref isRefreshing, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
            if (snapshot is null || !snapshot.Configs.TryGetValue("shared", out var shared))
            {
                return null;
            }

            var socket = await ProbeSocketAsync(shared, cancellationToken);
            if (!socket.Ok)
            {
                var checkedAt = DateTimeOffset.Now;
                var persist = await PersistAsync(
                    sessionId,
                    online: false,
                    socket.Error,
                    socket.LatencyMs,
                    "Provider offline.",
                    snapshot,
                    cancellationToken);
                if (persist is null)
                {
                    return null;
                }

                return new ProviderReachabilityRefreshResult(
                    sessionId,
                    persist.Status,
                    checkedAt,
                    ModelCount: null,
                    NextInterval: TimeSpan.FromSeconds(3),
                    SnapshotChanged: persist.SnapshotChanged);
            }

            if (!shared.LastTestOk)
            {
                var health = await providerHealth.CheckAsync(HealthProbeConfig(shared), cancellationToken);
                var readiness = UntestedProviderReadiness(shared, health.Ok, health.Error);
                var persist = await PersistAsync(
                    sessionId,
                    readiness.Online,
                    readiness.Error,
                    socket.LatencyMs,
                    readiness.Status,
                    snapshot,
                    cancellationToken);
                if (persist is null)
                {
                    return null;
                }

                return new ProviderReachabilityRefreshResult(
                    sessionId,
                    persist.Status,
                    health.CheckedAt,
                    health.ModelCount,
                    readiness.NextInterval,
                    SnapshotChanged: persist.SnapshotChanged);
            }

            var onlineAt = DateTimeOffset.Now;
            var onlinePersist = await PersistAsync(
                sessionId,
                online: true,
                "",
                socket.LatencyMs,
                "Provider online.",
                snapshot,
                cancellationToken);
            if (onlinePersist is null)
            {
                return null;
            }

            return new ProviderReachabilityRefreshResult(
                sessionId,
                onlinePersist.Status,
                onlineAt,
                ModelCount: null,
                NextInterval: TimeSpan.FromSeconds(10),
                SnapshotChanged: onlinePersist.SnapshotChanged);
        }
        finally
        {
            Volatile.Write(ref isRefreshing, 0);
        }
    }

    public async Task<ProviderReachabilityPersistResult?> PersistAsync(
        string? sessionId,
        bool online,
        string error,
        int latencyMs,
        string status,
        ArenaSnapshot? snapshot = null,
        CancellationToken cancellationToken = default,
        Func<ArenaSnapshot, bool>? additionalIdentityMatches = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        error ??= "";
        var nextInterval = online ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
        var candidate = snapshot;
        ProviderIdentity? probedIdentity = null;
        for (var attempt = 0; attempt < StatusPersistAttempts; attempt++)
        {
            candidate ??= await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
            if (candidate is null || !candidate.Configs.TryGetValue("shared", out var shared))
            {
                return null;
            }

            probedIdentity ??= ProviderIdentity.From(shared);
            if (!probedIdentity.Matches(shared))
            {
                // A reachability result is valid only for the endpoint/model that
                // was actually probed. A concurrent provider edit deliberately
                // resets readiness and must win over this stale continuation.
                return null;
            }

            if (additionalIdentityMatches is not null && !additionalIdentityMatches(candidate))
            {
                return null;
            }

            if (shared.LastTestOk == online
                && (shared.LastError ?? "").Equals(error, StringComparison.Ordinal))
            {
                // A supplied snapshot can already contain the requested status but
                // still be stale. Re-read before reporting success so an identity
                // change cannot leak an old provider's status into the current UI.
                if (snapshot is not null)
                {
                    var latest = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
                    if (latest is null || !latest.Configs.TryGetValue("shared", out var latestShared))
                    {
                        return null;
                    }

                    if (!probedIdentity.Matches(latestShared))
                    {
                        return null;
                    }

                    if (additionalIdentityMatches is not null && !additionalIdentityMatches(latest))
                    {
                        return null;
                    }

                    if (latestShared.LastTestOk != online
                        || !(latestShared.LastError ?? "").Equals(error, StringComparison.Ordinal))
                    {
                        candidate = latest;
                        continue;
                    }
                }

                return new ProviderReachabilityPersistResult(sessionId, status, nextInterval, SnapshotChanged: false);
            }

            candidate.Configs["shared"] = CopyConfigWithStatus(shared, online, error, latencyMs);
            try
            {
                await sessionStore.SaveSnapshotAsync(candidate, sessionId, cancellationToken);
                await eventLogStore.AppendAsync(sessionId, "native_provider_reachability_changed", new
                {
                    Online = online,
                    Error = error,
                    LatencyMs = latencyMs
                }, cancellationToken);

                return new ProviderReachabilityPersistResult(sessionId, status, nextInterval, SnapshotChanged: true);
            }
            catch (SnapshotConcurrencyException)
            {
                // Another arena operation committed while the health probe was in flight.
                // Reload and merge only the reachability fields instead of overwriting it.
                if (attempt == StatusPersistAttempts - 1)
                {
                    return new ProviderReachabilityPersistResult(
                        sessionId,
                        "Provider status refresh deferred because the session changed; retrying.",
                        TimeSpan.FromSeconds(3),
                        SnapshotChanged: false);
                }

                candidate = null;
            }
        }

        throw new UnreachableException();
    }

    private sealed record ProviderIdentity(string BaseUrl, string ApiMode, string ApiToken, string Model)
    {
        internal static ProviderIdentity From(ModelProviderConfig config)
        {
            return new ProviderIdentity(
                config.BaseUrl.Trim(),
                ModelProviderApiModes.Normalize(config.ApiMode),
                config.ApiToken.Trim(),
                config.Model.Trim());
        }

        internal bool Matches(ModelProviderConfig config)
        {
            return BaseUrl.Equals(config.BaseUrl.Trim(), StringComparison.Ordinal)
                && ApiMode.Equals(ModelProviderApiModes.Normalize(config.ApiMode), StringComparison.OrdinalIgnoreCase)
                && ApiToken.Equals(config.ApiToken.Trim(), StringComparison.Ordinal)
                && Model.Equals(config.Model.Trim(), StringComparison.Ordinal);
        }
    }

    public static ModelProviderConfig HealthProbeConfig(ModelProviderConfig source)
    {
        return new ModelProviderConfig
        {
            BaseUrl = source.BaseUrl,
            ApiMode = source.ApiMode,
            ApiToken = source.ApiToken,
            Model = source.Model,
            Timeout = Math.Clamp(Math.Min(source.Timeout, 3), 1, 3),
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            ContextLength = source.ContextLength,
            Reasoning = source.Reasoning,
            NativeStatefulChat = source.NativeStatefulChat,
            NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
            LastError = source.LastError,
            LastLatencyMs = source.LastLatencyMs,
            LastTestOk = source.LastTestOk,
            Extra = source.Extra
        };
    }

    public static ModelProviderConfig CopyConfigWithStatus(ModelProviderConfig source, bool online, string error, int latencyMs)
    {
        return new ModelProviderConfig
        {
            BaseUrl = source.BaseUrl,
            ApiMode = source.ApiMode,
            ApiToken = source.ApiToken,
            Model = source.Model,
            Timeout = source.Timeout,
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            ContextLength = source.ContextLength,
            Reasoning = source.Reasoning,
            NativeStatefulChat = source.NativeStatefulChat,
            NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
            LastError = error,
            LastLatencyMs = latencyMs,
            LastTestOk = online,
            Extra = source.Extra
        };
    }

    internal static ProviderReachabilityReadiness UntestedProviderReadiness(ModelProviderConfig source, bool modelListOk, string modelListError)
    {
        if (!modelListOk)
        {
            return new ProviderReachabilityReadiness(
                false,
                string.IsNullOrWhiteSpace(modelListError) ? "Provider returned no advertised models." : modelListError,
                "Provider reachable; model list unavailable.",
                TimeSpan.FromSeconds(3));
        }

        var completionError = string.IsNullOrWhiteSpace(source.LastError)
            ? "Provider reachable; run Test connection to verify the selected model can complete."
            : source.LastError;
        var status = string.IsNullOrWhiteSpace(source.LastError)
            ? "Provider reachable; completion test required."
            : "Provider reachable; completion test failed.";
        return new ProviderReachabilityReadiness(true, completionError, status, TimeSpan.FromSeconds(10));
    }

    private static async Task<ProviderSocketProbe> ProbeSocketAsync(
        ModelProviderConfig config,
        CancellationToken cancellationToken)
    {
        var baseUrl = SocketProbeBaseUrl(config);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return new ProviderSocketProbe(false, 0, $"Provider URL is invalid: {baseUrl}");
        }

        var port = uri.IsDefaultPort
            ? uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            watch.Stop();
            return new ProviderSocketProbe(true, (int)watch.ElapsedMilliseconds, "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            watch.Stop();
            return new ProviderSocketProbe(
                false,
                (int)watch.ElapsedMilliseconds,
                ProviderUnreachableError(config, baseUrl));
        }
    }

    internal static string SocketProbeBaseUrl(ModelProviderConfig config)
    {
        return ModelProviderApiModes.IsOllamaNative(config.ApiMode)
            ? ModelProviderClient.NormalizeOllamaApiBase(config.BaseUrl)
            : ModelProviderHealthService.NormalizeBaseUrl(config.BaseUrl);
    }

    internal static string ProviderUnreachableError(ModelProviderConfig config, string baseUrl)
    {
        var action = ModelProviderApiModes.Normalize(config.ApiMode) switch
        {
            ModelProviderApiModes.LmStudioNative => "Start LM Studio server",
            ModelProviderApiModes.OllamaNative => "Start Ollama server",
            _ => "Start the provider server"
        };
        return $"Provider unreachable at {baseUrl}. {action} or check the base URL.";
    }

    private sealed record ProviderSocketProbe(bool Ok, int LatencyMs, string Error);
}

public sealed record ProviderReachabilityRefreshResult(
    string SessionId,
    string Status,
    DateTimeOffset CheckedAt,
    int? ModelCount,
    TimeSpan NextInterval,
    bool SnapshotChanged);

public sealed record ProviderReachabilityPersistResult(
    string SessionId,
    string Status,
    TimeSpan NextInterval,
    bool SnapshotChanged);

internal sealed record ProviderReachabilityReadiness(
    bool Online,
    string Error,
    string Status,
    TimeSpan NextInterval);
