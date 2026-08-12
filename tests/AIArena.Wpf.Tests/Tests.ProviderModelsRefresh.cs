using System.Net;
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
