using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIArena.Core.Persistence;

internal static class EventLogBatchingOptimizationTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 8, 14, 12, 34, 56, TimeSpan.FromHours(1));

    internal static void OrderedBatchMatchesLegacyBytesAndRecordsPerformance()
    {
        var exactRoot = TemporaryRoot();
        try
        {
            var records = Records(100);
            var clockIndex = 0;
            var times = Enumerable.Range(0, records.Length)
                .Select(index => FixedTime.AddMilliseconds(index))
                .ToArray();
            var observer = new EventLogWriteObserver
            {
                Clock = () => times[clockIndex++]
            };
            var store = new EventLogStore(exactRoot, observer);
            store.AppendBatchAsync("exact", records).GetAwaiter().GetResult();

            var actual = File.ReadAllBytes(store.EventPath("exact"));
            var expected = LegacyBytes(records, times);
            Require(actual.AsSpan().SequenceEqual(expected), "ordered batch changed legacy JSONL bytes or record order");
            Require(observer.Receipt == new EventLogWriteReceipt(1, 1, expected.Length, records.Length),
                $"exact batch did not use one lease and stream: {observer.Receipt}");
            AssertOnlyPublicEnvelopeFields(store.EventPath("exact"));
        }
        finally
        {
            DeleteRoot(exactRoot);
        }

        foreach (var count in new[] { 1, 10, 100 })
        {
            var samples = new List<(double Milliseconds, long Allocated, EventLogWriteReceipt Receipt, long FileBytes)>();
            for (var iteration = 0; iteration < 7; iteration++)
            {
                var root = TemporaryRoot();
                try
                {
                    var observer = new EventLogWriteObserver { Clock = () => FixedTime };
                    var store = new EventLogStore(root, observer);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var started = Stopwatch.GetTimestamp();
                    var measuredRecords = Records(count);
                    if (count == 1)
                    {
                        store.AppendAsync(
                            "receipt",
                            measuredRecords[0].Type,
                            measuredRecords[0].Payload).GetAwaiter().GetResult();
                    }
                    else
                    {
                        store.AppendBatchAsync("receipt", measuredRecords).GetAwaiter().GetResult();
                    }
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    samples.Add((
                        elapsed,
                        allocated,
                        observer.Receipt,
                        new FileInfo(store.EventPath("receipt")).Length));
                }
                finally
                {
                    DeleteRoot(root);
                }
            }

            var median = samples.OrderBy(sample => sample.Milliseconds).ElementAt(samples.Count / 2);
            Require(median.Receipt.LeaseAcquisitions == 1, $"{count}-record batch acquired {median.Receipt.LeaseAcquisitions} leases");
            Require(median.Receipt.StreamOpens == 1, $"{count}-record batch opened {median.Receipt.StreamOpens} streams");
            Require(median.Receipt.RecordsInLease == count, $"{count}-record lease covered {median.Receipt.RecordsInLease} records");
            Require(median.Receipt.BytesWritten == median.FileBytes, $"{count}-record byte receipt did not match the durable file");
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT event_log_batch records={count} elapsed_ms={median.Milliseconds:F3} " +
                $"allocated_bytes={median.Allocated} file_bytes={median.FileBytes} " +
                $"stream_opens={median.Receipt.StreamOpens} lease_acquisitions={median.Receipt.LeaseAcquisitions}");
        }
    }

    internal static void CancellationAndCrashPrefixRemainSafe()
    {
        var cancellationRoot = TemporaryRoot();
        FileStream? blocker = null;
        try
        {
            var store = new EventLogStore(cancellationRoot);
            var path = store.EventPath("blocked");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            blocker = new FileStream($"{path}.write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            RequireThrows<OperationCanceledException>(
                () => store.AppendBatchAsync("blocked", Records(10), cancellation.Token).GetAwaiter().GetResult(),
                "a canceled batch waiting for a cross-process lease was published");
            Require(!File.Exists(path), "canceled batch left a partial event log");

            blocker.Dispose();
            blocker = null;
            store.AppendBatchAsync("blocked", Records(2)).GetAwaiter().GetResult();
            Require(ReadIndexes(path).SequenceEqual([0, 1]), "session did not recover after canceled batch lease acquisition");
            Require(!File.Exists($"{path}.write.lock"), "recovered batch left the lease sidecar behind");
        }
        finally
        {
            blocker?.Dispose();
            DeleteRoot(cancellationRoot);
        }

        var crashRoot = TemporaryRoot();
        try
        {
            var observer = new EventLogWriteObserver
            {
                Clock = () => FixedTime,
                BeforeWrite = index =>
                {
                    if (index == 3)
                    {
                        throw new IOException("Injected process interruption between complete JSONL records.");
                    }
                }
            };
            var store = new EventLogStore(crashRoot, observer);
            RequireThrows<IOException>(
                () => store.AppendBatchAsync("crash-prefix", Records(8)).GetAwaiter().GetResult(),
                "injected record-boundary interruption did not fail the caller");
            var path = store.EventPath("crash-prefix");
            Require(ReadIndexes(path).SequenceEqual([0, 1, 2]), "interrupted batch did not leave the exact valid JSONL prefix");
            AssertEveryLineIsJson(path);

            new EventLogStore(crashRoot).AppendAsync("crash-prefix", "batch_event", new { index = 3, value = "fixed" })
                .GetAwaiter().GetResult();
            Require(ReadIndexes(path).SequenceEqual([0, 1, 2, 3]), "event log could not append after a record-boundary interruption");

            File.AppendAllText(path, "{\"type\":\"interrupted", Encoding.UTF8);
            new EventLogStore(crashRoot).AppendAsync("crash-prefix", "batch_event", new { index = 4, value = "recovered" })
                .GetAwaiter().GetResult();
            Require(ReadIndexes(path).SequenceEqual([0, 1, 2, 3, 4]),
                "event log did not truncate a process-kill-style partial trailing record before appending");
            AssertEveryLineIsJson(path);
        }
        finally
        {
            DeleteRoot(crashRoot);
        }
    }

    internal static void RotationConcurrencyAndSessionsRemainOrderedAndIsolated()
    {
        var rotationRoot = TemporaryRoot();
        try
        {
            var observer = new EventLogWriteObserver { Clock = () => FixedTime };
            var store = new EventLogStore(rotationRoot, observer);
            var records = Enumerable.Range(0, 5)
                .Select(index => new EventLogRecord("rotation_event", new { index, value = new string((char)('a' + index), 70_000) }))
                .ToArray();
            store.AppendBatchAsync("rotate", records).GetAwaiter().GetResult();
            var current = store.EventPath("rotate");
            var prefix = current[..^".jsonl".Length];
            Require(File.Exists($"{prefix}.1.jsonl") && File.Exists($"{prefix}.2.jsonl"),
                "large ordered batch did not preserve bounded rotation");
            var retainedOrder = new[] { $"{prefix}.2.jsonl", $"{prefix}.1.jsonl", current }
                .SelectMany(ReadIndexes)
                .ToArray();
            Require(retainedOrder.SequenceEqual([0, 1, 2, 3, 4]), "rotation changed ordered batch record order");
            Require(observer.Receipt.LeaseAcquisitions == 1 && observer.Receipt.StreamOpens == 3,
                $"rotation should reuse one lease while reopening only at limits: {observer.Receipt}");
        }
        finally
        {
            DeleteRoot(rotationRoot);
        }

        var concurrentRoot = TemporaryRoot();
        try
        {
            var observer = new EventLogWriteObserver { Clock = () => FixedTime };
            var store = new EventLogStore(concurrentRoot, observer);
            var tasks = Enumerable.Range(0, 100)
                .Select(index => store.AppendAsync("shared", "batch_event", new { index, value = "fixed" }))
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            Require(ReadIndexes(store.EventPath("shared")).SequenceEqual(Enumerable.Range(0, 100)),
                "transparent concurrent coalescing changed enqueue order");
            Require(observer.Receipt.LeaseAcquisitions < tasks.Length,
                $"concurrent appends were not coalesced: {observer.Receipt}");
            Require(observer.Receipt.RecordsInLease == tasks.Length,
                "concurrent coalescing lost or duplicated records");
            Console.WriteLine(
                $"OPTIMIZED_RECEIPT event_log_concurrent records={tasks.Length} " +
                $"lease_acquisitions={observer.Receipt.LeaseAcquisitions} stream_opens={observer.Receipt.StreamOpens} " +
                $"bytes_written={observer.Receipt.BytesWritten}");
        }
        finally
        {
            DeleteRoot(concurrentRoot);
        }

        var isolationRoot = TemporaryRoot();
        FileStream? sessionBlocker = null;
        try
        {
            var store = new EventLogStore(isolationRoot);
            var blockedPath = store.EventPath("blocked");
            Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
            sessionBlocker = new FileStream($"{blockedPath}.write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var blocked = store.AppendBatchAsync("blocked", Records(5));
            var independent = store.AppendBatchAsync("independent", Records(5));
            Require(independent.Wait(TimeSpan.FromSeconds(5)), "one session batch blocked an independent session queue");
            Require(ReadIndexes(store.EventPath("independent")).SequenceEqual(Enumerable.Range(0, 5)),
                "independent session batch changed or disappeared");
            Require(!blocked.IsCompleted, "blocked session unexpectedly bypassed its cross-process lease");

            sessionBlocker.Dispose();
            sessionBlocker = null;
            blocked.GetAwaiter().GetResult();
            Require(ReadIndexes(blockedPath).SequenceEqual(Enumerable.Range(0, 5)),
                "blocked session batch did not resume in order");
        }
        finally
        {
            sessionBlocker?.Dispose();
            DeleteRoot(isolationRoot);
        }
    }

    internal static void BatchAndLegacyWriterSerializeAcrossProcesses()
    {
        const int childCount = 24;
        var root = TemporaryRoot();
        var coordination = Path.Combine(root, "coordination");
        var ready = Path.Combine(coordination, "child.ready");
        var gate = Path.Combine(coordination, "go");
        var result = Path.Combine(coordination, "child.result");
        Process? child = null;
        try
        {
            Directory.CreateDirectory(coordination);
            child = StartExistingEventWriter(root, "shared", "legacy-child", ready, gate, result, childCount);
            Require(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(10)),
                "cross-process legacy writer did not reach its gate");

            var store = new EventLogStore(root);
            var parentBatch = store.AppendBatchAsync(
                "shared",
                Enumerable.Range(0, childCount)
                    .Select(index => new EventLogRecord("batch_parent_event", new { writer = "batch-parent", index }))
                    .ToArray());
            File.WriteAllText(gate, "go", new UTF8Encoding(false));
            parentBatch.GetAwaiter().GetResult();
            Require(child.WaitForExit(30_000), "cross-process legacy writer did not exit");
            Require(child.ExitCode == 0, $"cross-process legacy writer failed: {ReadResult(result)}");

            var lines = File.ReadAllLines(store.EventPath("shared"));
            Require(lines.Length == childCount * 2, "cross-process batch lost or duplicated JSONL records");
            var parentPositions = new List<int>();
            var childIndexes = new HashSet<int>();
            var parentIndexes = new HashSet<int>();
            for (var position = 0; position < lines.Length; position++)
            {
                using var document = JsonDocument.Parse(lines[position]);
                var rootElement = document.RootElement;
                var type = rootElement.GetProperty("type").GetString();
                var index = rootElement.GetProperty("payload").GetProperty("index").GetInt32();
                if (type == "batch_parent_event")
                {
                    parentPositions.Add(position);
                    parentIndexes.Add(index);
                }
                else
                {
                    Require(type == "cross_process_event", "cross-process writer emitted an unexpected event type");
                    childIndexes.Add(index);
                }
            }

            Require(parentIndexes.SetEquals(Enumerable.Range(0, childCount)), "parent batch records changed across processes");
            Require(childIndexes.SetEquals(Enumerable.Range(0, childCount)), "legacy child records changed across processes");
            Require(parentPositions.SequenceEqual(Enumerable.Range(parentPositions[0], childCount)),
                "another process interleaved records inside the parent lease batch");
            Require(!Directory.EnumerateFiles(root, "*.write.lock", SearchOption.AllDirectories).Any(),
                "cross-process batch left a lease artifact");
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5_000);
            }

            child?.Dispose();
            DeleteRoot(root);
        }
    }

    private static EventLogRecord[] Records(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new EventLogRecord("batch_event", new { index, value = "fixed" }))
            .ToArray();
    }

    private static byte[] LegacyBytes(IReadOnlyList<EventLogRecord> records, IReadOnlyList<DateTimeOffset> times)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var now = times[index];
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = record.Type,
                created_at = now.ToUnixTimeSeconds(),
                created_at_iso = now.ToString("O"),
                payload = record.Payload
            });
            output.Write(bytes);
            output.Write(Encoding.UTF8.GetBytes(Environment.NewLine));
        }

        return output.ToArray();
    }

    private static int[] ReadIndexes(string path)
    {
        return File.ReadLines(path)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("payload").GetProperty("index").GetInt32();
            })
            .ToArray();
    }

    private static void AssertOnlyPublicEnvelopeFields(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Require(names.SequenceEqual(["type", "created_at", "created_at_iso", "payload"]),
                "batch added internal queue, path, lease, or diagnostic data to the public event envelope");
        }
    }

    private static void AssertEveryLineIsJson(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            Require(document.RootElement.ValueKind == JsonValueKind.Object, "event prefix contains a non-object JSONL record");
        }
    }

    private static Process StartExistingEventWriter(
        string root,
        string sessionId,
        string writerId,
        string readyPath,
        string gatePath,
        string resultPath,
        int count)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Core test executable path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        start.ArgumentList.Add("--event-log-writer");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(sessionId);
        start.ArgumentList.Add(writerId);
        start.ArgumentList.Add(readyPath);
        start.ArgumentList.Add(gatePath);
        start.ArgumentList.Add(resultPath);
        start.ArgumentList.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the cross-process event writer.");
    }

    private static string ReadResult(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : "no result file";
    }

    private static string TemporaryRoot()
    {
        return Path.Combine(Path.GetTempPath(), "ai-arena-event-batch-tests", Guid.NewGuid().ToString("N"));
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

}
