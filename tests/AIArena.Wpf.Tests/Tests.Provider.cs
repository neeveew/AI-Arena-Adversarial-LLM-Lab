using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using System.Collections;
using System.Runtime.ExceptionServices;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


internal static partial class Program
{
static void ProviderOperationsPublishCausalUniversalStatuses()
{
    var center = new ApplicationStatusCenter();
    var identity = new ApplicationStatusIdentity("session-provider", "connection-provider");
    center.SetContext(identity);
    var publisher = new ProviderOperationStatusPublisher(center, () => identity);

    var test = publisher.Begin("provider.test", "Testing provider...");
    Require(center.Primary.Key == "provider.test", "provider operation status should use its stable operation key");
    Require(center.Primary.Source == "Provider", "provider operation status should identify the Provider source");
    Require(center.Primary.State == ApplicationStatusState.Running, "provider operation status should begin as running");
    Require(center.Primary.Identity == identity, "provider operation status should retain session and connection identity");
    Require(center.Primary.NavigationTarget == "provider", "provider operation status should navigate back to provider settings");
    Require(test.Update("Waiting for completion...", progress: 50), "current provider receipt should accept causal progress");
    Require(test.Complete("Provider test passed."), "current provider receipt should complete");
    Require(!test.Fail("Late provider failure."), "terminal provider operation wrappers should reject a second outcome");
    Require(center.History.Single(entry => entry.Key == "provider.test").State == ApplicationStatusState.Succeeded, "provider completion should project a typed success");

    var routingTransitions = new List<ApplicationStatusState>();
    center.Changed += (_, change) =>
    {
        var routing = change.Snapshot.History.FirstOrDefault(entry => entry.Key == "provider.routing-save");
        if (routing is not null)
        {
            routingTransitions.Add(routing.State);
        }
    };
    var routingSave = publisher.BeginIf(
        true,
        "provider.routing-save",
        "Saving provider routing...",
        identity: new ApplicationStatusIdentity(identity.SessionId));
    Require(routingSave.Complete("Provider routing saved."), "direct routing save should complete its causal status");
    Require(
        routingTransitions.SequenceEqual([ApplicationStatusState.Running, ApplicationStatusState.Succeeded]),
        "direct routing save should publish one Running to Succeeded status sequence");

    var changingProviderSave = publisher.BeginIf(
        true,
        "provider.routing-save",
        "Saving a replacement provider...",
        identity: new ApplicationStatusIdentity(identity.SessionId));
    center.SetContext(new ApplicationStatusIdentity(identity.SessionId, "replacement-provider"));
    Require(changingProviderSave.Complete("Replacement provider saved."),
        "provider-mutating saves should use session-only identity and survive the provider fingerprint change they cause");

    var historyBeforeSuppressedSave = center.History.Count;
    var suppressedRoutingSave = publisher.BeginIf(
        false,
        "provider.routing-save",
        "Saving provider routing...");
    Require(suppressedRoutingSave.Complete("Provider routing saved."), "suppressed nested routing status should remain a harmless causal scope");
    Require(
        center.History.Count == historyBeforeSuppressedSave,
        "nested quick-setup and auto-config routing saves should not add a duplicate status entry");

    var failedRoutingSave = publisher.BeginIf(
        true,
        "provider.routing-save",
        "Saving provider routing...",
        identity: new ApplicationStatusIdentity(identity.SessionId));
    Require(failedRoutingSave.Fail("Provider routing save failed."), "routing save failures should terminate the current receipt");
    Require(center.Primary.State == ApplicationStatusState.Failed, "routing save exceptions and validation failures should project Failed");
    center.Resolve("provider.routing-save");

    var cancelledRoutingSave = publisher.BeginIf(
        true,
        "provider.routing-save",
        "Saving provider routing...",
        identity: new ApplicationStatusIdentity(identity.SessionId));
    Require(cancelledRoutingSave.Cancel("Provider routing save cancelled."), "routing save cancellation should terminate the current receipt");
    Require(
        center.History.Where(entry => entry.Key == "provider.routing-save")
            .MaxBy(entry => entry.Generation)?.State == ApplicationStatusState.Cancelled,
        "routing save cancellation should project Cancelled rather than Failed");

    var preload = publisher.Begin("provider.models.preload", "Preloading models...");
    center.SetContext(new ApplicationStatusIdentity("replacement-session", "replacement-connection"));
    Require(!preload.Fail("Late preload failure."), "session/provider replacement should reject stale provider completion");

    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    foreach (var name in new[] { "ProviderTestStatus", "AutoConfigureStatusText", "PreloadModelsStatusText", "DownloadModelStatusText" })
    {
        Require(
            XamlStartTag(xaml, name, "TextBlock").Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
            $"{name} should keep detailed local output without duplicating the universal live announcement");
    }

    var mainWindowSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs");
    Require(
        mainWindowSource.Contains("statusCenter: ShellTopBar.Presentation.StatusCenter", StringComparison.Ordinal),
        "MainWindow should inject the canonical application status center into provider settings");

    var coordinatorSource = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs");
    foreach (var key in new[]
    {
        "provider.test",
        "provider.quick-setup",
        "provider.models.preload",
        "provider.models.unload",
        "provider.model.download",
        "provider.model.download-status",
        "provider.auto-configure.scan",
        "provider.auto-configure.apply",
        "provider.routing-save"
    })
    {
        Require(
            coordinatorSource.Contains($"\"{key}\"", StringComparison.Ordinal),
            $"provider settings should publish the stable {key} operation key");
    }

    Require(
        coordinatorSource.Split("suppressOperationStatus: true", StringSplitOptions.None).Length - 1 == 2,
        "only quick setup and auto-configure should suppress their nested routing-save status");
    Require(
        coordinatorSource.Contains("parentOperationStatus: status", StringComparison.Ordinal)
        && coordinatorSource.Split("if (status.IsTerminal)", StringSplitOptions.None).Length - 1 >= 2,
        "nested quick setup and auto-configure should stop truthfully when routing persistence reports an early failure");
    Require(
        coordinatorSource.Contains("FailRouting(\"Provider routing was not saved.\"", StringComparison.Ordinal)
        && coordinatorSource.Contains("? \"Provider routing was saved; follow-up work was cancelled.\"", StringComparison.Ordinal)
        && coordinatorSource.Contains("? \"Provider routing was saved, but follow-up work failed.\"", StringComparison.Ordinal),
        "routing persistence should terminate validation, cancellation, and exception outcomes truthfully");
}

static void ProviderReachabilityClampsProbeTimeout()
{
    var source = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "test-model",
        Timeout = 120,
        Temperature = 0.7,
        MaxOutputTokens = 2048,
        LastError = "previous error",
        LastLatencyMs = 99,
        LastTestOk = true
    };

    var probe = ProviderReachabilityService.HealthProbeConfig(source);

    Require(probe.Timeout == 3, "health probe timeout should clamp to 3 seconds");
    Require(probe.Model == source.Model, "health probe should preserve model");
    Require(probe.Temperature == source.Temperature, "health probe should preserve temperature");
    Require(probe.LastTestOk == source.LastTestOk, "health probe should preserve prior status");
}

static void ProviderReachabilityCopiesStatusMetadata()
{
    var source = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        Model = "test-model",
        Timeout = 60,
        Temperature = 0.4,
        MaxOutputTokens = 1024,
        LastError = "old",
        LastLatencyMs = 12,
        LastTestOk = false
    };

    var updated = ProviderReachabilityService.CopyConfigWithStatus(source, online: true, error: "", latencyMs: 42);
    var failed = ProviderReachabilityService.CopyConfigWithStatus(source, online: false, error: "completion failed", latencyMs: 77);
    var advisory = ProviderReachabilityService.CopyConfigWithStatus(source, online: true, error: "completion test required", latencyMs: 55);

    Require(updated.BaseUrl == source.BaseUrl, "status copy should preserve base URL");
    Require(updated.Model == source.Model, "status copy should preserve model");
    Require(updated.LastTestOk, "status copy should update online flag");
    Require(updated.LastLatencyMs == 42, "status copy should update latency");
    Require(updated.LastError == "", "status copy should update error");
    Require(!failed.LastTestOk, "failed completion should mark provider not ready");
    Require(failed.LastLatencyMs == 77, "failed completion should keep failure latency");
    Require(failed.LastError == "completion failed", "failed completion should preserve completion error");
    Require(advisory.LastTestOk, "reachable advisory should mark provider online");
    Require(advisory.LastError == "completion test required", "reachable advisory should preserve the provider note");
}

static void ProviderReachabilityPreservesApiMode()
{
    var source = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "local-token",
        Model = "test-model",
        Timeout = 90,
        Temperature = 0.4,
        MaxOutputTokens = 2048,
        ContextLength = 8192,
        Reasoning = "low",
        NativeStatefulChat = false,
        NativeIdleTtlSeconds = 900
    };

    var probe = ProviderReachabilityService.HealthProbeConfig(source);
    var updated = ProviderReachabilityService.CopyConfigWithStatus(source, online: true, error: "", latencyMs: 42);

    Require(probe.ApiMode == ModelProviderApiModes.LmStudioNative, "health probe should preserve API mode");
    Require(probe.ApiToken == "local-token", "health probe should preserve API token");
    Require(probe.ContextLength == 8192, "health probe should preserve context length");
    Require(probe.Reasoning == "low", "health probe should preserve reasoning mode");
    Require(!probe.NativeStatefulChat, "health probe should preserve native stateful chat setting");
    Require(probe.NativeIdleTtlSeconds == 900, "health probe should preserve native idle TTL");
    Require(updated.ApiMode == ModelProviderApiModes.LmStudioNative, "status copy should preserve API mode");
    Require(updated.ApiToken == "local-token", "status copy should preserve API token");
    Require(updated.ContextLength == 8192, "status copy should preserve context length");
    Require(updated.Reasoning == "low", "status copy should preserve reasoning mode");
    Require(!updated.NativeStatefulChat, "status copy should preserve native stateful chat setting");
    Require(updated.NativeIdleTtlSeconds == 900, "status copy should preserve native idle TTL");
}

static void ProviderReachabilityFormatsProviderSpecificOfflineHints()
{
    var ollama = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:11434/v1",
        ApiMode = ModelProviderApiModes.OllamaNative
    };
    var lmStudio = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative
    };
    var compatible = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:5000/v1",
        ApiMode = ModelProviderApiModes.OpenAiCompatible
    };

    var ollamaBase = ProviderReachabilityService.SocketProbeBaseUrl(ollama);
    var ollamaError = ProviderReachabilityService.ProviderUnreachableError(ollama, ollamaBase);
    var lmStudioError = ProviderReachabilityService.ProviderUnreachableError(lmStudio, ProviderReachabilityService.SocketProbeBaseUrl(lmStudio));
    var compatibleError = ProviderReachabilityService.ProviderUnreachableError(compatible, ProviderReachabilityService.SocketProbeBaseUrl(compatible));

    Require(ollamaBase == "http://127.0.0.1:11434/api", "Ollama socket probe display URL should use the native API base");
    Require(ollamaError.Contains("Start Ollama server", StringComparison.Ordinal), "Ollama offline hint should name Ollama");
    Require(!ollamaError.Contains("LM Studio", StringComparison.OrdinalIgnoreCase), "Ollama offline hint should not mention LM Studio");
    Require(lmStudioError.Contains("Start LM Studio server", StringComparison.Ordinal), "LM Studio offline hint should name LM Studio");
    Require(compatibleError.Contains("Start the provider server", StringComparison.Ordinal), "compatible-mode offline hint should stay provider-neutral");
}

static void ProviderReachabilityPreservesCompletionFailureDuringRefresh()
{
    var failedCompletion = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        Model = "missing-model",
        LastError = "model missing or unloaded",
        LastTestOk = false
    };
    var failedReadiness = ProviderReachabilityService.UntestedProviderReadiness(failedCompletion, modelListOk: true, modelListError: "");

    Require(failedReadiness.Online, "model-list success should mark recovered provider reachability online");
    Require(failedReadiness.Error == "model missing or unloaded", "refresh should preserve completion failure error");
    Require(failedReadiness.Status == "Provider reachable; completion test failed.", "refresh should report reachable but completion-failed status");
    Require(failedReadiness.NextInterval == TimeSpan.FromSeconds(10), "reachable completion-failed refresh can use the online cadence");

    var untested = new ModelProviderConfig
    {
        BaseUrl = failedCompletion.BaseUrl,
        ApiMode = failedCompletion.ApiMode,
        Model = failedCompletion.Model,
        LastError = "",
        LastTestOk = false
    };
    var untestedReadiness = ProviderReachabilityService.UntestedProviderReadiness(untested, modelListOk: true, modelListError: "");
    Require(untestedReadiness.Online, "model-list success should mark untested but reachable providers online");
    Require(untestedReadiness.Error.Contains("run Test connection", StringComparison.OrdinalIgnoreCase), "untested provider should ask for a completion test");

    var listFailure = ProviderReachabilityService.UntestedProviderReadiness(failedCompletion, modelListOk: false, modelListError: "models endpoint failed");
    Require(!listFailure.Online, "model-list failure should stay not ready");
    Require(listFailure.Error == "models endpoint failed", "model-list failure should surface list error");
    Require(listFailure.NextInterval == TimeSpan.FromSeconds(3), "model-list failure should use the retry cadence");
}

static void ProviderReachabilityMergesStatusAfterConcurrentSaves()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-race", Guid.NewGuid().ToString("N"));
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var initial = SessionStore.CreateDefaultSnapshot();
        sessionStore.SaveSnapshotAsync(initial).GetAwaiter().GetResult();

        var staleProbeSnapshot = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("provider race test snapshot should load");
        var interveningArenaSnapshot = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("provider race test snapshot should reload");
        interveningArenaSnapshot.Engine.LastError = "newer arena write";
        sessionStore.SaveSnapshotAsync(interveningArenaSnapshot).GetAwaiter().GetResult();

        var service = new ProviderReachabilityService(
            sessionStore,
            eventLogStore,
            new ModelProviderHealthService());
        var result = service.PersistAsync(
                "default",
                online: true,
                error: "",
                latencyMs: 42,
                status: "Provider online.",
                staleProbeSnapshot)
            .GetAwaiter()
            .GetResult();

        var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("merged provider snapshot should load");
        Require(result?.SnapshotChanged == true, "provider status retry should report the merged snapshot change");
        Require(persisted.Engine.LastError == "newer arena write", "provider status retry should preserve the intervening arena write");
        Require(persisted.Configs["shared"].LastTestOk, "provider status retry should merge the online state");
        Require(persisted.Configs["shared"].LastLatencyMs == 42, "provider status retry should merge latency");
        Require(persisted.PersistenceRevision == 3, "provider status retry should commit after the intervening revision");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderReachabilityRejectsStaleStatusAfterIdentityChanges()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-identity-race", Guid.NewGuid().ToString("N"));
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var service = new ProviderReachabilityService(
            sessionStore,
            eventLogStore,
            new ModelProviderHealthService());
        var changedIdentities = new[]
        {
            (Name: "url", BaseUrl: "http://127.0.0.1:9999/v1", ApiMode: ModelProviderApiModes.LmStudioNative, ApiToken: "token-a", Model: "model-a", InitialOnline: false),
            (Name: "mode", BaseUrl: "http://127.0.0.1:1234/v1", ApiMode: ModelProviderApiModes.OpenAiCompatible, ApiToken: "token-a", Model: "model-a", InitialOnline: true),
            (Name: "token", BaseUrl: "http://127.0.0.1:1234/v1", ApiMode: ModelProviderApiModes.LmStudioNative, ApiToken: "token-b", Model: "model-a", InitialOnline: false),
            (Name: "model", BaseUrl: "http://127.0.0.1:1234/v1", ApiMode: ModelProviderApiModes.LmStudioNative, ApiToken: "token-a", Model: "model-b", InitialOnline: true)
        };

        foreach (var changedIdentity in changedIdentities)
        {
            var sessionId = $"identity-{changedIdentity.Name}";
            var initial = SessionStore.CreateDefaultSnapshot();
            initial.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.LmStudioNative,
                ApiToken = "token-a",
                Model = "model-a",
                LastError = "",
                LastLatencyMs = changedIdentity.InitialOnline ? 11 : 0,
                LastTestOk = changedIdentity.InitialOnline
            };
            sessionStore.SaveSnapshotAsync(initial, sessionId).GetAwaiter().GetResult();

            var staleProbeSnapshot = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"{changedIdentity.Name} stale probe snapshot should load");
            var changedProviderSnapshot = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"{changedIdentity.Name} provider snapshot should reload");
            changedProviderSnapshot.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = changedIdentity.BaseUrl,
                ApiMode = changedIdentity.ApiMode,
                ApiToken = changedIdentity.ApiToken,
                Model = changedIdentity.Model,
                LastError = "",
                LastLatencyMs = 0,
                LastTestOk = false
            };
            changedProviderSnapshot.Engine.LastError = $"newer {changedIdentity.Name} provider write";
            sessionStore.SaveSnapshotAsync(changedProviderSnapshot, sessionId).GetAwaiter().GetResult();

            var staleResult = service.PersistAsync(
                    sessionId,
                    online: true,
                    error: "",
                    latencyMs: 42,
                    status: "Provider online.",
                    staleProbeSnapshot)
                .GetAwaiter()
                .GetResult();
            var persisted = sessionStore.LoadSnapshotAsync(sessionId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"{changedIdentity.Name} persisted snapshot should load");
            var provider = persisted.Configs["shared"];

            Require(staleResult is null, $"{changedIdentity.Name} identity change should discard the stale probe result");
            Require(provider.BaseUrl == changedIdentity.BaseUrl, $"{changedIdentity.Name} race should preserve the new provider URL");
            Require(provider.ApiMode == changedIdentity.ApiMode, $"{changedIdentity.Name} race should preserve the new provider mode");
            Require(provider.ApiToken == changedIdentity.ApiToken, $"{changedIdentity.Name} race should preserve the new provider token");
            Require(provider.Model == changedIdentity.Model, $"{changedIdentity.Name} race should preserve the new provider model");
            Require(!provider.LastTestOk, $"{changedIdentity.Name} identity change should remain untested after a stale online probe");
            Require(provider.LastLatencyMs == 0, $"{changedIdentity.Name} identity change should not inherit stale latency");
            Require(provider.LastError == "", $"{changedIdentity.Name} identity change should not inherit a stale error");
            Require(persisted.Engine.LastError == $"newer {changedIdentity.Name} provider write", $"{changedIdentity.Name} race should preserve unrelated concurrent state");
            Require(persisted.PersistenceRevision == 2, $"{changedIdentity.Name} stale probe should not write another snapshot revision");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderSettingsResetStaleReadinessOnIdentityChanges()
{
    var existing = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "secret",
        Model = "model-a",
        LastError = "",
        LastLatencyMs = 44,
        LastTestOk = true
    };

    Require(!ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a"), "same provider identity should preserve ready status");
    Require(ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "MODEL-A"), "case-only model ID changes should be treated as a different provider identity");
    Require(ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:9999/v1", ModelProviderApiModes.LmStudioNative, "secret", "model-a"), "base URL change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:1234/v1", ModelProviderApiModes.OpenAiCompatible, "secret", "model-a"), "API mode change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "other-token", "model-a"), "API token change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderIdentityChanged(existing, "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret", "model-b"), "model change should reset provider readiness");
    Require(!ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a", 0, "", true, 0), "same native payload should preserve ready status");
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "MODEL-A", 0, "", true, 0), "case-only model ID changes should reset readiness because provider model identifiers can be case-sensitive");
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a", 8192, "", true, 0), "native context change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a", 0, "high", true, 0), "native reasoning change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a", 0, "", false, 0), "native stateful chat change should reset provider readiness");
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(existing, "http://127.0.0.1:1234/v1", "lmstudio", "secret", "model-a", 0, "", true, 300), "native idle TTL change should reset provider readiness");

    var configs = new Dictionary<string, ModelProviderConfig>(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = existing
    };
    var shared = new ModelProviderConfig
    {
        BaseUrl = existing.BaseUrl,
        ApiMode = existing.ApiMode,
        ApiToken = existing.ApiToken,
        Model = "default-model",
        LastError = existing.LastError,
        LastLatencyMs = existing.LastLatencyMs,
        LastTestOk = existing.LastTestOk
    };

    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "model-b", shared);
    Require(!configs["alpha"].LastTestOk, "role model identity change should clear stale ready status");
    Require(configs["alpha"].LastLatencyMs == 0, "role model identity change should clear stale latency");
    Require(configs["alpha"].LastError == "", "role model identity change should clear stale error");

    configs["alpha"] = existing;
    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "model-a", shared);
    Require(configs["alpha"].LastTestOk, "unchanged role model identity should preserve ready status");
    Require(configs["alpha"].LastLatencyMs == 44, "unchanged role model identity should preserve latency");

    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "model-a", shared, 1.4, 9000);
    Require(Math.Abs(configs["alpha"].Temperature - 1.4) < 0.0001, "role temperature override should persist on the role config");
    Require(configs["alpha"].MaxOutputTokens == 9000, "role max output override should persist on the role config");

    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "model-a", shared);
    Require(Math.Abs(configs["alpha"].Temperature - shared.Temperature) < 0.0001, "saving without overrides should fall back to shared temperature");
    Require(configs["alpha"].MaxOutputTokens == shared.MaxOutputTokens, "saving without overrides should fall back to shared max output");

    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "", shared, 1.1, null);
    Require(configs["alpha"].Model == "default-model", "override without explicit role model should pin the shared model");
    Require(Math.Abs(configs["alpha"].Temperature - 1.1) < 0.0001, "override without explicit role model should persist temperature");

    ProviderSettingsCoordinator.SaveRoleModelConfig(configs, "alpha", "", shared);
    Require(!configs.ContainsKey("alpha"), "blank role model without overrides should remove the role config");

    Require(ProviderSettingsCoordinator.ResolveRoleModelForDiagnostic("explicit-model", "shared-model", false) == "explicit-model",
        "all-role diagnostics should retain an explicit role model while the default is disabled");
    Require(ProviderSettingsCoordinator.ResolveRoleModelForDiagnostic("", "shared-model", true) == "shared-model",
        "all-role diagnostics should use the shared model for an inherited role while the default is enabled");
    Require(ProviderSettingsCoordinator.ResolveRoleModelForDiagnostic("", "shared-model", false) == "",
        "all-role diagnostics should report an inherited role as unassigned while the default is disabled");
}

static void ProviderRoutingKeepsCurrentNativeOptions()
{
    var existing = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "secret",
        Model = "old-model",
        Timeout = 120,
        Temperature = 0.8,
        MaxOutputTokens = 2048,
        ContextLength = 4096,
        Reasoning = "low",
        NativeStatefulChat = true,
        NativeIdleTtlSeconds = 1200,
        LastError = "old model failed",
        LastLatencyMs = 88,
        LastTestOk = true
    };

    var updated = ProviderSettingsCoordinator.ModelRoutingSharedConfig(
        existing,
        "http://127.0.0.1:1234/v1",
        ModelProviderApiModes.LmStudioNative,
        "secret",
        "new-model",
        8192,
        "high",
        nativeStatefulChat: false,
        nativeIdleTtlSeconds: 600);

    Require(updated.ContextLength == 8192, "provider routing should keep current native context length");
    Require(updated.Reasoning == "high", "provider routing should keep current reasoning mode");
    Require(!updated.NativeStatefulChat, "provider routing should keep current stateful chat toggle");
    Require(updated.NativeIdleTtlSeconds == 600, "provider routing should keep current native idle TTL");
    Require(updated.Timeout == existing.Timeout, "provider routing should preserve timeout");
    Require(updated.Temperature == existing.Temperature, "provider routing should preserve temperature");
    Require(updated.MaxOutputTokens == existing.MaxOutputTokens, "provider routing should preserve max output");
    Require(!updated.LastTestOk, "model identity change should clear stale readiness");
    Require(updated.LastLatencyMs == 0, "model identity change should clear stale latency");
    Require(updated.LastError == "", "model identity change should clear stale error");

    var roleConfigs = new Dictionary<string, ModelProviderConfig>(StringComparer.OrdinalIgnoreCase);
    ProviderSettingsCoordinator.SaveRoleModelConfig(roleConfigs, "alpha", "alpha-model", updated);
    Require(roleConfigs["alpha"].ContextLength == 8192, "role routing should inherit current native context length");
    Require(roleConfigs["alpha"].Reasoning == "high", "role routing should inherit current reasoning mode");
    Require(!roleConfigs["alpha"].NativeStatefulChat, "role routing should inherit current stateful chat toggle");
    Require(roleConfigs["alpha"].NativeIdleTtlSeconds == 600, "role routing should inherit current native idle TTL");

    Require(ProviderSettingsCoordinator.TryNormalizeProviderContextLength("", out var blankContext), "blank native context should be accepted");
    Require(blankContext == 0, "blank native context should use provider default");
    Require(ProviderSettingsCoordinator.TryNormalizeProviderContextLength("8192", out var parsedContext), "numeric native context should be accepted");
    Require(parsedContext == 8192, "numeric native context should be preserved");
    Require(ProviderSettingsCoordinator.TryNormalizeProviderContextLength("-1", out var negativeContext), "negative native context should be accepted for clamping");
    Require(negativeContext == 0, "negative native context should clamp to provider default");
    Require(!ProviderSettingsCoordinator.TryNormalizeProviderContextLength("many", out var invalidContext), "invalid native context should block provider routing save");
    Require(invalidContext == 0, "invalid native context should output provider default fallback");
    Require(ProviderSettingsCoordinator.TryNormalizeProviderNativeIdleTtlSeconds("", out var blankTtl), "blank native TTL should be accepted");
    Require(blankTtl == 0, "blank native TTL should use LM Studio default");
    Require(ProviderSettingsCoordinator.TryNormalizeProviderNativeIdleTtlSeconds("3600", out var parsedTtl), "numeric native TTL should be accepted");
    Require(parsedTtl == 3600, "numeric native TTL should be preserved");
    Require(ProviderSettingsCoordinator.TryNormalizeProviderNativeIdleTtlSeconds("-1", out var negativeTtl), "negative native TTL should be accepted for clamping");
    Require(negativeTtl == 0, "negative native TTL should clamp to LM Studio default");
    Require(!ProviderSettingsCoordinator.TryNormalizeProviderNativeIdleTtlSeconds("soon", out var invalidTtl), "invalid native TTL should block provider routing save");
    Require(invalidTtl == 0, "invalid native TTL should output provider default fallback");

    var clamped = ProviderSettingsCoordinator.ModelRoutingSharedConfig(
        existing,
        existing.BaseUrl,
        existing.ApiMode,
        existing.ApiToken,
        existing.Model,
        2_000_000,
        "strange",
        nativeStatefulChat: true,
        nativeIdleTtlSeconds: 999_999);
    Require(clamped.ContextLength == 1048576, "provider routing should clamp oversized native context length");
    Require(clamped.Reasoning == "", "provider routing should normalize unknown reasoning mode to provider default");
    Require(clamped.NativeIdleTtlSeconds == 86400, "provider routing should clamp oversized native idle TTL");
    Require(!clamped.LastTestOk, "native option changes should clear stale readiness");
    Require(clamped.LastLatencyMs == 0, "native option changes should clear stale latency");

    var compatibleExisting = new ModelProviderConfig
    {
        BaseUrl = existing.BaseUrl,
        ApiMode = ModelProviderApiModes.OpenAiCompatible,
        ApiToken = existing.ApiToken,
        Model = existing.Model,
        Timeout = existing.Timeout,
        Temperature = existing.Temperature,
        MaxOutputTokens = existing.MaxOutputTokens,
        ContextLength = existing.ContextLength,
        Reasoning = existing.Reasoning,
        NativeStatefulChat = existing.NativeStatefulChat,
        NativeIdleTtlSeconds = existing.NativeIdleTtlSeconds,
        LastError = existing.LastError,
        LastLatencyMs = 88,
        LastTestOk = true
    };
    Require(!ProviderSettingsCoordinator.ProviderReadinessChanged(compatibleExisting, compatibleExisting.BaseUrl, ModelProviderApiModes.OpenAiCompatible, compatibleExisting.ApiToken, compatibleExisting.Model, 8192, "high", false, 300), "native-only option changes should not matter in OpenAI-compatible mode");
    var compatibleNativeOptionChange = ProviderSettingsCoordinator.ModelRoutingSharedConfig(
        compatibleExisting,
        compatibleExisting.BaseUrl,
        compatibleExisting.ApiMode,
        compatibleExisting.ApiToken,
        compatibleExisting.Model,
        8192,
        "high",
        nativeStatefulChat: false,
        nativeIdleTtlSeconds: 600);
    Require(compatibleNativeOptionChange.LastTestOk, "OpenAI-compatible readiness should ignore native-only option changes");
    Require(compatibleNativeOptionChange.LastLatencyMs == 88, "OpenAI-compatible native-only option changes should preserve latency");

    var appliedSameProvider = ArenaSessionMutationCoordinator.AppliedSharedProviderConfig(
        existing,
        "http://127.0.0.1:1234/v1",
        ModelProviderApiModes.LmStudioNative,
        "secret",
        "old-model",
        timeout: 300,
        temperature: 0.5,
        maxOutput: 4096,
        contextLength: 4096,
        reasoning: "low",
        nativeStatefulChat: true,
        nativeIdleTtlSeconds: 1200);
    Require(appliedSameProvider.LastTestOk, "Apply Settings should preserve readiness when provider identity and native options are unchanged");
    Require(appliedSameProvider.LastLatencyMs == 88, "Apply Settings should preserve latency when provider readiness is still valid");
    Require(appliedSameProvider.Timeout == 300, "Apply Settings should keep newly entered timeout while preserving readiness");
    Require(Math.Abs(appliedSameProvider.Temperature - 0.5) < 0.001, "Apply Settings should keep newly entered temperature while preserving readiness");
    Require(appliedSameProvider.MaxOutputTokens == 4096, "Apply Settings should keep newly entered max output while preserving readiness");

    var appliedChangedProvider = ArenaSessionMutationCoordinator.AppliedSharedProviderConfig(
        existing,
        "http://127.0.0.1:1234/v1",
        ModelProviderApiModes.LmStudioNative,
        "secret",
        "new-model",
        timeout: 300,
        temperature: 0.5,
        maxOutput: 4096,
        contextLength: 4096,
        reasoning: "low",
        nativeStatefulChat: true,
        nativeIdleTtlSeconds: 1200);
    Require(!appliedChangedProvider.LastTestOk, "Apply Settings should clear stale readiness when provider model changes");
    Require(appliedChangedProvider.LastLatencyMs == 0, "Apply Settings should clear stale latency when provider model changes");
    Require(appliedChangedProvider.LastError == "", "Apply Settings should clear stale error when provider model changes");

    Require(ProviderSettingsCoordinator.NativeLifecycleAvailable(ModelProviderApiModes.LmStudioNative), "native lifecycle controls should enable in LM Studio native mode");
    Require(ProviderSettingsCoordinator.NativeLifecycleAvailable(ModelProviderApiModes.OllamaNative), "native lifecycle controls should enable in Ollama native mode");
    Require(!ProviderSettingsCoordinator.NativeLifecycleAvailable(ModelProviderApiModes.LlamaCppNative), "legacy native lifecycle controls should stay disabled when the dedicated llama.cpp capability-driven card owns those actions");
    Require(!ProviderSettingsCoordinator.NativeLifecycleAvailable(ModelProviderApiModes.OpenAiCompatible), "native lifecycle controls should disable in OpenAI-compatible mode");
    Require(ProviderSettingsCoordinator.ShouldEnableNativeOptionControls(ModelProviderApiModes.LmStudioNative), "native option inputs should enable in LM Studio native mode");
    Require(ProviderSettingsCoordinator.ShouldEnableNativeOptionControls(ModelProviderApiModes.OllamaNative), "native option inputs should enable in Ollama native mode");
    Require(!ProviderSettingsCoordinator.ShouldEnableNativeOptionControls(ModelProviderApiModes.OpenAiCompatible), "native option inputs should disable in OpenAI-compatible mode");
    Require(ProviderSettingsCoordinator.ShouldEnableDownloadStatusButton(ModelProviderApiModes.LmStudioNative, "job_123"), "download status should enable for native jobs");
    Require(!ProviderSettingsCoordinator.ShouldEnableDownloadStatusButton(ModelProviderApiModes.LmStudioNative, ""), "download status should disable without a job id");
    Require(!ProviderSettingsCoordinator.ShouldEnableDownloadStatusButton(ModelProviderApiModes.OllamaNative, "job_123"), "Ollama native should not enable LM Studio download status polling");
    Require(!ProviderSettingsCoordinator.ShouldEnableDownloadStatusButton(ModelProviderApiModes.OpenAiCompatible, "job_123"), "download status should disable outside native mode");
    var busyNativeControls = ProviderSettingsCoordinator.NativeLifecycleControlStateFor(ModelProviderApiModes.LmStudioNative, "job_123", isBusy: true);
    Require(busyNativeControls.NativeAvailable, "busy native lifecycle state should still know native mode is available");
    Require(!busyNativeControls.LifecycleControlsEnabled, "busy native lifecycle state should disable lifecycle buttons");
    Require(!busyNativeControls.DownloadStatusEnabled, "busy native lifecycle state should disable download status polling");
    Require(!busyNativeControls.NativeOptionsEnabled, "busy native lifecycle state should disable native option inputs");
    var ollamaNativeControls = ProviderSettingsCoordinator.NativeLifecycleControlStateFor(ModelProviderApiModes.OllamaNative, "job_123", isBusy: false);
    Require(ollamaNativeControls.NativeAvailable, "Ollama native lifecycle state should know native mode is available");
    Require(ollamaNativeControls.LifecycleControlsEnabled, "Ollama native lifecycle state should enable preload/unload");
    Require(ollamaNativeControls.DownloadControlsEnabled, "Ollama native lifecycle state should enable immediate model pulls");
    Require(!ollamaNativeControls.DownloadStatusEnabled, "Ollama native lifecycle state should not enable LM Studio download polling");
    Require(ollamaNativeControls.NativeOptionsEnabled, "Ollama native lifecycle state should enable native option inputs");
    Require(!ollamaNativeControls.StatefulChatEnabled, "Ollama native lifecycle state should not enable LM Studio stateful chat");
    Require(!ollamaNativeControls.QuantizationEnabled, "Ollama native lifecycle state should disable LM Studio quantization");
    var llamaCppNativeControls = ProviderSettingsCoordinator.NativeLifecycleControlStateFor(ModelProviderApiModes.LlamaCppNative, "", isBusy: false);
    Require(!llamaCppNativeControls.LifecycleControlsEnabled, "llama.cpp should not route through legacy preload or unload controls");
    Require(!llamaCppNativeControls.DownloadControlsEnabled, "llama.cpp should not expose an unimplemented model download action");
    Require(!llamaCppNativeControls.NativeOptionsEnabled, "llama.cpp startup-time runtime values should remain read-only in the legacy native-options editor");

    var ollamaExisting = new ModelProviderConfig
    {
        BaseUrl = existing.BaseUrl,
        ApiMode = ModelProviderApiModes.OllamaNative,
        ApiToken = existing.ApiToken,
        Model = existing.Model,
        Timeout = existing.Timeout,
        Temperature = existing.Temperature,
        MaxOutputTokens = existing.MaxOutputTokens,
        ContextLength = existing.ContextLength,
        Reasoning = existing.Reasoning,
        NativeStatefulChat = true,
        NativeIdleTtlSeconds = existing.NativeIdleTtlSeconds,
        LastError = existing.LastError,
        LastLatencyMs = existing.LastLatencyMs,
        LastTestOk = existing.LastTestOk
    };
    Require(ProviderSettingsCoordinator.ProviderReadinessChanged(ollamaExisting, ollamaExisting.BaseUrl, ModelProviderApiModes.OllamaNative, ollamaExisting.ApiToken, ollamaExisting.Model, 8192, "low", true, 1200), "Ollama native readiness should reset on context changes");
    Require(!ProviderSettingsCoordinator.ProviderReadinessChanged(ollamaExisting, ollamaExisting.BaseUrl, ModelProviderApiModes.OllamaNative, ollamaExisting.ApiToken, ollamaExisting.Model, ollamaExisting.ContextLength, ollamaExisting.Reasoning, false, ollamaExisting.NativeIdleTtlSeconds), "Ollama native readiness should ignore LM Studio-only stateful chat changes");
}

static void LlamaCppSettingsRefreshPreservesRouterModelDiscovery()
{
    Require(ProviderSettingsCoordinator.ShouldUseCompatibleModelListFallback(ModelProviderApiModes.LmStudioNative), "LM Studio native catalog failure should use its compatible model-list fallback");
    Require(ProviderSettingsCoordinator.ShouldUseCompatibleModelListFallback(ModelProviderApiModes.OllamaNative), "Ollama native catalog failure should use its compatible model-list fallback");
    Require(!ProviderSettingsCoordinator.ShouldUseCompatibleModelListFallback(ModelProviderApiModes.LlamaCppNative), "llama.cpp model refresh must preserve native mode so router /models is probed before /v1/models");
    Require(!ProviderSettingsCoordinator.ShouldUseCompatibleModelListFallback(ModelProviderApiModes.OpenAiCompatible), "compatible providers should not be rewritten through a native fallback path");

    var existing = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = "local-token",
        Model = "model.gguf",
        ContextLength = 4096,
        Reasoning = "",
        NativeStatefulChat = true,
        NativeIdleTtlSeconds = 0,
        LastTestOk = true
    };
    Require(
        !ProviderSettingsCoordinator.ProviderReadinessChanged(
            existing,
            existing.BaseUrl,
            existing.ApiMode,
            existing.ApiToken,
            existing.Model,
            contextLength: 32768,
            reasoning: "high",
            nativeStatefulChat: false,
            nativeIdleTtlSeconds: 600),
        "unsupported legacy native options must not invalidate llama.cpp readiness because they are not sent to llama-server");
}

static void LlamaCppRuntimeInspectionExtractsSafeObservedEvidence()
{
    const string privateModelPath = @"C:\Users\someone\private-models\qwen3-8b-Q4_K_M.gguf";
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/health" => JsonResponse("""{  "status"  :  "ok"  }"""),
            "/models" => JsonResponse("""
                {
                  "data": [{
                    "id": "C:\\Users\\someone\\private-models\\qwen3-8b-Q4_K_M.gguf",
                    "status": {
                      "value": "loaded",
                      "args": ["llama-server", "-m", "C:\\Users\\someone\\private-models\\qwen3-8b-Q4_K_M.gguf", "--ctx-size", "8192", "--gpu-layers=33", "--parallel", "2"]
                    }
                  }]
                }
                """),
            "/v1/models" => JsonResponse("""
                {
                  "data" : [ {
                    "id" : "qwen3-8b-Q4_K_M.gguf",
                    "owned_by"     :     "llamacpp",
                    "meta" : { "size_bytes" : 5368709120, "n_params" : 8000000000 }
                  } ]
                }
                """),
            "/props" => JsonResponse("""{"default_generation_settings":{"n_ctx":8192},"total_slots":2,"build_info":"llama.cpp b7000","is_sleeping":false}"""),
            "/slots" => JsonResponse("""[{"id":0,"is_processing":true,"n_ctx":8192,"next_token":{"n_decoded":24},"timings":{"predicted_per_second":48.5}},{"id":1,"is_processing":false,"n_ctx":8192}]"""),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var service = new LlamaCppRuntimeService(new HttpClient(handler));
    var snapshot = service.InspectAsync(new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = "runtime-token",
        Model = "qwen3-8b-Q4_K_M.gguf",
        Timeout = 5
    }).GetAwaiter().GetResult();

    Require(snapshot.Available && snapshot.Ready && snapshot.IsLlamaCpp, $"llama.cpp runtime should be identified and ready: {snapshot.Error}");
    Require(snapshot.RouterMode && snapshot.Loaded == true, "router model status should identify a loaded model");
    Require(snapshot.BuildInfo == "llama.cpp b7000", "llama.cpp build evidence mismatch");
    Require(snapshot.Model == "qwen3-8b-Q4_K_M.gguf", "runtime model should use a safe identifier rather than a filesystem path");
    Require(snapshot.Quantization == "Q4_K_M", "GGUF quantization should be labelled as identifier-derived evidence");
    Require(snapshot.ContextLength == 8192 && snapshot.GpuLayers == 33, "router startup arguments should expose observed context and GPU layers");
    Require(snapshot.SlotCount == 2 && snapshot.BusySlots == 1, "slot capacity evidence mismatch");
    Require(snapshot.Sleeping == false, "sleeping evidence mismatch");
    Require(snapshot.ModelSizeBytes == 5368709120 && snapshot.ParameterCount == 8000000000, "model metadata evidence mismatch");
    Require(snapshot.MemoryBytes is null, "runtime inspection must not invent memory usage from model file size");
    Require(Math.Abs(snapshot.TokensPerSecond.GetValueOrDefault() - 48.5) < 0.001, "slot throughput evidence mismatch");
    Require(snapshot.Capabilities.Health && snapshot.Capabilities.OpenAiModels && snapshot.Capabilities.Props && snapshot.Capabilities.Slots, "available runtime capabilities should be explicit");
    Require(snapshot.Capabilities.RouterModels && snapshot.Capabilities.ModelLifecycle, "router evidence should enable model lifecycle capability");
    Require(snapshot.Warnings.Count == 0, $"complete runtime evidence should not produce warnings: {string.Join(" | ", snapshot.Warnings)}");
    Require(handler.Requests.Count == 5, "runtime inspection should use the bounded health/models/props/slots probe set");
    Require(handler.Requests.Where(uri => uri.AbsolutePath is "/props" or "/slots").All(uri => uri.Query.Contains("autoload=false", StringComparison.Ordinal)), "router telemetry probes must not silently autoload a model");
    Require(handler.AuthorizationHeaders.All(header => header == "Bearer runtime-token"), "runtime probes should retain configured bearer authorization");
    var renderedEvidence = JsonSerializer.Serialize(snapshot);
    Require(!renderedEvidence.Contains(privateModelPath, StringComparison.OrdinalIgnoreCase), "runtime evidence must not retain an absolute model path");
    Require(!renderedEvidence.Contains("private-models", StringComparison.OrdinalIgnoreCase), "runtime evidence must not retain private path segments from router arguments");

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimeInspectionDegradesOptionalEndpointsIndependently()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
            "/v1/models" => JsonResponse("""
                {
                  "data": [
                    { "id": "single-model.gguf", "owned_by" :  "llama.cpp" }
                  ]
                }
                """),
            "/props" => JsonResponse("{"),
            "/slots" => new HttpResponseMessage(System.Net.HttpStatusCode.NotImplemented),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var snapshot = new LlamaCppRuntimeService(new HttpClient(handler)).InspectAsync(new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        Model = "single-model.gguf",
        Timeout = 5
    }).GetAwaiter().GetResult();

    Require(snapshot.Available && snapshot.Ready && snapshot.IsLlamaCpp, "valid health and structured owned_by evidence should survive missing optional endpoints");
    Require(!snapshot.RouterMode && snapshot.Capabilities.OpenAiModels, "single-model inventory should remain available without implying router mode");
    Require(!snapshot.Capabilities.Props && !snapshot.Capabilities.Slots && !snapshot.Capabilities.ModelLifecycle, "unreadable or unsupported optional endpoints must disable only their capabilities");
    Require(snapshot.Error == "", "optional capability failures must not become a global runtime error");
    Require(snapshot.Warnings.Any(value => value.Contains("properties returned unreadable JSON", StringComparison.OrdinalIgnoreCase)), "malformed props should produce a bounded capability warning");
    Require(snapshot.Warnings.Any(value => value.Contains("Slot telemetry is not exposed", StringComparison.OrdinalIgnoreCase)), "unsupported slots should remain explicit");

    var malformedModelsHandler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" or "/v1/models" => JsonResponse("{"),
            "/props" => JsonResponse("""{"build_info":"llama.cpp b7001","total_slots":1}"""),
            "/slots" => JsonResponse("[]"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var partial = new LlamaCppRuntimeService(new HttpClient(malformedModelsHandler)).InspectAsync(new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        Model = "configured-model.gguf",
        Timeout = 5
    }).GetAwaiter().GetResult();
    Require(partial.Available && partial.BuildInfo == "llama.cpp b7001", "valid props evidence should survive malformed model inventories");
    Require(partial.Warnings.Count(value => value.Contains("unreadable JSON", StringComparison.OrdinalIgnoreCase)) >= 2, "each malformed model capability should be reported independently");

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimeContinuesAfterOptionalTransportFailure()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
            "/v1/models" => JsonResponse("""{"data":[{"id":"transport-model.gguf","owned_by":"llamacpp"}]}"""),
            "/props" => throw new HttpRequestException("simulated props connection reset"),
            "/slots" => JsonResponse("""[{"id":0,"is_processing":true,"n_ctx":4096,"timings":{"predicted_per_second":22.5}}]"""),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });

    var snapshot = new LlamaCppRuntimeService(new HttpClient(handler)).InspectAsync(LlamaRuntimeConfig("transport-model.gguf"))
        .GetAwaiter()
        .GetResult();

    Require(snapshot.Available && snapshot.Ready, "a thrown optional props request must not discard valid health and model evidence");
    Require(!snapshot.Capabilities.Props && snapshot.Capabilities.Slots, "the failed transport should disable only props capability");
    Require(snapshot.Slots.Count == 1 && snapshot.BusySlots == 1, "slot evidence after the thrown props request should still be retained");
    Require(snapshot.Error == "", "an optional transport failure must not become a global runtime failure");
    Require(snapshot.Warnings.Any(value => value.Contains("Runtime properties unavailable", StringComparison.OrdinalIgnoreCase)
        && value.Contains("connection reset", StringComparison.OrdinalIgnoreCase)), "the thrown transport failure should remain visible as a capability warning");
    Require(handler.Requests.Last().AbsolutePath == "/slots", "inspection must continue to later probes after an optional transport exception");

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimeGivesEachOptionalProbeAnIndependentTimeout()
{
    Require(LlamaCppRuntimeService.ProbeTimeoutSeconds(0) == 1, "runtime probes should retain the one-second minimum timeout");
    Require(LlamaCppRuntimeService.ProbeTimeoutSeconds(3) == 3, "short configured timeouts should be preserved per probe");
    Require(LlamaCppRuntimeService.ProbeTimeoutSeconds(30) == 5, "long generation timeouts should not make five runtime probes block for minutes");

    var handler = new AsyncProbeHttpMessageHandler(async (request, cancellationToken) =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path == "/props")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("timed-out props probe unexpectedly resumed");
        }

        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
            "/v1/models" => JsonResponse("""{"data":[{"id":"timeout-model.gguf","owned_by":"llama.cpp"}]}"""),
            "/slots" => JsonResponse("""[{"id":0,"is_processing":false,"n_ctx":8192}]"""),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var config = LlamaRuntimeConfig("timeout-model.gguf", timeoutSeconds: 1);
    var snapshot = new LlamaCppRuntimeService(new HttpClient(handler)).InspectAsync(config)
        .GetAwaiter()
        .GetResult();

    Require(snapshot.Available && snapshot.Ready, "an optional per-probe timeout must preserve valid runtime evidence");
    Require(!snapshot.Capabilities.Props && snapshot.Capabilities.Slots, "a timed-out props probe must not cancel the later slots probe");
    Require(snapshot.Slots.Count == 1, "slot evidence should survive the earlier per-probe timeout");
    Require(snapshot.Warnings.Any(value => value.Contains("Runtime properties unavailable", StringComparison.OrdinalIgnoreCase)
        && value.Contains("Timed out", StringComparison.OrdinalIgnoreCase)), "the optional timeout should be reported as a bounded capability warning");
    Require(handler.Requests.Last().AbsolutePath == "/slots", "a per-probe timeout token must not remain canceled for subsequent endpoints");

    var callerCancellationHandler = new AsyncProbeHttpMessageHandler(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("caller-canceled runtime probe unexpectedly resumed");
    });
    using var callerCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
    try
    {
        _ = new LlamaCppRuntimeService(new HttpClient(callerCancellationHandler))
            .InspectAsync(config, callerCancellation.Token)
            .GetAwaiter()
            .GetResult();
        throw new InvalidOperationException("runtime inspection converted caller cancellation into partial evidence");
    }
    catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
    {
    }

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimeContinuesAfterOversizedOptionalResponse()
{
    CountingReadStream? oversizedStream = null;
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path == "/models")
        {
            oversizedStream = new CountingReadStream(LlamaCppRuntimeService.MaximumResponseBytes + 8192);
            var oversized = new StreamContent(oversizedStream);
            oversized.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = oversized };
        }

        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/v1/models" => JsonResponse("""{"data":[{"id":"bounded-model.gguf","owned_by":"llamacpp"}]}"""),
            "/props" => JsonResponse("""{"build_info":"llama.cpp bounded-test","total_slots":1}"""),
            "/slots" => JsonResponse("[]"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });

    var snapshot = new LlamaCppRuntimeService(new HttpClient(handler)).InspectAsync(LlamaRuntimeConfig("bounded-model.gguf"))
        .GetAwaiter()
        .GetResult();

    Require(snapshot.Available && snapshot.Ready, "an oversized optional router response must not discard later compatible evidence");
    Require(!snapshot.RouterMode && snapshot.Capabilities.OpenAiModels, "the rejected oversized router inventory should degrade independently");
    Require(snapshot.Capabilities.Props && snapshot.Capabilities.Slots, "later props and slots probes should still complete after bounded-body rejection");
    Require(snapshot.BuildInfo == "llama.cpp bounded-test", "valid props evidence after the oversized response should be retained");
    Require(snapshot.Error == "", "an oversized optional response must not become a global failure when other evidence identifies llama.cpp");
    Require(snapshot.Warnings.Any(value => value.Contains("Router model inventory unavailable", StringComparison.OrdinalIgnoreCase)
        && value.Contains("safety limit", StringComparison.OrdinalIgnoreCase)), "bounded-body rejection should remain visible as a capability warning");
    Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual(["/health", "/models", "/v1/models", "/props", "/slots"]), "oversized optional evidence must not stop the remaining bounded probe sequence");
    Require(oversizedStream is not null
            && oversizedStream.BytesRead <= LlamaCppRuntimeService.MaximumResponseBytes + 1,
        "a chunked llama.cpp response should stream and stop reading at the byte safety boundary");

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimeCapsSourceEntriesAndProjectsPartialEvidence()
{
    const int sourceCount = 300;
    var routerBody = JsonSerializer.Serialize(new
    {
        data = Enumerable.Range(0, sourceCount)
            .Select(index => new
            {
                id = $"router-{index:000}.gguf",
                status = new { value = index == 0 ? "loaded" : "unloaded" }
            })
            .ToArray()
    });
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" => JsonResponse(routerBody),
            "/v1/models" => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
            "/props" => JsonResponse("""{"build_info":"llama.cpp bounded-inventory"}"""),
            "/slots" => JsonResponse("[]"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var config = LlamaRuntimeConfig("router-000.gguf");
    var runtime = new LlamaCppRuntimeService(new HttpClient(handler))
        .InspectAsync(config)
        .GetAwaiter()
        .GetResult();
    var expectedOmitted = sourceCount - 256;

    Require(runtime.Available
            && runtime.RouterMode
            && runtime.Models.Count == 256
            && runtime.OmittedModelCount == expectedOmitted
            && runtime.Warnings.Any(value => value.Contains($"{expectedOmitted}", StringComparison.Ordinal)
                && value.Contains("omitted", StringComparison.OrdinalIgnoreCase)),
        "native llama.cpp inspection did not retain its bounded source set and exact raw omission evidence");

    var projection = new ProviderModelCatalogProjectionService();
    var projected = ProviderModelCatalogProjectionService.FromLlamaCpp(
        projection.BeginRefresh("llama-bounded-source", config),
        runtime,
        config.Model);
    Require(projected.CatalogEvidence == ProviderCatalogEvidenceState.Partial
            && projected.ResidencyEvidence == ProviderCatalogEvidenceState.Partial
            && projected.Models.Count == 256
            && projected.OmittedModelCount == expectedOmitted
            && projected.Status.Contains($"{expectedOmitted} additional catalog entries omitted", StringComparison.Ordinal),
        "native llama.cpp source omissions were not projected as truthful Partial catalog and residency evidence");

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static ModelProviderConfig LlamaRuntimeConfig(string model, int timeoutSeconds = 5)
{
    return new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        Model = model,
        Timeout = timeoutSeconds
    };
}

static void LlamaCppRuntimeLifecycleIsCapabilitySafeAndCancelable()
{
    const string privateModelPath = @"C:\Users\someone\models\router-model.gguf";
    var loadHandler = new TestHttpMessageHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"success":true}""", System.Text.Encoding.UTF8, "application/json")
    });
    var config = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:8080/v1",
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = "lifecycle-token",
        Timeout = 5
    };
    var loaded = new LlamaCppRuntimeService(new HttpClient(loadHandler))
        .LoadAsync(config, privateModelPath)
        .GetAwaiter()
        .GetResult();
    Require(loaded.Supported && loaded.Ok, $"supported router load should succeed: {loaded.Error}");
    Require(loaded.Model == "router-model.gguf", "lifecycle result must not expose an absolute model path");
    Require(loadHandler.Requests.Single().AbsolutePath == "/models/load", "load should use the official router lifecycle endpoint");
    Require(loadHandler.AuthorizationHeaders.Single() == "Bearer lifecycle-token", "lifecycle requests should retain bearer authorization");
    Require(loadHandler.Bodies.Single().Contains(privateModelPath.Replace("\\", "\\\\"), StringComparison.Ordinal), "lifecycle request should preserve the exact server model identifier");

    var mappedHandler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Post && path == "/models/load")
        {
            return JsonResponse("""{"success":true}""");
        }

        return path switch
        {
            "/health" => JsonResponse("""{"status":"ok"}"""),
            "/models" => JsonResponse("""{"data":[{"id":"C:\\Users\\someone\\models\\router-model.gguf","status":{"value":"unloaded"}}]}"""),
            "/v1/models" => JsonResponse("""{"data":[]}"""),
            "/props" => JsonResponse("""{"build_info":"llama.cpp test"}"""),
            "/slots" => JsonResponse("[]"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        };
    });
    var mappedService = new LlamaCppRuntimeService(new HttpClient(mappedHandler));
    var mappedSnapshot = mappedService.InspectAsync(config).GetAwaiter().GetResult();
    var mappedLoad = mappedService.LoadAsync(config, mappedSnapshot.Model).GetAwaiter().GetResult();
    Require(mappedLoad.Ok, "a safe displayed model id should resolve back to the inspected router identifier for lifecycle requests");
    Require(mappedHandler.Bodies.Last().Contains(privateModelPath.Replace("\\", "\\\\"), StringComparison.Ordinal), "lifecycle mapping should keep private router paths in transport only, not presentation evidence");

    var unsupportedHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    var unsupported = new LlamaCppRuntimeService(new HttpClient(unsupportedHandler))
        .UnloadAsync(config, "router-model.gguf")
        .GetAwaiter()
        .GetResult();
    Require(!unsupported.Supported && !unsupported.Ok, "missing router lifecycle endpoints should be reported as unsupported");

    var oversizedLifecycleStream = new CountingReadStream(LlamaCppRuntimeService.MaximumResponseBytes + 8192);
    var oversizedLifecycleHandler = new TestHttpMessageHandler(_ =>
    {
        var content = new StreamContent(oversizedLifecycleStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    });
    var oversizedLifecycle = new LlamaCppRuntimeService(new HttpClient(oversizedLifecycleHandler))
        .LoadAsync(config, "router-model.gguf")
        .GetAwaiter()
        .GetResult();
    Require(!oversizedLifecycle.Ok
            && oversizedLifecycle.Error.Contains("safety limit", StringComparison.OrdinalIgnoreCase)
            && oversizedLifecycleStream.BytesRead <= LlamaCppRuntimeService.MaximumResponseBytes + 1,
        "a chunked oversized llama.cpp lifecycle response should fail safely and stop reading at the byte boundary");

    var cancellationHandler = new CancellationBlockingHttpMessageHandler();
    var cancellationService = new LlamaCppRuntimeService(new HttpClient(cancellationHandler));
    using var cancellation = new CancellationTokenSource();
    var pending = cancellationService.LoadAsync(config, "router-model.gguf", cancellation.Token);
    Require(cancellationHandler.Started.Task.Wait(TimeSpan.FromSeconds(2)), "cancelable lifecycle request did not start");
    cancellation.Cancel();
    try
    {
        _ = pending.GetAwaiter().GetResult();
        throw new InvalidOperationException("llama.cpp lifecycle converted caller cancellation into an action result");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }

    static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
}

static void LlamaCppRuntimePresentationFormatsOnlyObservedEvidence()
{
    var checkedAt = new DateTimeOffset(2026, 8, 2, 10, 30, 0, TimeSpan.Zero);
    var evidence = new LlamaCppRuntimePresentationInput(
        Available: true,
        Ready: true,
        Status: "ok",
        BuildInfo: "llama.cpp b6123",
        Model: "qwen3-8b-q4_k_m.gguf",
        Quantization: "Q4_K_M",
        ContextLength: 32768,
        GpuLayers: 33,
        SlotCount: 4,
        BusySlots: 1,
        Sleeping: false,
        RouterMode: true,
        Loaded: true,
        MemoryBytes: 8L * 1024 * 1024 * 1024,
        ModelSizeBytes: 5L * 1024 * 1024 * 1024,
        ParameterCount: 8_000_000_000,
        TokensPerSecond: 51.25,
        ModelLifecycleAvailable: true,
        Warnings: ["Slot telemetry is partial."],
        Error: "",
        CheckedAt: checkedAt);
    var state = LlamaCppRuntimePresentation.FromEvidence(evidence);

    Require(state.Build == "llama.cpp b6123", "llama.cpp build evidence should be displayed verbatim");
    Require(state.Model == "qwen3-8b-q4_k_m.gguf", "llama.cpp selected model evidence should be displayed");
    Require(state.Quantization == "Q4_K_M", "llama.cpp quantization should be displayed only when observed");
    Require(state.Context == "32.8k tokens", "llama.cpp context should use deterministic compact formatting");
    Require(state.GpuLayers == "33", "llama.cpp GPU-layer evidence should be displayed");
    Require(state.Slots == "1 busy / 4 total", "llama.cpp slot evidence should distinguish busy and total slots");
    Require(state.Memory.Contains("8 GiB runtime", StringComparison.Ordinal), "binary runtime bytes should be accurately labelled as GiB runtime evidence");
    Require(state.Memory.Contains("5 GiB file", StringComparison.Ordinal), "binary model bytes should be accurately labelled as GiB file size rather than memory usage");
    Require(state.Memory.Contains("8B params", StringComparison.Ordinal), "reported parameter count should stay separately labelled");
    Require(state.Throughput == "51.3 tok/s", "llama.cpp throughput should use deterministic one-decimal formatting");
    Require(state.Warning == "Slot telemetry is partial.", "runtime capability warnings should remain visible");
    Require(state.WarningBrushKey == "BetaAccentBrush", "partial optional runtime evidence should use warning rather than failure styling");
    var saturated = LlamaCppRuntimePresentation.FromEvidence(evidence with { GpuLayers = -1, BusySlots = 4 });
    Require(saturated.GpuLayers == "All", "the llama.cpp all-GPU-layers sentinel should remain observed evidence");
    Require(saturated.Warning.Contains("All reported llama.cpp slots are busy", StringComparison.Ordinal), "observed slot saturation should surface a bounded capacity warning");

    var unavailable = LlamaCppRuntimePresentation.FromEvidence(new LlamaCppRuntimePresentationInput(
        Available: false,
        Ready: false,
        Status: "unavailable",
        BuildInfo: "",
        Model: "",
        Quantization: "",
        ContextLength: null,
        GpuLayers: null,
        SlotCount: null,
        BusySlots: null,
        Sleeping: null,
        RouterMode: null,
        Loaded: null,
        MemoryBytes: null,
        ModelSizeBytes: null,
        ParameterCount: null,
        TokensPerSecond: null,
        ModelLifecycleAvailable: false,
        Warnings: [],
        Error: "Connection refused.",
        CheckedAt: checkedAt));
    foreach (var value in new[]
             {
                 unavailable.Build,
                 unavailable.Model,
                 unavailable.Quantization,
                 unavailable.Context,
                 unavailable.GpuLayers,
                 unavailable.Slots,
                 unavailable.Memory,
                 unavailable.Throughput
             })
    {
        Require(value == LlamaCppRuntimePresentation.NotReported, "missing llama.cpp evidence must say Not reported rather than inventing a value");
    }

    Require(unavailable.Warning.Contains("Connection refused.", StringComparison.Ordinal), "inspection failure should remain visible as explicit evidence");
    Require(unavailable.WarningBrushKey == "DangerTextBrush", "unavailable runtime evidence should retain failure styling");
}

static void LlamaCppRuntimePresentationGatesRouterLifecycleActions()
{
    LlamaCppRuntimePresentationInput Evidence(bool available, bool router, bool lifecycle, bool? loaded, string model = "model.gguf") => new(
        available,
        available,
        available ? "ok" : "unavailable",
        "",
        model,
        "",
        null,
        null,
        null,
        null,
        null,
        router,
        loaded,
        null,
        null,
        null,
        null,
        lifecycle,
        [],
        "",
        DateTimeOffset.UtcNow);

    var unloaded = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, true, true, false));
    Require(unloaded.PreloadEnabled && !unloaded.UnloadEnabled, "an inspected unloaded router model should enable only preload");
    var loaded = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, true, true, true));
    Require(!loaded.PreloadEnabled && loaded.UnloadEnabled, "an inspected loaded router model should enable only unload");
    var noRouter = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, false, true, false));
    Require(!noRouter.PreloadEnabled && !noRouter.UnloadEnabled, "single-model llama-server should not imply router lifecycle support");
    var unsupported = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, true, false, false));
    Require(!unsupported.PreloadEnabled && !unsupported.UnloadEnabled, "missing lifecycle capability should disable both model actions");
    var noModel = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, true, true, false, ""));
    Require(!noModel.PreloadEnabled && !noModel.UnloadEnabled, "lifecycle actions should require an observed selected model");
    var busy = LlamaCppRuntimePresentation.FromEvidence(Evidence(true, true, true, true), isBusy: true);
    Require(!busy.InspectEnabled && !busy.ReconnectEnabled && !busy.PreloadEnabled && !busy.UnloadEnabled, "all llama.cpp controls should disable while another operation is running");
    Require(LlamaCppRuntimePresentation.IsVisible(ModelProviderApiModes.LlamaCppNative), "runtime card should show in llama.cpp native mode");
    Require(!LlamaCppRuntimePresentation.IsVisible(ModelProviderApiModes.OpenAiCompatible), "runtime card should stay hidden in generic compatible mode");
}

static void LlamaCppRuntimePublishesCausalUniversalStatuses()
{
    RunStaTest(() =>
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        var config = LlamaRuntimeConfig("router-model.gguf");
        var center = new ApplicationStatusCenter();
        ApplicationStatusIdentity StatusIdentity(ModelProviderConfig value) => new(
            "llama-session",
            ProviderModelCatalogProjectionService.ConnectionFingerprint("llama-session", value));
        center.SetContext(StatusIdentity(config));
        var loaded = false;
        var handler = new AsyncProbeHttpMessageHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Post && path == "/models/load")
            {
                loaded = true;
                return Task.FromResult(JsonResponse("""{"success":true}"""));
            }

            if (request.Method == HttpMethod.Post && path == "/models/unload")
            {
                loaded = false;
                return Task.FromResult(JsonResponse("""{"success":true}"""));
            }

            return Task.FromResult(path switch
            {
                "/health" => JsonResponse("""{"status":"ok"}"""),
                "/models" => JsonResponse(JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "router-model.gguf",
                            status = new { value = loaded ? "loaded" : "unloaded" }
                        }
                    }
                })),
                "/v1/models" => JsonResponse("""{"data":[]}"""),
                "/props" => JsonResponse("""{"build_info":"llama.cpp status-test","total_slots":2,"default_generation_settings":{"n_ctx":8192}}"""),
                "/slots" => JsonResponse("[]"),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            });
        });
        var controls = RuntimeControls();
        var coordinator = new LlamaCppRuntimeCoordinator(
            new LlamaCppRuntimeService(new HttpClient(handler)),
            controls,
            () => config,
            () => false,
            _ => Brushes.White,
            center,
            StatusIdentity);

        AwaitOnDispatcher(coordinator.InspectAsync());
        var inspection = Latest("provider.llamacpp.inspect");
        Require(inspection.State == ApplicationStatusState.Succeeded
                && inspection.Source == "Provider"
                && inspection.Identity == StatusIdentity(config)
                && inspection.NavigationTarget == "provider",
            "llama.cpp inspection should complete one provider-scoped causal universal status");
        Require(controls.Preload.IsEnabled && !controls.Unload.IsEnabled,
            "successful inspection should retain detailed local lifecycle truth");

        AwaitOnDispatcher(coordinator.PreloadAsync());
        Require(Latest("provider.llamacpp.preload").State == ApplicationStatusState.Succeeded
                && controls.Unload.IsEnabled,
            "confirmed llama.cpp preload should complete universally and refresh local residency evidence");
        AwaitOnDispatcher(coordinator.UnloadAsync());
        Require(Latest("provider.llamacpp.unload").State == ApplicationStatusState.Succeeded
                && controls.Preload.IsEnabled,
            "confirmed llama.cpp unload should complete universally and refresh local residency evidence");

        var staleStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHealth = 0;
        var staleHandler = new AsyncProbeHttpMessageHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path == "/health" && Interlocked.Exchange(ref firstHealth, 1) == 0)
            {
                staleStarted.TrySetResult(true);
                await releaseStale.Task.WaitAsync(cancellationToken);
            }

            return path switch
            {
                "/health" => JsonResponse("""{"status":"ok"}"""),
                "/models" => JsonResponse("""{"data":[{"id":"router-model.gguf","status":{"value":"unloaded"}}]}"""),
                "/v1/models" => JsonResponse("""{"data":[]}"""),
                "/props" => JsonResponse("""{"build_info":"llama.cpp stale-test"}"""),
                "/slots" => JsonResponse("[]"),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            };
        });
        var staleControls = RuntimeControls();
        var staleCoordinator = new LlamaCppRuntimeCoordinator(
            new LlamaCppRuntimeService(new HttpClient(staleHandler)),
            staleControls,
            () => config,
            () => false,
            _ => Brushes.White,
            center,
            StatusIdentity);
        var staleInspection = staleCoordinator.InspectAsync();
        Require(staleStarted.Task.Wait(TimeSpan.FromSeconds(2)), "stale llama.cpp inspection did not start");
        config = LlamaRuntimeConfig("replacement-model.gguf");
        staleCoordinator.ConfigurationChanged();
        releaseStale.TrySetResult(true);
        AwaitOnDispatcher(staleInspection);
        Require(Latest("provider.llamacpp.inspect").State == ApplicationStatusState.Cancelled,
            "a model/configuration change should cancel rather than apply a late llama.cpp inspection outcome");
        Require(staleControls.Model.Text == LlamaCppRuntimePresentation.NotReported,
            "late llama.cpp evidence must not overwrite the reset local view after a configuration change");

        var cancellationCenter = new ApplicationStatusCenter();
        var cancellationConfig = LlamaRuntimeConfig("cancel-model.gguf");
        ApplicationStatusIdentity CancellationIdentity(ModelProviderConfig value) => new(
            "llama-cancel-session",
            ProviderModelCatalogProjectionService.ConnectionFingerprint("llama-cancel-session", value));
        cancellationCenter.SetContext(CancellationIdentity(cancellationConfig));
        var cancellationHandler = new CancellationBlockingHttpMessageHandler();
        var cancellationCoordinator = new LlamaCppRuntimeCoordinator(
            new LlamaCppRuntimeService(new HttpClient(cancellationHandler)),
            RuntimeControls(),
            () => cancellationConfig,
            () => false,
            _ => Brushes.White,
            cancellationCenter,
            CancellationIdentity);
        using var cancellation = new CancellationTokenSource();
        var pendingCancellation = cancellationCoordinator.InspectAsync(cancellation.Token);
        Require(cancellationHandler.Started.Task.Wait(TimeSpan.FromSeconds(2)), "cancelable llama.cpp status operation did not start");
        cancellation.Cancel();
        try
        {
            AwaitOnDispatcher(pendingCancellation);
            throw new InvalidOperationException("cancelled llama.cpp inspection unexpectedly completed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Require(cancellationCenter.History
                .Where(entry => entry.Key == "provider.llamacpp.inspect")
                .MaxBy(entry => entry.Generation)?.State == ApplicationStatusState.Cancelled,
            "caller cancellation should close the causal llama.cpp receipt as Cancelled");

        ApplicationStatusEntry Latest(string key) => center.History
            .Where(entry => entry.Key == key)
            .MaxBy(entry => entry.Generation)
            ?? throw new InvalidOperationException($"Missing llama.cpp status '{key}'.");

        void AwaitOnDispatcher(Task task)
        {
            if (task.IsCompleted)
            {
                task.GetAwaiter().GetResult();
                return;
            }

            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }

        static LlamaCppRuntimeControls RuntimeControls() => new(
            new Border(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new TextBlock(),
            new Border(),
            new TextBlock(),
            new Button(),
            new Button(),
            new Button(),
            new Button());

        static HttpResponseMessage JsonResponse(string body) => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    });
}

static void LlamaCppSettingsSurfaceStaysCapabilityDrivenAndAccessible()
{
    var xaml = ReadWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml");
    var coordinator = ReadWorkspaceFile("src/AIArena.Wpf/Shell/LlamaCppRuntimeCoordinator.cs");
    var providerSettings = ReadWorkspaceFile("src/AIArena.Wpf/Shell/ProviderSettingsCoordinator.cs");
    Require(xaml.Contains("Content=\"llama.cpp\" Tag=\"llama_cpp\"", StringComparison.Ordinal), "provider presets should expose llama.cpp");
    Require(xaml.Contains("Tag=\"llamacpp_native\"", StringComparison.Ordinal), "API modes should expose llama.cpp native mode");
    Require(providerSettings.Contains("http://127.0.0.1:8080/v1", StringComparison.Ordinal), "llama.cpp preset should use the documented local llama-server port");

    var cardStart = xaml.IndexOf("x:Name=\"LlamaCppRuntimeStatusCard\"", StringComparison.Ordinal);
    var localToolsStart = xaml.IndexOf("x:Name=\"ProviderLocalModelToolsExpander\"", StringComparison.Ordinal);
    var advancedStart = xaml.IndexOf("x:Name=\"ProviderAdvancedCallsExpander\"", StringComparison.Ordinal);
    Require(localToolsStart >= 0 && cardStart > localToolsStart && cardStart < advancedStart, "llama.cpp runtime evidence should live inside Local model tools");
    var cardMarkup = xaml[cardStart..advancedStart];
    foreach (var name in new[]
             {
                 "LlamaCppRuntimeBuildValue",
                 "LlamaCppRuntimeModelValue",
                 "LlamaCppRuntimeQuantizationValue",
                 "LlamaCppRuntimeContextValue",
                 "LlamaCppRuntimeGpuLayersValue",
                 "LlamaCppRuntimeSlotsValue",
                 "LlamaCppRuntimeMemoryValue",
                 "LlamaCppRuntimeThroughputValue"
             })
    {
        Require(cardMarkup.Contains($"<TextBlock x:Name=\"{name}\"", StringComparison.Ordinal), $"{name} should be read-only evidence");
        Require(!cardMarkup.Contains($"<TextBox x:Name=\"{name}\"", StringComparison.Ordinal), $"{name} must not imply editable runtime ownership");
    }

    foreach (var name in new[] { "LlamaCppInspectButton", "LlamaCppReconnectButton", "LlamaCppPreloadButton", "LlamaCppUnloadButton" })
    {
        var buttonStart = cardMarkup.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Require(buttonStart >= 0, $"{name} should exist");
        var buttonEnd = cardMarkup.IndexOf('>', buttonStart);
        var buttonMarkup = cardMarkup[buttonStart..buttonEnd];
        Require(buttonMarkup.Contains("AutomationProperties.Name=", StringComparison.Ordinal), $"{name} should expose an automation name");
        Require(buttonMarkup.Contains("ToolTip=", StringComparison.Ordinal), $"{name} should explain its operation and unsupported state");
    }

    Require(coordinator.Contains("Capabilities.ModelLifecycle", StringComparison.Ordinal), "lifecycle action enablement should consume inspected capability evidence");
    Require(coordinator.Contains("AI Arena does not start or restart the llama-server process", StringComparison.Ordinal), "reconnect help should truthfully preserve user ownership of llama-server");
    Require(coordinator.Contains("AutomationProperties.SetHelpText", StringComparison.Ordinal), "runtime evidence and actions should expose dynamic automation help");
    Require(coordinator.Contains("Model load and unload endpoints are unsupported", StringComparison.Ordinal), "unsupported lifecycle endpoints should remain explicit instead of silently failing");
    Require(coordinator.Contains("ApplicationStatusCenter statusCenter", StringComparison.Ordinal)
            && coordinator.Contains("captureStatusIdentity", StringComparison.Ordinal)
            && coordinator.Contains("provider.llamacpp.inspect", StringComparison.Ordinal)
            && coordinator.Contains("provider.llamacpp.reconnect", StringComparison.Ordinal)
            && coordinator.Contains("provider.llamacpp.preload", StringComparison.Ordinal)
            && coordinator.Contains("provider.llamacpp.unload", StringComparison.Ordinal),
        "llama.cpp user operations should publish through the canonical causal status center");
    foreach (var name in new[] { "LlamaCppRuntimeStatusText", "LlamaCppRuntimeCapacityWarningText" })
    {
        Require(XamlStartTag(xaml, name, "TextBlock").Contains("AutomationProperties.LiveSetting=\"Off\"", StringComparison.Ordinal),
            $"{name} should preserve local runtime truth without duplicating the shell status announcement");
    }
}

static void AutoConfigureLowVramSingleModel()
{
    var hardware = new HardwareProbe(
        [new GpuDeviceInfo("Tiny GPU", "unknown", 6, 1, 0)],
        16,
        8);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["large-13b-q4", "small-3b-q4", "medium-7b-q4"],
        hardware,
        "auto");

    Require(plan.ProviderOnline, "provider should stay online");
    Require(plan.Strategy == "low_vram", "auto strategy should pick low_vram");
    Require(plan.Assignments.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, "low VRAM should use one unique model");
    Require(plan.DefaultModel.Contains("3b", StringComparison.OrdinalIgnoreCase), "low VRAM should prefer smallest model");
}

static void AutoConfigureHighVramVariety()
{
    var hardware = new HardwareProbe(
        [new GpuDeviceInfo("Big GPU", "NVIDIA", 48, 4, 2)],
        96,
        24);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["model-a-3b-q4", "model-b-7b-q4", "model-c-13b-q4", "model-d-30b-q4", "embedding-model"],
        hardware,
        "max_variety");

    var uniqueModels = plan.Assignments.Select(item => item.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    Require(plan.Strategy == "max_variety", "explicit strategy should be honored");
    Require(uniqueModels >= 3, "high VRAM variety should spread across models");
    Require(plan.Models.All(model => !model.Name.Contains("embedding", StringComparison.OrdinalIgnoreCase)), "embedding model should not be used as chat candidate");
    Require(plan.Assignments.Single(item => item.Role == "Narrator").Model.Contains("3b", StringComparison.OrdinalIgnoreCase), "narrator should use smallest model");
}

static void AutoConfigurePrefersUsefulMultiGpuFit()
{
    var hardware = new HardwareProbe(
        [
            new GpuDeviceInfo("Primary GPU", "NVIDIA", 16, 1, 4),
            new GpuDeviceInfo("Secondary GPU", "AMD", 8, 1, 3)
        ],
        64,
        18);
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        ["tiny-helper-1b-q4", "useful-alpha-3b-q4", "useful-beta-4b-q4", "useful-gamma-4b-q4", "too-large-13b-q4"],
        hardware,
        "balanced");

    var assigned = plan.Assignments.Select(item => item.Model).ToArray();
    var uniqueUseful = assigned
        .Where(model => model.Contains("useful", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    Require(uniqueUseful >= 2, "multi-GPU balanced routing should use multiple useful small models");
    Require(!assigned.Take(4).Any(model => model.Contains("tiny-helper", StringComparison.OrdinalIgnoreCase)), "participant routes should not prefer the tiny helper");
    Require(!assigned.Any(model => model.Contains("too-large", StringComparison.OrdinalIgnoreCase)), "comfortable-fit routing should avoid oversized models");
    Require(plan.Warnings.Any(warning => warning.Contains("Multi-GPU", StringComparison.OrdinalIgnoreCase)), "multi-GPU guidance note should be present");
}

static void LmStudioCatalogParsesNativeModelMetadata()
{
    var models = LmStudioModelCatalogService.ParseModels(LmStudioCatalogJson());
    var llm = models.Single(model => model.Key == "google/gemma-4-26b-a4b");
    var embedding = models.Single(model => model.Type == "embedding");

    Require(models.Count == 2, "native catalog count mismatch");
    Require(llm.IsChatModel, "llm model should be chat-capable");
    Require(llm.Loaded, "loaded_instances should mark model as loaded");
    Require(llm.LoadedContextLength == 4096, "loaded context length mismatch");
    Require(llm.MaxContextLength == 262144, "max context should parse");
    Require(llm.QuantizationName == "Q4_K_M", "quantization name should parse");
    Require(llm.SizeGb is > 16 and < 18, "size bytes should convert to GB");
    Require(llm.Vision, "vision capability should parse");
    Require(llm.TrainedForToolUse, "tool-use capability should parse");
    Require(llm.ReasoningDefault == "on", "reasoning default should parse");
    Require(llm.Matches("Gemma 4 26B A4B"), "display name alias should match");
    Require(embedding.IsEmbeddingModel, "embedding type should parse");
}

static void LmStudioCatalogBoundsNativeResponseEvidence()
{
    const int extraEntries = 3;
    var catalogBody = JsonSerializer.Serialize(new
    {
        models = Enumerable.Range(0, LmStudioModelCatalogService.MaximumModelEntries + extraEntries)
            .Select(index => new
            {
                type = "llm",
                key = $"bounded-{index:0000}",
                display_name = $"Bounded {index:0000}",
                loaded_instances = Array.Empty<object>()
            })
            .ToArray()
    });
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(catalogBody, System.Text.Encoding.UTF8, "application/json")
    });
    var catalog = new LmStudioModelCatalogService(new HttpClient(handler))
        .TryLoadAsync("http://127.0.0.1:1234/v1", "bounded-secret")
        .GetAwaiter()
        .GetResult();

    Require(catalog.Ok
            && catalog.Models.Count == LmStudioModelCatalogService.MaximumModelEntries
            && catalog.OmittedModelCount == extraEntries,
        "the native reader should retain a bounded entry set and report the exact upstream omission count");
    var config = new ModelProviderConfig
    {
        BaseUrl = "http://127.0.0.1:1234/v1",
        ApiMode = ModelProviderApiModes.LmStudioNative,
        ApiToken = "bounded-secret",
        Model = "bounded-0000"
    };
    var projection = new ProviderModelCatalogProjectionService();
    var snapshot = ProviderModelCatalogProjectionService.FromLmStudio(
        projection.BeginRefresh("bounded-session", config),
        catalog,
        config.Model,
        DateTimeOffset.UnixEpoch);
    var expectedOmitted = LmStudioModelCatalogService.MaximumModelEntries
        + extraEntries
        - ProviderModelCatalogProjectionService.MaximumModelCount;
    Require(snapshot.CatalogEvidence == ProviderCatalogEvidenceState.Partial
            && snapshot.ResidencyEvidence == ProviderCatalogEvidenceState.Partial
            && snapshot.AvailableModels.Count == ProviderModelCatalogProjectionService.MaximumModelCount
            && snapshot.OmittedModelCount == expectedOmitted
            && snapshot.Status.Contains(
                $"{expectedOmitted} additional catalog entries omitted",
                StringComparison.Ordinal),
        "bounded native evidence should remain explicitly partial and report every upstream and display omission");

    var oversizedStream = new CountingReadStream(LmStudioModelCatalogService.MaximumResponseBytes + 8192);
    var oversizedHandler = new TestHttpMessageHandler(_ =>
    {
        var content = new StreamContent(oversizedStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    });
    var oversized = new LmStudioModelCatalogService(new HttpClient(oversizedHandler))
        .TryLoadAsync("http://127.0.0.1:1234/v1", "bounded-secret")
        .GetAwaiter()
        .GetResult();
    Require(!oversized.Ok
            && oversized.Models.Count == 0
            && oversized.Error.Contains("response limit", StringComparison.OrdinalIgnoreCase)
            && oversizedStream.BytesRead <= LmStudioModelCatalogService.MaximumResponseBytes + 1,
        "a chunked oversized response should stop at the byte boundary and must not become partial catalog evidence");

    var excessiveDepth = "{\"models\":[],\"nested\":"
        + new string('[', LmStudioModelCatalogService.MaximumJsonDepth + 2)
        + "0"
        + new string(']', LmStudioModelCatalogService.MaximumJsonDepth + 2)
        + "}";
    var depthHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(excessiveDepth, System.Text.Encoding.UTF8, "application/json")
    });
    var excessive = new LmStudioModelCatalogService(new HttpClient(depthHandler))
        .TryLoadAsync("http://127.0.0.1:1234/v1", "bounded-secret")
        .GetAwaiter()
        .GetResult();
    Require(!excessive.Ok && excessive.Models.Count == 0,
        "JSON beyond the native catalog depth limit should fail closed instead of publishing incomplete evidence");
}

static void OllamaCatalogParsesNativeModelMetadata()
{
    var tags = OllamaModelCatalogService.ParseTags(OllamaTagsJson());
    var running = OllamaModelCatalogService.ParseRunningModels(OllamaPsJson());
    var merged = OllamaModelCatalogService.MergeRunningModels(tags, running);
    var loaded = merged.Single(model => model.Model == "qwen3:8b");
    var local = merged.Single(model => model.Model == "llama3.2:latest");

    Require(tags.Count == 2, "Ollama tags count mismatch");
    Require(running.Count == 1, "Ollama running model count mismatch");
    Require(loaded.Loaded, "Ollama running model should be marked loaded");
    Require(loaded.ContextLength == 8192, "Ollama running context length should parse");
    Require(loaded.SizeVramGb is > 5 and < 6, "Ollama VRAM size should convert to GB");
    Require(loaded.ParameterSize == "8B", "Ollama parameter size should parse");
    Require(loaded.QuantizationLevel == "Q4_K_M", "Ollama quantization should parse");
    Require(loaded.CapabilitySummary.Contains("loaded", StringComparison.OrdinalIgnoreCase), "Ollama capability summary should include loaded state");
    Require(loaded.Tooltip().Contains("VRAM", StringComparison.OrdinalIgnoreCase), "Ollama tooltip should include VRAM detail");
    Require(!local.Loaded, "Ollama local-only model should not be marked loaded");
    Require(local.SizeGb is > 1 and < 3, "Ollama local model size should convert to GB");

    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/tags", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(OllamaTagsJson())
            };
        }

        if (path.EndsWith("/ps", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(OllamaPsJson())
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var catalog = new OllamaModelCatalogService(new HttpClient(handler))
        .TryLoadAsync("http://127.0.0.1:11434/v1", " secret-token ")
        .GetAwaiter()
        .GetResult();

    Require(catalog.Ok, $"Ollama catalog should load: {catalog.Error}");
    Require(catalog.Models.Count == 2, "Ollama catalog should merge local and running models");
    Require(catalog.LoadedCount == 1, "Ollama catalog should count loaded models");
    Require(catalog.RunningModelsOk, "Ollama running model request should be marked available");
    Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual(["/api/tags", "/api/ps"]), "Ollama catalog should call tags then ps");
    Require(handler.AuthorizationHeaders.Count(header => header == "Bearer secret-token") == 2, "Ollama catalog should send configured bearer token to both native requests");
}

static void LmStudioCatalogHandlesInvalidNativeUrl()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"models":[]}""")
    });
    var service = new LmStudioModelCatalogService(new HttpClient(handler));

    var catalog = service
        .TryLoadAsync("not a url", "")
        .GetAwaiter()
        .GetResult();

    Require(!catalog.Ok, "invalid native catalog URL should fail gracefully");
    Require(catalog.Error.Contains("Invalid", StringComparison.OrdinalIgnoreCase), "invalid native catalog URL should return a friendly error");
    Require(handler.Requests.Count == 0, "invalid native catalog URL should not issue an HTTP request");
}

static void LmStudioCatalogSendsBearerToken()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"models":[]}""")
    });
    var service = new LmStudioModelCatalogService(new HttpClient(handler));

    var catalog = service
        .TryLoadAsync("http://127.0.0.1:1234/v1", " secret-token ")
        .GetAwaiter()
        .GetResult();

    Require(catalog.Ok, "native catalog request should succeed");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "native catalog should send configured bearer token");
}

static void LmStudioProviderSurfacesNestedNativeErrors()
{
    var catalogHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":{"message":"catalog nested failure"}}""")
    });
    var catalog = new LmStudioModelCatalogService(new HttpClient(catalogHandler))
        .TryLoadAsync("http://127.0.0.1:1234/v1", "")
        .GetAwaiter()
        .GetResult();

    Require(!catalog.Ok, "native catalog should fail on HTTP errors");
    Require(catalog.Error == "catalog nested failure", "native catalog should surface nested JSON error messages");

    var downloadHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":{"message":"download nested failure"}}""")
    });
    var download = new LmStudioModelDownloadService(new HttpClient(downloadHandler))
        .StartDownloadAsync("http://127.0.0.1:1234/v1", "google/gemma-3-4b")
        .GetAwaiter()
        .GetResult();

    Require(!download.Ok, "native download should fail on HTTP errors");
    Require(download.Error == "download nested failure", "native download should surface nested JSON error messages");

    var preloadHandler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioPreloadCatalogJson())
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"preload nested failure"}}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var preloadHttpClient = new HttpClient(preloadHandler);
    var preloadResults = new ModelPreloadService(preloadHttpClient, new LmStudioModelCatalogService(preloadHttpClient))
        .PreloadAsync("http://127.0.0.1:1234/v1", ["test-chat"])
        .GetAwaiter()
        .GetResult();

    Require(preloadResults.Count == 1, "native preload should return one result");
    Require(preloadResults[0].IsFailure, "native preload should fail on HTTP errors");
    Require(preloadResults[0].Detail == "preload nested failure", "native preload should surface nested JSON error messages");
}

static void LmStudioModelDownloadStartsNativeJob()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {
          "job_id": "job_123",
          "status": "downloading",
          "model": "google/gemma-3-4b",
          "progress": 0.25
        }
        """)
    });
    var service = new LmStudioModelDownloadService(new HttpClient(handler));

    var result = service
        .StartDownloadAsync("http://127.0.0.1:1234/v1", " google/gemma-3-4b ", " Q4_K_M ", ModelProviderApiModes.LmStudioNative, " secret-token ")
        .GetAwaiter()
        .GetResult();

    Require(result.Ok, $"native download start should succeed: {result.Error}");
    Require(!result.IsComplete, "downloading job should not be complete");
    Require(result.JobId == "job_123", "download job id should parse");
    Require(result.Detail.Contains("25", StringComparison.Ordinal), "download progress percent should be included");
    Require(handler.Requests.Single().AbsolutePath == "/api/v1/models/download", "download should use native download endpoint");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "download should send configured bearer token");
    Require(handler.Bodies.Single().Contains("\"model\":\"google/gemma-3-4b\"", StringComparison.Ordinal), "download should send trimmed model id");
    Require(handler.Bodies.Single().Contains("\"quantization\":\"Q4_K_M\"", StringComparison.Ordinal), "download should send selected quantization");

    var unsupported = service
        .StartDownloadAsync("http://127.0.0.1:1234/v1", "google/gemma-3-4b", "", ModelProviderApiModes.OpenAiCompatible)
        .GetAwaiter()
        .GetResult();
    Require(!unsupported.Ok, "download should reject OpenAI-compatible mode before issuing a native request");

    var missingJob = LmStudioModelDownloadService.ParseStartResponse("""{"status":"downloading"}""", "google/gemma-3-4b", "");
    Require(!missingJob.Ok, "download start without a job id should fail instead of becoming an unpollable running job");
    Require(missingJob.Error.Contains("job_id", StringComparison.OrdinalIgnoreCase), "missing download job id error should be actionable");
}

static void LmStudioModelDownloadChecksStatus()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {
          "status": "completed",
          "downloaded_bytes": 1073741824,
          "total_size_bytes": 1073741824
        }
        """)
    });
    var service = new LmStudioModelDownloadService(new HttpClient(handler));

    var result = service
        .GetStatusAsync("http://127.0.0.1:1234/api/v1", "job/with space", "google/gemma-3-4b", "Q4_K_M", "secret-token")
        .GetAwaiter()
        .GetResult();

    Require(result.Ok, $"native download status should succeed: {result.Error}");
    Require(result.IsComplete, "completed job should be marked complete");
    Require(result.Detail.Contains("1 GB / 1 GB", StringComparison.Ordinal), "download status should include byte progress");
    Require(ProviderSettingsCoordinator.FormatDownloadStatusText(result).StartsWith("Download ready: google/gemma-3-4b.", StringComparison.Ordinal), "completed download should format as ready");
    Require(handler.Requests.Single().AbsolutePath == "/api/v1/models/download/status/job%2Fwith%20space", "download status should escape job id on native endpoint");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "download status should send configured bearer token");

    var running = LmStudioModelDownloadService.ParseStatusResponse("""{"status":"downloading","progress":0.5}""", "google/gemma-3-4b", "Q4_K_M", "job_123");
    Require(ProviderSettingsCoordinator.FormatDownloadStatusText(running).StartsWith("Download running: google/gemma-3-4b.", StringComparison.Ordinal), "running download should format as running");
    Require(ProviderSettingsCoordinator.ShouldRetainDownloadJob(running), "running download jobs with ids should remain pollable");
    foreach (var terminalStatus in new[] { "succeeded", "success", "ready", "downloaded", "done" })
    {
        var terminal = LmStudioModelDownloadService.ParseStatusResponse($$"""{"status":"{{terminalStatus}}"}""", "google/gemma-3-4b", "Q4_K_M", "job_123");
        Require(terminal.Ok && terminal.IsComplete, $"download status '{terminalStatus}' should be treated as complete");
        Require(!ProviderSettingsCoordinator.ShouldRetainDownloadJob(terminal), $"download status '{terminalStatus}' should retire the pollable job");
    }

    var failed = LmStudioModelDownloadService.ParseStatusResponse("""{"status":"failed","detail":{"message":"disk full"}}""", "google/gemma-3-4b", "Q4_K_M", "job_123");
    Require(!failed.Ok, "failed download job payload should fail");
    Require(failed.Error == "disk full", "failed download job should surface nested detail message");
    Require(!ProviderSettingsCoordinator.ShouldRetainDownloadJob(failed), "failed download jobs should not remain pollable");
    Require(!ProviderSettingsCoordinator.ShouldClearDownloadJob("", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret"), "missing job should not clear download state");
    Require(!ProviderSettingsCoordinator.ShouldClearDownloadJob("job_123", "http://127.0.0.1:1234/api/v1", ModelProviderApiModes.LmStudioNative, "secret", "http://127.0.0.1:1234/v1", "lmstudio", "secret"), "same native provider context should preserve download state");
    Require(ProviderSettingsCoordinator.ShouldClearDownloadJob("job_123", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret", "http://127.0.0.1:9999/v1", ModelProviderApiModes.LmStudioNative, "secret"), "base URL change should clear download state");
    Require(ProviderSettingsCoordinator.ShouldClearDownloadJob("job_123", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret", "http://127.0.0.1:1234/v1", ModelProviderApiModes.OpenAiCompatible, "secret"), "API mode change should clear download state");
    Require(ProviderSettingsCoordinator.ShouldClearDownloadJob("job_123", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "secret", "http://127.0.0.1:1234/v1", ModelProviderApiModes.LmStudioNative, "other"), "API token change should clear download state");
}

static void OllamaModelPullStartsNativeRequest()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
        {
          "status": "success",
          "digest": "sha256:qwen",
          "completed": 5620000000,
          "total": 5620000000
        }
        """)
    });
    var service = new OllamaModelPullService(new HttpClient(handler));

    var result = service
        .PullAsync("http://127.0.0.1:11434/v1", " qwen3:8b ", " secret-token ")
        .GetAwaiter()
        .GetResult();

    Require(result.Ok, $"Ollama pull should succeed: {result.Error}");
    Require(result.Status == "success", "Ollama pull status should parse");
    Require(result.Digest == "sha256:qwen", "Ollama pull digest should parse");
    Require(result.Detail.Contains("5.2 GB / 5.2 GB", StringComparison.Ordinal), "Ollama pull detail should include byte progress");
    Require(handler.Requests.Single().AbsolutePath == "/api/pull", "Ollama pull should use native pull endpoint");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "Ollama pull should send configured bearer token");
    Require(handler.Bodies.Single().Contains("\"model\":\"qwen3:8b\"", StringComparison.Ordinal), "Ollama pull should send trimmed model id");
    Require(handler.Bodies.Single().Contains("\"stream\":false", StringComparison.Ordinal), "Ollama pull should disable streaming for app-friendly completion");
    Require(ProviderSettingsCoordinator.FormatOllamaPullStatusText(result).StartsWith("Ollama pull ready: qwen3:8b.", StringComparison.Ordinal), "Ollama pull status text should format as ready");

    var failed = OllamaModelPullService.ParseResponse("""{"error":{"message":"pull denied"}}""", "qwen3:8b");
    Require(!failed.Ok, "Ollama pull response with nested error should fail");
    Require(failed.Error == "pull denied", "Ollama pull should surface nested error messages");
    Require(ProviderSettingsCoordinator.FormatOllamaPullStatusText(failed) == "Ollama pull failed: pull denied", "Ollama pull failure text should format cleanly");
}

static void ModelPreloadSendsBearerToken()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioPreloadCatalogJson())
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"load_time_seconds":1.25}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service
        .PreloadAsync("http://127.0.0.1:1234/v1", ["test-chat"], ModelProviderApiModes.LmStudioNative, "secret-token", contextLength: 32768, nativeIdleTtlSeconds: 1200)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "preload should return one result");
    Require(results[0].Status == "loaded", "unloaded native model should be loaded");
    Require(handler.AuthorizationHeaders.Count(header => header == "Bearer secret-token") == 2, "catalog and load requests should send configured bearer token");
    Require(handler.Bodies.Any(body => body.Contains("\"context_length\":32768", StringComparison.Ordinal)), "native preload should send configured context length");
    Require(handler.Bodies.Any(body => body.Contains("\"ttl\":1200", StringComparison.Ordinal)), "native preload should send configured idle TTL");
}

static void NativeProviderOperationsPropagateCallerCancellation()
{
    RequireCancellation(
        (client, cancellationToken) => new OllamaModelPullService(client).PullAsync(
            "http://127.0.0.1:11434/v1",
            "qwen3:8b",
            cancellationToken: cancellationToken),
        "Ollama pull");
    RequireCancellation(
        (client, cancellationToken) => new LmStudioModelDownloadService(client).StartDownloadAsync(
            "http://127.0.0.1:1234/v1",
            "model-id",
            cancellationToken: cancellationToken),
        "LM Studio download");
    RequireCancellation(
        (client, cancellationToken) => new LmStudioModelCatalogService(client).TryLoadAsync(
            "http://127.0.0.1:1234/v1",
            cancellationToken: cancellationToken),
        "LM Studio catalog");
    RequireCancellation(
        (client, cancellationToken) => new ModelPreloadService(
            client,
            new LmStudioModelCatalogService(client)).PreloadAsync(
                "http://127.0.0.1:1234/v1",
                ["model-id"],
                cancellationToken: cancellationToken),
        "model preload");

    static void RequireCancellation(
        Func<HttpClient, CancellationToken, Task> operation,
        string label)
    {
        using var handler = new CancellationBlockingHttpMessageHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = operation(client, cancellation.Token);
        Require(handler.Started.Task.Wait(TimeSpan.FromSeconds(2)), $"{label} did not start its HTTP request");
        cancellation.Cancel();
        try
        {
            task.GetAwaiter().GetResult();
            throw new InvalidOperationException($"{label} swallowed caller cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }
}

static void ProviderReachabilityPropagatesCallerCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-reachability-cancel", Guid.NewGuid().ToString("N"));
    try
    {
        var sessionStore = new SessionStore(root);
        var service = new ProviderReachabilityService(
            sessionStore,
            new EventLogStore(root),
            new ModelProviderHealthService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            service.RefreshAsync("cancelled-session", cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("provider reachability swallowed caller cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ModelPreloadCapsRequestedContextToNativeMax()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioPreloadCatalogJson())
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"load_time_seconds":1.5}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service
        .PreloadAsync("http://127.0.0.1:1234/v1", ["test-chat"], ModelProviderApiModes.LmStudioNative, "secret-token", contextLength: 65536)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "capped preload should return one result");
    Require(results[0].Status == "loaded", "unloaded native model should still load with capped context");
    Require(results[0].Detail.Contains("using 32,768", StringComparison.OrdinalIgnoreCase), "capped preload detail should explain effective context");
    Require(handler.Bodies.Any(body => body.Contains("\"context_length\":32768", StringComparison.Ordinal)), "native preload should cap context length to native max");
    Require(!handler.Bodies.Any(body => body.Contains("\"context_length\":65536", StringComparison.Ordinal)), "native preload should not send context above native max");
}

static void ModelPreloadReloadsLowContextNativeInstance()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        if (path.EndsWith("/models/unload", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"load_time_seconds":2.5}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));
    var mutationStarts = 0;

    var results = service
        .PreloadAsync(
            "http://127.0.0.1:1234/v1",
            ["google/gemma-4-26b-a4b"],
            ModelProviderApiModes.LmStudioNative,
            "secret-token",
            contextLength: 32768,
            mutationStarting: () => mutationStarts++)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "context reload should return one result");
    Require(results[0].Status == "reloaded", "low-context native instance should be reloaded");
    Require(!results[0].IsFailure, "context reload should be successful");
    Require(results[0].Detail.Contains("Reloaded from", StringComparison.OrdinalIgnoreCase), "context reload detail should explain the reload");
    Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual([
        "/api/v1/models",
        "/api/v1/models/unload",
        "/api/v1/models/load"
    ]), "context reload should list, unload the low-context instance, then load with requested context");
    Require(handler.AuthorizationHeaders.Count(header => header == "Bearer secret-token") == 3, "catalog, unload, and load should send configured bearer token");
    Require(handler.Bodies.Any(body => body.Contains("\"instance_id\":\"google/gemma-4-26b-a4b\"", StringComparison.Ordinal)), "context reload should unload the existing instance id");
    Require(handler.Bodies.Any(body => body.Contains("\"context_length\":32768", StringComparison.Ordinal)), "context reload should load with requested context length");
    Require(mutationStarts == 2, "context reload should revalidate lifecycle identity immediately before both unload and load mutations");
}

static void ModelPreloadReloadsUnknownContextNativeInstance()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioUnknownContextCatalogJson())
            };
        }

        if (path.EndsWith("/models/unload", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"load_time_seconds":1.75}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service
        .PreloadAsync("http://127.0.0.1:1234/v1", ["unknown-context-chat"], ModelProviderApiModes.LmStudioNative, "secret-token", contextLength: 16384)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "unknown-context reload should return one result");
    Require(results[0].Status == "reloaded", "unknown-context native instance should be reloaded when a context is requested");
    Require(results[0].Detail.Contains("unknown context", StringComparison.OrdinalIgnoreCase), "unknown-context reload detail should explain the uncertainty");
    Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual([
        "/api/v1/models",
        "/api/v1/models/unload",
        "/api/v1/models/load"
    ]), "unknown-context reload should list, unload the stale instance, then load with requested context");
    Require(handler.Bodies.Any(body => body.Contains("\"instance_id\":\"unknown-instance\"", StringComparison.Ordinal)), "unknown-context reload should unload the reported instance id");
    Require(handler.Bodies.Any(body => body.Contains("\"context_length\":16384", StringComparison.Ordinal)), "unknown-context reload should load with requested context length");
}

static void ModelPreloadFailsWhenLoadedInstanceIdsAreMissing()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioMissingInstanceIdCatalogJson())
            };
        }

        if (path.EndsWith("/models/unload", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            };
        }

        if (path.EndsWith("/models/load", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"load_time_seconds":1.25}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service
        .PreloadAsync("http://127.0.0.1:1234/v1", ["missing-instance-chat"], ModelProviderApiModes.LmStudioNative, "secret-token", contextLength: 32768)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "missing-instance reload should return one result");
    Require(results[0].IsFailure, "missing loaded instance ids should block reload instead of guessing");
    Require(results[0].Detail.Contains("did not provide a loaded instance id", StringComparison.OrdinalIgnoreCase), "missing instance id failure should explain the catalog issue");
    Require(handler.Requests.Select(uri => uri.AbsolutePath).SequenceEqual(["/api/v1/models"]), "missing instance ids should not call unload or load endpoints");
}

static void ModelUnloadSendsBearerToken()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        if (path.EndsWith("/models/unload", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service
        .UnloadAsync("http://127.0.0.1:1234/v1", ["google/gemma-4-26b-a4b"], ModelProviderApiModes.LmStudioNative, "secret-token")
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "unload should return one result");
    Require(results[0].Status == "unloaded", "loaded native model should be unloaded");
    Require(handler.AuthorizationHeaders.Count(header => header == "Bearer secret-token") == 2, "catalog and unload requests should send configured bearer token");
    Require(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/api/v1/models/unload", StringComparison.OrdinalIgnoreCase)), "native unload endpoint should be called");
    Require(handler.Bodies.Any(body => body.Contains("\"instance_id\":\"google/gemma-4-26b-a4b\"", StringComparison.Ordinal)), "native unload should send loaded instance id");

    var aliasHandler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? """
                  {"models":[
                    {"type":"llm","key":"alpha","display_name":"left","loaded_instances":[]},
                    {"type":"llm","key":"beta","display_name":"right","loaded_instances":[{"id":"instance-beta"}]},
                    {"type":"llm","key":"bridge","selected_variant":"left","display_name":"right","loaded_instances":[]}
                  ]}
                  """
                : "{}")
        };
    });
    using var aliasClient = new HttpClient(aliasHandler);
    var aliasService = new ModelPreloadService(aliasClient, new LmStudioModelCatalogService(aliasClient));
    var aliasResults = aliasService.UnloadAsync(
            "http://127.0.0.1:1234/v1",
            ["alpha"],
            ModelProviderApiModes.LmStudioNative,
            "secret-token")
        .GetAwaiter()
        .GetResult();
    Require(aliasResults.Single().Status == "unloaded"
            && aliasHandler.Requests.Select(uri => uri.AbsolutePath)
                .SequenceEqual(["/api/v1/models", "/api/v1/models/unload"])
            && aliasHandler.Bodies.Any(body => body.Contains("\"instance_id\":\"instance-beta\"", StringComparison.Ordinal)),
        "unloading a deduplicated canonical alias did not target the loaded equivalent instance");

    var guardedHandler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"models":[{"type":"llm","key":"two-instance-model","loaded_instances":[{"id":"instance-one"},{"id":"instance-two"}]}]}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };
    });
    using var guardedClient = new HttpClient(guardedHandler);
    var guardedService = new ModelPreloadService(guardedClient, new LmStudioModelCatalogService(guardedClient));
    var guardedStarts = 0;
    var contextChangeSurfaced = false;
    try
    {
        _ = guardedService.UnloadAsync(
                "http://127.0.0.1:1234/v1",
                ["two-instance-model"],
                ModelProviderApiModes.LmStudioNative,
                "secret-token",
                mutationStarting: () =>
                {
                    guardedStarts++;
                    if (guardedStarts == 2)
                    {
                        throw new InvalidOperationException("session changed");
                    }
                })
            .GetAwaiter()
            .GetResult();
    }
    catch (InvalidOperationException exception) when (exception.Message == "session changed")
    {
        contextChangeSurfaced = true;
    }

    Require(contextChangeSurfaced
            && guardedStarts == 2
            && guardedHandler.Requests.Count(uri => uri.AbsolutePath.EndsWith("/api/v1/models/unload", StringComparison.OrdinalIgnoreCase)) == 1,
        "a session change before the second instance mutation must stop the next POST while surfacing the earlier partial mutation");
}

static void ModelPreloadResolvesSanitizedLmStudioPathKey()
{
    const string providerModelPath = @"C:\LM Studio\models\coding\yi-coder.gguf";
    var selectedModel = ProviderModelCatalogProjectionService.SafeModelIdentifier(providerModelPath);
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? $$"""
                    {"models":[{"type":"llm","key":"{{providerModelPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}","loaded_instances":[]}]}
                    """
                : """{"load_time_seconds":0.25}""")
        };
    });
    using var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service.PreloadAsync(
            "http://127.0.0.1:1234/v1",
            [selectedModel],
            ModelProviderApiModes.LmStudioNative,
            requireCatalogMatch: true)
        .GetAwaiter()
        .GetResult();

    Require(results.Single().Status == "loaded", "a filename-safe selected id should resolve its LM Studio absolute-path catalog key for load");
    Require(handler.Requests.Select(uri => uri.AbsolutePath)
            .SequenceEqual(["/api/v1/models", "/api/v1/models/load"]),
        "path-key load should perform exactly one catalog request and one native load request");
    using var payload = JsonDocument.Parse(handler.Bodies.Last());
    Require(payload.RootElement.GetProperty("model").GetString() == providerModelPath,
        "path-key load must send LM Studio's exact raw provider identifier instead of its filename-safe display alias");
}

static void ModelUnloadResolvesSanitizedLmStudioPathKey()
{
    const string providerModelPath = @"C:\LM Studio\models\coding\yi-coder.gguf";
    const string loadedInstanceId = "yi-coder-instance";
    var selectedModel = ProviderModelCatalogProjectionService.SafeModelIdentifier(providerModelPath);
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? $$"""
                    {"models":[{"type":"llm","key":"{{providerModelPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}","loaded_instances":[{"id":"{{loadedInstanceId}}"}]}]}
                    """
                : "{}")
        };
    });
    using var httpClient = new HttpClient(handler);
    var service = new ModelPreloadService(httpClient, new LmStudioModelCatalogService(httpClient));

    var results = service.UnloadAsync(
            "http://127.0.0.1:1234/v1",
            [selectedModel],
            ModelProviderApiModes.LmStudioNative)
        .GetAwaiter()
        .GetResult();

    Require(results.Single().Status == "unloaded", "a filename-safe selected id should resolve its loaded LM Studio absolute-path catalog key for unload");
    Require(handler.Requests.Select(uri => uri.AbsolutePath)
            .SequenceEqual(["/api/v1/models", "/api/v1/models/unload"]),
        "path-key unload should perform exactly one catalog request and one native unload request");
    using var payload = JsonDocument.Parse(handler.Bodies.Last());
    Require(payload.RootElement.GetProperty("instance_id").GetString() == loadedInstanceId,
        "path-key unload must send LM Studio's exact loaded instance id");
}

static void ModelPreloadUsesOllamaKeepAlive()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"load_duration":250000000}""")
    });
    var service = new ModelPreloadService(new HttpClient(handler));

    var results = service
        .PreloadAsync("http://127.0.0.1:11434/v1", ["qwen3:8b"], ModelProviderApiModes.OllamaNative, "secret-token", contextLength: 32768, nativeIdleTtlSeconds: 1200)
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "Ollama preload should return one result");
    Require(results[0].Status == "loaded", "Ollama preload should report loaded");
    Require(handler.Requests.Single().AbsolutePath == "/api/generate", "Ollama preload should call /api/generate");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "Ollama preload should send configured bearer token");
    Require(handler.Bodies.Single().Contains("\"model\":\"qwen3:8b\"", StringComparison.Ordinal), "Ollama preload should send model id");
    Require(handler.Bodies.Single().Contains("\"stream\":false", StringComparison.Ordinal), "Ollama preload should disable streaming");
    Require(handler.Bodies.Single().Contains("\"keep_alive\":1200", StringComparison.Ordinal), "Ollama preload should send keep_alive");
    Require(handler.Bodies.Single().Contains("\"num_ctx\":32768", StringComparison.Ordinal), "Ollama preload should send options.num_ctx");

    var defaultTtlHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"load_duration":1}""")
    });
    var defaultTtlService = new ModelPreloadService(new HttpClient(defaultTtlHandler));
    _ = defaultTtlService
        .PreloadAsync("http://127.0.0.1:11434/api", ["llama3.2"], ModelProviderApiModes.OllamaNative)
        .GetAwaiter()
        .GetResult();
    Require(!defaultTtlHandler.Bodies.Single().Contains("keep_alive", StringComparison.Ordinal), "Ollama preload should omit keep_alive when TTL is provider default");
}

static void ModelUnloadUsesOllamaKeepAliveZero()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""{"done":true}""")
    });
    var service = new ModelPreloadService(new HttpClient(handler));

    var results = service
        .UnloadAsync("http://127.0.0.1:11434/v1", ["qwen3:8b"], ModelProviderApiModes.OllamaNative, "secret-token")
        .GetAwaiter()
        .GetResult();

    Require(results.Count == 1, "Ollama unload should return one result");
    Require(results[0].Status == "unloaded", "Ollama unload should report unloaded");
    Require(handler.Requests.Single().AbsolutePath == "/api/generate", "Ollama unload should call /api/generate");
    Require(handler.AuthorizationHeaders.Single() == "Bearer secret-token", "Ollama unload should send configured bearer token");
    Require(handler.Bodies.Single().Contains("\"stream\":false", StringComparison.Ordinal), "Ollama unload should disable streaming");
    Require(handler.Bodies.Single().Contains("\"keep_alive\":0", StringComparison.Ordinal), "Ollama unload should send explicit keep_alive zero");
}

static void ModelPreloadHandlesInvalidOllamaUrl()
{
    var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    var service = new ModelPreloadService(new HttpClient(handler));

    var preloadResults = service
        .PreloadAsync("not a url", ["qwen3:8b"], ModelProviderApiModes.OllamaNative)
        .GetAwaiter()
        .GetResult();
    var unloadResults = service
        .UnloadAsync("not a url", ["qwen3:8b"], ModelProviderApiModes.OllamaNative)
        .GetAwaiter()
        .GetResult();

    Require(preloadResults.Count == 1, "invalid Ollama preload URL should still return one result");
    Require(unloadResults.Count == 1, "invalid Ollama unload URL should still return one result");
    Require(preloadResults[0].IsFailure, "invalid Ollama preload URL should report a failed result");
    Require(unloadResults[0].IsFailure, "invalid Ollama unload URL should report a failed result");
    Require(preloadResults[0].Detail.Contains("Invalid native provider base URL", StringComparison.Ordinal), "invalid preload URL should use the friendly native URL error");
    Require(unloadResults[0].Detail.Contains("Invalid native provider base URL", StringComparison.Ordinal), "invalid unload URL should use the friendly native URL error");
    Require(handler.Requests.Count == 0, "invalid Ollama lifecycle URLs should fail before issuing HTTP requests");
}

static void AutoConfigureUsesLmStudioNativeMetadata()
{
    var hardware = new HardwareProbe(
        [new GpuDeviceInfo("Workstation GPU", "NVIDIA", 24, 4, 2)],
        64,
        18);
    var models = LmStudioModelCatalogService.ParseModels(LmStudioCatalogJson());
    var plan = ProviderAutoConfigureService.Recommend(
        "http://127.0.0.1:1234/v1",
        providerOnline: true,
        lmStudioNativeApi: true,
        models,
        hardware,
        "balanced");

    Require(plan.LmStudioNativeApi, "native API flag should stay true");
    Require(plan.Models.Count == 1, "embedding model should not be used as a chat candidate");
    Require(plan.Models[0].Loaded, "native loaded state should reach model profile");
    Require(plan.Models[0].TrainedForToolUse, "native tool capability should reach model profile");
    Require(plan.Models[0].MaxContextLength == 262144, "native max context should reach model profile");
    Require(plan.Warnings.Any(warning => warning.Contains("native metadata", StringComparison.OrdinalIgnoreCase)), "native metadata guidance should be present");
}

static void AutoConfigureNativeModeUsesNativeCatalogFirst()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":"openai models unavailable"}""")
        };
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:1234/v1", "balanced", ModelProviderApiModes.LmStudioNative)
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "native catalog should make provider online even when OpenAI-compatible model listing fails");
    Require(plan.LmStudioNativeApi, "native catalog success should keep native API mode");
    Require(plan.Models.Count == 1, "native catalog should provide chat model candidates");
    Require(plan.Models[0].MaxContextLength == 262144, "native-first detection should preserve native catalog metadata");
    Require(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase)), "native catalog endpoint should be probed");
    Require(!handler.Requests.Any(uri => uri.AbsolutePath.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)), "native mode should not require OpenAI-compatible model listing before native catalog");
}

static void AutoConfigureOllamaNativeIgnoresLmStudioDefaultEndpoint()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var port = request.RequestUri?.Port ?? 0;
        if (port == 1234 && path.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        if (port == 1234 && path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"lmstudio-compatible-chat"}]}""")
            };
        }

        if (port == 11434 && path.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(OllamaTagsJson())
            };
        }

        if (port == 11434 && path.EndsWith("/api/ps", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(OllamaPsJson())
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient),
        new OllamaModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:1234/v1", "balanced", ModelProviderApiModes.OllamaNative)
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "Ollama native catalog should make provider online even when LM Studio also responds");
    Require(plan.ApiMode == ModelProviderApiModes.OllamaNative, "Ollama native auto configure should preserve Ollama API mode");
    Require(!plan.LmStudioNativeApi, "Ollama native auto configure should not be labeled as LM Studio native");
    Require(plan.ProviderBaseUrl.Contains(":11434", StringComparison.Ordinal), "Ollama native auto configure should choose the Ollama default endpoint");
    Require(plan.Models.Any(model => model.Name.Equals("qwen3:8b", StringComparison.OrdinalIgnoreCase)), "Ollama native auto configure should use Ollama catalog models");
    Require(!handler.Requests.Any(uri => uri.Port == 1234 && uri.AbsolutePath.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase)), "Ollama native mode should not probe LM Studio native catalog on port 1234");
    Require(!handler.Requests.Any(uri => uri.Port == 1234 && uri.AbsolutePath.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase)), "Ollama native mode should not accept LM Studio compatible fallback before trying Ollama");
    Require(handler.Requests.Any(uri => uri.Port == 11434 && uri.AbsolutePath.EndsWith("/api/tags", StringComparison.OrdinalIgnoreCase)), "Ollama native mode should probe Ollama's native tags endpoint");
}

static void AutoConfigureLlamaNativeStaysOnConfiguredEndpoint()
{
    Require(ProviderAutoConfigureService.ProviderModeLabel(ModelProviderApiModes.LlamaCppNative) == "llama.cpp native", "llama.cpp plans need an honest provider-mode label");
    Require(!ProviderAutoConfigureService.ShouldProbeLmStudioNative("http://127.0.0.1:1234/v1", ModelProviderApiModes.LlamaCppNative), "explicit llama.cpp mode must never opportunistically probe LM Studio native metadata");

    var handler = new TestHttpMessageHandler(request =>
    {
        var port = request.RequestUri?.Port ?? 0;
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (port == 8080 && path == "/models")
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"llama-router-8b-q4.gguf","status":{"value":"unloaded"}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        }

        if (port == 1234)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"wrong-lm-studio-model"}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient),
        new OllamaModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:8080/v1", "balanced", ModelProviderApiModes.LlamaCppNative)
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "the configured llama.cpp router inventory should make auto configure available");
    Require(plan.ApiMode == ModelProviderApiModes.LlamaCppNative, "llama.cpp auto configure must preserve the selected API mode");
    Require(plan.ProviderBaseUrl == "http://127.0.0.1:8080/v1", "llama.cpp auto configure must preserve the configured endpoint");
    Require(plan.Models.Any(model => model.Name == "llama-router-8b-q4.gguf"), "llama.cpp router models should drive recommendations");
    Require(!plan.Models.Any(model => model.Name == "wrong-lm-studio-model"), "LM Studio defaults must not bleed into explicit llama.cpp recommendations");
    Require(handler.Requests.Count > 0 && handler.Requests.All(uri => uri.Port == 8080), "explicit llama.cpp mode should probe only the configured llama-server endpoint");
    Require(
        ProviderSettingsCoordinator.AutoConfigureProviderLabel(plan) == "Provider: http://127.0.0.1:8080/v1 - llama.cpp native.",
        "auto-configure UI label should derive from plan.ApiMode instead of classifying every non-LM plan as OpenAI-compatible");
}

static void AutoConfigureDetectsLmStudioNativeCatalogOnDefaultEndpoint()
{
    Require(ProviderAutoConfigureService.ShouldProbeLmStudioNative("http://127.0.0.1:1234/v1", ModelProviderApiModes.OpenAiCompatible), "LM Studio default endpoint should opportunistically probe native catalog");
    Require(ProviderAutoConfigureService.ShouldProbeLmStudioNative("http://localhost:1234/api/v1", ModelProviderApiModes.OpenAiCompatible), "localhost native API endpoint should opportunistically probe native catalog");
    Require(!ProviderAutoConfigureService.ShouldProbeLmStudioNative("http://127.0.0.1:9999/v1", ModelProviderApiModes.OpenAiCompatible), "non-default local ports should not be assumed to be LM Studio native");
    Require(!ProviderAutoConfigureService.ShouldProbeLmStudioNative("http://127.0.0.1:1234/v1", ModelProviderApiModes.OllamaNative), "explicit Ollama native mode should not opportunistically probe LM Studio native catalog");

    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":"compatible models unavailable"}""")
        };
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:1234/v1", "balanced", ModelProviderApiModes.OpenAiCompatible)
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "default LM Studio endpoint should be online when native catalog responds");
    Require(plan.LmStudioNativeApi, "compatible-mode auto configure should upgrade to native metadata on the LM Studio default endpoint");
    Require(plan.Models.Count == 1, "default endpoint native catalog should provide chat model candidates");
    Require(plan.Models[0].TrainedForToolUse, "native catalog upgrade should preserve tool-use metadata");
    Require(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase)), "native catalog endpoint should be probed from default compatible mode");
    Require(!handler.Requests.Any(uri => uri.AbsolutePath.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)), "native catalog success should avoid compatible fallback probing");
}

static void AutoConfigureNativeFallbackUsesCompatibleMode()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"error":"native catalog unavailable"}""")
            };
        }

        if (path.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"compatible-chat-7b-q4"}]}""")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:1234/v1", "balanced", ModelProviderApiModes.LmStudioNative)
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "compatible fallback should keep auto configure useful when native catalog is unavailable");
    Require(!plan.LmStudioNativeApi, "compatible fallback should not be mislabeled as LM Studio native");
    Require(plan.Models.Count == 1, "compatible fallback should use advertised OpenAI-compatible models");
    Require(plan.Warnings.Any(warning => warning.Contains("recommendation used", StringComparison.OrdinalIgnoreCase) && warning.Contains("/v1", StringComparison.OrdinalIgnoreCase)), "compatible fallback should explain that native metadata was unavailable");
    Require(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase)), "native catalog should be attempted first");
    Require(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/v1/models", StringComparison.OrdinalIgnoreCase)), "compatible fallback should probe /v1/models");
}

static void AutoConfigureSendsBearerToken()
{
    var handler = new TestHttpMessageHandler(request =>
    {
        var authorization = request.Headers.TryGetValues("Authorization", out var values)
            ? values.SingleOrDefault()
            : "";
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/api/v1/models", StringComparison.OrdinalIgnoreCase)
            && authorization == "Bearer secret-token")
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(LmStudioCatalogJson())
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"missing bearer token"}""")
        };
    });
    var httpClient = new HttpClient(handler);
    var service = new ProviderAutoConfigureService(
        new ModelProviderHealthService(httpClient),
        new LmStudioModelCatalogService(httpClient));

    var plan = service
        .DetectAsync("http://127.0.0.1:1234/v1", "balanced", ModelProviderApiModes.LmStudioNative, " secret-token ")
        .GetAwaiter()
        .GetResult();

    Require(plan.ProviderOnline, "token-protected native catalog should be available during auto configure");
    Require(handler.AuthorizationHeaders.Any(header => header == "Bearer secret-token"), "auto configure should send configured bearer token");
}

static void ProviderConfigurationControlAppliesAtomicSecretSafePatches()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-control", Guid.NewGuid().ToString("N"));
    const string oldToken = "old-provider-control-secret";
    const string newToken = "new-provider-control-secret";
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var shared = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = oldToken,
            Model = "shared-model",
            Timeout = 90,
            Temperature = 0.7,
            MaxOutputTokens = 4096,
            ContextLength = 16384,
            Reasoning = "low",
            NativeStatefulChat = false,
            NativeIdleTtlSeconds = 600,
            LastError = $"Bearer {oldToken}",
            LastLatencyMs = 37,
            LastTestOk = true
        };
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = shared;
        ProviderConfigurationControlService.SaveRoleModelConfig(
            snapshot.Configs,
            "alpha",
            "alpha-specialist",
            shared,
            temperatureOverride: 1.35,
            maxOutputTokensOverride: 7777);
        sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult().Single();
        var refreshFlags = new List<bool>();
        using var operationLock = new SemaphoreSlim(1, 1);
        var service = new ProviderConfigurationControlService(
            sessionStore,
            eventLogStore,
            operationLock,
            () => active,
            () => false,
            (_, refreshModels, _) =>
            {
                refreshFlags.Add(refreshModels);
                return Task.CompletedTask;
            });

        var invalidPatches = new (string Label, AIArenaProviderConfigurationPatch Patch)[]
        {
            ("empty", ProviderControlPatch()),
            ("non-http URL", ProviderControlPatch(baseUrl: "file:///tmp/provider")),
            ("URL credentials", ProviderControlPatch(baseUrl: "https://user:pass@example.test/v1")),
            ("URL query", ProviderControlPatch(baseUrl: "https://example.test/v1?token=secret")),
            ("unsupported API mode", ProviderControlPatch(apiMode: "invented")),
            ("token set and clear", ProviderControlPatch(apiToken: "secret", clearApiToken: true)),
            ("token line break", ProviderControlPatch(apiToken: "secret\nvalue")),
            ("unknown role", ProviderControlPatch(roleModels: new Dictionary<string, string> { ["judge"] = "model" })),
            ("timeout", ProviderControlPatch(timeoutSeconds: 0)),
            ("temperature", ProviderControlPatch(temperature: double.PositiveInfinity)),
            ("max output", ProviderControlPatch(maxOutputTokens: 32769)),
            ("context", ProviderControlPatch(contextLength: -1)),
            ("reasoning", ProviderControlPatch(reasoning: "extreme")),
            ("native TTL", ProviderControlPatch(nativeIdleTtlSeconds: 86401))
        };
        foreach (var invalid in invalidPatches)
        {
            var validation = ProviderConfigurationControlService.Validate(invalid.Patch);
            Require(!validation.Ok && !string.IsNullOrWhiteSpace(validation.ErrorCode), $"provider configuration should reject {invalid.Label}");
        }

        var beforeRejected = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("provider configuration snapshot should load before rejection");
        var rejected = service.ApplyAsync(invalidPatches[0].Patch).GetAwaiter().GetResult();
        var afterRejected = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("provider configuration snapshot should load after rejection");
        Require(!rejected.Ok && rejected.ErrorCode == "missing_argument", "empty provider configuration patches should fail through the service boundary");
        Require(afterRejected.PersistenceRevision == beforeRejected.PersistenceRevision && refreshFlags.Count == 0, "invalid provider configuration should not save or refresh");

        var applied = service.ApplyAsync(ProviderControlPatch(
                apiToken: newToken,
                timeoutSeconds: 222,
                temperature: 0.55,
                roleModels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gamma"] = "gamma-specialist"
                },
                refreshModels: true))
            .GetAwaiter()
            .GetResult();
        var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("patched provider configuration should persist");
        var persistedShared = persisted.Configs[ModelProviderRouting.SharedConfigKey];
        Require(applied.Ok, "valid provider partial patch should succeed");
        Require(persisted.PersistenceRevision == beforeRejected.PersistenceRevision + 1, "one provider partial patch should commit exactly one snapshot revision");
        Require(persistedShared.BaseUrl == shared.BaseUrl
            && persistedShared.ApiMode == shared.ApiMode
            && persistedShared.Model == shared.Model
            && persistedShared.ContextLength == shared.ContextLength
            && persistedShared.Reasoning == shared.Reasoning
            && persistedShared.NativeStatefulChat == shared.NativeStatefulChat
            && persistedShared.NativeIdleTtlSeconds == shared.NativeIdleTtlSeconds, "omitted provider fields should survive an atomic partial patch");
        Require(persistedShared.ApiToken == newToken && persistedShared.Timeout == 222 && Math.Abs(persistedShared.Temperature - 0.55) < 0.000001, "provider partial patch should apply every supplied shared field together");
        Require(persisted.Configs["alpha"].Model == "alpha-specialist"
            && Math.Abs(persisted.Configs["alpha"].Temperature - 1.35) < 0.000001
            && persisted.Configs["alpha"].MaxOutputTokens == 7777, "provider partial patch should preserve an untouched role model and its generation overrides");
        Require(persisted.Configs["alpha"].ApiToken == newToken
            && persisted.Configs["gamma"].ApiToken == newToken
            && persisted.Configs["gamma"].Model == "gamma-specialist", "token and role updates should propagate through the complete routing snapshot");
        var alphaState = applied.State.Roles.Single(role => role.Id == "alpha");
        Require(alphaState.ConfiguredModel == "alpha-specialist"
            && alphaState.TemperatureOverride == 1.35
            && alphaState.MaxOutputTokensOverride == 7777, "provider state should report preserved role routing without credentials");
        Require(applied.State.ApiTokenConfigured && refreshFlags.SequenceEqual([true]), "provider result should expose only token presence and forward refresh intent once");
        Require(applied.ChangedFields.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["apiToken", "timeoutSeconds", "temperature", "gammaModel"]), "provider patch should audit exactly the changed fields");

        var serializedResult = AIArenaControlPlaneProtocol.Serialize(applied);
        var serializedEvent = File.ReadAllText(eventLogStore.EventPath());
        Require(!serializedResult.Contains(oldToken, StringComparison.Ordinal)
            && !serializedResult.Contains(newToken, StringComparison.Ordinal)
            && !serializedEvent.Contains(oldToken, StringComparison.Ordinal)
            && !serializedEvent.Contains(newToken, StringComparison.Ordinal), "provider responses and audit events must never serialize API token values");

        var beforeClearRevision = persisted.PersistenceRevision;
        var cleared = service.ApplyAsync(ProviderControlPatch(clearApiToken: true)).GetAwaiter().GetResult();
        var afterClear = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("cleared provider configuration should persist");
        Require(cleared.Ok && !cleared.State.ApiTokenConfigured, "provider token clear should succeed and report only the cleared presence flag");
        Require(afterClear.PersistenceRevision == beforeClearRevision + 1, "provider token clear should be one atomic save");
        Require(afterClear.Configs.Values.All(config => config.ApiToken.Length == 0), "provider token clear should remove credentials from shared and role configurations");
        Require(refreshFlags.SequenceEqual([true, false]), "provider set and clear should each refresh the host exactly once");
        Require(!AIArenaControlPlaneProtocol.Serialize(cleared).Contains(newToken, StringComparison.Ordinal), "provider clear response must not echo the removed credential");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderModelConfigurationPersistsAliasesCausallyWithoutRoutingOrResidency()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-model-runtime-settings", Guid.NewGuid().ToString("N"));
    const string rawModel = @"C:\private-model-library\Yi-Coder.gguf";
    const string safeModel = "Yi-Coder.gguf";
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        var shared = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.LmStudioNative,
            ApiToken = "model-settings-secret",
            Model = rawModel,
            Timeout = 60,
            Temperature = 0.4,
            MaxOutputTokens = 2048
        };
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = shared;
        ProviderConfigurationControlService.SaveRoleModelConfig(
            snapshot.Configs,
            "alpha",
            rawModel,
            shared,
            explicitAssignment: true);
        sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult().Single();
        var refreshes = 0;
        using var operationLock = new SemaphoreSlim(1, 1);
        var service = new ProviderConfigurationControlService(
            sessionStore,
            eventLogStore,
            operationLock,
            () => active,
            () => false,
            (_, refreshModels, _) =>
            {
                Require(!refreshModels, "ordinary model behavior saves must not request catalog or residency refresh");
                refreshes++;
                return Task.CompletedTask;
            });

        var before = service.CaptureModelConfigurationAsync(safeModel, [safeModel], CancellationToken.None).GetAwaiter().GetResult();
        var revision = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()!.PersistenceRevision;
        var saved = service.SetModelConfigurationAsync(new ProviderModelConfigurationRequest(
                safeModel,
                32768,
                ModelHistoryPolicies.Rolling80,
                ModelResponseTones.Analytical,
                "",
                before.ConfigurationIdentity,
                [safeModel]))
            .GetAwaiter()
            .GetResult();
        var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("model settings snapshot should persist");
        var rawIdentity = ModelRuntimeSettingsRegistry.Identity(shared);
        var safeIdentity = ModelRuntimeSettingsRegistry.Identity(new ModelProviderConfig
        {
            BaseUrl = shared.BaseUrl,
            ApiMode = shared.ApiMode,
            Model = safeModel
        });
        var resolved = ModelRuntimeSettingsRegistry.Resolve(persisted, persisted.Configs[ModelProviderRouting.SharedConfigKey]);
        Require(saved.Ok && persisted.PersistenceRevision == revision + 1 && refreshes == 1,
            "one causal model behavior save should produce one revision and one host refresh");
        Require(persisted.ModelSettings.ContainsKey(rawIdentity)
            && persisted.ModelSettings.ContainsKey(safeIdentity),
            "a catalog-safe alias save should register both safe and raw routed identities using opaque keys");
        Require(resolved.ConfiguredContextWindow == 32768
            && resolved.HistoryPolicy == ModelHistoryPolicies.Rolling80
            && resolved.ResponseTone == ModelResponseTones.Analytical,
            "provider routing should resolve the saved canonical settings through the raw configured path after restart");
        Require(persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == rawModel
            && persisted.Configs["alpha"].Model == rawModel
            && persisted.Configs["alpha"].ExplicitModelAssignment,
            "model behavior saves must not mutate shared or explicit role routing");
        var audit = File.ReadAllText(eventLogStore.EventPath());
        Require(!audit.Contains(rawModel, StringComparison.Ordinal)
            && !audit.Contains("model-settings-secret", StringComparison.Ordinal),
            "model behavior audit evidence must not disclose raw paths or credentials");

        var stale = service.SetModelConfigurationAsync(new ProviderModelConfigurationRequest(
                safeModel,
                65536,
                ModelHistoryPolicies.Strict,
                ModelResponseTones.Direct,
                "",
                before.ConfigurationIdentity,
                [safeModel]))
            .GetAwaiter()
            .GetResult();
        var afterStale = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("stale model settings snapshot should reload");
        Require(!stale.Ok && stale.ErrorCode == "conflict"
            && afterStale.PersistenceRevision == persisted.PersistenceRevision
            && refreshes == 1,
            "a stale per-model configuration identity must fail without saving, refreshing, routing, or residency mutation");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderRuntimeRejectsStaleProbePersistence()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-runtime-stale", Guid.NewGuid().ToString("N"));
    const string probeToken = "runtime-probe-secret";
    const string newerToken = "runtime-newer-secret";
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            ApiToken = probeToken,
            Model = "probe-model",
            Timeout = 30,
            Temperature = 0.3,
            MaxOutputTokens = 256
        };
        sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();

        var mutated = false;
        var httpHandler = new TestHttpMessageHandler(request =>
        {
            if (!mutated && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true)
            {
                mutated = true;
                var latest = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("runtime race snapshot should reload");
                var previous = latest.Configs[ModelProviderRouting.SharedConfigKey];
                latest.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = previous.BaseUrl,
                    ApiMode = previous.ApiMode,
                    ApiToken = newerToken,
                    Model = "newer-model",
                    Timeout = previous.Timeout,
                    Temperature = previous.Temperature,
                    MaxOutputTokens = previous.MaxOutputTokens,
                    ContextLength = previous.ContextLength,
                    Reasoning = previous.Reasoning,
                    NativeStatefulChat = previous.NativeStatefulChat,
                    NativeIdleTtlSeconds = previous.NativeIdleTtlSeconds
                };
                sessionStore.SaveSnapshotAsync(latest).GetAwaiter().GetResult();
            }

            var body = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = $"ok {probeToken} https://user:password@example.test/v1?api_key={probeToken}"
                        }
                    }
                }
            });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(httpHandler);
        var health = new ModelProviderHealthService(httpClient);
        var runtime = new ProviderRuntimeService(
            sessionStore,
            health,
            new ProviderReachabilityService(sessionStore, eventLogStore, health));

        var result = runtime.TestAsync("default").GetAwaiter().GetResult();
        var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("runtime stale result snapshot should load");
        Require(mutated && result.Available && result.Reachable, "provider runtime test should complete its real probe while a provider edit races it");
        Require(result.Stale && !result.Persisted && !result.Ok, "provider runtime should reject persistence for a stale provider identity");
        Require(result.Status.Contains("changed while the test was running", StringComparison.OrdinalIgnoreCase), "stale provider diagnostics should return a stable actionable status");
        Require(persisted.PersistenceRevision == 2
            && persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == "newer-model"
            && persisted.Configs[ModelProviderRouting.SharedConfigKey].ApiToken == newerToken
            && !persisted.Configs[ModelProviderRouting.SharedConfigKey].LastTestOk, "stale probe must not overwrite the newer provider configuration or add a readiness revision");
        var serialized = AIArenaControlPlaneProtocol.Serialize(result);
        Require(!serialized.Contains(probeToken, StringComparison.Ordinal)
            && !serialized.Contains("user:password", StringComparison.Ordinal)
            && !result.Reply.Contains(probeToken, StringComparison.Ordinal), "provider runtime diagnostic text should redact tokens and URL credentials");
        Require(httpHandler.AuthorizationHeaders.Single() == $"Bearer {probeToken}", "provider runtime should use the configured token for the probe without returning it");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderRuntimeTreatsTimeoutChangesAsStaleProbeIdentity()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-runtime-timeout-race", Guid.NewGuid().ToString("N"));
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            ApiToken = "timeout-race-placeholder",
            Model = "timeout-probe-model",
            Timeout = 30,
            Temperature = 0.2,
            MaxOutputTokens = 256,
            ContextLength = 4096,
            Reasoning = "low"
        };
        sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        var capturedSnapshot = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("timeout identity snapshot should load");
        var capturedIdentity = ProviderConfigurationControlService.ConfigurationIdentity(capturedSnapshot);
        var mutated = false;
        var httpHandler = new TestHttpMessageHandler(request =>
        {
            if (!mutated && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true)
            {
                mutated = true;
                var latest = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("timeout identity race snapshot should reload");
                var previous = latest.Configs[ModelProviderRouting.SharedConfigKey];
                latest.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
                {
                    BaseUrl = previous.BaseUrl,
                    ApiMode = previous.ApiMode,
                    ApiToken = previous.ApiToken,
                    Model = previous.Model,
                    Timeout = 31,
                    Temperature = previous.Temperature,
                    MaxOutputTokens = previous.MaxOutputTokens,
                    ContextLength = previous.ContextLength,
                    Reasoning = previous.Reasoning,
                    NativeStatefulChat = previous.NativeStatefulChat,
                    NativeIdleTtlSeconds = previous.NativeIdleTtlSeconds
                };
                sessionStore.SaveSnapshotAsync(latest).GetAwaiter().GetResult();
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(httpHandler);
        var health = new ModelProviderHealthService(httpClient);
        var runtime = new ProviderRuntimeService(
            sessionStore,
            health,
            new ProviderReachabilityService(sessionStore, eventLogStore, health));

        var result = runtime.TestAsync("default").GetAwaiter().GetResult();
        var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("timeout identity result snapshot should load");
        var currentIdentity = ProviderConfigurationControlService.ConfigurationIdentity(persisted);
        Require(mutated && result.Reachable, "timeout identity regression should complete the captured provider probe");
        Require(result.Stale && !result.Persisted && !result.Ok, "a timeout-only edit during completion should invalidate the captured probe result");
        Require(persisted.PersistenceRevision == 2
            && persisted.Configs[ModelProviderRouting.SharedConfigKey].Timeout == 31
            && !persisted.Configs[ModelProviderRouting.SharedConfigKey].LastTestOk, "stale timeout probe should not add a readiness save or overwrite the edited timeout");
        Require(result.ConfigurationIdentity == capturedIdentity
            && !currentIdentity.Equals(capturedIdentity, StringComparison.Ordinal), "completion results should retain their internal captured fingerprint and timeout should change the current fingerprint");
        var serialized = AIArenaControlPlaneProtocol.Serialize(result);
        Require(!serialized.Contains("ConfigurationIdentity", StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains(capturedIdentity, StringComparison.Ordinal)
            && !serialized.Contains("CapturedSessionId", StringComparison.OrdinalIgnoreCase), "runtime probe fingerprints and captured session ids must remain internal to control-plane JSON");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderRuntimeCapsAndRedactsModelCatalogs()
{
    var root = Path.Combine(Path.GetTempPath(), "ai-arena-provider-runtime-models", Guid.NewGuid().ToString("N"));
    const string apiToken = "runtime-catalog-secret";
    try
    {
        var sessionStore = new SessionStore(root);
        var eventLogStore = new EventLogStore(root);
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:1234/v1",
            ApiMode = ModelProviderApiModes.OpenAiCompatible,
            ApiToken = apiToken,
            Model = "catalog-model",
            Timeout = 30
        };
        sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
        var modelNames = Enumerable.Range(0, 300)
            .Select(index => $"catalog-{index:000}")
            .ToList();
        modelNames[0] = $"aaa-{apiToken}";
        modelNames[1] = "aab-" + new string('x', 260);
        var catalogBody = JsonSerializer.Serialize(new
        {
            data = modelNames.Select(model => new { id = model }).ToArray()
        });
        var httpHandler = new TestHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(catalogBody, System.Text.Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(httpHandler);
        var health = new ModelProviderHealthService(httpClient);
        var runtime = new ProviderRuntimeService(
            sessionStore,
            health,
            new ProviderReachabilityService(sessionStore, eventLogStore, health));

        var result = runtime.RefreshModelsAsync("default").GetAwaiter().GetResult();
        Require(result.Available && result.Ok, "provider runtime model discovery should complete against the configured endpoint");
        Require(result.ModelCount == 300 && result.Models.Count == 256 && result.Truncated, "provider runtime should report the full safe count while capping returned models at 256");
        Require(result.Models.All(model => model.Length <= 192), "provider runtime should bound every advertised model name");
        Require(result.Models.Contains("aaa-[redacted]", StringComparer.Ordinal), "provider runtime should redact the exact configured token from advertised model names");
        Require(result.Models.Single(model => model.StartsWith("aab-", StringComparison.Ordinal)).Length == 192, "provider runtime should truncate oversized advertised model names");
        Require(!AIArenaControlPlaneProtocol.Serialize(result).Contains(apiToken, StringComparison.Ordinal), "provider model discovery results must not serialize the configured token");
        Require(httpHandler.AuthorizationHeaders.Single() == $"Bearer {apiToken}", "provider model discovery should authenticate without exposing the token in its result");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ProviderSecretProtectionRewrapsAmbiguousLegacyPrefixes()
{
    const string ambiguousPlaintext = "dpapi:legitimate-token";
    var protectedEnvelope = SecretProtection.Protect(ambiguousPlaintext);
    Require(protectedEnvelope.StartsWith("dpapi:v1:", StringComparison.Ordinal)
        && protectedEnvelope != ambiguousPlaintext, "Protect should wrap ambiguous dpapi-prefixed plaintext in the unambiguous versioned envelope");
    Require(SecretProtection.Unprotect(protectedEnvelope) == ambiguousPlaintext, "a versioned provider credential envelope should round-trip for the current Windows user");
    Require(SecretProtection.Protect(protectedEnvelope) == protectedEnvelope, "Protect should remain idempotent for a valid versioned envelope");

    Require(SecretProtection.Unprotect("dpapi:not-valid-base64") == "", "a corrupt legacy provider credential envelope should fail closed");
    Require(SecretProtection.Unprotect("dpapi:v1:not-valid-base64") == "", "a corrupt versioned provider credential envelope should fail closed");
    Require(SecretProtection.Unprotect("dpapi:v1:") == "", "an empty versioned provider credential envelope should fail closed");
}

private static AIArenaProviderConfigurationPatch ProviderControlPatch(
    string? baseUrl = null,
    string? apiMode = null,
    string? apiToken = null,
    bool clearApiToken = false,
    string? model = null,
    int? timeoutSeconds = null,
    double? temperature = null,
    int? maxOutputTokens = null,
    int? contextLength = null,
    string? reasoning = null,
    bool? nativeStatefulChat = null,
    int? nativeIdleTtlSeconds = null,
    IReadOnlyDictionary<string, string>? roleModels = null,
    bool refreshModels = false)
{
    return new AIArenaProviderConfigurationPatch(
        baseUrl,
        apiMode,
        apiToken,
        clearApiToken,
        model,
        timeoutSeconds,
        temperature,
        maxOutputTokens,
        contextLength,
        reasoning,
        nativeStatefulChat,
        nativeIdleTtlSeconds,
        roleModels ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        refreshModels);
}

private sealed class AsyncProbeHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null)
        {
            Requests.Add(request.RequestUri);
        }

        return respond(request, cancellationToken);
    }
}

}
