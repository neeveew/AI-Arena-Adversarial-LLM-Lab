using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIArena.Core.Models;

/// <summary>
/// Privacy-safe evidence identifying the provider/model route that produced a
/// transcript response. It intentionally stores no endpoint, model name, or
/// credential-bearing configuration.
/// </summary>
public sealed class CompletionRouteReceipt
{
    public const string ContractVersion = "completion_route_v1";
    public const string MetadataKey = "completion_route_receipt";
    public const string PrimaryPhase = "primary";
    public const string FallbackPhase = "fallback";

    [JsonPropertyName("contract")]
    public string Contract { get; init; } = ContractVersion;

    [JsonPropertyName("model_identity")]
    public string ModelIdentity { get; init; } = "";

    [JsonPropertyName("route_phase")]
    public string RoutePhase { get; init; } = PrimaryPhase;

    public static CompletionRouteReceipt Create(ModelProviderConfig config, string routePhase) => new()
    {
        ModelIdentity = ModelRuntimeSettingsRegistry.Identity(config),
        RoutePhase = NormalizePhase(routePhase)
    };

    public static bool Matches(CompletionRouteReceipt receipt, ModelProviderConfig config, string routePhase) =>
        receipt is not null
        && receipt.Contract.Equals(ContractVersion, StringComparison.Ordinal)
        && receipt.ModelIdentity.Equals(ModelRuntimeSettingsRegistry.Identity(config), StringComparison.Ordinal)
        && receipt.RoutePhase.Equals(NormalizePhase(routePhase), StringComparison.Ordinal);

    public static bool TryRead(DialogueMessage message, out CompletionRouteReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(message);
        receipt = new CompletionRouteReceipt();
        if (!message.Metadata.TryGetValue(MetadataKey, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            receipt = value.Deserialize<CompletionRouteReceipt>() ?? new CompletionRouteReceipt();
            return receipt.Contract.Equals(ContractVersion, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(receipt.ModelIdentity)
                && receipt.RoutePhase is PrimaryPhase or FallbackPhase;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static void Stamp(DialogueMessage message, CompletionRouteReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (receipt is null)
        {
            message.Metadata.Remove(MetadataKey);
            return;
        }

        message.Metadata[MetadataKey] = JsonSerializer.SerializeToElement(receipt);
    }

    private static string NormalizePhase(string? value) => value?.Trim().ToLowerInvariant() == FallbackPhase
        ? FallbackPhase
        : PrimaryPhase;
}
