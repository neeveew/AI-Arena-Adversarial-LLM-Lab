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

    public EventLogStore(string? dataRoot = null)
        : this(dataRoot, observer: null)
    {
    }

    internal EventLogStore(string? dataRoot, EventLogWriteObserver? observer)
    {
        DataRoot = string.IsNullOrWhiteSpace(dataRoot) ? NativeDataPaths.DefaultDataRoot() : dataRoot;
        this.observer = observer;
    }

    public string DataRoot { get; }

    public async Task AppendAsync(string sessionId, string type, object payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(EventPath(sessionId));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var pending = new PendingWrite(
            SerializeLine(type, payload, observer?.GetNow() ?? DateTimeOffset.Now),
            cancellationToken,
            observer);
        Enqueue(fullPath, [pending]);
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

        var fullPath = Path.GetFullPath(EventPath(sessionId));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
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
                observer);
        }

        Enqueue(fullPath, lines);
        await Task.WhenAll(lines.Select(line => line.Completion.Task)).ConfigureAwait(false);
    }

    public string EventPath(string sessionId = "default")
    {
        var safeSession = string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId;
        return NativeDataPaths.EventPath(DataRoot, safeSession);
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

    private static void Enqueue(string path, IReadOnlyList<PendingWrite> lines)
    {
        SessionWriteQueue queue;
        var startDrain = false;
        lock (EventQueueGate)
        {
            if (!EventWriteQueues.TryGetValue(path, out queue!))
            {
                queue = new SessionWriteQueue();
                EventWriteQueues.Add(path, queue);
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
            _ = DrainQueueAsync(path, queue);
        }
    }

    private static async Task DrainQueueAsync(string path, SessionWriteQueue queue)
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
                        EventWriteQueues.Remove(path);
                        return;
                    }

                    var count = Math.Min(queue.Writes.Count, MaxCoalescedRecords);
                    batch = new PendingWrite[count];
                    for (var index = 0; index < count; index++)
                    {
                        batch[index] = queue.Writes.Dequeue();
                    }
                }

                await PersistBatchAsync(path, batch).ConfigureAwait(false);
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
                EventWriteQueues.Remove(path);
            }

            foreach (var write in abandoned)
            {
                write.Fail(ex);
            }
        }
    }

    private static async Task PersistBatchAsync(string path, IReadOnlyList<PendingWrite> batch)
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
                    using (await EventWriteLocks.AcquireAsync(path, leaseOwner.CancellationToken).ConfigureAwait(false))
                    using (await CrossProcessWriteLease.AcquireAsync(
                        path,
                        EventWriteLeaseTimeout,
                        leaseOwner.CancellationToken).ConfigureAwait(false))
                    {
                        started = batch.Where(write => write.TryStart()).ToArray();
                        if (started.Length == 0)
                        {
                            return;
                        }

                        foreach (var batchObserver in started
                            .Select(write => write.Observer)
                            .Where(item => item is not null)
                            .Distinct())
                        {
                            batchObserver!.RecordLeaseAcquisition(started.Length);
                        }

                        try
                        {
                            await AppendLinesAsync(path, started).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            mutationError = ex;
                        }
                    }

                    foreach (var write in started)
                    {
                        if (mutationError is null)
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
                await stream.WriteAsync(pending.Line, CancellationToken.None).ConfigureAwait(false);
                pending.Observer?.RecordBytesWritten(pending.Line.Length);
            }

            if (stream is not null)
            {
                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
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

    private sealed class SessionWriteQueue
    {
        public Queue<PendingWrite> Writes { get; } = new();

        public bool Draining { get; set; }
    }

    private sealed class PendingWrite
    {
        private readonly CancellationTokenRegistration cancellationRegistration;
        private int state;

        public PendingWrite(byte[] line, CancellationToken cancellationToken, EventLogWriteObserver? observer)
        {
            Line = line;
            CancellationToken = cancellationToken;
            Observer = observer;
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
                Completion.TrySetResult(true);
            }
        }

        public void Fail(Exception exception)
        {
            var previous = Interlocked.Exchange(ref state, 2);
            if (previous is 0 or 1)
            {
                cancellationRegistration.Dispose();
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
