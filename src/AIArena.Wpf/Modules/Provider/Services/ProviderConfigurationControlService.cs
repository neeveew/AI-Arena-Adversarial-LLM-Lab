using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;

namespace AIArena.Wpf.Services;

internal sealed record AIArenaProviderConfigurationPatch(
    string? BaseUrl,
    string? ApiMode,
    string? ApiToken,
    bool ClearApiToken,
    string? Model,
    int? TimeoutSeconds,
    double? Temperature,
    int? MaxOutputTokens,
    int? ContextLength,
    string? Reasoning,
    bool? NativeStatefulChat,
    int? NativeIdleTtlSeconds,
    IReadOnlyDictionary<string, string> RoleModels,
    bool RefreshModels)
{
    public bool HasMutation =>
        BaseUrl is not null
        || ApiMode is not null
        || ApiToken is not null
        || ClearApiToken
        || Model is not null
        || TimeoutSeconds.HasValue
        || Temperature.HasValue
        || MaxOutputTokens.HasValue
        || ContextLength.HasValue
        || Reasoning is not null
        || NativeStatefulChat.HasValue
        || NativeIdleTtlSeconds.HasValue
        || RoleModels.Count > 0;
}

internal sealed record AIArenaProviderConfigurationControlResult(
    bool Ok,
    string ErrorCode,
    string Message,
    AIArenaProviderControlState State,
    IReadOnlyList<string> ChangedFields,
    bool RefreshModelsRequested);

/// <summary>
/// Applies provider configuration without reading or writing WPF controls. The UI
/// and local control plane share the same snapshot policy, app-wide operation lock,
/// optimistic-concurrency retry, and secret-safe audit record.
/// </summary>
internal sealed class ProviderConfigurationControlService
{
    internal static readonly string[] RoleKeys = ["alpha", "beta", "gamma", "delta", "narrator"];

    private const int MaxBaseUrlLength = 2048;
    private const int MaxModelLength = 1024;
    private const int MaxApiTokenLength = 16 * 1024;
    private const int MaxSafeErrorLength = 512;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly Func<SessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, bool, CancellationToken, Task> refreshHostAsync;

    public ProviderConfigurationControlService(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        SemaphoreSlim arenaOperationLock,
        Func<SessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<string, bool, CancellationToken, Task> refreshHostAsync)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.arenaOperationLock = arenaOperationLock;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.refreshHostAsync = refreshHostAsync;
    }

    public async Task<AIArenaProviderControlState> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = activeSession();
        if (session is null)
        {
            return EmptyState();
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
        return snapshot is null ? EmptyState(session.Id) : CaptureState(session.Id, snapshot);
    }

    public async Task<AIArenaProviderConfigurationControlResult> ApplyAsync(
        AIArenaProviderConfigurationPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var validation = Validate(patch);
        if (!validation.Ok)
        {
            return Failure(validation.ErrorCode, validation.Message, await CaptureAsync(cancellationToken));
        }

        if (isArenaBusy())
        {
            return Failure("busy", "Provider configuration cannot change while the arena is running.", await CaptureAsync(cancellationToken));
        }

        var session = activeSession();
        if (session is null)
        {
            return Failure("not_available", "No active session is available.", EmptyState());
        }

        IReadOnlyList<string> changedFields = [];
        AIArenaProviderControlState savedState = EmptyState(session.Id);
        await arenaOperationLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
                if (snapshot is null)
                {
                    return Failure("not_available", $"No snapshot was found for session {session.Id}.", EmptyState(session.Id));
                }

                changedFields = ApplyPatch(snapshot, patch);
                if (changedFields.Count == 0)
                {
                    savedState = CaptureState(session.Id, snapshot);
                    break;
                }

                try
                {
                    await sessionStore.SaveSnapshotAsync(snapshot, session.Id, cancellationToken);
                    savedState = CaptureState(session.Id, snapshot);
                    await eventLogStore.AppendAsync(session.Id, "provider_configuration_changed", new
                    {
                        ChangedFields = changedFields,
                        savedState.ApiTokenConfigured,
                        savedState.ApiMode,
                        savedState.Model
                    }, cancellationToken);
                    break;
                }
                catch (SnapshotConcurrencyException) when (attempt == 0)
                {
                    // Reload the newest revision and reapply the complete validated patch once.
                }
                catch (SnapshotConcurrencyException)
                {
                    return Failure(
                        "conflict",
                        "Provider configuration changed concurrently; refresh the state and retry.",
                        await CaptureAsync(cancellationToken));
                }
            }
        }
        finally
        {
            arenaOperationLock.Release();
        }

        var message = changedFields.Count == 0
            ? "Provider configuration already matched the requested values."
            : "Provider configuration saved.";
        await refreshHostAsync(message, patch.RefreshModels, cancellationToken);
        var refreshedState = await CaptureAsync(cancellationToken);
        return new AIArenaProviderConfigurationControlResult(
            true,
            "",
            message,
            refreshedState.SessionId.Length == 0 ? savedState : refreshedState,
            changedFields,
            patch.RefreshModels);
    }

    internal static (bool Ok, string ErrorCode, string Message) Validate(AIArenaProviderConfigurationPatch patch)
    {
        if (!patch.HasMutation)
        {
            return (false, "missing_argument", "provider.config.set requires at least one configuration value; refreshModels alone is not a change.");
        }

        if (patch.ApiToken is not null && patch.ClearApiToken)
        {
            return (false, "invalid_argument", "args.apiToken and args.clearApiToken cannot be used together.");
        }

        if (patch.BaseUrl is not null && !TryValidateBaseUrl(patch.BaseUrl, out var baseUrlError))
        {
            return (false, "invalid_argument", baseUrlError);
        }

        if (patch.ApiMode is not null && !IsSupportedApiMode(patch.ApiMode))
        {
            return (false, "invalid_argument", "args.apiMode must be openai_compatible, lmstudio_native, or ollama_native.");
        }

        if (patch.ApiToken is not null
            && (string.IsNullOrWhiteSpace(patch.ApiToken)
                || patch.ApiToken.Length > MaxApiTokenLength
                || patch.ApiToken.IndexOfAny(['\0', '\r', '\n']) >= 0))
        {
            return (false, "invalid_argument", "args.apiToken must be non-empty, at most 16384 characters, and contain no NUL or line breaks.");
        }

        if (patch.Model is not null && !TryValidateModel(patch.Model, allowEmpty: true))
        {
            return (false, "invalid_argument", "args.model must be at most 1024 characters and contain no control characters.");
        }

        foreach (var (role, model) in patch.RoleModels)
        {
            if (!RoleKeys.Contains(role, StringComparer.OrdinalIgnoreCase))
            {
                return (false, "invalid_argument", $"Unknown provider role '{role}'.");
            }

            if (!TryValidateModel(model, allowEmpty: true))
            {
                return (false, "invalid_argument", $"args.{role}Model must be at most 1024 characters and contain no control characters.");
            }
        }

        if (patch.TimeoutSeconds is int timeout && timeout is < 1 or > 3600)
        {
            return (false, "invalid_argument", "args.timeoutSeconds must be between 1 and 3600.");
        }

        if (patch.Temperature is double temperature
            && (!double.IsFinite(temperature) || temperature is < 0 or > 2))
        {
            return (false, "invalid_argument", "args.temperature must be a finite number between 0 and 2.");
        }

        if (patch.MaxOutputTokens is int maxOutputTokens && maxOutputTokens is < 1 or > 32768)
        {
            return (false, "invalid_argument", "args.maxOutputTokens must be between 1 and 32768.");
        }

        if (patch.ContextLength is int contextLength && contextLength is < 0 or > 1_048_576)
        {
            return (false, "invalid_argument", "args.contextLength must be between 0 and 1048576.");
        }

        if (patch.Reasoning is not null && !IsSupportedReasoning(patch.Reasoning))
        {
            return (false, "invalid_argument", "args.reasoning must be default, off, low, medium, high, or on.");
        }

        if (patch.NativeIdleTtlSeconds is int ttl && ttl is < 0 or > 86_400)
        {
            return (false, "invalid_argument", "args.nativeIdleTtlSeconds must be between 0 and 86400.");
        }

        return (true, "", "");
    }

    internal static IReadOnlyList<string> ApplyPatch(ArenaSnapshot snapshot, AIArenaProviderConfigurationPatch patch)
    {
        var existingShared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var shared)
            ? shared
            : new ModelProviderConfig();
        var existingRoleModels = RoleKeys.ToDictionary(
            role => role,
            role => ConfiguredRoleModel(snapshot.Configs, role, existingShared),
            StringComparer.OrdinalIgnoreCase);
        var roleOverrides = RoleKeys.ToDictionary(
            role => role,
            role => GenerationOverrides(snapshot.Configs, role, existingShared),
            StringComparer.OrdinalIgnoreCase);

        var baseUrl = patch.BaseUrl is null
            ? existingShared.BaseUrl
            : ModelProviderHealthService.NormalizeBaseUrl(patch.BaseUrl.Trim());
        var apiMode = patch.ApiMode is null ? existingShared.ApiMode : patch.ApiMode.Trim().ToLowerInvariant();
        var apiToken = patch.ClearApiToken ? "" : patch.ApiToken ?? existingShared.ApiToken;
        var model = patch.Model is null ? existingShared.Model : patch.Model.Trim();
        var timeout = patch.TimeoutSeconds ?? existingShared.Timeout;
        var temperature = patch.Temperature ?? existingShared.Temperature;
        var maxOutputTokens = patch.MaxOutputTokens ?? existingShared.MaxOutputTokens;
        var contextLength = patch.ContextLength ?? existingShared.ContextLength;
        var reasoning = patch.Reasoning is null
            ? ModelProviderReasoningModes.Normalize(existingShared.Reasoning)
            : NormalizeReasoning(patch.Reasoning);
        var nativeStatefulChat = patch.NativeStatefulChat ?? existingShared.NativeStatefulChat;
        var nativeIdleTtlSeconds = patch.NativeIdleTtlSeconds ?? existingShared.NativeIdleTtlSeconds;
        var readinessChanged = ProviderReadinessChanged(
            existingShared,
            baseUrl,
            apiMode,
            apiToken,
            model,
            contextLength,
            reasoning,
            nativeStatefulChat,
            nativeIdleTtlSeconds);
        var updatedShared = new ModelProviderConfig
        {
            BaseUrl = baseUrl,
            ApiMode = apiMode,
            ApiToken = apiToken,
            Model = model,
            Timeout = timeout,
            Temperature = temperature,
            MaxOutputTokens = maxOutputTokens,
            ContextLength = contextLength,
            Reasoning = reasoning,
            NativeStatefulChat = nativeStatefulChat,
            NativeIdleTtlSeconds = nativeIdleTtlSeconds,
            LastError = readinessChanged ? "" : existingShared.LastError,
            LastLatencyMs = readinessChanged ? 0 : existingShared.LastLatencyMs,
            LastTestOk = !readinessChanged && existingShared.LastTestOk,
            Extra = existingShared.Extra
        };

        var changed = ChangedSharedFields(existingShared, updatedShared, patch);
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = updatedShared;
        foreach (var role in RoleKeys)
        {
            var configuredModel = patch.RoleModels.TryGetValue(role, out var requestedModel)
                ? requestedModel.Trim()
                : existingRoleModels[role];
            if (patch.RoleModels.ContainsKey(role)
                && !configuredModel.Equals(existingRoleModels[role], StringComparison.Ordinal))
            {
                changed.Add($"{role}Model");
            }

            var (temperatureOverride, maxOutputTokensOverride) = roleOverrides[role];
            SaveRoleModelConfig(
                snapshot.Configs,
                role,
                configuredModel,
                updatedShared,
                temperatureOverride,
                maxOutputTokensOverride);
        }

        return changed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void SaveRoleModelConfig(
        IDictionary<string, ModelProviderConfig> configs,
        string role,
        string model,
        ModelProviderConfig shared,
        double? temperatureOverride = null,
        int? maxOutputTokensOverride = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            if (!temperatureOverride.HasValue && !maxOutputTokensOverride.HasValue)
            {
                configs.Remove(role);
                return;
            }

            model = shared.Model;
            if (string.IsNullOrWhiteSpace(model))
            {
                configs.Remove(role);
                return;
            }
        }

        var existing = configs.TryGetValue(role, out var current) ? current : null;
        var readinessChanged = existing is null || ProviderReadinessChanged(
            existing,
            shared.BaseUrl,
            shared.ApiMode,
            shared.ApiToken,
            model,
            shared.ContextLength,
            shared.Reasoning,
            shared.NativeStatefulChat,
            shared.NativeIdleTtlSeconds);
        configs[role] = new ModelProviderConfig
        {
            BaseUrl = shared.BaseUrl,
            ApiMode = shared.ApiMode,
            ApiToken = shared.ApiToken,
            Model = model,
            Timeout = shared.Timeout,
            Temperature = temperatureOverride ?? shared.Temperature,
            MaxOutputTokens = maxOutputTokensOverride ?? shared.MaxOutputTokens,
            ContextLength = shared.ContextLength,
            Reasoning = shared.Reasoning,
            NativeStatefulChat = shared.NativeStatefulChat,
            NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds,
            LastError = readinessChanged ? "" : existing!.LastError,
            LastLatencyMs = readinessChanged ? 0 : existing!.LastLatencyMs,
            LastTestOk = !readinessChanged && existing!.LastTestOk,
            Extra = existing?.Extra
        };
    }

    internal static bool ProviderReadinessChanged(
        ModelProviderConfig existing,
        string baseUrl,
        string apiMode,
        string apiToken,
        string model,
        int contextLength,
        string reasoning,
        bool nativeStatefulChat,
        int nativeIdleTtlSeconds)
    {
        var identityChanged = !existing.BaseUrl.Trim().Equals(baseUrl.Trim(), StringComparison.Ordinal)
            || !ModelProviderApiModes.Normalize(existing.ApiMode).Equals(ModelProviderApiModes.Normalize(apiMode), StringComparison.OrdinalIgnoreCase)
            || !existing.ApiToken.Equals(apiToken, StringComparison.Ordinal)
            || !existing.Model.Trim().Equals(model.Trim(), StringComparison.Ordinal);
        if (identityChanged || !ModelProviderApiModes.IsNative(apiMode))
        {
            return identityChanged;
        }

        var nativeOptionChanged = existing.ContextLength != contextLength
            || !ModelProviderReasoningModes.Normalize(existing.Reasoning).Equals(ModelProviderReasoningModes.Normalize(reasoning), StringComparison.OrdinalIgnoreCase)
            || existing.NativeIdleTtlSeconds != nativeIdleTtlSeconds;
        return ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? nativeOptionChanged || existing.NativeStatefulChat != nativeStatefulChat
            : nativeOptionChanged;
    }

    internal static AIArenaProviderControlState CaptureState(string sessionId, ArenaSnapshot snapshot)
    {
        var shared = snapshot.Configs.TryGetValue(ModelProviderRouting.SharedConfigKey, out var configured)
            ? configured
            : snapshot.Configs.Values.FirstOrDefault() ?? new ModelProviderConfig();
        var roles = RoleKeys.Select(role =>
        {
            var configuredModel = ConfiguredRoleModel(snapshot.Configs, role, shared);
            var effectiveModel = snapshot.Configs.TryGetValue(role, out var roleConfig)
                && !string.IsNullOrWhiteSpace(roleConfig.Model)
                ? roleConfig.Model.Trim()
                : shared.Model.Trim();
            var (temperatureOverride, maxOutputTokensOverride) = GenerationOverrides(snapshot.Configs, role, shared);
            return new AIArenaProviderRoleControlState(
                role,
                configuredModel,
                effectiveModel,
                string.IsNullOrWhiteSpace(configuredModel),
                temperatureOverride,
                maxOutputTokensOverride);
        }).ToArray();
        string Effective(string role) => roles.First(item => item.Id.Equals(role, StringComparison.OrdinalIgnoreCase)).EffectiveModel;
        return new AIArenaProviderControlState(
            shared.LastTestOk,
            shared.Model.Trim(),
            Effective("alpha"),
            Effective("beta"),
            Effective("gamma"),
            Effective("delta"),
            Effective("narrator"),
            SanitizeError(shared.LastError, shared.ApiToken))
        {
            ConfigurationIdentity = ConfigurationIdentity(snapshot),
            SessionId = sessionId,
            PersistenceRevision = snapshot.PersistenceRevision,
            Configured = !string.IsNullOrWhiteSpace(shared.BaseUrl),
            BaseUrl = SanitizeBaseUrl(shared.BaseUrl),
            ApiMode = ModelProviderApiModes.Normalize(shared.ApiMode),
            ApiTokenConfigured = !string.IsNullOrEmpty(shared.ApiToken),
            TimeoutSeconds = shared.Timeout,
            Temperature = shared.Temperature,
            MaxOutputTokens = shared.MaxOutputTokens,
            ContextLength = shared.ContextLength,
            Reasoning = string.IsNullOrWhiteSpace(shared.Reasoning) ? "default" : ModelProviderReasoningModes.Normalize(shared.Reasoning),
            NativeStatefulChat = shared.NativeStatefulChat,
            NativeIdleTtlSeconds = shared.NativeIdleTtlSeconds,
            LastTestOk = shared.LastTestOk,
            LastLatencyMs = shared.LastLatencyMs,
            Roles = roles
        };
    }

    internal static AIArenaProviderControlState EmptyState(string sessionId = "")
    {
        return new AIArenaProviderControlState(false, "", "", "", "", "", "", "")
        {
            SessionId = sessionId,
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            Reasoning = "default",
            Roles = []
        };
    }

    internal static string ConfigurationIdentity(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var key in new[] { ModelProviderRouting.SharedConfigKey }.Concat(RoleKeys))
        {
            AppendIdentityValue(hash, key);
            if (!snapshot.Configs.TryGetValue(key, out var config))
            {
                AppendIdentityValue(hash, "<missing>");
                continue;
            }

            AppendIdentityValue(hash, config.BaseUrl.Trim().TrimEnd('/'));
            AppendIdentityValue(hash, ModelProviderApiModes.Normalize(config.ApiMode));
            AppendIdentityValue(hash, config.ApiToken);
            AppendIdentityValue(hash, config.Model.Trim());
            AppendIdentityValue(hash, config.Timeout.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.Temperature.ToString("R", CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, config.ContextLength.ToString(CultureInfo.InvariantCulture));
            AppendIdentityValue(hash, ModelProviderReasoningModes.Normalize(config.Reasoning));
            AppendIdentityValue(hash, config.NativeStatefulChat ? "1" : "0");
            AppendIdentityValue(hash, config.NativeIdleTtlSeconds.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendIdentityValue(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    internal static string SanitizeBaseUrl(string value)
    {
        var text = (value ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Regex.Replace(text, @"(?i)(https?://)[^/@\s]+@", "$1[redacted]@").Split(['?', '#'])[0];
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    internal static string SanitizeError(string value, string apiToken)
    {
        var message = value ?? "";
        if (!string.IsNullOrEmpty(apiToken))
        {
            message = message.Replace(apiToken, "[redacted]", StringComparison.Ordinal);
        }

        message = Regex.Replace(
            message,
            @"(?i)\bhttps?://[^\s<>""']+",
            match => SanitizeBaseUrl(match.Value));
        message = Regex.Replace(message, @"(?i)(bearer\s+)[^\s,;]+", "$1[redacted]");
        message = Regex.Replace(message, @"(?i)((?:api[_-]?key|token|authorization)\s*[:=]\s*)[^\s,;]+", "$1[redacted]");
        message = Regex.Replace(message, @"(?i)(https?://)[^/@\s]+@", "$1");
        if (message.Length > MaxSafeErrorLength)
        {
            message = message[..MaxSafeErrorLength] + "...";
        }

        return message;
    }

    private static AIArenaProviderConfigurationControlResult Failure(
        string errorCode,
        string message,
        AIArenaProviderControlState state)
    {
        return new AIArenaProviderConfigurationControlResult(false, errorCode, message, state, [], false);
    }

    private static List<string> ChangedSharedFields(
        ModelProviderConfig before,
        ModelProviderConfig after,
        AIArenaProviderConfigurationPatch patch)
    {
        var changed = new List<string>();
        Add(patch.BaseUrl is not null && !before.BaseUrl.Equals(after.BaseUrl, StringComparison.Ordinal), "baseUrl");
        Add(patch.ApiMode is not null && !before.ApiMode.Equals(after.ApiMode, StringComparison.OrdinalIgnoreCase), "apiMode");
        Add((patch.ApiToken is not null || patch.ClearApiToken) && !before.ApiToken.Equals(after.ApiToken, StringComparison.Ordinal), "apiToken");
        Add(patch.Model is not null && !before.Model.Equals(after.Model, StringComparison.Ordinal), "model");
        Add(patch.TimeoutSeconds.HasValue && before.Timeout != after.Timeout, "timeoutSeconds");
        Add(patch.Temperature.HasValue && before.Temperature != after.Temperature, "temperature");
        Add(patch.MaxOutputTokens.HasValue && before.MaxOutputTokens != after.MaxOutputTokens, "maxOutputTokens");
        Add(patch.ContextLength.HasValue && before.ContextLength != after.ContextLength, "contextLength");
        Add(patch.Reasoning is not null && !ModelProviderReasoningModes.Normalize(before.Reasoning).Equals(after.Reasoning, StringComparison.OrdinalIgnoreCase), "reasoning");
        Add(patch.NativeStatefulChat.HasValue && before.NativeStatefulChat != after.NativeStatefulChat, "nativeStatefulChat");
        Add(patch.NativeIdleTtlSeconds.HasValue && before.NativeIdleTtlSeconds != after.NativeIdleTtlSeconds, "nativeIdleTtlSeconds");
        return changed;

        void Add(bool condition, string field)
        {
            if (condition)
            {
                changed.Add(field);
            }
        }
    }

    private static (double? Temperature, int? MaxOutputTokens) GenerationOverrides(
        IReadOnlyDictionary<string, ModelProviderConfig> configs,
        string role,
        ModelProviderConfig shared)
    {
        if (!configs.TryGetValue(role, out var config))
        {
            return (null, null);
        }

        return (
            Math.Abs(config.Temperature - shared.Temperature) > 0.000_001 ? config.Temperature : null,
            config.MaxOutputTokens != shared.MaxOutputTokens ? config.MaxOutputTokens : null);
    }

    private static string ConfiguredRoleModel(
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

    private static bool TryValidateBaseUrl(string value, out string error)
    {
        var text = value.Trim();
        if (text.Length == 0 || text.Length > MaxBaseUrlLength)
        {
            error = "args.baseUrl must be a non-empty HTTP or HTTPS URL no longer than 2048 characters.";
            return false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "args.baseUrl must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "args.baseUrl cannot contain credentials, a query string, or a fragment.";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryValidateModel(string value, bool allowEmpty)
    {
        return (allowEmpty || !string.IsNullOrWhiteSpace(value))
            && value.Length <= MaxModelLength
            && !value.Any(char.IsControl);
    }

    private static bool IsSupportedApiMode(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is ModelProviderApiModes.OpenAiCompatible
            or ModelProviderApiModes.LmStudioNative
            or ModelProviderApiModes.OllamaNative;
    }

    private static bool IsSupportedReasoning(string value)
    {
        return value.Trim().ToLowerInvariant() is "default" or "off" or "low" or "medium" or "high" or "on";
    }

    private static string NormalizeReasoning(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "default" ? "" : normalized;
    }
}
