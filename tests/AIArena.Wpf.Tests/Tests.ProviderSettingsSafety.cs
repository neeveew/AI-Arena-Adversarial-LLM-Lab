using System.Net;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ProviderSettingsDownloadKeepsCapturedSessionAndCredentials()
    {
        RunStaTest(() =>
        {
            foreach (var mode in new[] { ModelProviderApiModes.LmStudioNative, ModelProviderApiModes.OllamaNative })
            {
                using var fixture = new ProviderSettingsSafetyFixture(mode);
                fixture.Handler.Response = request => request.Method == HttpMethod.Post
                    ? mode == ModelProviderApiModes.OllamaNative ? "{\"status\":\"success\"}" : "{\"job_id\":\"job-a\",\"status\":\"downloading\"}"
                    : "{\"status\":\"completed\"}";
                fixture.Handler.BlockNext();
                PumpProviderModelsRefresh(async () =>
                {
                    var download = fixture.Coordinator.DownloadModelAsync();
                    await fixture.Handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await fixture.Coordinator.DownloadModelAsync();
                    Require(fixture.Handler.Requests.Count == 1, "a second click must not duplicate an in-flight download mutation");
                    fixture.ActiveSession = fixture.SessionB;
                    fixture.TextBox("providerBaseUrlText").Text = "http://127.0.0.1:4322/v1";
                    fixture.Password.Password = "fixture-token-b";
                    fixture.Status.Text = "Session B status";
                    fixture.Handler.Release.TrySetResult();
                    await download;
                });
                Require(fixture.Handler.Requests.Count == (mode == ModelProviderApiModes.OllamaNative ? 1 : 2)
                    && fixture.Handler.Requests.All(request => request.Port == 4321 && request.Authorization == "Bearer fixture-token-a"),
                    "an in-flight download/pull must retain A's destination and token through its initial observation");
                Require(fixture.Status.Text == "Session B status", "a late download result must not repaint the newly selected session");
                Require(File.Exists(fixture.Events.EventPath(fixture.SessionA.Id))
                    && !File.Exists(fixture.Events.EventPath(fixture.SessionB.Id)),
                    "download and pull audit entries must belong to the captured session");
            }
        });
    }

    static void ProviderSettingsDownloadRetiresInitialAndPolledFailures()
    {
        RunStaTest(() =>
        {
            using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.LmStudioNative);
            var failObservation = true;
            fixture.Handler.Response = request => request.Method == HttpMethod.Post
                ? "{\"job_id\":\"job-a\",\"status\":\"downloading\"}"
                : request.RequestUri!.AbsolutePath.Contains("/download/status/", StringComparison.Ordinal)
                    ? failObservation ? "{\"status\":\"failed\",\"detail\":{\"message\":\"disk full\"}}" : "{\"status\":\"downloading\"}"
                    : "{\"models\":[]}";
            PumpProviderModelsRefresh(async () =>
            {
                await fixture.Coordinator.DownloadModelAsync();
                Require(fixture.Status.Text.Contains("disk full", StringComparison.Ordinal)
                    && !fixture.Button("checkDownloadStatusButton").IsEnabled,
                    "the initial failed observation must replace the accepted start and disable polling");
                var before = fixture.Handler.Requests.Count;
                await fixture.Coordinator.CheckDownloadStatusAsync();
                Require(fixture.Handler.Requests.Count == before, "a terminal initial failure must retire the job");
                failObservation = false;
                await fixture.Coordinator.DownloadModelAsync();
                Require(fixture.Button("checkDownloadStatusButton").IsEnabled, "a running download should remain pollable");
                failObservation = true;
                await fixture.Coordinator.CheckDownloadStatusAsync();
                Require(fixture.Status.Text.Contains("disk full", StringComparison.Ordinal)
                    && !fixture.Button("checkDownloadStatusButton").IsEnabled,
                    "a manual failed observation must retire the job even when busy-button cleanup runs");
                before = fixture.Handler.Requests.Count;
                await fixture.Coordinator.CheckDownloadStatusAsync();
                Require(fixture.Handler.Requests.Count == before, "a terminal polled failure must not issue another request");
            });
        });
    }

    static void ProviderSettingsDownloadPreservesUnknownObservations()
    {
        RunStaTest(() =>
        {
            using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.LmStudioNative);
            var failure = "http";
            fixture.Handler.ResponseStatus = request => request.RequestUri!.AbsolutePath.Contains("/download/status/", StringComparison.Ordinal)
                && failure == "http" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            fixture.Handler.Response = request =>
            {
                if (request.Method == HttpMethod.Post) return "{\"job_id\":\"job-a\",\"status\":\"downloading\"}";
                if (failure == "timeout") throw new TaskCanceledException("fixture timeout");
                return failure switch
                {
                    "http" => "{\"error\":\"temporarily unavailable\"}",
                    "unknown" => "{\"status\":\"future_state\"}",
                    _ => "{\"status\":\"failed\",\"error\":\"disk full\"}"
                };
            };
            PumpProviderModelsRefresh(async () =>
            {
                await fixture.Coordinator.DownloadModelAsync();
                Require(fixture.Status.Text.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    && fixture.Button("checkDownloadStatusButton").IsEnabled,
                    "an accepted download followed by HTTP 503 must retain its job and offer an observation retry");
                foreach (var next in new[] { "timeout", "unknown" })
                {
                    failure = next;
                    await fixture.Coordinator.CheckDownloadStatusAsync();
                    Require(fixture.Status.Text.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                        && fixture.Button("checkDownloadStatusButton").IsEnabled,
                        "transient or unrecognized manual observations must retain the same pollable job");
                }
                Require(fixture.Handler.Requests.Count(request => request.Method == "POST") == 1
                    && fixture.Handler.Requests.Where(request => request.Method == "GET").All(request => request.Path.EndsWith("/job-a", StringComparison.Ordinal)),
                    "recovering an unknown observation must poll the accepted job without starting another download");
                failure = "terminal";
                await fixture.Coordinator.CheckDownloadStatusAsync();
                Require(!fixture.Button("checkDownloadStatusButton").IsEnabled && fixture.Status.Text.Contains("disk full", StringComparison.Ordinal),
                    "an authoritative failed state must retire the job after transient observations");
            });
            var terminal = LmStudioModelDownloadService.ParseStatusResponse("{\"status\":\"failed\"}", "fixture-model", "", "job-a");
            Require(terminal.IsComplete && terminal.ObservationAvailable && !terminal.Ok,
                "a parsed failed state must explicitly carry terminal evidence");
        });
    }

    static void ProviderSettingsAcceptedDownloadSurvivesCancellationAndAuditFailure()
    {
        RunStaTest(() =>
        {
            using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.LmStudioNative);
            Directory.CreateDirectory(fixture.Events.EventPath(fixture.SessionA.Id));
            fixture.Handler.Response = request => request.Method == HttpMethod.Post
                ? "{\"job_id\":\"job-a\",\"status\":\"downloading\"}" : "{\"status\":\"completed\"}";
            fixture.Handler.BlockRequest = request => request.Method == HttpMethod.Get;
            fixture.Handler.BlockNext();
            PumpProviderModelsRefresh(async () =>
            {
                using var cancellation = new CancellationTokenSource();
                var download = fixture.Coordinator.DownloadModelAsync(cancellation.Token);
                await fixture.Handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                var cancelled = false;
                try { await download; }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && fixture.Button("checkDownloadStatusButton").IsEnabled
                    && fixture.Status.Text.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                    && fixture.Status.Text.Contains("activity-log", StringComparison.Ordinal),
                    "cancelled initial polling and failed secondary logging must preserve acceptance with an unknown-status warning");
                await fixture.Coordinator.CheckDownloadStatusAsync();
                Require(fixture.Handler.Requests.Count(request => request.Method == "POST") == 1
                    && fixture.Status.Text.StartsWith("Download ready:", StringComparison.Ordinal)
                    && !fixture.Button("checkDownloadStatusButton").IsEnabled,
                    "the retained accepted receipt must be recoverable by polling without repeating the mutation");
            });

            using var pull = new ProviderSettingsSafetyFixture(ModelProviderApiModes.OllamaNative);
            Directory.CreateDirectory(pull.Events.EventPath(pull.SessionA.Id));
            pull.Handler.Response = _ => "{\"status\":\"success\"}";
            PumpProviderModelsRefresh(() => pull.Coordinator.DownloadModelAsync());
            Require(pull.Status.Text.StartsWith("Ollama pull ready:", StringComparison.Ordinal)
                && pull.Status.Text.Contains("activity-log", StringComparison.Ordinal),
                "a completed Ollama pull must remain successful when secondary logging fails");
        });
    }
    static void ProviderSettingsCatalogRejectsStaleSessionProviderAndSelection()
    {
        RunStaTest(() =>
        {
            foreach (var change in new[] { "session", "provider", "selection" })
            {
                using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.OpenAiCompatible);
                fixture.Handler.Response = _ => "{\"data\":[{\"id\":\"captured-model\"}]}";
                fixture.Handler.BlockNext();
                PumpProviderModelsRefresh(async () =>
                {
                    var refresh = fixture.Coordinator.RefreshAdvertisedModelsAsync(force: true);
                    await fixture.Handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (change == "session") fixture.ActiveSession = fixture.SessionB;
                    else if (change == "provider") fixture.TextBox("providerBaseUrlText").Text = "http://127.0.0.1:4322/v1";
                    else fixture.ComboBox("providerModelText").Text = "new-selection";
                    fixture.ModelsStatus.Text = "Current settings";
                    fixture.Handler.Release.TrySetResult();
                    await refresh;
                    Require(fixture.Coordinator.AdvertisedModels.Count == 0 && fixture.ModelsStatus.Text == "Current settings",
                        $"a stale {change} catalog response must not publish into current Settings");
                    fixture.Handler.Response = _ => "{\"data\":[{\"id\":\"fresh-model\"}]}";
                    await fixture.Coordinator.RefreshAdvertisedModelsAsync(force: true);
                    Require(fixture.Coordinator.AdvertisedModels.SequenceEqual(new[] { "fresh-model" }),
                        "rejecting an old lease must permit a fresh catalog to publish without configured-only rows becoming advertised");
                });
            }
        });
    }

    static void ProviderSettingsCatalogQueuesReplacementRefresh()
    {
        RunStaTest(() =>
        {
            foreach (var cancelOld in new[] { false, true })
            {
                using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.OpenAiCompatible);
                fixture.Handler.Response = request => request.RequestUri!.Port == 4321
                    ? "{\"data\":[{\"id\":\"old-model\"}]}" : "{\"data\":[{\"id\":\"replacement-model\"}]}";
                fixture.Handler.BlockNext();
                PumpProviderModelsRefresh(async () =>
                {
                    using var oldCancellation = new CancellationTokenSource();
                    var old = fixture.Coordinator.RefreshAdvertisedModelsAsync(force: true, oldCancellation.Token);
                    await fixture.Handler.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    fixture.ActiveSession = fixture.SessionB;
                    fixture.TextBox("providerBaseUrlText").Text = "http://127.0.0.1:4322/v1";
                    var replacement = fixture.Coordinator.RefreshAdvertisedModelsAsync(force: true);
                    Require(!replacement.IsCompleted, "replacement refresh must wait for its own inventory rather than silently returning");
                    if (cancelOld) oldCancellation.Cancel();
                    else fixture.Handler.Release.TrySetResult();
                    await replacement;
                    try { await old; }
                    catch (OperationCanceledException) when (cancelOld) { }
                    Require(fixture.Handler.Requests.Count == 2
                        && fixture.Coordinator.AdvertisedModels.SequenceEqual(new[] { "replacement-model" }),
                        "a changed provider must publish its queued inventory automatically even if the old refresh is cancelled");
                });
            }
        });
    }

    static void ProviderSettingsAndControlPreserveRoleSpecificProbeSettings()
    {
        RunStaTest(() =>
        {
            using var fixture = new ProviderSettingsSafetyFixture(ModelProviderApiModes.OpenAiCompatible);
            var snapshot = fixture.Store.LoadSnapshotAsync(fixture.SessionA.Id).GetAwaiter().GetResult()!;
            snapshot.Configs.Clear();
            snapshot.Engine.DefaultForUnassignedAgentsEnabled = false;
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:4321/v1", ApiToken = "fixture-token-a", Model = "shared", Timeout = 90
            };
            snapshot.Configs["alpha"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:4331/v1", ApiToken = "fixture-role-a", Model = "same-role-model",
                ExplicitModelAssignment = true, Timeout = 77, ContextLength = 4096, NativeStatefulChat = false, NativeIdleTtlSeconds = 45
            };
            snapshot.Configs["beta"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:4332/v1", ApiToken = "fixture-role-b", Model = "same-role-model",
                ExplicitModelAssignment = true, Timeout = 91, ContextLength = 8192
            };
            fixture.ApplySnapshot(snapshot);
            var persisted = fixture.Store.LoadSnapshotAsync(fixture.SessionA.Id).GetAwaiter().GetResult()!;
            var canonicalPlans = ModelProviderProbePlan.Build(persisted, allRoles: true);
            var uiPlans = ModelProviderProbePlan.Build(fixture.Coordinator.CaptureDiagnosticDraft(persisted), allRoles: true);
            Require(canonicalPlans.Count == uiPlans.Count && canonicalPlans.Zip(uiPlans).All(pair =>
                pair.First.Roles.SequenceEqual(pair.Second.Roles)
                && ModelProviderProbePlan.SameRequest(pair.First.Config, pair.Second.Config)),
                "unchanged Settings must retain every effective role's persisted endpoint, credential, timeout and runtime settings");
            fixture.Handler.Response = _ => "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
            PumpProviderModelsRefresh(async () =>
            {
                await fixture.Coordinator.TestAllRolesAsync();
                var uiRequests = fixture.Handler.Requests.ToArray();
                fixture.Handler.Requests.Clear();
                await fixture.Runtime.TestAsync(fixture.SessionA.Id, allRoles: true);
                Require(uiRequests.Length == 2 && uiRequests.SequenceEqual(fixture.Handler.Requests)
                    && uiRequests.Select(request => request.Port).SequenceEqual(new[] { 4331, 4332 }),
                    "Settings and control-plane runtime diagnostics must send identical effective role requests");
            });
            fixture.TextBox("providerBaseUrlText").Text = "http://127.0.0.1:4399/v1";
            fixture.ComboBox("providerModelText").Text = "new-shared-draft";
            fixture.ComboBox("alphaRoleModelText").Text = "new-role-draft";
            fixture.Coordinator.SaveRoleModelDrafts();
            var overlaid = fixture.Coordinator.CaptureDiagnosticDraft(persisted);
            Require(overlaid.Configs["alpha"].Model == "new-role-draft"
                && overlaid.Configs["alpha"].BaseUrl == persisted.Configs["alpha"].BaseUrl
                && overlaid.Configs["alpha"].Timeout == 77 && overlaid.Configs["beta"] == persisted.Configs["beta"],
                "shared/model textbox overlays must preserve saved role-specific connection and runtime settings");
        });
    }
    static void ProviderRoleProbesRespectUnassignedRoutesAndRuntimeSettings()
    {
        var root = CreateProviderCatalogTestRoot("effective-role-probes");
        try
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs.Clear();
            snapshot.Engine.DefaultForUnassignedAgentsEnabled = false;
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig { Model = "shared" };
            snapshot.Configs["alpha"] = new ModelProviderConfig { Model = "same-model", ExplicitModelAssignment = true, Timeout = 45, ContextLength = 4096 };
            snapshot.Configs["beta"] = new ModelProviderConfig { Model = "same-model", ExplicitModelAssignment = true, Timeout = 60, ContextLength = 8192 };
            store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            using var handler = new ProviderSettingsSafetyHandler { Response = _ => "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}" };
            using var client = new HttpClient(handler);
            var health = new ModelProviderHealthService(client);
            var runtime = new ProviderRuntimeService(store, health, new ProviderReachabilityService(store, new EventLogStore(root), health));
            var result = runtime.TestAsync("default", allRoles: true).GetAwaiter().GetResult();
            Require(!result.Ok && result.Reachable && handler.Requests.Count == 2,
                "all-role diagnostics must probe distinct runtime settings and fail unavailable routes without substituting shared");
            var unavailable = result.RoleResults.Single(item => item.Model.Length == 0);
            Require(!unavailable.Ok && unavailable.Roles.SequenceEqual(new[] { "gamma", "delta", "narrator" }),
                "the same effective route plan must retain the unavailable role identities");
            var plans = ModelProviderProbePlan.Build(snapshot, allRoles: true);
            Require(plans.Count == 3 && plans.Count(plan => plan.Config?.Model == "same-model") == 2,
                "probe deduplication must include runtime settings, not just model IDs");
        }
        finally { DeleteProviderCatalogTestRoot(root); }
    }

    private sealed class ProviderSettingsSafetyHandler : HttpMessageHandler
    {
        private bool blockNext;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(int Port, string Authorization, string Method, string Path)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpStatusCode> ResponseStatus { get; set; } = _ => HttpStatusCode.OK;
        public Func<HttpRequestMessage, bool>? BlockRequest { get; set; }
        public Func<HttpRequestMessage, string> Response { get; set; } = _ => "{}";
        public void BlockNext() => blockNext = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.Port, request.Headers.Authorization?.ToString() ?? "", request.Method.Method, request.RequestUri.AbsolutePath));
            if (blockNext && (BlockRequest?.Invoke(request) ?? true))
            {
                blockNext = false;
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(ResponseStatus(request)) { Content = new StringContent(Response(request), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ProviderSettingsSafetyFixture : IDisposable
    {
        private readonly string root = CreateProviderCatalogTestRoot("settings-safety");
        private readonly HttpClient client;
        private readonly SemaphoreSlim operationLock = new(1, 1);
        private readonly Dictionary<string, object?> arguments = new();
        public ProviderSettingsSafetyHandler Handler { get; } = new();
        public ProviderSettingsCoordinator Coordinator { get; }
        public EventLogStore Events { get; }
        public SessionStore Store { get; }
        public ArenaViewSnapshot? RenderedSnapshot { get; private set; }
        public ProviderRuntimeService Runtime => (ProviderRuntimeService)arguments["providerRuntime"]!;
        public SessionSummary SessionA { get; }
        public SessionSummary SessionB { get; }
        public SessionSummary? ActiveSession { get; set; }
        public TextBox TextBox(string name) => (TextBox)arguments[name]!;
        public ComboBox ComboBox(string name) => (ComboBox)arguments[name]!;
        public Button Button(string name) => (Button)arguments[name]!;
        public PasswordBox Password => (PasswordBox)arguments["providerApiTokenBox"]!;
        public TextBlock Status => (TextBlock)arguments["downloadModelStatusText"]!;
        public TextBlock ModelsStatus => (TextBlock)arguments["providerModelsStatus"]!;

        public ProviderSettingsSafetyFixture(string mode, bool persistChanges = false)
        {
            client = new HttpClient(Handler);
            var store = Store = new SessionStore(root);
            Events = new EventLogStore(root);
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "session-a").GetAwaiter().GetResult();
            store.SaveSnapshotAsync(SessionStore.CreateDefaultSnapshot(), "session-b").GetAwaiter().GetResult();
            var sessions = store.ListSessionsAsync().GetAwaiter().GetResult();
            SessionA = sessions.Single(session => session.Id == "session-a");
            SessionB = sessions.Single(session => session.Id == "session-b");
            ActiveSession = SessionA;
            var health = new ModelProviderHealthService(client);
            var lmCatalog = new LmStudioModelCatalogService(client);
            var ollamaCatalog = new OllamaModelCatalogService(client);
            arguments["sessionStore"] = store;
            arguments["eventLogStore"] = Events;
            arguments["providerHealth"] = health;
            arguments["providerRuntime"] = new ProviderRuntimeService(store, health, new ProviderReachabilityService(store, Events, health));
            arguments["modelPreloadService"] = new ModelPreloadService(client, lmCatalog);
            arguments["modelDownloadService"] = new LmStudioModelDownloadService(client);
            arguments["providerAutoConfigureService"] = new ProviderAutoConfigureService(health, lmCatalog, ollamaCatalog);
            arguments["lmStudioModelCatalogService"] = lmCatalog;
            arguments["ollamaModelCatalogService"] = ollamaCatalog;
            arguments["ollamaModelPullService"] = new OllamaModelPullService(client);
            arguments["arenaOperationLock"] = operationLock;
            arguments["activeSession"] = (Func<SessionSummary?>)(() => ActiveSession);
            arguments["lastRenderedSnapshot"] = (Func<ArenaViewSnapshot?>)(() => RenderedSnapshot);
            arguments["theme"] = (Func<ThemePalette>)(() => ThemePalette.Resolve("dark-blue"));
            arguments["loadSessionsAsync"] = (Func<string?, CancellationToken, Task>)((_, _) => Task.CompletedTask);
            arguments["saveSnapshotWithFeedbackAsync"] = (Func<ArenaSnapshot, string, CancellationToken, Task>)((snapshot, sessionId, token) =>
                persistChanges ? store.SaveSnapshotAsync(snapshot, sessionId, token) : Task.CompletedTask);
            arguments["refreshActiveSessionAsync"] = (Func<string, CancellationToken, Task>)((_, _) => Task.CompletedTask);
            arguments["refreshProviderReachabilityAsync"] = (Func<bool, CancellationToken, Task>)((_, _) => Task.CompletedTask);
            arguments["updateProviderHealthPopup"] = (Action)(() => { });
            var constructor = typeof(ProviderSettingsCoordinator).GetConstructors().Single();
            // Build real controls through the production constructor; only provider I/O is replaced.
            var values = constructor.GetParameters().Select(parameter =>
            {
                if (arguments.TryGetValue(parameter.Name!, out var value)) return value;
                if (parameter.HasDefaultValue) value = parameter.DefaultValue;
                else if (parameter.ParameterType == typeof(Panel)) value = new StackPanel();
                else if (typeof(FrameworkElement).IsAssignableFrom(parameter.ParameterType)) value = Activator.CreateInstance(parameter.ParameterType);
                else if (parameter.ParameterType == typeof(Func<bool>)) value = (Func<bool>)(() => false);
                else if (parameter.ParameterType == typeof(Func<string, Brush>)) value = (Func<string, Brush>)(_ => Brushes.Black);
                else if (parameter.ParameterType == typeof(Func<string, string>)) value = (Func<string, string>)(text => text);
                else throw new InvalidOperationException($"Missing fixture argument: {parameter.Name}");
                arguments[parameter.Name!] = value;
                return value;
            }).ToArray();
            Coordinator = (ProviderSettingsCoordinator)constructor.Invoke(values);
            foreach (var role in new[] { "alpha", "beta", "gamma", "delta", "narrator" })
            {
                var rolePicker = ComboBox(role + "RoleModelText");
                rolePicker.Tag = role;
                rolePicker.IsEditable = true;
            }
            TextBox("providerBaseUrlText").Text = "http://127.0.0.1:4321/v1";
            TextBox("providerTimeoutText").Text = "5";
            TextBox("downloadModelText").Text = "fixture-model";
            Password.Password = "fixture-token-a";
            var picker = ComboBox("providerApiModePicker");
            picker.Items.Add(new ComboBoxItem { Tag = mode, Content = mode });
            picker.SelectedIndex = 0;
            ComboBox("providerModelText").IsEditable = true;
            ComboBox("providerModelText").Text = "fixture-model";
        }

        public void ApplySnapshot(ArenaSnapshot snapshot)
        {
            Store.SaveSnapshotAsync(snapshot, SessionA.Id).GetAwaiter().GetResult();
            var persisted = Store.LoadSnapshotAsync(SessionA.Id).GetAwaiter().GetResult()!;
            RenderedSnapshot = SnapshotViewMapper.FromCore(SessionA, persisted);
            Coordinator.ApplySnapshot(RenderedSnapshot);
            TextBox("providerTimeoutText").Text = RenderedSnapshot.ProviderTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public void Dispose()
        {
            client.Dispose();
            operationLock.Dispose();
            DeleteProviderCatalogTestRoot(root);
        }
    }
}
