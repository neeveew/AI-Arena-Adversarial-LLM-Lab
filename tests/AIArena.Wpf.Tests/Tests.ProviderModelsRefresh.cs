using System.IO;
using System.Net;
using System.Windows.Automation;
using System.Windows.Input;
using System.Text;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
static void ProviderModelsSaveFailuresReleasePendingControls()
{
    foreach (var configuration in new[] { false, true })
    foreach (var failure in new[] { "snapshot", "cancel" })
        ProviderModelsSaveFailureRegression(configuration, failure);
}

static void ProviderModelsCommittedSavesSurviveSecondaryFailures()
{
    foreach (var configuration in new[] { false, true })
    foreach (var failure in new[] { "log", "refresh", "refresh-cancel", "log-refresh" })
        ProviderModelsSaveFailureRegression(configuration, failure);
}

static void ProviderModelsSaveFailureRegression(bool configuration, string failure)
{
    RunStaTest(() =>
    {
        var root = CreateProviderCatalogTestRoot("model-save-" + failure);
        try
        {
            const string sessionId = "model-save-session";
            const string modelId = "single-flight-model";
            var store = new SessionStore(root);
            var events = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                Model = modelId
            };
            store.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
            var active = store.ListSessionsAsync().GetAwaiter().GetResult().Single(item => item.Id == sessionId);
            var original = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
            using var operationLock = new SemaphoreSlim(1, 1);
            var refreshCalls = 0;
            var configurationService = new ProviderConfigurationControlService(
                store, events, operationLock, () => active, () => false, (_, _, _) =>
                {
                    refreshCalls++;
                    if (failure == "refresh-cancel")
                        throw new OperationCanceledException("private canceled refresh detail");
                    if (failure.Contains("refresh", StringComparison.Ordinal))
                        throw new IOException("private failed refresh path");
                    return Task.CompletedTask;
                });
            using var handler = new BlockingProviderModelsRefreshHandler();
            using var http = new HttpClient(handler);
            var catalog = new LmStudioModelCatalogService(http);
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            using var coordinator = new ProviderModelsSurfaceCoordinator(
                control, store, configurationService, new ModelProviderHealthService(http),
                new ModelPreloadService(http, catalog), operationLock, () => active, () => false, catalog);
            var host = new System.Windows.Window
            {
                Content = control, Width = 1300, Height = 800, ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None, Opacity = 0, Left = -10000, Top = -10000
            };
            host.Show();
            try
            {
                PumpProviderModelsRefresh(() => coordinator.RefreshAsync(true));
                FlushProviderModelsDispatcher(host);
                ProviderModelAssignmentChangedEventArgs? assignment = null;
                ProviderModelConfigurationChangedEventArgs? settings = null;
                control.AssignmentChanged += (_, args) => assignment = args;
                control.ConfigurationChanged += (_, args) => settings = args;
                if (configuration)
                {
                    control.ContextWindowInput.Text = "8192";
                    RaiseProviderModelsPreviewKey(control.ContextWindowInput, host, Key.Enter);
                    Require(settings is not null && control.HasPendingConfiguration,
                        "context edit did not create a pending configuration save");
                }
                else
                {
                    var target = FindProviderModelsDescendants<ProviderAssignmentCheckBox>(control.AssignmentTargets)
                        .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Default for unassigned agents", StringComparison.Ordinal));
                    Require(target.IsChecked == true, "fixture default model assignment was missing");
                    ToggleProviderModelsCheckBox(target);
                    Require(assignment is { IsAssigned: false } && control.HasPendingAssignment,
                        "default toggle did not create a pending assignment save");
                }
                Require(!control.RefreshAction.IsEnabled && !control.LifecycleAction.IsEnabled,
                    "the regression fixture did not enter the mutation-pending state");

                if (failure.Contains("log", StringComparison.Ordinal))
                    Directory.CreateDirectory(events.EventPath(sessionId));
                using var locked = failure == "snapshot"
                    ? File.Open(store.SnapshotPath(sessionId), FileMode.Open, FileAccess.Read, FileShare.Read)
                    : null;
                using var cancellation = new CancellationTokenSource();
                if (failure == "cancel") cancellation.Cancel();
                Exception? observed = null;
                PumpProviderModelsRefresh(async () =>
                {
                    try
                    {
                        if (configuration) await coordinator.SaveConfigurationAsync(settings!, cancellation.Token);
                        else await coordinator.SaveAssignmentAsync(assignment!, cancellation.Token);
                    }
                    catch (Exception exception)
                    {
                        observed = exception;
                    }
                });
                locked?.Dispose();
                FlushProviderModelsDispatcher(host);
                var status = configuration ? control.ConfigurationStatus : control.AssignmentStatus;
                var committed = failure is not ("snapshot" or "cancel");
                Require(!control.HasPendingAssignment && !control.HasPendingConfiguration
                        && control.RefreshAction.IsEnabled && control.ContextWindowInput.IsEnabled
                        && control.LifecycleAction.IsEnabled,
                    $"{configuration}/{failure}: a terminal save left model controls stuck pending");
                Require(AutomationProperties.GetItemStatus(status) == (committed ? "Saved" : "Failed"),
                    $"{configuration}/{failure}: save outcome did not distinguish persistence from secondary work: {status.Text}");
                if (committed)
                {
                    Require(observed is null && refreshCalls == 1 && status.Text.Contains("Warning:", StringComparison.Ordinal)
                            && !status.Text.Contains("private", StringComparison.Ordinal),
                        $"{configuration}/{failure}: a secondary error lost saved status, suppressed refresh, or exposed exception details");
                }
                else
                {
                    Require((failure == "cancel" ? observed is OperationCanceledException : observed is IOException or UnauthorizedAccessException)
                            && refreshCalls == 0,
                        $"{configuration}/{failure}: the expected pre-commit failure was not reported");
                }
                var durable = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                var durableConfiguration = ProviderConfigurationControlService.CaptureModelConfiguration(sessionId, durable, modelId);
                Require(durable.Engine.DefaultForUnassignedAgentsEnabled == (!configuration && committed ? false : original.Engine.DefaultForUnassignedAgentsEnabled)
                        && durableConfiguration.ConfiguredContextWindow == (configuration && committed ? 8192 : 0)
                        && (durable.PersistenceRevision > original.PersistenceRevision) == committed,
                    $"{configuration}/{failure}: durable configuration disagreed with the reported save outcome");
                PumpProviderModelsRefresh(() => coordinator.RefreshAsync(false));
                Require(!control.HasPendingAssignment && !control.HasPendingConfiguration && control.RefreshAction.IsEnabled,
                    "a subsequent refresh resurrected the failed save or disabled retry");
            }
            finally
            {
                host.Close();
            }
        }
        finally
        {
            DeleteProviderCatalogTestRoot(root);
        }
    });
}

static void ProviderModelsRefreshCoalescesAndDisposesQueuedWork()
{
    RunStaTest(() =>
    {
        var root = CreateProviderCatalogTestRoot("refresh-single-flight");
        try
        {
            const string sessionId = "refresh-single-flight-session";
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                Model = "single-flight-model"
            };
            sessionStore.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
            SessionSummary? activeSession = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
                .Single(item => item.Id == sessionId);

            using var operationLock = new SemaphoreSlim(1, 1);
            var providerConfiguration = new ProviderConfigurationControlService(
                sessionStore,
                eventLogStore,
                operationLock,
                () => activeSession,
                () => false,
                (_, _, _) => Task.CompletedTask);
            using var handler = new BlockingProviderModelsRefreshHandler();
            using var httpClient = new HttpClient(handler);
            var catalogService = new LmStudioModelCatalogService(httpClient);
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            var coordinator = new ProviderModelsSurfaceCoordinator(
                control,
                sessionStore,
                providerConfiguration,
                new ModelProviderHealthService(httpClient),
                new ModelPreloadService(httpClient, catalogService),
                operationLock,
                () => activeSession,
                () => false,
                catalogService);
            var host = new System.Windows.Window
            {
                Content = control,
                Width = 1300,
                Height = 800,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
                Opacity = 0,
                Left = -10000,
                Top = -10000
            };

            host.Show();
            try
            {
                handler.BlockNextRequest();
                PumpProviderModelsRefresh(async () =>
                {
                    var heartbeat = coordinator.HeartbeatAsync();
                    await handler.WaitUntilBlockedAsync();
                    var manual = coordinator.RefreshAsync(refreshCatalog: true);
                    handler.ReleaseBlockedRequest();
                    await Task.WhenAll(heartbeat, manual);
                });
                Require(handler.NativeCatalogRequestCount == 1,
                    "simultaneous heartbeat and manual refresh did not coalesce to one native catalog request");

                handler.BlockNextRequest();
                var queuedWasCancelled = false;
                PumpProviderModelsRefresh(async () =>
                {
                    var active = coordinator.RefreshAsync(refreshCatalog: true);
                    await handler.WaitUntilBlockedAsync();
                    var queued = coordinator.RefreshAsync(refreshCatalog: true);
                    coordinator.Dispose();
                    handler.ReleaseBlockedRequest();
                    await IgnoreCancellationAsync(active);
                    try
                    {
                        await queued;
                    }
                    catch (Exception exception) when (exception is OperationCanceledException
                                                       or ObjectDisposedException)
                    {
                        queuedWasCancelled = true;
                    }
                });
                Require(queuedWasCancelled && handler.NativeCatalogRequestCount == 2,
                    "disposing the Models coordinator did not cancel queued refresh work before another provider request");
            }
            finally
            {
                coordinator.Dispose();
                host.Close();
            }
        }
        finally
        {
            DeleteProviderCatalogTestRoot(root);
        }
    });
}

static void PumpProviderModelsRefresh(Func<Task> start)
{
    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    var previousContext = SynchronizationContext.Current;
    try
    {
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        var task = start();
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    new Action(() => frame.Continue = false),
                    System.Windows.Threading.DispatcherPriority.Send),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }
    finally
    {
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }
}

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
}

private sealed class BlockingProviderModelsRefreshHandler : HttpMessageHandler
{
    private TaskCompletionSource<bool> blocked = NewSignal();
    private TaskCompletionSource<bool> released = NewSignal();
    private int blockNext;
    private int nativeCatalogRequestCount;

    public int NativeCatalogRequestCount => Volatile.Read(ref nativeCatalogRequestCount);

    public void BlockNextRequest()
    {
        blocked = NewSignal();
        released = NewSignal();
        Interlocked.Exchange(ref blockNext, 1);
    }

    public Task WaitUntilBlockedAsync() => blocked.Task;

    public void ReleaseBlockedRequest() => released.TrySetResult(true);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/api/v1/models", StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref nativeCatalogRequestCount);
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                blocked.TrySetResult(true);
                await released.Task.WaitAsync(cancellationToken);
            }

            return Json("""
                {"models":[{"type":"llm","publisher":"local","key":"single-flight-model","display_name":"Single flight model","loaded_instances":[]}]}
                """);
        }

        return Json("{}", HttpStatusCode.NotFound);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
}
