namespace AIArena.CodeIntelligence;

public sealed class TestReliabilityAnalyzer
{
    private const int MaxEvidenceRuns = 64;

    public IReadOnlyList<TestReliabilitySummary> Summarize(
        IEnumerable<TestRunObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        return observations
            .Where(IsSafeObservation)
            .GroupBy(item => item.TestNodeId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(SummarizeTest)
            .ToArray();
    }

    private static TestReliabilitySummary SummarizeTest(
        IGrouping<string, TestRunObservation> group)
    {
        var ordered = group
            .OrderBy(item => item.ObservedAt)
            .ThenBy(item => item.RunId, StringComparer.Ordinal)
            .GroupBy(item => item.RunId, StringComparer.Ordinal)
            .Select(run => run.Last())
            .OrderBy(item => item.ObservedAt)
            .ThenBy(item => item.RunId, StringComparer.Ordinal)
            .ToArray();
        var observed = ordered
            .Where(item => item.Outcome != TestObservationOutcome.Unavailable)
            .ToArray();
        var terminal = observed
            .Where(item => item.Outcome is TestObservationOutcome.Passed or TestObservationOutcome.Failed)
            .ToArray();
        var passed = terminal.Count(item => item.Outcome == TestObservationOutcome.Passed);
        var failed = terminal.Count(item => item.Outcome == TestObservationOutcome.Failed);
        var skipped = observed.Count(item => item.Outcome == TestObservationOutcome.Skipped);
        var latest = terminal.LastOrDefault()?.Outcome;
        var consecutiveFailures = 0;
        for (var index = terminal.Length - 1;
             index >= 0 && terminal[index].Outcome == TestObservationOutcome.Failed;
             index--)
        {
            consecutiveFailures++;
        }

        var state = terminal.Length == 0
            ? TestReliabilityState.Unavailable
            : failed > 0 && passed > 0
                ? TestReliabilityState.Flaky
                : consecutiveFailures >= 2
                    ? TestReliabilityState.RepeatedlyFailing
                    : latest == TestObservationOutcome.Failed
                        ? TestReliabilityState.Failing
                        : TestReliabilityState.Passing;
        var availability = terminal.Length == 0
            ? ImpactAvailability.Unavailable
            : ordered.Any(item => item.Outcome == TestObservationOutcome.Unavailable)
                ? ImpactAvailability.Partial
                : ImpactAvailability.Available;
        var representative = ordered.Last();
        return new TestReliabilitySummary(
            group.Key,
            representative.TestName,
            representative.ProjectRelativePath,
            availability,
            state,
            passed,
            failed,
            skipped,
            consecutiveFailures,
            latest,
            ordered
                .Select(item => item.RunId)
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaxEvidenceRuns)
                .ToArray());
    }

    private static bool IsSafeObservation(TestRunObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.TestNodeId)
        && observation.TestNodeId.Length <= 200
        && !string.IsNullOrWhiteSpace(observation.TestName)
        && observation.TestName.Length <= 1_000
        && IsSafeRelativePath(observation.ProjectRelativePath)
        && !string.IsNullOrWhiteSpace(observation.RunId)
        && observation.RunId.Length <= 200
        && !observation.RunId.Any(char.IsControl);

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\0'))
        {
            return false;
        }
        var normalized = path.Replace('\\', '/');
        return normalized.Split('/').All(segment => segment is not ("" or "." or ".."));
    }
}
