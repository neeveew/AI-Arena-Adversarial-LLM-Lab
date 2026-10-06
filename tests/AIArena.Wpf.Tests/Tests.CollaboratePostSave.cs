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
    private const string PostSaveAnswer = "Exactly one saved team answer.";
    private const string PrivateProjectionFailure = "fixture private refresh detail sk-test-secret C:\\private\\saved-chat";

    static void CollaborateSavedAnswerSurvivesRecentRefreshFailure()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var persistent in new[] { false, true })
        foreach (var failSaving in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("collaborate-post-save");
                var refreshFailureArmed = false;
                var refreshFailed = false;
                var store = new PostSaveCollaborateHistoryStore(Path.Combine(root, "history.json"), () => refreshFailureArmed = true, failSaving);
                var client = new FixedCollaborateModelClient(PostSaveAnswer);
                var prompt = new TextBox();
                var status = new TextBlock();
                var center = new ApplicationStatusCenter();
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                Brush BrushForTest(string key)
                {
                    if (refreshFailureArmed && (!refreshFailed || persistent))
                    {
                        refreshFailed = true;
                        throw new InvalidOperationException(PrivateProjectionFailure);
                    }
                    return AccentResourceBrush(key);
                }
                var coordinator = CreateLifecycleCollaborate(client, prompt, status, panel, center, "fast", store, BrushForTest);
                try
                {
                    coordinator.Initialize();
                    prompt.Text = "Produce one answer, once.";
                    PumpDocumentImportTask(coordinator.SendAsync());
                    var saved = store.Load();
                    Require(client.CompleteCalls == 1 && store.SaveCalls == 1,
                        $"A refresh failure repeated the provider or save (saves {store.SaveCalls}).");
                    if (!failSaving)
                        Require(saved.Count == 1 && saved[0].Exchanges.Count == 1 && saved[0].Exchanges[0].Answer == PostSaveAnswer
                                && !saved[0].Exchanges[0].Interrupted,
                            "A post-save failure duplicated or replaced a durably completed exchange.");
                    else
                        Require(saved.Count == 0 && prompt.Text == "Produce one answer, once." && status.Text.Contains("AA-COLLAB-IO", StringComparison.Ordinal),
                            "A failed primary save was hidden or consumed the unsaved draft.");
                    Require(!coordinator.IsRunning && prompt.IsEnabled && (failSaving || prompt.Text.Length == 0)
                            && center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State ==
                                (failSaving ? ApplicationStatusState.Failed : ApplicationStatusState.Succeeded),
                        "Presentation failure changed the primary saved outcome or prevented control cleanup.");
                    var warning = center.VisibleEntries.Single(entry => entry.Key == "collaborate.notice");
                    Require(warning.State == ApplicationStatusState.Warning
                            && warning.Summary.Contains("saved", StringComparison.OrdinalIgnoreCase) == !failSaving,
                        "The refresh warning did not distinguish a durable answer from a failed save.");
                    Require(!status.Text.Contains("sk-test-secret", StringComparison.Ordinal) && !status.Text.Contains("private", StringComparison.OrdinalIgnoreCase),
                        "The secondary status leaked raw refresh exception details.");
                    if (!persistent)
                    {
                        if (panel is VirtualizingConversationPanel virtualPanel) virtualPanel.ApplyViewportForTest(0, 1600, 800);
                        Require(LifecycleText(panel).Contains(PostSaveAnswer, StringComparison.Ordinal),
                            "The recoverable final view lost the accepted provider response.");
                    }
                }
                finally { coordinator.Stop(); DeleteDraftTestRoot(root); }
            }));
    }

    static void CollaborateSavedOutcomeSurvivesStatusObserverFailure()
    {
        foreach (var virtualized in new[] { false, true })
        foreach (var providerOk in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("collaborate-post-save-status");
                var store = new PostSaveCollaborateHistoryStore(Path.Combine(root, "history.json"), () => { });
                var client = new PostSaveOutcomeClient(providerOk);
                var prompt = new TextBox();
                var status = new TextBlock();
                var center = new ApplicationStatusCenter();
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                var coordinator = CreateLifecycleCollaborate(client, prompt, status, panel, center, "fast", store);
                coordinator.Initialize();
                center.Changed += (_, _) => { if (store.SaveCalls > 0) throw new InvalidOperationException(PrivateProjectionFailure); };
                try
                {
                    prompt.Text = "Preserve this model outcome.";
                    PumpDocumentImportTask(coordinator.SendAsync());
                    var saved = store.Load().Single();
                    Require(client.Calls == 1 && store.SaveCalls == 1 && saved.Exchanges.Count == 1
                            && saved.Exchanges[0].Answer.Contains(PostSaveAnswer, StringComparison.Ordinal) && !saved.Exchanges[0].Interrupted,
                        "A failing status observer replaced or duplicated an already-saved model result.");
                    Require(center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State ==
                                (providerOk ? ApplicationStatusState.Succeeded : ApplicationStatusState.Failed)
                            && prompt.Text == (providerOk ? "" : "Preserve this model outcome.") && !coordinator.IsRunning && prompt.IsEnabled,
                        "A post-save status callback changed the provider outcome, draft policy, or cleanup.");
                    Require(center.VisibleEntries.Any(entry => entry.Key == "collaborate.notice" && entry.State == ApplicationStatusState.Warning)
                            && !status.Text.Contains("sk-test-secret", StringComparison.Ordinal),
                        "The post-save status failure lacked a safe separate warning.");
                }
                finally { coordinator.Stop(); DeleteDraftTestRoot(root); }
            }));
    }

    static void CollaborateTracePreviewFailureDoesNotStopTeamCompletion()
    {
        foreach (var virtualized in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var root = DraftTestRoot("collaborate-trace-preview");
                var failNextBrush = false;
                var client = new PostSaveOutcomeClient(true, () => failNextBrush = true);
                var store = new PostSaveCollaborateHistoryStore(Path.Combine(root, "history.json"), () => { });
                var prompt = new TextBox();
                var center = new ApplicationStatusCenter();
                Panel panel = virtualized ? new VirtualizingConversationPanel() : new StackPanel();
                var coordinator = CreateLifecycleCollaborate(client, prompt, new TextBlock(), panel, center, "team", store, key =>
                {
                    if (failNextBrush) { failNextBrush = false; throw new InvalidOperationException(PrivateProjectionFailure); }
                    return AccentResourceBrush(key);
                });
                try
                {
                    coordinator.Initialize();
                    prompt.Text = "Finish the team despite a failed preview.";
                    PumpDocumentImportTask(coordinator.SendAsync());
                    var exchange = store.Load().Single().Exchanges.Single();
                    Require(client.Calls == 4 && store.SaveCalls == 1 && exchange.TraceSteps.Count == 4
                            && exchange.TraceSteps.All(step => step.Ok && step.Text == PostSaveAnswer) && !exchange.Interrupted,
                        "A presentation-only trace failure truncated the causal team work or its saved trace.");
                    Require(prompt.Text.Length == 0 && center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Succeeded,
                        "A failed preview changed a completed team outcome or consumed-draft policy.");
                }
                finally { coordinator.Stop(); DeleteDraftTestRoot(root); }
            }));
    }

    private sealed class PostSaveCollaborateHistoryStore(string path, Action afterSave, bool failSaving = false) : CollaborateHistoryStore(path)
    {
        internal int SaveCalls { get; private set; }
        public override void Save(IReadOnlyList<CollaborateHistoryConversation> conversations)
        {
            SaveCalls++;
            if (failSaving) { afterSave(); throw new IOException("fixture private storage failure"); }
            base.Save(conversations);
            afterSave();
        }
    }

    private sealed class PostSaveOutcomeClient(bool ok, Action? completed = null) : IModelProviderClient
    {
        internal int Calls { get; private set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.UtcNow));
        public Task<ModelCompletionResult> CompleteChatAsync(ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            completed?.Invoke();
            return Task.FromResult(new ModelCompletionResult(ok, config.BaseUrl, config.Model, PostSaveAnswer,
                ok ? "" : "fixture provider failure", 10, 11, 22, 33, "", DateTimeOffset.UtcNow));
        }
    }
}
