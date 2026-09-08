using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;

namespace AIArena.Wpf.Services;

/// <summary>
/// Ephemeral discovery evidence joined with the session's existing provider configs.
/// Credentials remain in internal request configs; this is not a persistence or UI contract.
/// </summary>
internal sealed class ProviderServerInventory
{
    private readonly object sync = new();
    private IReadOnlyList<ModelProviderConfig> detectedServers = [];

    public void ReplaceDetectedServers(IReadOnlyList<ModelProviderConfig> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var retained = servers.Where(server => CanonicalEndpoint(server.BaseUrl).Length > 0)
            .GroupBy(ServerIdentity, StringComparer.Ordinal)
            .Select(group => Copy(group.First()))
            .ToArray();
        lock (sync) detectedServers = retained;
    }

    public IReadOnlyList<ModelProviderConfig> CaptureServers(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var configured = snapshot.Configs
            .Where(pair => pair.Key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("narrator", StringComparison.OrdinalIgnoreCase)
                || AgentRosterService.IsParticipantId(pair.Key))
            .OrderBy(pair => pair.Key.Equals(ModelProviderRouting.SharedConfigKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Where(pair => CanonicalEndpoint(pair.Value.BaseUrl).Length > 0)
            .Select(pair => pair.Value)
            .ToArray();
        IReadOnlyList<ModelProviderConfig> detected;
        lock (sync) detected = detectedServers;
        var result = new List<ModelProviderConfig>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in detected)
        {
            var source = configured.FirstOrDefault(config => SameEndpoint(config.BaseUrl, server.BaseUrl)
                && SameOrigin(config.BaseUrl, server.BaseUrl));
            // Current persisted credentials are authoritative, including a deliberate
            // clear. A previous discovery must not restore a revoked token.
            var token = source is not null ? source.ApiToken : server.ApiToken;
            result.Add(Copy(source ?? server, server.BaseUrl, server.ApiMode, token));
            observed.Add(ServerIdentity(server));
        }
        foreach (var server in configured)
        {
            if (observed.Add(ServerIdentity(server))) result.Add(Copy(server));
        }
        return result.AsReadOnly();
    }

    public static string ServerIdentity(ModelProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // A server is the same physical endpoint when discovery learns a better
        // adapter. Model settings retain their existing adapter-qualified identity.
        return ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
        {
            BaseUrl = CanonicalEndpoint(config.BaseUrl),
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            Model = ""
        });
    }

    public static string ModelIdentity(ModelProviderConfig config, string rawModelId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
        {
            BaseUrl = config.BaseUrl,
            ApiMode = config.ApiMode,
            Model = rawModelId
        });
    }

    public static bool SameEndpoint(string left, string right)
    {
        var leftEndpoint = CanonicalEndpoint(left);
        return leftEndpoint.Length > 0 && leftEndpoint.Equals(CanonicalEndpoint(right), StringComparison.Ordinal);
    }

    public static bool SameOrigin(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var first)
        && Uri.TryCreate(right, UriKind.Absolute, out var second)
        && first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase)
        && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    private static string CanonicalEndpoint(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "";
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        if (uri.IsLoopback) builder.Host = "localhost";
        var value = builder.Uri.AbsoluteUri.TrimEnd('/');
        if (value.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return ModelProviderClient.NormalizeBaseUrl(value);
    }

    private static ModelProviderConfig Copy(ModelProviderConfig source, string? baseUrl = null, string? apiMode = null, string? apiToken = null) => new()
    {
        BaseUrl = baseUrl ?? source.BaseUrl,
        ApiMode = apiMode ?? source.ApiMode,
        ApiToken = apiToken ?? source.ApiToken,
        Model = source.Model,
        ExplicitModelAssignment = source.ExplicitModelAssignment,
        Timeout = source.Timeout,
        Temperature = source.Temperature,
        MaxOutputTokens = source.MaxOutputTokens,
        ContextLength = source.ContextLength,
        ConfiguredContextWindow = source.ConfiguredContextWindow,
        HistoryPolicy = source.HistoryPolicy,
        ResponseTone = source.ResponseTone,
        CustomTone = source.CustomTone,
        Reasoning = source.Reasoning,
        NativeStatefulChat = source.NativeStatefulChat,
        NativeIdleTtlSeconds = source.NativeIdleTtlSeconds,
        PreviousResponseId = source.PreviousResponseId,
        PreserveNativeInputWhitespace = source.PreserveNativeInputWhitespace,
        LastError = source.LastError,
        LastLatencyMs = source.LastLatencyMs,
        LastTestOk = source.LastTestOk,
        Extra = source.Extra?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
    };
}