using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Core.Providers;
using AIArena.Wpf;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    private const string RetainedTeamAnswer = "Completed Alpha evidence that must survive interruption.";

    static void CollaborateInterruptionRetainsCompletedTrace()
    {
        foreach (var mode in new[] { "team", "critique", "redteam" })
        foreach (var virtualized in new[] { false, true })
        foreach (var fail in new[] { false, true })
        foreach (var blockAt in new[] { 2, 4 })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("collaborate-interrupted-trace");
                var client = new InterruptedCollaborateClient { FailAfterRelease = fail, BlockAtCall = blockAt };
                var store = new CollaborateHistoryStore(Path.Combine(root, "history.json"));
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                var prompt = new TextBox();
                var center = new ApplicationStatusCenter();
                var resources = new Border();
                AttachArenaPresentationResources(resources);
                ApplyExperimentSurfaceTheme(resources, ThemePalette.BuiltIn.Single(theme => theme.Id == "dark-blue"));
                var coordinator = CreateLifecycleCollaborate(client, prompt, new TextBlock(), panel, center, mode, store,
                    key => resources.TryFindResource(key) as Brush ?? AccentResourceBrush(key));
                Task? send = null;
                try
                {
                    coordinator.Initialize();
                    prompt.Text = "Keep this interrupted team draft.";
                    send = coordinator.SendAsync();
                    PumpDocumentImportTask(client.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
                    PumpLifecycleUi();
                    if (panel is VirtualizingConversationPanel livePanel) livePanel.ApplyViewportForTest(0, 1600, 800);
                    Require(LifecycleText(panel).Contains(RetainedTeamAnswer, StringComparison.Ordinal),
                        "The fixture did not render its completed first team step.");
                    if (!fail) coordinator.Stop();
                    client.Release.TrySetResult();
                    PumpDocumentImportTask(send);
                    PumpLifecycleUi();
                    if (panel is VirtualizingConversationPanel finalPanel) finalPanel.ApplyViewportForTest(0, 1600, 800);

                    var review = coordinator.CaptureControlReview("");
                    Require(client.Calls == blockAt && review.Trace.Count == blockAt - 1 && review.Trace[0].Text == RetainedTeamAnswer
                            && review.Trace.All(step => step.Ok) && review.TotalTokens == 33 * (blockAt - 1) && review.TotalLatencyMs == 22 * (blockAt - 1),
                        "An interrupted collaboration erased or invented completed trace evidence or its usage.");
                    Require(review.NeedsReview && prompt.Text == "Keep this interrupted team draft."
                            && center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State ==
                                (fail ? ApplicationStatusState.Failed : ApplicationStatusState.Cancelled),
                        "Interrupted trace retention consumed the draft or promoted an incomplete run to success.");
                    Require(LifecycleText(panel).Contains(RetainedTeamAnswer, StringComparison.Ordinal),
                        "Completing the interrupted virtual conversation replaced its retained visible trace.");
                    if (mode == "team" && blockAt == 2)
                        CaptureWorkspaceConversationPreview(panel, resources,
                            $"collaborate-{(virtualized ? "virtualized" : "panel")}-{(fail ? "failed" : "stopped")}.png");
                    var saved = store.Load().Single();
                    Require(saved.Exchanges.Last().Interrupted && saved.Exchanges.Last().TraceSteps.Count == blockAt - 1 && saved.Exchanges.Last().TraceSteps[0].Text == RetainedTeamAnswer,
                        "Completed trace did not survive actual history save/load.");

                    var restoredPanel = new VirtualizingConversationPanel();
                    var restored = CreateLifecycleCollaborate(new FixedCollaborateModelClient("unused"), new TextBox(), new TextBlock(),
                        restoredPanel, new ApplicationStatusCenter(), mode, store);
                    restored.Initialize();
                    Require(restored.TryOpenConversation(saved.Id), "The interrupted saved conversation could not be reopened.");
                    restoredPanel.ApplyViewportForTest(0, 1600, 800);
                    var restoredReview = restored.CaptureControlReview(saved.Id.ToString("N"));
                    Require(restoredReview.Trace.Count == blockAt - 1 && restoredReview.NeedsReview
                            && LifecycleText(restoredPanel).Contains(RetainedTeamAnswer, StringComparison.Ordinal),
                        "Reopening an interrupted collaboration lost evidence or displayed a successful review.");
                }
                finally
                {
                    client.Release.TrySetResult();
                    coordinator.Stop();
                    if (send is not null) PumpDocumentImportTask(send);
                    DeleteDraftTestRoot(root);
                }
            }));
    }

    static void CollaborateInterruptedReviewStaysIncompleteAfterExport()
    {
        var exchange = CollaborateCoordinator.InterruptedExchange("Prompt", "Collaboration stopped.",
            [CollaborateCoordinator.CollaborateStep.Completed("alpha", "Alpha", "model", "Draft", RetainedTeamAnswer, 22, 33)]);
        var exported = CollaborateCoordinator.BuildConversationExport("Interrupted", [exchange], []);
        Require(exported.Contains(RetainedTeamAnswer, StringComparison.Ordinal)
                && exported.Contains("Verdict: Needs review", StringComparison.Ordinal)
                && !exported.Contains("Verdict: Ready to use", StringComparison.Ordinal),
            "Export discarded retained trace or promoted an interrupted collaboration to a complete answer.");
        var conversation = new CollaborateCoordinator.CollaborateConversation(Guid.NewGuid(), "Interrupted", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, [exchange], []);
        Require(CollaborateCoordinator.ConversationReviewState(conversation) == "Needs review",
            "Recent chat filters promoted an interrupted run with successful earlier steps to Ready.");
        Require(CollaborateCoordinator.ConversationReviewState(conversation with
                { Exchanges = [exchange with { Interrupted = false, Answer = "A completed answer." }] }) == "Ready",
            "The interruption flag incorrectly marked a completed healthy chat for review.");
    }

    private sealed class InterruptedCollaborateClient : IModelProviderClient
    {
        internal int Calls { get; private set; }
        internal bool FailAfterRelease { get; init; }
        internal int BlockAtCall { get; init; } = 2;
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public async Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == BlockAtCall)
            {
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (FailAfterRelease) throw new InvalidOperationException("fixture failure after a completed role");
            }
            return new ModelCompletionResult(true, config.BaseUrl, config.Model, RetainedTeamAnswer, "", 22, 11, 22, 33, "", DateTimeOffset.UtcNow);
        }
    }
}
