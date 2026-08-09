using System.Collections.Immutable;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

internal static class RouteApplicationTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2038, 2, 3, 4, 5, 6, TimeSpan.Zero);
    private static readonly DateTimeOffset AppliedAt = ApprovedAt.AddSeconds(1);

    internal static void AppliesOnlyAfterRecheckingPersistedSetupAndCurrentModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-route-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = Config("model-current", "private-token");
            store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
            var persisted = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
            var proposal = Proposal(SessionStore.SetupFingerprint(persisted), "alpha");
            var service = new ArenaRouteApplicationService(store, new FixedTimeProvider(AppliedAt));

            var receipt = service.ApplyApprovedAsync(
                "active",
                proposal,
                "operator:local",
                ApprovedAt).GetAwaiter().GetResult();

            var updated = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
            Require(updated.Configs["alpha"].Model == "model-candidate", "approved agent route was not persisted");
            Require(updated.Configs[ModelProviderRouting.SharedConfigKey].Model == "model-current", "agent route application mutated the shared route");
            Require(receipt.Changes.Single().AgentId == "alpha"
                    && receipt.Changes.Single().PreviousModelId == "model-current"
                    && receipt.Changes.Single().AppliedModelId == "model-candidate",
                "route receipt did not preserve the exact applied change");
            Require(ArenaContractCodec.Validate(receipt).IsValid, "route application emitted an invalid receipt");
            var receiptJson = ArenaContractCodec.Serialize(receipt);
            Require(!receiptJson.Contains("private-token", StringComparison.Ordinal)
                    && !receiptJson.Contains(root, StringComparison.OrdinalIgnoreCase),
                "route receipt retained credentials or a local path");

            var staleProposal = Proposal(new string('b', 64), "beta");
            RequireThrows<ArenaRouteApplicationConflictException>(
                () => service.ApplyApprovedAsync("active", staleProposal, "operator:local", ApprovedAt).GetAwaiter().GetResult(),
                "stale setup fingerprint was applied");
            var afterStale = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
            Require(!afterStale.Configs.ContainsKey("beta"), "stale proposal partially changed routing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void RejectsDuplicateAndNonRosterTargetsBeforeMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-arena-route-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(root);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs[ModelProviderRouting.SharedConfigKey] = Config("model-current", "private-token");
            store.SaveSnapshotAsync(snapshot, "active").GetAwaiter().GetResult();
            var persisted = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
            var fingerprint = SessionStore.SetupFingerprint(persisted);
            var service = new ArenaRouteApplicationService(store, new FixedTimeProvider(AppliedAt));

            RequireThrows<ArenaRouteApplicationConflictException>(
                () => service.ApplyApprovedAsync(
                    "active",
                    Proposal(fingerprint, "phantom"),
                    "operator:local",
                    ApprovedAt).GetAwaiter().GetResult(),
                "non-roster route target was applied");

            var duplicate = Proposal(fingerprint, "alpha", duplicateAgentTarget: true);
            Require(duplicate.Status == ArenaRouteProposalStatus.Proposed
                    && duplicate.Changes.Count(item => item.AgentId == "alpha") == 2,
                "duplicate-agent proposal fixture was not valid");
            RequireThrows<ArenaRouteApplicationConflictException>(
                () => service.ApplyApprovedAsync(
                    "active",
                    duplicate,
                    "operator:local",
                    ApprovedAt).GetAwaiter().GetResult(),
                "duplicate agent route targets were applied");

            var after = store.LoadSnapshotAsync("active").GetAwaiter().GetResult()!;
            Require(after.Configs.Keys.SequenceEqual([ModelProviderRouting.SharedConfigKey]), "rejected route proposal partially mutated configs");
            Require(after.PersistenceRevision == persisted.PersistenceRevision, "rejected route proposal wrote a new snapshot revision");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArenaRouteProposalContract Proposal(string setupFingerprint, string agentId, bool duplicateAgentTarget = false)
    {
        var targets = ImmutableArray.CreateBuilder<ArenaRouteOptimizationTarget>();
        targets.Add(Target("route-target:first", agentId, setupFingerprint, "model-candidate"));
        if (duplicateAgentTarget)
            targets.Add(Target("route-target:second", agentId, setupFingerprint, "model-candidate-two"));
        return ArenaModelRoutingOptimizer.Propose(new(
            $"route-proposal:{Guid.NewGuid():N}",
            ApprovedAt.AddMinutes(-1),
            "experiment:route-apply",
            setupFingerprint,
            targets.ToImmutable()));
    }

    private static ArenaRouteOptimizationTarget Target(
        string targetId,
        string agentId,
        string setupFingerprint,
        string candidateModel) => new(
            targetId,
            agentId,
            "model-current",
            [new("quality", 1m)],
            [
                Candidate("model-current", "current", setupFingerprint, [0.40m, 0.45m]),
                Candidate(candidateModel, candidateModel, setupFingerprint, [0.80m, 0.85m])
            ]);

    private static ArenaRouteCandidateEvidence Candidate(
        string model,
        string idPrefix,
        string setupFingerprint,
        decimal[] scores) => new(
            model,
            [.. scores.Select((score, index) => new ArenaRouteSampleEvidence(
                $"run:{idPrefix}:{index}",
                setupFingerprint,
                [new(
                    "quality",
                    score,
                    Observed($"evidence:{idPrefix}:score:{index}"))]))],
            [
                new(
                    $"constraint:{idPrefix}:hardware",
                    ArenaRouteConstraintKind.Hardware,
                    "Observed compatible local hardware.",
                    true,
                    Observed($"evidence:{idPrefix}:hardware")),
                new(
                    $"constraint:{idPrefix}:capability",
                    ArenaRouteConstraintKind.Capability,
                    "Observed compatible chat capability.",
                    true,
                    Observed($"evidence:{idPrefix}:capability"))
            ]);

    private static ArenaEvidenceAssertion Observed(string id) =>
        new(id, ArenaEvidenceState.Observed, "Observed route-application fixture evidence.", ReferenceId: id);

    private static ModelProviderConfig Config(string model, string token) => new()
    {
        BaseUrl = "http://127.0.0.1:65535/v1",
        ApiToken = token,
        Model = model
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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
