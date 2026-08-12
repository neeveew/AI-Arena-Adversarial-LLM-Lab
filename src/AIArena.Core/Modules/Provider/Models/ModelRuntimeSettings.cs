using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

public static class ModelHistoryPolicies
{
    public const string Strict = "strict";
    public const string Rolling80 = "rolling_80";
    public const string Chaptered = "chaptered";

    public static string NormalizeHistoryPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Rolling80 => Rolling80,
        Chaptered => Chaptered,
        _ => Strict
    };
}

public static class ModelResponseTones
{
    public const string Default = "default";
    public const string Neutral = "neutral";
    public const string Concise = "concise";
    public const string Analytical = "analytical";
    public const string Creative = "creative";
    public const string Direct = "direct";
    public const string Custom = "custom";
    public const int MaximumCustomToneCharacters = 240;

    public static string NormalizeResponseTone(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Neutral => Neutral,
        Concise => Concise,
        Analytical => Analytical,
        Creative => Creative,
        Direct => Direct,
        Custom => Custom,
        _ => Default
    };

    public static string NormalizeCustomTone(string? value)
    {
        var filtered = new string((value ?? "")
            .Replace('\0', ' ')
            .Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))
            .ToArray());
        var normalized = string.Join(" ", filtered
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumCustomToneCharacters
            ? normalized
            : normalized[..MaximumCustomToneCharacters].TrimEnd();
    }
}

public sealed class ModelRuntimeSettings
{
    [JsonPropertyName("model_identity")]
    public string ModelIdentity { get; set; } = "";

    [JsonPropertyName("configured_context_window")]
    public int ConfiguredContextWindow { get; set; }

    [JsonPropertyName("history_policy")]
    public string HistoryPolicy { get; set; } = ModelHistoryPolicies.Strict;

    [JsonPropertyName("response_tone")]
    public string ResponseTone { get; set; } = ModelResponseTones.Default;

    [JsonPropertyName("custom_tone")]
    public string CustomTone { get; set; } = "";
}

public static class ModelRuntimeSettingsRegistry
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPendingConfigurationApplies = 256;
    public const int MinimumConfiguredContextWindow = 512;
    public const int MaximumConfiguredContextWindow = 1_048_576;

    /// <summary>Normalizes to disabled (0) or the supported 512..1,048,576 range.</summary>
    public static int ClampConfiguredContextWindow(int value) => value <= 0
        ? 0
        : Math.Clamp(value, MinimumConfiguredContextWindow, MaximumConfiguredContextWindow);

    public static string DefaultHistoryPolicy(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ModelSettingsVersion >= CurrentSchemaVersion
            ? ModelHistoryPolicies.Rolling80
            : ModelHistoryPolicies.Strict;
    }

    public static string Identity(ModelProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var endpoint = CanonicalEndpoint(config.BaseUrl);
        var canonical = string.Join(
            "\n",
            ModelProviderApiModes.Normalize(config.ApiMode),
            endpoint,
            (config.Model ?? "").Trim());
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"model-settings:{digest}";
    }

    public static ModelRuntimeSettings SettingsForConfig(
        ModelProviderConfig config,
        string? historyPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity(config);
        return new ModelRuntimeSettings
        {
            ModelIdentity = identity,
            ConfiguredContextWindow = ClampConfiguredContextWindow(
                config.ConfiguredContextWindow > 0 ? config.ConfiguredContextWindow : config.ContextLength),
            HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(historyPolicy ?? config.HistoryPolicy),
            ResponseTone = ModelResponseTones.NormalizeResponseTone(config.ResponseTone),
            CustomTone = ModelResponseTones.NormalizeCustomTone(config.CustomTone)
        };
    }

    /// <summary>Returns the registry-authoritative configuration for a provider call.</summary>
    public static ModelProviderConfig Resolve(ArenaSnapshot snapshot, ModelProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity(config);
        var settings = snapshot.ModelSettings.TryGetValue(identity, out var registered)
            ? NormalizeSettings(identity, registered)
            : SettingsForConfig(config, DefaultHistoryPolicy(snapshot));
        return CopyWithSettings(config, settings);
    }

    /// <summary>
    /// Canonicalizes registry keys and values. Missing legacy entries remain
    /// strict; clean-session creation explicitly registers rolling_80 instead.
    /// </summary>
    public static bool Normalize(ArenaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = new Dictionary<string, ModelRuntimeSettings>(StringComparer.Ordinal);
        foreach (var pair in snapshot.ModelSettings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var identity = IsIdentity(pair.Key) ? pair.Key.ToLowerInvariant() : pair.Value?.ModelIdentity ?? "";
            if (!IsIdentity(identity) || pair.Value is null)
            {
                continue;
            }

            normalized[identity] = NormalizeSettings(identity, pair.Value);
        }

        foreach (var config in snapshot.Configs.Values.Where(config => !string.IsNullOrWhiteSpace(config.Model)))
        {
            var identity = Identity(config);
            normalized.TryAdd(identity, SettingsForConfig(config, DefaultHistoryPolicy(snapshot)));
        }

        var changed = snapshot.ModelSettings.Count != normalized.Count
            || snapshot.ModelSettings.Any(pair => !normalized.TryGetValue(pair.Key, out var value)
                || !SettingsEqual(pair.Value, value));
        if (changed)
        {
            snapshot.ModelSettings.Clear();
            foreach (var pair in normalized)
            {
                snapshot.ModelSettings[pair.Key] = pair.Value;
            }
        }

        var normalizedPending = snapshot.PendingModelConfigurationApplies
            .Where(IsIdentity)
            .Select(identity => identity.ToLowerInvariant())
            .Where(normalized.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(identity => identity, StringComparer.Ordinal)
            .Take(MaximumPendingConfigurationApplies)
            .ToHashSet(StringComparer.Ordinal);
        var pendingChanged = !snapshot.PendingModelConfigurationApplies.SetEquals(normalizedPending);
        if (pendingChanged)
        {
            snapshot.PendingModelConfigurationApplies.Clear();
            snapshot.PendingModelConfigurationApplies.UnionWith(normalizedPending);
        }

        return changed || pendingChanged;
    }

    public static ModelRuntimeSettings Register(
        ArenaSnapshot snapshot,
        ModelProviderConfig config,
        int configuredContextWindow,
        string historyPolicy,
        string responseTone,
        string customTone)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);
        var identity = Identity(config);
        var settings = NormalizeSettings(identity, new ModelRuntimeSettings
        {
            ModelIdentity = identity,
            ConfiguredContextWindow = configuredContextWindow,
            HistoryPolicy = historyPolicy,
            ResponseTone = responseTone,
            CustomTone = customTone
        });
        snapshot.ModelSettings[identity] = settings;
        return settings;
    }

    /// <summary>
    /// Writes one normalized setting to every equivalent provider/model alias.
    /// Only opaque identity hashes are retained by the registry.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelRuntimeSettings> RegisterAliases(
        ArenaSnapshot snapshot,
        IEnumerable<ModelProviderConfig> aliases,
        int configuredContextWindow,
        string historyPolicy,
        string responseTone,
        string customTone)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(aliases);
        var registered = new Dictionary<string, ModelRuntimeSettings>(StringComparer.Ordinal);
        foreach (var config in aliases)
        {
            if (config is null || string.IsNullOrWhiteSpace(config.Model))
            {
                continue;
            }

            var settings = Register(
                snapshot,
                config,
                configuredContextWindow,
                historyPolicy,
                responseTone,
                customTone);
            registered[settings.ModelIdentity] = settings;
        }

        return registered;
    }

    public static int EffectiveConfiguredContextWindow(ModelProviderConfig config) =>
        ClampConfiguredContextWindow(config.ConfiguredContextWindow > 0
            ? config.ConfiguredContextWindow
            : config.ContextLength);

    public static bool HasPendingConfigurationApply(
        ArenaSnapshot snapshot,
        IEnumerable<ModelProviderConfig> aliases)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(aliases);
        return aliases.Any(config => config is not null
            && !string.IsNullOrWhiteSpace(config.Model)
            && snapshot.PendingModelConfigurationApplies.Contains(Identity(config)));
    }

    public static bool MarkPendingConfigurationApplies(
        ArenaSnapshot snapshot,
        IEnumerable<ModelProviderConfig> aliases)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(aliases);
        var changed = false;
        foreach (var identity in aliases
            .Where(config => config is not null && !string.IsNullOrWhiteSpace(config.Model))
            .Select(Identity)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumPendingConfigurationApplies))
        {
            if (snapshot.ModelSettings.ContainsKey(identity)
                && snapshot.PendingModelConfigurationApplies.Count < MaximumPendingConfigurationApplies)
            {
                changed |= snapshot.PendingModelConfigurationApplies.Add(identity);
            }
        }

        return changed;
    }

    public static bool ClearPendingConfigurationApplies(
        ArenaSnapshot snapshot,
        IEnumerable<ModelProviderConfig> aliases)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(aliases);
        var changed = false;
        foreach (var identity in aliases
            .Where(config => config is not null && !string.IsNullOrWhiteSpace(config.Model))
            .Select(Identity)
            .Distinct(StringComparer.Ordinal))
        {
            changed |= snapshot.PendingModelConfigurationApplies.Remove(identity);
        }

        return changed;
    }

    public static bool IsIdentity(string? value) => value is { Length: 79 }
        && value.StartsWith("model-settings:", StringComparison.OrdinalIgnoreCase)
        && value[15..].All(Uri.IsHexDigit);

    private static ModelRuntimeSettings NormalizeSettings(string identity, ModelRuntimeSettings value)
    {
        var responseTone = ModelResponseTones.NormalizeResponseTone(value.ResponseTone);
        var customTone = responseTone == ModelResponseTones.Custom
            ? ModelResponseTones.NormalizeCustomTone(value.CustomTone)
            : "";
        if (responseTone == ModelResponseTones.Custom && string.IsNullOrWhiteSpace(customTone))
        {
            responseTone = ModelResponseTones.Default;
        }

        return new ModelRuntimeSettings
        {
            ModelIdentity = identity,
            ConfiguredContextWindow = ClampConfiguredContextWindow(value.ConfiguredContextWindow),
            HistoryPolicy = ModelHistoryPolicies.NormalizeHistoryPolicy(value.HistoryPolicy),
            ResponseTone = responseTone,
            CustomTone = customTone
        };
    }

    private static ModelProviderConfig CopyWithSettings(ModelProviderConfig config, ModelRuntimeSettings settings) => new()
    {
        BaseUrl = config.BaseUrl,
        ApiMode = config.ApiMode,
        ApiToken = config.ApiToken,
        Model = config.Model,
        ExplicitModelAssignment = config.ExplicitModelAssignment,
        Timeout = config.Timeout,
        Temperature = config.Temperature,
        MaxOutputTokens = config.MaxOutputTokens,
        ContextLength = config.ContextLength,
        ConfiguredContextWindow = settings.ConfiguredContextWindow,
        HistoryPolicy = settings.HistoryPolicy,
        ResponseTone = settings.ResponseTone,
        CustomTone = settings.CustomTone,
        Reasoning = config.Reasoning,
        NativeStatefulChat = config.NativeStatefulChat,
        NativeIdleTtlSeconds = config.NativeIdleTtlSeconds,
        PreviousResponseId = config.PreviousResponseId,
        PreserveNativeInputWhitespace = config.PreserveNativeInputWhitespace,
        RequestInspectionContext = config.RequestInspectionContext,
        LastError = config.LastError,
        LastLatencyMs = config.LastLatencyMs,
        LastTestOk = config.LastTestOk,
        Extra = config.Extra
    };

    private static bool SettingsEqual(ModelRuntimeSettings? left, ModelRuntimeSettings right) => left is not null
        && left.ModelIdentity == right.ModelIdentity
        && left.ConfiguredContextWindow == right.ConfiguredContextWindow
        && left.HistoryPolicy == right.HistoryPolicy
        && left.ResponseTone == right.ResponseTone
        && left.CustomTone == right.CustomTone;

    private static string CanonicalEndpoint(string? value)
    {
        var raw = (value ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return raw.ToLowerInvariant();
        }

        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }
}

public static class ModelResponseToneInstructions
{
    public static IReadOnlyList<ModelChatMessage> Apply(
        ModelProviderConfig config,
        IReadOnlyList<ModelChatMessage> messages,
        bool factoryMode)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(messages);
        if (factoryMode)
        {
            return messages;
        }

        var instruction = Instruction(config.ResponseTone, config.CustomTone);
        if (instruction.Length == 0)
        {
            return messages;
        }

        var result = messages.ToList();
        var systemIndex = result.FindIndex(message => message.Role.Equals("system", StringComparison.OrdinalIgnoreCase));
        if (systemIndex >= 0)
        {
            var system = result[systemIndex];
            result[systemIndex] = system with { Content = $"{system.Content}{Environment.NewLine}{instruction}" };
        }
        else
        {
            result.Insert(0, new ModelChatMessage("system", instruction));
        }

        return result;
    }

    public static string Instruction(string? responseTone, string? customTone) =>
        ModelResponseTones.NormalizeResponseTone(responseTone) switch
        {
            ModelResponseTones.Neutral => "Response tone: use neutral, even-handed language.",
            ModelResponseTones.Concise => "Response tone: be concise and omit nonessential elaboration.",
            ModelResponseTones.Analytical => "Response tone: be analytical; make assumptions and tradeoffs explicit.",
            ModelResponseTones.Creative => "Response tone: be imaginative while keeping claims clearly grounded.",
            ModelResponseTones.Direct => "Response tone: be direct, concrete, and action-oriented.",
            ModelResponseTones.Custom when ModelResponseTones.NormalizeCustomTone(customTone) is { Length: > 0 } custom
                => $"Response tone: {custom}",
            _ => ""
        };
}
