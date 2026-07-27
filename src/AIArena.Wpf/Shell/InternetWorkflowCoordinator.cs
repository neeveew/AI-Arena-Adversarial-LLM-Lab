using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class InternetWorkflowCoordinator : IDisposable
{
    private static readonly HttpClient BackendHealthHttpClient = new();

    private readonly CheckBox useInternetCheckBox;
    private readonly TextBlock internetHintText;
    private readonly TextBlock backendStatusText;
    private readonly Button testInternetButton;
    private readonly TextBlock diagnosticResultText;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<CancellationToken, Task<SearxngSupervisorStatus>> ensureBackendAsync;
    private readonly Action stopBackend;
    private readonly Func<string, bool, CancellationToken, Task> persistInternetSettingAsync;
    private readonly LatestInternetDiagnosticsRunner diagnosticsRunner;

    private int backendStatusVersion;
    private int diagnosticUiVersion;
    private CancellationTokenSource? backendStatusCancellation;
    private CancellationTokenSource? settingPersistenceCancellation;
    private int settingPersistenceVersion;
    private bool applyingSnapshot;
    private bool lastPersistedInternetEnabled;
    private string appliedSessionId = "";
    private string settingPersistenceSessionId = "";
    private bool settingPersistenceEnabled;
    private bool settingPersistencePriorEnabled;
    private bool controlsInitialized;

    public InternetWorkflowCoordinator(
        CheckBox useInternetCheckBox,
        TextBlock internetHintText,
        TextBlock backendStatusText,
        Button testInternetButton,
        TextBlock diagnosticResultText,
        Func<string, Brush> resourceBrush,
        Func<CancellationToken, Task<SearxngSupervisorStatus>>? ensureBackendAsync = null,
        Func<CancellationToken, Task<InternetDiagnosticsReport>>? runDiagnosticsAsync = null,
        Action? stopBackend = null,
        Func<string, bool, CancellationToken, Task>? persistInternetSettingAsync = null)
    {
        this.useInternetCheckBox = useInternetCheckBox;
        this.internetHintText = internetHintText;
        this.backendStatusText = backendStatusText;
        this.testInternetButton = testInternetButton;
        this.diagnosticResultText = diagnosticResultText;
        this.resourceBrush = resourceBrush;
        this.ensureBackendAsync = ensureBackendAsync
            ?? (cancellationToken => (Application.Current as App)?.EnsureInternetSearchAsync(cancellationToken)
                ?? SearxngSupervisorService.ProbeAsync(BackendHealthHttpClient, cancellationToken: cancellationToken));
        this.stopBackend = stopBackend
            ?? (() => (Application.Current as App)?.StopInternetSearch());
        this.persistInternetSettingAsync = persistInternetSettingAsync
            ?? ((_, _, _) => Task.CompletedTask);
        lastPersistedInternetEnabled = useInternetCheckBox.IsChecked == true;
        diagnosticsRunner = new LatestInternetDiagnosticsRunner(
            runDiagnosticsAsync
            ?? (cancellationToken => (Application.Current as App)?.TestInternetAsync(cancellationToken)
                ?? Task.FromResult(UnavailableDiagnosticsReport())));
    }

    public void InitializeControls()
    {
        useInternetCheckBox.Checked += (_, _) => InternetSettingChanged();
        useInternetCheckBox.Unchecked += (_, _) => InternetSettingChanged();
        testInternetButton.Click += (_, _) => _ = TestInternetAsync();
        controlsInitialized = true;
        UpdateSettingsHint();
        _ = RefreshBackendHealthAsync();
    }

    public object ControlState => new
    {
        Enabled = useInternetCheckBox.IsChecked == true,
        SessionId = appliedSessionId,
        Backend = backendStatusText.Text,
        Diagnostic = diagnosticResultText.Text
    };

    public bool IsEnabled => useInternetCheckBox.IsChecked == true;

    public async Task ControlSetEnabledAsync(bool enabled)
    {
        if (useInternetCheckBox.IsChecked != enabled)
        {
            applyingSnapshot = true;
            try
            {
                useInternetCheckBox.IsChecked = enabled;
            }
            finally
            {
                applyingSnapshot = false;
            }

            if (!string.IsNullOrWhiteSpace(appliedSessionId))
            {
                await PersistInternetSettingLatestAsync(appliedSessionId, enabled);
            }
        }

        await RefreshBackendHealthAsync();
    }

    public void ApplySnapshot(ArenaViewSnapshot snapshot)
    {
        var settingChanged = false;
        var persistenceBelongsToSnapshot = settingPersistenceCancellation is not null
            && settingPersistenceSessionId.Equals(snapshot.SessionId, StringComparison.OrdinalIgnoreCase);
        appliedSessionId = snapshot.SessionId;
        if (!persistenceBelongsToSnapshot)
        {
            lastPersistedInternetEnabled = snapshot.InternetEnabled;
        }

        var renderedInternetEnabled = persistenceBelongsToSnapshot
            ? settingPersistenceEnabled
            : snapshot.InternetEnabled;
        settingChanged = useInternetCheckBox.IsChecked != renderedInternetEnabled;
        applyingSnapshot = true;
        try
        {
            useInternetCheckBox.IsChecked = renderedInternetEnabled;
        }
        finally
        {
            applyingSnapshot = false;
        }

        if (controlsInitialized && settingChanged)
        {
            // Checked/Unchecked synchronously applied the setting and health state.
            return;
        }

        UpdateSettingsHint();
        _ = RefreshBackendHealthAsync();
    }

    public void UpdateBusyState(bool busy, bool autoChatRunning)
    {
        useInternetCheckBox.IsEnabled = !busy;
        // Diagnostics do not read or mutate arena state, so they remain available
        // while a session is busy and when there is no active session at all.
        testInternetButton.IsEnabled = true;
    }

    public void UpdateSettingsHint()
    {
        if (useInternetCheckBox.IsChecked != true)
        {
            // Internet off is a deliberate choice, and the more conservative one:
            // nothing leaves the machine. The danger tone read as a fault report
            // for a setting the reader had just chosen on purpose.
            internetHintText.Text = "Internet is off. Agents and narrator cannot search the web or fetch pages.";
            internetHintText.Foreground = resourceBrush("MutedTextBrush");
            return;
        }

        internetHintText.Text = "Internet is on. Agents and narrator can search the web and fetch pages when external facts would improve a turn.";
        internetHintText.Foreground = resourceBrush("MutedTextBrush");
    }

    public async Task RefreshBackendHealthAsync()
    {
        var version = ++backendStatusVersion;
        var currentCancellation = new CancellationTokenSource();
        var previousCancellation = backendStatusCancellation;
        backendStatusCancellation = currentCancellation;
        CancelBestEffort(previousCancellation);
        var cancellationToken = currentCancellation.Token;
        var enabled = useInternetCheckBox.IsChecked == true;
        try
        {
            if (!enabled)
            {
                ApplyBackendHealthStatus(null, enabled);
                return;
            }

            backendStatusText.Text = "Local search: checking...";
            backendStatusText.Foreground = resourceBrush("MutedTextBrush");
            var status = await ensureBackendAsync(cancellationToken);
            if (version == backendStatusVersion)
            {
                ApplyBackendHealthStatus(status, enabled);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer health refresh superseded this one.
        }
        catch
        {
            if (version == backendStatusVersion)
            {
                ApplyBackendHealthStatus(
                    new SearxngSupervisorStatus(false, false, false, SearxngSupervisorService.ResolveBaseUri(null), "Local search backend check failed."),
                    enabled);
            }
        }
        finally
        {
            if (ReferenceEquals(backendStatusCancellation, currentCancellation))
            {
                backendStatusCancellation = null;
            }

            currentCancellation.Dispose();
        }
    }

    public async Task TestInternetAsync()
    {
        var version = ++diagnosticUiVersion;
        testInternetButton.Content = "Testing... (click to restart)";
        diagnosticResultText.Text = "Testing local search and a safe public HTTPS page...";
        diagnosticResultText.Foreground = resourceBrush("MutedTextBrush");
        try
        {
            var report = await diagnosticsRunner.RunAsync();
            if (report is null || version != diagnosticUiVersion)
            {
                return;
            }

            diagnosticResultText.Text = DiagnosticResultText(report);
            diagnosticResultText.Foreground = resourceBrush(DiagnosticStatusBrushKey(report));
            diagnosticResultText.ToolTip = diagnosticResultText.Text;
            ApplyBackendHealthStatus(report.Backend, useInternetCheckBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            if (version == diagnosticUiVersion)
            {
                diagnosticResultText.Text = $"Internet test failed before completion: {ex.Message}{Environment.NewLine}Action: retry; if it persists, restart AI Arena and check firewall/DNS access.";
                diagnosticResultText.Foreground = resourceBrush("DangerTextBrush");
                diagnosticResultText.ToolTip = diagnosticResultText.Text;
            }
        }
        finally
        {
            if (version == diagnosticUiVersion)
            {
                if (useInternetCheckBox.IsChecked != true)
                {
                    // Diagnostics may start the backend while Internet is off. Keep
                    // that temporary process only if the user enabled Internet while
                    // this latest diagnostic was running.
                    stopBackend();
                }

                testInternetButton.Content = "Test Internet";
            }
        }
    }

    internal static string BackendStatusText(SearxngSupervisorStatus? status, bool internetEnabled)
    {
        if (!internetEnabled)
        {
            return "Local search: inactive";
        }

        if (status is null)
        {
            return "Local search: checking...";
        }

        var endpoint = $"{status.BaseUri.Host}:{status.BaseUri.Port}";
        if (status.Started || status.AlreadyRunning)
        {
            return $"Local search: ready ({endpoint})";
        }

        return !status.PayloadFound && SearxngSupervisorService.IsBundledDefaultUri(status.BaseUri)
            ? "Local search: unavailable (not installed)"
            : $"Local search: unavailable ({endpoint})";
    }

    internal static string BackendStatusBrushKey(SearxngSupervisorStatus? status, bool internetEnabled)
    {
        if (!internetEnabled || status is null)
        {
            return "MutedTextBrush";
        }

        return status.Started || status.AlreadyRunning
            ? "PrimaryBorderBrush"
            : "DangerTextBrush";
    }

    internal static string DiagnosticResultText(InternetDiagnosticsReport report)
    {
        var hasEngineWarnings = report.Search.UnresponsiveEngineCount > 0;
        var heading = report.Ok
            ? hasEngineWarnings ? "Internet test passed with engine warnings." : "Internet test passed."
            : "Internet test needs attention.";

        var searchLine = report.Search.Ok
            ? $"Search: {report.Search.ResultCount} result(s) in {FormatLatency(report.Search.Latency)}; {FormatEngineCounts(report.Search)}."
            : $"Search: failed in {FormatLatency(report.Search.Latency)} — {CleanError(report.Search.Error)} Action: {SearchFailureAction(report.Backend)}";
        var fetchLine = report.Fetch.Ok
            ? $"Direct fetch: passed in {FormatLatency(report.Fetch.Latency)} ({report.Fetch.FinalUri?.Host ?? "public HTTPS page"})."
            : $"Direct fetch: failed in {FormatLatency(report.Fetch.Latency)} — {CleanError(report.Fetch.Error)} Action: allow AI Arena through the firewall and verify HTTPS/DNS access.";
        var usesBundledPayload = SearxngSupervisorService.IsBundledDefaultUri(report.Backend.BaseUri);
        var payloadVersion = !usesBundledPayload
            ? "external endpoint"
            : string.IsNullOrWhiteSpace(report.Backend.PayloadVersion)
                ? report.Backend.PayloadFound ? "version unavailable" : "not installed"
                : $"revision {report.Backend.PayloadVersion}";
        var payloadPath = !usesBundledPayload
            ? "not used (external endpoint)"
            : string.IsNullOrWhiteSpace(report.Backend.PayloadPath)
                ? "not resolved"
                : report.Backend.PayloadPath;

        return string.Join(
            Environment.NewLine,
            heading,
            searchLine,
            fetchLine,
            $"Search payload: {payloadVersion}",
            $"Payload path: {payloadPath}");
    }

    internal static string DiagnosticStatusBrushKey(InternetDiagnosticsReport report)
    {
        return report.Ok ? "PrimaryBorderBrush" : "DangerTextBrush";
    }

    private void InternetSettingChanged()
    {
        var enabled = useInternetCheckBox.IsChecked == true;
        if (!enabled)
        {
            stopBackend();
        }

        UpdateSettingsHint();
        if (!applyingSnapshot)
        {
            var sessionId = appliedSessionId;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _ = PersistInternetSettingLatestAsync(sessionId, enabled);
            }
        }

        _ = RefreshBackendHealthAsync();
    }

    private async Task PersistInternetSettingLatestAsync(string sessionId, bool enabled)
    {
        var version = ++settingPersistenceVersion;
        var currentCancellation = new CancellationTokenSource();
        var previousCancellation = settingPersistenceCancellation;
        settingPersistenceCancellation = currentCancellation;
        settingPersistenceSessionId = sessionId;
        settingPersistenceEnabled = enabled;
        settingPersistencePriorEnabled = lastPersistedInternetEnabled;
        CancelBestEffort(previousCancellation);
        try
        {
            await persistInternetSettingAsync(sessionId, enabled, currentCancellation.Token);
            if (version == settingPersistenceVersion
                && appliedSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                lastPersistedInternetEnabled = enabled;
            }
        }
        catch (OperationCanceledException)
        {
            // A newer toggle or application shutdown superseded this write.
        }
        catch (Exception ex)
        {
            if (version != settingPersistenceVersion
                || !appliedSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            applyingSnapshot = true;
            try
            {
                useInternetCheckBox.IsChecked = settingPersistencePriorEnabled;
            }
            finally
            {
                applyingSnapshot = false;
            }

            internetHintText.Text = $"Internet setting could not be saved: {ex.Message}";
            internetHintText.Foreground = resourceBrush("DangerTextBrush");
        }
        finally
        {
            if (ReferenceEquals(settingPersistenceCancellation, currentCancellation))
            {
                settingPersistenceCancellation = null;
                settingPersistenceSessionId = "";
                settingPersistenceEnabled = false;
                settingPersistencePriorEnabled = false;
            }

            currentCancellation.Dispose();
        }
    }

    internal static async Task<bool> PersistSessionSettingAsync(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        string sessionId,
        bool enabled,
        CancellationToken cancellationToken = default,
        Func<string, bool, CancellationToken, Task>? appendEventAsync = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(sessionId, cancellationToken);
            if (snapshot is null)
            {
                return false;
            }

            if (snapshot.Engine.Internet.UseInternet == enabled)
            {
                return true;
            }

            snapshot.Engine.Internet.UseInternet = enabled;
            try
            {
                await sessionStore.SaveSnapshotAsync(snapshot, sessionId, cancellationToken);
                try
                {
                    if (appendEventAsync is not null)
                    {
                        await appendEventAsync(sessionId, enabled, cancellationToken);
                    }
                    else
                    {
                        await eventLogStore.AppendAsync(sessionId, "internet_setting_changed", new { Enabled = enabled }, cancellationToken);
                    }
                }
                catch
                {
                    // The snapshot is the source of truth. Once its atomic save has
                    // committed, an auxiliary audit-log failure must not make the UI
                    // revert to a value that no longer matches durable session state.
                }

                return true;
            }
            catch (SnapshotConcurrencyException) when (attempt == 0)
            {
                // Reload once so a concurrent unrelated snapshot update is kept.
            }
        }

        return false;
    }

    private void ApplyBackendHealthStatus(SearxngSupervisorStatus? status, bool internetEnabled)
    {
        backendStatusText.Text = BackendStatusText(status, internetEnabled);
        backendStatusText.Foreground = resourceBrush(BackendStatusBrushKey(status, internetEnabled));
        if (status is null)
        {
            backendStatusText.ToolTip = null;
            return;
        }

        var tooltipLines = new List<string> { status.Message };
        if (!string.IsNullOrWhiteSpace(status.PayloadVersion))
        {
            tooltipLines.Add($"Payload revision: {status.PayloadVersion}");
        }
        if (!string.IsNullOrWhiteSpace(status.PayloadPath))
        {
            tooltipLines.Add($"Payload path: {status.PayloadPath}");
        }
        backendStatusText.ToolTip = string.Join(Environment.NewLine, tooltipLines);
    }

    public void Dispose()
    {
        diagnosticUiVersion++;
        diagnosticsRunner.Dispose();
        backendStatusVersion++;
        CancelBestEffort(backendStatusCancellation);
        backendStatusCancellation = null;
        settingPersistenceVersion++;
        CancelBestEffort(settingPersistenceCancellation);
        settingPersistenceCancellation = null;
        settingPersistenceSessionId = "";
        settingPersistenceEnabled = false;
        settingPersistencePriorEnabled = false;
    }

    private static InternetDiagnosticsReport UnavailableDiagnosticsReport()
    {
        var backend = new SearxngSupervisorStatus(
            false,
            false,
            false,
            SearxngSupervisorService.ResolveBaseUri(null),
            "Internet diagnostics are unavailable because the app service is not initialized.");
        return new InternetDiagnosticsReport(
            backend,
            new InternetSearchDiagnostic(false, TimeSpan.Zero, 0, null, null, backend.Message),
            new InternetFetchDiagnostic(false, TimeSpan.Zero, null, backend.Message));
    }

    private static string FormatLatency(TimeSpan latency)
    {
        return latency.TotalMilliseconds < 1000
            ? $"{Math.Max(0, latency.TotalMilliseconds):0} ms"
            : $"{latency.TotalSeconds:0.0} s";
    }

    private static string FormatEngineCounts(InternetSearchDiagnostic search)
    {
        if (search.ResponsiveEngineCount is null && search.UnresponsiveEngineCount is null)
        {
            return "engine counts not reported";
        }

        var responsive = search.ResponsiveEngineCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not reported";
        var unresponsive = search.UnresponsiveEngineCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not reported";
        return $"engines: {responsive} responsive, {unresponsive} unresponsive";
    }

    private static string SearchFailureAction(SearxngSupervisorStatus backend)
    {
        if (!SearxngSupervisorService.IsBundledDefaultUri(backend.BaseUri))
        {
            return "verify AIARENA_SEARXNG_URL and its HTTPS endpoint, then retry.";
        }

        if (!backend.PayloadFound)
        {
            return "reinstall AI Arena with the Local web search component enabled, then retry.";
        }

        return "restart AI Arena; if it persists, check local firewall access and enabled SearXNG engines.";
    }

    private static string CleanError(string error)
    {
        return string.IsNullOrWhiteSpace(error)
            ? "No failure detail was returned."
            : error.Trim().ReplaceLineEndings(" ");
    }

    private static void CancelBestEffort(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // Cancellation callback failures must not prevent a newer health check or shutdown.
        }
    }
}

internal sealed class LatestInternetDiagnosticsRunner : IDisposable
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<InternetDiagnosticsReport>> runAsync;
    private CancellationTokenSource? currentCancellation;
    private int version;
    private bool disposed;

    public LatestInternetDiagnosticsRunner(Func<CancellationToken, Task<InternetDiagnosticsReport>> runAsync)
    {
        this.runAsync = runAsync;
    }

    public async Task<InternetDiagnosticsReport?> RunAsync()
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        int currentVersion;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = currentCancellation;
            current = new CancellationTokenSource();
            currentCancellation = current;
            currentVersion = ++version;
        }

        CancelBestEffort(previous);
        try
        {
            var report = await runAsync(current.Token);
            return IsCurrent(currentVersion) ? report : null;
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return null;
        }
        catch when (!IsCurrent(currentVersion))
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(currentCancellation, current))
                {
                    currentCancellation = null;
                }
            }

            current.Dispose();
        }
    }

    private bool IsCurrent(int candidateVersion)
    {
        lock (gate)
        {
            return !disposed && candidateVersion == version;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            version++;
            cancellation = currentCancellation;
            currentCancellation = null;
        }

        CancelBestEffort(cancellation);
    }

    private static void CancelBestEffort(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // A completed owner or a failing callback must not block the newest run or shutdown.
        }
    }
}
