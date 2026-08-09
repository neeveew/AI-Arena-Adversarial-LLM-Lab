using System.Collections.Immutable;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;

namespace AIArena.VerificationLab;

internal static class ExperimentExecutionVerification
{
    private const string ProfileId = "provider:verification-live";
    private const string PrivateCredential = "verification-registry-credential-91c28f";

    public static async Task RunAsync(
        ScriptedProviderHost provider,
        ModelProviderClient providerClient,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ai-arena-live-experiment-verification",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sessionStore = new SessionStore(Path.Combine(root, "data"));
            var source = SessionStore.CreateDefaultSnapshot();
            foreach (var agent in source.Engine.Agents)
            {
                agent.Active = agent.Id == "alpha";
                agent.Status = agent.Active ? "waiting" : "muted";
            }
            source.Configs.Clear();
            source.Configs[ModelProviderRouting.SharedConfigKey] = TokenFreeSourceConfig(provider);
            source.Configs["alpha"] = TokenFreeSourceConfig(provider, ScriptedProviderHost.SecondaryModel);
            await sessionStore.SaveSnapshotAsync(source, "verification-source", cancellationToken);

            var profiles = new ArenaExperimentProviderProfileRegistry(
                new Dictionary<string, ModelProviderConfig>(StringComparer.Ordinal)
                {
                    [ProfileId] = LiveConfig(provider)
                });
            var packStore = new ArenaExperimentPackStore(Path.Combine(root, "packs"));
            var rubricStore = new ArenaRubricStore(Path.Combine(root, "evaluation"));
            var rubricWrite = await rubricStore.SaveRubricAsync(VerificationRubric(), cancellationToken);
            Require(rubricWrite.Succeeded, Format(rubricWrite.Diagnostics));
            var resolver = new ArenaExperimentExecutionResolver(packStore, sessionStore, profiles, rubricStore: rubricStore);
            var sourceResolution = await resolver.ResolveSessionSourceAsync(
                "session:verification-source",
                cancellationToken);
            Require(sourceResolution.IsAvailable && sourceResolution.Source is not null,
                Format(sourceResolution.Diagnostics));

            var scenarioPack = ScenarioPack(sourceResolution.Source!);
            var packWrite = await packStore.SaveScenarioPackAsync(scenarioPack, cancellationToken);
            Require(packWrite.Succeeded, Format(packWrite.Diagnostics));

            var successExperiment = Experiment("experiment:verification-live", repetitions: 2, turnBudget: 2);
            var successResolution = await resolver.ResolveAsync(successExperiment, "scenario:verification-live", cancellationToken);
            Require(successResolution.IsAvailable && successResolution.Plan is not null,
                Format(successResolution.Diagnostics));
            var successRunRoot = Path.Combine(root, "success-evidence");
            var successStore = new ExperimentRunStore(successRunRoot);
            var callsBeforeSuccess = provider.Captures.Count;
            var successRunner = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                successResolution.Plan!,
                profiles,
                sessionStore,
                providerClient));
            var success = await successRunner.RunAsync(
                successExperiment,
                successResolution.Plan!.Expansion,
                successStore,
                new ArenaExperimentRunnerOptions(MaximumParallelism: 1),
                cancellationToken);
            Require(success.StartedCells == 2 && success.Runs.Length == 2,
                "Repeat-trial expansion did not execute both real loopback cells.");
            Require(success.Runs.All(item => item.State == ArenaExperimentRunState.Completed),
                "A real loopback experiment cell did not complete.");
            Require(provider.Captures.Count - callsBeforeSuccess == 4,
                "Configured turn budgets did not produce the expected real loopback calls.");
            Require(success.Runs.All(item => item.TrialIds.Length == 1),
                "Repeat cells did not retain one durable trial identity each.");
            Require(success.Runs.All(item => item.Evidence.Any(evidence =>
                    evidence.Summary.Contains("Adapter-exposed aggregate telemetry", StringComparison.Ordinal))),
                "Real loopback telemetry was not retained with conservative provenance wording.");

            var successChildren = ChildSessionIds(success.Runs);
            Require(successChildren.Length == 2 && successChildren.Distinct(StringComparer.Ordinal).Count() == 2,
                "Repeat cells did not retain distinct child-session identities.");
            foreach (var childSessionId in successChildren)
            {
                var child = await sessionStore.LoadSnapshotAsync(childSessionId, cancellationToken);
                Require(child is not null, "A referenced experiment child session is unreadable.");
                Require(child!.Engine.Messages.Count == 2,
                    "Real loopback turns were not retained only in the isolated child.");
                Require(child.Engine.Messages.All(message => message.Text == ScriptedProviderHost.CompletionText),
                    "The scripted loopback provider did not drive the real turn runtime.");
                Require(child.Configs.Keys.SequenceEqual([ModelProviderRouting.SharedConfigKey]),
                    "A role-specific provider override survived in the experiment child.");
                Require(child.Configs[ModelProviderRouting.SharedConfigKey].ApiToken.Length == 0,
                    "The process-memory credential was persisted into the child snapshot.");
                Require(child.BranchReceipt?.ExperimentId == successExperiment.Id,
                    "The child branch receipt lost experiment identity.");
            }

            var sourceAfterSuccess = await sessionStore.LoadSnapshotAsync("verification-source", cancellationToken);
            Require(sourceAfterSuccess is not null
                    && sourceAfterSuccess.Engine.Messages.Count == 0
                    && sourceAfterSuccess.Configs.ContainsKey("alpha"),
                "Experiment turns or route mutation leaked back into the source session.");

            var capturesBeforeResume = provider.Captures.Count;
            var sessionsBeforeResume = await sessionStore.ListSessionsAsync(SessionListingDetail.Identity, cancellationToken);
            var resumed = await successRunner.RunAsync(
                successExperiment,
                successResolution.Plan.Expansion,
                successStore,
                new ArenaExperimentRunnerOptions(MaximumParallelism: 1),
                cancellationToken);
            Require(resumed.EligibleCells == 0 && resumed.StartedCells == 0,
                "Completed real loopback cells reran without explicit approval.");
            Require(provider.Captures.Count == capturesBeforeResume,
                "Resume duplicated a completed loopback provider trial.");
            Require((await sessionStore.ListSessionsAsync(SessionListingDetail.Identity, cancellationToken)).Count
                    == sessionsBeforeResume.Count,
                "Resume created duplicate child sessions.");

            var invalidExperiment = Experiment("experiment:verification-invalid", 1, 1) with
            {
                Dimensions = [new("dimension:invalid", "top_p", ["0.9"])]
            };
            var callsBeforeInvalid = provider.Captures.Count;
            var invalidResolution = await resolver.ResolveAsync(invalidExperiment, cancellationToken: cancellationToken);
            Require(!invalidResolution.IsAvailable
                    && invalidResolution.Diagnostics.Any(item => item.Code == "experiment_dimension_unsupported"),
                "Unknown behavior dimension did not block a live execution plan.");
            Require(provider.Captures.Count == callsBeforeInvalid,
                "Invalid behavior dimension reached the loopback provider.");

            provider.QueueFault(ScriptedProviderFault.HttpError);
            var failureExperiment = Experiment("experiment:verification-failure", 1, 1);
            var failureResolution = await resolver.ResolveAsync(failureExperiment, cancellationToken: cancellationToken);
            Require(failureResolution.IsAvailable && failureResolution.Plan is not null,
                Format(failureResolution.Diagnostics));
            var failureRoot = Path.Combine(root, "failure-evidence");
            var failure = await new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                    failureResolution.Plan!, profiles, sessionStore, providerClient))
                .RunAsync(
                    failureExperiment,
                    failureResolution.Plan!.Expansion,
                    new ExperimentRunStore(failureRoot),
                    new ArenaExperimentRunnerOptions(MaximumParallelism: 1),
                    cancellationToken);
            var failedRun = failure.Runs.Single();
            Require(failedRun.State == ArenaExperimentRunState.Failed,
                "Scripted provider HTTP failure was not classified as a failed cell.");
            Require(failedRun.Evidence.Any(item =>
                    item.Summary.Contains("provider rejected classification", StringComparison.Ordinal)),
                "Provider failure was not reduced to a bounded content-free classification.");

            provider.QueueFault(ScriptedProviderFault.Timeout);
            var cancellationExperiment = Experiment("experiment:verification-cancel", 1, 1) with
            {
                Dimensions =
                [
                    new("dimension:context", "context_length", ["4096"]),
                    new("dimension:max-output", "max_output_tokens", ["64"]),
                    new("dimension:reasoning", "reasoning", ["low"]),
                    new("dimension:temperature", "temperature", ["0.25"]),
                    new("dimension:timeout", "timeout_seconds", ["10"])
                ]
            };
            var cancellationResolution = await resolver.ResolveAsync(cancellationExperiment, cancellationToken: cancellationToken);
            Require(cancellationResolution.IsAvailable && cancellationResolution.Plan is not null,
                Format(cancellationResolution.Diagnostics));
            var cancellationRoot = Path.Combine(root, "cancellation-evidence");
            using (var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var captureCountBeforeCancellation = provider.Captures.Count;
                var cancellationRun = new ExperimentRunnerService(new ArenaExperimentCellExecutor(
                        cancellationResolution.Plan!, profiles, sessionStore, providerClient))
                    .RunAsync(
                        cancellationExperiment,
                        cancellationResolution.Plan!.Expansion,
                        new ExperimentRunStore(cancellationRoot),
                        new ArenaExperimentRunnerOptions(MaximumParallelism: 1),
                        callerCancellation.Token);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    while (provider.Captures.Count <= captureCountBeforeCancellation)
                    {
                        await Task.Delay(10, handshakeTimeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Cancellation verification never observed the deterministic post-fork provider handshake.");
                }

                callerCancellation.Cancel();
                var cancelled = await cancellationRun;
                var cancelledRun = cancelled.Runs.Single();
                Require(cancelled.WasCancelled && cancelledRun.State == ArenaExperimentRunState.Cancelled,
                    "Caller cancellation did not propagate into durable experiment state.");
                Require(cancelledRun.Evidence.Any(item =>
                        item.ReferenceId?.StartsWith("session:experiment-trial-", StringComparison.Ordinal) == true),
                    "Cancellation after forking lost the owned child-session identity.");
            }

            var evidenceText = string.Join(
                "\n",
                new[] { successRunRoot, failureRoot, cancellationRoot }
                    .SelectMany(directory => Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                    .Select(File.ReadAllText));
            Require(!evidenceText.Contains(PrivateCredential, StringComparison.Ordinal),
                "Experiment evidence persisted a provider credential.");
            Require(!evidenceText.Contains(ScriptedProviderHost.CompletionText, StringComparison.Ordinal)
                    && !evidenceText.Contains(ScriptedProviderHost.ReasoningText, StringComparison.Ordinal),
                "Experiment evidence persisted response or reasoning content.");
            Require(!evidenceText.Contains("scripted HTTP failure", StringComparison.OrdinalIgnoreCase),
                "Experiment evidence persisted a raw provider error.");
            Require(!evidenceText.Contains(root, StringComparison.OrdinalIgnoreCase),
                "Experiment evidence persisted an absolute data path.");
            foreach (var run in success.Runs.Append(failedRun))
            {
                Require(ArenaContractCodec.Validate(run).IsValid,
                    "A real experiment run no longer satisfies the frozen run contract.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ArenaScenarioPackContract ScenarioPack(ArenaExperimentSessionSource source)
    {
        var now = DateTimeOffset.UtcNow;
        ImmutableArray<ArenaScenarioInvariant> invariants =
            [new("invariant:verification-live", "rule:verification-live", "The bounded loopback trial reaches a terminal state.", true)];
        ImmutableArray<ArenaScenarioDefinition> scenarios =
        [
            new(
                "scenario:verification-live",
                "1.0.0",
                "Verification Lab live experiment",
                source.MatchSetupReference,
                source.SetupFingerprint,
                "A deterministic loopback verification scenario.",
                2,
                ["local", "verification"],
                ["invariant:verification-live"],
                ["evidence:verification-live-pack"])
        ];
        return new(
            ArenaContractSchemas.ScenarioPack,
            "scenario-pack:verification-live",
            now,
            "Verification Lab live scenarios",
            "1.0.0",
            ArenaExperimentFingerprints.ScenarioPackContent(invariants, scenarios),
            null,
            invariants,
            scenarios,
            [new(
                "evidence:verification-live-pack",
                ArenaEvidenceState.Observed,
                "Verification Lab recorded the local scenario identity.",
                "artifact:verification-live-pack")]);
    }

    private static ArenaExperimentContract Experiment(string id, int repetitions, int turnBudget) => new(
        ArenaContractSchemas.Experiment,
        id,
        DateTimeOffset.UtcNow,
        "Verification Lab live matrix",
        ArenaExperimentStatus.Draft,
        "scenario-pack:verification-live",
        null,
        [ProfileId],
        ["rubric:verification-quality"],
        [],
        [
            new("dimension:context", "context_length", ["4096"]),
            new("dimension:max-output", "max_output_tokens", ["64"]),
            new("dimension:reasoning", "reasoning", ["low"]),
            new("dimension:temperature", "temperature", ["0.25"]),
            new("dimension:timeout", "timeout_seconds", ["5"])
        ],
        repetitions,
        turnBudget,
        1,
        [],
        [new(
            "evidence:verification-live-experiment",
            ArenaEvidenceState.Observed,
            "Verification Lab recorded the local experiment identity.",
            "artifact:verification-live-experiment")]);

    private static ArenaRubricContract VerificationRubric() => new(
        ArenaContractSchemas.Rubric,
        "rubric:verification-quality",
        DateTimeOffset.UtcNow,
        "Verification quality",
        "1.0.0",
        [new("evaluator:human", ArenaRubricEvaluatorKind.Human, null)],
        [new("criterion:completion", "Completion", "Checks bounded loopback completion.", 1m, 0m, 5m)],
        [new(
            "evidence:verification-rubric",
            ArenaEvidenceState.Observed,
            "Verification Lab recorded the immutable rubric identity.",
            "artifact:verification-rubric")]);

    private static ModelProviderConfig LiveConfig(ScriptedProviderHost provider) => new()
    {
        BaseUrl = provider.BaseUri.AbsoluteUri,
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = PrivateCredential,
        Model = ScriptedProviderHost.PrimaryModel,
        Timeout = 5,
        Temperature = 0.8,
        MaxOutputTokens = 128,
        ContextLength = 8192,
        Reasoning = "off",
        NativeStatefulChat = false
    };

    private static ModelProviderConfig TokenFreeSourceConfig(
        ScriptedProviderHost provider,
        string? model = null) => new()
    {
        BaseUrl = provider.BaseUri.AbsoluteUri,
        ApiMode = ModelProviderApiModes.LlamaCppNative,
        ApiToken = "",
        Model = model ?? ScriptedProviderHost.PrimaryModel,
        Timeout = 5,
        Temperature = 0.8,
        MaxOutputTokens = 128,
        ContextLength = 8192,
        Reasoning = "off",
        NativeStatefulChat = false
    };

    private static ImmutableArray<string> ChildSessionIds(IEnumerable<ArenaExperimentRunContract> runs) =>
        [.. runs
            .SelectMany(run => run.Evidence)
            .Select(item => item.ReferenceId)
            .Where(item => item?.StartsWith("session:experiment-trial-", StringComparison.Ordinal) == true)
            .Select(item => item!["session:".Length..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)];

    private static string Format(ImmutableArray<ArenaExperimentExecutionDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Summary}"));

    private static string Format(ImmutableArray<ArenaArtifactDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
