using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ConversationDeferredFactoryFailureStaysInsideDispatcher()
    {
        foreach (var nullResult in new[] { false, true })
            RunStaTest(() => RunWithDispatcherContext(() =>
            {
                var panel = new VirtualizingConversationPanel();
                var failing = true;
                var attempts = 0;
                panel.AddRow(() => new TextBlock { Text = "Before the failed card." }, "before");
                panel.AddRow(() =>
                {
                    attempts++;
                    if (failing)
                    {
                        if (nullResult) return null!;
                        throw new InvalidOperationException(PrivateProjectionFailure);
                    }
                    return new TextBlock { Text = "Retained public answer." };
                }, "broken", automationName: "Assistant reply", automationHelpText: "Retained public answer.");
                panel.AddRow(() => new TextBlock { Text = "After the failed card." }, "after");
                WithDeferredConversationHost(panel, () => failing = false, escaped =>
                {
                    Require(escaped.Count == 0 && attempts == 1 && panel.LogicalRowCount == 3
                            && panel.GetRealizedElement("broken") is FrameworkElement failed
                            && LifecycleText(failed).Contains("Retained public answer.", StringComparison.Ordinal)
                            && !LifecycleText(failed).Contains("sk-test-secret", StringComparison.Ordinal),
                        "A deferred factory failure escaped the dispatcher, discarded the retained answer, or leaked exception details.");
                    for (var index = 0; index < 4; index++)
                    {
                        panel.InvalidateMeasure();
                        PumpLifecycleUi();
                    }
                    Require(attempts == 1 && panel.RealizedVisualKeys.SequenceEqual(new object[] { "before", "broken", "after" }),
                        "An unchanged failed card retried during each layout or changed the logical/visual order.");
                    var retry = FindProviderModelsDescendants<Button>((FrameworkElement)panel.GetRealizedElement("broken")!)
                        .Single(button => AutomationProperties.GetName(button) == "Retry message display");
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpLifecycleUi();
                    Require(attempts == 2 && escaped.Count == 0
                            && panel.GetRealizedElement("broken") is Border stillFailed
                            && LifecycleText(stillFailed).Contains("Retained public answer.", StringComparison.Ordinal),
                        "A persistent display retry escaped, erased retained text, or queued more than one attempt.");
                    retry = FindProviderModelsDescendants<Button>((FrameworkElement)panel.GetRealizedElement("broken")!)
                        .Single(button => AutomationProperties.GetName(button) == "Retry message display");
                    failing = false;
                    var peer = new ButtonAutomationPeer(retry);
                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    PumpLifecycleUi();
                    PumpLifecycleUi();
                    Require(attempts == 3 && panel.GetRealizedElement("broken") is TextBlock restored
                            && restored.Text == "Retained public answer.",
                        $"Retry display did not restore the original row with one local rendering attempt (attempts {attempts}; text '{LifecycleText((FrameworkElement)panel.GetRealizedElement("broken")!)}').");
                });
            }));
    }

    static void ConversationDeferredAgentCardKeepsSavedOutcome()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            using var fixture = new AgentPresentationFixture(virtualized: true);
            fixture.Prompt.Text = "Explain this workspace once.";
            PumpDocumentImportTask(fixture.Agent.DebugSendAsync());
            PumpDocumentImportTask(fixture.Agent.DebugWorkspaceProfileRefreshTask);
            PumpLifecycleUi();
            fixture.BrushFailureArmed = true;
            WithDeferredConversationHost((VirtualizingConversationPanel)fixture.Panel,
                () => fixture.BrushFailureArmed = false, escaped =>
                {
                    Require(escaped.Count == 0 && fixture.Client.Calls == 1
                            && fixture.Store.Load().AgentWorkspaceMessages.Any(message => message.Kind == "Agent"
                                && message.Body == "completed response")
                            && fixture.Center.VisibleEntries.Single(entry => entry.Key == "agent.run").State == ApplicationStatusState.Succeeded
                            && fixture.Prompt.Text.Length == 0 && fixture.Send.IsEnabled && !fixture.Stop.IsEnabled
                            && LifecycleText(fixture.Panel).Contains("completed response", StringComparison.Ordinal),
                        $"Deferred Agent rendering escaped, replayed the model, hid the retained reply, or changed its saved outcome (escaped {escaped.Count}; calls {fixture.Client.Calls}; trace '{escaped.FirstOrDefault()?.StackTrace}').");
                    CaptureWorkspaceConversationPreviewAfterDetach(fixture.Panel, fixture.Resources, "agent-deferred-display.png");
                });
        }));
    }

    static void ConversationDeferredOwnedElementAndRemovedRetryStayContained()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var panel = new VirtualizingConversationPanel();
            var shared = new TextBlock { Text = "Already owned content." };
            var unrelatedOwner = new StackPanel();
            unrelatedOwner.Children.Add(shared);
            var attempts = 0;
            var fail = true;
            var handle = panel.AddRow(() =>
            {
                attempts++;
                return fail ? shared : new TextBlock { Text = "Retained public answer." };
            }, "owned", automationName: "Assistant reply", automationHelpText: "Retained public answer.");
            WithDeferredConversationHost(panel, () => fail = false, escaped =>
            {
                Require(escaped.Count == 0 && panel.GetRealizedElement("owned") is Border
                        && ReferenceEquals(shared.Parent, unrelatedOwner)
                        && panel.RealizedVisualKeys.SequenceEqual(new object[] { "owned" }),
                    "A factory returning an already-owned element escaped or corrupted conversation ownership.");
                var retry = FindProviderModelsDescendants<Button>((FrameworkElement)panel.GetRealizedElement("owned")!)
                    .Single(button => AutomationProperties.GetName(button) == "Retry message display");
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                handle.Remove();
                PumpLifecycleUi();
                Require(attempts == 1 && panel.LogicalRowCount == 0 && panel.RealizedRowCount == 0,
                    "A queued display retry resurrected a removed row or reran its factory.");
            });
        }));
    }

    static void ConversationDeferredCollaborateCardKeepsSavedOutcome()
    {
        RunStaTest(() => RunWithDispatcherContext(() =>
        {
            var root = DraftTestRoot("collaborate-deferred-rendering");
            var armed = false;
            var panel = new VirtualizingConversationPanel();
            var prompt = new TextBox();
            var center = new ApplicationStatusCenter();
            var store = new PostSaveCollaborateHistoryStore(Path.Combine(root, "history.json"), () => { });
            var client = new FixedCollaborateModelClient(PostSaveAnswer);
            var coordinator = CreateLifecycleCollaborate(client, prompt, new TextBlock(), panel, center, "fast", store,
                key => armed ? throw new InvalidOperationException(PrivateProjectionFailure) : AccentResourceBrush(key));
            try
            {
                coordinator.Initialize();
                prompt.Text = "Produce one answer, once.";
                PumpDocumentImportTask(coordinator.SendAsync());
                armed = true;
                WithDeferredConversationHost(panel, () => armed = false, escaped =>
                {
                    Require(escaped.Count == 0 && client.CompleteCalls == 1 && store.SaveCalls == 1
                            && store.Load().Single().Exchanges.Single().Answer == PostSaveAnswer
                            && center.VisibleEntries.Single(entry => entry.Key == "collaborate.run").State == ApplicationStatusState.Succeeded
                            && !coordinator.IsRunning && prompt.IsEnabled && prompt.Text.Length == 0
                            && LifecycleText(panel).Contains(PostSaveAnswer, StringComparison.Ordinal),
                        "Deferred Collaborate rendering escaped, repeated work, hid the retained answer, or changed its saved outcome.");
                });
            }
            finally { armed = false; coordinator.Stop(); DeleteDraftTestRoot(root); }
        }));
    }

    static void WithDeferredConversationHost(VirtualizingConversationPanel panel, Action stopFailing,
        Action<List<Exception>> inspect)
    {
        panel.Visibility = Visibility.Collapsed;
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var host = new Window { Content = scroll, Width = 820, Height = 650, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Opacity = 0, Left = -10000, Top = -10000 };
        AttachArenaPresentationResources(host);
        ApplyExperimentSurfaceTheme(host, ThemePalette.Resolve("dark-blue"));
        var escaped = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, args) =>
        {
            escaped.Add(args.Exception);
            stopFailing(); // Keep the deliberately broken baseline from repeatedly faulting layout.
            args.Handled = true;
        };
        Dispatcher.CurrentDispatcher.UnhandledException += capture;
        try
        {
            host.Show();
            Dispatcher.CurrentDispatcher.BeginInvoke(() => panel.Visibility = Visibility.Visible, DispatcherPriority.Background);
            PumpLifecycleUi();
            inspect(escaped);
        }
        finally
        {
            stopFailing();
            Dispatcher.CurrentDispatcher.UnhandledException -= capture;
            scroll.Content = null;
            host.Content = null;
            host.Close();
        }
    }

    static void CaptureWorkspaceConversationPreviewAfterDetach(Panel panel, FrameworkElement resources, string filename)
    {
        // Hosted snapshots are captured separately; this helper only renders after
        // temporarily detaching from the offscreen owner's ScrollViewer.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIARENA_CONVERSATION_PREVIEW_DIR"))) return;
        var parent = panel.Parent as ScrollViewer;
        if (parent is null) return;
        parent.Content = null;
        try { CaptureWorkspaceConversationPreview(panel, resources, filename); }
        finally { parent.Content = panel; }
    }
}
