using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ControlPlaneParsesCommandsAndStableResponses()
    {
        var json = """
            {
              "id": "abc",
              "command": "agent_send",
              "args": {
                "prompt": "Build a tiny app"
              }
            }
            """;

        Require(AIArenaControlPlaneProtocol.TryParseRequest(json, out var request, out var error), $"control-plane request should parse: {error}");
        Require(request.Id == "abc", "control-plane parser should preserve request ids");
        Require(request.Command == AIArenaControlCommands.AgentSend, "control-plane parser should normalize command names");
        Require(request.Args is not null && request.Args["prompt"].GetString() == "Build a tiny app", "control-plane parser should preserve args");
        var response = AIArenaControlResponse.Success(request, "Agent prompt sent.", new { accepted = true });
        var serialized = AIArenaControlPlaneProtocol.Serialize(response);
        Require(serialized.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase), "control-plane response schema should include ok");
        Require(serialized.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase), "control-plane response schema should include status");
        Require(serialized.Contains("\"command\":\"agent.send\"", StringComparison.OrdinalIgnoreCase), "control-plane response schema should include normalized command");
        var stateResponse = response with { State = new { View = "arena", InternetEnabled = true } };
        var stateSerialized = AIArenaControlPlaneProtocol.Serialize(stateResponse);
        Require(stateSerialized.Contains("\"state\":{", StringComparison.OrdinalIgnoreCase), "authenticated command responses should support a consistent state summary");
        Require(stateSerialized.Contains("\"view\":\"arena\"", StringComparison.OrdinalIgnoreCase), "control-plane state summary should serialize current view");

        Require(!AIArenaControlPlaneProtocol.TryParseRequest("{", out _, out var invalidError), "invalid JSON should be rejected");
        Require(invalidError.Contains("Invalid JSON", StringComparison.Ordinal), "invalid JSON should report a parse error");
        Require(!AIArenaControlPlaneProtocol.TryParseRequest("""{"id":"x"}""", out _, out var missingCommandError), "missing commands should be rejected");
        Require(missingCommandError.Contains("Command is required", StringComparison.Ordinal), "missing command errors should be stable");
    }

    static void ControlPlaneRequiresSessionToken()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var target = new FakeControlTarget();
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(target, hub, pipeName, tokenPath);
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(!string.IsNullOrWhiteSpace(host.SessionToken), "control-plane host should generate a session token when enabled");
            Require(File.Exists(tokenPath), "control-plane host should expose the session token through the debug client token file");

            var unauthorized = SendControlRequest(pipeName, """{"id":"bad","command":"status","args":{}}""");
            Require(unauthorized.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase), "missing token requests should fail");
            Require(unauthorized.Contains("\"errorCode\":\"unauthorized\"", StringComparison.OrdinalIgnoreCase), "missing token requests should return unauthorized");
            Require(target.Calls == 0, "unauthorized requests should not dispatch to the control target");

            var authorized = SendControlRequest(pipeName, $"{{\"id\":\"good\",\"command\":\"status\",\"token\":\"{host.SessionToken}\",\"args\":{{}}}}");
            Require(authorized.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase), "valid token requests should succeed");
            Require(target.Calls == 1, "valid token requests should dispatch exactly once");
        }
        finally
        {
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneRejectsOversizedRequests()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(new FakeControlTarget(), hub, pipeName, tokenPath);
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var oversized = new string('x', AIArenaControlPlaneProtocol.MaxRequestBytes + 1);
            var response = SendControlRequest(pipeName, oversized);
            Require(response.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase), "oversized requests should fail");
            Require(response.Contains("\"errorCode\":\"invalid_request\"", StringComparison.OrdinalIgnoreCase), "oversized requests should return invalid_request");
            Require(response.Contains("too large", StringComparison.OrdinalIgnoreCase), "oversized requests should report size failure");
        }
        finally
        {
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneStopDrainsActiveClients()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var target = new BlockingControlTarget();
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(target, hub, pipeName, tokenPath);
        Task<string>? clientTask = null;
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var token = host.SessionToken;
            clientTask = Task.Run(() => SendControlRequest(
                pipeName,
                $"{{\"id\":\"blocking\",\"command\":\"status\",\"token\":\"{token}\",\"args\":{{}}}}"));

            target.Started.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            Require(target.CancellationObserved.IsCompletedSuccessfully, "stopping the control plane should cancel an active control target");
            Require(target.ActiveCalls == 0, "control-plane stop should wait for active control targets to unwind");
            Require(!host.IsRunning, "control-plane host should report stopped after its accept loop and clients drain");
            Require(string.IsNullOrEmpty(host.SessionToken), "control-plane stop should clear the active session token");
            Require(!File.Exists(tokenPath), "control-plane stop should remove its session token file after clients drain");
            WaitForClientDisconnect(clientTask);
        }
        finally
        {
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (clientTask is not null)
            {
                WaitForClientDisconnect(clientTask);
            }

            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneRestartsAfterDrainedStop()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var target = new FakeControlTarget();
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(target, hub, pipeName, tokenPath);
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var firstToken = host.SessionToken;
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var secondToken = host.SessionToken;
            Require(host.IsRunning, "control-plane host should accept a new run immediately after StopAsync completes");
            Require(!string.IsNullOrWhiteSpace(secondToken), "a restarted control plane should generate a session token");
            Require(!secondToken.Equals(firstToken, StringComparison.Ordinal), "a restarted control plane should rotate its session token");

            var staleResponse = SendControlRequest(pipeName, $"{{\"id\":\"stale\",\"command\":\"status\",\"token\":\"{firstToken}\",\"args\":{{}}}}" );
            Require(staleResponse.Contains("\"errorCode\":\"unauthorized\"", StringComparison.OrdinalIgnoreCase), "a restarted control plane should reject its prior run token");
            var currentResponse = SendControlRequest(pipeName, $"{{\"id\":\"current\",\"command\":\"status\",\"token\":\"{secondToken}\",\"args\":{{}}}}" );
            Require(currentResponse.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase), "a restarted control plane should accept requests authenticated with its new token");
            Require(target.Calls == 1, "only the request authenticated for the current run should reach the target");
        }
        finally
        {
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneAsyncTransitionsDoNotBlockPendingDrain()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var target = new BlockingControlTarget(holdCancellation: true);
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(target, hub, pipeName, tokenPath);
        Task<string>? clientTask = null;
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            var firstToken = host.SessionToken;
            clientTask = Task.Run(() => SendControlRequest(
                pipeName,
                $"{{\"id\":\"pending\",\"command\":\"status\",\"token\":\"{firstToken}\",\"args\":{{}}}}"));
            target.Started.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            var stopTask = host.StopAsync();
            target.CancellationStarted.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(!stopTask.IsCompleted, "StopAsync should remain awaitable while an active UI-bound target unwinds");

            var restartTask = host.StartIfEnabledAsync();
            Require(!restartTask.IsCompleted, "StartIfEnabledAsync should yield instead of synchronously waiting for a pending stop");
            target.ReleaseCancellation();
            restartTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            Require(stopTask.IsCompletedSuccessfully, "pending control-plane stop should complete after its target unwinds");
            Require(host.IsRunning, "rapid stop/start should start a fresh control-plane run after the drain");
            Require(!host.SessionToken.Equals(firstToken, StringComparison.Ordinal), "rapid stop/start should rotate the run token");
            WaitForClientDisconnect(clientTask);
        }
        finally
        {
            target.ReleaseCancellation();
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (clientTask is not null)
            {
                WaitForClientDisconnect(clientTask);
            }

            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneStopCancelsBlockedEventWrites()
    {
        var pipeName = $"ai-arena-test-{Guid.NewGuid():N}";
        var tokenPath = Path.Combine(Path.GetTempPath(), $"ai-arena-token-{Guid.NewGuid():N}.token");
        var hub = new AIArenaControlPlaneEventHub();
        using var host = new AIArenaControlPlaneHost(new FakeControlTarget(), hub, pipeName, tokenPath);
        try
        {
            host.StartIfEnabledAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(5000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            writer.WriteLine($"{{\"id\":\"watch\",\"command\":\"events.watch\",\"token\":\"{host.SessionToken}\",\"args\":{{}}}}");
            var connected = reader.ReadLine();
            Require(connected?.Contains("events.connected", StringComparison.Ordinal) == true, "event watcher should connect before its read side is stalled");

            var largeMessage = new string('x', 4 * 1024 * 1024);
            hub.Publish(new AIArenaControlEvent("event.large", DateTimeOffset.UtcNow, largeMessage));
            Thread.Sleep(250);

            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(!host.IsRunning, "stopping should cancel an event write blocked by a non-reading client");
        }
        finally
        {
            host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
    }

    static void ControlPlaneMainWindowAwaitsLifecycleTransitions()
    {
        var mainWindow = ReadMainWindowSource();
        Require(mainWindow.Contains("private async Task RefreshControlPlaneHostAsync()", StringComparison.Ordinal), "MainWindow should use an asynchronous control-plane transition path");
        Require(mainWindow.Contains("await host.StartIfEnabledAsync();", StringComparison.Ordinal), "control-plane enable should await a pending stop without blocking the dispatcher");
        Require(mainWindow.Contains("await host.StopAsync();", StringComparison.Ordinal), "control-plane disable should asynchronously drain active clients");
        Require(mainWindow.Contains("await _controlPlaneHost.StopAsync();", StringComparison.Ordinal), "window closing should drain control-plane clients while the dispatcher can still run continuations");

        var collaborateIndex = mainWindow.IndexOf("_collaborateCoordinator = new CollaborateCoordinator", StringComparison.Ordinal);
        var diagnosticsIndex = mainWindow.IndexOf("DiagnosticsWorkflow.InitializeTiles();", StringComparison.Ordinal);
        var controlPlaneIndex = mainWindow.IndexOf("_controlPlaneHost = new AIArenaControlPlaneHost", StringComparison.Ordinal);
        Require(collaborateIndex >= 0 && diagnosticsIndex >= 0 && controlPlaneIndex > collaborateIndex && controlPlaneIndex > diagnosticsIndex,
            "control-plane startup should occur only after command coordinators and shell controls are initialized");
    }

    static void ControlPlaneEventQueueIsCapped()
    {
        var host = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaControlPlaneHost.cs"));
        var protocol = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaControlPlaneProtocol.cs"));
        Require(protocol.Contains("MaxEventQueueItems", StringComparison.Ordinal), "control-plane protocol should define a bounded event queue size");
        Require(host.Contains("events.Count < AIArenaControlPlaneProtocol.MaxEventQueueItems", StringComparison.Ordinal), "event queue should enforce the bounded size");
        Require(host.Contains("events.Dequeue();", StringComparison.Ordinal), "event queue should drop oldest events instead of growing without bound");
    }

    static void ControlPlaneEventQueueOverflowDrainsSafely()
    {
        using var queue = new AIArenaControlPlaneHost.EventQueue();
        const int overflowItems = 37;
        var totalItems = AIArenaControlPlaneProtocol.MaxEventQueueItems + overflowItems;
        for (var index = 0; index < totalItems; index++)
        {
            queue.Enqueue(new AIArenaControlEvent(
                $"event.{index}",
                DateTimeOffset.UtcNow,
                $"Event {index}."));
        }

        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var drained = new List<AIArenaControlEvent>(AIArenaControlPlaneProtocol.MaxEventQueueItems);
        for (var index = 0; index < AIArenaControlPlaneProtocol.MaxEventQueueItems; index++)
        {
            drained.Add(queue.NextAsync(drainTimeout.Token).GetAwaiter().GetResult());
        }

        Require(drained.Count == AIArenaControlPlaneProtocol.MaxEventQueueItems, "overflowed event queue should retain exactly the configured capacity");
        Require(drained[0].Type == $"event.{overflowItems}", "overflowed event queue should discard the oldest events");
        Require(drained[^1].Type == $"event.{totalItems - 1}", "overflowed event queue should retain the newest event");

        using var emptyReadTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            _ = queue.NextAsync(emptyReadTimeout.Token).GetAwaiter().GetResult();
            Require(false, "drained event queue should wait for another event");
        }
        catch (OperationCanceledException) when (emptyReadTimeout.IsCancellationRequested)
        {
            // A fully drained queue has no stale semaphore permit and remains cancellable.
        }
    }

    static void ControlPlaneDefaultsOnAndPersistsSettingsToggle()
    {
        WithTempSettingsStore(store =>
        {
            Require(new WpfSettings().EnableControlPlane, "new settings should enable the control plane by default");

            Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
            File.WriteAllText(store.SettingsPath, """{"AllowDebugControls":false,"EnableControlPlane":false}""");
            var migrated = store.Load();
            Require(migrated.EnableControlPlane, "legacy off-by-default settings should migrate to the new enabled default");
            Require(!migrated.AllowDebugControls, "control-plane migration should not enable unrelated Debug controls");
            Require(migrated.ControlPlanePreferenceVersion == 1, "control-plane migration should be recorded once");

            migrated.EnableControlPlane = false;
            store.Save(migrated);
            var disabled = store.Load();
            Require(!disabled.EnableControlPlane, "an explicit Settings toggle off should persist after migration");

            disabled.EnableControlPlane = true;
            disabled.AllowDebugControls = false;
            store.Save(disabled);
            var enabled = store.Load();
            Require(enabled.EnableControlPlane && !enabled.AllowDebugControls, "control plane should remain enabled independently of Debug controls");
        });
    }

    static void ControlPlaneEventsFormatJsonLines()
    {
        var hub = new AIArenaControlPlaneEventHub();
        AIArenaControlEvent? captured = null;
        using (hub.Subscribe(item => captured = item))
        {
            hub.Publish("command.staged", "Command staged.", new { shell = "PowerShell" });
        }

        Require(captured is not null, "control-plane event hub should notify subscribers");
        var json = captured!.ToJsonLine();
        Require(json.Contains("\"type\":\"command.staged\"", StringComparison.OrdinalIgnoreCase), "event JSON should include type");
        Require(json.Contains("\"message\":\"Command staged.\"", StringComparison.OrdinalIgnoreCase), "event JSON should include message");
        Require(!json.Contains(Environment.NewLine, StringComparison.Ordinal), "event JSON should be line-delimited");
    }

    static void ControlPlaneDispatcherInvokesOnStaDispatcher()
    {
        RunStaTest(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var invoked = false;
            var result = AIArenaControlDispatcher.InvokeAsync(
                dispatcher,
                () =>
                {
                    invoked = dispatcher.CheckAccess();
                    return Task.FromResult("ok");
                },
                CancellationToken.None).GetAwaiter().GetResult();
            Require(result == "ok", "control-plane dispatcher should return handler results");
            Require(invoked, "control-plane dispatcher should invoke handlers on the dispatcher thread");
        });
    }

    static void ControlPlaneKnownCommandRegistryCoversFirstSurface()
    {
        Require(AIArenaControlPlaneProtocol.PipeName == "ai-arena-wpf-control", "the WPF control plane must use an implementation-specific pipe namespace");
        Require(Path.GetFileName(AIArenaControlPlaneProtocol.DefaultTokenPath()).StartsWith("ai-arena-wpf-control-", StringComparison.Ordinal), "the WPF control token must not collide with another AI Arena implementation");
        Require(AIArenaControlCommands.IsKnown("capabilities"), "control-plane registry should include capability discovery");
        Require(AIArenaControlCommands.IsKnown("status"), "control-plane registry should include status");
        Require(AIArenaControlCommands.IsKnown("snapshot"), "control-plane registry should include snapshot");
        Require(AIArenaControlCommands.IsKnown("app.screenshot"), "control-plane registry should include application screenshots");
        Require(AIArenaControlCommands.IsKnown("events.watch"), "control-plane registry should include event streaming");
        Require(AIArenaControlCommands.IsKnown("navigation.select"), "control-plane registry should include navigation select");
        Require(AIArenaControlCommands.IsKnown("navigation.theme.set"), "control-plane registry should include theme setting");
        Require(AIArenaControlCommands.IsKnown("navigation.provider.focus"), "control-plane registry should include provider focus");
        Require(AIArenaControlCommands.IsKnown("navigation.rail.set"), "control-plane registry should include right rail control");
        Require(AIArenaControlCommands.IsKnown("view.preset.set"), "control-plane registry should include transcript presets");
        Require(AIArenaControlCommands.IsKnown("match.setup.state"), "control-plane registry should include Match Setup state");
        Require(AIArenaControlCommands.IsKnown("match.setup.open"), "control-plane registry should include Match Setup open");
        Require(AIArenaControlCommands.IsKnown("match.setup.close"), "control-plane registry should include Match Setup close");
        Require(AIArenaControlCommands.IsKnown("match.setup.export"), "control-plane registry should include portable Match Setup export");
        Require(AIArenaControlCommands.IsKnown("match.setup.import"), "control-plane registry should include portable Match Setup import");
        Require(AIArenaControlCommands.IsKnown("match.generation.state"), "control-plane registry should include generation state");
        Require(AIArenaControlCommands.IsKnown("match.generate.random"), "control-plane registry should include random generation");
        Require(AIArenaControlCommands.IsKnown("match.generate.ai"), "control-plane registry should include AI Choice generation");
        Require(AIArenaControlCommands.IsKnown("match.generate.current"), "control-plane registry should include Current Topics generation");
        Require(AIArenaControlCommands.IsKnown("match.generate.wild"), "control-plane registry should include Wild generation");
        Require(AIArenaControlCommands.IsKnown("match.replay"), "control-plane registry should include generated setup replay");
        Require(AIArenaControlCommands.IsKnown("match.replay.new"), "control-plane registry should include clean replay sessions");
        Require(AIArenaControlCommands.IsKnown("settings.state"), "control-plane registry should include Settings state");
        Require(AIArenaControlCommands.IsKnown("settings.open"), "control-plane registry should include Settings open");
        Require(AIArenaControlCommands.IsKnown("settings.close"), "control-plane registry should include Settings close");
        Require(AIArenaControlCommands.IsKnown("settings.search"), "control-plane registry should include Settings search");
        Require(AIArenaControlCommands.IsKnown("session.state"), "control-plane registry should include saved-state inventory");
        Require(AIArenaControlCommands.IsKnown("session.select"), "control-plane registry should include session selection");
        Require(AIArenaControlCommands.IsKnown("session.create"), "control-plane registry should include session creation");
        Require(AIArenaControlCommands.IsKnown("session.fork"), "control-plane registry should include full-state session forks");
        Require(AIArenaControlCommands.IsKnown("session.checkpoint.create"), "control-plane registry should include checkpoint creation");
        Require(AIArenaControlCommands.IsKnown("session.checkpoint.restore"), "control-plane registry should include checkpoint restore");
        Require(AIArenaControlCommands.IsKnown("provider.state"), "control-plane registry should include provider state");
        Require(AIArenaControlCommands.IsKnown("provider.config.set"), "control-plane registry should include atomic provider configuration");
        Require(AIArenaControlCommands.IsKnown("provider.model.set"), "control-plane registry should include provider model setting");
        Require(AIArenaControlCommands.IsKnown("provider.test"), "control-plane registry should include provider completion diagnostics");
        Require(AIArenaControlCommands.IsKnown("provider.models.refresh"), "control-plane registry should include provider model discovery");
        Require(AIArenaControlCommands.IsKnown("agent.state"), "control-plane registry should include agent state");
        Require(AIArenaControlCommands.IsKnown("agent.command.state"), "control-plane registry should include agent command state");
        Require(AIArenaControlCommands.IsKnown("agent.work.brief"), "control-plane registry should include agent work brief");
        Require(AIArenaControlCommands.IsKnown("agent.build.evidence"), "control-plane registry should include agent build evidence");
        Require(AIArenaControlCommands.IsKnown("agent.outputs"), "control-plane registry should include agent outputs");
        Require(AIArenaControlCommands.IsKnown("agent.runbook.state"), "control-plane registry should include Agent runbook state");
        Require(AIArenaControlCommands.IsKnown("agent.runbook.resume"), "control-plane registry should include Agent runbook resume");
        Require(AIArenaControlCommands.IsKnown("agent.runbook.checkpoint"), "control-plane registry should include Agent runbook checkpoints");
        Require(AIArenaControlCommands.IsKnown("agent.send"), "control-plane registry should include agent send");
        Require(AIArenaControlCommands.IsKnown("agent.stage.next"), "control-plane registry should include agent next staging");
        Require(AIArenaControlCommands.IsKnown("agent.stage.verify"), "control-plane registry should include agent verify staging");
        Require(AIArenaControlCommands.IsKnown("agent.stage.artifact"), "control-plane registry should include agent artifact staging");
        Require(AIArenaControlCommands.IsKnown("agent.command.stage"), "control-plane registry should include agent command staging");
        Require(AIArenaControlCommands.IsKnown("agent_workspace_set"), "control-plane registry should normalize underscore commands");
        Require(AIArenaControlCommands.IsKnown("arena.start"), "control-plane registry should include arena start");
        Require(AIArenaControlCommands.IsKnown("arena.stop"), "control-plane registry should include arena stop");
        Require(AIArenaControlCommands.IsKnown("arena.turn"), "control-plane registry should include one-turn execution");
        Require(AIArenaControlCommands.IsKnown("arena.narrate"), "control-plane registry should include narration");
        Require(AIArenaControlCommands.IsKnown("arena.reset"), "control-plane registry should include reset");
        Require(AIArenaControlCommands.IsKnown("arena.operator.send"), "control-plane registry should include operator injection");
        Require(AIArenaControlCommands.IsKnown("internet.state"), "control-plane registry should include Internet state");
        Require(AIArenaControlCommands.IsKnown("internet.set"), "control-plane registry should include Internet toggling");
        Require(AIArenaControlCommands.IsKnown("internet.test"), "control-plane registry should include Internet diagnostics");
        Require(AIArenaControlCommands.IsKnown("settings.update"), "control-plane registry should include bounded Settings mutation");
        Require(AIArenaControlCommands.IsKnown("match.roster.set"), "control-plane registry should include Match Setup roster sizing");
        Require(AIArenaControlCommands.IsKnown("match.matrix.state"), "control-plane registry should include relationship matrix state");
        Require(AIArenaControlCommands.IsKnown("match.matrix.set"), "control-plane registry should include named relationship patterns");
        Require(AIArenaControlCommands.IsKnown("collaborate.state"), "control-plane registry should include collaborate state");
        Require(AIArenaControlCommands.IsKnown("collaborate.review"), "control-plane registry should include collaborate review export");
        Require(AIArenaControlCommands.IsKnown("collaborate.send"), "control-plane registry should include collaborate send");
        Require(AIArenaControlCommands.IsKnown("collaborate.stop"), "control-plane registry should include collaborate stop");
        Require(AIArenaControlCommands.IsKnown("collaborate.fork"), "control-plane registry should include collaborate fork");
        Require(AIArenaControlCommands.IsKnown("collaborate.repeat"), "control-plane registry should include collaborate repeat");
        Require(AIArenaControlCommands.IsKnown("export.transcript"), "control-plane registry should include transcript export");
        Require(AIArenaControlCommands.IsKnown("export.session"), "control-plane registry should include session export");
        Require(AIArenaControlCommands.IsKnown("export.receipts"), "control-plane registry should include receipts export");
        Require(!AIArenaControlCommands.IsKnown("not.real"), "control-plane registry should reject unknown commands");
        Require(AIArenaControlCapabilityCatalog.All.Count == 80, "capability catalog should expose the complete 80-command surface");
        Require(AIArenaControlCapabilityCatalog.All.Select(item => item.Command).Distinct(StringComparer.OrdinalIgnoreCase).Count() == AIArenaControlCapabilityCatalog.All.Count, "capability catalog commands should be unique");
        var reset = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == "arena.reset");
        Require(reset.Destructive && reset.RequiredArguments.Contains("confirm", StringComparer.OrdinalIgnoreCase), "capability catalog should mark arena reset as destructive and confirmation-gated");
        var restore = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.SessionCheckpointRestore);
        Require(restore.Destructive && restore.RequiredArguments.Contains("confirm", StringComparer.OrdinalIgnoreCase), "capability catalog should mark checkpoint restore as destructive and confirmation-gated");
        var wild = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.MatchGenerateWild);
        Require(wild.Destructive && wild.RequiredArguments.Contains("confirm", StringComparer.OrdinalIgnoreCase), "capability catalog should mark Wild generation as broad and confirmation-gated");
        var settingsUpdate = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.SettingsUpdate);
        Require(!settingsUpdate.Destructive && settingsUpdate.OptionalArguments.Contains("topStripMode", StringComparer.OrdinalIgnoreCase), "capability catalog should describe safe Settings mutation arguments");
        var roster = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.MatchRosterSet);
        Require(!roster.Destructive && roster.RequiredArguments.Contains("count", StringComparer.OrdinalIgnoreCase), "capability catalog should require a roster count without mislabeling it destructive");
        var matrix = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.MatchMatrixSet);
        Require(!matrix.Destructive && matrix.RequiredArguments.Contains("pattern", StringComparer.OrdinalIgnoreCase), "capability catalog should require an auditable matrix pattern");
        var collaborateReview = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.CollaborateReview);
        Require(!collaborateReview.Destructive && collaborateReview.OptionalArguments.Contains("id", StringComparer.OrdinalIgnoreCase), "collaborate review should support an optional saved run id");
        var providerConfig = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.ProviderConfigSet);
        Require(!providerConfig.Destructive
            && providerConfig.OptionalArguments.Contains("apiToken", StringComparer.OrdinalIgnoreCase)
            && providerConfig.OptionalArguments.Contains("clearApiToken", StringComparer.OrdinalIgnoreCase)
            && providerConfig.OptionalArguments.Contains("narratorModel", StringComparer.OrdinalIgnoreCase), "provider configuration capability should describe secret and role-routing inputs without marking the patch destructive");
        var providerTest = AIArenaControlCapabilityCatalog.All.Single(item => item.Command == AIArenaControlCommands.ProviderTest);
        Require(providerTest.OptionalArguments.Contains("allRoles", StringComparer.OrdinalIgnoreCase), "provider diagnostic capability should advertise its all-role probe");
        Require(AIArenaControlCapabilityCatalog.All.All(item => !string.IsNullOrWhiteSpace(item.Category) && !string.IsNullOrWhiteSpace(item.Description)), "capability catalog entries should remain auditable");

        var controlPlaneDocumentation = File.ReadAllText(FindWorkspaceFile("CONTROLPLANE.md"));
        foreach (var capability in AIArenaControlCapabilityCatalog.All)
        {
            Require(
                controlPlaneDocumentation.Contains($"`{capability.Command}`", StringComparison.Ordinal),
                $"root CONTROLPLANE.md should document '{capability.Command}'");
        }
    }

    static void ControlPlanePowerShellClientExposesTypedVerbs()
    {
        var scriptPath = FindWorkspaceFile("scripts/ai-arena-control.ps1");
        var script = File.ReadAllText(scriptPath);
        var functionCount = script
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.TrimStart().StartsWith("function ", StringComparison.OrdinalIgnoreCase));
        Require(functionCount == 54, "PowerShell client should expose the complete 54-function surface");
        Require(script.Contains("[string]$Token", StringComparison.Ordinal), "PowerShell client should expose -Token for authenticated control-plane calls");
        Require(script.Contains("AI_ARENA_CONTROL_TOKEN", StringComparison.Ordinal), "PowerShell client should support token injection through AI_ARENA_CONTROL_TOKEN");
        Require(script.Contains("Get-AIArenaControlToken", StringComparison.Ordinal), "PowerShell client should load the app-written token for debug calls");
        Require(script.Contains("[string]$Prompt", StringComparison.Ordinal), "PowerShell client should expose -Prompt for agent.send");
        Require(script.Contains("[string]$Path", StringComparison.Ordinal), "PowerShell client should expose -Path for agent.workspace.set");
        Require(script.Contains("[string]$View", StringComparison.Ordinal), "PowerShell client should expose -View for navigation.select");
        Require(script.Contains("[string]$Theme", StringComparison.Ordinal), "PowerShell client should expose -Theme for navigation.theme.set");
        Require(script.Contains("function Invoke-AIArenaAgent", StringComparison.Ordinal), "PowerShell client should include Agent convenience commands");
        Require(script.Contains("function Invoke-AIArenaArena", StringComparison.Ordinal), "PowerShell client should include Arena convenience commands");
        Require(script.Contains("function Invoke-AIArenaCollaborate", StringComparison.Ordinal), "PowerShell client should include Collaborate convenience commands");
        Require(script.Contains("function Get-AIArenaCollaborateReview", StringComparison.Ordinal), "PowerShell client should expose saved Collaborate run reviews and traces");
        Require(script.Contains("function Export-AIArena", StringComparison.Ordinal), "PowerShell client should include export convenience commands");
        Require(script.Contains("function Save-AIArenaScreenshot", StringComparison.Ordinal), "PowerShell client should expose application screenshot capture");
        Require(script.Contains("function Get-AIArenaProvider", StringComparison.Ordinal), "PowerShell client should expose secret-free provider state");
        Require(script.Contains("function Set-AIArenaProviderConfig", StringComparison.Ordinal), "PowerShell client should expose typed provider configuration patches");
        Require(script.Contains("function Test-AIArenaProvider", StringComparison.Ordinal), "PowerShell client should expose provider completion diagnostics");
        Require(script.Contains("function Update-AIArenaProviderModels", StringComparison.Ordinal), "PowerShell client should expose provider model discovery");
        Require(script.Contains("function Set-AIArenaProviderModel", StringComparison.Ordinal), "PowerShell client should include provider model setting");
        Require(script.Contains("[System.Security.SecureString]$ApiToken", StringComparison.Ordinal), "PowerShell provider token input should use SecureString");
        Require(script.Contains("SecureStringToBSTR($ApiToken)", StringComparison.Ordinal), "PowerShell provider configuration should unwrap SecureString only at the invocation boundary");
        Require(script.Contains("ZeroFreeBSTR($apiTokenBstr)", StringComparison.Ordinal), "PowerShell provider configuration should zero its temporary token buffer");
        Require(script.Contains("$providerArgs.Remove('apiToken')", StringComparison.Ordinal), "PowerShell provider configuration should remove plaintext token material after invocation");
        Require(script.Contains("[switch]$AllRoles", StringComparison.Ordinal) && script.Contains("$providerArgs['allRoles']", StringComparison.Ordinal), "PowerShell provider diagnostics should expose the typed all-role switch");
        Require(script.Contains("-Command 'provider.config.set'", StringComparison.Ordinal)
            && script.Contains("-Command 'provider.test'", StringComparison.Ordinal)
            && script.Contains("-Command 'provider.models.refresh'", StringComparison.Ordinal), "PowerShell provider functions should route to the new provider commands");
        Require(script.Contains("function Set-AIArenaAgentCommand", StringComparison.Ordinal), "PowerShell client should include Agent command staging");
        Require(script.Contains("function Get-AIArenaCapabilities", StringComparison.Ordinal), "PowerShell client should expose capability discovery");
        Require(script.Contains("function Set-AIArenaRightRail", StringComparison.Ordinal), "PowerShell client should expose right rail control");
        Require(script.Contains("function Set-AIArenaViewPreset", StringComparison.Ordinal), "PowerShell client should expose transcript presets");
        Require(script.Contains("function Get-AIArenaInternet", StringComparison.Ordinal), "PowerShell client should expose Internet state");
        Require(script.Contains("function Set-AIArenaInternet", StringComparison.Ordinal), "PowerShell client should expose Internet toggling");
        Require(script.Contains("function Test-AIArenaInternet", StringComparison.Ordinal), "PowerShell client should expose Internet diagnostics");
        Require(script.Contains("function Get-AIArenaRunbook", StringComparison.Ordinal), "PowerShell client should expose durable Agent runbook state");
        Require(script.Contains("function Resume-AIArenaRunbook", StringComparison.Ordinal), "PowerShell client should expose Agent runbook resume");
        Require(script.Contains("function Add-AIArenaRunbookCheckpoint", StringComparison.Ordinal), "PowerShell client should expose durable operator checkpoints");
        Require(script.Contains("function Get-AIArenaSession", StringComparison.Ordinal), "PowerShell client should expose session inventory");
        Require(script.Contains("function Select-AIArenaSession", StringComparison.Ordinal), "PowerShell client should expose session selection");
        Require(script.Contains("function New-AIArenaSession", StringComparison.Ordinal), "PowerShell client should expose session creation");
        Require(script.Contains("function New-AIArenaSessionFork", StringComparison.Ordinal), "PowerShell client should expose full-state session forks");
        Require(script.Contains("function New-AIArenaCheckpoint", StringComparison.Ordinal), "PowerShell client should expose checkpoint creation");
        Require(script.Contains("function Restore-AIArenaCheckpoint", StringComparison.Ordinal), "PowerShell client should expose checkpoint restore");
        Require(script.Contains("function Get-AIArenaMatchSetup", StringComparison.Ordinal), "PowerShell client should expose Match Setup state");
        Require(script.Contains("function Open-AIArenaMatchSetup", StringComparison.Ordinal), "PowerShell client should expose Match Setup sections");
        Require(script.Contains("function Close-AIArenaMatchSetup", StringComparison.Ordinal), "PowerShell client should expose Match Setup close");
        Require(script.Contains("function Export-AIArenaMatchSetup", StringComparison.Ordinal), "PowerShell client should expose portable Match Setup export");
        Require(script.Contains("function Import-AIArenaMatchSetup", StringComparison.Ordinal), "PowerShell client should expose portable Match Setup import");
        Require(script.Contains("'match.setup.export'", StringComparison.Ordinal) && script.Contains("'match.setup.import'", StringComparison.Ordinal), "PowerShell portability verbs should route through the Match Setup control family");
        Require(script.Contains("function Set-AIArenaMatchRoster", StringComparison.Ordinal), "PowerShell client should expose typed Match Setup roster sizing");
        Require(script.Contains("[ValidateRange(1, 8)]", StringComparison.Ordinal), "PowerShell roster sizing should reject out-of-range counts locally");
        Require(script.Contains("function Get-AIArenaMatchMatrix", StringComparison.Ordinal), "PowerShell client should expose relationship matrix state");
        Require(script.Contains("function Set-AIArenaMatchMatrix", StringComparison.Ordinal), "PowerShell client should expose typed relationship patterns");
        Require(script.Contains("'evidence_ladder'", StringComparison.Ordinal) && script.Contains("'spotlight_defense'", StringComparison.Ordinal), "PowerShell matrix patterns should remain discoverable through ValidateSet");
        Require(script.Contains("function Get-AIArenaMatchGeneration", StringComparison.Ordinal), "PowerShell client should expose match generation state");
        Require(script.Contains("function New-AIArenaMatch", StringComparison.Ordinal), "PowerShell client should expose match generation modes");
        Require(script.Contains("function Invoke-AIArenaMatchReplay", StringComparison.Ordinal), "PowerShell client should expose generated setup replay");
        Require(script.Contains("ConfirmWild", StringComparison.Ordinal), "PowerShell client should require an explicit Wild generation switch");
        Require(script.Contains("function Get-AIArenaSettings", StringComparison.Ordinal), "PowerShell client should expose Settings state");
        Require(script.Contains("function Set-AIArenaSettings", StringComparison.Ordinal), "PowerShell client should expose typed Settings mutation");
        Require(script.Contains("Set-AIArenaSettings requires at least one preference parameter", StringComparison.Ordinal), "PowerShell Settings mutation should reject empty updates locally");
        Require(script.Contains("function Open-AIArenaSettings", StringComparison.Ordinal), "PowerShell client should expose Settings open");
        Require(script.Contains("function Search-AIArenaSettings", StringComparison.Ordinal), "PowerShell client should expose Settings search");
        Require(script.Contains("function Close-AIArenaSettings", StringComparison.Ordinal), "PowerShell client should expose Settings close");
        Require(script.Contains("confirm = $true", StringComparison.Ordinal), "PowerShell checkpoint restore should forward explicit confirmation after ShouldProcess");
        Require(script.Contains("'turn', 'narrate', 'reset'", StringComparison.Ordinal), "PowerShell Arena wrapper should expose one turn, narration, and reset");
        Require(script.Contains("-ConfirmReset:$ConfirmReset", StringComparison.Ordinal), "PowerShell reset should forward explicit confirmation");
        Require(script.Contains("function Watch-AIArena", StringComparison.Ordinal), "PowerShell client should support Watch-AIArena events");
        Require(script.Contains("stage.verify", StringComparison.Ordinal), "PowerShell client should expose Agent stage actions");
        Require(script.Contains("operator.send", StringComparison.Ordinal), "PowerShell client should expose operator injection");
        Require(script.Contains("[ValidateSet('public', 'private', 'narrator')]", StringComparison.Ordinal), "PowerShell operator injection should reject route typos before sending text");
        Require(script.Contains("collaborate.$Action", StringComparison.Ordinal), "PowerShell client should expose Collaborate actions");
    }

    static void SavedStateControlServiceCreatesSelectsAndRestores()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-saved-state-control-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var events = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var service = new SavedStateControlService(
                store,
                events,
                () => active,
                (session, _, _) =>
                {
                    active = session;
                    return Task.CompletedTask;
                },
                async (preferredId, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
                },
                async (_, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(active!.Id, StringComparison.OrdinalIgnoreCase));
                });

            var created = service.CreateSessionAsync("powershell-audit").GetAwaiter().GetResult();
            Require(created.Ok && created.State.ActiveSessionId == "powershell-audit", "session creation should select the clean copy");
            Require(created.State.Sessions.Count == 2, "session inventory should include the original and copy");

            var checkpoint = service.SaveCheckpointAsync("before change").GetAwaiter().GetResult();
            Require(checkpoint.Ok && checkpoint.State.Checkpoints.Count == 1, "checkpoint creation should return refreshed inventory");
            var checkpointId = checkpoint.State.Checkpoints.Single().Id;

            var restored = service.RestoreCheckpointAsync(checkpointId).GetAwaiter().GetResult();
            Require(restored.Ok, "a listed checkpoint should restore successfully");
            Require(restored.State.Checkpoints.Single().Id == checkpointId, "restore receipt should retain checkpoint identity");

            var selected = service.SelectSessionAsync("default").GetAwaiter().GetResult();
            Require(selected.Ok && selected.State.ActiveSessionId == "default", "session selection should update active state");

            var fork = store.ForkSessionAsync("default", "lineage-state").GetAwaiter().GetResult();
            active = store.ListSessionsAsync().GetAwaiter().GetResult()
                .Single(session => session.Id.Equals(fork.TargetSessionId, StringComparison.OrdinalIgnoreCase));
            var lineageState = service.CaptureAsync().GetAwaiter().GetResult();
            Require(lineageState.ActiveForkLineage?.ParentSessionId == "default", "session state should expose the active branch's direct parent");
            Require(lineageState.ActiveForkLineage?.ParentPersistenceRevision == fork.SourcePersistenceRevision, "session state should expose the captured parent revision");
            Require(lineageState.ParentAvailable, "session state should report when the active branch's parent is still selectable");

            var missing = service.RestoreCheckpointAsync("").GetAwaiter().GetResult();
            Require(!missing.Ok && missing.ErrorCode == "missing_argument", "checkpoint restore should reject a missing id with a stable error code");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void ShellOverlayControlServicePreservesNavigationContracts()
    {
        var setupOpen = false;
        var setupSection = "scenario";
        var settingsOpen = false;
        var settingsQuery = "";
        var setupShowCalls = 0;
        var service = new ShellOverlayControlService(
            () => new AIArenaMatchSetupControlState(setupOpen, setupSection, "agent", "default", "balanced", "Audit", 4, false),
            () =>
            {
                setupOpen = true;
                setupShowCalls++;
            },
            () => setupOpen = false,
            section =>
            {
                setupSection = section;
                return true;
            },
            () => new AIArenaSettingsControlState(
                settingsOpen,
                settingsQuery,
                "dark-blue",
                CompactTranscript: false,
                FollowTranscript: true,
                TopStripMode: "diagnostics",
                TurnCompare: false,
                MatchTimeline: false,
                BattleReview: false,
                MemoryNotes: false,
                DecisionCard: false,
                AutoModerator: true,
                StyleFit: false,
                InternetDetails: false,
                RightRailCollapsed: false,
                DebugControls: true,
                WorldEnabled: false,
                AgentWorkspaceEnabled: true,
                ControlPlaneEnabled: true,
                VoiceEnabled: false),
            () => settingsOpen = true,
            () => settingsOpen = false,
            query => settingsQuery = query);

        var invalid = service.OpenMatchSetup("unknown");
        Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument", "Match Setup should reject unknown sections with a stable error code");
        Require(setupShowCalls == 0 && !setupOpen, "invalid Match Setup input should not change the visible workspace");

        var opened = service.OpenMatchSetup("MATRIX");
        Require(opened.Ok && opened.State.Open && opened.State.Section == "matrix", "Match Setup should normalize and select the requested section");
        var closed = service.CloseMatchSetup();
        Require(closed.Ok && !closed.State.Open && closed.State.ReturnView == "agent", "Match Setup close should preserve the host-defined return view");

        var searched = service.SearchSettings("  voice    narration  ");
        Require(searched.Ok && searched.State.Open && searched.State.SearchQuery == "voice narration", "Settings search should open the overlay and normalize whitespace");
        var cleared = service.SearchSettings("");
        Require(cleared.Ok && cleared.State.SearchQuery == "", "Settings search should be explicitly clearable");
        var settingsClosed = service.CloseSettings();
        Require(settingsClosed.Ok && !settingsClosed.State.Open, "Settings close should use the host visibility boundary");
    }

    static void AppPreferenceControlServicePersistsSafeUpdates()
    {
        WithTempSettingsStore(store =>
        {
            var current = new WpfSettings
            {
                AllowDebugControls = true,
                CompactTranscriptMode = false,
                TopStripMode = "diagnostics",
                ShowTranscriptDiagnostics = true,
                ShowBattleReview = false,
                VoiceTtsEnabled = false,
                ShowWorldDebug = false
            };
            store.Save(current);
            var applyCalls = 0;

            AIArenaSettingsControlState Capture() => new(
                Open: false,
                SearchQuery: "",
                Theme: current.ThemeId,
                CompactTranscript: current.CompactTranscriptMode,
                FollowTranscript: current.FollowTranscript,
                TopStripMode: current.TopStripMode,
                TurnCompare: current.TurnCompareMode,
                MatchTimeline: current.ShowMatchQualityTimeline,
                BattleReview: current.ShowBattleReview,
                MemoryNotes: current.ShowAgentMemoryNotes,
                DecisionCard: current.ShowDecisionCard,
                AutoModerator: current.ShowAutoModerator,
                StyleFit: current.ShowStyleFit,
                InternetDetails: current.ShowTranscriptInternetDetails,
                RightRailCollapsed: current.RightRailCollapsed,
                DebugControls: current.AllowDebugControls,
                WorldEnabled: current.ShowWorldDebug,
                AgentWorkspaceEnabled: current.ShowAgentWorkspace,
                ControlPlaneEnabled: current.EnableControlPlane,
                VoiceEnabled: current.VoiceTtsEnabled);

            var service = new AppPreferenceControlService(store, () => current, () => applyCalls++, Capture);
            var updated = service.Update(new AIArenaPreferencePatch(
                CompactTranscript: true,
                TopStripMode: "hidden",
                BattleReview: true,
                VoiceEnabled: true,
                WorldEnabled: true));

            Require(updated.Ok && updated.Data.Changed.Count == 5, "preference update should report exactly the preferences it changed");
            Require(applyCalls == 1, "a successful preference batch should reapply the UI exactly once");
            var persisted = store.Load();
            Require(persisted.CompactTranscriptMode && persisted.TopStripMode == "hidden", "preference update should persist transcript presentation");
            Require(persisted.ShowBattleReview && persisted.VoiceTtsEnabled && persisted.ShowWorldDebug, "preference update should persist review, voice, and debug-gated World state");
            Require(!persisted.ShowTranscriptDiagnostics, "hidden top strip should keep the legacy diagnostics flag synchronized");

            var invalid = service.Update(new AIArenaPreferencePatch(TopStripMode: "unknown"));
            Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument" && applyCalls == 1, "invalid top-strip input should not persist or reapply preferences");

            current.AllowDebugControls = false;
            current.ShowWorldDebug = false;
            var gated = service.Update(new AIArenaPreferencePatch(WorldEnabled: true));
            Require(!gated.Ok && gated.ErrorCode == "debug_controls_required" && applyCalls == 1, "automation should not bypass the UI master-debug gate");

            var hiddenAgent = service.Update(new AIArenaPreferencePatch(AgentWorkspaceEnabled: false));
            Require(hiddenAgent.Ok && !current.ShowAgentWorkspace && applyCalls == 2, "automation should hide Agent independently of Debug controls");
            var shownAgent = service.Update(new AIArenaPreferencePatch(AgentWorkspaceEnabled: true));
            Require(shownAgent.Ok && current.ShowAgentWorkspace && applyCalls == 3, "automation should restore Agent independently of Debug controls");
            Require(store.Load().ShowAgentWorkspace, "automation should persist the restored Agent workspace preference");
        });
    }

    static void MatchSetupControlHandlerValidatesRosterChanges()
    {
        var matrixRoot = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-handler-{Guid.NewGuid():N}");
        try
        {
        var activeAgents = 4;
        var resizeCalls = 0;
        var overlays = new ShellOverlayControlService(
            () => new AIArenaMatchSetupControlState(false, "cast", "arena", "default", "balanced", "Audit", activeAgents, false),
            () => { },
            () => { },
            _ => true,
            () => new AIArenaSettingsControlState(false, "", "dark-blue", false, true, "diagnostics", false, false, false, false, false, true, false, false, false, true, false, false, true, false),
            () => { },
            () => { },
            _ => { });
        var events = new AIArenaControlPlaneEventHub();
        AIArenaControlEvent? published = null;
        using var subscription = events.Subscribe(item => published = item);
        var matrix = new RivalryMatrixControlService(
            new SessionStore(matrixRoot),
            new EventLogStore(matrixRoot),
            () => null,
            () => false,
            (_, action) => action(CancellationToken.None),
            (_, _) => Task.CompletedTask);
        var handler = new AIArenaMatchSetupControlHandler(
            overlays,
            count =>
            {
                resizeCalls++;
                activeAgents = count;
                return Task.FromResult(new AIArenaAgentRosterResizeResult(true, "", $"Agent roster resized to {count} active agent(s).", count));
            },
            matrix,
            new MatchSetupPortabilityService(
                new SessionStore(matrixRoot),
                new EventLogStore(matrixRoot),
                () => null,
                () => false,
                (_, _) => Task.CompletedTask),
            events);

        Require(AIArenaControlPlaneProtocol.TryParseRequest("""{"id":"bad","command":"match.roster.set","args":{"count":"many"}}""", out var badRequest, out _), "invalid roster request shape should still parse at the protocol boundary");
        var bad = handler.ExecuteAsync(badRequest).GetAwaiter().GetResult();
        Require(!bad.Ok && bad.ErrorCode == "invalid_argument" && resizeCalls == 0, "handler should reject non-integer roster input before mutation");

        Require(AIArenaControlPlaneProtocol.TryParseRequest("""{"id":"good","command":"match.roster.set","args":{"count":6}}""", out var goodRequest, out _), "valid roster request should parse");
        var good = handler.ExecuteAsync(goodRequest).GetAwaiter().GetResult();
        Require(good.Ok && resizeCalls == 1 && activeAgents == 6, "handler should delegate one valid roster resize and return refreshed state");
        Require(published?.Type == "match.roster.changed", "successful roster sizing should publish an auditable event");

        var mainWindow = ReadMainWindowSource();
        Require(mainWindow.Contains("_matchSetupControlHandler.CanHandle", StringComparison.Ordinal), "MainWindow should route the Match Setup family through its focused handler");
        Require(!mainWindow.Contains("case AIArenaControlCommands.MatchSetupState", StringComparison.Ordinal), "MainWindow should not retain duplicated Match Setup overlay cases");
        }
        finally
        {
            if (Directory.Exists(matrixRoot))
            {
                Directory.Delete(matrixRoot, recursive: true);
            }
        }
    }

    static void AgentRosterControlPathPersistsBoundedCastChanges()
    {
        RunStaTest(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"ai-arena-roster-control-{Guid.NewGuid():N}");
            try
            {
                var store = new SessionStore(root);
                var eventStore = new EventLogStore(root);
                store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
                AIArena.Core.Models.SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
                var busy = false;
                var refreshCalls = 0;
                var coordinator = new AgentRosterCoordinator(
                    store,
                    eventStore,
                    new ComboBox(),
                    new ComboBox(),
                    new Button(),
                    new TextBlock(),
                    () => false,
                    () => busy,
                    () => active,
                    _ => Task.CompletedTask,
                    async (_, _, action, _) => await action(),
                    (snapshot, sessionId) => store.SaveSnapshotAsync(snapshot, sessionId),
                    _ =>
                    {
                        refreshCalls++;
                        return Task.CompletedTask;
                    },
                    _ => { });

                var resized = coordinator.ResizeAgentCountAsync(6).GetAwaiter().GetResult();
                var persisted = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
                Require(resized.Ok && persisted is not null && persisted.Engine.Agents.Count(agent => agent.Active) == 6, "control-path resize should persist the requested active cast");
                Require(refreshCalls == 1, "control-path resize should refresh the active session exactly once");

                busy = true;
                var blocked = coordinator.ResizeAgentCountAsync(2).GetAwaiter().GetResult();
                var afterBusy = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
                Require(!blocked.Ok && blocked.ErrorCode == "busy" && afterBusy?.Engine.Agents.Count(agent => agent.Active) == 6, "busy roster changes should fail without touching persisted state");

                busy = false;
                var invalid = coordinator.ResizeAgentCountAsync(9).GetAwaiter().GetResult();
                Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument" && refreshCalls == 1, "out-of-range roster changes should fail before persistence or refresh");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });
    }

    static void RivalryMatrixControlServicePersistsNamedPatterns()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-matrix-control-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var eventStore = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            AIArena.Core.Models.SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var busy = false;
            var refreshCalls = 0;
            var service = new RivalryMatrixControlService(
                store,
                eventStore,
                () => active,
                () => busy,
                async (_, action) => await action(CancellationToken.None),
                (_, _) =>
                {
                    refreshCalls++;
                    return Task.CompletedTask;
                });

            var applied = service.ApplyPatternAsync("evidence-ladder").GetAwaiter().GetResult();
            var persisted = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
            Require(applied.Ok && applied.State.Enabled && applied.State.Pattern == "evidence_ladder", "named matrix pattern should normalize and apply");
            Require(applied.State.Links.Count == 4 && persisted?.Engine.RivalryMatrix.Links.Count == 4, "evidence ladder should persist one relationship per active agent");
            Require(applied.State.Links.Select(link => link.Stance).SequenceEqual(new[] { "fact_check", "cross_examine", "steelman", "fact_check" }), "evidence ladder should preserve its deterministic stance sequence");
            var recaptured = service.CaptureAsync().GetAwaiter().GetResult();
            Require(recaptured.Pattern == "evidence_ladder", "matrix state should infer the persisted named pattern after a fresh read");
            Require(refreshCalls == 1 && File.ReadAllText(eventStore.EventPath()).Contains("native_rivalry_matrix_pattern_applied", StringComparison.Ordinal), "matrix application should refresh once and append an audit event");

            busy = true;
            var blocked = service.ApplyPatternAsync("support_chain").GetAwaiter().GetResult();
            Require(!blocked.Ok && blocked.ErrorCode == "busy" && refreshCalls == 1, "busy matrix changes should fail before persistence");

            busy = false;
            var invalid = service.ApplyPatternAsync("invented_pattern").GetAwaiter().GetResult();
            Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument" && refreshCalls == 1, "unknown patterns should fail without refresh or mutation");

            var disabled = service.ApplyPatternAsync("off").GetAwaiter().GetResult();
            var afterOff = store.LoadSnapshotAsync("default").GetAwaiter().GetResult();
            Require(disabled.Ok && !disabled.State.Enabled && disabled.State.Links.Count == 0, "off should disable and clear the matrix atomically");
            Require(afterOff is not null && !afterOff.Engine.RivalryMatrix.Enabled && afterOff.Engine.RivalryMatrix.Links.Count == 0, "disabled matrix state should persist");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void ScenarioGenerationControlServiceGeneratesAndReplaysNativeHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-generation-control-{Guid.NewGuid():N}");
        MatchGenerationService? engine = null;
        try
        {
            var store = new SessionStore(root);
            var events = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            engine = new MatchGenerationService(sessionStore: store, eventLogStore: events);
            var settings = new WpfSettings
            {
                RandomSeedStyle = "technical",
                RandomSeedIntensity = "sharp",
                RandomSeedRolePack = "technical_architecture",
                RandomSeedAbsurdity = "grounded"
            };
            var service = new ScenarioGenerationControlService(
                engine,
                store,
                () => settings,
                () => active,
                async (_, action) => await action(CancellationToken.None),
                async (_, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(active!.Id, StringComparison.OrdinalIgnoreCase));
                },
                async (preferredId, cancellationToken) =>
                {
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
                });

            var partialSnapshot = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            partialSnapshot.Engine.Steering.Global = "Quality contract: define what a good outcome means.";
            store.SaveSnapshotAsync(partialSnapshot, "default").GetAwaiter().GetResult();
            var partialState = service.CaptureAsync().GetAwaiter().GetResult();
            Require(!partialState.QualityContractPresent, "PowerShell state must use the complete shared quality-contract audit rather than marker presence");

            var generated = service.GenerateAsync(
                "random",
                new AIArenaMatchGenerationOptions(Seed: "AUDIT-SEED-1")).GetAwaiter().GetResult();
            Require(generated.Ok, "native random match generation should succeed through the control service");
            Require(generated.Data.Receipt?.Seed == "AUDIT-SEED-1", "generation receipt should preserve a deterministic requested seed");
            Require(generated.Data.Receipt?.HistoryId == generated.Data.State.History.Single().Id, "generation receipt should expose the saved replay history id");
            Require(generated.Data.Receipt?.SeedDeterministic == true && generated.Data.Receipt?.ReplayMode == "seed_deterministic", "generation receipt should expose its determinism class immediately");
            Require(generated.Data.State.History.Count == 1, "generated setup should appear in auditable history");
            Require(generated.Data.State.QualityContractPresent, "PowerShell generation state should explicitly report the scenario quality contract");
            Require(generated.Data.State.GlobalInstruction.Contains("unacceptable failure", StringComparison.OrdinalIgnoreCase), "PowerShell generation state should expose the full evaluable scenario instruction");
            Require(generated.Data.State.History.Single().SeedDeterministic, "PowerShell history should classify random generation as seed-deterministic");
            Require(generated.Data.State.History.Single().ReplayMode == "seed_deterministic", "PowerShell history should expose the shared replay mode");
            var historyId = generated.Data.State.History.Single().Id;

            var replayed = service.ReplayAsync(historyId, newSession: false).GetAwaiter().GetResult();
            Require(replayed.Ok && replayed.Data.State.SessionId == "default", "same-session replay should preserve the active session");

            var newRun = service.ReplayAsync(historyId, newSession: true).GetAwaiter().GetResult();
            Require(newRun.Ok && newRun.Data.State.SessionId != "default", "new-run replay should select its clean comparison session");
            Require(store.ListSessionsAsync().GetAwaiter().GetResult().Count == 2, "new-run replay should create exactly one comparison session");

            var invalid = service.GenerateAsync(
                "random",
                new AIArenaMatchGenerationOptions(Prompt: new string('x', 501))).GetAwaiter().GetResult();
            Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument", "generation inputs should remain bounded before reaching the engine");
        }
        finally
        {
            engine?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void ScreenshotControlServiceResolvesSafePathsAndPreservesExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-screenshot-paths-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var service = new AIArenaScreenshotControlService(new Window(), root);
                var screenshotsRoot = Path.GetFullPath(Path.Combine(NativeDataPaths.ExportsRoot(root), "screenshots"));

                Require(service.TryResolvePath(null, out var defaultPath, out var defaultError), $"default screenshot path should resolve: {defaultError}");
                Require(Path.GetDirectoryName(defaultPath)?.Equals(screenshotsRoot, StringComparison.OrdinalIgnoreCase) == true
                    && Path.GetFileName(defaultPath).StartsWith("AI-Arena-", StringComparison.Ordinal)
                    && defaultPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase), "default screenshot path should be a timestamped PNG in the app screenshot directory");

                Require(service.TryResolvePath("nested/capture", out var relativePath, out var relativeError), $"safe relative screenshot path should resolve: {relativeError}");
                Require(relativePath.Equals(Path.Combine(screenshotsRoot, "nested", "capture.png"), StringComparison.OrdinalIgnoreCase), "relative screenshot paths should stay under the app screenshot directory and gain a PNG extension");

                Require(!service.TryResolvePath("../escape.png", out _, out var traversalError)
                    && traversalError.Contains("cannot leave", StringComparison.OrdinalIgnoreCase), "relative screenshot traversal should be rejected before any write");
                Require(!service.TryResolvePath("capture.jpg", out _, out var extensionError)
                    && extensionError.Contains("PNG", StringComparison.Ordinal), "non-PNG screenshot targets should be rejected");

                var absoluteTarget = Path.Combine(root, "explicit-target.PNG");
                Require(service.TryResolvePath(absoluteTarget, out var resolvedAbsolute, out var absoluteError), $"absolute PNG screenshot path should resolve: {absoluteError}");
                Require(resolvedAbsolute.Equals(Path.GetFullPath(absoluteTarget), StringComparison.OrdinalIgnoreCase), "an explicit absolute PNG screenshot path should be preserved");

                Require(service.TryResolvePath("existing.png", out var existingPath, out var existingError), $"existing screenshot path should resolve: {existingError}");
                Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
                byte[] sentinel = [0x41, 0x49, 0x41, 0x52, 0x45, 0x4E, 0x41];
                File.WriteAllBytes(existingPath, sentinel);
                var existingResult = service.CaptureAsync("existing.png").GetAwaiter().GetResult();
                Require(!existingResult.Ok
                    && existingResult.ErrorCode == "already_exists"
                    && existingResult.Path.Equals(existingPath, StringComparison.OrdinalIgnoreCase), "screenshot capture should return a stable no-overwrite error for an existing target");
                Require(existingResult.ByteSize == 0
                    && existingResult.PixelWidth == 0
                    && existingResult.PixelHeight == 0
                    && existingResult.CapturedAt is null, "failed no-overwrite results should not claim capture metadata");
                Require(File.ReadAllBytes(existingPath).SequenceEqual(sentinel), "screenshot no-overwrite protection should preserve existing file contents exactly");

                var traversalResult = service.CaptureAsync("../escape.png").GetAwaiter().GetResult();
                var nonPngResult = service.CaptureAsync("capture.txt").GetAwaiter().GetResult();
                Require(!traversalResult.Ok && traversalResult.ErrorCode == "invalid_argument", "screenshot capture should reject traversal through its public service boundary");
                Require(!nonPngResult.Ok && nonPngResult.ErrorCode == "invalid_argument", "screenshot capture should reject non-PNG targets through its public service boundary");
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void AppScreenshotHandlerCapturesRealPngAndPublishesEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-screenshot-handler-{Guid.NewGuid():N}");
        try
        {
            RunStaTest(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var initialSurfaceColor = Color.FromRgb(148, 20, 28);
                var settledSurfaceColor = Color.FromRgb(18, 42, 66);
                var surfaceBrush = new SolidColorBrush(initialSurfaceColor);
                var captureSurface = new Border
                {
                    Background = surfaceBrush,
                    Child = new TextBlock
                    {
                        Text = "AI Arena screenshot regression",
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                var window = new Window
                {
                    Width = 240,
                    Height = 140,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 1000,
                    Top = SystemParameters.VirtualScreenTop - 1000,
                    Content = captureSurface
                };
                EventHandler? navigationRendering = null;
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var navigationRenderTurns = 0;
                    navigationRendering = (_, _) =>
                    {
                        navigationRenderTurns++;
                        if (navigationRenderTurns == 3)
                        {
                            // Simulate visual work queued by a newly navigated
                            // surface after its first Loaded/render callbacks.
                            surfaceBrush.Color = settledSurfaceColor;
                            CompositionTarget.Rendering -= navigationRendering;
                        }
                    };
                    CompositionTarget.Rendering += navigationRendering;
                    var events = new AIArenaControlPlaneEventHub();
                    var published = new List<AIArenaControlEvent>();
                    AIArenaScreenshotControlResult? visibleReceipt = null;
                    using var subscription = events.Subscribe(published.Add);
                    var handler = new AIArenaAppControlHandler(
                        new AIArenaScreenshotControlService(window, root),
                        events,
                        result => visibleReceipt = result);
                    Require(handler.CanHandle("APP.SCREENSHOT") && !handler.CanHandle(AIArenaControlCommands.ProviderState), "app handler should claim only the screenshot command family");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        """{"id":"capture","command":"app.screenshot","args":{"path":"nested/handler-capture.png"}}""",
                        out var captureRequest,
                        out var captureParseError), $"screenshot request should parse: {captureParseError}");
                    var captureResponse = PumpDispatcherTask(handler.ExecuteAsync(captureRequest));
                    Require(captureResponse.Ok && captureResponse.Data is AIArenaScreenshotControlResult, "app screenshot handler should return a successful typed capture result");
                    var capture = (AIArenaScreenshotControlResult)captureResponse.Data!;
                    Require(AIArenaScreenshotControlService.MinimumRenderedFramesBeforeCapture >= 4,
                        "first-frame capture should span navigation, Loaded follow-up, and a stable composition turn");
                    Require(AIArenaScreenshotControlService.RenderedFramesAfterWarmup >= 2,
                        "capture should redraw retained WPF visuals after the discarded cache-warming render");
                    Require(navigationRenderTurns >= 3 && surfaceBrush.Color == settledSurfaceColor,
                        "screenshot settling should observe visual work deferred through the third composition turn");
                    var expectedPath = Path.GetFullPath(Path.Combine(NativeDataPaths.ExportsRoot(root), "screenshots", "nested", "handler-capture.png"));
                    Require(capture.Path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(capture.Path), "app screenshot handler should return the absolute path of the created PNG");
                    Require(capture.ByteSize == new FileInfo(capture.Path).Length
                        && capture.ByteSize > 8
                        && capture.PixelWidth > 0
                        && capture.PixelHeight > 0
                        && capture.CapturedAt.HasValue, "successful screenshot results should report byte size, pixel dimensions, and capture time");

                    byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
                    var initialBytes = File.ReadAllBytes(capture.Path);
                    Require(initialBytes.Take(pngSignature.Length).SequenceEqual(pngSignature), "captured screenshot should have a real PNG signature");
                    using (var stream = File.OpenRead(capture.Path))
                    {
                        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        Require(decoder.Frames.Count == 1
                            && decoder.Frames[0].PixelWidth == capture.PixelWidth
                            && decoder.Frames[0].PixelHeight == capture.PixelHeight, "captured PNG dimensions should match the control-plane result");
                        var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                        var settledPixel = new byte[4];
                        converted.CopyPixels(new Int32Rect(8, 8, 1, 1), settledPixel, 4, 0);
                        Require(
                            Math.Abs(settledPixel[2] - settledSurfaceColor.R) <= 1
                            && Math.Abs(settledPixel[1] - settledSurfaceColor.G) <= 1
                            && Math.Abs(settledPixel[0] - settledSurfaceColor.B) <= 1,
                            "the first screenshot after navigation should contain the visual state committed on the third render turn");
                    }

                    Require(published.Count == 1
                        && published[0].Type == "app.screenshot.captured"
                        && published[0].Data is AIArenaScreenshotControlResult eventResult
                        && eventResult.Path.Equals(capture.Path, StringComparison.OrdinalIgnoreCase)
                        && eventResult.ByteSize == capture.ByteSize, "successful screenshot capture should publish one metadata-complete event");
                    Require(visibleReceipt is not null
                        && visibleReceipt.Path.Equals(capture.Path, StringComparison.OrdinalIgnoreCase), "successful screenshot capture should also drive a nonmodal in-app receipt");
                    var serialized = AIArenaControlPlaneProtocol.Serialize(captureResponse);
                    Require(serialized.Contains("\"ByteSize\":", StringComparison.Ordinal)
                        && serialized.Contains("\"PixelWidth\":", StringComparison.Ordinal)
                        && serialized.Contains("\"PixelHeight\":", StringComparison.Ordinal), "screenshot response schema should expose documented byte and pixel metadata");

                    var duplicateResponse = PumpDispatcherTask(handler.ExecuteAsync(captureRequest with { Id = "duplicate" }));
                    Require(!duplicateResponse.Ok
                        && duplicateResponse.ErrorCode == "already_exists"
                        && published.Count == 1
                        && File.ReadAllBytes(capture.Path).SequenceEqual(initialBytes), "app screenshot handler should not overwrite a target or publish a success event for the duplicate request");
                    Require(!Directory.EnumerateFiles(Path.GetDirectoryName(capture.Path)!, "*.tmp").Any(), "successful and rejected screenshot operations should leave no temporary files");

                    Require(AIArenaControlPlaneProtocol.TryParseRequest(
                        """{"id":"invalid","command":"app.screenshot","args":{"path":42}}""",
                        out var invalidRequest,
                        out var invalidParseError), $"invalid screenshot argument shape should parse at the protocol boundary: {invalidParseError}");
                    var invalidResponse = PumpDispatcherTask(handler.ExecuteAsync(invalidRequest));
                    Require(!invalidResponse.Ok && invalidResponse.ErrorCode == "invalid_argument" && published.Count == 1, "app screenshot handler should reject a non-string path without capturing or publishing");

                    var mainWindow = ReadMainWindowSource();
                    Require(mainWindow.Contains("_appControlHandler.CanHandle(request.Command)", StringComparison.Ordinal)
                        && mainWindow.Contains("operationCancellationToken => _appControlHandler.ExecuteAsync(request, operationCancellationToken)", StringComparison.Ordinal), "MainWindow should delegate app screenshots through the tracked control-operation boundary");
                    Require(!mainWindow.Contains("case AIArenaControlCommands.AppScreenshot", StringComparison.Ordinal), "MainWindow should not duplicate app screenshot routing in its command switch");
                }
                finally
                {
                    if (navigationRendering is not null)
                    {
                        CompositionTarget.Rendering -= navigationRendering;
                    }
                    window.Close();
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }

                static T PumpDispatcherTask<T>(Task<T> task)
                {
                    if (!task.IsCompleted)
                    {
                        var frame = new DispatcherFrame();
                        task.ContinueWith(
                            _ => frame.Continue = false,
                            CancellationToken.None,
                            TaskContinuationOptions.None,
                            TaskScheduler.FromCurrentSynchronizationContext());
                        Dispatcher.PushFrame(frame);
                    }

                    return task.GetAwaiter().GetResult();
                }
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void ProviderControlHandlerRoutesCommandsAndPublishesEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-provider-handler-{Guid.NewGuid():N}");
        const string apiToken = "provider-handler-secret";
        try
        {
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.OpenAiCompatible,
                Model = "seed-model",
                Timeout = 30,
                Temperature = 0.4,
                MaxOutputTokens = 512
            };
            sessionStore.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
            SessionSummary? active = sessionStore.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var configurationRefreshes = 0;
            var activeSessionRefreshes = 0;
            using var operationLock = new SemaphoreSlim(1, 1);
            var configuration = new ProviderConfigurationControlService(
                sessionStore,
                eventLogStore,
                operationLock,
                () => active,
                () => false,
                (_, _, _) =>
                {
                    configurationRefreshes++;
                    return Task.CompletedTask;
                });
            var httpHandler = new TestHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":[{"id":"catalog-a"},{"id":"catalog-b"}]}""", Encoding.UTF8, "application/json")
                    };
                }

                if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}]}""", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var httpClient = new HttpClient(httpHandler);
            var health = new ModelProviderHealthService(httpClient);
            var runtime = new ProviderRuntimeService(
                sessionStore,
                health,
                new ProviderReachabilityService(sessionStore, eventLogStore, health));
            var eventHub = new AIArenaControlPlaneEventHub();
            var published = new List<AIArenaControlEvent>();
            using var subscription = eventHub.Subscribe(published.Add);
            var handler = new AIArenaProviderControlHandler(
                configuration,
                runtime,
                () => active,
                (_, _) =>
                {
                    activeSessionRefreshes++;
                    return Task.CompletedTask;
                },
                eventHub);

            foreach (var command in new[]
                     {
                         AIArenaControlCommands.ProviderState,
                         AIArenaControlCommands.ProviderConfigSet,
                         AIArenaControlCommands.ProviderModelSet,
                         AIArenaControlCommands.ProviderTest,
                         AIArenaControlCommands.ProviderModelsRefresh
                     })
            {
                Require(handler.CanHandle(command), $"provider handler should claim {command}");
            }
            Require(!handler.CanHandle(AIArenaControlCommands.AgentState), "provider handler should reject commands outside its family");

            var stateResponse = handler.ExecuteAsync(ParseProviderRequest("""{"id":"state","command":"provider.state"}""")).GetAwaiter().GetResult();
            var configRequestJson = JsonSerializer.Serialize(new
            {
                id = "config",
                command = "provider.config.set",
                args = new
                {
                    baseUrl = "http://127.0.0.1:1234/v1",
                    apiMode = "openai_compatible",
                    apiToken,
                    model = "handler-model",
                    alphaModel = "alpha-handler",
                    timeoutSeconds = 45
                }
            });
            var configResponse = handler.ExecuteAsync(ParseProviderRequest(configRequestJson)).GetAwaiter().GetResult();
            var modelResponse = handler.ExecuteAsync(ParseProviderRequest("""{"id":"model","command":"provider.model.set","args":{"model":"unified-handler","refreshModels":false}}""")).GetAwaiter().GetResult();
            var modelsResponse = handler.ExecuteAsync(ParseProviderRequest("""{"id":"models","command":"provider.models.refresh"}""")).GetAwaiter().GetResult();
            var testResponse = handler.ExecuteAsync(ParseProviderRequest("""{"id":"test","command":"provider.test","args":{"allRoles":true}}""")).GetAwaiter().GetResult();
            var enrichedStateResponse = handler.ExecuteAsync(ParseProviderRequest("""{"id":"enriched","command":"provider.state"}""")).GetAwaiter().GetResult();
            var unknownResponse = handler.ExecuteAsync(new AIArenaControlRequest("unknown", AIArenaControlCommands.AgentState, null)).GetAwaiter().GetResult();

            Require(stateResponse.Ok && configResponse.Ok && modelResponse.Ok && testResponse.Ok && modelsResponse.Ok && enrichedStateResponse.Ok, "provider handler should route every owned command to a successful focused service path");
            Require(!unknownResponse.Ok && unknownResponse.ErrorCode == "unknown_command", "provider handler should return a stable error if called directly for an unowned command");
            Require(configurationRefreshes == 2, "provider config and model commands should each refresh the host once");
            Require(activeSessionRefreshes == 1, "a persisted provider test should refresh active session state once");
            Require(published.Select(item => item.Type).SequenceEqual([
                "provider.config.changed",
                "provider.model.changed",
                "provider.models.refreshed",
                "provider.test.completed"
            ]), "provider handler should publish one ordered event for every mutating or diagnostic command");
            Require(httpHandler.Requests.Count(uri => uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) == 1
                && httpHandler.Requests.Count(uri => uri.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) == 1, "provider handler diagnostics should perform exactly one completion and one model-list request");
            var persisted = sessionStore.LoadSnapshotAsync().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("provider handler snapshot should persist");
            Require(persisted.Configs[ModelProviderRouting.SharedConfigKey].Model == "unified-handler"
                && ProviderConfigurationControlService.RoleKeys.All(role => persisted.Configs[role].Model == "unified-handler"), "provider.model.set should delegate one unified model through shared and role routing");
            var enrichedJson = AIArenaControlPlaneProtocol.Serialize(enrichedStateResponse);
            Require(enrichedJson.Contains("catalog-a", StringComparison.Ordinal)
                && enrichedJson.Contains("catalog-b", StringComparison.Ordinal), "provider state should retain handler-cached advertised models after discovery");
            var testJson = AIArenaControlPlaneProtocol.Serialize(testResponse);
            Require(testJson.Contains("catalog-a", StringComparison.Ordinal)
                && testJson.Contains("catalog-b", StringComparison.Ordinal), "provider.test should preserve and return a valid cached model catalog when the provider fingerprint is unchanged");
            var internalState = configuration.CaptureAsync().GetAwaiter().GetResult();
            Require(!string.IsNullOrWhiteSpace(internalState.ConfigurationIdentity), "captured provider state should carry an internal configuration fingerprint");
            var publicJson = string.Join(
                Environment.NewLine,
                new[] { stateResponse, configResponse, modelResponse, testResponse, modelsResponse, enrichedStateResponse }
                    .Select(AIArenaControlPlaneProtocol.Serialize))
                + AIArenaControlPlaneProtocol.Serialize(published);
            Require(!publicJson.Contains(apiToken, StringComparison.Ordinal), "provider handler responses and events must never serialize the configured API token");
            Require(!publicJson.Contains("ConfigurationIdentity", StringComparison.OrdinalIgnoreCase)
                && !publicJson.Contains(internalState.ConfigurationIdentity, StringComparison.Ordinal), "provider handler JSON and events must not disclose the internal configuration fingerprint");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static AIArenaControlRequest ParseProviderRequest(string json)
        {
            Require(AIArenaControlPlaneProtocol.TryParseRequest(json, out var request, out var error), $"provider control request should parse: {error}");
            return request;
        }
    }

    static void ProviderDiagnosticsCacheRejectsSwitchedAndEditedFingerprints()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-provider-fingerprint-{Guid.NewGuid():N}");
        try
        {
            var sessionStore = new SessionStore(root);
            var eventLogStore = new EventLogStore(root);
            sessionStore.SaveSnapshotAsync(
                ProviderSnapshot("session-a-model", "session-a-placeholder", timeout: 30),
                "session-a").GetAwaiter().GetResult();
            sessionStore.SaveSnapshotAsync(
                ProviderSnapshot("session-b-model", "session-b-placeholder", timeout: 45),
                "session-b").GetAwaiter().GetResult();
            var sessions = sessionStore.ListSessionsAsync().GetAwaiter().GetResult()
                .ToDictionary(session => session.Id, StringComparer.OrdinalIgnoreCase);
            SessionSummary? active = sessions["session-a"];
            var switchDuringDiscovery = true;
            var switchDuringTest = false;
            var httpHandler = new TestHttpMessageHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? "";
                var authorization = request.Headers.TryGetValues("Authorization", out var values)
                    ? string.Join(",", values)
                    : "";
                if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    var model = authorization.Contains("session-a-placeholder", StringComparison.Ordinal)
                        ? "catalog-session-a"
                        : "catalog-session-b";
                    if (switchDuringDiscovery)
                    {
                        switchDuringDiscovery = false;
                        active = sessions["session-b"];
                    }

                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new { data = new[] { new { id = model } } }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    if (switchDuringTest)
                    {
                        switchDuringTest = false;
                        active = sessions["session-a"];
                    }

                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"choices":[{"message":{"content":"ok"}}]}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            });
            using var httpClient = new HttpClient(httpHandler);
            var health = new ModelProviderHealthService(httpClient);
            using var operationLock = new SemaphoreSlim(1, 1);
            var configuration = new ProviderConfigurationControlService(
                sessionStore,
                eventLogStore,
                operationLock,
                () => active,
                () => false,
                (_, _, _) => Task.CompletedTask);
            var events = new AIArenaControlPlaneEventHub();
            var published = new List<AIArenaControlEvent>();
            using var subscription = events.Subscribe(published.Add);
            var handler = new AIArenaProviderControlHandler(
                configuration,
                new ProviderRuntimeService(
                    sessionStore,
                    health,
                    new ProviderReachabilityService(sessionStore, eventLogStore, health)),
                () => active,
                (_, _) => Task.CompletedTask,
                events);

            var switchedDiscovery = handler.ExecuteAsync(Parse("""{"id":"stale-models","command":"provider.models.refresh"}""")).GetAwaiter().GetResult();
            var switchedDiscoveryJson = AIArenaControlPlaneProtocol.Serialize(switchedDiscovery);
            Require(active?.Id == "session-b"
                && switchedDiscovery.Ok
                && switchedDiscoveryJson.Contains("\"Stale\":true", StringComparison.OrdinalIgnoreCase)
                && !switchedDiscoveryJson.Contains("catalog-session-a", StringComparison.Ordinal), "model discovery captured for one session should be discarded when the active session switches before caching");
            var stateAfterSwitchedDiscovery = State(handler, "state-after-stale-models");
            Require(stateAfterSwitchedDiscovery.SessionId == "session-b"
                && stateAfterSwitchedDiscovery.AdvertisedModels.Count == 0
                && stateAfterSwitchedDiscovery.LastModelListCheckedAt is null, "a switched provider should not inherit the previous session's discovered catalog or timestamp");

            var freshModels = handler.ExecuteAsync(Parse("""{"id":"fresh-models","command":"provider.models.refresh"}""")).GetAwaiter().GetResult();
            var stateB = State(handler, "state-b-with-catalog");
            Require(freshModels.Ok
                && stateB.AdvertisedModels.SequenceEqual(["catalog-session-b"])
                && stateB.AdvertisedModelCount == 1
                && stateB.LastModelListCheckedAt.HasValue
                && stateB.LastHealthCheckedAt is null, "current-session model discovery should populate the matching provider cache only");
            var sessionBIdentity = stateB.ConfigurationIdentity;

            active = sessions["session-a"];
            var stateA = State(handler, "state-a-after-b-cache");
            Require(stateA.AdvertisedModels.Count == 0
                && stateA.LastModelListCheckedAt is null, "switching back to another session should not expose a cached catalog from the previous session");

            active = sessions["session-b"];
            switchDuringTest = true;
            var switchedTest = handler.ExecuteAsync(Parse("""{"id":"switched-test","command":"provider.test"}""")).GetAwaiter().GetResult();
            var switchedTestJson = AIArenaControlPlaneProtocol.Serialize(switchedTest);
            Require(active?.Id == "session-a"
                && switchedTest.Ok
                && !switchedTestJson.Contains("catalog-session-b", StringComparison.Ordinal), "provider.test captured for one session should not attach that session's diagnostics cache to a newly active session");
            var stateAAfterTest = State(handler, "state-a-after-switched-test");
            Require(stateAAfterTest.AdvertisedModels.Count == 0
                && stateAAfterTest.LastHealthCheckedAt is null, "switched provider.test results should not enrich the newly active provider state");

            active = sessions["session-b"];
            var stateBAfterTest = State(handler, "state-b-after-switched-test");
            Require(stateBAfterTest.AdvertisedModels.SequenceEqual(["catalog-session-b"])
                && stateBAfterTest.LastHealthCheckedAt is null, "a provider.test result rejected by the handler fingerprint check should not mutate the prior session cache");

            var timeoutEdit = handler.ExecuteAsync(Parse("""{"id":"timeout-edit","command":"provider.config.set","args":{"timeoutSeconds":46}}""")).GetAwaiter().GetResult();
            var editedState = State(handler, "state-b-after-timeout-edit");
            Require(timeoutEdit.Ok
                && editedState.TimeoutSeconds == 46
                && editedState.AdvertisedModels.Count == 0
                && editedState.LastModelListCheckedAt is null
                && !editedState.ConfigurationIdentity.Equals(sessionBIdentity, StringComparison.Ordinal), "editing timeout should change the provider fingerprint and invalidate the previous configuration's cached catalog");

            var publicJson = string.Join(
                Environment.NewLine,
                new[] { switchedDiscovery, freshModels, switchedTest, timeoutEdit }
                    .Select(AIArenaControlPlaneProtocol.Serialize))
                + AIArenaControlPlaneProtocol.Serialize(published);
            Require(!publicJson.Contains("ConfigurationIdentity", StringComparison.OrdinalIgnoreCase)
                && !publicJson.Contains(sessionBIdentity, StringComparison.Ordinal)
                && !publicJson.Contains(editedState.ConfigurationIdentity, StringComparison.Ordinal), "session/configuration fingerprints must remain internal across provider responses and events");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static ArenaSnapshot ProviderSnapshot(string model, string token, int timeout)
        {
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                ApiMode = ModelProviderApiModes.OpenAiCompatible,
                ApiToken = token,
                Model = model,
                Timeout = timeout,
                Temperature = 0.3,
                MaxOutputTokens = 512,
                ContextLength = 4096
            };
            return snapshot;
        }

        static AIArenaControlRequest Parse(string json)
        {
            Require(AIArenaControlPlaneProtocol.TryParseRequest(json, out var request, out var error), $"provider fingerprint request should parse: {error}");
            return request;
        }

        static AIArenaProviderControlState State(AIArenaProviderControlHandler handler, string id)
        {
            var response = handler.ExecuteAsync(new AIArenaControlRequest(id, AIArenaControlCommands.ProviderState, null)).GetAwaiter().GetResult();
            Require(response.Ok && response.Data is AIArenaProviderControlState, "provider fingerprint state request should return typed state");
            return (AIArenaProviderControlState)response.Data!;
        }
    }

    static void ControlPlaneMainWindowDelegatesProviderCommands()
    {
        var mainWindow = ReadMainWindowSource();
        Require(mainWindow.Contains("_providerControlHandler.CanHandle(request.Command)", StringComparison.Ordinal), "MainWindow should delegate the provider command family before its legacy command switch");
        Require(mainWindow.Contains("RunProviderControlOperationAsync(request, cancellationToken)", StringComparison.Ordinal), "MainWindow should track provider commands through the operation coordinator");
        Require(mainWindow.Contains("operationCancellationToken => _providerControlHandler.ExecuteAsync(request, operationCancellationToken)", StringComparison.Ordinal), "MainWindow should pass the tracked linked cancellation token into the focused provider handler");
        Require(mainWindow.Contains("response = await operation(linkedCancellation.Token)", StringComparison.Ordinal), "MainWindow should link caller and shutdown cancellation before invoking tracked control handlers");
        foreach (var command in new[]
                 {
                     nameof(AIArenaControlCommands.ProviderState),
                     nameof(AIArenaControlCommands.ProviderConfigSet),
                     nameof(AIArenaControlCommands.ProviderModelSet),
                     nameof(AIArenaControlCommands.ProviderTest),
                     nameof(AIArenaControlCommands.ProviderModelsRefresh)
                 })
        {
            Require(!mainWindow.Contains($"case AIArenaControlCommands.{command}", StringComparison.Ordinal), $"MainWindow should not retain a duplicated {command} switch case");
        }
    }

    static void EmptyArgumentsAreNotMistakenForMissingOnes()
    {
        static AIArenaControlRequest Parse(string json)
        {
            Require(AIArenaControlPlaneProtocol.TryParseRequest(json, out var request, out var error), $"request should parse: {error}");
            return request;
        }

        // Typing an empty string clears a field, so "" and "not supplied" are
        // different instructions. OptionalString collapses both to null, which
        // let shell.input.type accept a request carrying no text at all and
        // silently wipe the target field - the failure mode of a mistyped
        // argument name was destructive rather than an error.
        var supplied = Parse("""{"id":"1","command":"shell.input.type","args":{"text":""}}""");
        Require(AIArenaControlArguments.TryGetString(supplied, "text", out var empty), "an empty string is still an argument");
        Require(empty.Length == 0, "an empty argument should read back empty");
        Require(
            AIArenaControlArguments.OptionalString(supplied, "text") is null,
            "OptionalString still collapses empty to null, which is exactly why the presence-aware read exists");

        var omitted = Parse("""{"id":"2","command":"shell.input.type","args":{}}""");
        Require(!AIArenaControlArguments.TryGetString(omitted, "text", out _), "an omitted argument should report absent");

        var nulled = Parse("""{"id":"3","command":"shell.input.type","args":{"text":null}}""");
        Require(!AIArenaControlArguments.TryGetString(nulled, "text", out _), "an explicit null should count as absent");

        var valued = Parse("""{"id":"4","command":"shell.input.type","args":{"text":"internet"}}""");
        Require(
            AIArenaControlArguments.TryGetString(valued, "text", out var text) && text == "internet",
            "a real value should read back unchanged");

        // And the dispatch must actually use it, or the distinction is academic.
        var dispatch = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.ControlPlane.cs"));
        Require(
            dispatch.Contains("TryGetString(request, \"text\"", StringComparison.Ordinal),
            "shell.input.type should read text with the presence-aware accessor");
    }

    static void ControlPlanePublishesRequiredEventVocabulary()
    {
        var mainWindow = ReadMainWindowSource();
        var matchSetupHandler = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaMatchSetupControlHandler.cs"));
        var settingsHandler = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaSettingsControlHandler.cs"));
        var agentWorkspace = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/AgentWorkspaceCoordinator.cs"));
        var host = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/ControlPlane/AIArenaControlPlaneHost.cs"));
        var combined = string.Concat(mainWindow, matchSetupHandler, settingsHandler, agentWorkspace, host);
        Require(combined.Contains("\"status.changed\"", StringComparison.Ordinal), "control-plane should publish status changed events");
        Require(combined.Contains("\"shell.overlay.changed\"", StringComparison.Ordinal), "control-plane should publish shell overlay changes");
        Require(combined.Contains("\"match.matrix.changed\"", StringComparison.Ordinal), "control-plane should publish relationship matrix changes");
        Require(mainWindow.Contains("MatchSetupOpen =", StringComparison.Ordinal) && mainWindow.Contains("SettingsOpen =", StringComparison.Ordinal), "uniform command state should include Match Setup and Settings overlays");
        Require(mainWindow.Contains("\"match.generation.changed\"", StringComparison.Ordinal), "control-plane should publish match generation changes");
        Require(mainWindow.Contains("GenerationHistoryCount =", StringComparison.Ordinal), "uniform command state should include replay history count");
        Require(combined.Contains("\"message.added\"", StringComparison.Ordinal), "control-plane should publish transcript message events");
        Require(combined.Contains("\"command.staged\"", StringComparison.Ordinal), "control-plane should publish staged command events");
        Require(combined.Contains("\"command.running\"", StringComparison.Ordinal), "control-plane should publish command running events");
        Require(combined.Contains("\"command.completed\"", StringComparison.Ordinal), "control-plane should publish command completed events");
        Require(combined.Contains("\"file.receipt.captured\"", StringComparison.Ordinal), "control-plane should publish file receipt events");
        Require(combined.Contains("\"artifact.detected\"", StringComparison.Ordinal), "control-plane should publish artifact events");
        Require(combined.Contains("\"loop.guard.paused\"", StringComparison.Ordinal), "control-plane should publish loop guard events");
        Require(combined.Contains("\"provider.online\"", StringComparison.Ordinal), "control-plane should publish provider online events");
        Require(combined.Contains("\"provider.offline\"", StringComparison.Ordinal), "control-plane should publish provider offline events");
        Require(combined.Contains("\"agent.runbook.started\"", StringComparison.Ordinal), "control-plane should publish runbook start events");
        Require(combined.Contains("\"agent.runbook.resumed\"", StringComparison.Ordinal), "control-plane should publish runbook resume events");
        Require(combined.Contains("\"agent.runbook.checkpointed\"", StringComparison.Ordinal), "control-plane should publish operator checkpoint events");
    }

    static void ControlPlaneNavigationClosesSettingsForMainViews()
    {
        var mainWindow = ReadMainWindowSource();
        var selectStart = mainWindow.IndexOf("private bool SelectControlPlaneView", StringComparison.Ordinal);
        var selectedStart = mainWindow.IndexOf("private string SelectedControlPlaneView", StringComparison.Ordinal);
        Require(selectStart >= 0, "MainWindow should define control-plane view selection");
        Require(selectedStart >= 0, "MainWindow should define control-plane selected-view reporting");
        var selectBlock = mainWindow[selectStart..selectedStart];
        Require(selectBlock.Contains("AppSettingsWorkflow.SetVisible(false);", StringComparison.Ordinal), "main control-plane navigation views should close Settings");
        Require(selectBlock.Contains("case \"world\":", StringComparison.Ordinal) && selectBlock.Contains("if (!IsWorldDebugEnabled(_wpfSettings))", StringComparison.Ordinal), "control-plane World navigation should obey the default-off debug gate");
        Require(selectBlock.Contains("case \"agent\":", StringComparison.Ordinal) && selectBlock.Contains("if (!IsAgentWorkspaceEnabled(_wpfSettings))", StringComparison.Ordinal), "control-plane Agent navigation should obey the normal Settings preference");
        Require(mainWindow.Contains("\"feature_disabled\"", StringComparison.Ordinal) && mainWindow.Contains("Settings -> Agent workspace", StringComparison.Ordinal), "disabled optional surfaces should report a stable actionable control-plane error");
        var selectedBlock = mainWindow[selectedStart..Math.Min(mainWindow.Length, selectedStart + 800)];
        var settingsIndex = selectedBlock.IndexOf("AppSettingsPanel.Visibility", StringComparison.Ordinal);
        var agentIndex = selectedBlock.IndexOf("AgentWorkspacePanel.Visibility", StringComparison.Ordinal);
        Require(settingsIndex >= 0 && agentIndex >= 0 && settingsIndex < agentIndex, "selected view should report Settings as topmost when visible");
    }

    static void ControlPlaneMatchSetupOpenOwnsVisibleOverlay()
    {
        var mainWindow = ReadMainWindowSource();
        Require(
            mainWindow.Contains(
                "BuildMatchSetupControlState,\r\n            OpenMatchSetupFromControlPlane,",
                StringComparison.Ordinal)
            || mainWindow.Contains(
                "BuildMatchSetupControlState,\n            OpenMatchSetupFromControlPlane,",
                StringComparison.Ordinal),
            "match.setup.open should use the host path that owns overlay cleanup");

        var open = CSharpMethodBlock(mainWindow, "private void OpenMatchSetupFromControlPlane()");
        var closeSettings = open.IndexOf("AppSettingsWorkflow.SetVisible(false)", StringComparison.Ordinal);
        var closeFlyouts = open.IndexOf("CloseNamedTransientShellFlyouts()", StringComparison.Ordinal);
        var showMatchSetup = open.IndexOf("ShowCustomMatchPanel()", StringComparison.Ordinal);
        Require(closeSettings >= 0, "match.setup.open should close Settings before changing the visible workspace");
        Require(closeFlyouts > closeSettings, "match.setup.open should dismiss transient flyouts after closing Settings");
        Require(showMatchSetup > closeFlyouts, "match.setup.open should reveal Match Setup only after overlay cleanup completes");
        Require(open.Contains("_settingsFocusReturnTarget", StringComparison.Ordinal), "match.setup.open should preserve the Settings opener for the eventual Match Setup return path");

        var closeTransient = CSharpMethodBlock(mainWindow, "private void CloseNamedTransientShellFlyouts()");
        foreach (var marker in new[]
                 {
                     "_providerReachabilityCoordinator?.ClosePopup()",
                     "_transcriptSearchCoordinator?.CloseSearch()",
                     "ViewMenuPopup.IsOpen = false",
                     "DebugMenuPopup.IsOpen = false",
                     "_diagnosticsWorkflowCoordinator?.CloseDetail()",
                     "GenerationHelpPopup.IsOpen = false",
                     "AgentComposerControlsPopup.IsOpen = false",
                     "_agentPerformanceCoordinator?.CloseDetail()"
                 })
        {
            Require(closeTransient.Contains(marker, StringComparison.Ordinal), $"Match Setup overlay cleanup should include {marker}");
        }

        var select = CSharpMethodBlock(mainWindow, "private bool SelectControlPlaneView(string view)");
        var customMatchStart = select.IndexOf("case \"custom-match\":", StringComparison.Ordinal);
        var worldStart = select.IndexOf("case \"world\":", customMatchStart, StringComparison.Ordinal);
        Require(customMatchStart >= 0 && worldStart > customMatchStart, "navigation.select should retain its Match Setup aliases");
        var customMatch = select[customMatchStart..worldStart];
        Require(customMatch.Contains("OpenMatchSetupFromControlPlane()", StringComparison.Ordinal), "navigation.select and match.setup.open should share one overlay-consistent path");
        Require(!customMatch.Contains("ShowCustomMatchPanel()", StringComparison.Ordinal), "navigation.select should not bypass Match Setup overlay cleanup");

        var state = CSharpMethodBlock(mainWindow, "private object BuildControlPlaneStateSummary()");
        Require(state.Contains("MatchSetupOpen = CustomMatchPanel.Visibility == Visibility.Visible", StringComparison.Ordinal), "post-command state should report actual Match Setup visibility");
        Require(state.Contains("SettingsOpen = AppSettingsPanel.Visibility == Visibility.Visible", StringComparison.Ordinal), "post-command state should report actual Settings visibility after cleanup");
    }

    private static string SendControlRequest(string pipeName, string request)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.None);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        writer.WriteLine(request);
        return reader.ReadLine() ?? "";
    }

    private static void WaitForClientDisconnect(Task clientTask)
    {
        try
        {
            clientTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            // Closing the server side during shutdown may disconnect the test client.
        }
    }

    private sealed class BlockingControlTarget : IAIArenaControlTarget
    {
        private readonly bool holdCancellation;
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;

        public BlockingControlTarget(bool holdCancellation = false)
        {
            this.holdCancellation = holdCancellation;
            if (!holdCancellation)
            {
                releaseCancellation.TrySetResult();
            }
        }

        public bool IsControlPlaneEnabled => true;

        public Task Started => started.Task;

        public Task CancellationStarted => cancellationStarted.Task;

        public Task CancellationObserved => cancellationObserved.Task;

        public int ActiveCalls => Volatile.Read(ref activeCalls);

        public async Task<AIArenaControlResponse> ExecuteControlCommandAsync(
            AIArenaControlRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref activeCalls);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return AIArenaControlResponse.Success(request, "unexpected");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationStarted.TrySetResult();
                if (holdCancellation)
                {
                    await releaseCancellation.Task;
                }

                _ = cancellationToken.WaitHandle.WaitOne(0);
                using var registration = cancellationToken.Register(static () => { });
                cancellationObserved.TrySetResult();
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }

        public void ReleaseCancellation()
        {
            releaseCancellation.TrySetResult();
        }
    }

    private sealed class FakeControlTarget : IAIArenaControlTarget
    {
        public bool IsControlPlaneEnabled => true;

        public int Calls { get; private set; }

        public Task<AIArenaControlResponse> ExecuteControlCommandAsync(AIArenaControlRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AIArenaControlResponse.Success(request, "ok", new { Calls }));
        }
    }
}
