using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AIArena.Core.Models;

namespace AIArena.Core.Persistence;

/// <summary>
/// Builds serializer metadata used only for durable snapshot writes. Provider
/// tokens are projected through the captured protector as they are written, so
/// persistence never needs a second, token-bearing object graph.
/// </summary>
internal static class SnapshotPersistenceJson
{
    internal static JsonSerializerOptions CreateOptions(
        JsonSerializerOptions source,
        Func<string, string> protectSecret)
    {
        ArgumentNullException.ThrowIfNull(source);

        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            ProtectModelProviderToken(typeInfo, protectSecret);
            RemoveReservedSnapshotCollisions(typeInfo);
        });

        return new JsonSerializerOptions(source)
        {
            TypeInfoResolver = resolver
        };
    }

    private static void ProtectModelProviderToken(
        JsonTypeInfo typeInfo,
        Func<string, string> protectSecret)
    {
        if (typeInfo.Type != typeof(ModelProviderConfig))
        {
            return;
        }

        var tokenProperty = typeInfo.Properties.FirstOrDefault(property =>
            property.Name.Equals("api_token", StringComparison.Ordinal));
        if (tokenProperty is null)
        {
            throw new InvalidOperationException("Model provider token metadata is unavailable for persistence.");
        }

        tokenProperty.Get = instance =>
        {
            var token = ((ModelProviderConfig)instance).ApiToken;
            return string.IsNullOrEmpty(token)
                ? token
                : ProtectNonEmptyToken(token, protectSecret);
        };

        // Extension data normally contains forward-compatible provider fields and
        // is returned unchanged. A hand-authored object can nevertheless contain
        // a duplicate api_token key; System.Text.Json writes that key after the
        // declared property. Remove that reserved collision so it cannot override
        // the protected authoritative value during round-trip loading.
        var extensionProperty = typeInfo.Properties.FirstOrDefault(property => property.IsExtensionData);
        if (extensionProperty?.Get is not { } getExtensionData)
        {
            return;
        }

        extensionProperty.Get = instance =>
        {
            var extensionData = (Dictionary<string, JsonElement>?)getExtensionData(instance);
            if (extensionData is null
                || !extensionData.Keys.Any(key => key.Equals("api_token", StringComparison.OrdinalIgnoreCase)))
            {
                return extensionData;
            }

            // Extension data is forward-compatible, but a case-insensitive
            // collision with a declared property is not a second field. Never
            // write it after the protected declared token, where it could win
            // case-insensitive deserialization and restore plaintext.
            var sanitized = extensionData
                .Where(pair => !pair.Key.Equals("api_token", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, extensionData.Comparer);
            return sanitized;
        };
    }

    private static void RemoveReservedSnapshotCollisions(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ArenaSnapshot))
        {
            return;
        }

        var extensionProperty = typeInfo.Properties.FirstOrDefault(property => property.IsExtensionData);
        if (extensionProperty?.Get is not { } getExtensionData)
        {
            return;
        }

        extensionProperty.Get = instance =>
        {
            var extensionData = (Dictionary<string, JsonElement>?)getExtensionData(instance);
            if (extensionData is null
                || !extensionData.Keys.Any(IsReservedSnapshotProperty))
            {
                return extensionData;
            }

            // Typed configs and the causal revision are authoritative. Duplicate
            // extension entries are emitted afterward and could replace either
            // value during loading, bypassing token protection or stale-write
            // checks, so omit only those reserved collisions.
            return extensionData
                .Where(pair => !IsReservedSnapshotProperty(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, extensionData.Comparer);
        };
    }

    private static bool IsReservedSnapshotProperty(string key) =>
        key.Equals("configs", StringComparison.OrdinalIgnoreCase)
        || key.Equals("persistence_revision", StringComparison.OrdinalIgnoreCase);

    private static string ProtectNonEmptyToken(string token, Func<string, string> protectSecret) =>
        protectSecret(token) ?? throw new NullReferenceException("The provider token protector returned null.");
}
