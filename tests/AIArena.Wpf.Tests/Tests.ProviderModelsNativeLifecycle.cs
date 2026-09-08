using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
static void ProviderModelsNativeLifecycleUsesDetectedAdapters()
{
    foreach (var mode in new[] { ModelProviderApiModes.OllamaNative, ModelProviderApiModes.LlamaCppNative })
    {
        RunStaTest(() =>
        {
            var root = CreateProviderCatalogTestRoot("native-lifecycle-" + mode);
            try
            {
                const string sessionId = "native-models-session";
                const string modelId = "native-model";
                var sessionStore = new SessionStore(root);
                var snapshot = SessionStore.CreateDefaultSnapshot();
                snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = "http://127.0.0.1:19099/v1",
                    ApiMode = mode,
                    Model = modelId,
                    ApiToken = "native-test-token",
                    ContextLength = 4096,
                    NativeIdleTtlSeconds = 45
                };
                sessionStore.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
                var revision = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!.PersistenceRevision;
                var active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult().Single(session => session.Id == sessionId);
                using var operationLock = new SemaphoreSlim(1, 1);
                var providerConfig = new ProviderConfigurationControlService(
                    sessionStore, new EventLogStore(root), operationLock, () => active, () => false,
                    (_, _, _) => Task.CompletedTask);
                using var handler = new NativeModelsLifecycleHandler(mode, modelId);
                using var http = new HttpClient(handler);
                var control = new ProviderModelAssignmentsControl();
                AttachArenaPresentationResources(control);
                ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
                using var coordinator = new ProviderModelsSurfaceCoordinator(
                    control, sessionStore, providerConfig, new ModelProviderHealthService(http),
                    new ModelPreloadService(http), operationLock, () => active, () => false,
                    ollamaCatalog: new OllamaModelCatalogService(http),
                    llamaCppRuntime: new LlamaCppRuntimeService(http));
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
                    Require(control.LifecycleAction.IsEnabled
                            && control.LifecycleAction.Content?.ToString() == "Load model"
                            && handler.Mutations.Count == 0,
                        $"{mode}: native catalog did not enable an explicit Load action without automatically mutating the server");
                    Require(!AutomationProperties.GetHelpText(control.LifecycleAction).Contains("LM Studio", StringComparison.Ordinal),
                        $"{mode}: load guidance named a different provider");
                    Task? lifecycle = null;
                    control.LifecycleRequested += (_, args) => lifecycle = coordinator.RunLifecycleAsync(args);
                    void ClickLifecycle() => PumpProviderModelsRefresh(() =>
                    {
                        lifecycle = null;
                        control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(
                            System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        return lifecycle ?? throw new InvalidOperationException("Expected an enabled native lifecycle action.");
                    });
                    ClickLifecycle();
                    Require(handler.Loaded && handler.Mutations.Count == 1
                            && control.LifecycleAction.Content?.ToString() == "Unload model"
                            && AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded",
                        $"{mode}: native load was not confirmed in the shared Models surface");
                    ClickLifecycle();
                    Require(!handler.Loaded && handler.Mutations.Count == 2
                            && control.LifecycleAction.Content?.ToString() == "Load model",
                        $"{mode}: native unload was not confirmed in the shared Models surface");
                    if (ModelProviderApiModes.IsOllamaNative(mode))
                    {
                        using var loadBody = JsonDocument.Parse(handler.Mutations[0].Body);
                        using var unloadBody = JsonDocument.Parse(handler.Mutations[1].Body);
                        Require(handler.Mutations.All(item => item.Path == "/api/generate")
                                && loadBody.RootElement.GetProperty("model").GetString() == modelId
                                && loadBody.RootElement.GetProperty("keep_alive").GetInt32() == 45
                                && loadBody.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32() == 4096
                                && unloadBody.RootElement.GetProperty("keep_alive").GetInt32() == 0,
                            "Ollama lifecycle did not use native generate with the saved context and keep-alive settings");
                    }
                    else
                    {
                        Require(handler.Mutations[0].Path == "/models/load" && handler.Mutations[1].Path == "/models/unload",
                            "llama.cpp lifecycle did not use the existing router endpoints");
                        using var body = JsonDocument.Parse(handler.Mutations[0].Body);
                        Require(body.RootElement.GetProperty("model").GetString() == modelId,
                            "llama.cpp lifecycle changed the actual server model identifier");
                    }
                    Require(handler.Mutations.All(item => item.Authorization == "Bearer native-test-token"),
                        $"{mode}: native mutation lost the configured authorization");

                    handler.Loaded = true;
                    PumpProviderModelsRefresh(() => coordinator.HeartbeatAsync());
                    Require(control.LifecycleAction.Content?.ToString() == "Unload model",
                        $"{mode}: background heartbeat did not observe a load performed in the provider app");

                    handler.DropNextMutationResponse = true;
                    handler.FailEvidenceAfterMutation = true;
                    ClickLifecycle();
                    Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Unconfirmed"
                            && control.HasUnconfirmedLifecycleReceipt,
                        $"{mode}: an interrupted native response falsely reported failure or success without post-action evidence");
                    handler.FailEvidenceAfterMutation = false;
                    PumpProviderModelsRefresh(() => coordinator.HeartbeatAsync());
                    Require(AutomationProperties.GetItemStatus(control.LifecycleStatus) == "Succeeded"
                            && !control.HasUnconfirmedLifecycleReceipt,
                        $"{mode}: later authoritative evidence did not reconcile the uncertain native mutation");

                    handler.LifecycleEvidence = false;
                    PumpProviderModelsRefresh(() => coordinator.RefreshAsync(true));
                    Require(!control.LifecycleAction.IsEnabled,
                        $"{mode}: missing provider capability or residency evidence left mutations enabled");
                    if (ModelProviderApiModes.IsLlamaCppNative(mode))
                    {
                        handler.LifecycleEvidence = true;
                        handler.RouterStatusOverride = "loading";
                        PumpProviderModelsRefresh(() => coordinator.RefreshAsync(true));
                        Require(!control.LifecycleAction.IsEnabled,
                            "llama.cpp in-progress native status was overwritten by a compatible model name and enabled a duplicate mutation");
                    }
                    var persisted = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                    Require(persisted.PersistenceRevision == revision
                            && persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == modelId,
                        $"{mode}: provider lifecycle or heartbeat changed durable model routing");
                }
                finally { host.Close(); }
            }
            finally { DeleteProviderCatalogTestRoot(root); }
        });
    }
}

static void ProviderModelsMultiServerCatalogKeepsSources()
{
    RunStaTest(() =>
    {
        var root = CreateProviderCatalogTestRoot("multi-server-models");
        try
        {
            const string sessionId = "multi-server-models-session";
            const string rawModelId = "same-model";
            var first = new ModelProviderConfig { BaseUrl = "http://127.0.0.1:19099/v1", ApiMode = ModelProviderApiModes.LlamaCppNative, Model = rawModelId };
            var second = new ModelProviderConfig { BaseUrl = "http://127.0.0.1:19100/v1", ApiMode = ModelProviderApiModes.OllamaNative, Model = rawModelId };
            var inventory = new ProviderServerInventory();
            inventory.ReplaceDetectedServers([first, second]);
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            foreach (var key in snapshot.Configs.Keys.ToArray())
                snapshot.Configs[key] = new ModelProviderConfig { BaseUrl = first.BaseUrl, ApiMode = first.ApiMode, Model = "" };
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = first;
            store.SaveSnapshotAsync(snapshot, sessionId).GetAwaiter().GetResult();
            var active = store.ListSessionsAsync().GetAwaiter().GetResult().Single(session => session.Id == sessionId);
            using var operationLock = new SemaphoreSlim(1, 1);
            var service = new ProviderConfigurationControlService(store, new EventLogStore(root), operationLock,
                () => active, () => false, (_, _, _) => Task.CompletedTask);
            using var firstHandler = new NativeModelsLifecycleHandler(first.ApiMode, rawModelId);
            using var secondHandler = new NativeModelsLifecycleHandler(second.ApiMode, rawModelId);
            using var handler = new MultiServerModelsHandler(firstHandler, secondHandler);
            using var http = new HttpClient(handler);
            var control = new ProviderModelAssignmentsControl();
            AttachArenaPresentationResources(control);
            ApplyExperimentSurfaceTheme(control, ThemePalette.Resolve("dark-blue"));
            using var coordinator = new ProviderModelsSurfaceCoordinator(control, store, service,
                new ModelProviderHealthService(http), new ModelPreloadService(http), operationLock, () => active, () => false,
                ollamaCatalog: new OllamaModelCatalogService(http), llamaCppRuntime: new LlamaCppRuntimeService(http),
                serverInventory: inventory);
            var host = new System.Windows.Window { Content = control, Width = 1300, Height = 800,
                ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Opacity = 0, Left = -10000, Top = -10000 };
            host.Show();
            try
            {
                PumpProviderModelsRefresh(() => coordinator.RefreshAsync(true));
                FlushProviderModelsDispatcher(host);
                var firstRow = ProviderServerInventory.ModelIdentity(first, rawModelId);
                var secondRow = ProviderServerInventory.ModelIdentity(second, rawModelId);
                Require(firstRow != secondRow && control.CatalogRowCount == 2,
                    "Servers serving the same model identifier were incorrectly deduplicated into one Models row");
                Require(control.SelectModel(secondRow), "The second server's qualified model row was not selectable");
                FlushProviderModelsDispatcher(host);
                Task? assignment = null;
                control.AssignmentChanged += (_, args) => assignment = coordinator.SaveAssignmentAsync(args);
                var alpha = FindProviderModelsDescendants<System.Windows.Controls.CheckBox>(control.DetailSurface)
                    .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Alpha", StringComparison.OrdinalIgnoreCase));
                Require(alpha.IsChecked != true, "The first server's shared model was treated as an explicit assignment to the second server");
                PumpProviderModelsRefresh(() =>
                {
                    ToggleProviderModelsCheckBox(alpha);
                    return assignment ?? throw new InvalidOperationException("Expected server-qualified Alpha assignment.");
                });
                var saved = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Require(saved.Configs["alpha"].BaseUrl == second.BaseUrl
                        && saved.Configs["alpha"].ApiMode == second.ApiMode
                        && saved.Configs["alpha"].Model == rawModelId
                        && saved.Configs[ModelProviderRouting.SharedConfigKey].BaseUrl == first.BaseUrl,
                    "Selecting a model from the second server failed to persist its actual endpoint, adapter, and raw model identifier");
                Require(control.SelectModel(firstRow), "The original server disappeared after assigning a role to the second server");
                FlushProviderModelsDispatcher(host);
                var firstAlpha = FindProviderModelsDescendants<System.Windows.Controls.CheckBox>(control.DetailSurface)
                    .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Alpha", StringComparison.OrdinalIgnoreCase));
                Require(firstAlpha.IsChecked != true, "Same-name model rows on different servers shared an assignment toggle");
                Require(control.SelectModel(secondRow), "Could not return to second server before lifecycle test");
                Task? lifecycle = null;
                control.LifecycleRequested += (_, args) => lifecycle = coordinator.RunLifecycleAsync(args);
                PumpProviderModelsRefresh(() =>
                {
                    control.LifecycleAction.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    return lifecycle ?? throw new InvalidOperationException("Expected second-server load action.");
                });
                Require(firstHandler.Mutations.Count == 0 && secondHandler.Mutations.Count == 1 && secondHandler.Loaded,
                    "A model lifecycle action was sent to the shared server instead of the selected model's server");
                using var sent = JsonDocument.Parse(secondHandler.Mutations[0].Body);
                Require(sent.RootElement.GetProperty("model").GetString() == rawModelId,
                    "A server-qualified UI row key was sent as an HTTP model identifier");

                void ReplaceOllamaToken(string token)
                {
                    var current = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                    current.Configs["alpha"] = new ModelProviderConfig
                    {
                        BaseUrl = second.BaseUrl, ApiMode = second.ApiMode, Model = rawModelId,
                        ExplicitModelAssignment = true, ApiToken = token
                    };
                    store.SaveSnapshotAsync(current, sessionId).GetAwaiter().GetResult();
                }
                ReplaceOllamaToken("previous-ollama-token");
                PumpProviderModelsRefresh(() => coordinator.RefreshAsync(true));
                control.SelectModel(secondRow);
                FlushProviderModelsDispatcher(host);
                ReplaceOllamaToken("current-ollama-token");
                var beforeStaleAssignment = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                var beta = FindProviderModelsDescendants<System.Windows.Controls.CheckBox>(control.DetailSurface)
                    .Single(toggle => AutomationProperties.GetName(toggle).EndsWith("Beta", StringComparison.OrdinalIgnoreCase));
                Require(beta.IsChecked != true, "Stale-source regression requires an empty Beta assignment");
                PumpProviderModelsRefresh(() =>
                {
                    assignment = null;
                    ToggleProviderModelsCheckBox(beta);
                    return assignment ?? throw new InvalidOperationException("Expected stale-source Beta assignment attempt.");
                });
                var rejectedAssignment = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Require(rejectedAssignment.PersistenceRevision == beforeStaleAssignment.PersistenceRevision
                        && rejectedAssignment.Configs["alpha"].ApiToken == "current-ollama-token"
                        && ProviderModelAssignmentProjectionService.ConfiguredRoleModel(
                            rejectedAssignment.Configs, "beta", rejectedAssignment.Configs[ModelProviderRouting.SharedConfigKey]).Length == 0
                        && !control.HasPendingAssignment,
                    "A stale cached Ollama connection was used to assign Beta after another role rotated its credential");

                // The rejected write refreshed current evidence; rotate again to
                // prove model configuration also validates the selected source.
                ReplaceOllamaToken("latest-ollama-token");
                var beforeStaleConfiguration = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Task? configuration = null;
                control.ConfigurationChanged += (_, args) => configuration = coordinator.SaveConfigurationAsync(args);
                PumpProviderModelsRefresh(() =>
                {
                    control.ProviderDefaultContextToggle.IsChecked = false;
                    return configuration ?? throw new InvalidOperationException("Expected stale-source configuration attempt.");
                });
                var rejectedConfiguration = store.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()!;
                Require(rejectedConfiguration.PersistenceRevision == beforeStaleConfiguration.PersistenceRevision
                        && rejectedConfiguration.Configs["alpha"].ApiToken == "latest-ollama-token"
                        && !control.HasPendingConfiguration,
                    "Model settings were saved against a stale cached Ollama connection");
            }
            finally { host.Close(); }
        }
        finally { DeleteProviderCatalogTestRoot(root); }
    });
}

private sealed class MultiServerModelsHandler(HttpMessageHandler first, HttpMessageHandler second) : HttpMessageHandler
{
    private readonly HttpMessageInvoker firstInvoker = new(first, disposeHandler: false);
    private readonly HttpMessageInvoker secondInvoker = new(second, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        (request.RequestUri!.Port == 19099 ? firstInvoker : secondInvoker).SendAsync(request, cancellationToken);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { firstInvoker.Dispose(); secondInvoker.Dispose(); }
        base.Dispose(disposing);
    }
}

private sealed class NativeModelsLifecycleHandler(string mode, string modelId) : HttpMessageHandler
{
    public bool Loaded { get; set; }
    public bool LifecycleEvidence { get; set; } = true;
    public string RouterStatusOverride { get; set; } = "";
    public bool DropNextMutationResponse { get; set; }
    public bool FailEvidenceAfterMutation { get; set; }
    public List<(string Path, string Body, string Authorization)> Mutations { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Mutations.Add((path, body, request.Headers.Authorization?.ToString() ?? ""));
            using var document = JsonDocument.Parse(body);
            Loaded = path == "/api/generate"
                ? !document.RootElement.TryGetProperty("keep_alive", out var keepAlive) || keepAlive.GetInt32() != 0
                : path.EndsWith("/load", StringComparison.Ordinal);
            if (DropNextMutationResponse)
            {
                DropNextMutationResponse = false;
                throw new HttpRequestException("Connection closed after accepting the request.");
            }
            return Json(path == "/api/generate" ? "{\"load_duration\":1000000}" : "{\"success\":true}");
        }
        if (FailEvidenceAfterMutation && Mutations.Count > 2)
            return Json("{}", HttpStatusCode.ServiceUnavailable);
        if (ModelProviderApiModes.IsOllamaNative(mode))
        {
            if (path == "/api/tags")
                return Json(JsonSerializer.Serialize(new { models = new[] { new { name = modelId, model = modelId } } }));
            if (path == "/api/ps")
                return !LifecycleEvidence ? Json("{}", HttpStatusCode.NotFound)
                    : Json(JsonSerializer.Serialize(new { models = Loaded
                        ? new[] { new { name = modelId, model = modelId, context_length = 4096 } } : [] }));
        }
        else
        {
            if (path == "/health") return Json("{\"status\":\"ok\"}");
            if (path == "/models" && LifecycleEvidence)
                return Json(JsonSerializer.Serialize(new { data = new[] { new { id = modelId, status = new { value = RouterStatusOverride.Length > 0 ? RouterStatusOverride : Loaded ? "loaded" : "unloaded" } } } }));
            if (path == "/v1/models")
                return Json(JsonSerializer.Serialize(new { data = new[] { new { id = modelId, owned_by = "llamacpp" } } }));
        }
        return Json("{}", HttpStatusCode.NotFound);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
}
