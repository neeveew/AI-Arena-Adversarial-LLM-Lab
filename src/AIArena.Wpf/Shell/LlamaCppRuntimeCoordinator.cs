using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class LlamaCppRuntimeCoordinator
{
    private readonly LlamaCppRuntimeService runtimeService;
    private readonly LlamaCppRuntimeControls controls;
    private readonly Func<ModelProviderConfig> captureConfig;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Brush> resourceBrush;

    private RuntimeIdentity configurationIdentity = RuntimeIdentity.Empty;
    private LlamaCppRuntimeSnapshot? snapshot;
    private int configurationVersion;
    private bool operationRunning;
    private string operationStatus = "";
    private string actionNotice = "";
    private bool actionNoticeIsFailure;
    private bool lifecycleKnownUnsupported;

    public LlamaCppRuntimeCoordinator(
        LlamaCppRuntimeService runtimeService,
        LlamaCppRuntimeControls controls,
        Func<ModelProviderConfig> captureConfig,
        Func<bool> isArenaBusy,
        Func<string, Brush> resourceBrush)
    {
        this.runtimeService = runtimeService;
        this.controls = controls;
        this.captureConfig = captureConfig;
        this.isArenaBusy = isArenaBusy;
        this.resourceBrush = resourceBrush;
        ConfigurationChanged();
    }

    public void ConfigurationChanged()
    {
        var config = captureConfig();
        var identity = RuntimeIdentity.From(config);
        if (identity != configurationIdentity)
        {
            configurationIdentity = identity;
            configurationVersion++;
            snapshot = null;
            actionNotice = "";
            actionNoticeIsFailure = false;
            lifecycleKnownUnsupported = false;
        }

        ApplyCurrent(config);
    }

    public void UpdateBusyState()
    {
        ApplyCurrent(captureConfig());
    }

    public Task InspectAsync(CancellationToken cancellationToken = default)
    {
        return InspectAsync(reconnect: false, cancellationToken);
    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        return InspectAsync(reconnect: true, cancellationToken);
    }

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        return RunLifecycleAsync(load: true, cancellationToken);
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        return RunLifecycleAsync(load: false, cancellationToken);
    }

    private async Task InspectAsync(bool reconnect, CancellationToken cancellationToken)
    {
        var config = captureConfig();
        ConfigurationChanged();
        if (!LlamaCppRuntimePresentation.IsVisible(config.ApiMode) || operationRunning || isArenaBusy())
        {
            ApplyCurrent(config);
            return;
        }

        var identity = RuntimeIdentity.From(config);
        var version = configurationVersion;
        operationRunning = true;
        operationStatus = reconnect
            ? "Reconnecting to llama-server and refreshing runtime evidence..."
            : "Inspecting llama-server runtime evidence...";
        if (reconnect)
        {
            // A manually requested reconnect may target a restarted or upgraded
            // user-owned server, so allow its lifecycle capability to be tried again.
            lifecycleKnownUnsupported = false;
        }

        actionNotice = "";
        actionNoticeIsFailure = false;
        ApplyCurrent(config);
        LlamaCppRuntimeSnapshot? result = null;
        try
        {
            result = reconnect
                ? await runtimeService.ReconnectAsync(config, cancellationToken)
                : await runtimeService.InspectAsync(config, cancellationToken);
        }
        finally
        {
            operationRunning = false;
            operationStatus = "";
            ApplyCurrent(captureConfig());
        }

        if (IsCurrent(identity, version))
        {
            snapshot = result;
        }

        ApplyCurrent(captureConfig());
    }

    private async Task RunLifecycleAsync(bool load, CancellationToken cancellationToken)
    {
        var config = captureConfig();
        ConfigurationChanged();
        var inspected = snapshot;
        if (!CanRunLifecycle(config, inspected) || operationRunning || isArenaBusy())
        {
            ApplyCurrent(config);
            return;
        }

        var model = !string.IsNullOrWhiteSpace(inspected!.Model)
            ? inspected.Model.Trim()
            : config.Model.Trim();
        var identity = RuntimeIdentity.From(config);
        var version = configurationVersion;
        operationRunning = true;
        operationStatus = load
            ? $"Requesting llama.cpp router preload for {model}..."
            : $"Requesting llama.cpp router unload for {model}...";
        actionNotice = "";
        actionNoticeIsFailure = false;
        ApplyCurrent(config);

        LlamaCppRuntimeActionResult? actionResult = null;
        LlamaCppRuntimeSnapshot? refreshed = null;
        try
        {
            actionResult = load
                ? await runtimeService.LoadAsync(config, model, cancellationToken)
                : await runtimeService.UnloadAsync(config, model, cancellationToken);
            if (actionResult.Supported)
            {
                refreshed = await runtimeService.InspectAsync(config, cancellationToken);
            }
        }
        finally
        {
            operationRunning = false;
            operationStatus = "";
            ApplyCurrent(captureConfig());
        }

        if (IsCurrent(identity, version) && actionResult is not null)
        {
            if (refreshed is not null)
            {
                snapshot = refreshed;
            }

            lifecycleKnownUnsupported = !actionResult.Supported;
            actionNoticeIsFailure = !actionResult.Ok;
            actionNotice = DisplayActionResult(actionResult);
        }

        ApplyCurrent(captureConfig());
    }

    private bool CanRunLifecycle(ModelProviderConfig config, LlamaCppRuntimeSnapshot? inspected)
    {
        return LlamaCppRuntimePresentation.IsVisible(config.ApiMode)
            && inspected is { Available: true, RouterMode: true }
            && inspected.Capabilities.ModelLifecycle
            && !lifecycleKnownUnsupported
            && (!string.IsNullOrWhiteSpace(inspected.Model) || !string.IsNullOrWhiteSpace(config.Model));
    }

    private bool IsCurrent(RuntimeIdentity identity, int version)
    {
        return version == configurationVersion
            && identity == configurationIdentity
            && identity == RuntimeIdentity.From(captureConfig());
    }

    private void ApplyCurrent(ModelProviderConfig config)
    {
        var identity = RuntimeIdentity.From(config);
        if (identity != configurationIdentity)
        {
            configurationIdentity = identity;
            configurationVersion++;
            snapshot = null;
            actionNotice = "";
            actionNoticeIsFailure = false;
            lifecycleKnownUnsupported = false;
        }

        var visible = LlamaCppRuntimePresentation.IsVisible(config.ApiMode);
        controls.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        var busy = operationRunning || isArenaBusy();
        var state = snapshot is null
            ? LlamaCppRuntimePresentation.Waiting(busy)
            : LlamaCppRuntimePresentation.FromEvidence(PresentationInput(snapshot), busy);

        if (operationRunning)
        {
            state = state with { Status = operationStatus };
        }

        if (!string.IsNullOrWhiteSpace(actionNotice))
        {
            state = actionNoticeIsFailure
                ? state with
                {
                    Warning = AppendLine(state.Warning, actionNotice),
                    WarningBrushKey = "DangerTextBrush"
                }
                : state with { Status = $"{actionNotice} {state.Status}" };
        }

        if (lifecycleKnownUnsupported)
        {
            state = state with
            {
                PreloadEnabled = false,
                UnloadEnabled = false,
                Warning = AppendLine(state.Warning, "Model load and unload endpoints are unsupported by this llama-server build."),
                WarningBrushKey = "BetaAccentBrush"
            };
        }

        ApplyState(state);
    }

    private void ApplyState(LlamaCppRuntimeViewState state)
    {
        controls.Status.Text = state.Status;
        controls.Status.Foreground = resourceBrush(state.StatusBrushKey);
        controls.CheckedAt.Text = state.CheckedAt;
        controls.CheckedAt.ToolTip = $"Last runtime inspection: {state.CheckedAt}";
        AutomationProperties.SetName(controls.CheckedAt, $"Last runtime inspection: {state.CheckedAt}");
        AutomationProperties.SetHelpText(controls.CheckedAt, $"Last runtime inspection: {state.CheckedAt}");
        SetEvidence(controls.Build, "Build", state.Build);
        SetEvidence(controls.Model, "Model", state.Model);
        SetEvidence(
            controls.Quantization,
            "Quantization inferred from model ID",
            state.Quantization,
            "Inferred from the advertised model identifier; this is not claimed as measured runtime configuration.");
        SetEvidence(controls.Context, "Context", state.Context);
        SetEvidence(controls.GpuLayers, "GPU layers", state.GpuLayers);
        SetEvidence(controls.Slots, "Slots", state.Slots);
        SetEvidence(
            controls.Memory,
            "Memory and model size",
            state.Memory,
            "Runtime memory is shown only when reported by llama-server; model file size and parameter count are labelled separately and are not claimed as RAM or VRAM usage.");
        SetEvidence(controls.Throughput, "Throughput", state.Throughput);

        controls.Warning.Visibility = string.IsNullOrWhiteSpace(state.Warning)
            ? Visibility.Collapsed
            : Visibility.Visible;
        controls.WarningText.Text = state.Warning;
        ApplyWarningTheme(state.WarningBrushKey);

        controls.Inspect.IsEnabled = state.InspectEnabled;
        controls.Reconnect.IsEnabled = state.ReconnectEnabled;
        controls.Preload.IsEnabled = state.PreloadEnabled && !lifecycleKnownUnsupported;
        controls.Unload.IsEnabled = state.UnloadEnabled && !lifecycleKnownUnsupported;
        ApplyAccessibility(state);
    }

    private void ApplyAccessibility(LlamaCppRuntimeViewState state)
    {
        var fullStatus = string.IsNullOrWhiteSpace(state.Warning)
            ? state.Status
            : $"{state.Status}{Environment.NewLine}{state.Warning}";
        controls.Card.ToolTip = fullStatus;
        controls.Status.ToolTip = fullStatus;
        controls.Warning.ToolTip = state.Warning;
        AutomationProperties.SetHelpText(controls.Card, fullStatus);
        AutomationProperties.SetName(controls.Status, $"llama.cpp runtime status: {state.Status}");
        AutomationProperties.SetHelpText(controls.Status, fullStatus);
        AutomationProperties.SetName(controls.Warning, "llama.cpp runtime warning");
        AutomationProperties.SetHelpText(controls.Warning, state.Warning);

        SetButtonHelp(
            controls.Inspect,
            state.InspectEnabled
                ? "Read available health, model, context, slot, and throughput evidence from the configured llama-server."
                : "Runtime inspection is unavailable while another arena operation is running.");
        SetButtonHelp(
            controls.Reconnect,
            state.ReconnectEnabled
                ? "Perform a fresh, uncached runtime inspection. AI Arena does not start or restart the llama-server process."
                : "Runtime reconnection is unavailable while another arena operation is running.");
        SetButtonHelp(controls.Preload, LifecycleHelp(load: true, state.PreloadEnabled));
        SetButtonHelp(controls.Unload, LifecycleHelp(load: false, state.UnloadEnabled));
    }

    private string LifecycleHelp(bool load, bool enabled)
    {
        if (enabled && !lifecycleKnownUnsupported)
        {
            return load
                ? "Ask the inspected llama.cpp router to load the selected model."
                : "Ask the inspected llama.cpp router to unload the selected model.";
        }

        if (lifecycleKnownUnsupported)
        {
            return "This llama-server build did not expose the router model lifecycle endpoint.";
        }

        if (snapshot is null)
        {
            return "Inspect the configured llama-server before using model lifecycle actions.";
        }

        if (!snapshot.Available)
        {
            return "Model lifecycle actions require an available llama-server.";
        }

        if (!snapshot.RouterMode || !snapshot.Capabilities.ModelLifecycle)
        {
            return "Preload and unload are available only when llama-server reports router model lifecycle support.";
        }

        if (string.IsNullOrWhiteSpace(snapshot.Model))
        {
            return "Select a llama.cpp router model before using model lifecycle actions.";
        }

        return load
            ? "The selected model is already reported as loaded."
            : "The selected model is not reported as loaded.";
    }

    private void ApplyWarningTheme(string brushKey)
    {
        var danger = brushKey.Equals("DangerTextBrush", StringComparison.Ordinal);
        controls.Warning.Background = resourceBrush(danger ? "DangerBrush" : "InputBrush");
        controls.Warning.BorderBrush = resourceBrush(danger ? "DangerBorderBrush" : "BetaAccentBrush");
        controls.WarningText.Foreground = resourceBrush(danger ? "DangerTextBrush" : "BetaAccentBrush");
    }

    private static LlamaCppRuntimePresentationInput PresentationInput(LlamaCppRuntimeSnapshot value)
    {
        return new LlamaCppRuntimePresentationInput(
            value.Available,
            value.Ready,
            value.Status,
            value.BuildInfo,
            value.Model,
            value.Quantization,
            value.ContextLength,
            value.GpuLayers,
            value.SlotCount,
            value.BusySlots,
            value.Sleeping,
            value.RouterMode,
            value.Loaded,
            value.MemoryBytes,
            value.ModelSizeBytes,
            value.ParameterCount,
            value.TokensPerSecond,
            value.Capabilities.ModelLifecycle,
            value.Warnings,
            value.Error,
            value.CheckedAt);
    }

    private static string DisplayActionResult(LlamaCppRuntimeActionResult result)
    {
        var action = result.Action.Equals("load", StringComparison.OrdinalIgnoreCase) ? "Preload" : "Unload";
        if (result.Ok)
        {
            return $"{action} completed for {result.Model}.";
        }

        var detail = !string.IsNullOrWhiteSpace(result.Error) ? result.Error.Trim() : result.Status.Trim();
        return result.Supported
            ? $"{action} failed for {result.Model}: {detail}"
            : $"{action} is unsupported: {detail}";
    }

    private static string AppendLine(string current, string value)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return value;
        }

        return current.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Equals(value, StringComparison.Ordinal))
            ? current
            : $"{current}{Environment.NewLine}{value}";
    }

    private static void SetEvidence(TextBlock target, string label, string text, string? help = null)
    {
        target.Text = text;
        var labelledValue = $"{label}: {text}";
        target.ToolTip = string.IsNullOrWhiteSpace(help) ? labelledValue : $"{labelledValue}{Environment.NewLine}{help}";
        AutomationProperties.SetName(target, labelledValue);
        AutomationProperties.SetHelpText(target, string.IsNullOrWhiteSpace(help) ? labelledValue : $"{labelledValue}. {help}");
    }

    private static void SetButtonHelp(Button button, string help)
    {
        button.ToolTip = help;
        AutomationProperties.SetHelpText(button, help);
    }

    private sealed record RuntimeIdentity(string BaseUrl, string ApiMode, string Model, string TokenFingerprint)
    {
        public static RuntimeIdentity Empty { get; } = new("", "", "", "");

        public static RuntimeIdentity From(ModelProviderConfig config)
        {
            var token = config.ApiToken?.Trim() ?? "";
            var fingerprint = token.Length == 0
                ? ""
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            return new RuntimeIdentity(
                (config.BaseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant(),
                ModelProviderApiModes.Normalize(config.ApiMode),
                (config.Model ?? "").Trim(),
                fingerprint);
        }
    }
}

internal sealed record LlamaCppRuntimeControls(
    Border Card,
    TextBlock Status,
    TextBlock CheckedAt,
    TextBlock Build,
    TextBlock Model,
    TextBlock Quantization,
    TextBlock Context,
    TextBlock GpuLayers,
    TextBlock Slots,
    TextBlock Memory,
    TextBlock Throughput,
    Border Warning,
    TextBlock WarningText,
    Button Inspect,
    Button Reconnect,
    Button Preload,
    Button Unload);
