using System.Diagnostics;
using System.Net.Sockets;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

public sealed class ProviderReachabilityService
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ModelProviderHealthService providerHealth;
    private bool isRefreshing;

    public ProviderReachabilityService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ModelProviderHealthService providerHealth)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerHealth = providerHealth;
    }

    public async Task<ModelProviderConfig?> LoadSharedConfigAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(sessionId);
        if (snapshot is null)
        {
            return null;
        }

        return snapshot.Configs.TryGetValue("shared", out var shared)
            ? shared
            : snapshot.Configs.Values.FirstOrDefault();
    }

    public async Task<ProviderReachabilityRefreshResult?> RefreshAsync(string sessionId)
    {
        if (isRefreshing)
        {
            return null;
        }

        isRefreshing = true;
        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId);
            if (snapshot is null || !snapshot.Configs.TryGetValue("shared", out var shared))
            {
                return null;
            }

            var socket = await ProbeSocketAsync(shared);
            if (!socket.Ok)
            {
                var checkedAt = DateTimeOffset.Now;
                var persist = await PersistAsync(sessionId, online: false, socket.Error, socket.LatencyMs, "Provider offline.", snapshot);
                return new ProviderReachabilityRefreshResult(
                    sessionId,
                    persist?.Status ?? "Provider offline.",
                    checkedAt,
                    ModelCount: null,
                    NextInterval: TimeSpan.FromSeconds(3));
            }

            if (!shared.LastTestOk)
            {
                var health = await providerHealth.CheckAsync(HealthProbeConfig(shared));
                var persist = await PersistAsync(
                    sessionId,
                    health.Ok,
                    health.Ok ? "" : health.Error,
                    socket.LatencyMs,
                    health.Ok ? "Provider online." : "Provider reachable; model list unavailable.",
                    snapshot);
                return new ProviderReachabilityRefreshResult(
                    sessionId,
                    persist?.Status ?? (health.Ok ? "Provider online." : "Provider reachable; model list unavailable."),
                    health.CheckedAt,
                    health.ModelCount,
                    health.Ok ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3));
            }

            var onlineAt = DateTimeOffset.Now;
            var onlinePersist = await PersistAsync(sessionId, online: true, "", socket.LatencyMs, "Provider online.", snapshot);
            return new ProviderReachabilityRefreshResult(
                sessionId,
                onlinePersist?.Status ?? "Provider online.",
                onlineAt,
                ModelCount: null,
                NextInterval: TimeSpan.FromSeconds(10));
        }
        finally
        {
            isRefreshing = false;
        }
    }

    public async Task<ProviderReachabilityPersistResult?> PersistAsync(
        string? sessionId,
        bool online,
        string error,
        int latencyMs,
        string status,
        ArenaSnapshot? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        snapshot ??= await sessionStore.LoadSnapshotAsync(sessionId);
        if (snapshot is null || !snapshot.Configs.TryGetValue("shared", out var shared))
        {
            return null;
        }

        error = online ? "" : error;
        var nextInterval = online ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
        if (shared.LastTestOk == online
            && shared.LastError.Equals(error, StringComparison.Ordinal))
        {
            return new ProviderReachabilityPersistResult(sessionId, status, nextInterval, SnapshotChanged: false);
        }

        snapshot.Configs["shared"] = CopyConfigWithStatus(shared, online, error, latencyMs);
        await sessionStore.SaveSnapshotAsync(snapshot, sessionId);
        await eventLogStore.AppendAsync(sessionId, "native_provider_reachability_changed", new
        {
            Online = online,
            Error = error,
            LatencyMs = latencyMs
        });

        return new ProviderReachabilityPersistResult(sessionId, status, nextInterval, SnapshotChanged: true);
    }

    public static ModelProviderConfig HealthProbeConfig(ModelProviderConfig source)
    {
        return new ModelProviderConfig
        {
            BaseUrl = source.BaseUrl,
            Model = source.Model,
            Timeout = Math.Clamp(Math.Min(source.Timeout, 3), 1, 3),
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
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
            Model = source.Model,
            Timeout = source.Timeout,
            Temperature = source.Temperature,
            MaxOutputTokens = source.MaxOutputTokens,
            LastError = error,
            LastLatencyMs = latencyMs,
            LastTestOk = online,
            Extra = source.Extra
        };
    }

    private static async Task<ProviderSocketProbe> ProbeSocketAsync(ModelProviderConfig config)
    {
        var baseUrl = ModelProviderHealthService.NormalizeBaseUrl(config.BaseUrl);
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            watch.Stop();
            return new ProviderSocketProbe(true, (int)watch.ElapsedMilliseconds, "");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            watch.Stop();
            return new ProviderSocketProbe(
                false,
                (int)watch.ElapsedMilliseconds,
                $"Provider unreachable at {baseUrl}. Start LM Studio server or check the base URL.");
        }
    }

    private sealed record ProviderSocketProbe(bool Ok, int LatencyMs, string Error);
}

public sealed record ProviderReachabilityRefreshResult(
    string SessionId,
    string Status,
    DateTimeOffset CheckedAt,
    int? ModelCount,
    TimeSpan NextInterval);

public sealed record ProviderReachabilityPersistResult(
    string SessionId,
    string Status,
    TimeSpan NextInterval,
    bool SnapshotChanged);
