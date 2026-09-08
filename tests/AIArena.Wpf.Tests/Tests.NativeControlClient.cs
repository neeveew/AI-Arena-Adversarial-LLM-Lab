using System.IO;
using System.Text;
using AIArena.Native.Protocol;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private const string NativeClientOperationId = "0198c160-0000-7000-8000-000000000001";

    static void NativeControlClientDiscoversTypedCapabilities()
    {
        foreach (var fullCatalog in new[] { true, false })
        {
            var calls = new List<Request>();
            var advertised = fullCatalog
                ? new[] { "health.state", "inference.models.state", "operation.list", "diagnostics.bundle.create", "operation.state", "operation.wait", "operation.cancel", "future.optional", "health.state" }
                : new[] { "health.state", "diagnostics.bundle.create", "operation.state", "operation.wait" };
            var client = new NativeControlClient((request, timeout, token) =>
            {
                Require(timeout == TimeSpan.FromSeconds(10), "Discovery reads must retain the bounded default deadline");
                Require(Guid.TryParseExact(request.RequestId, "N", out _) && NativeControlClient.IsUuidV7(request.CorrelationId),
                    "Every native request needs its own transport request ID and UUIDv7 correlation");
                var captured = request.Clone();
                captured.Token = "";
                calls.Add(captured);
                var response = NativeClientResponse(request);
                switch (request.ActionCase)
                {
                    case Request.ActionOneofCase.Discover:
                        response.Capabilities = new Capabilities { ProtocolVersion = 1, MaximumRequestBytes = 262144, MaximumResponseBytes = 1048576 };
                        response.Capabilities.Commands.Add(advertised.Select(command => new Capability { Command = command }));
                        break;
                    case Request.ActionOneofCase.Health:
                        response.Health = new Health { State = "degraded", ComponentCount = 3 };
                        response.Health.BlockedCapabilities.Add("fixture.blocked");
                        break;
                    case Request.ActionOneofCase.Models:
                        response.Models = new Models { RunningCount = 1, NeedsAttentionCount = 1 };
                        response.Models.ModelInstanceIds.Add(["model-a", "model-b"]);
                        response.Models.GenerationIds.Add("generation-a");
                        break;
                    case Request.ActionOneofCase.OperationList:
                        Require(request.OperationList.Limit == 100, "The connection inventory must request a bounded operation list");
                        response.Operations = new OperationList { Truncated = true };
                        response.Operations.OperationIds.Add(NativeClientOperationId);
                        break;
                    default:
                        throw new InvalidOperationException("Connecting must only discover and inspect advertised native services.");
                }
                return Task.FromResult(response);
            });

            var snapshot = client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            Request.ActionOneofCase[] expected = fullCatalog
                ? [Request.ActionOneofCase.Discover, Request.ActionOneofCase.Health, Request.ActionOneofCase.Models, Request.ActionOneofCase.OperationList]
                : [Request.ActionOneofCase.Discover, Request.ActionOneofCase.Health];
            Require(calls.Select(request => request.ActionCase).SequenceEqual(expected),
                "Connection must issue typed discovery first and probe only commands the native app advertises");
            Require(snapshot.CanCreateDiagnosticBundle == fullCatalog,
                "Creation must remain unavailable until create, state, wait, and cancel are all advertised");
            Require(snapshot.Commands.Count == advertised.Distinct(StringComparer.Ordinal).Count()
                && snapshot.HealthSummary.Contains("degraded", StringComparison.Ordinal),
                "Connection state must retain discovered capabilities and authoritative health without duplicate commands");
            Require(fullCatalog
                    ? snapshot.ModelsSummary.Contains("2 model instances", StringComparison.Ordinal)
                        && snapshot.OperationsSummary.Contains("more available", StringComparison.Ordinal)
                    : snapshot.ModelsSummary == "Model inventory is unavailable."
                        && snapshot.OperationsSummary == "Operation inventory is unavailable.",
                "Omitted inventory capabilities must stay unavailable instead of looking like empty successful probes");
        }
    }

    static void NativeControlClientRetryIdentityAndCancellationReconcile()
    {
        const string destination = @"C:\NativeClientFixture\diagnostics.zip";
        const string createKey = "fixture-create-stable";
        const string cancelKey = "fixture-cancel-stable";
        const string fixtureSecret = "native-fixture-secret";
        var captured = new List<Request>();
        var actualRequests = new List<Request>();
        var createAttempts = 0;
        var client = new NativeControlClient((request, timeout, token) =>
        {
            request.Token = fixtureSecret;
            actualRequests.Add(request);
            var evidence = request.Clone();
            evidence.Token = "";
            captured.Add(evidence);
            var response = NativeClientResponse(request);
            switch (request.ActionCase)
            {
                case Request.ActionOneofCase.CreateDiagnosticBundle:
                    if (++createAttempts == 1)
                        throw new IOException($"Lost fixture acknowledgement: {fixtureSecret} at {destination}");
                    response.Operation = NativeClientOperation("accepted", 1, terminal: false);
                    break;
                case Request.ActionOneofCase.OperationWait:
                    Require(timeout == TimeSpan.FromSeconds(20) && request.OperationWait.TimeoutMilliseconds == 15000
                        && request.OperationWait.AfterTransition == 1 && request.OperationWait.OperationId == NativeClientOperationId,
                        "Waiting must preserve the operation/cursor and bound native wait below the transport deadline");
                    Require(request.OperationWait.TerminalStates.SequenceEqual(new[] { "succeeded", "partial", "blocked", "failed", "canceled", "interrupted" }),
                        "The wait request must use the native terminal-state vocabulary");
                    response.Operation = NativeClientOperation("running", 2, terminal: false);
                    break;
                case Request.ActionOneofCase.OperationCancel:
                    response.Operation = NativeClientOperation("cancel_requested", 3, terminal: false);
                    break;
                case Request.ActionOneofCase.OperationState:
                    response.Operation = NativeClientOperation("succeeded", 4, terminal: true);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected native workflow request.");
            }
            return Task.FromResult(response);
        });

        var uncertain = NativeClientExpect<NativeControlException>(() =>
            client.CreateDiagnosticBundleAsync(destination, createKey, CancellationToken.None).GetAwaiter().GetResult());
        Require(uncertain.OutcomeUnknown && createAttempts == 1
            && !uncertain.Message.Contains(fixtureSecret, StringComparison.Ordinal)
            && !uncertain.Message.Contains(destination, StringComparison.Ordinal),
            "A lost create acknowledgement must remain uncertain, avoid automatic replay, and not expose transport details");
        var accepted = client.CreateDiagnosticBundleAsync(destination, createKey, CancellationToken.None).GetAwaiter().GetResult();
        var first = captured[0];
        var retry = captured[1];
        Require(first.IdempotencyKey == createKey && retry.IdempotencyKey == createKey
            && first.CreateDiagnosticBundle.Equals(retry.CreateDiagnosticBundle)
            && first.RequestId != retry.RequestId && first.CorrelationId != retry.CorrelationId,
            "Explicit retry must keep the exact create arguments and idempotency key while refreshing transport correlation");
        Require(first.CreateDiagnosticBundle.Destination == destination
            && first.CreateDiagnosticBundle.MaximumBytes == 32 * 1024 * 1024
            && first.CreateDiagnosticBundle.MaximumMembers == 1000
            && first.CreateDiagnosticBundle.Categories.SequenceEqual(new[] { "version", "health", "operations", "logs" }),
            "Diagnostic creation must send the explicitly approved destination and bounded category/size selection");
        var observed = client.WaitOperationAsync(accepted.OperationId, accepted.Transition, CancellationToken.None).GetAwaiter().GetResult();
        Require(observed.State == "running" && observed.Transition == 2, "Wait must return the native transition receipt");
        var canceled = client.CancelOperationAsync(accepted.OperationId, cancelKey, CancellationToken.None).GetAwaiter().GetResult();
        var cancel = captured.Single(request => request.ActionCase == Request.ActionOneofCase.OperationCancel);
        Require(cancel.IdempotencyKey == cancelKey && cancel.IdempotencyKey != createKey
            && cancel.OperationCancel.OperationId == accepted.OperationId
            && captured[^1].ActionCase == Request.ActionOneofCase.OperationState
            && captured[^1].IdempotencyKey.Length == 0,
            "Cancel must use its separate action key and then reconcile through a fresh authoritative state query");
        Require(canceled.State == "succeeded" && canceled.IsTerminal && !canceled.CanCancel,
            "Native completion winning a cancel race must remain succeeded rather than being relabeled canceled");
        Require(actualRequests.All(request => request.Token.Length == 0) && captured.All(request => request.Token.Length == 0),
            "Successful and uncertain requests must release authentication text, and captured test evidence must omit tokens");
    }

    static void NativeControlClientRejectsUncorrelatedOperationEvidence()
    {
        (string Name, Action<Response> Change)[] invalid =
        [
            ("request identity", response => response.RequestId = "other-request"),
            ("correlation identity", response => response.CorrelationId = "0198c160-0000-7000-8000-000000000002"),
            ("command", response => response.Command = "operation.cancel"),
            ("operation identity", response => response.Operation.OperationId = "0198c160-0000-7000-8000-000000000003"),
            ("unknown state", response => response.Operation.State = "future-completion"),
            ("contradictory terminal evidence", response => response.Operation.Terminal = true),
            ("overflowing cursor", response => response.Operation.TransitionOrdinal = (ulong)long.MaxValue + 1),
            ("wrong result type", response => response.Health = new Health { State = "healthy" })
        ];
        foreach (var (name, change) in invalid)
        {
            var client = new NativeControlClient((request, _, _) =>
            {
                var response = NativeClientResponse(request);
                response.Operation = NativeClientOperation("running", 2, terminal: false);
                change(response);
                return Task.FromResult(response);
            });
            var error = NativeClientExpect<NativeControlException>(() =>
                client.GetOperationAsync(NativeClientOperationId, CancellationToken.None).GetAwaiter().GetResult());
            Require(error.OutcomeUnknown, $"Invalid {name} evidence must not be accepted as an authoritative operation result");
        }

        var incompatible = new NativeControlClient((request, _, _) =>
        {
            var response = NativeClientResponse(request);
            response.Capabilities = new Capabilities { ProtocolVersion = 99, MaximumRequestBytes = 262144, MaximumResponseBytes = 1048576 };
            return Task.FromResult(response);
        });
        var unsupported = NativeClientExpect<NativeControlException>(() => incompatible.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult());
        Require(!unsupported.OutcomeUnknown, "Unsupported discovery protocol must stop before starting a workflow");
    }

    static void NativeControlClientProtectsTokenFixturesAndCancellation()
    {
        var tokenPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tokenPath, "  native-token-fixture\r\n", new UTF8Encoding(false));
            Require(NativeControlClient.ReadTokenAsync(tokenPath, CancellationToken.None).GetAwaiter().GetResult() == "native-token-fixture",
                "The bounded token reader must accept a regular UTF-8 fixture with surrounding whitespace");
            File.WriteAllBytes(tokenPath, Enumerable.Repeat((byte)'x', 4096).ToArray());
            Require(NativeControlClient.ReadTokenAsync(tokenPath, CancellationToken.None).GetAwaiter().GetResult().Length == 4096,
                "The authentication reader must accept exactly its documented byte bound");
            byte[][] invalid = [[], [0xC3, 0x28], Encoding.UTF8.GetBytes("fixture\0secret"), Enumerable.Repeat((byte)'x', 4097).ToArray()];
            foreach (var bytes in invalid)
            {
                File.WriteAllBytes(tokenPath, bytes);
                var error = NativeClientExpect<NativeControlException>(() =>
                    NativeControlClient.ReadTokenAsync(tokenPath, CancellationToken.None).GetAwaiter().GetResult());
                Require(!error.OutcomeUnknown && !error.Message.Contains(tokenPath, StringComparison.Ordinal)
                    && !error.Message.Contains("secret", StringComparison.Ordinal),
                    "Invalid size, UTF-8, and embedded control characters must fail before transport without exposing fixture contents");
            }
        }
        finally
        {
            File.Delete(tokenPath);
        }

        Request? pendingRequest = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new NativeControlClient((request, _, token) =>
        {
            request.Token = "native-cancellation-fixture";
            pendingRequest = request;
            return Task.FromCanceled<Response>(token);
        });
        var stopped = NativeClientExpect<OperationCanceledException>(() =>
            canceled.GetOperationAsync(NativeClientOperationId, cancellation.Token).GetAwaiter().GetResult());
        Require(stopped.CancellationToken == cancellation.Token && pendingRequest?.Token.Length == 0,
            "Caller cancellation must propagate unchanged while clearing request authentication text");

        var timedOut = new NativeControlClient((_, _, _) => Task.FromException<Response>(new OperationCanceledException()));
        var deadline = NativeClientExpect<NativeControlException>(() =>
            timedOut.GetOperationAsync(NativeClientOperationId, CancellationToken.None).GetAwaiter().GetResult());
        Require(deadline.OutcomeUnknown, "A transport deadline is an uncertain outcome, distinct from caller cancellation");
    }

    private static Response NativeClientResponse(Request request) => new()
    {
        RequestId = request.RequestId,
        CorrelationId = request.CorrelationId,
        Ok = true,
        Command = request.ActionCase switch
        {
            Request.ActionOneofCase.Discover => "control.catalog",
            Request.ActionOneofCase.Health => "health.state",
            Request.ActionOneofCase.Models => "inference.models.state",
            Request.ActionOneofCase.CreateDiagnosticBundle => "diagnostics.bundle.create",
            Request.ActionOneofCase.OperationState => "operation.state",
            Request.ActionOneofCase.OperationWait => "operation.wait",
            Request.ActionOneofCase.OperationCancel => "operation.cancel",
            Request.ActionOneofCase.OperationList => "operation.list",
            _ => throw new InvalidOperationException("Unexpected typed native request.")
        }
    };

    private static Operation NativeClientOperation(string state, ulong transition, bool terminal) => new()
    {
        OperationId = NativeClientOperationId,
        State = state,
        TransitionOrdinal = transition,
        Terminal = terminal
    };

    private static TException NativeClientExpect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"The native client should have rejected the fixture with {typeof(TException).Name}.");
    }
}
