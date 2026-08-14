using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class AgentBoardCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Panel agentItems;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<bool> isAutoChatRunning;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<AgentState, Task> runAgentTurnAsync;
    private readonly RoutedEventHandler narrateNowHandler;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setArenaRunStatus;
    private readonly Func<bool> animationsEnabled;
    private readonly Dictionary<string, AgentCardView> agentCards = new(StringComparer.OrdinalIgnoreCase);
    private NarratorCardView? narratorCard;
    private bool participantActionsReady;
    private string participantReadinessMessage = "Complete provider, model, and cast setup before running this model.";
    private bool narratorActionsReady;
    private bool factoryMode;

    internal int DiagnosticCardCreations { get; private set; }
    internal int DiagnosticCardUpdates { get; private set; }
    internal FrameworkElement? DiagnosticCardFor(string participantId) =>
        participantId.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            ? narratorCard?.Card
            : agentCards.GetValueOrDefault(participantId)?.Card;
    internal FrameworkElement? DiagnosticActivitySweepFor(string participantId) =>
        participantId.Equals("narrator", StringComparison.OrdinalIgnoreCase)
            ? narratorCard?.ActivitySweep
            : agentCards.GetValueOrDefault(participantId)?.ActivitySweep;

    public AgentBoardCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Panel agentItems,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> isArenaBusy,
        Func<bool> isAutoChatRunning,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> displayStatusValue,
        Func<AgentState, Task> runAgentTurnAsync,
        RoutedEventHandler narrateNowHandler,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setArenaRunStatus,
        Func<bool>? animationsEnabled = null)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.agentItems = agentItems;
        this.activeSession = activeSession;
        this.isArenaBusy = isArenaBusy;
        this.isAutoChatRunning = isAutoChatRunning;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.displayStatusValue = displayStatusValue;
        this.runAgentTurnAsync = runAgentTurnAsync;
        this.narrateNowHandler = narrateNowHandler;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setArenaRunStatus = setArenaRunStatus;
        this.animationsEnabled = animationsEnabled ?? (() => SystemMotionPreferences.AnimationsEnabled);
    }

    public void Populate(ArenaViewSnapshot snapshot, string? currentAgentId)
    {
        var readiness = ArenaOperationCoordinator.EvaluateReadiness(snapshot);
        participantActionsReady = readiness.CanRun;
        participantReadinessMessage = readiness.Message;
        narratorActionsReady = readiness.CanRun && readiness.CanNarrate;
        factoryMode = snapshot.FactoryMode;
        var agents = snapshot.Agents
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (agents.Length == 0)
        {
            Clear();
            agentItems.Children.Add(CreateAgentStatusCard("No agents", "No active snapshot", resourceBrush("ControlBorderBrush")));
            return;
        }

        var desiredCards = new List<UIElement>(agents.Length + 1);
        var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            desiredIds.Add(agent.Id);
            if (!agentCards.TryGetValue(agent.Id, out var view))
            {
                view = CreateAgentCardView(agent.Id);
                agentCards[agent.Id] = view;
                DiagnosticCardCreations++;
            }

            UpdateAgentCard(view, agent, currentAgentId);
            desiredCards.Add(view.Card);
        }

        foreach (var staleId in agentCards.Keys.Where(id => !desiredIds.Contains(id)).ToArray())
        {
            var stale = agentCards[staleId];
            stale.Menu.IsOpen = false;
            agentItems.Children.Remove(stale.Card);
            agentCards.Remove(staleId);
        }

        narratorCard ??= CreateNarratorCardView();
        if (narratorCard.Card.Parent is null)
        {
            DiagnosticCardCreations++;
        }
        UpdateNarratorCard(narratorCard, snapshot);
        desiredCards.Add(narratorCard.Card);
        ReconcileCards(desiredCards);
    }

    public void PopulateFallback()
    {
        Clear();
        agentItems.Children.Add(CreateAgentStatusCard("Alpha", "waiting", resourceBrush("AlphaAccentBrush")));
        agentItems.Children.Add(CreateAgentStatusCard("Beta", "waiting", resourceBrush("BetaAccentBrush")));
        agentItems.Children.Add(CreateAgentStatusCard("Gamma", "waiting", resourceBrush("GammaAccentBrush")));
        agentItems.Children.Add(CreateAgentStatusCard("Delta", "waiting", resourceBrush("DeltaAccentBrush")));
    }

    public void UpdateBusyState(bool busy)
    {
        var modeActionEnabled = ModeActionEnabled(busy, isAutoChatRunning());
        foreach (var view in agentCards.Values)
        {
            view.PrimaryButton.IsEnabled = view.IsPaused
                ? modeActionEnabled
                : !busy && participantActionsReady;
            view.PauseItem.IsEnabled = modeActionEnabled;
            view.SoloItem.IsEnabled = modeActionEnabled;
            view.SourcesItem.IsEnabled = view.HasSources;
            view.OverflowButton.IsEnabled = view.HasSources || modeActionEnabled;
        }
        if (narratorCard is not null)
        {
            narratorCard.PlayButton.IsEnabled = modeActionEnabled && narratorActionsReady;
        }
    }

    public void RefreshMotionPreference()
    {
        foreach (var view in agentCards.Values)
        {
            RefreshActivitySweep(view, view.IsRunning, view.ActivityAccent);
        }

        if (narratorCard is not null)
        {
            RefreshActivitySweep(narratorCard, narratorCard.IsRunning, narratorCard.ActivityAccent);
        }
    }

    private void Clear()
    {
        foreach (var view in agentCards.Values)
        {
            view.Menu.IsOpen = false;
        }
        agentItems.Children.Clear();
        agentCards.Clear();
        narratorCard = null;
    }

    private Border CreateAgentStatusCard(string title, string status, Brush accent)
    {
        var card = new Border
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title
        });
        text.Children.Add(new TextBlock
        {
            Text = status,
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = status
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        card.Child = grid;
        return card;
    }

    private AgentCardView CreateAgentCardView(string agentId)
    {
        AgentCardView? view = null;
        var title = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var subtitle = new TextBlock
        {
            FontSize = 10.5,
            LineHeight = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(subtitle);

        var primaryButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE768", 12),
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 13
        };
        primaryButton.Click += async (_, _) =>
        {
            var current = view?.CurrentAgent;
            if (current is null)
            {
                return;
            }
            if (view!.IsPaused)
            {
                await SetAgentMuteAsync(view.AgentId, mute: false);
            }
            else
            {
                await runAgentTurnAsync(current);
            }
        };

        var overflowButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE712", 12),
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0)
        };
        var menu = new ContextMenu
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = overflowButton,
            MinWidth = 184,
            Padding = new Thickness(3)
        };
        var pauseItem = CreateAgentMenuItem("Pause agent", "Pause agent", "Pause this agent without changing other agents.");
        var soloItem = CreateAgentMenuItem("Solo agent", "Solo agent", "Mute other agents and keep this agent active.");
        var sourceSeparator = new Separator();
        var sourcesItem = CreateAgentMenuItem("Internet sources", "Show internet sources", "Shows this agent's latest internet sources.");
        pauseItem.Click += async (_, _) => await SetAgentMuteAsync(view!.AgentId, mute: true);
        soloItem.Click += async (_, _) => await SoloAgentAsync(view!.AgentId);
        sourcesItem.Click += (_, _) =>
        {
            if (view?.CurrentAgent?.InternetSources is { Sources.Count: > 0 } sources)
            {
                AgentInternetSourcesPresenter.ShowSourcesPopup(overflowButton, sources, resourceBrush, blendBrush);
            }
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add(soloItem);
        menu.Items.Add(sourceSeparator);
        menu.Items.Add(sourcesItem);
        overflowButton.ContextMenu = menu;
        overflowButton.Click += (_, e) =>
        {
            e.Handled = true;
            menu.PlacementTarget = overflowButton;
            menu.IsOpen = true;
        };
        menu.Opened += (_, _) => menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.IsEnabled && item.Visibility == Visibility.Visible)
            ?.Focus();

        var content = new Grid { Margin = new Thickness(4, 0, 0, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(overflowButton, 1);
        Grid.SetColumn(primaryButton, 2);
        content.Children.Add(text);
        content.Children.Add(overflowButton);
        content.Children.Add(primaryButton);

        var accentStrip = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            IsHitTestVisible = false
        };
        var cardLayer = new Grid();
        cardLayer.Children.Add(accentStrip);
        cardLayer.Children.Add(content);
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 5),
            ClipToBounds = true,
            Child = cardLayer
        };

        view = new AgentCardView(
            agentId,
            card,
            cardLayer,
            accentStrip,
            title,
            subtitle,
            primaryButton,
            overflowButton,
            menu,
            pauseItem,
            soloItem,
            sourceSeparator,
            sourcesItem);
        return view;
    }

    private void UpdateAgentCard(AgentCardView view, AgentState agent, string? currentAgentId)
    {
        DiagnosticCardUpdates++;
        view.CurrentAgent = agent;
        var isActive = agent.Active;
        var isPaused = !isActive || agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase);
        var isCurrent = isActive && string.Equals(agent.Id, currentAgentId, StringComparison.OrdinalIgnoreCase);
        var isRunning = isActive && IsAgentWorkingStatus(agent.Status);
        var activityLabel = isRunning ? "thinking" : isCurrent ? "current" : isPaused ? "paused" : "waiting";
        var identityAccent = accentForSpeaker(agent.Id);
        var stateAccent = resourceBrush("PrimaryBorderBrush");
        var modelText = string.IsNullOrWhiteSpace(agent.Model) ? "model not set" : agent.Model;

        view.IsPaused = isPaused;
        view.Title.Text = displayStatusValue(agent.Id);
        view.Title.Foreground = resourceBrush(isPaused ? "MutedTextBrush" : "TextBrush");
        view.Title.ToolTip = agent.Name;
        view.Subtitle.Text = $"{activityLabel}  ·  {modelText}";
        view.Subtitle.Foreground = isPaused
            ? resourceBrush("DisabledTextBrush")
            : isRunning || isCurrent ? stateAccent : resourceBrush("MutedTextBrush");
        view.Subtitle.ToolTip = $"State: {activityLabel}\nModel: {modelText}\nName: {agent.Name}";
        view.Card.Background = isRunning
            ? blendBrush(resourceBrush("InputBrush"), stateAccent, 0.18)
            : isCurrent
                ? blendBrush(resourceBrush("InputBrush"), stateAccent, 0.1)
                : isPaused
                    ? blendBrush(resourceBrush("InputBrush"), resourceBrush("DisabledBorderBrush"), 0.12)
                    : resourceBrush("InputBrush");
        view.Card.BorderBrush = isPaused
            ? blendBrush(resourceBrush("DisabledBorderBrush"), resourceBrush("MutedTextBrush"), 0.18)
            : blendBrush(resourceBrush("DisabledBorderBrush"), isRunning || isCurrent ? stateAccent : identityAccent, isRunning || isCurrent ? 0.68 : 0.24);
        view.Card.ToolTip = !isActive
            ? $"{agent.Name} is paused. Choose Resume to return this agent to the active roster."
            : null;
        view.AccentStrip.Background = isPaused
            ? resourceBrush("DisabledBorderBrush")
            : isRunning || isCurrent ? stateAccent : identityAccent;
        view.AccentStrip.Opacity = isPaused ? 0.55 : isRunning || isCurrent ? 0.95 : 0.72;

        view.IsRunning = isRunning;
        view.ActivityAccent = stateAccent;
        RefreshActivitySweep(view, isRunning, stateAccent);

        view.PrimaryButton.IsEnabled = !string.IsNullOrWhiteSpace(agent.Id)
            && (isPaused
                ? ModeActionEnabled(isArenaBusy(), isAutoChatRunning())
                : !isArenaBusy() && participantActionsReady);
        view.PrimaryButton.Background = isPaused
            ? blendBrush(resourceBrush("InputBrush"), resourceBrush("Arena.Brush.Warning"), 0.24)
            : isCurrent ? resourceBrush("PrimaryBrush") : blendBrush(resourceBrush("InputBrush"), identityAccent, 0.10);
        view.PrimaryButton.BorderBrush = isPaused
            ? resourceBrush("Arena.Brush.Warning")
            : isCurrent ? resourceBrush("PrimaryBorderBrush") : blendBrush(resourceBrush("DisabledBorderBrush"), identityAccent, 0.28);
        view.PrimaryButton.Foreground = isPaused ? resourceBrush("Arena.Brush.Warning") : resourceBrush("TextBrush");
        var primaryHelp = isPaused
            ? $"Returns {agent.Name} to the active roster."
            : participantActionsReady ? $"Runs one turn for {agent.Name}." : ParticipantRunUnavailableHelp();
        view.PrimaryButton.ToolTip = isPaused
            ? $"Resume {agent.Name}"
            : participantActionsReady
                ? $"Run one turn for {agent.Name}"
                : ParticipantRunUnavailableHelp();
        SetButtonAutomation(view.PrimaryButton, isPaused ? $"Resume {agent.Name}" : $"Run one turn for {agent.Name}", primaryHelp);

        view.OverflowButton.Background = blendBrush(resourceBrush("InputBrush"), resourceBrush("DisabledBorderBrush"), 0.16);
        view.OverflowButton.BorderBrush = resourceBrush("DisabledBorderBrush");
        view.OverflowButton.Foreground = resourceBrush("MutedTextBrush");
        view.OverflowButton.ToolTip = $"More actions for {agent.Name}";
        SetButtonAutomation(view.OverflowButton, $"More actions for {agent.Name}", $"Opens pause, solo, and available source actions for {agent.Name}.");
        view.Menu.Background = resourceBrush("CardBrush");
        view.Menu.BorderBrush = resourceBrush("ControlBorderBrush");
        view.Menu.Foreground = resourceBrush("TextBrush");
        AutomationProperties.SetName(view.Menu, $"Actions for {agent.Name}");
        if (isPaused && view.Menu.Items.Contains(view.PauseItem))
        {
            view.Menu.Items.Remove(view.PauseItem);
        }
        else if (!isPaused && !view.Menu.Items.Contains(view.PauseItem))
        {
            view.Menu.Items.Insert(0, view.PauseItem);
        }
        view.PauseItem.Visibility = Visibility.Visible;
        view.PauseItem.IsEnabled = ModeActionEnabled(isArenaBusy(), isAutoChatRunning());
        view.PauseItem.ToolTip = $"Pause {agent.Name} without changing other agents.";
        AutomationProperties.SetName(view.PauseItem, $"Pause {agent.Name}");
        AutomationProperties.SetHelpText(view.PauseItem, $"Pause {agent.Name} without changing other agents.");
        view.SoloItem.IsEnabled = ModeActionEnabled(isArenaBusy(), isAutoChatRunning());
        view.SoloItem.ToolTip = $"Mute other agents and keep {agent.Name} active.";
        AutomationProperties.SetName(view.SoloItem, $"Solo {agent.Name}");
        AutomationProperties.SetHelpText(view.SoloItem, $"Mute other agents and keep {agent.Name} active.");
        view.HasSources = agent.InternetSources is { Sources.Count: > 0 };
        view.SourceSeparator.Visibility = view.HasSources ? Visibility.Visible : Visibility.Collapsed;
        view.SourcesItem.Visibility = view.HasSources ? Visibility.Visible : Visibility.Collapsed;
        view.SourcesItem.IsEnabled = view.HasSources;
        view.SourcesItem.Header = view.HasSources ? $"Internet sources ({agent.InternetSources!.Sources.Count})" : "Internet sources";
        AutomationProperties.SetName(view.SourcesItem, $"Show internet sources for {agent.Name}");
        AutomationProperties.SetHelpText(view.SourcesItem, $"Shows the sources found by {agent.Name}'s latest internet search.");
        view.OverflowButton.IsEnabled = view.HasSources || ModeActionEnabled(isArenaBusy(), isAutoChatRunning());
    }

    private MenuItem CreateAgentMenuItem(string header, string automationName, string helpText)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            MinHeight = 32,
            Padding = new Thickness(10, 4, 10, 4),
            Foreground = resourceBrush("TextBrush"),
            ToolTip = helpText
        };
        AutomationProperties.SetName(menuItem, automationName);
        AutomationProperties.SetHelpText(menuItem, helpText);
        return menuItem;
    }

    private static TextBlock CreateAgentButtonGlyph(string glyph, double fontSize)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
    }

    private static void SetButtonAutomation(Button button, string name, string helpText)
    {
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, helpText);
    }

    private async Task SetAgentMuteAsync(string agentId, bool mute)
    {
        await runArenaBusyAsync(mute ? $"Muting {agentId}..." : $"Activating {agentId}...", null, async () =>
        {
            var session = activeSession();
            if (session is null)
            {
                return;
            }

            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            var agent = snapshot?.Engine.Agents.FirstOrDefault(item => item.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null || agent is null)
            {
                setArenaRunStatus($"Agent {agentId} not found.");
                return;
            }

            agent.Active = !mute;
            if (!agent.Active)
            {
                agent.Status = "muted";
            }
            else if (agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(agent.Status))
            {
                agent.Status = "waiting";
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_active_changed", new { Agent = agentId, Active = agent.Active });
            await refreshActiveSessionAsync(agent.Active ? $"{displayStatusValue(agentId)} activated." : $"{displayStatusValue(agentId)} muted.");
        }, true);
    }

    private async Task SoloAgentAsync(string agentId)
    {
        await runArenaBusyAsync($"Soloing {agentId}...", null, async () =>
        {
            var session = activeSession();
            if (session is null)
            {
                return;
            }

            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                return;
            }

            foreach (var agent in snapshot.Engine.Agents)
            {
                var selected = agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase);
                agent.Active = selected;
                agent.Status = selected ? "waiting" : "muted";
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_solo_enabled", new { Agent = agentId });
            await refreshActiveSessionAsync($"{displayStatusValue(agentId)} solo enabled.");
        }, true);
    }

    private NarratorCardView CreateNarratorCardView()
    {
        var playButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE768", 12),
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 13
        };
        playButton.Click += narrateNowHandler;
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 5),
            ClipToBounds = true
        };
        var cardLayer = new Grid();
        var accentStrip = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            IsHitTestVisible = false
        };
        cardLayer.Children.Add(accentStrip);
        var grid = new Grid
        {
            Margin = new Thickness(4, 0, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = "Narrator",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Narrator"
        };
        var subtitle = new TextBlock
        {
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Children.Add(title);
        text.Children.Add(subtitle);
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(playButton, 1);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        return new NarratorCardView(card, cardLayer, accentStrip, title, subtitle, playButton);
    }

    private void UpdateNarratorCard(NarratorCardView view, ArenaViewSnapshot snapshot)
    {
        DiagnosticCardUpdates++;
        var accent = resourceBrush("NarratorAccentBrush");
        var isRunning = IsAgentWorkingStatus(snapshot.NarratorStatus);
        var modelText = string.IsNullOrWhiteSpace(snapshot.NarratorModel) ? "model not set" : snapshot.NarratorModel;
        var status = string.IsNullOrWhiteSpace(snapshot.NarratorStatus) ? "idle" : snapshot.NarratorStatus;
        var narratorHelp = factoryMode
            ? "Narration is unavailable in Factory mode. Turn Apply Match Setup to models on to use narrator guidance."
            : "Narrate now. Ask the narrator to speak without advancing the participant turn order.";

        view.Card.Background = isRunning
            ? blendBrush(resourceBrush("InputBrush"), accent, 0.18)
            : resourceBrush("InputBrush");
        view.Card.BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, isRunning ? 0.68 : 0.24);
        view.Card.ToolTip = string.IsNullOrWhiteSpace(snapshot.NarratorPersona) ? "Narrator" : snapshot.NarratorPersona;
        view.AccentStrip.Background = accent;
        view.AccentStrip.Opacity = isRunning ? 0.95 : 0.72;
        view.Title.Foreground = resourceBrush("TextBrush");
        view.Subtitle.Text = $"{DisplayInlineStatus(status)}  ·  {modelText}";
        view.Subtitle.Foreground = isRunning ? accent : resourceBrush("MutedTextBrush");
        view.Subtitle.ToolTip = modelText;
        view.PlayButton.IsEnabled = (!isArenaBusy() || isAutoChatRunning()) && narratorActionsReady;
        view.PlayButton.Background = blendBrush(resourceBrush("InputBrush"), accent, 0.5);
        view.PlayButton.BorderBrush = accent;
        view.PlayButton.Foreground = resourceBrush("TextBrush");
        view.PlayButton.ToolTip = narratorHelp;
        SetButtonAutomation(view.PlayButton, "Narrate now", narratorHelp);

        view.IsRunning = isRunning;
        view.ActivityAccent = accent;
        RefreshActivitySweep(view, isRunning, accent);
    }

    private void RefreshActivitySweep(AgentCardView view, bool isRunning, Brush accent)
    {
        var shouldAnimate = ShouldAnimateActivity(animationsEnabled(), isRunning);
        var accentColor = BrushColor(accent, Colors.DeepSkyBlue);
        if (!isRunning)
        {
            if (view.ActivitySweep is not null)
            {
                view.CardLayer.Children.Remove(view.ActivitySweep);
                view.ActivitySweep = null;
            }
            view.ActivitySweepAnimated = false;
            view.ActivitySweepColor = default;
            return;
        }

        if (view.ActivitySweep is not null
            && view.ActivitySweepAnimated == shouldAnimate
            && view.ActivitySweepColor == accentColor)
        {
            return;
        }

        if (view.ActivitySweep is not null)
        {
            view.CardLayer.Children.Remove(view.ActivitySweep);
        }
        view.ActivitySweep = CreateAgentActivitySweep(accent, isRunning: true);
        view.ActivitySweepAnimated = shouldAnimate;
        view.ActivitySweepColor = accentColor;
        view.CardLayer.Children.Insert(1, view.ActivitySweep);
    }

    private void RefreshActivitySweep(NarratorCardView view, bool isRunning, Brush accent)
    {
        var shouldAnimate = ShouldAnimateActivity(animationsEnabled(), isRunning);
        var accentColor = BrushColor(accent, Colors.DeepSkyBlue);
        if (!isRunning)
        {
            if (view.ActivitySweep is not null)
            {
                view.CardLayer.Children.Remove(view.ActivitySweep);
                view.ActivitySweep = null;
            }
            view.ActivitySweepAnimated = false;
            view.ActivitySweepColor = default;
            return;
        }

        if (view.ActivitySweep is not null
            && view.ActivitySweepAnimated == shouldAnimate
            && view.ActivitySweepColor == accentColor)
        {
            return;
        }

        if (view.ActivitySweep is not null)
        {
            view.CardLayer.Children.Remove(view.ActivitySweep);
        }
        view.ActivitySweep = CreateAgentActivitySweep(accent, isRunning: true);
        view.ActivitySweepAnimated = shouldAnimate;
        view.ActivitySweepColor = accentColor;
        view.CardLayer.Children.Insert(1, view.ActivitySweep);
    }

    private void ReconcileCards(IReadOnlyList<UIElement> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var card = desired[index];
            if (index < agentItems.Children.Count && ReferenceEquals(agentItems.Children[index], card))
            {
                continue;
            }
            var oldIndex = agentItems.Children.IndexOf(card);
            if (oldIndex >= 0)
            {
                agentItems.Children.RemoveAt(oldIndex);
            }
            agentItems.Children.Insert(index, card);
        }
        while (agentItems.Children.Count > desired.Count)
        {
            agentItems.Children.RemoveAt(agentItems.Children.Count - 1);
        }
    }

    public static string DisplayInlineStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "-" : status.Trim().ToLowerInvariant();
    }

    internal static bool ModeActionEnabled(bool busy, bool autoChatRunning)
    {
        return !busy || autoChatRunning;
    }

    private string ParticipantRunUnavailableHelp()
    {
        return string.IsNullOrWhiteSpace(participantReadinessMessage)
            ? "Complete provider, model, and cast setup before running this model."
            : participantReadinessMessage;
    }

    internal static bool ShouldAnimateActivity(bool systemAnimationsEnabled, bool isRunning)
    {
        return systemAnimationsEnabled && isRunning;
    }

    public static bool IsAgentWorkingStatus(string status)
    {
        var normalized = status.Trim().ToLowerInvariant();
        return normalized is "thinking" or "generating" or "running" or "working" or "busy";
    }

    private Border CreateAgentActivitySweep(Brush accent, bool isRunning)
    {
        var accentColor = BrushColor(accent, Colors.DeepSkyBlue);
        var sweep = new Border
        {
            Width = 86,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Opacity = isRunning ? 0.92 : 0.56,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Transparent, 0),
                    new(Color.FromArgb(isRunning ? (byte)74 : (byte)38, accentColor.R, accentColor.G, accentColor.B), 0.48),
                    new(Colors.Transparent, 1)
                },
                new Point(0, 0.5),
                new Point(1, 0.5))
        };

        var translate = new TranslateTransform(-110, 0);
        sweep.RenderTransform = translate;
        if (!ShouldAnimateActivity(animationsEnabled(), isRunning))
        {
            sweep.Width = 52;
            sweep.Opacity = isRunning ? 0.28 : 0.18;
            translate.X = 0;
            return sweep;
        }

        var animation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-110, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 1350 : 1900))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(230, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(isRunning ? 2050 : 3000))));
        translate.BeginAnimation(TranslateTransform.XProperty, animation);
        return sweep;
    }

    private static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private sealed class AgentCardView(
        string agentId,
        Border card,
        Grid cardLayer,
        Border accentStrip,
        TextBlock title,
        TextBlock subtitle,
        Button primaryButton,
        Button overflowButton,
        ContextMenu menu,
        MenuItem pauseItem,
        MenuItem soloItem,
        Separator sourceSeparator,
        MenuItem sourcesItem)
    {
        public string AgentId { get; } = agentId;
        public Border Card { get; } = card;
        public Grid CardLayer { get; } = cardLayer;
        public Border AccentStrip { get; } = accentStrip;
        public TextBlock Title { get; } = title;
        public TextBlock Subtitle { get; } = subtitle;
        public Button PrimaryButton { get; } = primaryButton;
        public Button OverflowButton { get; } = overflowButton;
        public ContextMenu Menu { get; } = menu;
        public MenuItem PauseItem { get; } = pauseItem;
        public MenuItem SoloItem { get; } = soloItem;
        public Separator SourceSeparator { get; } = sourceSeparator;
        public MenuItem SourcesItem { get; } = sourcesItem;
        public AgentState? CurrentAgent { get; set; }
        public bool IsPaused { get; set; }
        public bool HasSources { get; set; }
        public bool IsRunning { get; set; }
        public Brush ActivityAccent { get; set; } = Brushes.Transparent;
        public bool ActivitySweepAnimated { get; set; }
        public Color ActivitySweepColor { get; set; }
        public FrameworkElement? ActivitySweep { get; set; }
    }

    private sealed class NarratorCardView(
        Border card,
        Grid cardLayer,
        Border accentStrip,
        TextBlock title,
        TextBlock subtitle,
        Button playButton)
    {
        public Border Card { get; } = card;
        public Grid CardLayer { get; } = cardLayer;
        public Border AccentStrip { get; } = accentStrip;
        public TextBlock Title { get; } = title;
        public TextBlock Subtitle { get; } = subtitle;
        public Button PlayButton { get; } = playButton;
        public bool IsRunning { get; set; }
        public Brush ActivityAccent { get; set; } = Brushes.Transparent;
        public bool ActivitySweepAnimated { get; set; }
        public Color ActivitySweepColor { get; set; }
        public FrameworkElement? ActivitySweep { get; set; }
    }
}
