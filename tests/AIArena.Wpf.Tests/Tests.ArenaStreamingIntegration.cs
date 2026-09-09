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