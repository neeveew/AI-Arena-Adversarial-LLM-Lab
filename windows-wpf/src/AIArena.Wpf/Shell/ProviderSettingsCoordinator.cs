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
    private readonly ModelPreloadService modelPreloadService;
    private readonly ProviderAutoConfigureService providerAutoConfigureService;
    private readonly SemaphoreSlim arenaOperationLock;
    private readonly ComboBox providerPresetPicker;
    private readonly TextBlock providerPresetStatusText;
    private readonly TextBox providerBaseUrlText;
    private readonly ComboBox providerModelText;
    private readonly TextBlock defaultModelStatusText;
    private readonly IReadOnlyDictionary<string, ComboBox> roleModelTextByKey;
    private readonly IReadOnlyDictionary<string, TextBlock> roleModelStatusByKey;
    private readonly TextBlock roleModelSummaryText;
    private readonly ComboBox autoConfigureStrategyPicker;
    private readonly Button autoConfigureButton;
    private readonly Button applyAutoConfigureButton;
    private readonly TextBlock autoConfigureStatusText;
    private readonly TextBlock autoConfigureHardwareText;
    private readonly TextBlock autoConfigureProviderText;
    private readonly Panel autoConfigureRecommendationItems;
    private readonly Button preloadSelectedModelsButton;
    private readonly TextBlock loadPlanPreviewText;
    private readonly TextBlock preloadModelsStatusText;
    private readonly Panel preloadModelsItems;
    private readonly TextBox providerTimeoutText;
    private readonly TextBlock providerTestStatus;
    private readonly TextBlock providerModelsStatus;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> appSettingsVisible;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> shortModelName;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<Task<CoreModelProviderConfig?>> loadSharedProviderConfigAsync;
    private readonly Func<bool, string, int, string, Task> persistProviderReachabilityAsync;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Func<bool, Task> refreshProviderReachabilityAsync;
    private readonly Action updateProviderHealthPopup;

    private readonly Dictionary<string, string> roleModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelPreloadResult> lastPreloadResults = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> advertisedModels = [];
    private bool isRefreshingModels;
    private bool isUpdatingRoleModelEditor;
    private bool isPersistingModelRouting;
    private int lastProviderModelCount = -1;
    private DateTimeOffset? lastProviderHealthCheckedAt;
    private DateTimeOffset? lastModelListCheckedAt;
    private ProviderAutoConfigurePlan? lastAutoConfigurePlan;

    public ProviderSettingsCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ModelProviderHealthService providerHealth,
        ModelPreloadService modelPreloadService,
        ProviderAutoConfigureService providerAutoConfigureService,
        SemaphoreSlim arenaOperationLock,
        ComboBox providerPresetPicker,
        TextBlock providerPresetStatusText,
        TextBox providerBaseUrlText,
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
        TextBlock loadPlanPreviewText,
        TextBlock preloadModelsStatusText,
        Panel preloadModelsItems,
        TextBox providerTimeoutText,
        TextBlock providerTestStatus,
        TextBlock providerModelsStatus,
        Func<CoreSessionSummary?> activeSession,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Func<ThemePalette> theme,
        Func<bool> isRenderingSnapshot,
        Func<bool> appSettingsVisible,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> shortModelName,
        Func<string, string> displayStatusValue,
        Func<Task<CoreModelProviderConfig?>> loadSharedProviderConfigAsync,
        Func<bool, string, int, string, Task> persistProviderReachabilityAsync,
        Func<string?, Task> loadSessionsAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Func<bool, Task> refreshProviderReachabilityAsync,
        Action updateProviderHealthPopup)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.providerHealth = providerHealth;
        this.modelPreloadService = modelPreloadService;
        this.providerAutoConfigureService = providerAutoConfigureService;
        this.arenaOperationLock = arenaOperationLock;
        this.providerPresetPicker = providerPresetPicker;
        this.providerPresetStatusText = providerPresetStatusText;
        this.providerBaseUrlText = providerBaseUrlText;
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
        this.loadPlanPreviewText = loadPlanPreviewText;
        this.preloadModelsStatusText = preloadModelsStatusText;
        this.preloadModelsItems = preloadModelsItems;
        this.providerTimeoutText = providerTimeoutText;
        this.providerTestStatus = providerTestStatus;
        this.providerModelsStatus = providerModelsStatus;
        this.activeSession = activeSession;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.theme = theme;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.appSettingsVisible = appSettingsVisible;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.shortModelName = shortModelName;
        this.displayStatusValue = displayStatusValue;
        this.loadSharedProviderConfigAsync = loadSharedProviderConfigAsync;
        this.persistProviderReachabilityAsync = persistProviderReachabilityAsync;
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
        providerBaseUrlText.Text = snapshot.ProviderBaseUrl;
        SelectComboTag(providerPresetPicker, ProviderPresetTagForUrl(snapshot.ProviderBaseUrl));
        providerModelText.Text = snapshot.ProviderModel == "-" ? "" : snapshot.ProviderModel;
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

    public async Task ApplyProviderPresetAsync()
    {
        var preset = SelectedComboTag(providerPresetPicker, "lm_studio");
        var url = ProviderPresetBaseUrl(preset);
        if (string.IsNullOrWhiteSpace(url))
        {
            providerPresetStatusText.Foreground = resourceBrush("MutedTextBrush");
            providerPresetStatusText.Text = "Manual provider selected. Type a base URL, then press Enter or Apply Settings.";
            providerBaseUrlText.Focus();
            return;
        }

        providerBaseUrlText.Text = url;
        providerPresetStatusText.Foreground = resourceBrush("AlphaAccentBrush");
        providerPresetStatusText.Text = $"Provider preset applied: {url}";
        await PersistModelRoutingAsync("Provider preset applied.", refreshModels: true);
    }

    public async Task TestProviderAsync(Control busyControl)
    {
        if (activeSession() is null)
        {
            providerTestStatus.Text = "No active session.";
            return;
        }

        await RunBusyAsync(busyControl, async () =>
        {
            providerTestStatus.Text = "Testing provider...";
            var config = await loadSharedProviderConfigAsync();
            if (config is null)
            {
                providerTestStatus.Text = "No provider config found.";
                return;
            }

            var result = await providerHealth.TestCompletionAsync(config);
            lastProviderHealthCheckedAt = result.CheckedAt;
            if (result.Ok)
            {
                await persistProviderReachabilityAsync(true, "", result.LatencyMs, "Provider online.");
            }
            else
            {
                var health = await providerHealth.CheckAsync(ProviderReachabilityService.HealthProbeConfig(config));
                lastProviderHealthCheckedAt = health.CheckedAt;
                lastProviderModelCount = health.ModelCount;
                await persistProviderReachabilityAsync(
                    health.Ok,
                    health.Ok ? result.Error : health.Error,
                    result.LatencyMs,
                    health.Ok ? "Provider online; completion test failed." : "Provider offline.");
            }

            providerTestStatus.Text = result.Ok
                ? $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Text}"
                : $"Provider failed: {result.Error}";
            updateProviderHealthPopup();
        });
    }

    public async Task SaveAndTestProviderQuickSetupAsync(string baseUrl, string model, TextBlock statusText)
    {
        if (activeSession() is null)
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
        providerModelText.Text = model;
        await PersistModelRoutingAsync("Provider quick setup saved.", refreshModels: true);

        var config = await loadSharedProviderConfigAsync();
        if (config is null)
        {
            statusText.Foreground = resourceBrush("DangerTextBrush");
            statusText.Text = "Provider setup could not be loaded after save.";
            return;
        }

        statusText.Text = "Testing provider completion...";
        var result = await providerHealth.TestCompletionAsync(config);
        lastProviderHealthCheckedAt = result.CheckedAt;
        if (result.Ok)
        {
            await persistProviderReachabilityAsync(true, "", result.LatencyMs, "Provider online.");
            statusText.Foreground = resourceBrush("AlphaAccentBrush");
            statusText.Text = $"Provider online: {result.Model}, {result.LatencyMs} ms.";
            providerTestStatus.Text = $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Text}";
            await refreshActiveSessionAsync("Provider quick setup complete.");
            return;
        }

        var health = await providerHealth.CheckAsync(ProviderReachabilityService.HealthProbeConfig(config));
        lastProviderHealthCheckedAt = health.CheckedAt;
        lastProviderModelCount = health.ModelCount;
        await persistProviderReachabilityAsync(
            health.Ok,
            health.Ok ? result.Error : health.Error,
            result.LatencyMs,
            health.Ok ? "Provider online; completion test failed." : "Provider offline.");

        statusText.Foreground = health.Ok ? resourceBrush("BetaAccentBrush") : resourceBrush("DangerTextBrush");
        statusText.Text = health.Ok
            ? $"Provider responded, but completion failed: {result.Error}"
            : $"Provider offline: {health.Error}";
        providerTestStatus.Text = result.Ok
            ? $"Provider ok: {result.Model} at {result.BaseUrl}; {result.LatencyMs} ms; reply: {result.Text}"
            : $"Provider failed: {result.Error}";
        await refreshActiveSessionAsync(health.Ok ? "Provider reachable; completion test failed." : "Provider offline.");
    }

    public async Task PreloadSelectedModelsAsync()
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

            var results = await modelPreloadService.PreloadAsync(providerBaseUrlText.Text.Trim(), models);
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

            await RefreshAdvertisedModelsAsync(force: true);
            UpdateModelStateLabels();
        });
    }

    public async Task AutoConfigureAsync()
    {
        await RunBusyAsync(autoConfigureButton, async () =>
        {
            applyAutoConfigureButton.IsEnabled = false;
            autoConfigureRecommendationItems.Children.Clear();
            autoConfigureStatusText.Foreground = resourceBrush("MutedTextBrush");
            autoConfigureStatusText.Text = "Detecting GPU setup, provider capability, and advertised models...";
            autoConfigureHardwareText.Text = "";
            autoConfigureProviderText.Text = "";

            var strategy = SelectedComboTag(autoConfigureStrategyPicker, "auto");
            var plan = await providerAutoConfigureService.DetectAsync(providerBaseUrlText.Text.Trim(), strategy);
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

    public async Task ApplyAutoConfigureAsync()
    {
        if (lastAutoConfigurePlan is null)
        {
            autoConfigureStatusText.Text = "Run Auto Configure first.";
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
                await sessionStore.EnsureDefaultSessionAsync();
                await loadSessionsAsync("default");
            }

            isUpdatingRoleModelEditor = true;
            try
            {
                providerBaseUrlText.Text = plan.ProviderBaseUrl;
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
            await PersistModelRoutingAsync("Auto configuration applied.", refreshModels: true);
            preloadModelsStatusText.Foreground = resourceBrush("MutedTextBrush");
            preloadModelsStatusText.Text = plan.PreloadGuidance;
            autoConfigureStatusText.Foreground = resourceBrush("AlphaAccentBrush");
            autoConfigureStatusText.Text = "Applied recommended model routing.";
        });
    }

    public async Task ProviderBaseUrlCommittedAsync()
    {
        await PersistModelRoutingAsync("Provider base URL saved.", refreshModels: true);
    }

    public async Task ProviderModelSelectionChangedAsync()
    {
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor)
        {
            return;
        }

        CommitSelectedComboBoxItem(providerModelText);
        await PersistModelRoutingAsync("Default model saved.");
    }

    public async Task ProviderModelCommittedAsync()
    {
        await PersistModelRoutingAsync("Default model saved.");
    }

    public async Task ParticipantModelSelectionChangedAsync(ComboBox comboBox)
    {
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor)
        {
            return;
        }

        CommitSelectedComboBoxItem(comboBox);
        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.");
    }

    public async Task ParticipantModelCommittedAsync(ComboBox comboBox)
    {
        SaveRoleModelDraft(comboBox);
        await PersistModelRoutingAsync($"{DisplayLockKey(comboBox.Tag?.ToString() ?? "")} model saved.");
    }

    public async Task PersistModelRoutingAsync(string successStatus, bool refreshModels = false)
    {
        var session = activeSession();
        if (isRenderingSnapshot() || isUpdatingRoleModelEditor || isPersistingModelRouting || session is null)
        {
            return;
        }

        var baseUrl = providerBaseUrlText.Text.Trim();
        var defaultModel = providerModelText.Text.Trim();
        SaveRoleModelDrafts();
        UpdateRoleModelSummary();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            providerTestStatus.Text = "Provider base URL is required.";
            return;
        }

        isPersistingModelRouting = true;
        try
        {
            await arenaOperationLock.WaitAsync();
            try
            {
                var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
                if (snapshot is null)
                {
                    providerTestStatus.Text = $"No snapshot found for session {session.Id}.";
                    return;
                }

                var existingShared = snapshot.Configs.TryGetValue("shared", out var shared)
                    ? shared
                    : new CoreModelProviderConfig();
                var updatedShared = new CoreModelProviderConfig
                {
                    BaseUrl = ModelProviderHealthService.NormalizeBaseUrl(baseUrl),
                    Model = defaultModel,
                    Timeout = existingShared.Timeout,
                    Temperature = existingShared.Temperature,
                    MaxOutputTokens = existingShared.MaxOutputTokens,
                    LastError = existingShared.LastError,
                    LastLatencyMs = existingShared.LastLatencyMs,
                    LastTestOk = existingShared.LastTestOk,
                    Extra = existingShared.Extra
                };

                snapshot.Configs["shared"] = updatedShared;
                SaveRoleModelConfig(snapshot.Configs, "alpha", RoleModel("alpha"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "beta", RoleModel("beta"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "gamma", RoleModel("gamma"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "delta", RoleModel("delta"), updatedShared);
                SaveRoleModelConfig(snapshot.Configs, "narrator", RoleModel("narrator"), updatedShared);

                await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
                await eventLogStore.AppendAsync(session.Id, "native_model_routing_applied", new
                {
                    updatedShared.BaseUrl,
                    updatedShared.Model,
                    AlphaModel = RoleModel("alpha"),
                    BetaModel = RoleModel("beta"),
                    GammaModel = RoleModel("gamma"),
                    DeltaModel = RoleModel("delta"),
                    NarratorModel = RoleModel("narrator")
                });
            }
            finally
            {
                arenaOperationLock.Release();
            }

            await refreshActiveSessionAsync(successStatus);
            providerTestStatus.Text = successStatus;
            if (refreshModels)
            {
                await RefreshAdvertisedModelsAsync(force: true);
            }

            _ = refreshProviderReachabilityAsync(true);
        }
        finally
        {
            isPersistingModelRouting = false;
        }
    }

    public async Task RefreshAdvertisedModelsAsync(bool force = false)
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
                Model = providerModelText.Text.Trim(),
                Timeout = int.TryParse(providerTimeoutText.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timeout)
                    ? Math.Clamp(timeout, 1, 3600)
                    : 5,
                Temperature = 0,
                MaxOutputTokens = 16
            };
            var result = await providerHealth.ListModelsAsync(config);
            lastModelListCheckedAt = result.CheckedAt;
            if (result.Ok)
            {
                advertisedModels = result.Models.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray();
                lastProviderModelCount = advertisedModels.Count;
                providerModelsStatus.Text = $"{advertisedModels.Count} advertised models found. Refreshes every 5s while settings are open.";
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
                providerModelsStatus.Text = $"Model list unavailable: {result.Error}";
                UpdateModelStateLabels();
            }

            updateProviderHealthPopup();
        }
        finally
        {
            isRefreshingModels = false;
        }
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

    public static void SaveRoleModelConfig(IDictionary<string, CoreModelProviderConfig> configs, string key, string model, CoreModelProviderConfig shared)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            configs.Remove(key);
            return;
        }

        configs[key] = new CoreModelProviderConfig
        {
            BaseUrl = shared.BaseUrl,
            Model = model,
            Timeout = shared.Timeout,
            Temperature = shared.Temperature,
            MaxOutputTokens = shared.MaxOutputTokens,
            LastError = configs.TryGetValue(key, out var existing) ? existing.LastError : "",
            LastLatencyMs = configs.TryGetValue(key, out existing) ? existing.LastLatencyMs : 0,
            LastTestOk = configs.TryGetValue(key, out existing) && existing.LastTestOk
        };
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
        autoConfigureProviderText.Text = $"Provider: {plan.ProviderBaseUrl} - {(plan.LmStudioNativeApi ? "LM Studio enhanced mode" : "OpenAI-compatible mode")}. {plan.PreloadGuidance}";

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
            ? $"{plan.Models.Count} advertised chat models found by Auto Configure."
            : "Auto Configure could not reach an OpenAI-compatible provider.";
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

        SetModelState(defaultModelStatusText, ModelStateLabel(model), ModelStateBrush(model));
    }

    private void UpdateRoleModelStateLabel(string key, TextBlock target)
    {
        var model = RoleModel(key);
        if (string.IsNullOrWhiteSpace(model))
        {
            SetModelState(target, "inherits default", resourceBrush("MutedTextBrush"));
            return;
        }

        SetModelState(target, ModelStateLabel(model), ModelStateBrush(model));
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

        return advertisedModels.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? "selected"
            : "unavailable";
    }

    private Brush ModelStateBrush(string model)
    {
        var label = ModelStateLabel(model);
        return label switch
        {
            "failed preload" or "unavailable" => resourceBrush("DangerTextBrush"),
            "selected" => resourceBrush("AlphaAccentBrush"),
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
                "loaded" or "ready" => resourceBrush("PrimaryBorderBrush"),
                "skipped" => resourceBrush("MutedTextBrush"),
                "unsupported" => resourceBrush("BetaAccentBrush"),
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

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private static string SelectedComboTag(ComboBox comboBox, string fallback)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
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

    private static void SetModelState(TextBlock target, string text, Brush brush)
    {
        target.Text = text;
        target.Foreground = brush;
        target.ToolTip = text;
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
        var baseColor = BrushColor(baseBrush, Colors.Transparent);
        var accentColor = BrushColor(accentBrush, baseColor);
        var amount = Math.Clamp(accentAmount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            BlendChannel(baseColor.R, accentColor.R, amount),
            BlendChannel(baseColor.G, accentColor.G, amount),
            BlendChannel(baseColor.B, accentColor.B, amount)));
    }

    private static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static byte BlendChannel(byte baseline, byte accent, double amount)
    {
        return (byte)Math.Round(baseline + ((accent - baseline) * amount));
    }
}
