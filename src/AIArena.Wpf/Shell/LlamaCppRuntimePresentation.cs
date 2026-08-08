using System.Globalization;
using AIArena.Core.Models;

namespace AIArena.Wpf;

internal static class LlamaCppRuntimePresentation
{
    internal const string NotReported = "Not reported";

    public static bool IsVisible(string apiMode)
    {
        return ModelProviderApiModes.IsLlamaCppNative(apiMode);
    }

    public static LlamaCppRuntimeViewState Waiting(bool isBusy = false)
    {
        return new LlamaCppRuntimeViewState(
            Status: isBusy ? "Another arena operation is running." : "Inspect the configured llama-server to load runtime evidence.",
            StatusBrushKey: "MutedTextBrush",
            CheckedAt: "Not checked",
            Build: NotReported,
            Model: NotReported,
            Quantization: NotReported,
            Context: NotReported,
            GpuLayers: NotReported,
            Slots: NotReported,
            Memory: NotReported,
            Throughput: NotReported,
            Warning: "",
            WarningBrushKey: "BetaAccentBrush",
            InspectEnabled: !isBusy,
            ReconnectEnabled: !isBusy,
            PreloadEnabled: false,
            UnloadEnabled: false);
    }

    public static LlamaCppRuntimeViewState FromEvidence(
        LlamaCppRuntimePresentationInput evidence,
        bool isBusy = false)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var status = DisplayStatus(evidence);
        var warning = DisplayWarning(evidence);
        var lifecycleAvailable = evidence.ModelLifecycleAvailable && evidence.RouterMode == true;
        var hasModel = !string.IsNullOrWhiteSpace(evidence.Model);
        var actionsAvailable = !isBusy && evidence.Available;

        return new LlamaCppRuntimeViewState(
            Status: isBusy ? "Another arena operation is running." : status,
            StatusBrushKey: evidence.Ready ? "PrimaryBorderBrush" : evidence.Available ? "BetaAccentBrush" : "DangerTextBrush",
            CheckedAt: evidence.CheckedAt == default
                ? "Not checked"
                : evidence.CheckedAt.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture),
            Build: DisplayText(evidence.BuildInfo),
            Model: DisplayText(evidence.Model),
            Quantization: DisplayText(evidence.Quantization),
            Context: evidence.ContextLength is > 0
                ? $"{FormatTokenCount(evidence.ContextLength.Value)} tokens"
                : NotReported,
            GpuLayers: evidence.GpuLayers switch
            {
                -1 => "All",
                >= 0 => evidence.GpuLayers.Value.ToString(CultureInfo.InvariantCulture),
                _ => NotReported
            },
            Slots: DisplaySlots(evidence.SlotCount, evidence.BusySlots),
            Memory: DisplayMemory(evidence.MemoryBytes, evidence.ModelSizeBytes, evidence.ParameterCount),
            Throughput: evidence.TokensPerSecond is > 0
                ? $"{evidence.TokensPerSecond.Value.ToString("0.#", CultureInfo.InvariantCulture)} tok/s"
                : NotReported,
            Warning: warning,
            WarningBrushKey: !evidence.Available || !string.IsNullOrWhiteSpace(evidence.Error)
                ? "DangerTextBrush"
                : "BetaAccentBrush",
            InspectEnabled: !isBusy,
            ReconnectEnabled: !isBusy,
            PreloadEnabled: actionsAvailable && lifecycleAvailable && hasModel && evidence.Loaded != true,
            UnloadEnabled: actionsAvailable && lifecycleAvailable && hasModel && evidence.Loaded == true);
    }

    internal static string DisplaySlots(int? slotCount, int? busySlots)
    {
        if (slotCount is not > 0)
        {
            return NotReported;
        }

        if (busySlots is null)
        {
            return slotCount.Value == 1 ? "1 slot" : $"{slotCount.Value} slots";
        }

        var boundedBusy = Math.Clamp(busySlots.Value, 0, slotCount.Value);
        return $"{boundedBusy} busy / {slotCount.Value} total";
    }

    internal static string DisplayModelSize(long? sizeBytes, long? parameterCount)
    {
        var parts = new List<string>();
        if (sizeBytes is > 0)
        {
            parts.Add($"{FormatBytes(sizeBytes.Value)} file");
        }

        if (parameterCount is > 0)
        {
            parts.Add($"{FormatParameterCount(parameterCount.Value)} params");
        }

        return parts.Count == 0 ? NotReported : string.Join(" · ", parts);
    }

    internal static string DisplayMemory(long? memoryBytes, long? modelSizeBytes, long? parameterCount)
    {
        var parts = new List<string>();
        if (memoryBytes is > 0)
        {
            parts.Add($"{FormatBytes(memoryBytes.Value)} runtime");
        }

        var model = DisplayModelSize(modelSizeBytes, parameterCount);
        if (!model.Equals(NotReported, StringComparison.Ordinal))
        {
            parts.Add(model);
        }

        return parts.Count == 0 ? NotReported : string.Join(" · ", parts);
    }

    private static string DisplayStatus(LlamaCppRuntimePresentationInput evidence)
    {
        var primary = !string.IsNullOrWhiteSpace(evidence.Status)
            ? evidence.Status.Trim()
            : evidence.Ready
                ? "llama-server is ready."
                : evidence.Available
                    ? "llama-server responded but is not ready."
                    : "llama-server is unavailable.";

        var state = new List<string>();
        if (evidence.RouterMode == true)
        {
            state.Add("router mode");
        }

        if (evidence.Sleeping == true)
        {
            state.Add("sleeping");
        }

        if (evidence.Loaded == true)
        {
            state.Add("model loaded");
        }

        return state.Count == 0 ? primary : $"{primary} {string.Join(", ", state)}.";
    }

    private static string DisplayWarning(LlamaCppRuntimePresentationInput evidence)
    {
        var derived = new List<string>();
        if (evidence.SlotCount is > 0
            && evidence.BusySlots is not null
            && evidence.BusySlots.Value >= evidence.SlotCount.Value)
        {
            derived.Add("All reported llama.cpp slots are busy; new requests may queue.");
        }

        var values = evidence.Warnings
            .Concat(derived)
            .Append(evidence.Error)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 ? "" : string.Join(Environment.NewLine, values);
    }

    private static string DisplayText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? NotReported : value.Trim();
    }

    private static string FormatTokenCount(int value)
    {
        return value >= 1_000_000
            ? $"{value / 1_000_000d:0.#}m"
            : value >= 1_000
                ? $"{value / 1_000d:0.#}k"
                : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long value)
    {
        var gibibytes = value / Math.Pow(1024, 3);
        return gibibytes >= 0.1
            ? $"{gibibytes.ToString("0.#", CultureInfo.InvariantCulture)} GiB"
            : $"{(value / Math.Pow(1024, 2)).ToString("0.#", CultureInfo.InvariantCulture)} MiB";
    }

    private static string FormatParameterCount(long value)
    {
        return value >= 1_000_000_000
            ? $"{(value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}B"
            : value >= 1_000_000
                ? $"{(value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}M"
                : value.ToString("N0", CultureInfo.InvariantCulture);
    }
}

internal sealed record LlamaCppRuntimePresentationInput(
    bool Available,
    bool Ready,
    string Status,
    string BuildInfo,
    string Model,
    string Quantization,
    int? ContextLength,
    int? GpuLayers,
    int? SlotCount,
    int? BusySlots,
    bool? Sleeping,
    bool? RouterMode,
    bool? Loaded,
    long? MemoryBytes,
    long? ModelSizeBytes,
    long? ParameterCount,
    double? TokensPerSecond,
    bool ModelLifecycleAvailable,
    IReadOnlyList<string> Warnings,
    string Error,
    DateTimeOffset CheckedAt);

internal sealed record LlamaCppRuntimeViewState(
    string Status,
    string StatusBrushKey,
    string CheckedAt,
    string Build,
    string Model,
    string Quantization,
    string Context,
    string GpuLayers,
    string Slots,
    string Memory,
    string Throughput,
    string Warning,
    string WarningBrushKey,
    bool InspectEnabled,
    bool ReconnectEnabled,
    bool PreloadEnabled,
    bool UnloadEnabled);
