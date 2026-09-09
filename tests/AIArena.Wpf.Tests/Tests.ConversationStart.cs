using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

internal static partial class Program
{
    static void ConversationStarterRelocatesSingleComposerAndPreservesDrafts()
    {
        WithConversationStarter(async fixture =>
        {
            fixture.Apply(fixture.Current with { FactoryMode = false });
            fixture.Operator.SetRouteMode("private");
            fixture.Text.Text = "private draft kept out of the public conversation";
            fixture.Operator.UpdateTurnMeter();
            fixture.Apply(fixture.Current with { FactoryMode = true });
            Require(ReferenceEquals(fixture.StarterHost.Content, fixture.Composer) && fixture.Dock.Content is null
                    && ReferenceEquals(fixture.Composer.Child, fixture.Text)
                    && fixture.Text.Text == "" && fixture.PrivateButton.Visibility == Visibility.Collapsed
                    && AutomationProperties.GetName(fixture.SendOnlyButton) == "Send only",
                "The first-message surface must relocate the actual composer and restore the separate Public draft.");
            fixture.Text.Text = "  Public first message with exact whitespace  ";
            fixture.Operator.UpdateTurnMeter();
            fixture.Operator.SetRouteMode("private");
            Require(fixture.Text.Text.StartsWith("  Public", StringComparison.Ordinal),
                "The centered first-message composer must keep its public route.");
            fixture.Apply(fixture.Current with { FactoryMode = false });
            Require(ReferenceEquals(fixture.Dock.Content, fixture.Composer) && fixture.StarterHost.Content is null
                    && fixture.PrivateButton.Visibility == Visibility.Visible
                    && AutomationProperties.GetName(fixture.SendOnlyButton) == "Send Public",
                "Leaving Conversation only must return the same composer and normal route controls.");
            fixture.Operator.SetRouteMode("private");
            Require(fixture.Text.Text == "private draft kept out of the public conversation",
                "Opening the starter must not overwrite the private draft.");
            fixture.Operator.SetRouteMode("public");
            Require(fixture.Text.Text == "  Public first message with exact whitespace  ",
                "Relocating the composer must preserve the exact Public draft.");
            await Task.CompletedTask;
        }, hostComposer: true);
    }

    static void ConversationStarterPersistsBeforeRunAndPreservesInflightEdits()
    {
        WithConversationStarter(async fixture =>
        {
            var beforeCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCommit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.BeforeSave = async _ =>
            {
                beforeCommit.TrySetResult(true);
                await allowCommit.Task;
            };
            const string first = "  First public line\nSecond public line  ";
            fixture.Text.Text = first;
            fixture.Operator.UpdateTurnMeter();
            var send = fixture.Starter.SendAndStartAsync();
            await beforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && !fixture.StartButton.IsEnabled
                    && fixture.Load().Engine.Messages.Count == 0,
                "Auto Chat must not start before the first message is durably committed.");
            fixture.Text.Text = "new edit made while the first send is pending";
            fixture.Operator.UpdateTurnMeter();
            fixture.OnStart = () => Require(fixture.Load().Engine.Messages.Single().Text == first,
                "The run callback must observe the exact committed Public message.");
            allowCommit.TrySetResult(true);
            await send.WaitAsync(TimeSpan.FromSeconds(10));
            Require(fixture.Starts == 1 && fixture.Load().Engine.Messages.Single().SpeakerId == "operator"
                    && fixture.Text.Text == "new edit made while the first send is pending"
                    && ReferenceEquals(fixture.Dock.Content, fixture.Composer) && fixture.StarterHost.Content is null,
                "Successful send-and-start must run once, preserve newer edits, and return the same composer to its dock.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "send only retains explicit control over running";
            await fixture.Operator.SendOperatorTurnAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1 && fixture.Text.Text == "",
                "The existing Send only action must commit through the same pipeline without starting Auto Chat.");
        });
    }

    static void ConversationStarterFailuresAndSessionChangesDoNotStart()
    {
        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "retain after failed first-message save";
            fixture.BeforeSave = _ => throw new IOException("fixture save failed");
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 0
                    && fixture.Text.Text == "retain after failed first-message save"
                    && fixture.Status.Contains("fixture save failed", StringComparison.Ordinal)
                    && ReferenceEquals(fixture.StarterHost.Content, fixture.Composer),
                "Failed persistence must retain the draft and starter, report the failure, and never start a run.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "retain after completion failure";
            fixture.AfterRefresh = () => throw new IOException("fixture completion failed");
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1
                    && fixture.Text.Text == "retain after completion failure"
                    && fixture.Status.Contains("fixture completion failed", StringComparison.Ordinal),
                "A post-commit completion failure must never start Auto Chat or falsely clear the draft.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "committed in the originating session";
            fixture.AfterRefresh = () =>
            {
                fixture.Session = fixture.Session with { Id = "different-session" };
                fixture.Apply(fixture.Current with
                {
                    SessionId = fixture.Session.Id,
                    SessionInstanceId = Guid.NewGuid().ToString("N"),
                    Messages = [],
                    HasFactoryConversationRoot = false,
                    FactoryConversationRootAssigned = false
                });
                fixture.Text.Text = "new session draft";
                fixture.Operator.UpdateTurnMeter();
            };
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1
                    && fixture.Text.Text == "new session draft",
                "Completion in an earlier session must not start the new session or clear its draft.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "committed while provider went offline";
            fixture.AfterRefresh = () => fixture.Apply(fixture.Current with { ProviderOnline = false });
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1
                    && fixture.Status.Contains("Connect the configured provider", StringComparison.Ordinal),
                "Post-send readiness must retain a newly observed provider blocker instead of starting Auto Chat.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Text.Text = "committed before mode changed";
            fixture.AfterRefresh = () => fixture.Apply(fixture.Current with { FactoryMode = false });
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1,
                "A mode change during completion must prevent an automatic run.");
        });
    }

    static void ConversationStarterRejectsStaleRootsAndKeepsOtherBlockers()
    {
        WithConversationStarter(async fixture =>
        {
            var empty = fixture.Current;
            Require(ArenaOperationCoordinator.EvaluateReadiness(empty).RequiresConversationStart
                    && ConversationStartCoordinator.ShouldCenterComposer(empty with { TurnCount = 99 }),
                "First-message readiness must use the real input prerequisite, not a zero turn count.");
            foreach (var blocked in new[]
            {
                empty with { ProviderOnline = false },
                empty with { ProviderModel = "", ExplicitRoleModels = new Dictionary<string, string>() },
                empty with { MatchEnded = true },
                empty with { HasUnresolvedContextFailure = true },
                empty with { Agents = [] },
                empty with { FactoryConversationRootAssigned = true, HasFactoryConversationRoot = false }
            })
            {
                fixture.Apply(blocked);
                Require(!ArenaOperationCoordinator.EvaluateReadiness(blocked).RequiresConversationStart
                        && !fixture.Starter.FocusIfOnlyStartingMessageMissing() && !fixture.StartButton.IsEnabled,
                    "Provider, routing, ended-match, cast, and missing-root blockers must not become start-message actions.");
            }
            var missingRoot = empty with { FactoryConversationRootAssigned = true, HasFactoryConversationRoot = false };
            Require(!ConversationStartCoordinator.ShouldCenterComposer(missingRoot)
                    && !ConversationStartCoordinator.ShouldCenterComposer(empty with { HasUnresolvedContextFailure = true }),
                "Missing-root and context recovery must retain their transcript recovery surface instead of an opening composer overlay.");

            fixture.Apply(empty);
            fixture.Text.Text = "must not replace an unseen anchored root";
            var stored = fixture.Load();
            var rootMessage = new TranscriptService().CreateOperatorMessage("earlier root", 1);
            stored.Engine.Messages.Add(rootMessage);
            new FactoryConversationService().Resolve(stored);
            await fixture.Store.SaveSnapshotAsync(stored, ConversationStarterFixture.SessionId);
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1
                    && fixture.Text.Text == "must not replace an unseen anchored root",
                "An authoritative root discovered after rendering must reject the stale first send without clearing its draft.");
        });

        WithConversationStarter(async fixture =>
        {
            var stored = fixture.Load();
            var failure = new DialogueMessage
            {
                Speaker = "Alpha", SpeakerId = "alpha", Turn = 1,
                Status = "error", Kind = "message", Text = "pending recovery evidence"
            };
            failure.Metadata["completion_failure_kind"] = System.Text.Json.JsonSerializer.SerializeToElement("context_limit_exceeded");
            stored.Engine.Messages.Add(failure);
            await fixture.Store.SaveSnapshotAsync(stored, ConversationStarterFixture.SessionId);
            fixture.Text.Text = "must wait for recovery";
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 1
                    && fixture.Text.Text == "must wait for recovery"
                    && fixture.Status == TurnRunnerService.ContextRecoveryRequiredError,
                "Authoritative pending context recovery must reject a stale starter without changing its explanation or draft.");
        });

        WithConversationStarter(async fixture =>
        {
            fixture.Apply(fixture.Current with { SessionInstanceId = Guid.NewGuid().ToString("N") });
            fixture.Text.Text = "belongs to another session incarnation";
            await fixture.Starter.SendAndStartAsync();
            Require(fixture.Starts == 0 && fixture.Load().Engine.Messages.Count == 0
                    && fixture.Text.Text == "belongs to another session incarnation",
                "The starter must reject a reused session name whose persisted incarnation differs.");
        });
    }

    private static void WithConversationStarter(Func<ConversationStarterFixture, Task> test, bool hostComposer = false)
    {
        RunStaTest(() =>
        {
            using var fixture = new ConversationStarterFixture();
            if (hostComposer) fixture.HostVisualTree();
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                PumpDocumentImportTask(test(fixture));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
    }

    private sealed class ConversationStarterFixture : IDisposable
    {
        internal const string SessionId = "conversation-starter";
        private readonly string root = DraftTestRoot("conversation-starter");
        private readonly NarratorService narrator;
        private Window? visualHost;
        internal SessionStore Store { get; }
        internal SessionSummary Session { get; set; }
        internal ArenaViewSnapshot Current { get; private set; }
        internal TextBox Text { get; } = new();
        internal Border Composer { get; }
        internal ContentControl Dock { get; } = new();
        internal ContentControl StarterHost { get; } = new();
        internal Button PrivateButton { get; } = new();
        internal Button SendOnlyButton { get; } = new();
        internal Button StartButton { get; } = new();
        internal OperatorTurnCoordinator Operator { get; }
        internal ConversationStartCoordinator Starter { get; }
        internal string Status { get; private set; } = "";
        internal int Starts { get; private set; }
        internal Func<ArenaSnapshot, Task>? BeforeSave { get; set; }
        internal Action? AfterRefresh { get; set; }
        internal Action? OnStart { get; set; }

        internal ConversationStarterFixture()
        {
            Store = new SessionStore(root);
            Session = new SessionSummary(SessionId, "", false, 0, 0, 0, DateTimeOffset.UtcNow);
            var initial = SessionStore.CreateDefaultSnapshot();
            initial.Engine.FactoryMode = true;
            initial.Engine.Messages.Clear();
            initial.Configs["shared"] = new ModelProviderConfig { Model = "fixture-model" };
            Store.SaveSnapshotAsync(initial, SessionId).GetAwaiter().GetResult();
            Current = SnapshotViewMapper.FromCore(Session, Load()) with { ProviderOnline = true };
            narrator = new NarratorService(sessionStore: Store, eventLogStore: new EventLogStore(root));
            Composer = new Border { Child = Text };
            Dock.Content = Composer;
            Operator = new OperatorTurnCoordinator(
                Store, new EventLogStore(root), new TranscriptService(), narrator,
                new DiscourseDiagnosticsService(), new WpfSettingsStore(Path.Combine(root, "settings.json")),
                new Button(), PrivateButton, new Button(), new Grid(), new ComboBox(),
                new TextBlock(), new TextBlock(), new TextBlock(), new TextBlock(),
                [new Button(), new Button(), new Button(), new Button()],
                new ComboBox(), new Button(), new Button(), new Button(), Text, SendOnlyButton,
                () => new WpfSettings { OperatorTemplates = [] }, () => Session, () => Current, () => false,
                AccentResourceBrush,
                async (_, _, action, _) =>
                {
                    try { await action(); }
                    catch (Exception error) { Status = error.Message; }
                },
                async (snapshot, id) =>
                {
                    if (BeforeSave is not null) await BeforeSave(snapshot);
                    await Store.SaveSnapshotAsync(snapshot, id);
                },
                _ =>
                {
                    Apply(SnapshotViewMapper.FromCore(Session, Load()) with { ProviderOnline = true });
                    AfterRefresh?.Invoke();
                    return Task.CompletedTask;
                },
                status => Status = status, status => Status = status);
            Operator.InitializeControls();
            Starter = new ConversationStartCoordinator(
                Composer, Dock, StarterHost, new Grid(), new TextBlock(), StartButton, Text, Operator,
                () => Current, () => Session,
                () => { OnStart?.Invoke(); Starts++; return Task.CompletedTask; },
                () => { }, status => Status = status);
            Apply(Current);
        }

        // Assertions inspect durable state synchronously, including inside UI
        // callbacks. Keep that read off the dispatcher so async JSON I/O cannot
        // capture the fixture's synchronization context and deadlock the test.
        internal ArenaSnapshot Load() => Task.Run(() => Store.LoadSnapshotAsync(SessionId)).GetAwaiter().GetResult()!;

        internal void Apply(ArenaViewSnapshot value)
        {
            Current = value;
            Operator.ApplySnapshot(value);
            Starter.ApplySnapshot(value);
            visualHost?.UpdateLayout();
        }

        internal void HostVisualTree()
        {
            var content = new StackPanel();
            content.Children.Add(Dock);
            content.Children.Add(StarterHost);
            visualHost = new Window
            {
                Content = content, Width = 600, Height = 400, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Opacity = 0, Left = -10000, Top = -10000
            };
            visualHost.Show();
            visualHost.UpdateLayout();
        }

        public void Dispose()
        {
            visualHost?.Close();
            narrator.Dispose();
            DeleteDraftTestRoot(root);
        }
    }
}
