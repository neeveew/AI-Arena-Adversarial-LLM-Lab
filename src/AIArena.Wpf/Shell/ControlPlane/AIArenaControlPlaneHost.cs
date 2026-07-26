using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace AIArena.Wpf;

internal sealed class AIArenaControlPlaneHost : IDisposable
{
    private readonly IAIArenaControlTarget target;
    private readonly IAIArenaControlEventSource eventSource;
    private readonly string pipeName;
    private readonly string tokenPath;
    private readonly object gate = new();
    private RunState? currentRun;
    private Task? pendingStop;
    private bool disposed;

    public AIArenaControlPlaneHost(
        IAIArenaControlTarget target,
        IAIArenaControlEventSource eventSource,
        string pipeName = AIArenaControlPlaneProtocol.PipeName,
        string? tokenPath = null)
    {
        this.target = target;
        this.eventSource = eventSource;
        this.pipeName = string.IsNullOrWhiteSpace(pipeName) ? AIArenaControlPlaneProtocol.PipeName : pipeName;
        this.tokenPath = string.IsNullOrWhiteSpace(tokenPath) ? AIArenaControlPlaneProtocol.DefaultTokenPath() : tokenPath;
    }

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return currentRun?.AcceptLoop is { IsCompleted: false };
            }
        }
    }

    internal string SessionToken
    {
        get
        {
            lock (gate)
            {
                return currentRun?.SessionToken ?? "";
            }
        }
    }

    internal string TokenPath => tokenPath;

    internal async Task StartIfEnabledAsync()
    {
        while (true)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
            }

            if (!target.IsControlPlaneEnabled)
            {
                await StopAsync().ConfigureAwait(false);
                return;
            }

            Task? stopToAwait = null;
            var completedRunNeedsStop = false;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                if (currentRun?.AcceptLoop is { IsCompleted: false })
                {
                    return;
                }

                if (currentRun is not null)
                {
                    completedRunNeedsStop = true;
                }
                else if (pendingStop is not null)
                {
                    stopToAwait = pendingStop;
                }
                else
                {
                    var run = new RunState(GenerateToken());
                    currentRun = run;
                    WriteTokenFile(run);
                    run.AcceptLoop = Task.Run(() => AcceptLoopAsync(run));
                    return;
                }
            }

            if (completedRunNeedsStop)
            {
                await StopAsync().ConfigureAwait(false);
            }
            else
            {
                await stopToAwait!.ConfigureAwait(false);
            }
        }
    }

    internal Task StopAsync()
    {
        RunState? run;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (currentRun is null)
            {
                return pendingStop ?? Task.CompletedTask;
            }

            run = currentRun;
            currentRun = null;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingStop = completion.Task;
        }

        _ = CompleteStopAsync(run, completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(RunState run, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await StopRunAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        lock (gate)
        {
            if (ReferenceEquals(pendingStop, completion.Task))
            {
                pendingStop = null;
            }
        }

        if (failure is not null)
        {
            PublishBackgroundFailure("Control-plane shutdown stopped with an error.", failure);
        }

        completion.SetResult();
    }

    private async Task StopRunAsync(RunState run)
    {
        CancelBestEffort(run.Cancellation);
        await ObserveBackgroundTaskAsync(run.AcceptLoop, "Control-plane accept loop stopped with an error.").ConfigureAwait(false);

        Task[] clients;
        lock (run.Clients)
        {
            clients = run.Clients.ToArray();
        }

        if (clients.Length > 0)
        {
            try
            {
                await Task.WhenAll(clients).ConfigureAwait(false);
            }
            catch
            {
                // Each client task is observed and reported by ObserveClientAsync.
            }
        }

        try
        {
            DeleteTokenFile(run);
        }
        finally
        {
            run.Cancellation.Dispose();
            run.ClientSlots.Dispose();
        }
    }

    private async Task AcceptLoopAsync(RunState run)
    {
        var cancellationToken = run.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!await run.ClientSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    var busyPipe = pipe;
                    pipe = null;
                    await RejectBusyClientAsync(busyPipe, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var clientPipe = pipe;
                pipe = null;
                var clientTask = HandleClientWithSlotAsync(run, clientPipe, cancellationToken);
                TrackClient(run, clientTask);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientWithSlotAsync(RunState run, Stream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(pipe, run.SessionToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            run.ClientSlots.Release();
        }
    }

    private async Task HandleClientAsync(Stream pipe, string expectedToken, CancellationToken cancellationToken)
    {
        using var ownedPipe = pipe;

        string line;
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            requestTimeout.CancelAfter(AIArenaControlPlaneProtocol.RequestTimeout);
            try
            {
                line = await ReadBoundedLineAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                var errorRequest = new AIArenaControlRequest("", "invalid", null);
                await WriteLineAsync(
                    pipe,
                    AIArenaControlPlaneProtocol.Serialize(
                        AIArenaControlResponse.Error(errorRequest, "invalid_request", ex.Message)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var errorRequest = new AIArenaControlRequest("", "invalid", null);
                await WriteLineAsync(
                    pipe,
                    AIArenaControlPlaneProtocol.Serialize(
                        AIArenaControlResponse.Error(errorRequest, "request_timeout", "Control-plane request timed out.")),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        if (!AIArenaControlPlaneProtocol.TryParseRequest(line ?? "", out var request, out var error))
        {
            var errorRequest = new AIArenaControlRequest("", "invalid", null);
            await WriteLineAsync(
                pipe,
                AIArenaControlPlaneProtocol.Serialize(
                    AIArenaControlResponse.Error(errorRequest, "invalid_request", error)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TokenMatches(request.Token, expectedToken))
        {
            await WriteLineAsync(
                pipe,
                AIArenaControlPlaneProtocol.Serialize(
                    AIArenaControlResponse.Error(request, "unauthorized", "Control-plane token is missing or invalid.")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Command.Equals(AIArenaControlCommands.EventsWatch, StringComparison.OrdinalIgnoreCase))
        {
            await StreamEventsAsync(pipe, cancellationToken).ConfigureAwait(false);
            return;
        }

        eventSource.Publish(new AIArenaControlEvent(
            "command.running",
            DateTimeOffset.Now,
            $"Control command running: {request.Command}.",
            new { request.Id, request.Command }));
        var response = await target.ExecuteControlCommandAsync(request, cancellationToken).ConfigureAwait(false);
        eventSource.Publish(new AIArenaControlEvent(
            "command.completed",
            DateTimeOffset.Now,
            $"Control command completed: {request.Command}.",
            new
            {
                request.Id,
                request.Command,
                response.Ok,
                response.Status,
                response.ErrorCode
            }));
        await WriteLineAsync(pipe, AIArenaControlPlaneProtocol.Serialize(response), cancellationToken).ConfigureAwait(false);
    }

    private static async Task RejectBusyClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        await using var ownedPipe = pipe;
        var request = new AIArenaControlRequest("", "busy", null);
        await WriteLineAsync(
            pipe,
            AIArenaControlPlaneProtocol.Serialize(
                AIArenaControlResponse.Error(request, "busy", "AI Arena control plane has too many active clients.")),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var tooLarge = false;
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (oneByte[0] == (byte)'\n')
            {
                break;
            }

            if (oneByte[0] == (byte)'\r')
            {
                continue;
            }

            if (buffer.Length >= AIArenaControlPlaneProtocol.MaxRequestBytes)
            {
                tooLarge = true;
                continue;
            }

            buffer.WriteByte(oneByte[0]);
        }

        if (tooLarge)
        {
            throw new InvalidDataException("Request body is too large.");
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task StreamEventsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var queue = new EventQueue();
        using var subscription = eventSource.Subscribe(queue.Enqueue);
        await WriteLineAsync(
            stream,
            new AIArenaControlEvent("events.connected", DateTimeOffset.Now, "AI Arena event stream connected.").ToJsonLine(),
            cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await queue.NextAsync(cancellationToken).ConfigureAwait(false);
            await WriteLineAsync(stream, item.ToJsonLine(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteLineAsync(
        Stream stream,
        string value,
        CancellationToken cancellationToken)
    {
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeCancellation.CancelAfter(AIArenaControlPlaneProtocol.RequestTimeout);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var payload = GC.AllocateUninitializedArray<byte>(byteCount + 1);
        Encoding.UTF8.GetBytes(value.AsSpan(), payload);
        payload[^1] = (byte)'\n';
        await stream.WriteAsync(payload, writeCancellation.Token).ConfigureAwait(false);
        await stream.FlushAsync(writeCancellation.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        _ = StopAsync();
    }

    private static bool TokenMatches(string token, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(token.Trim());
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private void WriteTokenFile(RunState run)
    {
        try
        {
            var directory = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(tokenPath, run.SessionToken, new UTF8Encoding(false));
            run.TokenFileWritten = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            eventSource.Publish(new AIArenaControlEvent(
                "control.token.write_failed",
                DateTimeOffset.Now,
                "Control-plane token file could not be written.",
                new { tokenPath, error = ex.Message }));
        }
    }

    private void DeleteTokenFile(RunState run)
    {
        try
        {
            if (run.TokenFileWritten
                && File.Exists(tokenPath)
                && File.ReadAllText(tokenPath).Trim().Equals(run.SessionToken, StringComparison.Ordinal))
            {
                File.Delete(tokenPath);
            }

            run.TokenFileWritten = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            eventSource.Publish(new AIArenaControlEvent(
                "control.token.delete_failed",
                DateTimeOffset.Now,
                "Control-plane token file could not be deleted.",
                new { tokenPath, error = ex.Message }));
        }
    }

    private void TrackClient(RunState run, Task clientTask)
    {
        lock (run.Clients)
        {
            run.Clients.Add(clientTask);
        }

        _ = ObserveClientAsync(run, clientTask);
    }

    private async Task ObserveClientAsync(RunState run, Task clientTask)
    {
        try
        {
            await clientTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            // Expected while stopping the host.
        }
        catch (IOException) when (run.Cancellation.IsCancellationRequested)
        {
            // A connected client may close while the host is stopping.
        }
        catch (Exception ex)
        {
            PublishBackgroundFailure("Control-plane client stopped with an error.", ex);
        }
        finally
        {
            lock (run.Clients)
            {
                run.Clients.Remove(clientTask);
            }
        }
    }

    private async Task ObserveBackgroundTaskAsync(Task task, string message)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while stopping the host.
        }
        catch (IOException) when (disposed || !IsRunning)
        {
            // Pipe shutdown may surface as an I/O error after cancellation.
        }
        catch (Exception ex)
        {
            PublishBackgroundFailure(message, ex);
        }
    }

    private void PublishBackgroundFailure(string message, Exception exception)
    {
        try
        {
            eventSource.Publish(new AIArenaControlEvent(
                "control.background.failed",
                DateTimeOffset.Now,
                message,
                new { error = exception.Message }));
        }
        catch
        {
            // Background failure reporting must not fault lifecycle cleanup.
        }
    }

    private static void CancelBestEffort(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run may already have completed cleanup after a concurrent stop.
        }
        catch (AggregateException)
        {
            // A failing cancellation callback must not block host shutdown.
        }
    }

    private sealed class RunState(string sessionToken)
    {
        public string SessionToken { get; } = sessionToken;

        public CancellationTokenSource Cancellation { get; } = new();

        public SemaphoreSlim ClientSlots { get; } = new(AIArenaControlPlaneProtocol.MaxConcurrentClients);

        public HashSet<Task> Clients { get; } = [];

        public Task AcceptLoop { get; set; } = Task.CompletedTask;

        public bool TokenFileWritten { get; set; }
    }

    internal sealed class EventQueue : IDisposable
    {
        private readonly Queue<AIArenaControlEvent> events = new();
        private readonly SemaphoreSlim signal = new(0);
        private bool disposed;

        public void Enqueue(AIArenaControlEvent controlEvent)
        {
            lock (events)
            {
                if (disposed)
                {
                    return;
                }

                var queueGrew = events.Count < AIArenaControlPlaneProtocol.MaxEventQueueItems;
                if (!queueGrew)
                {
                    events.Dequeue();
                }

                events.Enqueue(controlEvent);
                if (queueGrew)
                {
                    signal.Release();
                }
            }
        }

        public async Task<AIArenaControlEvent> NextAsync(CancellationToken cancellationToken)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (events)
            {
                return events.Dequeue();
            }
        }

        public void Dispose()
        {
            lock (events)
            {
                disposed = true;
            }

            signal.Dispose();
        }
    }
}
