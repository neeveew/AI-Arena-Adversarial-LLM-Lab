using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class ArenaTurnActivityTests
{
    public static void ObservedStagesRemainContentFreeAndPublicDeltasExact()
    {
        var recorder = new Recorder();
        IProgress<string>? lateText = null;
        IProgress<ModelProviderActivity>? lateActivity = null;
        var client = new ActivityClient((config, text, activity, _) =>
        {
            lateText = text;
            lateActivity = activity;
            activity!.Report(Activity(ModelProviderActivityStage.Writing, publicCharacters: 900));
            activity.Report(Activity(ModelProviderActivityStage.Loading));
            activity.Report(Activity(ModelProviderActivityStage.Loading));
            activity.Report(Activity(ModelProviderActivityStage.ReadingContext));
            activity.Report(Activity(ModelProviderActivityStage.Thinking, reasoningCharacters: 200));
            text!.Report("Literal <public>\n");
            activity.Report(Activity(ModelProviderActivityStage.Writing, publicCharacters: 17));
            text.Report("answer.");
            activity.Report(Activity((ModelProviderActivityStage)999));
            return Task.FromResult(Success(config, "Literal <public>\nanswer."));
        });
        using var scope = Scope(recorder);
        var result = scope.CompleteAsync(client, Config(), [], CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Ok && client.ActivityCalls == 1 && client.StreamingCalls == 0 && client.BufferedCalls == 0,
            "an observed call must prefer the additive activity contract over its legacy streaming and buffered contracts");
        Require(recorder.Events.First().Activity is null,
            "starting a quiet provider request must not infer loading, context processing, or thinking");
        Require(recorder.Events.Where(item => item.Kind == ArenaTurnProgressKind.Activity).Select(item => item.Activity)
                .SequenceEqual(new ArenaTurnActivity?[] { ArenaTurnActivity.Loading, ArenaTurnActivity.ReadingContext, ArenaTurnActivity.Thinking }),
            "observed stages must retain order, coalesce repeated stages, and ignore unsupported or premature writing reports");
        Require(recorder.Events.Where(item => item.Kind == ArenaTurnProgressKind.Delta).Select(item => item.Text)
                .SequenceEqual(new[] { "Literal <public>\n", "answer." })
                && recorder.Events.Where(item => item.Kind != ArenaTurnProgressKind.Delta).All(item => item.Text.Length == 0),
            "only exact public deltas may carry text; reasoning activity and counters must not enter public progress text");
        var count = recorder.Events.Count;
        lateText!.Report("late response");
        lateActivity!.Report(Activity(ModelProviderActivityStage.Thinking));
        Require(recorder.Events.Count == count, "provider callbacks arriving after the request returned must be rejected");
    }

    public static void WithheldToolChoiceNeverClaimsVisibleWriting()
    {
        const string choice = "{\"tool\":\"private choice contents\"}";
        var recorder = new Recorder();
        var client = new ActivityClient((config, text, activity, _) =>
        {
            activity!.Report(Activity(ModelProviderActivityStage.Thinking, reasoningCharacters: 100));
            text!.Report(choice);
            activity.Report(Activity(ModelProviderActivityStage.Writing, publicCharacters: choice.Length));
            return Task.FromResult(Success(config, choice));
        });
        using var scope = Scope(recorder);
        scope.CompleteAsync(client, Config(), [], CancellationToken.None, publishText: false).GetAwaiter().GetResult();
        Require(recorder.Events.All(item => item.Text.Length == 0 && item.Activity != ArenaTurnActivity.Writing)
                && !recorder.Events.Any(item => item.Kind == ArenaTurnProgressKind.Delta),
            "withheld tool-choice deltas and buffered results must expose neither their contents nor a false visible-writing state");
        Require(recorder.Events.Any(item => item.Activity == ArenaTurnActivity.Thinking),
            "content-free reasoning activity remains useful while tool-choice output is withheld");
    }

    public static void ReducedReasoningRecoverySeparatesAttemptActivity()
    {
        var recorder = new Recorder();
        IProgress<string>? oldText = null;
        IProgress<ModelProviderActivity>? oldActivity = null;
        var calls = 0;
        var client = new ActivityClient((config, text, activity, _) =>
        {
            if (++calls == 1)
            {
                oldText = text;
                oldActivity = activity;
                activity!.Report(Activity(ModelProviderActivityStage.Thinking));
                return Task.FromResult(Success(config, ""));
            }
            var before = recorder.Events.Count;
            oldText!.Report("discarded old answer");
            oldActivity!.Report(Activity(ModelProviderActivityStage.Loading));
            Require(recorder.Events.Count == before, "old-attempt activity must not replace the retry status");
            Require(recorder.Events.Last().Kind == ArenaTurnProgressKind.Started
                    && recorder.Events.Last().Activity == ArenaTurnActivity.RetryingWithReducedReasoning,
                "the new attempt must retain the recovery explanation until it reports actual work");
            activity!.Report(Activity(ModelProviderActivityStage.Thinking));
            text!.Report("Recovered answer.");
            return Task.FromResult(Success(config, "Recovered answer."));
        });
        using var scope = Scope(recorder);
        scope.CompleteAsync(client, Config(), [], CancellationToken.None).GetAwaiter().GetResult();
        scope.Interrupt("failed");
        scope.RecoveryStarting();
        Require(recorder.Events.Last().Kind == ArenaTurnProgressKind.Activity
                && recorder.Events.Last().Activity == ArenaTurnActivity.RetryingWithReducedReasoning
                && recorder.Events.Last().Text.Length == 0,
            "reasoning recovery must explain the retry even when the previous attempt was interrupted");
        var beforeRetry = recorder.Events.Count;
        oldText!.Report("too late");
        oldActivity!.Report(Activity(ModelProviderActivityStage.Writing));
        Require(recorder.Events.Count == beforeRetry, "recovery must close callbacks before the next request begins");
        var recoveryConfig = Config("off");
        scope.CompleteAsync(client, recoveryConfig, [], CancellationToken.None).GetAwaiter().GetResult();
        var starts = recorder.Events.Where(item => item.Kind == ArenaTurnProgressKind.Started).ToArray();
        Require(starts.Length == 2 && starts[0].OperationId == starts[1].OperationId && starts[0].AttemptId != starts[1].AttemptId,
            "recovery must preserve the live operation while giving its new provider request a separate attempt identity");
        Require(recorder.Events.Where(item => item.Kind == ArenaTurnProgressKind.Delta).Select(item => item.Text)
                .SequenceEqual(new[] { "Recovered answer." }),
            "superseded callbacks must never mix with the recovered public response");
        Require(recorder.Events.Any(item => item.AttemptId == starts[1].AttemptId && item.Activity == ArenaTurnActivity.Thinking),
            "actual activity from the retry must replace its provisional recovery label");
    }

    public static void ActivityCallbacksAfterCancellationAreRejected()
    {
        var recorder = new Recorder();
        using var cancellation = new CancellationTokenSource();
        IProgress<string>? lateText = null;
        IProgress<ModelProviderActivity>? lateActivity = null;
        var client = new ActivityClient((_, text, activity, token) =>
        {
            lateText = text;
            lateActivity = activity;
            activity!.Report(Activity(ModelProviderActivityStage.Thinking));
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        });
        using var scope = Scope(recorder);
        try
        {
            scope.CompleteAsync(client, Config(), [], cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("The canceled provider request unexpectedly succeeded.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Require(recorder.Events.Last().Kind == ArenaTurnProgressKind.Interrupted && recorder.Events.Last().Status == "canceled",
            "cancellation must terminate the active progress attempt");
        var count = recorder.Events.Count;
        lateText!.Report("late text");
        lateActivity!.Report(Activity(ModelProviderActivityStage.Loading));
        scope.Dispose();
        Require(recorder.Events.Count == count, "cancellation and disposal must reject late activity and avoid duplicate terminal events");
    }

    public static void ActivityObserverFailuresDoNotAlterCompletion()
    {
        var client = new ActivityClient((config, text, activity, _) =>
        {
            activity!.Report(Activity(ModelProviderActivityStage.Loading));
            activity.Report(Activity(ModelProviderActivityStage.Thinking));
            text!.Report("Answer");
            return Task.FromResult(Success(config, "Answer"));
        });
        using var scope = Scope(new ThrowingObserver());
        var result = scope.CompleteAsync(client, Config(), [], CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Ok && result.Text == "Answer", "optional activity observers must not fail or alter a model completion");
    }

    public static void UnobservedActivityClientKeepsBufferedContract()
    {
        var client = new ActivityClient((config, _, _, _) => Task.FromResult(Success(config, "Answer")));
        using var scope = Scope(null);
        var result = scope.CompleteAsync(client, Config(), [], CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Ok && client.BufferedCalls == 1 && client.ActivityCalls == 0 && client.StreamingCalls == 0,
            "calls without presentation observers must retain the existing buffered behavior");
    }

    private static ArenaTurnProgressScope Scope(IProgress<ArenaTurnProgress>? observer) =>
        new(observer, "session", "instance", "alpha", "Alpha", 7);
    private static ModelProviderConfig Config(string reasoning = "auto") => new() { BaseUrl = "http://127.0.0.1:1234/v1", Model = "fixture-model", Reasoning = reasoning };
    private static ModelProviderActivity Activity(ModelProviderActivityStage stage, int publicCharacters = 0, int reasoningCharacters = 0) =>
        new(stage, DateTimeOffset.UtcNow, 10, publicCharacters, reasoningCharacters);
    private static ModelCompletionResult Success(ModelProviderConfig config, string text) =>
        new(true, config.BaseUrl, config.Model, text, "", 123, 100, 20, 120, "", DateTimeOffset.UtcNow,
            StopReason: ModelCompletionStopReason.Completed);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Recorder : IProgress<ArenaTurnProgress>
    {
        internal List<ArenaTurnProgress> Events { get; } = [];
        public void Report(ArenaTurnProgress value) => Events.Add(value);
    }
    private sealed class ThrowingObserver : IProgress<ArenaTurnProgress>
    {
        public void Report(ArenaTurnProgress value) => throw new InvalidOperationException("Synthetic observer failure.");
    }
    private sealed class ActivityClient(
        Func<ModelProviderConfig, IProgress<string>?, IProgress<ModelProviderActivity>?, CancellationToken, Task<ModelCompletionResult>> complete)
        : IModelProviderClient, IActivityStreamingModelProviderClient
    {
        internal int BufferedCalls { get; private set; }
        internal int StreamingCalls { get; private set; }
        internal int ActivityCalls { get; private set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            BufferedCalls++;
            return complete(config, null, null, cancellationToken);
        }
        public Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            return complete(config, progress, null, cancellationToken);
        }
        public Task<ModelCompletionResult> CompleteChatStreamingWithActivityAsync(ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages, IProgress<string>? publicProgress,
            IProgress<ModelProviderActivity>? activity, CancellationToken cancellationToken = default)
        {
            ActivityCalls++;
            return complete(config, publicProgress, activity, cancellationToken);
        }
    }
}
