using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreModelProviderConfig = AIArena.Core.Models.ModelProviderConfig;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class ProviderReachabilityCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly ProviderReachabilityService reachabilityService;
    private readonly DispatcherTimer providerHealthTimer;
    private readonly Popup providerHealthPopup;
    private readonly TextBlock providerHealthStatusText;
    private readonly TextBlock providerHealthBaseUrlText;
    private readonly TextBlock providerHealthModelCountText;
    private readonly TextBlock providerHealthDefaultModelText;
    private readonly TextBlock providerHealthLastCheckText;
    private readonly TextBlock providerHealthLastErrorText;
    private readonly Border providerHealthModelWarning;
    private readonly TextBlock providerHealthModelWarningText;
    private readonly Button providerHealthTestButton;
    private readonly Button providerHealthRefreshModelsButton;
    private readonly TextBox providerBaseUrlText;
    private readonly ComboBox providerModelText;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Func<ProviderSettingsCoordinator?> providerSettings;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action<CoreSessionSummary, ArenaViewSnapshot> applyProviderStatusSnapshot;
    private readonly Action<ArenaViewSnapshot> updateTopBarStatus;
    private readonly Action<string> setArenaRunStatus;
    private readonly Func<bool, Task> refreshAdvertisedModelsAsync;
    private readonly Action openModelProviderSettings;

    public ProviderReachabilityCoordinator(
        SessionStore sessionStore,
        ProviderReachabilityService reachabilityService,
        DispatcherTimer providerHealthTimer,
        Popup providerHealthPopup,
        TextBlock providerHealthStatusText,
        TextBlock providerHealthBaseUrlText,
        TextBlock providerHealthModelCountText,
        TextBlock providerHealthDefaultModelText,
        TextBlock providerHealthLastCheckText,
        TextBlock providerHealthLastErrorText,
        Border providerHealthModelWarning,
        TextBlock providerHealthModelWarningText,
        Button providerHealthTestButton,
        Button providerHealthRefreshModelsButton,
        TextBox providerBaseUrlText,
        ComboBox providerModelText,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Func<ProviderSettingsCoordinator?> providerSettings,
        Func<string, Brush> resourceBrush,
        Action<CoreSessionSummary, ArenaViewSnapshot> applyProviderStatusSnapshot,
        Action<ArenaViewSnapshot> updateTopBarStatus,
        Action<string> setArenaRunStatus,
        Func<bool, Task> refreshAdvertisedModelsAsync,
        Action openModelProviderSettings)
    {
        this.sessionStore = sessionStore;
        this.reachabilityService = reachabilityService;
        this.providerHealthTimer = providerHealthTimer;
        this.providerHealthPopup = providerHealthPopup;
        this.providerHealthStatusText = providerHealthStatusText;
        this.providerHealthBaseUrlText = providerHealthBaseUrlText;
        this.providerHealthModelCountText = providerHealthModelCountText;
        this.providerHealthDefaultModelText = providerHealthDefaultModelText;
        this.providerHealthLastCheckText = providerHealthLastCheckText;
        this.providerHealthLastErrorText = providerHealthLastErrorText;
        this.providerHealthModelWarning = providerHealthModelWarning;
        this.providerHealthModelWarningText = providerHealthModelWarningText;
        this.providerHealthTestButton = providerHealthTestButton;
        this.providerHealthRefreshModelsButton = providerHealthRefreshModelsButton;
        this.providerBaseUrlText = providerBaseUrlText;
        this.providerModelText = providerModelText;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.providerSettings = providerSettings;
        this.resourceBrush = resourceBrush;
        this.applyProviderStatusSnapshot = applyProviderStatusSnapshot;
        this.updateTopBarStatus = updateTopBarStatus;
        this.setArenaRunStatus = setArenaRunStatus;
        this.refreshAdvertisedModelsAsync = refreshAdvertisedModelsAsync;
        this.openModelProviderSettings = openModelProviderSettings;
    }

    public async Task<CoreModelProviderConfig?> LoadSharedConfigAsync()
    {
        return activeSession() is { } session
            ? await reachabilityService.LoadSharedConfigAsync(session.Id)
            : null;
    }

    public async Task RefreshAsync(bool force = false)
    {
        var session = activeSession();
        if (session is null || (isArenaBusy() && !force))
        {
            return;
        }

        var result = await reachabilityService.RefreshAsync(session.Id);
        if (result is null)
        {
            return;
        }

        providerSettings()?.RecordProviderReachabilityCheck(result.CheckedAt, result.ModelCount);
        providerHealthTimer.Interval = result.NextInterval;
        await UpdateActiveProviderStatusOnlyAsync(result.Status);
        UpdatePopup();
    }

    public async Task PersistAsync(
        bool online,
        string error,
        int latencyMs,
        string status,
        ArenaSnapshot? snapshot = null,
        string? sessionId = null)
    {
        var session = activeSession();
        if (session is null)
        {
            return;
        }

        sessionId ??= session.Id;
        var result = await reachabilityService.PersistAsync(sessionId, online, error, latencyMs, status, snapshot);
        if (result is null)
        {
            return;
        }

        providerHealthTimer.Interval = result.NextInterval;
        await UpdateActiveProviderStatusOnlyAsync(result.Status);
        UpdatePopup();
    }

    public void ShowPopup()
    {
        UpdatePopup();
        providerHealthPopup.IsOpen = true;
    }

    public void ClosePopup()
    {
        providerHealthPopup.IsOpen = false;
    }

    public async Task TestProviderAsync()
    {
        if (providerSettings() is not { } settings)
        {
            return;
        }

        await settings.TestProviderAsync(providerHealthTestButton);
        UpdatePopup();
    }

    public async Task RefreshModelsAsync()
    {
        await RunBusyAsync(providerHealthRefreshModelsButton, async () =>
        {
            providerHealthStatusText.Text = "Refreshing advertised models...";
            await refreshAdvertisedModelsAsync(true);
        });
        UpdatePopup();
    }

    public void OpenSettings()
    {
        ClosePopup();
        openModelProviderSettings();
    }

    public void UpdatePopup(ArenaViewSnapshot? snapshot = null)
    {
        snapshot ??= lastRenderedSnapshot();
        var modelState = ProviderHealthPopupState.From(
            snapshot,
            providerBaseUrlText.Text.Trim(),
            providerModelText.Text.Trim(),
            providerSettings()?.AdvertisedModels ?? [],
            providerSettings()?.LastProviderModelCount ?? -1,
            providerSettings()?.LastProviderHealthCheckedAt,
            providerSettings()?.LastModelListCheckedAt);

        providerHealthStatusText.Text = modelState.StatusText;
        providerHealthStatusText.Foreground = modelState.Online ? resourceBrush("PrimaryBorderBrush") : resourceBrush("DangerTextBrush");
        providerHealthBaseUrlText.Text = modelState.BaseUrl;
        providerHealthModelCountText.Text = modelState.ModelCountText;
        providerHealthDefaultModelText.Text = modelState.DefaultModelText;
        providerHealthLastCheckText.Text = modelState.LastCheckText;
        providerHealthLastErrorText.Text = modelState.ErrorText;
        providerHealthLastErrorText.Foreground = modelState.HasError ? resourceBrush("DangerTextBrush") : resourceBrush("MutedTextBrush");
        providerHealthModelWarning.Visibility = modelState.HasMissingModelWarning ? Visibility.Visible : Visibility.Collapsed;
        providerHealthModelWarningText.Text = modelState.ModelWarningText;
    }

    internal static string ProviderModelWarning(string model)
    {
        return $"Selected default model '{model}' is not in the advertised model list. Open settings to reselect or type it manually.";
    }

    private async Task UpdateActiveProviderStatusOnlyAsync(string status)
    {
        var session = activeSession();
        if (session is null)
        {
            return;
        }

        var latest = (await sessionStore.ListSessionsAsync()).FirstOrDefault(candidate => candidate.Id == session.Id);
        if (latest is null)
        {
            return;
        }

        var coreSnapshot = await sessionStore.LoadSnapshotAsync(latest.Id);
        if (coreSnapshot is null)
        {
            return;
        }

        var snapshot = SnapshotViewMapper.FromCore(latest, coreSnapshot);
        applyProviderStatusSnapshot(latest, snapshot);
        updateTopBarStatus(snapshot);
        if (!isArenaBusy())
        {
            setArenaRunStatus(status);
        }
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
}

internal sealed record ProviderHealthPopupState(
    bool Online,
    string StatusText,
    string BaseUrl,
    string ModelCountText,
    string DefaultModelText,
    string LastCheckText,
    string ErrorText,
    bool HasError,
    bool HasMissingModelWarning,
    string ModelWarningText)
{
    public static ProviderHealthPopupState From(
        ArenaViewSnapshot? snapshot,
        string providerBaseUrl,
        string providerModel,
        IReadOnlyList<string> advertisedModels,
        int lastProviderModelCount,
        DateTimeOffset? lastProviderHealthCheckedAt,
        DateTimeOffset? lastModelListCheckedAt)
    {
        var online = snapshot?.ProviderOnline == true;
        var baseUrl = snapshot?.ProviderBaseUrl ?? providerBaseUrl;
        var model = snapshot?.ProviderModel ?? providerModel;
        var error = snapshot?.ProviderLastError ?? "";
        var modelCount = advertisedModels.Count > 0
            ? advertisedModels.Count
            : lastProviderModelCount;
        var checkedAt = lastProviderHealthCheckedAt ?? lastModelListCheckedAt;
        var hasError = !string.IsNullOrWhiteSpace(error);
        var missingModel = advertisedModels.Count > 0
            && !string.IsNullOrWhiteSpace(model)
            && model != "-"
            && !advertisedModels.Contains(model, StringComparer.OrdinalIgnoreCase);

        return new ProviderHealthPopupState(
            online,
            online ? "ONLINE" : "OFFLINE",
            string.IsNullOrWhiteSpace(baseUrl) ? "-" : baseUrl,
            modelCount >= 0 ? modelCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown",
            string.IsNullOrWhiteSpace(model) || model == "-" ? "not selected" : model,
            checkedAt is null
                ? "waiting"
                : checkedAt.Value.ToLocalTime().ToString("h:mm:ss tt", System.Globalization.CultureInfo.CurrentCulture),
            hasError ? $"Last error: {error}" : "No provider error recorded.",
            hasError,
            missingModel,
            missingModel ? ProviderReachabilityCoordinator.ProviderModelWarning(model) : "");
    }
}
