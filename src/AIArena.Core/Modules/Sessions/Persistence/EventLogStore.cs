using System.Text;
using System.Text.Json;

namespace AIArena.Core.Persistence;

public sealed class EventLogStore
{
    private const long MaxBytes = 128 * 1024;
    private const int Rotations = 3;
    private const int MaxCoalescedRecords = 256;
    private static readonly TimeSpan EventWriteLeaseTimeout = TimeSpan.FromSeconds(45);
    private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);
    private static readonly KeyedAsyncLockRegistry EventWriteLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object EventQueueGate = new();
    private static readonly Dictionary<string, SessionWriteQueue> EventWriteQueues = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };
    private readonly EventLogWriteObserver? observer;
    private readonly bool requireExistingSession;

    public EventLogStore(string? dataRoot = null, bool requireExistingSession = false)
        : this(dataRoot, observer: null, requireExistingSession)
    {
    }

    internal EventLogStore(
        string? dataRoot,
        EventLogWriteObserver? observer,
        bool requireExistingSession = false)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
        this.observer = observer;
        this.requireExistingSession = requireExistingSession;
    }

    public string DataRoot { get; }

    public static EventLogStore ForSessionStore(SessionStore sessionStore)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        return new EventLogStore(sessionStore.DataRoot, requireExistingSession: true);
    }

    public async Task AppendAsync(string sessionId, string type, object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ResolveWriteTarget(sessionId);
        var pending = new PendingWrite(
            SerializeLine(type, payload, observer?.GetNow() ?? DateTimeOffset.Now),
            cancellationToken,
            observer,
            recordIndex: 0);
        Enqueue(target, [pending]);
        await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Appends an ordered group through bounded coalesced mutations. Rotation
    /// splits writes only at JSONL record boundaries, so every published prefix
    /// is valid and the returned task observes the complete group on disk.
    /// </summary>
    public async Task AppendBatchAsync(
        string sessionId,
        IReadOnlyList<EventLogRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        if (records.Count == 0)
        {
            return;
        }

        var target = ResolveWriteTarget(sessionId);
        var serializedLines = new byte[records.Count][];
        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[index];
            serializedLines[index] = SerializeLine(
                record.Type,
                record.Payload,
                observer?.GetNow() ?? DateTimeOffset.Now);
        }

        var lines = new PendingWrite[records.Count];
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = new PendingWrite(
                serializedLines[index],
                cancellationToken,
                observer,
                recordIndex: index);
        }

        Enqueue(target, lines);
        await Task.WhenAll(lines.Select(line => line.Completion.Task)).ConfigureAwait(false);
    }

    public string EventPath(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return NativeDataPaths.EventPath(DataRoot, safeSession);
    }

    private EventWriteTarget ResolveWriteTarget(string sessionId)
    {
        var fullEventPath = Path.GetFullPath(EventPath(sessionId));
        if (!requireExistingSession)
        {
            return EventWriteTarget.Standalone(fullEventPath);
        }

        var requestedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
        var safeSessionId = SessionStore.SafeSessionId(requestedSessionId);
        if (!safeSessionId.Equals(requestedSessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Guarded event logging requires a canonical session identity.",
                nameof(sessionId));
        }

        var snapshotPath = Path.GetFullPath(
            NativeDataPaths.SessionSnapshotPath(DataRoot, safeSessionId));
        // Fail before creating the per-session event directory (or even the
        // shared lease hierarchy) when the authoritative session is already
        // absent. The same condition is checked again while the shared
        // session-tree lease is held by the queue drain.
        if (!File.Exists(snapshotPath))
        {
            throw new DirectoryNotFoundException(
                "The event-log target session no longer has a live snapshot.");
        }

        var sessionTreeLeaseTarget = SessionStore.SessionTreeLeaseTargetForSnapshot(
            DataRoot,
            snapshotPath);
        return EventWriteTarget.Guarded(
            fullEventPath,
            snapshotPath,
            sessionTreeLeaseTarget);
    }

    private static byte[] SerializeLine(string type, object payload, DateTimeOffset now)
    {
        var entry = new
        {
            type,
            created_at = now.ToUnixTimeSeconds(),
            created_at_iso = now.ToString("O"),
            payload
        };
        var serializedEntry = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
        var line = new byte[serializedEntry.Length + NewLineBytes.Length];
        serializedEntry.CopyTo(line, 0);
        NewLineBytes.CopyTo(line, serializedEntry.Length);
        return line;
    }

    private static void Enqueue(EventWriteTarget target, IReadOnlyList<PendingWrite> lines)
    {
        SessionWriteQueue queue;
        var startDrain = false;
        lock (EventQueueGate)
        {
            if (!EventWriteQueues.TryGetValue(target.QueueKey, out queue!))
            {
                queue = new SessionWriteQueue();
                EventWriteQueues.Add(target.QueueKey, queue);
            }

            foreach (var line in lines)
            {
                queue.Writes.Enqueue(line);
            }

            if (!queue.Draining)
            {
                queue.Draining = true;
                startDrain = true;
            }
        }

        if (startDrain)
        {
            _ = DrainQueueAsync(target, queue);
        }
    }

    private static async Task DrainQueueAsync(EventWriteTarget target, SessionWriteQueue queue)
    {
        // Let same-turn callers enqueue before the first lease is acquired. This
        // keeps transparent AppendAsync bursts coalesced even when local file I/O
        // completes synchronously.
        await Task.Yield();
        try
        {
            while (true)
            {
                PendingWrite[] batch;
                lock (EventQueueGate)
                {
                    if (queue.Writes.Count == 0)
                    {
                        queue.Draining = false;
                        EventWriteQueues.Remove(target.QueueKey);
                        return;
                    }

                    var count = Math.Min(queue.Writes.Count, MaxCoalescedRecords);
                    batch = new PendingWrite[count];
                    for (var index = 0; index < count; index++)
                    {
                        batch[index] = queue.Writes.Dequeue();
                    }
                }

                await PersistBatchAsync(target, batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            PendingWrite[] abandoned;
            lock (EventQueueGate)
            {
                abandoned = queue.Writes.ToArray();
                queue.Writes.Clear();
                queue.Draining = false;
                EventWriteQueues.Remove(target.QueueKey);
            }

            foreach (var write in abandoned)
            {
                write.Fail(ex);
            }
        }
    }

    private static async Task PersistBatchAsync(EventWriteTarget target, IReadOnlyList<PendingWrite> batch)
    {
        try
        {
            while (true)
            {
                var leaseOwner = batch.FirstOrDefault(write => write.IsWaiting);
                if (leaseOwner is null)
                {
                    return;
                }

                try
                {
                    PendingWrite[] started;
                    Exception? mutationError = null;
                    if (target.IsGuarded)
                    {
                        foreach (var batchObserver in batch
                            .Where(write => write.IsWaiting)
                            .Select(write => write.Observer)
                            .Where(item => item is not null)
                            .Distinct())
                        {
                            batchObserver!.BeforeSessionGuardCheck();
                        }
                    }

                    using (await EventWriteLocks.AcquireAsync(
                               target.EventPath,
                               leaseOwner.CancellationToken).ConfigureAwait(false))
                    using (var sessionTreeLease = target.IsGuarded
                               ? await CrossProcessWriteLease.AcquireAsync(
                                   target.SessionTreeLeaseTarget!,
                                   EventWriteLeaseTimeout,
                                   leaseOwner.CancellationToken).ConfigureAwait(false)
                               : null)
                    {
                        if (target.IsGuarded && !File.Exists(target.LiveSnapshotPath!))
                        {
                            throw new DirectoryNotFoundException(
                                "The event-log target session no longer has a live snapshot.");
                        }

                        // Acquiring the event-file lease creates its containing
                        // directory. In guarded mode it must therefore happen
                        // only after the authoritative snapshot check succeeds
                        // under the shared session-tree exclusion.
                        using var eventLease = await CrossProcessWriteLease.AcquireAsync(
                            target.EventPath,
                            EventWriteLeaseTimeout,
                            leaseOwner.CancellationToken).ConfigureAwait(false);
                        started = batch.Where(write => write.TryStart()).ToArray();
                        if (started.Length == 0)
                        {
                            return;
                        }

                        try
                        {
                            // Directory creation, append repair, rotation, and the
                            // durable write all share the same session-tree
                            // exclusion as SessionStore Trash/restore.
                            Directory.CreateDirectory(Path.GetDirectoryName(target.EventPath)!);
                            foreach (var batchObserver in started
                                .Select(write => write.Observer)
                                .Where(item => item is not null)
                                .Distinct())
                            {
                                batchObserver!.RecordLeaseAcquisition(started.Length);
                            }

                            await AppendLinesAsync(target.EventPath, started).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            mutationError = ex;
                        }
                    }

                    var durablePrefixCount = mutationError is EventBatchMutationException batchFailure
                        ? batchFailure.DurablePrefixCount
                        : 0;
                    for (var index = 0; index < started.Length; index++)
                    {
                        var write = started[index];
                        if (mutationError is null || index < durablePrefixCount)
                        {
                            write.Succeed();
                        }
                        else
                        {
                            write.Fail(mutationError);
                        }
                    }

                    return;
                }
                catch (OperationCanceledException) when (leaseOwner.CancellationToken.IsCancellationRequested)
                {
                    // That caller canceled before mutation. Retry any siblings under
                    // their own live token without publishing the canceled record.
                }
                catch (Exception ex)
                {
                    foreach (var write in batch)
                    {
                        write.Fail(ex);
                    }

                    return;
                }
            }
        }
        finally
        {
            foreach (var write in batch)
            {
                write.ReleaseCancellationRegistration();
            }
        }
    }

    private static async Task AppendLinesAsync(string path, IReadOnlyList<PendingWrite> lines)
    {
        FileStream? stream = null;
        var writtenCount = 0;
        var durablePrefixCount = 0;
        var recordWriteInProgress = false;
        long ambiguousRecordStart = -1;
        Exception? failure = null;
        try
        {
            for (var index = 0; index < lines.Count; index++)
            {
                var pending = lines[index];
                if (stream is null || ShouldRotateBeforeAppend(stream.Length))
                {
                    if (stream is not null)
                    {
                        stream.Flush(flushToDisk: true);
                        durablePrefixCount = writtenCount;
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = null;
                        RotateIfNeeded(path);
                    }

                    var rotateAfterRepair = File.Exists(path)
                        && new FileInfo(path).Length >= MaxBytes;
                    ClearReadOnly(path);
                    stream = OpenAppendStream(path);
                    if (rotateAfterRepair || ShouldRotateBeforeAppend(stream.Length))
                    {
                        stream.Flush(flushToDisk: true);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        stream = null;
                        RotateIfNeeded(path, force: rotateAfterRepair);
                        ClearReadOnly(path);
                        stream = OpenAppendStream(path);
                    }

                    foreach (var streamObserver in lines
                        .Select(line => line.Observer)
                        .Where(item => item is not null)
                        .Distinct())
                    {
                        streamObserver!.RecordStreamOpen();
                    }
                }

                pending.Observer?.BeforeRecordWrite(index);
                ambiguousRecordStart = stream.Position;
                recordWriteInProgress = true;
                await stream.WriteAsync(pending.Line, CancellationToken.None).ConfigureAwait(false);
                recordWriteInProgress = false;
                writtenCount++;
                pending.Observer?.RecordBytesWritten(pending.Line.Length);
            }

            if (stream is not null)
            {
                stream.Flush(flushToDisk: true);
                durablePrefixCount = writtenCount;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            if (stream is not null && recordWriteInProgress && ambiguousRecordStart >= 0)
            {
                try
                {
                    stream.SetLength(ambiguousRecordStart);
                    stream.Position = ambiguousRecordStart;
                    recordWriteInProgress = false;
                }
                catch (Exception truncateFailure)
                {
                    failure = new AggregateException(exception, truncateFailure);
                }
            }

            if (stream is not null && durablePrefixCount < writtenCount)
            {
                try
                {
                    stream.Flush(flushToDisk: true);
                    durablePrefixCount = writtenCount;
                }
                catch (Exception flushFailure)
                {
                    failure = new AggregateException(exception, flushFailure);
                }
            }
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeFailure)
                {
                    if (failure is null && durablePrefixCount < writtenCount)
                    {
                        failure = disposeFailure;
                    }
                    else if (failure is not null && durablePrefixCount < writtenCount)
                    {
                        failure = new AggregateException(failure, disposeFailure);
                    }
                }
            }
        }

        if (failure is not null)
        {
            throw new EventBatchMutationException(durablePrefixCount, failure);
        }
    }

    private static FileStream OpenAppendStream(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        RepairTrailingPartialRecord(stream);
        stream.Position = stream.Length;
        return stream;
    }

    private static void RepairTrailingPartialRecord(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return;
        }

        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == (byte)'\n')
        {
            return;
        }

        for (var position = stream.Length - 2; position >= 0; position--)
        {
            stream.Position = position;
            if (stream.ReadByte() == (byte)'\n')
            {
                stream.SetLength(position + 1);
                return;
            }
        }

        stream.SetLength(0);
    }

    private static bool ShouldRotateBeforeAppend(long currentLength)
    {
        return currentLength >= MaxBytes;
    }

    private static void RotateIfNeeded(string path, bool force = false)
    {
        if (!File.Exists(path) || (!force && new FileInfo(path).Length < MaxBytes))
        {
            return;
        }

        for (var index = Rotations; index >= 1; index--)
        {
            var current = $"{path[..^".jsonl".Length]}.{index}.jsonl";
            var next = $"{path[..^".jsonl".Length]}.{index + 1}.jsonl";
            if (index == Rotations && File.Exists(current))
            {
                ClearReadOnly(current);
                if (!TryFileOperation(() => File.Delete(current)))
                {
                    return;
                }

                continue;
            }

            if (File.Exists(current))
            {
                ClearReadOnly(current);
                ClearReadOnly(next);
                if (!TryFileOperation(() => File.Move(current, next, overwrite: true)))
                {
                    return;
                }
            }
        }

        ClearReadOnly(path);
        ClearReadOnly($"{path[..^".jsonl".Length]}.1.jsonl");
        TryFileOperation(() => File.Move(path, $"{path[..^".jsonl".Length]}.1.jsonl", overwrite: true));
    }

    private static bool TryFileOperation(Action operation)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    return false;
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }

        return false;
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private sealed record EventWriteTarget(
        string EventPath,
        string? LiveSnapshotPath,
        string? SessionTreeLeaseTarget,
        string QueueKey)
    {
        public bool IsGuarded => LiveSnapshotPath is not null;

        public static EventWriteTarget Standalone(string eventPath)
        {
            return new EventWriteTarget(
                eventPath,
                LiveSnapshotPath: null,
                SessionTreeLeaseTarget: null,
                QueueKey: $"standalone\0{eventPath}");
        }

        public static EventWriteTarget Guarded(
            string eventPath,
            string liveSnapshotPath,
            string sessionTreeLeaseTarget)
        {
            return new EventWriteTarget(
                eventPath,
                liveSnapshotPath,
                sessionTreeLeaseTarget,
                QueueKey: $"guarded\0{eventPath}\0{liveSnapshotPath}\0{sessionTreeLeaseTarget}");
        }
    }

    private sealed class SessionWriteQueue
    {
        public Queue<PendingWrite> Writes { get; } = new();

        public bool Draining { get; set; }
    }

    private sealed class EventBatchMutationException : IOException
    {
        public EventBatchMutationException(int durablePrefixCount, Exception innerException)
            : base("An event-log batch stopped after committing a durable record prefix.", innerException)
        {
            DurablePrefixCount = durablePrefixCount;
        }

        public int DurablePrefixCount { get; }
    }

    private sealed class PendingWrite
    {
        private readonly CancellationTokenRegistration cancellationRegistration;
        private readonly int recordIndex;
        private int state;

        public PendingWrite(
            byte[] line,
            CancellationToken cancellationToken,
            EventLogWriteObserver? observer,
            int recordIndex)
        {
            Line = line;
            CancellationToken = cancellationToken;
            Observer = observer;
            this.recordIndex = recordIndex;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state => ((PendingWrite)state!).Cancel(),
                    this);
            }
        }

        public byte[] Line { get; }

        public CancellationToken CancellationToken { get; }

        public EventLogWriteObserver? Observer { get; }

        public TaskCompletionSource<bool> Completion { get; }

        public bool IsWaiting => Volatile.Read(ref state) == 0;

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref state, 1, 0) == 0;
        }

        public void Succeed()
        {
            if (Interlocked.Exchange(ref state, 2) == 1)
            {
                cancellationRegistration.Dispose();
                Observer?.RecordCompletion(recordIndex, succeeded: true);
                Completion.TrySetResult(true);
            }
        }

        public void Fail(Exception exception)
        {
            var previous = Interlocked.Exchange(ref state, 2);
            if (previous is 0 or 1)
            {
                cancellationRegistration.Dispose();
                Observer?.RecordCompletion(recordIndex, succeeded: false);
                Completion.TrySetException(exception);
            }
        }

        public void ReleaseCancellationRegistration()
        {
            if (Volatile.Read(ref state) == 2)
            {
                cancellationRegistration.Dispose();
            }
        }

        private void Cancel()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            {
                Observer?.RecordCompletion(recordIndex, succeeded: false);
                Completion.TrySetCanceled(CancellationToken);
            }
        }
    }
}

public sealed record EventLogRecord(string Type, object Payload);

internal sealed class EventLogWriteObserver
{
    private long leaseAcquisitions;
    private long streamOpens;
    private long bytesWritten;
    private long recordsInLease;

    public Action<int>? BeforeWrite { get; init; }

    public Action? BeforeSessionGuard { get; init; }

    public Action<int, bool>? Completion { get; init; }

    public Func<DateTimeOffset>? Clock { get; init; }

    public EventLogWriteReceipt Receipt => new(
        Interlocked.Read(ref leaseAcquisitions),
        Interlocked.Read(ref streamOpens),
        Interlocked.Read(ref bytesWritten),
        Interlocked.Read(ref recordsInLease));

    internal void RecordLeaseAcquisition(int records)
    {
        Interlocked.Increment(ref leaseAcquisitions);
        Interlocked.Add(ref recordsInLease, records);
    }

    internal void RecordStreamOpen()
    {
        Interlocked.Increment(ref streamOpens);
    }

    internal void RecordBytesWritten(int bytes)
    {
        Interlocked.Add(ref bytesWritten, bytes);
    }

    internal void BeforeRecordWrite(int index)
    {
        BeforeWrite?.Invoke(index);
    }

    internal void BeforeSessionGuardCheck()
    {
        BeforeSessionGuard?.Invoke();
    }

    internal void RecordCompletion(int index, bool succeeded)
    {
        try
        {
            Completion?.Invoke(index, succeeded);
        }
        catch
        {
            // Receipt observers must never alter event durability or caller completion.
        }
    }

    internal DateTimeOffset GetNow()
    {
        return Clock?.Invoke() ?? DateTimeOffset.Now;
    }
}

internal sealed record EventLogWriteReceipt(
    long LeaseAcquisitions,
    long StreamOpens,
    long BytesWritten,
    long RecordsInLease);
