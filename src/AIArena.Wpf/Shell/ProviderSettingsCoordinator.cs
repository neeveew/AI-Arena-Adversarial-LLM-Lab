using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreModelProviderConfig = AIArena.Core.Models.ModelProviderConfig;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class ProviderSettingsCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ModelProviderHealthService providerHealth;
    private readonly ProviderRuntimeService providerRuntime;
    private readonly ModelPreloadService modelPreloadService;
    private readonly LmStudioModelDownloadService modelDownloadService;
    private readonly OllamaModelPullService ollamaModelPullService = new();
    private readonly ProviderAutoConfigureService providerAutoConfigureService;
    private readonly LmStudioModelCatalogService lmStudioModelCatalogService = new();
    private readonly OllamaModelCatalogService ollamaModelCatalogService = new();
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly ComboBox providerPresetPicker;
    private readonly TextBlock providerPresetStatusText;
    private readonly ComboBox providerApiModePicker;
    private readonly TextBox providerBaseUrlText;
    private readonly PasswordBox providerApiTokenBox;
    private readonly ComboBox providerModelText;
    private readonly TextBlock defaultModelStatusText;
    private readonly IReadOnlyDictionary<string, ComboBox> roleModelTextByKey;
    private readonly IReadOnlyDictionary<string, TextBlock> roleModelStatusByKey;
    private readonly Func<string, (double? Temperature, int? MaxOutputTokens)> roleGenerationOverride;
    private readonly TextBlock roleModelSummaryText;
    private readonly ComboBox autoConfigureStrategyPicker;
    private readonly Button autoConfigureButton;
    private readonly Button applyAutoConfigureButton;
    private readonly TextBlock autoConfigureStatusText;
    private readonly TextBlock autoConfigureHardwareText;
    private readonly TextBlock autoConfigureProviderText;
    private readonly Panel autoConfigureRecommendationItems;
    private readonly Button preloadSelectedModelsButton;
    private readonly Button unloadSelectedModelsButton;
    private readonly TextBlock loadPlanPreviewText;
    private readonly TextBlock preloadModelsStatusText;
    private readonly Panel preloadModelsItems;
    private readonly TextBox downloadModelText;
    private readonly ComboBox downloadQuantizationPicker;
    private readonly Button downloadModelButton;
    private readonly Button checkDownloadStatusButton;
    private readonly TextBlock downloadModelStatusText;
    private readonly TextBox providerTimeoutText;
    private readonly TextBox providerContextLengthText;
    private readonly ComboBox providerReasoningPicker;
    private readonly CheckBox providerNativeStatefulChatCheckBox;
    private readonly TextBox providerNativeIdleTtlText;
    private readonly TextBlock providerTestStatus;
    private readonly TextBlock providerModelsStatus;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> appSettingsVisible;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> shortModelName;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<string?, CancellationToken, Task> loadSessionsAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, CancellationToken, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, CancellationToken, Task> refreshActiveSessionAsync;
    private readonly Func<bool, CancellationToken, Task> refreshProviderReachabilityAsync;
    private readonly Action updateProviderHealthPopup;

    private readonly Dictionary<string, string> roleModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPreloadResult> lastPreloadResults = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> advertisedModels = [];
    private LmStudioModelCatalog lastLmStudioCatalog = LmStudioModelCatalog.Empty;
    private OllamaModelCatalog lastOllamaCatalog = OllamaModelCatalog.Empty;
    private bool isRefreshingModels;
    private bool isUpdatingRoleModelEditor;
    private int lastProviderModelCount = -1;
    private DateTimeOffset? lastProviderHealthCheckedAt;
    private DateTimeOffset? lastModelListCheckedAt;
    private ProviderAutoConfigurePlan? lastAutoConfigurePlan;
    private string lastDownloadJobId = "";
    private string lastDownloadModel = "";
    private string lastDownloadQuantization = "";
    private string lastDownloadProviderBaseUrl = "";
    private string lastDownloadApiMode = "";
    private string lastDownloadApiToken = "";

    public ProviderSettingsCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ModelProviderHealthService providerHealth,
        ProviderRuntimeService providerRuntime,
        ModelPreloadService modelPreloadService,
        LmStudioModelDownloadService modelDownloadService,
        ProviderAutoConfigureService providerAutoConfigureService,
        SemaphoreSlim arenaOperationLock,
        ComboBox providerPresetPicker,
        TextBlock providerPresetStatusText,
        ComboBox providerApiModePicker,
        TextBox providerBaseUrlText,
        PasswordBox providerApiTokenBox,
        ComboBox providerModelText,
        TextBlock defaultModelStatusText,
        ComboBox alphaRoleModelText,
        TextBlock alphaModelStatusText,
        ComboBox betaRoleModelText,
        TextBlock betaModelStatusText,
        ComboBox gammaRoleModelText,
        TextBlock gammaModelStatusText,
        ComboBox deltaRoleModelText,
        TextBlock deltaModelStatusText,
        ComboBox narratorRoleModelText,
        TextBlock narratorModelStatusText,
        TextBlock roleModelSummaryText,
        ComboBox autoConfigureStrategyPicker,
        Button autoConfigureButton,
        Button applyAutoConfigureButton,
        TextBlock autoConfigureStatusText,
        TextBlock autoConfigureHardwareText,
        TextBlock autoConfigureProviderText,
        Panel autoConfigureRecommendationItems,
        Button preloadSelectedModelsButton,
        Button unloadSelectedModelsButton,
        TextBlock loadPlanPreviewText,
        TextBlock preloadModelsStatusText,
        Panel preloadModelsItems,
        TextBox downloadModelText,
        ComboBox downloadQuantizationPicker,
        Button downloadModelButton,
        Button checkDownloadStatusButton,
        TextBlock downloadModelStatusText,
        TextBox providerTimeoutText,
        TextBox providerContextLengthText,
        ComboBox providerReasoningPicker,
        CheckBox providerNativeStatefulChatCheckBox,
        TextBox providerNativeIdleTtlText,
        TextBlock providerTestStatus,
        TextBlock providerModelsStatus,
        Func<CoreSessionSummary?> activeSession,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Func<ThemePalette> theme,
        Func<bool> isRenderingSnapshot,
        Func<bool> appSettingsVisible,
        Func<bool> isArenaBusy,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> shortModelName,
        Func<string, string> displayStatusValue,
        Func<string?, CancellationToken, Task> loadSessionsAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, CancellationToken, Task> saveSnapshotWithFeedbackAsync,
        Func<string, CancellationToken, Task> refreshActiveSessionAsync,
        Func<bool, CancellationToken, Task> refreshProviderReachabilityAsync,
        Action updateProviderHealthPopup,
        Func<string, (double? Temperature, int? MaxOutputTokens)>? roleGenerationOverride = null)
    {
        this.roleGenerationOverride = roleGenerationOverride ?? (_ => (null, null));
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerHealth = providerHealth;
        this.providerRuntime = providerRuntime;
        this.modelPreloadService = modelPreloadService;
        this.modelDownloadService = modelDownloadService;
        this.providerAutoConfigureService = providerAutoConfigureService;
        this.arenaOperationLock = arenaOperationLock;
        this.providerPresetPicker = providerPresetPicker;
        this.providerPresetStatusText = providerPresetStatusText;
        this.providerApiModePicker = providerApiModePicker;
        this.providerBaseUrlText = providerBaseUrlText;
        this.providerApiTokenBox = providerApiTokenBox;
        this.providerModelText = providerModelText;
        this.defaultModelStatusText = defaultModelStatusText;
        this.roleModelTextByKey = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = alphaRoleModelText,
            ["beta"] = betaRoleModelText,
            ["gamma"] = gammaRoleModelText,
            ["delta"] = deltaRoleModelText,
            ["narrator"] = narratorRoleModelText
        };
        this.roleModelStatusByKey = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = alphaModelStatusText,
            ["beta"] = betaModelStatusText,
            ["gamma"] = gammaModelStatusText,
            ["delta"] = deltaModelStatusText,
            ["narrator"] = narratorModelStatusText
        };
        this.roleModelSummaryText = roleModelSummaryText;
        this.autoConfigureStrategyPicker = autoConfigureStrategyPicker;
        this.autoConfigureButton = autoConfigureButton;
        this.applyAutoConfigureButton = applyAutoConfigureButton;
        this.autoConfigureStatusText = autoConfigureStatusText;
        this.autoConfigureHardwareText = autoConfigureHardwareText;
        this.autoConfigureProviderText = autoConfigureProviderText;
        this.autoConfigureRecommendationItems = autoConfigureRecommendationItems;
        this.preloadSelectedModelsButton = preloadSelectedModelsButton;
        this.unloadSelectedModelsButton = unloadSelectedModelsButton;
        this.loadPlanPreviewText = loadPlanPreviewText;
        this.preloadModelsStatusText = preloadModelsStatusText;
        this.preloadModelsItems = preloadModelsItems;
        this.downloadModelText = downloadModelText;
        this.downloadQuantizationPicker = downloadQuantizationPicker;
        this.downloadModelButton = downloadModelButton;
        this.checkDownloadStatusButton = checkDownloadStatusButton;
        this.downloadModelStatusText = downloadModelStatusText;
        this.providerTimeoutText = providerTimeoutText;
        this.providerContextLengthText = providerContextLengthText;
        this.providerReasoningPicker = providerReasoningPicker;
        this.providerNativeStatefulChatCheckBox = providerNativeStatefulChatCheckBox;
        this.providerNativeIdleTtlText = providerNativeIdleTtlText;
        this.providerTestStatus = providerTestStatus;
        this.providerModelsStatus = providerModelsStatus;
        this.activeSession = activeSession;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.theme = theme;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.appSettingsVisible = appSettingsVisible;
        this.isArenaBusy = isArenaBusy;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.shortModelName = shortModelName;
        this.displayStatusValue = displayStatusValue;
        this.loadSessionsAsync = loadSessionsAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.refreshProviderReachabilityAsync = refreshProviderReachabilityAsync;
        this.updateProviderHealthPopup = updateProviderHealthPopup;
    }

    public IReadOnlyList<string> AdvertisedModels => advertisedModels;

    public int LastProviderModelCount => lastProviderModelCount;

    public DateTimeOffset? LastProviderHealthCheckedAt => lastProviderHealthCheckedAt;

    public DateTimeOffset? LastModelListCheckedAt => lastModelListCheckedAt;

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        if (ShouldClearDownloadJob(
            lastDownloadJobId,
            lastDownloadProviderBaseUrl,
            lastDownloadApiMode,
            lastDownloadApiToken,
            snapshot.ProviderBaseUrl,
            snapshot.ProviderApiMode,
            snapshot.ProviderApiToken))
        {
            ClearDownloadJob();
        }

        providerBaseUrlText.Text = snapshot.ProviderBaseUrl;
        ShellUiHelpers.SelectComboTag(providerApiModePicker, ModelProviderApiModes.Normalize(snapshot.ProviderApiMode));
        providerApiTokenBox.Password = snapshot.ProviderApiToken;
        ShellUiHelpers.SelectComboTag(providerPresetPicker, ProviderPresetTagForUrl(snapshot.ProviderBaseUrl));
        providerModelText.Text = snapshot.ProviderModel == "-" ? "" : snapshot.ProviderModel;
        providerContextLengthText.Text = snapshot.ProviderContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShellUiHelpers.SelectComboTag(providerReasoningPicker, ModelProviderReasoningModes.Normalize(snapshot.ProviderReasoning));
        providerNativeStatefulChatCheckBox.IsChecked = snapshot.ProviderNativeStatefulChat;
        providerNativeIdleTtlText.Text = snapshot.ProviderNativeIdleTtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateNativeLifecycleControls();
        if (!ModelProviderApiModes.IsLmStudioNative(snapshot.ProviderApiMode))
        {
            lastLmStudioCatalog = LmStudioModelCatalog.Empty;
        }

        if (!ModelProviderApiModes.IsOllamaNative(snapshot.ProviderApiMode))
        {
            lastOllamaCatalog = OllamaModelCatalog.Empty;
        }

        roleModels["alpha"] = snapshot.AlphaModel;
        roleModels["beta"] = snapshot.BetaModel;
        roleModels["gamma"] = snapshot.GammaModel;
        roleModels["delta"] = snapshot.DeltaModel;
        roleModels["narrator"] = snapshot.NarratorModel;
        UpdateRoleModelEditors();
        UpdateRoleModelSummary();
    }

    public void RecordProviderReachabilityCheck(DateTimeOffset checkedAt, int? modelCount)
    {
        lastProviderHealthCheckedAt = checkedAt;
        if (modelCount.HasValue)
        {
            lastProviderModelCount = modelCount.Value;
        }
    }

    public async Task ApplyProviderPresetAsync(CancellationToken cancellationToken = default)
    {
        var preset = ShellUiHelpers.SelectedComboTag(providerPresetPicker, "lm_studio");
        var url = ProviderPresetBaseUrl(preset);
        if (string.IsNullOrWhiteSpace(url))
        {
            providerPresetStatusText.Foreground = resourceBrush("MutedTextBrush");
            providerPresetStatusText.Text = "Manual provider selected. Open Custom connection, type a server address, then press Enter.";
            providerBaseUrlText.Focus();
            return;
        }

        providerBaseUrlText.Text = url;
        ShellUiHelpers.SelectComboTag(
            providerApiModePicker,
            ApiModeForProviderPreset(preset));
        providerPresetStatusText.Foreground = resourceBrush("AlphaAccentBrush");
        providerPresetStatusText.Text = $"Provider preset in use: {url}";
        await PersistModelRoutingAsync("Provider preset saved.", refreshModels: true, cancellationToken);
    }

    public async Task TestProviderAsync(Control busyControl, CancellationToken cancellationToken = default)
    {
        var session = activeSession();
        if (session is null)
        {
            providerTestStatus.Text = "No active session.";
            return;
        }

        await RunBusyAsync(busyControl, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            providerTestStatus.Text = "Testing provider...";
            var result = await providerRuntime.TestAsync(session.Id, allRoles: false, cancellationToken);
            if (!result.Available)
            {
                providerTestStatus.Text = result.Status;
                return;
            }

            if (result.Busy)
            {
                providerTestStatus.Text = result.Status;
                return;
            }

            lastProviderHealthCheckedAt = result.CheckedAt;
            if (result.ModelCount.HasValue)
            {
                lastProviderModelCount = result.ModelCount.Value;
            }

            if (result.Persisted)
            {
                await refreshActiveSessionAsync(result.Status, cancellationToken);
            }

            providerTestStatus.Text = result.Ok
                ? $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Reply}"
                : result.Status;
            updateProviderHealthPopup();
        });
    }

    public async Task SaveAndTestProviderQuickSetupAsync(
        string baseUrl,
        string model,
        TextBlock statusText,
        CancellationToken cancellationToken = default)
    {
        var session = activeSession();
        if (session is null)
        {
            statusText.Foreground = resourceBrush("DangerTextBrush");
            statusText.Text = "No active session.";
            return;
        }

        baseUrl = baseUrl.Trim();
        model = model.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            statusText.Foreground = resourceBrush("DangerTextBrush");
            statusText.Text = "Base URL and default model are required.";
            return;
        }

        statusText.Foreground = resourceBrush("MutedTextBrush");
        statusText.Text = "Saving provider setup...";
        providerBaseUrlText.Text = baseUrl;
        ShellUiHelpers.SelectComboTag(providerApiModePicker, ApiModeForBaseUrl(baseUrl));
        providerModelText.Text = model;
        await PersistModelRoutingAsync("Provider quick setup saved.", refreshModels: true, cancellationToken);

        statusText.Text = "Testing provider completion...";
        var result = await providerRuntime.TestAsync(session.Id, allRoles: false, cancellationToken);
        if (!result.Available)
        {
            statusText.Foreground = resourceBrush("DangerTextBrush");
            statusText.Text = result.Status;
            return;
        }

        if (result.Busy)
        {
            statusText.Foreground = resourceBrush("MutedTextBrush");
            statusText.Text = result.Status;
            return;
        }

        lastProviderHealthCheckedAt = result.CheckedAt;
        if (result.ModelCount.HasValue)
        {
            lastProviderModelCount = result.ModelCount.Value;
        }

        if (result.Ok)
        {
            statusText.Foreground = resourceBrush("AlphaAccentBrush");
            statusText.Text = $"Provider online: {result.Model}, {result.LatencyMs} ms.";
            providerTestStatus.Text = $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Reply}";
            await refreshActiveSessionAsync("Provider quick setup complete.", cancellationToken);
            return;
        }

        statusText.Foreground = result.Reachable ? resourceBrush("BetaAccentBrush") : resourceBrush("DangerTextBrush");
        statusText.Text = result.Reachable
            ? $"Provider responded, but completion failed: {result.Error}"
            : $"Provider offline: {result.Error}";
        providerTestStatus.Text = result.Status;
        if (result.Persisted)
        {
            await refreshActiveSessionAsync(result.Status, cancellationToken);
        }
    }

    public async Task PreloadSelectedModelsAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(preloadSelectedModelsButton, async () =>
        {
            SaveRoleModelDrafts();
            var models = SelectedModelsForPreload();
            var preview = CurrentLoadPlanPreview();
            UpdateLoadPlanPreview();
            if (models.Count == 0)
            {
                preloadModelsStatusText.Foreground = resourceBrush("DangerTextBrush");
                preloadModelsStatusText.Text = "Select a default or participant model before preloading.";
                return;
            }

            if (preview.Status.Equals("cautious", StringComparison.OrdinalIgnoreCase))
            {
                var confirm = ConfirmDialog.Show(
                    owner,
                    theme(),
                    "Preload Selected Models",
                    $"{FormatLoadPlanPreview(preview)}\n\nContinue with preload?",
                    "Preload");
                if (!confirm)
                {
                    preloadModelsStatusText.Foreground = resourceBrush("MutedTextBrush");
                    preloadModelsStatusText.Text = "Preload cancelled.";
                    return;
                }
            }

            preloadModelsStatusText.Foreground = resourceBrush("MutedTextBrush");
            preloadModelsStatusText.Text = $"Preloading {models.Count} selected model(s)...";
            preloadModelsItems.Children.Clear();

            if (!TryCurrentProviderContextLength(out var contextLength))
            {
                preloadModelsStatusText.Foreground = resourceBrush("DangerTextBrush");
                preloadModelsStatusText.Text = providerTestStatus.Text;
                return;
            }

            if (!TryCurrentProviderNativeIdleTtl(out var nativeIdleTtlSeconds))
            {
                preloadModelsStatusText.Foreground = resourceBrush("DangerTextBrush");
                preloadModelsStatusText.Text = providerTestStatus.Text;
                return;
            }

            var apiToken = await CurrentProviderApiTokenAsync();
            var results = await modelPreloadService.PreloadAsync(
                providerBaseUrlText.Text.Trim(),
                models,
                CurrentApiMode(),
                apiToken,
                contextLength,
                nativeIdleTtlSeconds,
                cancellationToken);
            lastPreloadResults.Clear();
            foreach (var result in results)
            {
                lastPreloadResults[result.Model] = result;
            }

            var failures = results.Count(result => result.IsFailure);
            preloadModelsStatusText.Foreground = failures > 0
                ? resourceBrush("DangerTextBrush")
                : resourceBrush("AlphaAccentBrush");
            preloadModelsStatusText.Text = $"Last preload: {DateTime.Now:h:mm:ss tt} - {results.Count} model(s), {failures} warning(s).";
            PopulatePreloadModelBadges(results);
            UpdateLoadPlanPreview();
            providerTestStatus.Text = failures > 0
                ? "Model preload finished with warnings. See preload telemetry."
                : "Selected models preloaded or already available.";

            await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
            UpdateModelStateLabels();
        });
    }

    public async Task UnloadSelectedModelsAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(unloadSelectedModelsButton, async () =>
        {
            SaveRoleModelDrafts();
            var models = SelectedModelsForPreload();
            if (models.Count == 0)
            {
                preloadModelsStatusText.Foreground = resourceBrush("DangerTextBrush");
                preloadModelsStatusText.Text = "Select a default or participant model before unloading.";
                return;
            }

            preloadModelsStatusText.Foreground = resourceBrush("MutedTextBrush");
            preloadModelsStatusText.Text = $"Unloading {models.Count} selected model(s)...";
            preloadModelsItems.Children.Clear();

            var apiToken = await CurrentProviderApiTokenAsync();
            var results = await modelPreloadService.UnloadAsync(
                providerBaseUrlText.Text.Trim(),
                models,
                CurrentApiMode(),
                apiToken,
                cancellationToken);
            lastPreloadResults.Clear();
            foreach (var result in results)
            {
                lastPreloadResults[result.Model] = result;
            }

            var failures = results.Count(result => result.IsFailure);
            preloadModelsStatusText.Foreground = failures > 0
                ? resourceBrush("DangerTextBrush")
                : resourceBrush("AlphaAccentBrush");
            preloadModelsStatusText.Text = $"Last unload: {DateTime.Now:h:mm:ss tt} - {results.Count} model(s), {failures} warning(s).";
            PopulatePreloadModelBadges(results);
            providerTestStatus.Text = failures > 0
                ? "Model unload finished with warnings. See unload telemetry."
                : "Selected models unloaded or already idle.";

            await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
            UpdateModelStateLabels();
        });
    }

    public async Task DownloadModelAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(downloadModelButton, async () =>
        {
            var model = downloadModelText.Text.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
                downloadModelStatusText.Text = "Model ID is required.";
                return;
            }

            var apiMode = CurrentApiMode();
            if (ModelProviderApiModes.IsOllamaNative(apiMode))
            {
                ClearDownloadJob();
                downloadModelStatusText.Foreground = resourceBrush("MutedTextBrush");
                downloadModelStatusText.Text = $"Pulling {model} with Ollama...";

                var pullResult = await ollamaModelPullService.PullAsync(
                    providerBaseUrlText.Text.Trim(),
                    model,
                    await CurrentProviderApiTokenAsync(),
                    cancellationToken);
                downloadModelStatusText.Foreground = pullResult.Ok
                    ? resourceBrush("AlphaAccentBrush")
                    : resourceBrush("DangerTextBrush");
                downloadModelStatusText.Text = FormatOllamaPullStatusText(pullResult);
                providerTestStatus.Text = downloadModelStatusText.Text;
                if (activeSession() is { } ollamaSession)
                {
                    await eventLogStore.AppendAsync(ollamaSession.Id, "ollama_model_pull_requested", new
                    {
                        pullResult.Model,
                        pullResult.Status,
                        pullResult.Digest,
                        pullResult.CompletedBytes,
                        pullResult.TotalBytes,
                        pullResult.Ok
                    }, cancellationToken);
                }

                if (pullResult.Ok)
                {
                    await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
                    UpdateModelStateLabels();
                }

                return;
            }

            if (!ModelProviderApiModes.IsLmStudioNative(apiMode))
            {
                downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
                downloadModelStatusText.Text = "Switch API mode to LM Studio native or Ollama native before downloading.";
                return;
            }

            var quantization = ShellUiHelpers.SelectedComboTag(downloadQuantizationPicker, "");
            ClearDownloadJob();
            downloadModelStatusText.Foreground = resourceBrush("MutedTextBrush");
            downloadModelStatusText.Text = $"Starting download for {model}...";

            var apiToken = await CurrentProviderApiTokenAsync();
            var result = await modelDownloadService.StartDownloadAsync(
                providerBaseUrlText.Text.Trim(),
                model,
                quantization,
                CurrentApiMode(),
                apiToken,
                cancellationToken);
            if (!result.Ok)
            {
                downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
                downloadModelStatusText.Text = $"Download failed: {result.Error}";
                providerTestStatus.Text = downloadModelStatusText.Text;
                return;
            }

            var displayed = result;
            RememberDownloadJob(result);
            if (!result.IsComplete && !string.IsNullOrWhiteSpace(result.JobId))
            {
                var status = await modelDownloadService.GetStatusAsync(
                    providerBaseUrlText.Text.Trim(),
                    result.JobId,
                    result.Model,
                    result.Quantization,
                    apiToken,
                    cancellationToken);
                if (status.Ok)
                {
                    displayed = status;
                    RememberDownloadJob(status);
                }
            }

            downloadModelStatusText.Foreground = displayed.IsComplete
                ? resourceBrush("AlphaAccentBrush")
                : resourceBrush("BetaAccentBrush");
            downloadModelStatusText.Text = FormatDownloadStatusText(displayed);
            providerTestStatus.Text = downloadModelStatusText.Text;
            if (activeSession() is { } session)
            {
                await eventLogStore.AppendAsync(session.Id, "native_model_download_requested", new
                {
                    displayed.Model,
                    displayed.Quantization,
                    displayed.JobId,
                    displayed.Status,
                    displayed.IsComplete
                }, cancellationToken);
            }

            await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
            UpdateModelStateLabels();
        });
    }

    public async Task CheckDownloadStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lastDownloadJobId))
        {
            downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
            downloadModelStatusText.Text = "No LM Studio download job has been started in this session.";
            return;
        }

        if (ShouldClearDownloadJob(
            lastDownloadJobId,
            lastDownloadProviderBaseUrl,
            lastDownloadApiMode,
            lastDownloadApiToken,
            providerBaseUrlText.Text,
            CurrentApiMode(),
            CurrentProviderApiTokenText()))
        {
            ClearDownloadJob();
            downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
            downloadModelStatusText.Text = "Provider changed since the last download job. Start a new download for this provider.";
            providerTestStatus.Text = downloadModelStatusText.Text;
            return;
        }

        await RunBusyAsync(checkDownloadStatusButton, async () =>
        {
            downloadModelStatusText.Foreground = resourceBrush("MutedTextBrush");
            downloadModelStatusText.Text = $"Checking download job {lastDownloadJobId}...";
            var status = await modelDownloadService.GetStatusAsync(
                providerBaseUrlText.Text.Trim(),
                lastDownloadJobId,
                lastDownloadModel,
                lastDownloadQuantization,
                await CurrentProviderApiTokenAsync(),
                cancellationToken);
            if (!status.Ok)
            {
                downloadModelStatusText.Foreground = resourceBrush("DangerTextBrush");
                downloadModelStatusText.Text = $"Download status failed: {status.Error}";
                providerTestStatus.Text = downloadModelStatusText.Text;
                return;
            }

            RememberDownloadJob(status);
            downloadModelStatusText.Foreground = status.IsComplete
                ? resourceBrush("AlphaAccentBrush")
                : resourceBrush("BetaAccentBrush");
            downloadModelStatusText.Text = FormatDownloadStatusText(status);
            providerTestStatus.Text = downloadModelStatusText.Text;
            if (status.IsComplete)
            {
                await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
                UpdateModelStateLabels();
            }
        });
    }

    internal static string FormatDownloadStatusText(LmStudioModelDownloadResult result)
    {
        var model = string.IsNullOrWhiteSpace(result.Model) ? "model" : result.Model;
        if (!result.Ok)
        {
            return $"Download failed: {result.Error}";
        }

        return result.IsComplete
            ? $"Download ready: {model}. {result.Detail}"
            : $"Download running: {model}. {result.Detail}";
    }

    internal static string FormatOllamaPullStatusText(OllamaModelPullResult result)
    {
        var model = string.IsNullOrWhiteSpace(result.Model) ? "model" : result.Model;
        return result.Ok
            ? $"Ollama pull ready: {model}. {result.Detail}"
            : $"Ollama pull failed: {result.Error}";
    }

    internal static bool ShouldClearDownloadJob(
        string jobId,
        string jobProviderBaseUrl,
        string jobApiMode,
        string jobApiToken,
        string currentProviderBaseUrl,
        string currentApiMode,
        string currentApiToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return false;
        }

        return !ProviderContextMatches(jobProviderBaseUrl, jobApiMode, jobApiToken, currentProviderBaseUrl, currentApiMode, currentApiToken);
    }

    internal static bool ShouldRetainDownloadJob(LmStudioModelDownloadResult result)
    {
        return result.Ok && !result.IsComplete && !string.IsNullOrWhiteSpace(result.JobId);
    }

    private void RememberDownloadJob(LmStudioModelDownloadResult result)
    {
        if (!ShouldRetainDownloadJob(result))
        {
            ClearDownloadJob();
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.JobId))
        {
            lastDownloadJobId = result.JobId.Trim();
            lastDownloadProviderBaseUrl = providerBaseUrlText.Text.Trim();
            lastDownloadApiMode = CurrentApiMode();
            lastDownloadApiToken = CurrentProviderApiTokenText();
        }

        if (!string.IsNullOrWhiteSpace(result.Model))
        {
            lastDownloadModel = result.Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(result.Quantization))
        {
            lastDownloadQuantization = result.Quantization.Trim();
        }

        UpdateNativeLifecycleControls();
    }

    private void ClearDownloadJob()
    {
        lastDownloadJobId = "";
        lastDownloadModel = "";
        lastDownloadQuantization = "";
        lastDownloadProviderBaseUrl = "";
        lastDownloadApiMode = "";
        lastDownloadApiToken = "";
        UpdateNativeLifecycleControls();
    }

    public void UpdateNativeLifecycleControls()
    {
        var apiMode = CurrentApiMode();
        var state = NativeLifecycleControlStateFor(apiMode, lastDownloadJobId, isArenaBusy());
        preloadSelectedModelsButton.IsEnabled = state.LifecycleControlsEnabled;
        unloadSelectedModelsButton.IsEnabled = state.LifecycleControlsEnabled;
        downloadModelButton.IsEnabled = state.DownloadControlsEnabled;
        downloadQuantizationPicker.IsEnabled = state.QuantizationEnabled;
        checkDownloadStatusButton.IsEnabled = state.DownloadStatusEnabled;
        providerContextLengthText.IsEnabled = state.NativeOptionsEnabled;
        providerReasoningPicker.IsEnabled = state.NativeOptionsEnabled;
        providerNativeStatefulChatCheckBox.IsEnabled = state.StatefulChatEnabled;
        providerNativeIdleTtlText.IsEnabled = state.NativeOptionsEnabled;
        providerNativeStatefulChatCheckBox.Content = ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? "Stateful LM Studio chat"
            : "Stateful LM Studio chat";

        var busyHint = state.IsBusy ? "Wait for the current arena operation to finish." : "";
        var nativeAvailable = state.NativeAvailable;
        var modeHint = state.IsBusy
            ? busyHint
            : nativeAvailable
            ? NativeLifecycleHint(apiMode)
            : "Switch API mode to LM Studio native or Ollama native to use model lifecycle controls.";
        var downloadHint = state.IsBusy
            ? busyHint
            : ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? "Uses LM Studio native model download endpoints."
            : ModelProviderApiModes.IsOllamaNative(apiMode)
            ? "Uses Ollama native /api/pull. Pulls finish in this request; Status remains for LM Studio jobs."
            : "Switch API mode to LM Studio native to use model downloads.";
        var quantizationHint = state.IsBusy
            ? busyHint
            : ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? "Optional LM Studio quantization selector. Auto lets LM Studio choose."
            : ModelProviderApiModes.IsOllamaNative(apiMode)
            ? "Ollama quantization is part of the model tag, for example qwen3:8b or llama3.2:latest."
            : "Switch API mode to LM Studio native to choose a quantization.";
        var nativeOptionsHint = state.IsBusy
            ? busyHint
            : nativeAvailable
            ? NativeOptionsHint(apiMode)
            : "Switch API mode to LM Studio native or Ollama native to edit native-only options.";
        var statefulHint = state.IsBusy
            ? busyHint
            : ModelProviderApiModes.IsLmStudioNative(apiMode)
            ? "Use LM Studio native response_id and previous_response_id for continuity across turns."
            : ModelProviderApiModes.IsOllamaNative(apiMode)
            ? "Ollama native chat does not use LM Studio response_id continuity."
            : "Switch API mode to LM Studio native to use response_id continuity.";
        preloadSelectedModelsButton.ToolTip = modeHint;
        unloadSelectedModelsButton.ToolTip = modeHint;
        downloadModelButton.ToolTip = downloadHint;
        downloadQuantizationPicker.ToolTip = quantizationHint;
        checkDownloadStatusButton.ToolTip = string.IsNullOrWhiteSpace(lastDownloadJobId)
            ? "Start an LM Studio native model download before checking status."
            : downloadHint;
        providerContextLengthText.ToolTip = nativeOptionsHint;
        providerReasoningPicker.ToolTip = nativeOptionsHint;
        providerNativeStatefulChatCheckBox.ToolTip = statefulHint;
        providerNativeIdleTtlText.ToolTip = nativeOptionsHint;
    }

    internal static bool NativeLifecycleAvailable(string apiMode)
    {
        return ModelProviderApiModes.IsNative(apiMode);
    }

    internal static bool ShouldEnableDownloadStatusButton(string apiMode, string jobId)
    {
        return NativeLifecycleControlStateFor(apiMode, jobId, isBusy: false).DownloadStatusEnabled;
    }

    internal static bool ShouldEnableNativeOptionControls(string apiMode)
    {
        return NativeLifecycleControlStateFor(apiMode, "", isBusy: false).NativeOptionsEnabled;
    }

    internal static NativeLifecycleControlState NativeLifecycleControlStateFor(string apiMode, string jobId, bool isBusy)
    {
        var nativeAvailable = NativeLifecycleAvailable(apiMode);
        var lifecycleEnabled = nativeAvailable && !isBusy;
        var downloadEnabled = (ModelProviderApiModes.IsLmStudioNative(apiMode) || ModelProviderApiModes.IsOllamaNative(apiMode)) && !isBusy;
        var lmStudioDownloadEnabled = ModelProviderApiModes.IsLmStudioNative(apiMode) && !isBusy;
        var statefulChatEnabled = ModelProviderApiModes.IsLmStudioNative(apiMode) && !isBusy;
        return new NativeLifecycleControlState(
            nativeAvailable,
            isBusy,
            lifecycleEnabled,
            downloadEnabled,
            lmStudioDownloadEnabled && !string.IsNullOrWhiteSpace(jobId),
            lifecycleEnabled,
            statefulChatEnabled,
            lmStudioDownloadEnabled);
    }

    internal readonly record struct NativeLifecycleControlState(
        bool NativeAvailable,
        bool IsBusy,
        bool LifecycleControlsEnabled,
        bool DownloadControlsEnabled,
        bool DownloadStatusEnabled,
        bool NativeOptionsEnabled,
        bool StatefulChatEnabled,
        bool QuantizationEnabled);

    private static bool ProviderContextMatches(
        string leftBaseUrl,
        string leftApiMode,
        string leftApiToken,
        string rightBaseUrl,
        string rightApiMode,
        string rightApiToken)
    {
        return NormalizeProviderContextBaseUrl(leftBaseUrl).Equals(NormalizeProviderContextBaseUrl(rightBaseUrl), StringComparison.OrdinalIgnoreCase)
            && ModelProviderApiModes.Normalize(leftApiMode).Equals(ModelProviderApiModes.Normalize(rightApiMode), StringComparison.OrdinalIgnoreCase)
            && leftApiToken.Trim().Equals(rightApiToken.Trim(), StringComparison.Ordinal);
    }

    private static string NormalizeProviderContextBaseUrl(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? ""
            : ModelProviderHealthService.NormalizeBaseUrl(trimmed);
    }

    public async Task AutoConfigureAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(autoConfigureButton, async () =>
        {
            applyAutoConfigureButton.IsEnabled = false;
            autoConfigureRecommendationItems.Children.Clear();
            autoConfigureStatusText.Foreground = resourceBrush("MutedTextBrush");
            autoConfigureStatusText.Text = "Detecting GPU setup, provider capability, and advertised models...";
            autoConfigureHardwareText.Text = "";
            autoConfigureProviderText.Text = "";

            var strategy = ShellUiHelpers.SelectedComboTag(autoConfigureStrategyPicker, "auto");
            var plan = await providerAutoConfigureService.DetectAsync(
                providerBaseUrlText.Text.Trim(),
                strategy,
                CurrentApiMode(),
                CurrentProviderApiTokenText(),
                cancellationToken);
            lastAutoConfigurePlan = plan;
            PopulateAutoConfigurePlan(plan);

            if (plan.ProviderOnline)
            {
                advertisedModels = plan.Models.Select(model => model.Name).ToArray();
                lastProviderModelCount = advertisedModels.Count;
                lastModelListCheckedAt = DateTimeOffset.Now;
                isUpdatingRoleModelEditor = true;
                try
                {
                    UpdateModelComboItems(providerModelText);
                    foreach (var comboBox in RoleModelComboBoxes())
                    {
                        UpdateModelComboItems(comboBox);
                    }
                }
                finally
                {
                    isUpdatingRoleModelEditor = false;
                }
            }

            updateProviderHealthPopup();
        });
    }

    public async Task ApplyAutoConfigureAsync(CancellationToken cancellationToken = default)
    {
        if (lastAutoConfigurePlan is null)
        {
            autoConfigureStatusText.Text = "Run Scan & recommend first.";
            return;
        }

        await RunBusyAsync(applyAutoConfigureButton, async () =>
        {
            var plan = lastAutoConfigurePlan;
            if (!plan.ProviderOnline || string.IsNullOrWhiteSpace(plan.DefaultModel) || plan.Assignments.Count == 0)
            {
                autoConfigureStatusText.Foreground = resourceBrush("DangerTextBrush");
                autoConfigureStatusText.Text = "No usable recommendation to apply.";
                return;
            }

            if (activeSession() is null)
            {
                await sessionStore.EnsureDefaultSessionAsync(cancellationToken);
                await loadSessionsAsync("default", cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            isUpdatingRoleModelEditor = true;
            try
            {
                providerBaseUrlText.Text = plan.ProviderBaseUrl;
                ShellUiHelpers.SelectComboTag(
                    providerApiModePicker,
                    ModelProviderApiModes.Normalize(plan.ApiMode));
                providerModelText.Text = plan.DefaultModel;
                var uniqueModels = plan.Assignments
                    .Select(item => item.Model)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                foreach (var assignment in plan.Assignments)
                {
                    var key = assignment.Role.ToLowerInvariant();
                    var model = uniqueModels <= 1 || assignment.Model.Equals(plan.DefaultModel, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : assignment.Model;
                    SetRoleModelText(key, model);
                    roleModels[key] = model;
                }
            }
            finally
            {
                isUpdatingRoleModelEditor = false;
            }

            SaveRoleModelDrafts();
            UpdateRoleModelSummary();
            await PersistModelRoutingAsync("Auto configuration applied.", refreshModels: true, cancellationToken);
            preloadModelsStatusText.Foreground = resourceBrush("MutedTextBrush");
            preloadModelsStatusText.Text = plan.PreloadGuidance;
            autoConfigureStatusText.Foreground = resourceBrush("AlphaAccentBrush");
            autoConfigureStatusText.Text = "Applied recommended model routing.";
        });
    }

    public async Task ProviderBaseUrlCommittedAsync(CancellationToken cancellationToken = default)
    {
        await PersistModelRoutingAsync("Server address saved.", refreshModels: true, cancellationToken);
    }

    public async Task ProviderModelSelectionChangedAsync(CancellationToken cancellationToken = default)
    {
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor)
        {
            return;
        }

        CommitSelectedComboBoxItem(providerModelText);
        await PersistModelRoutingAsync("Default model saved.", cancellationToken: cancellationToken);
    }

    public async Task ProviderModelCommittedAsync(CancellationToken cancellationToken = default)
    {
        await PersistModelRoutingAsync("Default model saved.", cancellationToken: cancellationToken);
    }

    public async Task ProviderNativeOptionsCommittedAsync(CancellationToken cancellationToken = default)
    {
        await PersistModelRoutingAsync("Provider native options saved.", cancellationToken: cancellationToken);
    }

    public async Task ParticipantModelSelectionChangedAsync(ComboBox comboBox, CancellationToken cancellationToken = default)
    {
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor)
        {
            return;
        }

        CommitSelectedComboBoxItem(comboBox);
        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.", cancellationToken: cancellationToken);
    }

    public async Task ParticipantModelCommittedAsync(ComboBox comboBox, CancellationToken cancellationToken = default)
    {
        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.", cancellationToken: cancellationToken);
    }

    public async Task PersistModelRoutingAsync(
        string successStatus,
        bool refreshModels = false,
        CancellationToken cancellationToken = default)
    {
        var session = activeSession();
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor || session is null)
        {
            return;
        }

        var baseUrl = providerBaseUrlText.Text.Trim();
        var apiMode = CurrentApiMode();
        UpdateNativeLifecycleControls();
        var apiToken = CurrentProviderApiTokenText();
        var defaultModel = providerModelText.Text.Trim();
        SaveRoleModelDrafts();
        UpdateRoleModelSummary();
        var roleModelsToSave = RoleModelKeys()
            .ToDictionary(key => key, RoleModel, StringComparer.OrdinalIgnoreCase);
        var roleOverridesToSave = RoleModelKeys()
            .ToDictionary(key => key, roleGenerationOverride, StringComparer.OrdinalIgnoreCase);
        if (ShouldClearDownloadJob(
            lastDownloadJobId,
            lastDownloadProviderBaseUrl,
            lastDownloadApiMode,
            lastDownloadApiToken,
            baseUrl,
            apiMode,
            apiToken))
        {
            ClearDownloadJob();
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            providerTestStatus.Text = "Server address is required.";
            return;
        }

        if (!TryCurrentProviderContextLength(out var providerContextLength))
        {
            return;
        }

        if (!TryCurrentProviderNativeIdleTtl(out var nativeIdleTtlSeconds))
        {
            return;
        }

        await arenaOperationLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
            if (snapshot is null)
            {
                providerTestStatus.Text = $"No snapshot found for session {session.Id}.";
                return;
            }

            var existingShared = snapshot.Configs.TryGetValue("shared", out var shared)
                ? shared
                : new CoreModelProviderConfig();
            var normalizedBaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl);
            var updatedShared = ModelRoutingSharedConfig(
                existingShared,
                normalizedBaseUrl,
                apiMode,
                apiToken,
                defaultModel,
                providerContextLength,
                ShellUiHelpers.SelectedComboTag(providerReasoningPicker, ""),
                providerNativeStatefulChatCheckBox.IsChecked == true,
                nativeIdleTtlSeconds);

            snapshot.Configs["shared"] = updatedShared;
            foreach (var roleKey in RoleModelKeys())
            {
                var (temperatureOverride, maxOutputTokensOverride) = roleOverridesToSave[roleKey];
                SaveRoleModelConfig(snapshot.Configs, roleKey, roleModelsToSave[roleKey], updatedShared, temperatureOverride, maxOutputTokensOverride);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await saveSnapshotWithFeedbackAsync(snapshot, session.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await eventLogStore.AppendAsync(session.Id, "native_model_routing_applied", new
            {
                updatedShared.BaseUrl,
                updatedShared.ApiMode,
                updatedShared.Model,
                updatedShared.NativeIdleTtlSeconds,
                AlphaModel = roleModelsToSave["alpha"],
                BetaModel = roleModelsToSave["beta"],
                GammaModel = roleModelsToSave["gamma"],
                DeltaModel = roleModelsToSave["delta"],
                NarratorModel = roleModelsToSave["narrator"]
            }, cancellationToken);
        }
        finally
        {
            arenaOperationLock.Release();
        }

        await refreshActiveSessionAsync(successStatus, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        providerTestStatus.Text = successStatus;
        if (refreshModels)
        {
            await RefreshAdvertisedModelsAsync(force: true, cancellationToken);
        }

        await refreshProviderReachabilityAsync(true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RefreshAdvertisedModelsAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!force && !appSettingsVisible())
        {
            return;
        }

        if (isRefreshingModels)
        {
            return;
        }

        isRefreshingModels = true;
        try
        {
            var config = new CoreModelProviderConfig
            {
                BaseUrl = providerBaseUrlText.Text.Trim(),
                ApiMode = CurrentApiMode(),
                ApiToken = CurrentProviderApiTokenText(),
                Model = providerModelText.Text.Trim(),
                Timeout = int.TryParse(providerTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout)
                    ? Math.Clamp(timeout, 1, 30)
                    : 5,
                Temperature = 0,
                MaxOutputTokens = 16
            };
            var apiToken = await CurrentProviderApiTokenAsync();
            var nativeCatalogError = "";
            if (config.ApiMode.Equals(ModelProviderApiModes.LmStudioNative, StringComparison.OrdinalIgnoreCase))
            {
                var nativeCatalog = await lmStudioModelCatalogService.TryLoadAsync(config.BaseUrl, apiToken, cancellationToken);
                if (nativeCatalog.Ok && nativeCatalog.Models.Count > 0)
                {
                    lastModelListCheckedAt = DateTimeOffset.Now;
                    lastLmStudioCatalog = nativeCatalog;
                    lastOllamaCatalog = OllamaModelCatalog.Empty;
                    advertisedModels = AdvertisedModelNames([], lastLmStudioCatalog, lastOllamaCatalog);
                    lastProviderModelCount = advertisedModels.Count;
                    providerModelsStatus.Text = FormatProviderModelsStatus(advertisedModels.Count, lastLmStudioCatalog, lastOllamaCatalog);
                    providerModelsStatus.ToolTip = FormatProviderModelsTooltip(lastLmStudioCatalog, lastOllamaCatalog, "");
                    isUpdatingRoleModelEditor = true;
                    try
                    {
                        UpdateModelComboItems(providerModelText);
                        foreach (var comboBox in RoleModelComboBoxes())
                        {
                            UpdateModelComboItems(comboBox);
                        }

                        UpdateModelStateLabels();
                    }
                    finally
                    {
                        isUpdatingRoleModelEditor = false;
                    }

                    updateProviderHealthPopup();
                    return;
                }

                nativeCatalogError = nativeCatalog.Error;
            }

            if (config.ApiMode.Equals(ModelProviderApiModes.OllamaNative, StringComparison.OrdinalIgnoreCase))
            {
                var ollamaCatalog = await ollamaModelCatalogService.TryLoadAsync(config.BaseUrl, apiToken, cancellationToken);
                if (ollamaCatalog.Ok)
                {
                    lastModelListCheckedAt = DateTimeOffset.Now;
                    lastLmStudioCatalog = LmStudioModelCatalog.Empty;
                    lastOllamaCatalog = ollamaCatalog;
                    advertisedModels = AdvertisedModelNames([], lastLmStudioCatalog, lastOllamaCatalog);
                    lastProviderModelCount = advertisedModels.Count;
                    providerModelsStatus.Text = FormatProviderModelsStatus(advertisedModels.Count, lastLmStudioCatalog, lastOllamaCatalog);
                    providerModelsStatus.ToolTip = FormatProviderModelsTooltip(lastLmStudioCatalog, lastOllamaCatalog, "");
                    isUpdatingRoleModelEditor = true;
                    try
                    {
                        UpdateModelComboItems(providerModelText);
                        foreach (var comboBox in RoleModelComboBoxes())
                        {
                            UpdateModelComboItems(comboBox);
                        }

                        UpdateModelStateLabels();
                    }
                    finally
                    {
                        isUpdatingRoleModelEditor = false;
                    }

                    updateProviderHealthPopup();
                    return;
                }

                nativeCatalogError = ollamaCatalog.Error;
            }

            // Native discovery already ran above. If it is unavailable, make one
            // explicit OpenAI-compatible fallback instead of repeating the same
            // native endpoint two more times.
            var listConfig = ModelProviderApiModes.IsNative(config.ApiMode)
                ? new CoreModelProviderConfig
                {
                    BaseUrl = config.BaseUrl,
                    ApiMode = ModelProviderApiModes.OpenAiCompatible,
                    ApiToken = config.ApiToken,
                    Model = config.Model,
                    Timeout = config.Timeout,
                    Temperature = config.Temperature,
                    MaxOutputTokens = config.MaxOutputTokens
                }
                : config;
            var result = await providerHealth.ListModelsAsync(listConfig, cancellationToken);
            lastModelListCheckedAt = result.CheckedAt;
            if (result.Ok)
            {
                lastLmStudioCatalog = LmStudioModelCatalog.Empty;
                lastOllamaCatalog = OllamaModelCatalog.Empty;
                advertisedModels = AdvertisedModelNames(result.Models, lastLmStudioCatalog, lastOllamaCatalog);
                lastProviderModelCount = advertisedModels.Count;
                providerModelsStatus.Text = FormatProviderModelsStatus(advertisedModels.Count, lastLmStudioCatalog, lastOllamaCatalog);
                providerModelsStatus.ToolTip = FormatProviderModelsTooltip(lastLmStudioCatalog, lastOllamaCatalog, nativeCatalogError);
                isUpdatingRoleModelEditor = true;
                try
                {
                    UpdateModelComboItems(providerModelText);
                    foreach (var comboBox in RoleModelComboBoxes())
                    {
                        UpdateModelComboItems(comboBox);
                    }

                    UpdateModelStateLabels();
                }
                finally
                {
                    isUpdatingRoleModelEditor = false;
                }
        }
        else
        {
            lastLmStudioCatalog = LmStudioModelCatalog.Empty;
            lastOllamaCatalog = OllamaModelCatalog.Empty;
            advertisedModels = [];
            lastProviderModelCount = 0;
            var modelListError = string.Join(
                " ",
                new[] { nativeCatalogError, result.Error }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal));
            providerModelsStatus.Text = $"Model list unavailable: {modelListError}";
            providerModelsStatus.ToolTip = modelListError;
            isUpdatingRoleModelEditor = true;
            try
            {
                UpdateModelComboItems(providerModelText);
                foreach (var comboBox in RoleModelComboBoxes())
                {
                    UpdateModelComboItems(comboBox);
                }

                UpdateModelStateLabels();
            }
            finally
            {
                isUpdatingRoleModelEditor = false;
            }
        }

            updateProviderHealthPopup();
        }
        finally
        {
            isRefreshingModels = false;
        }
    }

    public (string BaseUrl, string ApiMode, string Model, IReadOnlyDictionary<string, string> RoleModels) CaptureProviderProfile()
    {
        SaveRoleModelDrafts();
        var roleModelsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in RoleModelKeys())
        {
            roleModelsByKey[key] = RoleModel(key);
        }

        return (providerBaseUrlText.Text.Trim(), CurrentApiMode(), providerModelText.Text.Trim(), roleModelsByKey);
    }

    public async Task ApplyProviderProfileAsync(
        string baseUrl,
        string apiMode,
        string model,
        IReadOnlyDictionary<string, string> roleModelsByKey,
        string profileName,
        CancellationToken cancellationToken = default)
    {
        isUpdatingRoleModelEditor = true;
        try
        {
            providerBaseUrlText.Text = baseUrl;
            ShellUiHelpers.SelectComboTag(providerApiModePicker, ModelProviderApiModes.Normalize(apiMode));
            providerModelText.Text = model;
            foreach (var key in RoleModelKeys())
            {
                SetRoleModelText(key, roleModelsByKey.TryGetValue(key, out var roleModel) ? roleModel : "");
            }
        }
        finally
        {
            isUpdatingRoleModelEditor = false;
        }

        UpdateNativeLifecycleControls();
        await PersistModelRoutingAsync($"Profile '{profileName}' applied.", refreshModels: true, cancellationToken);
    }

    public async Task TestAllRolesAsync(CancellationToken cancellationToken = default)
    {
        SaveRoleModelDrafts();
        var defaultModel = providerModelText.Text.Trim();
        var resultsByModel = new Dictionary<string, ModelProviderTestResult>(StringComparer.OrdinalIgnoreCase);
        providerTestStatus.Text = "Testing all role models...";
        foreach (var key in RoleModelKeys())
        {
            if (!roleModelStatusByKey.TryGetValue(key, out var label))
            {
                continue;
            }

            var model = RoleModel(key);
            var effectiveModel = string.IsNullOrWhiteSpace(model) ? defaultModel : model;
            if (string.IsNullOrWhiteSpace(effectiveModel))
            {
                SetModelState(label, "no model", resourceBrush("MutedTextBrush"));
                continue;
            }

            SetModelState(label, "testing...", resourceBrush("MutedTextBrush"));
            if (!resultsByModel.TryGetValue(effectiveModel, out var result))
            {
                var config = new CoreModelProviderConfig
                {
                    BaseUrl = providerBaseUrlText.Text.Trim(),
                    ApiMode = CurrentApiMode(),
                    ApiToken = CurrentProviderApiTokenText(),
                    Model = effectiveModel,
                    Timeout = 120,
                    Temperature = 0,
                    MaxOutputTokens = 16
                };
                result = await providerHealth.TestCompletionAsync(config, cancellationToken);
                resultsByModel[effectiveModel] = result;
            }

            SetModelState(
                label,
                result.Ok ? $"ok {result.LatencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms" : "failed",
                resourceBrush(result.Ok ? "PrimaryBorderBrush" : "DangerTextBrush"),
                result.Ok ? $"{effectiveModel}: completed in {result.LatencyMs} ms" : $"{effectiveModel}: {result.Error}");
        }

        var failures = resultsByModel.Values.Count(result => !result.Ok);
        providerTestStatus.Text = failures == 0
            ? $"All {resultsByModel.Count} role model{(resultsByModel.Count == 1 ? "" : "s")} completed."
            : $"{failures} of {resultsByModel.Count} role model{(resultsByModel.Count == 1 ? "" : "s")} failed; hover a role status for the error.";
    }

    public async Task UseDefaultModelForAllRolesAsync(CancellationToken cancellationToken = default)
    {
        var model = providerModelText.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            providerTestStatus.Text = "Pick a default model before making every role inherit it.";
            return;
        }

        isUpdatingRoleModelEditor = true;
        try
        {
            foreach (var key in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
            {
                SetRoleModelText(key, "");
            }
        }
        finally
        {
            isUpdatingRoleModelEditor = false;
        }

        await PersistModelRoutingAsync($"Every role now follows the default model ({model}).", cancellationToken: cancellationToken);
    }

    public void SaveRoleModelDrafts()
    {
        foreach (var comboBox in RoleModelComboBoxes())
        {
            SaveRoleModelDraft(comboBox);
        }

        UpdateRoleModelSummary();
    }

    public string RoleModel(string key)
    {
        return roleModels.TryGetValue(key, out var model) ? model.Trim() : "";
    }

    public string CurrentApiMode()
    {
        return ModelProviderApiModes.Normalize(ShellUiHelpers.SelectedComboTag(providerApiModePicker, ModelProviderApiModes.OpenAiCompatible));
    }

    public static void SaveRoleModelConfig(
        IDictionary<string, CoreModelProviderConfig> configs,
        string key,
        string model,
        CoreModelProviderConfig shared,
        double? temperatureOverride = null,
        int? maxOutputTokensOverride = null)
    {
        ProviderConfigurationControlService.SaveRoleModelConfig(
            configs,
            key,
            model,
            shared,
            temperatureOverride,
            maxOutputTokensOverride);
    }

    internal static CoreModelProviderConfig ModelRoutingSharedConfig(
        CoreModelProviderConfig existingShared,
        string baseUrl,
        string apiMode,
        string apiToken,
        string model,
        int contextLength,
        string reasoning,
        bool nativeStatefulChat,
        int nativeIdleTtlSeconds)
    {
        var normalizedContextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(contextLength);
        var normalizedReasoning = ModelProviderReasoningModes.Normalize(reasoning);
        var normalizedNativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(nativeIdleTtlSeconds);
        var providerReadinessChanged = ProviderReadinessChanged(
            existingShared,
            baseUrl,
            apiMode,
            apiToken,
            model,
            normalizedContextLength,
            normalizedReasoning,
            nativeStatefulChat,
            normalizedNativeIdleTtlSeconds);
        return new CoreModelProviderConfig
        {
            BaseUrl = baseUrl,
            ApiMode = apiMode,
            ApiToken = apiToken,
            Model = model,
            Timeout = existingShared.Timeout,
            Temperature = existingShared.Temperature,
            MaxOutputTokens = existingShared.MaxOutputTokens,
            ContextLength = normalizedContextLength,
            Reasoning = normalizedReasoning,
            NativeStatefulChat = nativeStatefulChat,
            NativeIdleTtlSeconds = normalizedNativeIdleTtlSeconds,
            LastError = providerReadinessChanged ? "" : existingShared.LastError,
            LastLatencyMs = providerReadinessChanged ? 0 : existingShared.LastLatencyMs,
            LastTestOk = !providerReadinessChanged && existingShared.LastTestOk,
            Extra = existingShared.Extra
        };
    }

    internal static bool TryNormalizeProviderContextLength(string value, out int contextLength)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            contextLength = 0;
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            contextLength = ArenaSessionMutationCoordinator.ClampProviderContextLength(parsed);
            return true;
        }

        contextLength = 0;
        return false;
    }

    internal static bool TryNormalizeProviderNativeIdleTtlSeconds(string value, out int nativeIdleTtlSeconds)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            nativeIdleTtlSeconds = 0;
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            nativeIdleTtlSeconds = ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(parsed);
            return true;
        }

        nativeIdleTtlSeconds = 0;
        return false;
    }

    internal static bool ProviderIdentityChanged(CoreModelProviderConfig existing, string baseUrl, string apiMode, string apiToken, string model)
    {
        return !existing.BaseUrl.Trim().Equals(baseUrl.Trim(), StringComparison.Ordinal)
            || !ModelProviderApiModes.Normalize(existing.ApiMode).Equals(ModelProviderApiModes.Normalize(apiMode), StringComparison.OrdinalIgnoreCase)
            || !existing.ApiToken.Trim().Equals(apiToken.Trim(), StringComparison.Ordinal)
            || !existing.Model.Trim().Equals(model.Trim(), StringComparison.Ordinal);
    }

    internal static bool ProviderReadinessChanged(
        CoreModelProviderConfig existing,
        string baseUrl,
        string apiMode,
        string apiToken,
        string model,
        int contextLength,
        string reasoning,
        bool nativeStatefulChat,
        int nativeIdleTtlSeconds)
    {
        return ProviderConfigurationControlService.ProviderReadinessChanged(
            existing,
            baseUrl,
            apiMode,
            apiToken,
            model,
            ArenaSessionMutationCoordinator.ClampProviderContextLength(contextLength),
            ModelProviderReasoningModes.Normalize(reasoning),
            nativeStatefulChat,
            ArenaSessionMutationCoordinator.ClampProviderNativeIdleTtlSeconds(nativeIdleTtlSeconds));
    }

    private void PopulateAutoConfigurePlan(ProviderAutoConfigurePlan plan)
    {
        autoConfigureRecommendationItems.Children.Clear();
        autoConfigureStatusText.Foreground = plan.ProviderOnline
            ? resourceBrush("AlphaAccentBrush")
            : resourceBrush("DangerTextBrush");
        autoConfigureStatusText.Text = plan.ProviderOnline
            ? $"Detected {plan.Models.Count} chat model(s). Strategy: {DisplayAutoConfigureStrategy(plan.Strategy)}."
            : "Provider offline or no advertised models found.";
        autoConfigureHardwareText.Text = FormatHardwareSummary(plan.Hardware);
        autoConfigureProviderText.Text = $"Provider: {plan.ProviderBaseUrl} - {(plan.LmStudioNativeApi ? "LM Studio enhanced mode" : "OpenAI-compatible mode")}. {FormatAutoConfigureCapabilitySummary(plan)} {plan.PreloadGuidance}";

        foreach (var assignment in plan.Assignments)
        {
            autoConfigureRecommendationItems.Children.Add(CreateAutoConfigureBadge(assignment));
        }

        foreach (var warning in plan.Warnings)
        {
            autoConfigureRecommendationItems.Children.Add(CreateTextBadge("Note", warning, resourceBrush("MutedTextBrush")));
        }

        applyAutoConfigureButton.IsEnabled = plan.ProviderOnline && plan.Assignments.Count > 0;
        providerModelsStatus.Text = plan.ProviderOnline
            ? $"{plan.Models.Count} advertised chat models found during the scan."
            : "The scan could not reach an OpenAI-compatible provider.";
        UpdateLoadPlanPreview();
    }

    private Border CreateAutoConfigureBadge(ModelAssignmentRecommendation assignment)
    {
        var accent = accentForSpeaker(assignment.Role);
        return new Border
        {
            Background = BlendBrush(resourceBrush("InputBrush"), accent, 0.12),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 5),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = $"{assignment.Role}: {assignment.Model}{Environment.NewLine}{assignment.Reason}",
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = assignment.Role,
                        Foreground = accent,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = shortModelName(assignment.Model),
                        Foreground = resourceBrush("TextBrush"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 150
                    },
                    new TextBlock
                    {
                        Text = AutoConfigureBadgeDetail(assignment.Model),
                        Foreground = resourceBrush("MutedTextBrush"),
                        FontSize = 10.5,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 150
                    }
                }
            }
        };
    }

    private Border CreateTextBadge(string label, string text, Brush accent)
    {
        return new Border
        {
            Background = BlendBrush(resourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 5),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = text,
            Child = new TextBlock
            {
                Text = $"{label}: {text}",
                Foreground = accent,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            }
        };
    }

    private void SetRoleModelText(string key, string model)
    {
        if (roleModelTextByKey.TryGetValue(key, out var comboBox))
        {
            comboBox.Text = model;
        }
    }

    private static IReadOnlyList<string> AdvertisedModelNames(
        IEnumerable<string> openAiModels,
        LmStudioModelCatalog catalog,
        OllamaModelCatalog ollamaCatalog)
    {
        var nativeChatModels = catalog.Ok
            ? catalog.ChatModels.Select(model => model.PreferredIdentifier)
            : Array.Empty<string>();
        var ollamaModels = ollamaCatalog.Ok
            ? ollamaCatalog.Models.Select(model => model.PreferredIdentifier)
            : Array.Empty<string>();
        return nativeChatModels
            .Concat(ollamaModels)
            .Concat(openAiModels)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatProviderModelsStatus(int advertisedCount, LmStudioModelCatalog catalog, OllamaModelCatalog ollamaCatalog)
    {
        if (catalog.Ok && catalog.Models.Count > 0)
        {
            return $"LM Studio catalog: {catalog.ChatModels.Count} chat, {catalog.EmbeddingModels.Count} embedding, {catalog.LoadedCount} loaded. Refreshes every 5s.";
        }

        if (ollamaCatalog.Ok)
        {
            var psDetail = ollamaCatalog.RunningModelsOk
                ? $"{ollamaCatalog.LoadedCount} loaded"
                : "running state unavailable";
            return $"Ollama catalog: {ollamaCatalog.Models.Count} local, {psDetail}. Refreshes every 5s.";
        }

        return $"{advertisedCount} advertised models found. Refreshes every 5s while settings are open.";
    }

    private static string FormatProviderModelsTooltip(LmStudioModelCatalog catalog, OllamaModelCatalog ollamaCatalog, string fallbackError)
    {
        if (catalog.Ok && catalog.Models.Count > 0)
        {
            var highlights = catalog.ChatModels
                .Take(8)
                .Select(model => $"{model.PreferredIdentifier}: {model.CapabilitySummary}")
                .ToArray();
            return highlights.Length == 0
                ? "LM Studio native catalog is available, but no chat models were found."
                : string.Join(Environment.NewLine, highlights);
        }

        if (ollamaCatalog.Ok)
        {
            var highlights = ollamaCatalog.Models
                .Take(8)
                .Select(model => $"{model.PreferredIdentifier}: {model.CapabilitySummary}")
                .ToArray();
            var runningWarning = ollamaCatalog.RunningModelsOk || string.IsNullOrWhiteSpace(ollamaCatalog.RunningModelsError)
                ? ""
                : $"{Environment.NewLine}Running model state unavailable: {ollamaCatalog.RunningModelsError}";
            return highlights.Length == 0
                ? $"Ollama native catalog is available, but no local models were found.{runningWarning}"
                : string.Join(Environment.NewLine, highlights) + runningWarning;
        }

        return string.IsNullOrWhiteSpace(fallbackError)
            ? "OpenAI-compatible model list is available. LM Studio native metadata was not detected."
            : fallbackError;
    }

    private string FormatAutoConfigureCapabilitySummary(ProviderAutoConfigurePlan plan)
    {
        if (!plan.LmStudioNativeApi || plan.Models.Count == 0)
        {
            return "";
        }

        var loaded = plan.Models.Count(model => model.Loaded);
        var toolReady = plan.Models.Count(model => model.TrainedForToolUse);
        var vision = plan.Models.Count(model => model.Vision);
        var maxContext = plan.Models
            .Select(model => model.MaxContextLength)
            .Where(value => value.HasValue)
            .Select(value => value.GetValueOrDefault())
            .DefaultIfEmpty(0)
            .Max();
        var parts = new List<string>
        {
            $"{loaded} loaded",
            $"{toolReady} tool-ready"
        };
        if (vision > 0)
        {
            parts.Add($"{vision} vision");
        }

        if (maxContext > 0)
        {
            parts.Add($"max context {FormatTokenCount(maxContext)}");
        }

        return $"Native catalog: {string.Join(", ", parts)}.";
    }

    private string AutoConfigureBadgeDetail(string model)
    {
        var profile = lastAutoConfigurePlan?.Models.FirstOrDefault(item => item.Name.Equals(model, StringComparison.OrdinalIgnoreCase));
        return profile is null ? "" : profile.CapabilitySummary;
    }

    private string ModelStateTooltip(string model)
    {
        if (lastPreloadResults.TryGetValue(model, out var preload))
        {
            return $"{preload.Status}: {preload.Detail}";
        }

        var native = lastLmStudioCatalog.Find(model);
        if (native is not null)
        {
            return native.Tooltip();
        }

        var ollama = lastOllamaCatalog.Find(model);
        if (ollama is not null)
        {
            return ollama.Tooltip();
        }

        return advertisedModels.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? $"{model}{Environment.NewLine}Advertised by the OpenAI-compatible provider."
            : $"{model}{Environment.NewLine}Not present in the latest advertised model list.";
    }

    private void UpdateModelComboItems(ComboBox comboBox)
    {
        var current = comboBox.Text;
        var values = advertisedModels
            .Append(current)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        comboBox.ItemsSource = values;
        comboBox.Text = current;
    }

    private void UpdateRoleModelEditors()
    {
        isUpdatingRoleModelEditor = true;
        try
        {
            foreach (var comboBox in RoleModelComboBoxes())
            {
                UpdateModelComboItems(comboBox);
                comboBox.Text = RoleModel(comboBox.Tag?.ToString() ?? "");
            }
        }
        finally
        {
            isUpdatingRoleModelEditor = false;
        }
    }

    private void UpdateRoleModelSummary()
    {
        var defaultModel = providerModelText.Text.Trim();
        var lines = new List<string>
        {
            $"Default: {DisplayRoleModel(defaultModel, "not selected")}",
            $"Alpha: {DisplayParticipantModel(RoleModel("alpha"), defaultModel)}",
            $"Beta: {DisplayParticipantModel(RoleModel("beta"), defaultModel)}",
            $"Gamma: {DisplayParticipantModel(RoleModel("gamma"), defaultModel)}",
            $"Delta: {DisplayParticipantModel(RoleModel("delta"), defaultModel)}",
            $"Narrator: {DisplayParticipantModel(RoleModel("narrator"), defaultModel)}"
        };
        var extraAgents = (lastRenderedSnapshot()?.Agents ?? [])
            .Where(agent => AgentRosterService.ParticipantOrder(agent.Id) >= 4)
            .Select(agent => displayStatusValue(agent.Id))
            .ToArray();
        if (extraAgents.Length > 0)
        {
            lines.Insert(lines.Count - 1, $"{string.Join(", ", extraAgents)}: inherit default");
        }

        roleModelSummaryText.Text = string.Join(Environment.NewLine, lines);
        UpdateModelStateLabels();
        UpdateLoadPlanPreview();
    }

    private void UpdateModelStateLabels()
    {
        UpdateDefaultModelStateLabel();
        foreach (var key in RoleModelKeys())
        {
            if (roleModelStatusByKey.TryGetValue(key, out var target))
            {
                UpdateRoleModelStateLabel(key, target);
            }
        }
    }

    private void UpdateDefaultModelStateLabel()
    {
        var model = providerModelText.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            SetModelState(defaultModelStatusText, "not selected", resourceBrush("MutedTextBrush"));
            return;
        }

        SetModelState(defaultModelStatusText, ModelStateLabel(model), ModelStateBrush(model), ModelStateTooltip(model));
    }

    private void UpdateRoleModelStateLabel(string key, TextBlock target)
    {
        var model = RoleModel(key);
        if (string.IsNullOrWhiteSpace(model))
        {
            SetModelState(target, "inherits default", resourceBrush("MutedTextBrush"));
            return;
        }

        SetModelState(target, ModelStateLabel(model), ModelStateBrush(model), ModelStateTooltip(model));
    }

    private string ModelStateLabel(string model)
    {
        if (lastPreloadResults.TryGetValue(model, out var preload) && preload.IsFailure)
        {
            return "failed preload";
        }

        if (advertisedModels.Count == 0)
        {
            return "selected";
        }

        var native = lastLmStudioCatalog.Find(model);
        if (native is not null)
        {
            return native.Loaded ? "loaded" : "available";
        }

        var ollama = lastOllamaCatalog.Find(model);
        if (ollama is not null)
        {
            return ollama.Loaded ? "loaded" : "available";
        }

        return advertisedModels.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? "available"
            : "unavailable";
    }

    private Brush ModelStateBrush(string model)
    {
        var label = ModelStateLabel(model);
        return label switch
        {
            "failed preload" or "unavailable" => resourceBrush("DangerTextBrush"),
            "loaded" => resourceBrush("PrimaryBorderBrush"),
            "available" or "selected" => resourceBrush("AlphaAccentBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    private void SaveRoleModelDraft(ComboBox comboBox)
    {
        if (comboBox.Tag is string key)
        {
            roleModels[key] = comboBox.Text.Trim();
        }
    }

    private IEnumerable<ComboBox> RoleModelComboBoxes()
    {
        foreach (var key in RoleModelKeys())
        {
            yield return roleModelTextByKey[key];
        }
    }

    private IReadOnlyList<string> SelectedModelsForPreload()
    {
        var models = new List<string>();
        var defaultModel = providerModelText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(defaultModel))
        {
            models.Add(defaultModel);
        }

        foreach (var key in RoleModelKeys())
        {
            var model = RoleModel(key);
            if (!string.IsNullOrWhiteSpace(model))
            {
                models.Add(model);
            }
        }

        return models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<string> CurrentProviderApiTokenAsync()
    {
        await Task.CompletedTask;
        return CurrentProviderApiTokenText();
    }

    private string CurrentProviderApiTokenText()
    {
        return providerApiTokenBox.Password.Trim();
    }

    private int CurrentProviderContextLength()
    {
        return TryNormalizeProviderContextLength(providerContextLengthText.Text, out var contextLength)
            ? contextLength
            : 0;
    }

    private bool TryCurrentProviderContextLength(out int contextLength)
    {
        if (TryNormalizeProviderContextLength(providerContextLengthText.Text, out contextLength))
        {
            return true;
        }

        providerTestStatus.Text = "Provider context must be a whole number, or blank/0 for the provider default.";
        return false;
    }

    private bool TryCurrentProviderNativeIdleTtl(out int nativeIdleTtlSeconds)
    {
        if (TryNormalizeProviderNativeIdleTtlSeconds(providerNativeIdleTtlText.Text, out nativeIdleTtlSeconds))
        {
            return true;
        }

        providerTestStatus.Text = "Native idle TTL must be whole seconds, or blank/0 for provider default.";
        return false;
    }

    private ModelLoadPlanPreview CurrentLoadPlanPreview()
    {
        return ProviderAutoConfigureService.PreviewLoadPlan(SelectedModelsForPreload(), lastAutoConfigurePlan?.Hardware);
    }

    private void UpdateLoadPlanPreview()
    {
        var preview = CurrentLoadPlanPreview();
        loadPlanPreviewText.Text = FormatLoadPlanPreview(preview);
        loadPlanPreviewText.Foreground = preview.Status switch
        {
            "comfortable" => resourceBrush("AlphaAccentBrush"),
            "cautious" => resourceBrush("BetaAccentBrush"),
            "mixed" => resourceBrush("BetaAccentBrush"),
            "empty" => resourceBrush("MutedTextBrush"),
            _ => resourceBrush("MutedTextBrush")
        };
    }

    private void PopulatePreloadModelBadges(IReadOnlyList<ModelPreloadResult> results)
    {
        preloadModelsItems.Children.Clear();
        foreach (var result in results)
        {
            var accent = result.Status.ToLowerInvariant() switch
            {
                "loaded" or "ready" or "reloaded" => resourceBrush("PrimaryBorderBrush"),
                "unloaded" => resourceBrush("AlphaAccentBrush"),
                "skipped" or "not loaded" => resourceBrush("MutedTextBrush"),
                "unsupported" or "missing" => resourceBrush("BetaAccentBrush"),
                _ => resourceBrush("DangerTextBrush")
            };
            var label = string.IsNullOrWhiteSpace(result.Model)
                ? TitleCaseStatus(result.Status)
                : $"{shortModelName(result.Model)} - {TitleCaseStatus(result.Status)}";

            preloadModelsItems.Children.Add(new Border
            {
                Background = BlendBrush(resourceBrush("InputBrush"), accent, result.IsFailure ? 0.2 : 0.12),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = $"{result.Model}{Environment.NewLine}{result.Detail}",
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = accent,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                }
            });
        }
    }

    private static string ProviderPresetBaseUrl(string preset)
    {
        return preset switch
        {
            "ollama" => "http://127.0.0.1:11434/v1",
            "local_8000" => "http://127.0.0.1:8000/v1",
            "lm_studio" => "http://127.0.0.1:1234/v1",
            _ => ""
        };
    }

    private static string ApiModeForProviderPreset(string preset)
    {
        return preset switch
        {
            "lm_studio" => ModelProviderApiModes.LmStudioNative,
            "ollama" => ModelProviderApiModes.OllamaNative,
            _ => ModelProviderApiModes.OpenAiCompatible
        };
    }

    private static string ProviderPresetTagForUrl(string baseUrl)
    {
        var normalized = ModelProviderHealthService.NormalizeBaseUrl(baseUrl);
        return normalized switch
        {
            "http://127.0.0.1:1234/v1" => "lm_studio",
            "http://localhost:1234/v1" => "lm_studio",
            "http://127.0.0.1:11434/v1" => "ollama",
            "http://localhost:11434/v1" => "ollama",
            "http://127.0.0.1:8000/v1" => "local_8000",
            "http://localhost:8000/v1" => "local_8000",
            _ => "manual"
        };
    }

    private static string ApiModeForBaseUrl(string baseUrl)
    {
        return ApiModeForProviderPreset(ProviderPresetTagForUrl(baseUrl));
    }

    private static string NativeLifecycleHint(string apiMode)
    {
        return ModelProviderApiModes.Normalize(apiMode) switch
        {
            ModelProviderApiModes.OllamaNative => "Uses Ollama native keep-alive lifecycle requests.",
            ModelProviderApiModes.LmStudioNative => "Uses LM Studio native model lifecycle endpoints.",
            _ => "Switch API mode to LM Studio native or Ollama native to use model lifecycle controls."
        };
    }

    private static string NativeOptionsHint(string apiMode)
    {
        return ModelProviderApiModes.Normalize(apiMode) switch
        {
            ModelProviderApiModes.OllamaNative => "Sent through Ollama native /api/chat options such as num_ctx, think, and keep_alive.",
            ModelProviderApiModes.LmStudioNative => "Sent through LM Studio native /api/v1 chat and lifecycle requests.",
            _ => "Switch API mode to LM Studio native or Ollama native to edit native-only options."
        };
    }



    private static void CommitSelectedComboBoxItem(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is string selected)
        {
            comboBox.Text = selected;
        }
    }

    private static IEnumerable<string> RoleModelKeys()
    {
        yield return "alpha";
        yield return "beta";
        yield return "gamma";
        yield return "delta";
        yield return "narrator";
    }

    private static void SetModelState(TextBlock target, string text, Brush brush, string? tooltip = null)
    {
        target.Text = text;
        target.Foreground = brush;
        target.ToolTip = tooltip ?? text;
    }

    private string DisplayParticipantModel(string model, string defaultModel)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        return string.IsNullOrWhiteSpace(defaultModel)
            ? "default"
            : $"default ({shortModelName(defaultModel)})";
    }

    private static string DisplayRoleModel(string model, string fallback)
    {
        return string.IsNullOrWhiteSpace(model) ? fallback : model;
    }

    private static string DisplayAutoConfigureStrategy(string strategy)
    {
        return strategy switch
        {
            "low_vram" => "Low VRAM",
            "max_variety" => "Max variety",
            "absurd_lab" => "Absurd Lab",
            "performance" => "Performance",
            "conservative" => "Conservative",
            _ => "Balanced"
        };
    }

    private static string FormatHardwareSummary(HardwareProbe hardware)
    {
        var gpuSummary = hardware.Gpus.Count == 0
            ? "GPU: none detected"
            : "GPU: " + string.Join("; ", hardware.Gpus.Select(gpu =>
            {
                var vram = gpu.VramTotalGb.HasValue ? $"{gpu.VramTotalGb.Value:0.#} GB VRAM" : "VRAM unknown";
                var used = gpu.VramUsedGb.HasValue ? $", {gpu.VramUsedGb.Value:0.#} GB used" : "";
                return $"{gpu.Name} ({gpu.Vendor}, {vram}{used})";
            }));
        var ram = hardware.SystemRamTotalGb.HasValue
            ? $"RAM: {hardware.SystemRamTotalGb.Value:0.#} GB total"
            : "RAM: unknown";
        return $"{gpuSummary}. {ram}.";
    }

    private static string FormatLoadPlanPreview(ModelLoadPlanPreview preview)
    {
        if (preview.Models.Count == 0)
        {
            return preview.Guidance;
        }

        var modelNames = string.Join(", ", preview.Models.Select(model =>
        {
            var footprint = model.EstimatedFootprintGb is double gb ? $"{gb:0.#} GB" : "unknown size";
            return $"{model.Name} ({footprint})";
        }));
        return $"Load plan: {preview.Models.Count} unique model(s), estimated {preview.EstimatedTotalFootprintGb:0.#} GB total footprint, comfortable per-model target {preview.ComfortablePerModelTargetGb:0.#} GB. Status: {preview.Status}. {preview.Guidance} Models: {modelNames}";
    }

    private static string FormatTokenCount(int value)
    {
        return value >= 1000
            ? $"{value / 1000d:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string TitleCaseStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status)
            ? "Status"
            : string.Concat(status[..1].ToUpperInvariant(), status[1..]);
    }

    private static string DisplayLockKey(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            var agentId when AgentRosterService.IsParticipantId(agentId) => AgentRosterService.DisplayName(agentId),
            "narrator" => "Narrator",
            _ => "Scenario"
        };
    }

    private static async Task RunBusyAsync(Control control, Func<Task> action)
    {
        control.IsEnabled = false;
        try
        {
            await action();
        }
        finally
        {
            control.IsEnabled = true;
        }
    }

    private static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        return ShellUiHelpers.BlendBrush(baseBrush, accentBrush, accentAmount);
    }
}
