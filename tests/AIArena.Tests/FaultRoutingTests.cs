using System.Collections.Immutable;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class FaultRoutingTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly string Setup = new('a', 64);

    internal static void InjectsEveryBoundedFaultOutsideProtocolClient()
    {
        foreach (var kind in Enum.GetValues<ArenaFaultKind>())
        {
            var inner = new RecordingProviderClient();
            using var decorator = FaultInjectingModelProviderClient.Arm(inner, Profile(kind));
            Require(decorator.IsArmed && decorator.ArmedProfileId == $"fault-profile:{FaultLabel(kind)}", $"{kind} arm state changed");
            var progress = new InlineProgress();
            var result = kind is ArenaFaultKind.MalformedStream or ArenaFaultKind.Interruption
                ? decorator.CompleteChatStreamingAsync(Config(), Messages(), progress).GetAwaiter().GetResult()
                : decorator.CompleteChatAsync(Config(), Messages()).GetAwaiter().GetResult();

            Require(!result.Ok, $"{kind} did not fail at the decorator boundary");
            Require(result.Error.Contains(FaultLabel(kind), StringComparison.Ordinal), $"{kind} cause was not explicit");
            Require(inner.ChatCalls == 0 && inner.StreamCalls == 0, $"{kind} leaked into the provider protocol client");
            var observation = decorator.SnapshotObservations().Single();
            Require(observation.InjectedCause == kind, $"{kind} cause observation changed");
            Require(observation.ObservedOutcome == ArenaFaultObservedOutcome.InjectedFailure, $"{kind} outcome changed");
            Require(observation.CauseEvidence.State == ArenaEvidenceState.Observed, $"{kind} cause was not observed");
            Require(observation.RecoveryEvidence.State == ArenaEvidenceState.Unavailable, $"{kind} invented recovery evidence");
        }

        var disarmed = FaultInjectingModelProviderClient.Arm(new RecordingProviderClient(), Profile(ArenaFaultKind.Disconnect));
        disarmed.Disarm();
        Require(!disarmed.IsArmed, "disarm state was not observable");
        RequireThrows<ObjectDisposedException>(
            () => disarmed.CompleteChatAsync(Config(), Messages()).GetAwaiter().GetResult(),
            "disarmed decorator accepted provider work");
    }

    internal static void PreservesStreamingPartialAndMalformedSemantics()
    {
        var malformedProgress = new InlineProgress();
        using (var malformed = new FaultInjectingModelProviderClient(new RecordingProviderClient(), Profile(ArenaFaultKind.MalformedStream)))
        {
            var result = malformed.CompleteChatStreamingAsync(Config(), Messages(), malformedProgress).GetAwaiter().GetResult();
            Require(!result.Ok && malformedProgress.Values.SequenceEqual(["\uFFFD{"]), "malformed stream did not expose one deterministic malformed semantic chunk");
        }

        var interruptedProgress = new InlineProgress();
        using (var interrupted = new FaultInjectingModelProviderClient(new RecordingProviderClient(), Profile(ArenaFaultKind.Interruption)))
        {
            var result = interrupted.CompleteChatStreamingAsync(Config(), Messages(), interruptedProgress).GetAwaiter().GetResult();
            Require(!result.Ok && interruptedProgress.Values.SequenceEqual(["[injected-partial]"]), "interruption did not retain deterministic partial-stream semantics");
        }
    }

    internal static void DistinguishesCallerCancellationFromInjectedTimeout()
    {
        var inner = new RecordingProviderClient();
        var profile = Profile(ArenaFaultKind.Timeout, durationMilliseconds: 500);
        using var decorator = new FaultInjectingModelProviderClient(
            inner,
            profile,
            new ArenaFaultInjectionOptions(MaximumInjectedDelayMilliseconds: 500));
        using var cancellation = new CancellationTokenSource(20);
        try
        {
            _ = decorator.CompleteChatAsync(Config(), Messages(), cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("caller cancellation was swallowed as an injected timeout");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var observation = decorator.SnapshotObservations().Single();
        Require(observation.InjectedCause == ArenaFaultKind.Timeout, "scheduled injected cause was lost");
        Require(observation.ObservedOutcome == ArenaFaultObservedOutcome.CallerCancelled, "caller cancellation was mislabeled as injected timeout");
        Require(observation.RecoveryEvidence.State == ArenaEvidenceState.Unavailable, "caller cancellation invented recovery evidence");
        Require(inner.ChatCalls == 0, "cancelled injected timeout reached the provider client");
    }

    internal static void SchedulesSeededOccurrencesDeterministicallyAndPrivately()
    {
        var first = RunSchedule("seed-alpha");
        var second = RunSchedule("seed-alpha");
        Require(first.SequenceEqual(second), "same seed and call order produced different fault schedules");
        Require(first.Count(item => item.InjectionId == "fault:disconnect") <= 3, "disconnect exceeded max occurrences");
        Require(first.Count(item => item.InjectionId == "fault:timeout") <= 2, "timeout exceeded max occurrences");

        var changed = false;
        for (var index = 0; index < 32 && !changed; index++)
        {
            changed = !first.SequenceEqual(RunSchedule($"seed-{index}"));
        }
        Require(changed, "seed never influenced the deterministic occurrence schedule");

        var inner = new RecordingProviderClient();
        using var privacy = new FaultInjectingModelProviderClient(inner, Profile(ArenaFaultKind.EmptyResponse));
        _ = privacy.CompleteChatAsync(
            Config(),
            [new ModelChatMessage("user", "TOP SECRET RAW BODY")]).GetAwaiter().GetResult();
        var json = JsonSerializer.Serialize(privacy.SnapshotObservations());
        Require(!json.Contains("TOP SECRET RAW BODY", StringComparison.Ordinal)
            && !json.Contains("127.0.0.1", StringComparison.Ordinal)
            && !json.Contains("secret-token", StringComparison.Ordinal), "fault observation retained provider-private input");
    }

    internal static void BoundsConcurrentProviderWork()
    {
        var inner = new RecordingProviderClient(delayMilliseconds: 25);
        var ignoredProfile = new ArenaFaultProfileContract(
            ArenaContractSchemas.FaultProfile,
            "fault-profile:ui-only",
            At,
            "Ignored UI fault",
            "seed-ui",
            [new("fault:ui", ArenaFaultTarget.UserInterface, ArenaFaultKind.Saturation, 0, 0, 100, 1, "UI runner handles it.")],
            []);
        using var decorator = new FaultInjectingModelProviderClient(
            inner,
            ignoredProfile,
            new ArenaFaultInjectionOptions(MaximumConcurrentRequests: 2));
        var tasks = Enumerable.Range(0, 12)
            .Select(_ => decorator.CompleteChatAsync(Config(), Messages()))
            .ToArray();
        Task.WhenAll(tasks).GetAwaiter().GetResult();

        Require(tasks.All(item => item.Result.Ok), "bounded normal provider calls failed");
        Require(inner.MaximumConcurrency == 2, $"inner provider concurrency was {inner.MaximumConcurrency}, expected two");
        Require(decorator.MaximumObservedConcurrency == 2, "decorator concurrency evidence changed");
        Require(decorator.SnapshotObservations().IsEmpty, "non-provider fault created a provider observation");

        var liveInner = new RecordingProviderClient(delayMilliseconds: 40);
        var liveDecorator = FaultInjectingModelProviderClient.Arm(liveInner, ignoredProfile);
        var liveCall = liveDecorator.CompleteChatAsync(Config(), Messages());
        Require(SpinWait.SpinUntil(() => liveInner.ChatCalls == 1, 1_000), "in-flight disarm fixture never entered the provider");
        liveDecorator.Disarm();
        Require(liveCall.GetAwaiter().GetResult().Ok, "disarm corrupted an in-flight provider operation");
        RequireThrows<ObjectDisposedException>(
            () => liveDecorator.CompleteChatAsync(Config(), Messages()).GetAwaiter().GetResult(),
            "disarm accepted a new provider operation");
    }

    internal static void ProposesOnlyFromComparableObservedEvidence()
    {
        var snapshot = SessionStore.CreateDefaultSnapshot();
        snapshot.Configs[ModelProviderRouting.SharedConfigKey] = new ModelProviderConfig
        {
            BaseUrl = "http://127.0.0.1:65535/v1",
            ApiToken = "secret-token-value",
            Model = "model-current"
        };
        var before = JsonSerializer.Serialize(snapshot.Configs);
        var request = RoutingRequest(currentScores: [0.4m, 0.5m], proposedScores: [0.8m, 0.9m]);

        var proposal = ArenaModelRoutingOptimizer.Propose(request);

        Require(proposal.Schema == ArenaContractSchemas.RouteProposal, "optimizer returned a non-proposal contract");
        Require(proposal.Status == ArenaRouteProposalStatus.Proposed, "comparable observed evidence did not produce a proposal");
        var change = proposal.Changes.Single();
        Require(change.RequiresExplicitApproval, "proposal bypassed explicit approval");
        Require(change.SampleCount == 2 && change.EvidenceRunIds.Length == 4, "multi-sample provenance changed");
        Require(change.ScoreComponents.All(item => item.Evidence.State == ArenaEvidenceState.Observed), "score components lost observed evidence");
        Require(change.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Hardware && item.Evidence.State == ArenaEvidenceState.Observed), "hardware evidence missing");
        Require(change.Constraints.Any(item => item.Kind == ArenaRouteConstraintKind.Capability && item.Evidence.State == ArenaEvidenceState.Observed), "capability evidence missing");
        Require(ArenaContractCodec.Validate(proposal).IsValid, "optimizer emitted an invalid v1 proposal");
        Require(JsonSerializer.Serialize(snapshot.Configs) == before, "optimizer mutated provider routing");
        Require(ModelProviderRouting.Resolve(snapshot, "alpha", out _)?.Model == "model-current", "optimizer applied its proposal");
    }

    internal static void RefusesOptimizationWithInsufficientOrMismatchedEvidence()
    {
        var oneSample = RoutingRequest(currentScores: [0.4m], proposedScores: [0.8m]);
        var insufficient = ArenaModelRoutingOptimizer.Propose(oneSample);
        Require(insufficient.Status == ArenaRouteProposalStatus.InsufficientEvidence, "single samples claimed an optimization");
        Require(insufficient.Changes.Single().EvidenceSufficiency != ArenaEvidenceSufficiency.Sufficient, "single samples were marked sufficient");
        Require(insufficient.Changes.Single().Reason.StartsWith("No optimization is claimed", StringComparison.Ordinal), "insufficient route used optimization language");

        var mismatched = RoutingRequest(currentScores: [0.4m, 0.5m], proposedScores: [0.8m, 0.9m], candidateSetup: new('b', 64));
        var mismatchProposal = ArenaModelRoutingOptimizer.Propose(mismatched);
        var mismatch = mismatchProposal.Changes.Single();
        Require(mismatchProposal.Status == ArenaRouteProposalStatus.InsufficientEvidence, "mixed setup fingerprints produced a proposal");
        Require(mismatch.SampleCount == 0 && mismatch.EvidenceRunIds.IsEmpty, "mismatched runs were treated as comparable provenance");
        Require(mismatch.ScoreComponents.All(item => item.Evidence.State == ArenaEvidenceState.Unavailable
            && item.CurrentScore is null
            && item.ProposedScore is null), "mismatched setup retained numeric claims");

        var inferred = RoutingRequest(currentScores: [0.4m, 0.5m], proposedScores: [0.8m, 0.9m], inferredCandidate: true);
        var inferredProposal = ArenaModelRoutingOptimizer.Propose(inferred);
        Require(inferredProposal.Status == ArenaRouteProposalStatus.InsufficientEvidence, "inferred metrics claimed an optimization");
        Require(inferredProposal.Changes.Single().EvidenceSufficiency == ArenaEvidenceSufficiency.Partial, "inferred metrics were not explicitly partial");
    }

    internal static void RejectsPrivateProposalInputsAndUnavailableClaims()
    {
        var unavailable = RoutingRequest(currentScores: [0.4m, 0.5m], proposedScores: [0.8m, 0.9m]);
        var target = unavailable.Targets.Single();
        var candidate = target.Candidates.Single(item => item.ModelId == "model-candidate");
        var unsafeCandidate = candidate with
        {
            Constraints = candidate.Constraints.SetItem(0, candidate.Constraints[0] with { Description = "api_key=supersecret" })
        };
        var unsafeRequest = unavailable with
        {
            Targets = [target with { Candidates = target.Candidates.Replace(candidate, unsafeCandidate) }]
        };
        RequireThrows<InvalidDataException>(() => ArenaModelRoutingOptimizer.Propose(unsafeRequest), "private constraint content entered a proposal");

        var valid = ArenaModelRoutingOptimizer.Propose(unavailable);
        var score = valid.Changes.Single().ScoreComponents.Single();
        var unavailableClaim = valid with
        {
            Status = ArenaRouteProposalStatus.InsufficientEvidence,
            Changes = [valid.Changes.Single() with
            {
                EvidenceSufficiency = ArenaEvidenceSufficiency.Insufficient,
                ScoreComponents = [score with
                {
                    Evidence = Unavailable("ev:score-unavailable"),
                    CurrentScore = 0.2m,
                    ProposedScore = 0.9m
                }]
            }]
        };
        Require(ArenaContractCodec.Validate(unavailableClaim).Issues.Any(item => item.Code == "route.unavailable_score"), "unavailable route evidence carried numeric claims");
    }

    private static ImmutableArray<(string InjectionId, long Sequence, int Occurrence)> RunSchedule(string seed)
    {
        var profile = new ArenaFaultProfileContract(
            ArenaContractSchemas.FaultProfile,
            "fault-profile:schedule",
            At,
            "Seed schedule",
            seed,
            [
                new("fault:disconnect", ArenaFaultTarget.Network, ArenaFaultKind.Disconnect, 0, 0, 45, 3, "Disconnect safely."),
                new("fault:timeout", ArenaFaultTarget.Provider, ArenaFaultKind.Timeout, 0, 0, 35, 2, "Timeout safely.")
            ],
            []);
        using var decorator = new FaultInjectingModelProviderClient(new RecordingProviderClient(), profile);
        for (var index = 0; index < 20; index++)
        {
            _ = decorator.CompleteChatAsync(Config(), Messages()).GetAwaiter().GetResult();
        }
        return [.. decorator.SnapshotObservations().Select(item => (item.InjectionId, item.Sequence, item.Occurrence))];
    }

    private static ArenaFaultProfileContract Profile(ArenaFaultKind kind, int durationMilliseconds = 1) =>
        new(
            ArenaContractSchemas.FaultProfile,
            $"fault-profile:{FaultLabel(kind)}",
            At,
            $"{FaultLabel(kind)} profile",
            "seed-fixed",
            [new($"fault:{FaultLabel(kind)}", ArenaFaultTarget.Provider, kind, 0, durationMilliseconds, 100, 1, "Fail within the test boundary.")],
            []);

    private static ArenaRouteOptimizationRequest RoutingRequest(
        decimal[] currentScores,
        decimal[] proposedScores,
        string? candidateSetup = null,
        bool inferredCandidate = false)
    {
        var objectives = ImmutableArray.Create(new ArenaRouteObjective("quality", 1m));
        var current = Candidate("model-current", "current", currentScores, Setup, inferred: false);
        var candidate = Candidate("model-candidate", "candidate", proposedScores, candidateSetup ?? Setup, inferredCandidate);
        return new ArenaRouteOptimizationRequest(
            "route-proposal:test",
            At,
            "experiment:test",
            Setup,
            [new("route-target:alpha", "agent:alpha", "model-current", objectives, [current, candidate])]);
    }

    private static ArenaRouteCandidateEvidence Candidate(
        string modelId,
        string runPrefix,
        decimal[] scores,
        string setup,
        bool inferred)
    {
        var samples = scores.Select((score, index) => new ArenaRouteSampleEvidence(
            $"run:{runPrefix}:{index + 1}",
            setup,
            [new ArenaRouteMetricEvidence(
                "quality",
                score,
                inferred
                    ? new ArenaEvidenceAssertion($"ev:{runPrefix}:{index + 1}", ArenaEvidenceState.Inferred, "Inferred score.", Basis: "Deterministic fixture inference.")
                    : Observed($"ev:{runPrefix}:{index + 1}"))]))
            .ToImmutableArray();
        return new ArenaRouteCandidateEvidence(
            modelId,
            samples,
            [
                new("constraint:hardware", ArenaRouteConstraintKind.Hardware, "Fits observed memory budget.", true, Observed($"ev:{runPrefix}:hardware")),
                new("constraint:capability", ArenaRouteConstraintKind.Capability, "Supports required chat capability.", true, Observed($"ev:{runPrefix}:capability"))
            ]);
    }

    private static ModelProviderConfig Config() => new()
    {
        BaseUrl = "http://127.0.0.1:65535/v1",
        ApiToken = "secret-token-value",
        Model = "model-current"
    };

    private static IReadOnlyList<ModelChatMessage> Messages() => [new("user", "bounded fixture")];

    private static ArenaEvidenceAssertion Observed(string id) =>
        new(id, ArenaEvidenceState.Observed, "Observed fixture evidence.", ReferenceId: id);

    private static ArenaEvidenceAssertion Unavailable(string id) =>
        new(id, ArenaEvidenceState.Unavailable, "Evidence unavailable.", Limitation: "Fixture limitation.");

    private static string FaultLabel(ArenaFaultKind kind) => kind switch
    {
        ArenaFaultKind.Timeout => "timeout",
        ArenaFaultKind.Disconnect => "disconnect",
        ArenaFaultKind.MalformedStream => "malformed-stream",
        ArenaFaultKind.Saturation => "saturation",
        ArenaFaultKind.EmptyResponse => "empty-response",
        ArenaFaultKind.Interruption => "interruption",
        ArenaFaultKind.ContextPressure => "context-pressure",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress : IProgress<string>
    {
        internal List<string> Values { get; } = [];

        public void Report(string value) => Values.Add(value);
    }

    private sealed class RecordingProviderClient(int delayMilliseconds = 0) : IModelProviderClient, IStreamingModelProviderClient
    {
        private int _active;
        private int _chatCalls;
        private int _maximum;
        private int _streamCalls;

        internal int ChatCalls => _chatCalls;
        internal int StreamCalls => _streamCalls;
        internal int MaximumConcurrency => _maximum;

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", At));

        public async Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _chatCalls);
            return await CompleteAsync(config, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ModelCompletionResult> CompleteChatStreamingAsync(
            ModelProviderConfig config,
            IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _streamCalls);
            progress?.Report("complete");
            return await CompleteAsync(config, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ModelCompletionResult> CompleteAsync(ModelProviderConfig config, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximum);
                if (active <= maximum || Interlocked.CompareExchange(ref _maximum, active, maximum) == maximum) break;
            }
            try
            {
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
                return new ModelCompletionResult(true, config.BaseUrl, config.Model, "complete", "", 1, 1, 1, 2, "", At);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
