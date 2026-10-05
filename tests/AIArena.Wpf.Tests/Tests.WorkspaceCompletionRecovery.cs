using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void AgentRecoversNormalizedReasoningOnlyCompletion()
    {
        foreach (var streaming in new[] { false, true })
        WithWorkspaceRecoveryAgent(streaming, rescue: "", (agent, client, _) =>
        {
            client.Reply = (config, call) => call == 1 ? WorkspaceReasoningOnly(config) : WorkspaceAnswer(config);
            client.YieldBeforeReply = true;
            PumpDocumentImportTask(agent.DebugSendAsync());
            FlushWorkspaceRecoveryDispatcher();
            Require(client.Configs.Count == 2 && client.Configs[0].Reasoning == "high"
                    && client.Configs[1].Reasoning == "off" && client.Configs.All(config => config.Model == "same-model"),
                "Agent did not perform exactly one supported same-model recovery for a normalized reasoning-only result.");
            Require(client.Prompts[0].SequenceEqual(client.Prompts[1])
                    && agent.DebugLastMessageBody.Contains("Recovered public answer.", StringComparison.Ordinal)
                    && agent.DebugPhaseState("builder") == "Done",
                "Agent recovery changed the conversation or failed to finalize its public answer.");
        });
    }

    static void CollaborateRecoversReasoningWithoutChangingRoute()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var core = WorkspaceRoutingSnapshot();
            core.Configs["narrator"] = RecoveryConfig(core.Configs["narrator"]);
            var view = WorkspaceRoutingView(core);
            var client = new WorkspaceRecoveryClient { Reply = (config, call) => call == 1 ? WorkspaceReasoningOnly(config) : WorkspaceAnswer(config) };
            client.YieldBeforeReply = true;
            var coordinator = CreateCollaborateCoordinatorForTest(client, new TextBox { Text = "Explain these tradeoffs." },
                new TextBlock(), () => view, _ => { }, new RecordingCollaborateHistoryStore());
            coordinator.Initialize();
            PumpDocumentImportTask(coordinator.SendAsync());
            Require(client.Configs.Count == 2 && client.Configs.All(config => config.BaseUrl == core.Configs["narrator"].BaseUrl)
                    && client.Configs[1].Reasoning == "off" && client.Prompts[0].SequenceEqual(client.Prompts[1]),
                "Collaborate replaced its reasoning-only model with a fallback instead of one supported exact-conversation recovery.");
            Require(coordinator.CaptureControlReview("").LatestAnswer == "Recovered public answer.",
                "Collaborate did not preserve the recovered answer.");
        }));
    }

    static void WorkspaceRecoveryDoesNotGuessOrReplayTerminalFailures()
    {
        foreach (var kind in new[] { "unsupported", "off", "tool", "filtered", "context", "twice" })
        WithWorkspaceRecoveryAgent(false, "backup-model", (agent, client, core) =>
        {
            core.Configs["shared"] = kind switch
            {
                "unsupported" => ModelProviderRequests.Copy(core.Configs["shared"], runtimeEvidence: null, replaceRuntimeEvidence: true),
                "off" => ModelProviderRequests.Copy(core.Configs["shared"], reasoning: "off"),
                _ => core.Configs["shared"]
            };
            client.Reply = (config, _) => kind switch
            {
                "tool" => WorkspaceReasoningOnly(config) with { StopReason = ModelCompletionStopReason.ToolCall },
                "filtered" => WorkspaceReasoningOnly(config) with { StopReason = ModelCompletionStopReason.ContentFiltered },
                "context" => WorkspaceReasoningOnly(config) with { FailureKind = ModelCompletionFailureKind.ContextLimitExceeded },
                _ => WorkspaceReasoningOnly(config)
            };
            PumpDocumentImportTask(agent.DebugSendAsync());
            Require(client.Configs.Count == (kind == "twice" ? 2 : 1) && agent.DebugPhaseState("builder") == "Error",
                $"{kind}: recovery guessed support, replayed a terminal failure, or continued beyond one reduced-reasoning attempt.");
        });
    }

    static void WorkspaceFailedPartialAnswersRemainVisible()
    {
        const string partial = "Accepted partial answer.\n```powershell\nWrite-Output 'partial'\n```";
        foreach (var streaming in new[] { false, true })
        WithWorkspaceRecoveryAgent(streaming, "backup-model", (agent, client, _) =>
        {
            client.Reply = (config, call) => call == 1
                ? WorkspaceAnswer(config) with { Ok = false, Text = partial, Reasoning = "PRIVATE_REASONING_FIXTURE",
                    Error = "Interrupted fixture response.", FailureKind = ModelCompletionFailureKind.Transport, StopReason = ModelCompletionStopReason.ProviderError }
                : WorkspaceAnswer(config);
            PumpDocumentImportTask(agent.DebugSendAsync());
            client.LastProgress?.Report("Late output must not revive Writing state.");
            FlushWorkspaceRecoveryDispatcher();
            Require(client.Configs.Count == 1 && agent.DebugLastMessageBody.Contains(partial, StringComparison.Ordinal)
                    && agent.DebugLastMessageBody.Contains("Interrupted fixture response.", StringComparison.Ordinal)
                    && !agent.DebugLastMessageBody.Contains("PRIVATE_REASONING_FIXTURE", StringComparison.Ordinal)
                    && agent.DebugPhaseState("builder") == "Error" && !agent.DebugCommandRunEnabled,
                "Agent erased/replayed a failed public partial, exposed reasoning, revived late progress, or staged partial commands.");
        });

        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var view = WorkspaceRoutingView(WorkspaceRoutingSnapshot());
            var client = new WorkspaceRecoveryClient { Reply = (config, _) => WorkspaceAnswer(config) with
                { Ok = false, Text = partial, Error = "Interrupted fixture response.", FailureKind = ModelCompletionFailureKind.Transport } };
            var coordinator = CreateCollaborateCoordinatorForTest(client, new TextBox { Text = "Explain these tradeoffs." },
                new TextBlock(), () => view, _ => { }, new RecordingCollaborateHistoryStore());
            coordinator.Initialize();
            PumpDocumentImportTask(coordinator.SendAsync());
            var review = coordinator.CaptureControlReview("");
            Require(client.Configs.Count == 1 && review.LatestAnswer.Contains(partial, StringComparison.Ordinal)
                    && review.Trace.Count == 1 && !review.Trace[0].Ok && review.Trace[0].Text == partial,
                "Collaborate erased/replayed a failed public partial or lost its failure status in saved trace evidence.");
        }));
    }

    static void WorkspaceRecoveryUsesFreshCapabilitiesAndHonorsCancellation()
    {
        foreach (var capability in new[] { "unknown", "low", "cancel" })
        {
            var resolver = new WorkspaceRecoveryEvidence(capability);
            WithWorkspaceRecoveryAgent(true, "backup-model", (agent, client, core) =>
            {
                client.YieldBeforeReply = true;
                client.Reply = (config, call) => call == 1 ? WorkspaceReasoningOnly(config) : WorkspaceAnswer(config);
                if (capability == "cancel") resolver.BeforeReturn = agent.ControlStop;
                PumpDocumentImportTask(agent.DebugSendAsync());
                Require(resolver.Calls.Count == 1 && resolver.Calls[0].Model == "same-model"
                        && client.Configs.Count == (capability == "cancel" ? 0 : capability == "low" ? 2 : 1),
                    $"{capability}: fresh capability probing was bypassed or cancellation started a completion.");
                if (capability == "low")
                    Require(client.Configs[1].Reasoning == "low" && client.Configs[1].RuntimeEvidence?.ModelInstanceId == "fresh-instance",
                        "A mandatory-thinking model was incorrectly sent off instead of its fresh supported low mode.");
                Require(core.Configs["shared"].Reasoning == "high" && core.Configs["shared"].RuntimeEvidence?.ModelInstanceId == "fixture-instance",
                    "Request-only recovery or capability evidence overwrote the source configuration.");
            }, resolver);
        }

        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var core = WorkspaceRoutingSnapshot();
            core.Configs["narrator"] = RecoveryConfig(core.Configs["narrator"]);
            var resolver = new WorkspaceRecoveryEvidence("low");
            var client = new WorkspaceRecoveryClient { YieldBeforeReply = true,
                Reply = (config, call) => call == 1 ? WorkspaceReasoningOnly(config) : WorkspaceAnswer(config) };
            var coordinator = CreateCollaborateCoordinatorForTest(client, new TextBox { Text = "Explain these tradeoffs." },
                new TextBlock(), () => WorkspaceRoutingView(core), _ => { }, new RecordingCollaborateHistoryStore(),
                runtimeEvidenceResolver: resolver);
            coordinator.Initialize();
            PumpDocumentImportTask(coordinator.SendAsync());
            Require(resolver.Calls.Count == 1 && resolver.Calls[0].BaseUrl == core.Configs["narrator"].BaseUrl
                    && resolver.Calls[0].ApiToken == core.Configs["narrator"].ApiToken
                    && client.Configs.Count == 2 && client.Configs[1].Reasoning == "low",
                "Collaborate capability discovery used another route or ignored the supported reduction.");
        }));
    }

    static void WorkspaceRequestsRespectLoadedContextWithoutDroppingInput()
    {
        foreach (var context in new[] { 512, 4096 })
            WithWorkspaceRecoveryAgent(false, "backup-model", (agent, client, core) =>
            {
                core.Configs["shared"] = ModelProviderRequests.Copy(core.Configs["shared"], runtimeEvidence:
                    new ModelRuntimeEvidence(context, true, "small-instance", DateTimeOffset.UtcNow, "fixture"), replaceRuntimeEvidence: true);
                PumpDocumentImportTask(agent.DebugSendAsync());
                if (context == 512)
                    Require(client.Configs.Count == 0 && agent.DebugPhaseState("builder") == "Error"
                            && agent.DebugLastMessageBody.Contains("context", StringComparison.OrdinalIgnoreCase),
                        "An oversized Agent prompt reached the small-context model or escaped through fallback.");
                else
                    Require(client.Configs.Count == 1 && client.Configs[0].MaxOutputTokens < 6144
                            && ArenaHistoryBudgetService.EstimateTokens(client.Prompts[0]) + client.Configs[0].MaxOutputTokens <= context
                            && client.Prompts[0].Any(message => message.Content.Contains("Explain these tradeoffs.", StringComparison.Ordinal)),
                        "Agent output reserve exceeded the loaded context or its original input was removed to fit.");
            });

        foreach (var oversized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var core = WorkspaceRoutingSnapshot();
                core.Configs["narrator"] = ModelProviderRequests.Copy(core.Configs["narrator"], runtimeEvidence:
                    new ModelRuntimeEvidence(512, false, "small-narrator", DateTimeOffset.UtcNow, "fixture"), replaceRuntimeEvidence: true);
                var prompt = oversized ? string.Concat(Enumerable.Repeat("Unshortened input. ", 300)) : "Explain these tradeoffs.";
                var client = new WorkspaceRecoveryClient();
                var coordinator = CreateCollaborateCoordinatorForTest(client, new TextBox { Text = prompt }, new TextBlock(),
                    () => WorkspaceRoutingView(core), _ => { }, new RecordingCollaborateHistoryStore());
                coordinator.Initialize();
                PumpDocumentImportTask(coordinator.SendAsync());
                Require(client.Configs.Count == (oversized ? 0 : 1),
                    "Collaborate sent an oversized prompt or rejected a prompt with adequate output room.");
                if (!oversized)
                    Require(client.Configs[0].MaxOutputTokens < 1200
                            && ArenaHistoryBudgetService.EstimateTokens(client.Prompts[0]) + client.Configs[0].MaxOutputTokens <= 512
                            && client.Prompts[0].Any(message => message.Content.Contains(prompt, StringComparison.Ordinal)),
                        "Collaborate exceeded loaded context or rewrote its latest input while fitting output.");
            }));
    }

    private static void WithWorkspaceRecoveryAgent(bool streaming, string rescue,
        Action<AgentWorkspaceCoordinator, WorkspaceRecoveryClient, ArenaSnapshot> test,
        IModelRuntimeEvidenceResolver? runtimeEvidenceResolver = null)
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ai-arena-recovery-agent-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                var core = WorkspaceRoutingSnapshot();
                core.Configs["shared"] = RecoveryConfig(core.Configs["shared"]);
                var client = new WorkspaceRecoveryClient();
                var settings = new WpfSettings { AgentWorkspacePath = root, AgentBuilderOnlyDefault = true,
                    StreamModelResponses = streaming, AgentRescueModel = rescue };
                using var agent = CreateWorkspaceProfileTestCoordinator(settings, new WpfSettingsStore(Path.Combine(root, "settings.json")),
                    (_, _) => Task.FromResult("An isolated test workspace."), modelClient: client,
                    snapshot: () => WorkspaceRoutingView(core), promptText: new TextBox { Text = "Explain these tradeoffs." },
                    runtimeEvidenceResolver: runtimeEvidenceResolver);
                agent.Initialize();
                test(agent, client, core);
                FlushWorkspaceRecoveryDispatcher();
            }
            finally
            {
                Require(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                    "Temporary recovery test escaped its expected parent.");
                Directory.Delete(root, recursive: true);
            }
        }));
    }

    private static ModelProviderConfig RecoveryConfig(ModelProviderConfig config) =>
        ModelProviderRequests.Copy(config, reasoning: "high", runtimeEvidence:
            new ModelRuntimeEvidence(8192, true, "fixture-instance", DateTimeOffset.UtcNow, "fixture"), replaceRuntimeEvidence: true);

    private static ModelCompletionResult WorkspaceReasoningOnly(ModelProviderConfig config) =>
        ModelCompletionOutcomeClassifier.Normalize(new ModelCompletionResult(true, config.BaseUrl, config.Model, "", "Private reasoning.",
            12, 20, 30, 50, "", DateTimeOffset.UtcNow));

    private static ModelCompletionResult WorkspaceAnswer(ModelProviderConfig config) =>
        new(true, config.BaseUrl, config.Model, "Recovered public answer.", "", 10, 20, 10, 30, "", DateTimeOffset.UtcNow);

    private static void FlushWorkspaceRecoveryDispatcher() => PumpDocumentImportTask(
        Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task);

    private sealed class WorkspaceRecoveryClient : IModelProviderClient, IStreamingModelProviderClient
    {
        internal Func<ModelProviderConfig, int, ModelCompletionResult> Reply { get; set; } = (config, _) => WorkspaceAnswer(config);
        internal List<ModelProviderConfig> Configs { get; } = [];
        internal List<IReadOnlyList<ModelChatMessage>> Prompts { get; } = [];
        internal IProgress<string>? LastProgress { get; private set; }
        internal bool YieldBeforeReply { get; set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public async Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Configs.Add(config);
            Prompts.Add(messages.ToArray());
            if (YieldBeforeReply) await Task.Yield();
            return Reply(config, Configs.Count);
        }
        public async Task<ModelCompletionResult> CompleteChatStreamingAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            IProgress<string>? progress, CancellationToken cancellationToken = default)
        {
            LastProgress = progress;
            var result = await CompleteChatAsync(config, messages, cancellationToken);
            if (result.Text.Length > 0) progress?.Report(result.Text);
            return result;
        }
    }

    private sealed class WorkspaceRecoveryEvidence(string capability) : IModelRuntimeEvidenceResolver
    {
        internal List<ModelProviderConfig> Calls { get; } = [];
        internal Action? BeforeReturn { get; set; }
        public async Task<ModelRuntimeEvidence?> ResolveAsync(ModelProviderConfig config, CancellationToken cancellationToken = default)
        {
            Calls.Add(config);
            await Task.Yield();
            BeforeReturn?.Invoke();
            return capability == "unknown" ? null : new ModelRuntimeEvidence(8192, false, "fresh-instance", DateTimeOffset.UtcNow, "fixture", "low");
        }
    }
}
