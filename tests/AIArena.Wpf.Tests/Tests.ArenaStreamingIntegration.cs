using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Providers;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

internal static partial class Program
{
    static void ArenaRunStreamsIntoLiveTranscriptBeforeCommittingSameHost() =>
        RunStaTest(() => RunExperimentDispatcherTask(ArenaRunStreamsIntoLiveTranscriptCoreAsync));

    private static async Task ArenaRunStreamsIntoLiveTranscriptCoreAsync()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "ai-arena-stream-integration", Guid.NewGuid().ToString("N"));
        var priorApplication = Application.Current;
        var bound = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var provider = new GatedArenaIntegrationStreamingProvider();
        var rowPanel = new StackPanel();
        var window = new Window
        {
            Width = 1000, Height = 400, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Content = rowPanel
        };
        AttachArenaPresentationResources(window);
        window.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush");
        Task<bool>? running = null;
        DependencyPropertyDescriptor? bodyDescriptor = null;
        EventHandler? bodyChanged = null;
        TranscriptCardRenderer.LiveCard? live = null;
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var store = new SessionStore(fixtureRoot);
            var eventLog = new EventLogStore(fixtureRoot);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Configs["shared"] = new ModelProviderConfig
            {
                BaseUrl = "http://127.0.0.1:45678/v1",
                Model = "stream-integration-model",
                ApiMode = ModelProviderApiModes.OpenAiCompatible,
                Timeout = 10,
                MaxOutputTokens = 128
            };
            snapshot.Engine.Steering.Topic = "Describe a useful next step.";
            snapshot.Engine.Internet.UseInternet = false;
            await store.SaveSnapshotAsync(snapshot, cancellationToken: cancellation.Token);
            snapshot = await store.LoadSnapshotAsync(cancellationToken: cancellation.Token)
                ?? throw new InvalidOperationException("isolated streaming snapshot was not saved");
            var session = new CoreSessionSummary("default", "", true, 0, 0, 0, DateTimeOffset.UtcNow);
            var view = SnapshotViewMapper.FromCore(session, snapshot);
            var renderer = CreateTranscriptCardRendererForTest(resourceBrush: key =>
                window.TryFindResource(key) as Brush
                    ?? throw new InvalidOperationException($"Production preview brush '{key}' is unavailable."));
            var firstVisibleText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liveCards = 0;
            var finalRenders = 0;
            var refreshes = 0;
            List<object> displayedRows = [];
            ArenaTranscriptStreamCoordinator streams = null!;

            UIElement RenderSaved(object row)
            {
                finalRenders++;
                return renderer.CreateCard((TranscriptMessage)row, retryable: false, searchMatch: false, isLatest: true);
            }

            void Reconcile()
            {
                var rows = view.Messages.Cast<object>().ToList();
                streams.MergeRows(rows, row => row as TranscriptMessage, RenderSaved, view.Messages);
                displayedRows = rows;
                rowPanel.Children.Clear();
                foreach (var row in rows)
                    rowPanel.Children.Add(row is UIElement element ? element : RenderSaved(row));
                window.UpdateLayout();
            }

            streams = new ArenaTranscriptStreamCoordinator(
                Dispatcher.CurrentDispatcher,
                () => (view.SessionId, view.SessionInstanceId),
                message =>
                {
                    liveCards++;
                    live = renderer.CreateLiveCard(message);
                    bodyDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                    bodyChanged = (_, _) =>
                    {
                        if (live.Body.Text == GatedArenaIntegrationStreamingProvider.Prefix)
                            firstVisibleText.TrySetResult();
                    };
                    bodyDescriptor.AddValueChanged(live.Body, bodyChanged);
                    return live;
                },
                Reconcile);
            using var internet = new InternetToolService(eventLogStore: eventLog);
            var transcript = new TranscriptService();
            var runner = new TurnRunnerService(provider, store, eventLog, transcript, internet);
            using var narrator = new NarratorService(provider, store, eventLog, transcript, internet);
            using var operationGate = new SemaphoreSlim(1, 1);
            var busy = false;
            var coordinator = new ArenaRunCoordinator(
                runner, narrator, operationGate, new Button(), new Button(), new Button(),
                () => session, () => busy, () => false, () => TimeSpan.FromSeconds(30),
                (value, _, _, _) => busy = value,
                async (_, _, action, _) => { await action(); return true; },
                async _ =>
                {
                    var saved = await store.LoadSnapshotAsync(cancellationToken: cancellation.Token)
                        ?? throw new InvalidOperationException("completed streaming snapshot disappeared");
                    view = SnapshotViewMapper.FromCore(session, saved);
                    refreshes++;
                    Reconcile();
                },
                _ => { }, _ => { },
                speaker => speaker.Equals("alpha", StringComparison.OrdinalIgnoreCase),
                runCancelableArenaBusyAsync: async (_, _, action, _) =>
                {
                    busy = true;
                    await operationGate.WaitAsync(cancellation.Token);
                    try
                    {
                        await action(cancellation.Token);
                        return true;
                    }
                    finally
                    {
                        operationGate.Release();
                        busy = false;
                    }
                })
            {
                BeginTranscriptStream = streams.Begin
            };

            window.Show();
            running = coordinator.RunOneTurnAsync();
            await provider.PrefixReported.Task.WaitAsync(bound);
            // The dispatcher timer, not a direct test Flush call, must deliver
            // public text while the real turn runner is still awaiting completion.
            await firstVisibleText.Task.WaitAsync(bound);
            window.UpdateLayout();
            var host = displayedRows.OfType<ContentControl>().Single();
            var liveContent = host.Content;
            var liveCard = live ?? throw new InvalidOperationException("the live transcript card was not created");
            Require(!running.IsCompleted && !provider.Released
                    && liveCard.Body.Text == GatedArenaIntegrationStreamingProvider.Prefix
                    && liveCard.Body.IsVisible && ReferenceEquals(rowPanel.Children[0], host),
                "Arena run did not expose a hosted public delta while the provider was still generating");
            Require(liveCards == 1 && finalRenders == 0 && refreshes == 0
                    && provider.StreamingCalls == 1 && provider.BufferedCalls == 0,
                "Arena run bypassed the streaming path or prematurely finalized its live response");
            var inFlightSnapshot = await store.LoadSnapshotAsync(cancellationToken: cancellation.Token);
            Require(inFlightSnapshot is not null && inFlightSnapshot.Engine.Messages.Count == 0,
                "provisional streamed content was persisted before provider completion");
            Require(!DescendantButtons(liveCard.Element).Any(button => AutomationProperties.GetName(button) is "Copy" or "Delete"),
                "the provisional response exposed saved-message actions");

            CaptureArenaStreamingPreview(window, "arena-stream-live.png");
            provider.Release();
            Require(await running.WaitAsync(bound) && coordinator.LastTurnSucceeded && !busy,
                "the real Arena run did not finish successfully after its provider completed");
            var committed = await store.LoadSnapshotAsync(cancellationToken: cancellation.Token)
                ?? throw new InvalidOperationException("committed streaming snapshot was missing");
            var savedMessage = committed.Engine.Messages.Single();
            Require(savedMessage.Text == GatedArenaIntegrationStreamingProvider.FullText
                    && savedMessage.Model.PromptTokens == 11 && savedMessage.Model.CompletionTokens == 9
                    && committed.Engine.TurnCount == savedMessage.Turn,
                "the final transcript did not preserve exactly one authoritative response and provider usage");
            Require(displayedRows.Count == 1 && ReferenceEquals(displayedRows[0], host)
                    && ReferenceEquals(rowPanel.Children[0], host) && !ReferenceEquals(host.Content, liveContent)
                    && finalRenders == 1 && refreshes == 1 && liveCards == 1,
                "stream completion duplicated the response, replaced its stable host, or finalized it more than once");
            Require(host.Content is Border finalCard
                    && DescendantTextBlocks(finalCard).Any(block => block.Text == savedMessage.Text)
                    && DescendantButtons(finalCard).Any(button => AutomationProperties.GetName(button) == "Copy"),
                "the existing live host did not become an ordinary saved transcript card");
            Reconcile();
            Require(finalRenders == 1 && displayedRows.Count == 1 && ReferenceEquals(displayedRows[0], host),
                "reconciling an unchanged saved snapshot recreated the final card or its row");
            CaptureArenaStreamingPreview(window, "arena-stream-finalized.png");
            Require(ReferenceEquals(Application.Current, priorApplication),
                "the hosted integration fixture started or replaced the production application");
        }
        finally
        {
            cancellation.Cancel();
            provider.Release();
            if (running is not null)
            {
                try { await running.WaitAsync(bound); }
                catch { /* Preserve the assertion failure while draining the isolated turn. */ }
            }
            if (bodyDescriptor is not null && bodyChanged is not null && live is not null)
                bodyDescriptor.RemoveValueChanged(live.Body, bodyChanged);
            window.Close();
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    static void ArenaRunCommittedOutcomesSurvivePresentationFailures() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            foreach (var route in new[] { "one", "agent", "retry", "narrator", "auto" })
            foreach (var fault in new[] { "cancel", "refresh", "refreshCanceled", "evidence", "precommit" })
            {
                if (route == "auto" && fault == "precommit") continue;
                await RunCommittedOutcomeFixtureAsync(route, fault);
            }
            var refreshes = 0;
            var presentation = await AppPostCommitEvidence.RefreshAsync(
                "Saved turn.", true, _ => { refreshes++; return Task.CompletedTask; }, AppErrorContext.Arena,
                () => throw new IOException("private-preview-detail"));
            Require(refreshes == 1 && !presentation.Complete
                    && presentation.Status.StartsWith("Saved turn. Warning:", StringComparison.Ordinal)
                    && !presentation.Status.Contains("private-preview-detail", StringComparison.Ordinal)
                    && !presentation.Status.Contains("retry", StringComparison.OrdinalIgnoreCase),
                "a failed postcommit preview must still refresh the saved result, with a warning rather than advice to retry the turn");
        }));

    private static async Task RunCommittedOutcomeFixtureAsync(string route, string fault)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "ai-arena-committed-ui", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var statusDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        TranscriptCardRenderer.LiveCard? live = null;
        EventHandler? statusChanged = null;
        ArenaTranscriptStreamCoordinator? streams = null;
        try
        {
            var store = new SessionStore(fixtureRoot);
            var eventLog = new EventLogStore(fixtureRoot);
            var snapshot = SessionStore.CreateDefaultSnapshot();
            snapshot.Engine.Internet.UseInternet = false;
            snapshot.Configs["shared"] = new ModelProviderConfig
            {
                Model = "committed-ui-model", BaseUrl = "http://127.0.0.1:45678/v1",
                ApiMode = ModelProviderApiModes.OpenAiCompatible, MaxOutputTokens = 256
            };
            await store.SaveSnapshotAsync(snapshot);
            var provider = new CommittedOutcomeModelClient();
            using var internet = new InternetToolService(eventLogStore: eventLog);
            var transcript = new TranscriptService();
            var runner = new TurnRunnerService(provider, store, eventLog, transcript, internet);
            using var narrator = new NarratorService(provider, store, eventLog, transcript, internet);
            using var gate = new SemaphoreSlim(1, 1);
            if (route == "retry") await runner.RunOneTurnAsync("default");
            snapshot = (await store.LoadSnapshotAsync())!;
            var priorCount = snapshot.Engine.Messages.Count;
            var session = new CoreSessionSummary("default", store.SnapshotPath("default"), true, priorCount, 0, 0, DateTimeOffset.UtcNow);
            var view = SnapshotViewMapper.FromCore(session, snapshot);
            var refreshes = 0;
            var refreshedMessageCount = -1;
            var voiceCalls = 0;
            var statuses = new List<string>();
            var busy = false;
            ArenaRunCoordinator coordinator = null!;
            streams = new ArenaTranscriptStreamCoordinator(
                Dispatcher.CurrentDispatcher, () => (view.SessionId, view.SessionInstanceId),
                message =>
                {
                    live = CreateTranscriptCardRendererForTest().CreateLiveCard(message);
                    statusChanged = (_, _) =>
                    {
                        if (live.Status.Text != "Complete" || fault != "cancel") return;
                        if (route == "auto") coordinator.StopAutoChat();
                        else cancellation.Cancel();
                    };
                    statusDescriptor.AddValueChanged(live.Status, statusChanged);
                    return live;
                }, () => { });
            coordinator = new ArenaRunCoordinator(
                runner, narrator, gate, new Button(), new Button(), new Button(),
                () => session, () => busy, () => false, () => TimeSpan.Zero,
                (value, status, _, _) => { busy = value; statuses.Add(status); },
                async (_, _, action, _) => { await action(); return true; },
                async _ =>
                {
                    refreshes++;
                    if (fault == "refresh") throw new IOException("private-refresh-detail");
                    if (fault == "refreshCanceled") throw new OperationCanceledException("private-refresh-detail");
                    var saved = (await store.LoadSnapshotAsync())!;
                    refreshedMessageCount = saved.Engine.Messages.Count;
                    if (route == "auto") coordinator.StopAutoChat();
                }, statuses.Add, statuses.Add, id => id == "alpha", _ => voiceCalls++,
                runCancelableArenaBusyAsync: async (_, _, action, _) =>
                {
                    busy = true;
                    try { await action(cancellation.Token); return true; }
                    catch (OperationCanceledException) { statuses.Add("Operation cancelled."); return true; }
                    finally { busy = false; }
                })
            {
                BeginTranscriptStream = streams.Begin
            };
            if (fault == "evidence") provider.BeforeCompletion = () =>
            {
                var eventPath = eventLog.EventPath("default");
                if (File.Exists(eventPath)) File.Delete(eventPath);
                Directory.CreateDirectory(eventPath);
            };
            if (fault == "precommit") cancellation.Cancel();
            var callsBefore = provider.Calls;
            switch (route)
            {
                case "one": await coordinator.RunOneTurnAsync(); break;
                case "agent": await coordinator.RunAgentTurnAsync(view.Agents.First(agent => agent.Id == "alpha")); break;
                case "retry": await coordinator.RetryTranscriptMessageAsync(view.Messages.Single()); break;
                case "narrator": await coordinator.NarrateNowAsync(); break;
                case "auto": await coordinator.StartAutoChatAsync(); break;
            }
            var committed = (await store.LoadSnapshotAsync())!;
            var expectedCount = fault == "precommit" || route == "retry" ? priorCount : priorCount + 1;
            Require(committed.Engine.Messages.Count == expectedCount && !busy && !coordinator.IsAutoChatRunning,
                $"{route}/{fault}: saved response count or terminal busy state was wrong");
            if (fault == "precommit")
            {
                Require(refreshes == 0 && provider.Calls == callsBefore && !coordinator.LastTurnSucceeded
                        && !coordinator.LastNarrationSucceeded && statuses.Last() == "Operation cancelled.",
                    $"{route}: precommit cancellation was reported as committed or invoked the provider");
                return;
            }
            Require(provider.Calls == callsBefore + 1 && refreshes == 1,
                $"{route}/{fault}: completion skipped refresh or Auto Chat started another turn after projection failure/stop");
            Require(committed.Engine.Messages.Last().Text == CommittedOutcomeModelClient.Reply
                    && committed.Engine.Messages.All(message => message.Status != "error"),
                $"{route}/{fault}: a saved model response became an error or lost its body");
            Require(route != "one" || coordinator.LastTurnSucceeded, "a saved one-turn result lost its success receipt");
            Require(route != "narrator" || coordinator.LastNarrationSucceeded && voiceCalls == 1,
                "a saved narration lost its success receipt or optional voice callback");
            Require(!statuses.Last().Contains("private-refresh-detail", StringComparison.Ordinal)
                    && !statuses.Last().Contains("Operation cancelled", StringComparison.Ordinal)
                    && !statuses.Last().Contains("failed:", StringComparison.OrdinalIgnoreCase),
                $"{route}/{fault}: completion was relabeled failed/canceled or exposed private exception text");
            if (fault is "refresh" or "refreshCanceled")
                Require(statuses.Last().Contains("Warning:", StringComparison.Ordinal)
                        && statuses.Last().Contains("AA-ARENA-", StringComparison.Ordinal),
                    $"{route}/{fault}: a failed postcommit refresh did not produce a coded warning");
            else
                Require(refreshedMessageCount == expectedCount,
                    $"{route}/{fault}: the durable response was not reloaded after completion");
            if (fault == "evidence")
                Require(statuses.Last().Contains("activity-log", StringComparison.Ordinal),
                    $"{route}: missing completion-log evidence was hidden from the saved result status");
            if (fault == "cancel" && route != "auto") Require(cancellation.IsCancellationRequested,
                $"{route}: fixture did not cancel when the committed stream completed");
        }
        finally
        {
            if (live is not null && statusChanged is not null)
                statusDescriptor.RemoveValueChanged(live.Status, statusChanged);
            streams?.Clear();
            DeleteCommittedOutcomeFixture(fixtureRoot, "ai-arena-committed-ui");
        }
    }

    static void OperatorCommittedSendsClearDraftDespiteSecondaryFailures() =>
        RunStaTest(() => RunExperimentDispatcherTask(async () =>
        {
            foreach (var route in new[] { "narrator", "public", "private", "opening" })
            foreach (var fault in new[] { "evidence", "refresh", "voice", "precommit" })
            {
                if (fault == "voice" && route != "narrator") continue;
                var fixtureRoot = Path.Combine(Path.GetTempPath(), "ai-arena-operator-ui", Guid.NewGuid().ToString("N"));
                try
                {
                    var store = new SessionStore(fixtureRoot);
                    var events = new EventLogStore(fixtureRoot);
                    var snapshot = SessionStore.CreateDefaultSnapshot();
                    snapshot.Engine.Internet.UseInternet = false;
                    snapshot.Engine.FactoryMode = route == "opening";
                    snapshot.Configs["shared"] = new ModelProviderConfig
                    {
                        Model = "committed-ui-model", BaseUrl = "http://127.0.0.1:45678/v1",
                        ApiMode = ModelProviderApiModes.OpenAiCompatible
                    };
                    await store.SaveSnapshotAsync(snapshot);
                    snapshot = (await store.LoadSnapshotAsync())!;
                    var session = new CoreSessionSummary("default", store.SnapshotPath("default"), true, 0, 0, 0, DateTimeOffset.UtcNow);
                    var view = SnapshotViewMapper.FromCore(session, snapshot);
                    var provider = new CommittedOutcomeModelClient();
                    using var narrator = new NarratorService(provider, store, events);
                    var text = new TextBox();
                    var statuses = new List<string>();
                    var refreshes = 0;
                    void BlockEventLog()
                    {
                        var path = events.EventPath("default");
                        if (File.Exists(path)) File.Delete(path);
                        Directory.CreateDirectory(path);
                    }
                    var coordinator = new OperatorTurnCoordinator(
                        store, events, new TranscriptService(), narrator, new DiscourseDiagnosticsService(),
                        new WpfSettingsStore(Path.Combine(fixtureRoot, "settings.json")),
                        new Button(), new Button(), new Button(), new Grid(), new ComboBox(),
                        new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock(),
                        [new Button(), new Button(), new Button(), new Button()], new ComboBox(),
                        new Button(), new Button(), new Button(), text, new Button(),
                        () => new WpfSettings { OperatorTemplates = [] }, () => session, () => view, () => false,
                        AccentResourceBrush,
                        async (_, _, action, _) =>
                        {
                            try { await action(); }
                            catch (OperationCanceledException) { statuses.Add("Operation cancelled."); }
                        },
                        async (saved, id) =>
                        {
                            if (fault == "precommit") throw new OperationCanceledException();
                            await store.SaveSnapshotAsync(saved, id);
                            if (fault == "evidence") BlockEventLog();
                        },
                        _ =>
                        {
                            refreshes++;
                            return fault == "refresh" ? Task.FromException(new IOException("private-refresh-detail")) : Task.CompletedTask;
                        }, statuses.Add, statuses.Add,
                        _ => { if (fault == "voice") throw new IOException("private-voice-detail"); });
                    coordinator.InitializeControls();
                    coordinator.ApplySnapshot(view);
                    coordinator.SetRouteMode(route == "opening" ? "public" : route);
                    text.Text = "My operator request";
                    coordinator.UpdateTurnMeter();
                    provider.BeforeCompletion = () =>
                    {
                        if (fault == "precommit") throw new OperationCanceledException();
                        if (fault == "evidence") BlockEventLog();
                    };
                    OperatorTurnSendReceipt? receipt = null;
                    if (route == "opening") receipt = await coordinator.SendConversationStartAsync(view.SessionId, view.SessionInstanceId);
                    else await coordinator.SendOperatorTurnAsync();
                    var saved = (await store.LoadSnapshotAsync())!;
                    if (fault == "precommit")
                        Require(text.Text == "My operator request" && saved.Engine.Messages.Count == 0 && refreshes == 0
                                && saved.Engine.Agents.All(agent => agent.PrivateNotes.Count == 0) && receipt is null,
                            $"{route}: precommit cancellation cleared a draft or pretended a send was saved");
                    else
                    {
                        Require(text.Text.Length == 0 && refreshes == 1
                                && statuses.Last().Contains("Warning:", StringComparison.Ordinal)
                                && !statuses.Last().Contains("private-", StringComparison.Ordinal)
                                && !statuses.Last().Contains("retry", StringComparison.OrdinalIgnoreCase),
                            $"{route}/{fault}: a saved send must clear its draft and retain a privacy-safe warning without retry advice");
                        if (route == "private")
                            Require(saved.Engine.Messages.Count == 0 && saved.Engine.Agents.Where(agent => agent.Active)
                                    .All(agent => agent.PrivateNotes.Contains("Operator private: My operator request")),
                                $"{route}/{fault}: private guidance was not durably saved");
                        else
                            Require(saved.Engine.Messages.Single().Text == (route == "narrator" ? CommittedOutcomeModelClient.Reply : "My operator request"),
                                $"{route}/{fault}: saved message was missing or duplicated");
                        if (route == "opening")
                            Require(receipt is not null && receipt.Completed == (fault != "refresh"),
                                "Send and start requires a refreshed opening; failed presentation must retain a saved but incomplete receipt");
                    }
                }
                finally { DeleteCommittedOutcomeFixture(fixtureRoot, "ai-arena-operator-ui"); }
            }
        }));

    private sealed class CommittedOutcomeModelClient : IModelProviderClient
    {
        internal const string Reply = "A useful next step is to test the assumption and compare the evidence.";
        internal Action? BeforeCompletion { get; set; }
        internal int Calls { get; private set; }
        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));
        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            BeforeCompletion?.Invoke();
            return Task.FromResult(new ModelCompletionResult(true, config.BaseUrl, config.Model, Reply, "", 1,
                0, 0, 0, "", DateTimeOffset.Now, StopReason: ModelCompletionStopReason.Completed));
        }
    }

    private static void DeleteCommittedOutcomeFixture(string fixtureRoot, string parentName)
    {
        var root = Path.GetFullPath(fixtureRoot);
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), parentName));
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new InvalidOperationException("Refusing to clean up a fixture outside its temporary parent.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void CaptureArenaStreamingPreview(Window window, string filename)
    {
        var outputDirectory = Environment.GetEnvironmentVariable("AIARENA_STREAM_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        Directory.CreateDirectory(outputDirectory);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var destination = File.Create(Path.Combine(outputDirectory, filename));
        encoder.Save(destination);
    }
    private sealed class GatedArenaIntegrationStreamingProvider : IModelProviderClient, IStreamingModelProviderClient
    {
        internal const string Prefix = "The visible response arrives ";
        private const string Suffix = "before completion.";
        internal const string FullText = Prefix + Suffix;
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PrefixReported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StreamingCalls { get; private set; }
        internal int BufferedCalls { get; private set; }
        internal bool Released => release.Task.IsCompleted;
        internal void Release() => release.TrySetResult();

        public Task<ModelProviderModels> ListModelsAsync(ModelProviderConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderModels(true, config.BaseUrl, [config.Model], "", DateTimeOffset.Now));

        public Task<ModelCompletionResult> CompleteChatAsync(
            ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages, CancellationToken cancellationToken = default)
        {
            BufferedCalls++;
            return Task.FromException<ModelCompletionResult>(new InvalidOperationException("Arena integration unexpectedly used buffered completion"));
        }

        public Task<ModelCompletionResult> CompleteChatStreamingAsync(
            ModelProviderConfig config, IReadOnlyList<ModelChatMessage> messages, IProgress<string>? progress,
            CancellationToken cancellationToken = default)
        {
            StreamingCalls++;
            return Task.Run(async () =>
            {
                progress?.Report(Prefix);
                PrefixReported.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                progress?.Report(Suffix);
                return new ModelCompletionResult(
                    true, config.BaseUrl, config.Model, FullText, "Separate private reasoning",
                    25, 11, 9, 20, "", DateTimeOffset.Now, StopReason: ModelCompletionStopReason.Completed);
            }, cancellationToken);
        }
    }
}