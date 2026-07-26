using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Wpf;

internal static partial class Program
{
    static void SessionForkWorkflowBranchesAndSanitizesReceipts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-session-fork-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var eventStore = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            var sourceSnapshot = store.LoadSnapshotAsync("default").GetAwaiter().GetResult()!;
            sourceSnapshot.Engine.Messages.Add(new DialogueMessage
            {
                Turn = 1,
                Speaker = "Alpha",
                SpeakerId = "alpha",
                Text = "PRIVATE-FORK-CONTENT",
                Status = "ok",
                Kind = "message",
                CreatedAt = 1
            });
            sourceSnapshot.Engine.TurnCount = 1;
            sourceSnapshot.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:1234/v1",
                Model = "audit-model",
                ApiToken = "FORK-SECRET-TOKEN"
            };
            store.SaveSnapshotAsync(sourceSnapshot, "default").GetAwaiter().GetResult();

            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var exclusiveCalls = 0;
            var selectedIds = new List<string>();
            var published = new List<(string Type, string Message, object? Data)>();
            var service = new SessionForkWorkflowService(
                store,
                eventStore,
                () => active,
                () => false,
                async (_, action) =>
                {
                    exclusiveCalls++;
                    await action(CancellationToken.None);
                },
                async (id, cancellationToken) =>
                {
                    selectedIds.Add(id);
                    active = (await store.ListSessionsAsync(cancellationToken))
                        .Single(session => session.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                },
                (type, message, data) => published.Add((type, message, data)));

            var result = service.ForkCurrentAsync("review branch").GetAwaiter().GetResult();

            Require(result.Ok && result.ErrorCode == "", "fork workflow should return a stable success result");
            Require(exclusiveCalls == 1, "fork workflow should use the host exclusive-operation boundary exactly once");
            Require(result.Receipt is not null, "fork workflow should return a lineage/count receipt");
            var receipt = result.Receipt!;
            Require(receipt.SourceSessionId == "default" && receipt.ForkSessionId == "review-branch", "fork receipt should expose sanitized direct lineage ids");
            Require(receipt.MessageCount == 1 && receipt.TurnCount == 1 && receipt.ActiveAgentCount == 4, "fork receipt should expose bounded state counts");
            Require(selectedIds.SequenceEqual([receipt.ForkSessionId]) && active?.Id == receipt.ForkSessionId, "fork workflow should select the created branch");

            var branch = store.LoadSnapshotAsync(receipt.ForkSessionId).GetAwaiter().GetResult()!;
            Require(branch.Engine.Messages.Single().Text == "PRIVATE-FORK-CONTENT", "fork workflow should retain full match state in the branch");
            Require(branch.ForkLineage?.ParentSessionId == "default", "fork workflow should preserve Core lineage metadata");

            var receiptJson = JsonSerializer.Serialize(receipt);
            Require(!receiptJson.Contains("PRIVATE-FORK-CONTENT", StringComparison.Ordinal)
                && !receiptJson.Contains("FORK-SECRET-TOKEN", StringComparison.Ordinal)
                && !receiptJson.Contains("audit-model", StringComparison.Ordinal), "fork receipt should exclude transcript and provider details");
            Require(published.Count == 1 && published[0].Type == "session.forked", "successful workflow should publish one session fork event");
            var durableEvent = File.ReadAllText(eventStore.EventPath(receipt.ForkSessionId));
            Require(durableEvent.Contains("control_session_fork_created", StringComparison.Ordinal), "successful workflow should persist a fork audit event on the branch");
            Require(!durableEvent.Contains("PRIVATE-FORK-CONTENT", StringComparison.Ordinal)
                && !durableEvent.Contains("FORK-SECRET-TOKEN", StringComparison.Ordinal), "fork audit events should contain only sanitized lineage and counts");
            var sourceEvent = File.ReadAllText(eventStore.EventPath("default"));
            Require(sourceEvent.Contains("control_session_fork_child_created", StringComparison.Ordinal), "source audit should record its newly created child branch");
            Require(!sourceEvent.Contains("PRIVATE-FORK-CONTENT", StringComparison.Ordinal)
                && !sourceEvent.Contains("FORK-SECRET-TOKEN", StringComparison.Ordinal), "source fork events should also contain only sanitized lineage and counts");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void SessionForkWorkflowReturnsStableFailureCodes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-session-fork-failures-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            var eventStore = new EventLogStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var busy = true;
            var exclusiveCalls = 0;

            SessionForkWorkflowService Create(
                Func<string, Func<CancellationToken, Task>, Task>? run = null)
            {
                return new SessionForkWorkflowService(
                    store,
                    eventStore,
                    () => active,
                    () => busy,
                    run ?? (async (_, action) =>
                    {
                        exclusiveCalls++;
                        await action(CancellationToken.None);
                    }),
                    (_, _) => Task.CompletedTask,
                    (_, _, _) => { });
            }

            var busyResult = Create().ForkCurrentAsync().GetAwaiter().GetResult();
            Require(!busyResult.Ok && busyResult.ErrorCode == "busy" && exclusiveCalls == 0, "busy fork should fail before entering the exclusive operation");

            busy = false;
            var invalid = Create().ForkCurrentAsync("...").GetAwaiter().GetResult();
            Require(!invalid.Ok && invalid.ErrorCode == "invalid_argument" && exclusiveCalls == 0, "invalid fork names should fail before persistence");

            active = null;
            var unavailable = Create().ForkCurrentAsync().GetAwaiter().GetResult();
            Require(!unavailable.Ok && unavailable.ErrorCode == "not_found", "missing active sessions should return a stable not-found code");

            active = new SessionSummary("missing", "", true, 0, 0, 0, DateTimeOffset.UtcNow);
            var missing = Create().ForkCurrentAsync().GetAwaiter().GetResult();
            Require(!missing.Ok && missing.ErrorCode == "not_found", "disappeared source snapshots should return a stable not-found code");

            active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var failed = Create((_, _) => Task.CompletedTask).ForkCurrentAsync().GetAwaiter().GetResult();
            Require(!failed.Ok && failed.ErrorCode == "operation_failed", "an exclusive operation that does not complete should return a stable failure code");

            var selectionFailureService = new SessionForkWorkflowService(
                store,
                eventStore,
                () => active,
                () => false,
                async (_, action) => await action(CancellationToken.None),
                (_, _) => throw new InvalidOperationException("simulated selection failure"),
                (_, _, _) => { });
            var partial = selectionFailureService.ForkCurrentAsync("durable-partial").GetAwaiter().GetResult();
            Require(!partial.Ok && partial.ErrorCode == "selection_failed" && partial.Receipt?.ForkSessionId == "durable-partial", "selection failure should accurately return the durable fork receipt");
            Require(store.LoadSnapshotAsync("durable-partial").GetAwaiter().GetResult() is not null, "selection failure must not hide or discard the durable fork");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    static void SessionForkControlHandlerAndPowerShellHelperRouteSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-session-fork-handler-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(root);
            store.EnsureDefaultSessionAsync().GetAwaiter().GetResult();
            SessionSummary? active = store.ListSessionsAsync().GetAwaiter().GetResult().Single();
            var service = new SessionForkWorkflowService(
                store,
                new EventLogStore(root),
                () => active,
                () => false,
                async (_, action) => await action(CancellationToken.None),
                async (id, cancellationToken) => active = (await store.ListSessionsAsync(cancellationToken))
                    .Single(session => session.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
                (_, _, _) => { });
            var handler = new AIArenaSessionForkControlHandler(service);

            Require(handler.CanHandle("session.fork") && !handler.CanHandle("session.create"), "fork handler should own only its focused command");
            Require(AIArenaControlPlaneProtocol.TryParseRequest(
                """{"id":"bad","command":"session.fork","args":{"name":42}}""",
                out var badRequest,
                out _), "invalid fork request should parse at the protocol boundary");
            var bad = handler.ExecuteAsync(badRequest).GetAwaiter().GetResult();
            Require(!bad.Ok && bad.ErrorCode == "invalid_argument", "fork handler should reject non-string names before mutation");

            Require(AIArenaControlPlaneProtocol.TryParseRequest(
                """{"id":"good","command":"session.fork","args":{"name":"handler-branch"}}""",
                out var request,
                out _), "valid fork request should parse");
            var response = handler.ExecuteAsync(request).GetAwaiter().GetResult();
            Require(response.Ok && response.Data is AIArenaSessionForkReceipt receipt && receipt.ForkSessionId == "handler-branch", "fork handler should return the shared sanitized receipt");
            Require(active?.Id == "handler-branch", "fork handler should select the created branch through the shared workflow");

            var script = File.ReadAllText(FindWorkspaceFile("scripts/ai-arena-control.ps1"));
            Require(script.Contains("function New-AIArenaSessionFork", StringComparison.Ordinal), "PowerShell client should expose a typed session fork helper");
            Require(script.Contains("Invoke-AIArena -Command 'session.fork' -Args $forkArgs", StringComparison.Ordinal), "PowerShell fork helper should route through session.fork");
            Require(script.Contains("$PSBoundParameters.ContainsKey('Name')", StringComparison.Ordinal), "PowerShell fork helper should preserve optional automatic naming");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
