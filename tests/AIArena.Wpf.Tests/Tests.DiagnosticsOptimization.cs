using System.Diagnostics;
using System.Text.Json;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;

internal static partial class Program
{
    static void DiagnosticsProjectionCacheMatchesLegacyAcrossInvalidations()
    {
        var service = new DiscourseDiagnosticsService();
        var personas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "Generate hypotheses.",
            ["beta"] = "Challenge assumptions."
        };
        var cache = new DiagnosticsProjectionCache(service);
        var messages = Enumerable.Range(1, 100).Select(DiagnosticMessage).ToArray();

        var initial = cache.Project(messages, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, messages, personas), initial, "initial rebuild");
        Require(initial.Receipt.Mode == "rebuild" && initial.Receipt.SortedItems == 0, "an already ordered initial transcript should avoid sorting");

        var reused = cache.Project(messages, personas);
        RequireProjectionEquals(initial, reused, "unchanged reuse");
        Require(reused.Receipt.Mode == "reuse" && reused.Receipt.AnalyzedWindows == 0 && reused.Receipt.MappedTurns == 0,
            "an unchanged projection should reuse all diagnostic work");

        messages = messages.Append(DiagnosticMessage(101)).ToArray();
        var appended = cache.Project(messages, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, messages, personas), appended, "append-only update");
        Require(appended.Receipt.Mode == "append" && appended.Receipt.MappedTurns == 1 && appended.Receipt.SortedItems == 0,
            "an append inside the same stride should map only the new turn and avoid sorting");
        Require(appended.Receipt.AnalyzedWindows <= 3 && appended.Receipt.RetainedPoints <= 36,
            "an append should keep diagnostic analyses and retained sparkline points bounded");

        var edited = messages.ToArray();
        edited[50] = edited[50] with { Text = "However, the retried claim is not proven." };
        var editProjection = cache.Project(edited, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, edited, personas), editProjection, "historical edit");
        Require(editProjection.Receipt.Mode == "rebuild", "editing a historical message should invalidate the projection");

        var retried = edited.ToArray();
        retried[^1] = retried[^1] with { Text = "Retry replacement with a source.", InternetSources = ["retry-source"] };
        var retryProjection = cache.Project(retried, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, retried, personas), retryProjection, "retry replacement");
        Require(retryProjection.Receipt.Mode == "rebuild", "replacing the latest retry should invalidate the projection");

        var reset = retried.Take(12).ToArray();
        var resetProjection = cache.Project(reset, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, reset, personas), resetProjection, "reset/shorter history");
        Require(resetProjection.Receipt.Mode == "rebuild", "a reset to a shorter history should invalidate the projection");

        var beforeStrideChange = Enumerable.Range(1, 108).Select(DiagnosticMessage).ToArray();
        _ = cache.Project(beforeStrideChange, personas);
        var afterStrideChange = beforeStrideChange.Append(DiagnosticMessage(109)).ToArray();
        var strideProjection = cache.Project(afterStrideChange, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, afterStrideChange, personas), strideProjection, "stride change");
        Require(strideProjection.Receipt.Mode == "stride-rebuild" && strideProjection.Receipt.Stride == 4,
            "crossing a history stride boundary should rebuild sampled points exactly");

        var outOfOrder = afterStrideChange.ToArray();
        (outOfOrder[2], outOfOrder[^2]) = (outOfOrder[^2], outOfOrder[2]);
        var unorderedProjection = cache.Project(outOfOrder, personas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, outOfOrder, personas), unorderedProjection, "out-of-order history");
        Require(unorderedProjection.Receipt.Mode == "out-of-order-rebuild" && unorderedProjection.Receipt.SortedItems == outOfOrder.Length,
            "out-of-order input should use the stable sorted fallback and report its work");

        var changedPersonas = new Dictionary<string, string>(personas, StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "A changed role contract."
        };
        var personaProjection = cache.Project(afterStrideChange, changedPersonas);
        RequireProjectionEquals(LegacyDiagnosticsProjection(service, afterStrideChange, changedPersonas), personaProjection, "persona mutation");
        Require(personaProjection.Receipt.Mode == "rebuild", "persona changes should invalidate historical role-drift points");

        cache.Reset();
        Require(cache.LastReceipt.Mode == "reset" && cache.LastReceipt.RetainedPoints == 0,
            "explicit reset should release cached history and publish a deterministic empty receipt");
    }

    static void DiagnosticsProjectionReceiptScalesAtLargeHistories()
    {
        var personas = new Dictionary<string, string>();
        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            var service = new DiscourseDiagnosticsService();
            var cache = new DiagnosticsProjectionCache(service);
            var messages = Enumerable.Range(1, count).Select(DiagnosticMessage).ToArray();
            var appendedMessages = messages.Append(DiagnosticMessage(count + 1)).ToArray();
            var legacyPerformance = MedianProjectionReceipt(() =>
                LegacyDiagnosticsProjection(new DiscourseDiagnosticsService(), messages, personas));
            var initial = cache.Project(messages, personas);
            var appended = cache.Project(appendedMessages, personas);
            var appendPerformance = MedianProjectionReceipt(() =>
            {
                var measuredCache = new DiagnosticsProjectionCache(new DiscourseDiagnosticsService());
                _ = measuredCache.Project(messages, personas);
                return measuredCache.Project(appendedMessages, personas);
            });

            Console.WriteLine(
                $"diagnostics-projection-receipt count={count} initial_windows={initial.Receipt.AnalyzedWindows} " +
                $"append_mode={appended.Receipt.Mode} append_windows={appended.Receipt.AnalyzedWindows} " +
                $"append_mapped={appended.Receipt.MappedTurns} append_sorted={appended.Receipt.SortedItems} " +
                $"retained_points={appended.Receipt.RetainedPoints} " +
                $"legacy_us={legacyPerformance.ElapsedMicroseconds:F1} legacy_bytes={legacyPerformance.AllocatedBytes} " +
                $"append_us={appendPerformance.ElapsedMicroseconds:F1} append_bytes={appendPerformance.AllocatedBytes}");

            Require(initial.Receipt.AnalyzedWindows <= 39 && initial.Receipt.RetainedPoints <= 36,
                $"initial diagnostics work should stay bounded by the 36-point history at {count} messages");
            Require(appended.Receipt.MappedTurns == 1 && appended.Receipt.SortedItems == 0,
                $"append diagnostics should map one turn and sort no history at {count} messages");
            Require(appended.Receipt.AnalyzedWindows <= 39 && appended.Receipt.RetainedPoints <= 36,
                $"stride rebuilds and incremental appends should both remain bounded at {count} messages");
        }
    }

    private static (double ElapsedMicroseconds, long AllocatedBytes) MedianProjectionReceipt(
        Func<DiagnosticsProjection> action)
    {
        _ = action();
        var samples = new List<(long Ticks, long Bytes)>();
        for (var iteration = 0; iteration < 7; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            _ = action();
            samples.Add((Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - before));
        }

        samples.Sort((left, right) => left.Ticks.CompareTo(right.Ticks));
        var median = samples[samples.Count / 2];
        return (median.Ticks * 1_000_000.0 / Stopwatch.Frequency, median.Bytes);
    }

    private static DiagnosticsProjection LegacyDiagnosticsProjection(
        DiscourseDiagnosticsService service,
        IReadOnlyList<TranscriptMessage> messages,
        IReadOnlyDictionary<string, string> personas)
    {
        var diagnostics = service.Analyze(messages.Select(DiagnosticsWorkflowCoordinator.ToDiscourseTurn), personas);
        var ordered = messages
            .OrderBy(message => message.Turn)
            .ThenBy(message => message.CreatedAt)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new DiagnosticsProjection(
                diagnostics,
                new DiagnosticSeriesSet([], 0),
                new DiagnosticProjectionReceipt("legacy", 0, 0, 0, 0, 0, 0));
        }

        var stride = Math.Max(1, (int)Math.Ceiling(ordered.Length / 36d));
        var points = new List<DiagnosticHistoryPoint>();
        for (var end = 1; end <= ordered.Length; end += stride)
        {
            points.Add(DiagnosticsProjectionCache.PointForWindow(service, ordered, end, personas));
        }

        var final = DiagnosticsProjectionCache.PointForWindow(service, ordered, ordered.Length, personas);
        if (points.Count == 0 || points[^1] != final)
        {
            points.Add(final);
        }

        if (points.Count > 36)
        {
            points = points.Skip(points.Count - 36).ToList();
        }

        return new DiagnosticsProjection(
            diagnostics,
            new DiagnosticSeriesSet(
                points,
                points.Select(point => point.UnsupportedClaims).DefaultIfEmpty(0).Max()),
            new DiagnosticProjectionReceipt("legacy", ordered.Length, stride, ordered.Length, ordered.Length, points.Count, points.Count));
    }

    private static void RequireProjectionEquals(
        DiagnosticsProjection expected,
        DiagnosticsProjection actual,
        string context)
    {
        Require(
            JsonSerializer.Serialize(expected.Diagnostics) == JsonSerializer.Serialize(actual.Diagnostics),
            $"optimized current diagnostics should match the legacy projection for {context}");
        Require(
            expected.Series.Points.SequenceEqual(actual.Series.Points)
            && expected.Series.UnsupportedClaimsMax == actual.Series.UnsupportedClaimsMax,
            $"optimized diagnostic history should match the legacy projection for {context}");
    }

    private static TranscriptMessage DiagnosticMessage(int index)
    {
        var speaker = index % 13 == 0
            ? "system"
            : (index % 4) switch
            {
                0 => "alpha",
                1 => "beta",
                2 => "gamma",
                _ => "delta"
            };
        return new TranscriptMessage(
            index,
            speaker,
            speaker,
            index,
            "model",
            0,
            0,
            0,
            0,
            "ok",
            "",
            false,
            speaker == "system" ? "status" : "message",
            index % 7 == 0
                ? $"However, hypothesis {index} cites turn {Math.Max(1, index - 1)} and https://example.test/{index}."
                : $"Claim {index}: building on the framework.",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            false,
            index % 11 == 0 ? [$"source-{index}"] : []);
    }
}
