using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using AIArena.Native.Protocol;

namespace AIArena.Wpf.Services;

/// <summary>Typed outbound control only. Native services own operation execution and persistence.</summary>
internal sealed partial class NativeControlClient : INativeServicesClient, INativeModelClient, INativeInferenceClient
{
    private static readonly string[] TerminalStates = ["succeeded", "partial", "blocked", "failed", "canceled", "interrupted"];
    private static readonly string[] KnownStates = ["accepted", "queued", "running", "waiting", "cancel_requested", "reconciling", .. TerminalStates];
    private static readonly string[] WorkflowCommands = ["diagnostics.bundle.create", "operation.state", "operation.wait", "operation.cancel"];
    private readonly Func<Request, TimeSpan, CancellationToken, Task<Response>> send;

    public NativeControlClient() : this(SendOverPipeAsync) { }

    internal NativeControlClient(Func<Request, TimeSpan, CancellationToken, Task<Response>> send)
    {
        this.send = send;
    }

    public async Task<NativeConnectionSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { Discover = new DiscoverRequest() }, "control.catalog", cancellationToken);
        if (response.ResultCase != Response.ResultOneofCase.Capabilities || response.Capabilities.ProtocolVersion != 1)
            throw new NativeControlException("The native app does not support this control protocol.", false);
        var capabilities = response.Capabilities;
        if (capabilities.MaximumRequestBytes == 0 || capabilities.MaximumResponseBytes == 0)
            throw new NativeControlException("The native app did not advertise bounded message limits.", false);
        var commands = capabilities.Commands.Select(item => item.Command).Distinct(StringComparer.Ordinal).ToArray();
        bool Supports(string command) => commands.Contains(command, StringComparer.Ordinal);
        var health = Supports("health.state")
            ? await CallAsync(new Request { Health = new HealthRequest() }, "health.state", cancellationToken) : null;
        var models = Supports("inference.models.state")
            ? await CallAsync(new Request { Models = new ModelsRequest() }, "inference.models.state", cancellationToken) : null;
        var operations = Supports("operation.list")
            ? await CallAsync(new Request { OperationList = new OperationListRequest { Limit = 100 } }, "operation.list", cancellationToken) : null;

        if (health is not null && health.ResultCase != Response.ResultOneofCase.Health
            || models is not null && models.ResultCase != Response.ResultOneofCase.Models
            || operations is not null && operations.ResultCase != Response.ResultOneofCase.Operations)
            throw new NativeControlException("The native app returned a different result type than requested.");

        return new NativeConnectionSnapshot(
            health is null ? "Health is unavailable."
                : $"Health: {HealthLabel(health.Health.State)}; {health.Health.ComponentCount} components; {health.Health.BlockedCapabilities.Count} blocked capabilities.",
            models is null ? "Model inventory is unavailable."
                : $"{models.Models.ModelInstanceIds.Count} model instances; {models.Models.GenerationIds.Count} generations; {models.Models.RunningCount} running; {models.Models.NeedsAttentionCount} need attention.",
            operations is null ? "Operation inventory is unavailable."
                : $"{operations.Operations.OperationIds.Count} recent operations{(operations.Operations.Truncated ? " (more available)" : "")}.",
            commands,
            WorkflowCommands.All(Supports));
    }

    public async Task<NativeOperationSnapshot> CreateDiagnosticBundleAsync(string destination, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)
            || !Path.GetExtension(destination).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new NativeControlException("Choose an absolute ZIP destination for the diagnostic bundle.", false);
        var action = new CreateDiagnosticBundleRequest
        {
            Destination = destination,
            MaximumBytes = 32 * 1024 * 1024,
            MaximumMembers = 1000
        };
        action.Categories.Add(["version", "health", "operations", "logs"]);
        var response = await CallAsync(new Request { CreateDiagnosticBundle = action, IdempotencyKey = ValidateKey(idempotencyKey) },
            "diagnostics.bundle.create", cancellationToken);
        return ProjectOperation(response);
    }

    public async Task<NativeOperationSnapshot> GetOperationAsync(string operationId, CancellationToken cancellationToken)
    {
        var response = await CallAsync(new Request { OperationState = new OperationStateRequest { OperationId = ValidateOperationId(operationId) } },
            "operation.state", cancellationToken);
        var snapshot = ProjectOperation(response, operationId);
        if (response.Operation.HasTransitionOrdinal) return snapshot;
        // State reads omit their cursor. Drain immediate native observations to
        // obtain current state and an authoritative cursor together.
        ulong cursor = 0;
        for (var observation = 0; observation < 64; observation++)
        {
            var wait = new OperationWaitRequest { OperationId = operationId, AfterTransition = cursor, TimeoutMilliseconds = 0 };
            wait.TerminalStates.Add(TerminalStates);
            var observed = await CallAsync(new Request { OperationWait = wait }, "operation.wait", cancellationToken);
            snapshot = ProjectOperation(observed, operationId);
            if (!observed.Operation.HasTransitionOrdinal || observed.Operation.TransitionOrdinal < cursor)
                throw new NativeControlException("The native operation observation omitted a current cursor.");
            if (snapshot.IsTerminal || observed.Operation.TimedOut) return snapshot;
            if (observed.Operation.TransitionOrdinal == cursor)
                throw new NativeControlException("The native operation observation did not advance.");
            cursor = observed.Operation.TransitionOrdinal;
        }
        throw new NativeControlException("Native state is changing too quickly to reconcile. Refresh the operation again.");
    }

    public async Task<NativeOperationSnapshot> WaitOperationAsync(string operationId, long afterTransition, CancellationToken cancellationToken)
    {
        if (afterTransition < 0) throw new NativeControlException("The operation observation cursor is invalid.", false);
        var wait = new OperationWaitRequest
        {
            OperationId = ValidateOperationId(operationId),
            AfterTransition = (ulong)afterTransition,
            TimeoutMilliseconds = 15000
        };
        wait.TerminalStates.Add(TerminalStates);
        var response = await CallAsync(new Request { OperationWait = wait }, "operation.wait", cancellationToken, TimeSpan.FromSeconds(20));
        return ProjectOperation(response, operationId);
    }

    public async Task<NativeOperationSnapshot> CancelOperationAsync(string operationId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await CallAsync(new Request
        {
            OperationCancel = new OperationCancelRequest { OperationId = ValidateOperationId(operationId) },
            IdempotencyKey = ValidateKey(idempotencyKey)
        }, "operation.cancel", cancellationToken);
        // A cancel acknowledgement is not terminal state. The authoritative query wins races.
        return await GetOperationAsync(operationId, cancellationToken);
    }

    private async Task<Response> CallAsync(Request request, string command, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        request.RequestId = Guid.NewGuid().ToString("N");
        request.CorrelationId = Guid.CreateVersion7().ToString("D");
        Response response;
        try
        {
            response = await send(request, timeout ?? TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NativeControlException) { throw; }
        catch (OperationCanceledException)
        {
            throw new NativeControlException("The native request timed out. Reconnect or reconcile the operation before retrying.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new NativeControlException("Could not communicate with the native app. Check that it is running in this Windows sign-in session, then reconnect.");
        }
        finally
        {
            request.Token = "";
        }
        if (response.RequestId != request.RequestId || response.CorrelationId != request.CorrelationId || response.Command != command)
            throw new NativeControlException("The native response did not match this request. Reconcile before retrying.");
        if (!response.Ok)
            throw new NativeControlException(response.ErrorCode == "unauthorized"
                ? "The native app could not authenticate this connection. Reconnect and try again."
                : "The native app rejected this request. Refresh its health and operation state.", false);
        return response;
    }

    private static async Task<Response> SendOverPipeAsync(Request request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var endpoint = NativeControlEndpointResolver.Resolve();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        request.Token = await ReadTokenAsync(endpoint.TokenPath, deadline.Token).ConfigureAwait(false);
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await NativeProtobufFraming.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        return await NativeProtobufFraming.ReadAsync(pipe, Response.Parser, deadline.Token).ConfigureAwait(false);
    }

    internal static async Task<string> ReadTokenAsync(string tokenPath, CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(tokenPath);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new NativeControlException("The native authentication file is not a regular file.", false);
        await using var stream = new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 4096)
            throw new NativeControlException("The native authentication file is invalid.", false);
        var bytes = new byte[4097];
        try
        {
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length == 0 || length > 4096)
                throw new NativeControlException("The native authentication file is invalid.", false);
            var value = new UTF8Encoding(false, true).GetString(bytes, 0, length).Trim();
            if (value.Length == 0 || value.Any(char.IsControl))
                throw new NativeControlException("The native authentication file is invalid.", false);
            return value;
        }
        catch (DecoderFallbackException)
        {
            throw new NativeControlException("The native authentication file is unreadable.", false);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static NativeOperationSnapshot ProjectOperation(Response response, string? expectedId = null)
    {
        if (response.ResultCase != Response.ResultOneofCase.Operation)
            throw new NativeControlException("The native app did not return an operation receipt.");
        var operation = response.Operation;
        if (!IsUuidV7(operation.OperationId) || expectedId is not null && operation.OperationId != expectedId)
            throw new NativeControlException("The native operation receipt did not match the requested operation.");
        if (!KnownStates.Contains(operation.State, StringComparer.Ordinal))
            throw new NativeControlException("The native app reported an unknown operation state. Refresh with a compatible client.");
        if (operation.HasTerminal && operation.Terminal != TerminalStates.Contains(operation.State, StringComparer.Ordinal))
            throw new NativeControlException("The native app returned inconsistent terminal-state evidence.");
        if (operation.TransitionOrdinal > long.MaxValue)
            throw new NativeControlException("The native app returned an unsupported operation cursor.");
        ValidateConfirmationEvidence(operation.ConfirmationRequired, operation.OperationId, operation.PlanDigest, operation.CapacitySampleId);
        if (operation.ConfirmationRequired && TerminalStates.Contains(operation.State, StringComparer.Ordinal))
            throw new NativeControlException("The native app returned a terminal operation requiring confirmation.");
        return new NativeOperationSnapshot(operation.OperationId, operation.State, (long)operation.TransitionOrdinal,
            $"{operation.State.Replace('_', ' ')}; transition {operation.TransitionOrdinal}"
            + (operation.HasWarningCount ? $"; {operation.WarningCount} warnings" : ""),
            ConfirmationRequired: operation.ConfirmationRequired, PlanDigest: operation.PlanDigest, CapacitySampleId: operation.CapacitySampleId);
    }

    private static string ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw new NativeControlException("The operation retry identity is invalid.", false);
        return key;
    }

    private static string ValidateOperationId(string id) => IsUuidV7(id) ? id
        : throw new NativeControlException("The operation identifier is invalid.", false);

    internal static bool IsUuidV7(string value) => value is { Length: 36 }
        && Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value
        && value[14] == '7' && "89ab".Contains(value[19]);

    private static string HealthLabel(string state) => state switch
    {
        "healthy" or "ready" or "degraded" or "blocked" or "unavailable" or "unknown" or "stale" => state,
        _ => "unknown"
    };
}
