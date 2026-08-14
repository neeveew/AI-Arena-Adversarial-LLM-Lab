using System.Diagnostics;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Services;

internal static class DiscourseDiagnosticsOptimizationTests
{
    public static void OrderedAndBoundedSelectionMatchesLegacyOracle()
    {
        var service = new DiscourseDiagnosticsService();
        var personas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alpha"] = "Generate hypotheses.",
            ["beta"] = "Challenge assumptions."
        };

        for (var seed = 0; seed < 80; seed++)
        {
            var random = new Random(seed);
            var turns = Enumerable.Range(0, random.Next(0, 220))
                .Select(index => Turn(random, index))
                .OrderBy(_ => random.Next())
                .ToArray();

            var expected = LegacyOracle(service, turns, personas);
            var arbitrary = service.Analyze(turns, personas);
            RequireEquivalent(expected, arbitrary, $"arbitrary seed {seed}");

            var ordered = turns
                .OrderBy(turn => turn.Turn)
                .ThenBy(turn => turn.CreatedAt)
                .ToArray();
            var orderedResult = service.AnalyzeOrdered(ordered, personas);
            RequireEquivalent(expected, orderedResult, $"ordered seed {seed}");
        }

        // Equal-key ordering is deliberately observable through bounded detail
        // output. Stable source order must survive both optimized paths.
        var ties = Enumerable.Range(0, 14)
            .Select(index => new DiscourseTurn(
                9,
                index % 3 == 0 ? "system" : "alpha",
                $"Speaker {index}",
                index % 4 == 0 ? "internet" : "message",
                $"Marker {index}: however, building on the hypothesis.",
                index % 5 == 0 ? [$"source-{index}"] : [],
                42))
            .ToArray();
        RequireEquivalent(LegacyOracle(service, ties, personas), service.Analyze(ties, personas), "equal-key fallback");
        RequireEquivalent(LegacyOracle(service, ties, personas), service.AnalyzeOrdered(ties, personas), "equal-key ordered tail");
    }

    public static void PerformanceReceiptShowsBoundedSelection()
    {
        var service = new DiscourseDiagnosticsService();
        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            var turns = Enumerable.Range(0, count)
                .Select(index => Turn(new Random(index), index))
                .OrderBy(turn => turn.Turn)
                .ThenBy(turn => turn.CreatedAt)
                .ToArray();

            _ = service.AnalyzeOrdered(turns);
            _ = LegacyOracle(service, turns, null);
            var optimized = MedianReceipt(() => service.AnalyzeOrdered(turns));
            var legacy = MedianReceipt(() => LegacyOracle(service, turns, null));
            Console.WriteLine(
                $"diagnostics-receipt count={count} " +
                $"legacy_us={legacy.ElapsedMicroseconds:F1} optimized_us={optimized.ElapsedMicroseconds:F1} " +
                $"legacy_bytes={legacy.AllocatedBytes} optimized_bytes={optimized.AllocatedBytes} " +
                $"legacy_sorted_items={count} optimized_tail_limit=8");

            Require(
                optimized.AllocatedBytes < legacy.AllocatedBytes,
                $"ordered-tail diagnostics should allocate less than the legacy full sort at {count} turns");
            Require(
                JsonSerializer.Serialize(service.AnalyzeOrdered(turns)) == JsonSerializer.Serialize(LegacyOracle(service, turns, null)),
                $"performance corpus must preserve exact diagnostic output at {count} turns");
        }
    }

    private static DiscourseTurn Turn(Random random, int index)
    {
        var speaker = random.Next(7) switch
        {
            0 => "system",
            1 => "internet",
            2 => "transcript",
            3 => "operator",
            4 => "alpha",
            5 => "beta",
            _ => "gamma"
        };
        var createdAt = random.Next(5);
        var turn = random.Next(0, Math.Max(2, index / 3 + 2));
        var text = random.Next(4) switch
        {
            0 => $"I agree exactly; the framework stands at {index}.",
            1 => $"However this assumption is not proven; see turn {turn}.",
            2 => $"According to https://example.test/{index}, this hypothesis is speculative.",
            _ => $"Plain response {index}."
        };
        return new DiscourseTurn(
            turn,
            speaker,
            speaker,
            random.Next(6) == 0 ? "internet" : "message",
            text,
            random.Next(5) == 0 ? [$"source-{random.Next(4)}"] : [],
            createdAt);
    }

    private static FrictionDiagnostics LegacyOracle(
        DiscourseDiagnosticsService service,
        IReadOnlyList<DiscourseTurn> turns,
        IReadOnlyDictionary<string, string>? personas)
    {
        var newest = turns
            .OrderByDescending(turn => turn.Turn)
            .ThenByDescending(turn => turn.CreatedAt)
            .ToArray();
        return AnalyzeLegacyWindows(
            newest.Take(8).ToArray(),
            newest.Where(turn => !IsSystem(turn)).Take(8).ToArray(),
            personas);
    }

    private static FrictionDiagnostics AnalyzeLegacyWindows(
        IReadOnlyList<DiscourseTurn> evidence,
        IReadOnlyList<DiscourseTurn> conversation,
        IReadOnlyDictionary<string, string>? personas)
    {
        var method = typeof(DiscourseDiagnosticsService).GetMethod(
            "AnalyzeWindows",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Legacy diagnostics oracle could not locate AnalyzeWindows.");
        return (FrictionDiagnostics)(method.Invoke(
            null,
            [evidence, conversation, personas ?? new Dictionary<string, string>()])
            ?? throw new InvalidOperationException("Legacy diagnostics oracle returned no result."));
    }

    private static bool IsSystem(DiscourseTurn turn)
    {
        var speaker = string.IsNullOrWhiteSpace(turn.SpeakerId) ? "" : turn.SpeakerId.Trim().ToLowerInvariant();
        return speaker is "system" or "internet" or "transcript"
            || turn.Kind.Equals("internet", StringComparison.OrdinalIgnoreCase);
    }

    private static (double ElapsedMicroseconds, long AllocatedBytes) MedianReceipt(Func<FrictionDiagnostics> action)
    {
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

    private static void RequireEquivalent(
        FrictionDiagnostics expected,
        FrictionDiagnostics actual,
        string context)
    {
        Require(
            JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual),
            $"optimized discourse diagnostics must match the legacy selection oracle for {context}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
