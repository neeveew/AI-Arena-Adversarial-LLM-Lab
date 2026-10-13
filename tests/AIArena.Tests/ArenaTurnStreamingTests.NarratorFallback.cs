using AIArena.Core.Models;
using AIArena.Core.Services;

internal static partial class ArenaTurnStreamingTests
{
    public static void NarratorFallbackRetainsTerminalProviderOutcomes() =>
        RunNarratorFallbackCases(["filtered", "tool"], expectFallback: false);

    public static void NarratorFallbackRetainsAcceptedFailedPartialOutput() =>
        RunNarratorFallbackCases(["partial"], expectFallback: false);

    public static void NarratorFallbackKeepsEligibleEmptyTransportFailures() =>
        RunNarratorFallbackCases(["transport"], expectFallback: true);

    private static void RunNarratorFallbackCases(string[] outcomes, bool expectFallback)
    {
        const string partial = " \tAccepted public prefix.\r\n ";
        const string failure = "Primary fixture response ended.";
        var wrong = new List<string>();
        foreach (var route in new[] { "narrator", "ask-narrator", "decision-card" })
        foreach (var streaming in new[] { false, true }.Where(value => route != "decision-card" || !value))
        foreach (var outcome in outcomes)
        {
            WithFixture((store, log, _) =>
            {
                var snapshot = Load(store);
                snapshot.Configs["narrator"] = Config("primary-narrator");
                snapshot.Configs["shared"] = Config("fallback-narrator");
                store.SaveSnapshotAsync(snapshot).GetAwaiter().GetResult();
                var models = new List<string>();
                var client = new StreamingClient((config, progress, _) =>
                {
                    models.Add(config.Model);
                    if (models.Count > 1)
                    {
                        progress?.Report(Answer);
                        return Task.FromResult(Success(config, Answer));
                    }
                    var text = outcome == "partial" ? partial : "";
                    if (text.Length > 0) progress?.Report(text);
                    return Task.FromResult(Success(config, text) with
                    {
                        Ok = false, Error = failure,
                        FailureKind = outcome == "partial" || outcome == "transport"
                            ? ModelCompletionFailureKind.Transport : ModelCompletionFailureKind.ProviderRejected,
                        StopReason = outcome == "filtered" ? ModelCompletionStopReason.ContentFiltered
                            : outcome == "tool" ? ModelCompletionStopReason.ToolCall : ModelCompletionStopReason.ProviderError,
                        Reasoning = "PRIVATE_REASONING_FIXTURE"
                    });
                });
                using var service = new NarratorService(client, store, log);
                var observer = streaming ? new Recorder(store) : null;
                bool ok;
                bool committed;
                string error;
                if (route == "decision-card")
                {
                    var result = service.GenerateDecisionCardAsync("default").GetAwaiter().GetResult();
                    ok = result.Ok;
                    committed = result.Committed;
                    error = result.Error;
                }
                else
                {
                    var result = route == "narrator"
                        ? service.NarrateNowAsync("default", progress: observer).GetAwaiter().GetResult()
                        : service.AskNarratorAsync("default", "Explain the decision.", progress: observer).GetAwaiter().GetResult();
                    ok = result.Ok;
                    committed = result.Message is not null;
                    error = result.Error;
                    if (!expectFallback && result.Message is { } message)
                    {
                        if (message.Model.Model != "primary-narrator" || message.Status != "error"
                            || !message.Metadata.TryGetValue("completion_failure_kind", out var classification)
                            || classification.GetString() != ModelCompletionOutcomeClassifier.FailureKindWire(outcome == "partial"
                                ? ModelCompletionFailureKind.Transport : ModelCompletionFailureKind.ProviderRejected))
                            wrong.Add($"{route}/{outcome}/saved-classification");
                    }
                }
                var saved = Load(store);
                var expectedModels = expectFallback ? new[] { "primary-narrator", "fallback-narrator" } : new[] { "primary-narrator" };
                var fallbackEvents = File.ReadAllLines(log.EventPath()).Count(line => line.Contains("fallback_to_default", StringComparison.Ordinal));
                if (ok != expectFallback || !committed || error != (expectFallback ? "" : failure)
                    || !models.SequenceEqual(expectedModels) || fallbackEvents != (expectFallback ? 1 : 0)
                    || saved.Engine.Narrator.Status != (expectFallback ? "spoke" : "error")
                    || saved.Engine.LastError != (expectFallback ? "" : failure))
                    wrong.Add($"{route}/{outcome}/{(streaming ? "stream" : "buffered")}");
                if (observer is not null && outcome == "partial" && !expectFallback)
                {
                    var publicDeltas = string.Concat(observer.Events.Where(value => value.Kind == ArenaTurnProgressKind.Delta).Select(value => value.Text));
                    if (publicDeltas != partial || observer.Events.Count(value => value.Kind == ArenaTurnProgressKind.Started) != 1
                        || observer.Events.Any(value => value.Text.Contains("PRIVATE_REASONING", StringComparison.Ordinal)))
                        wrong.Add($"{route}/partial-progress");
                }
                Console.WriteLine($"narrator fallback receipt: route={route}; outcome={outcome}; streaming={streaming}; "
                    + $"models=[{string.Join(',', models)}]; saved={committed}; success={ok}; fallback_events={fallbackEvents}");
            });
        }
        Require(wrong.Count == 0, "Narrator fallback policy differed from the causal provider outcome: " + string.Join(", ", wrong));
    }
}
