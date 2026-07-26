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
    private readonly List<Button> agentTurnButtons = [];
    private readonly List<Button> agentModeButtons = [];
    private readonly List<MenuItem> agentModeMenuItems = [];
    private readonly List<(Button Button, bool HasPersistentAction)> agentOverflowButtons = [];
    private readonly List<Button> narratorActionButtons = [];

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
        var agents = snapshot.Agents;
        Clear();

        if (agents.Count == 0)
        {
            agentItems.Children.Add(CreateAgentStatusCard("No agents", "No active snapshot", resourceBrush("ControlBorderBrush")));
            return;
        }

        foreach (var agent in agents)
        {
            agentItems.Children.Add(CreateAgentCard(agent, currentAgentId));
        }

        agentItems.Children.Add(CreateNarratorCard(snapshot));
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
        foreach (var button in agentTurnButtons)
        {
            button.IsEnabled = !busy;
        }
        foreach (var button in agentModeButtons)
        {
            button.IsEnabled = ModeActionEnabled(busy, isAutoChatRunning());
        }

        var modeActionEnabled = ModeActionEnabled(busy, isAutoChatRunning());
        foreach (var menuItem in agentModeMenuItems)
        {
            menuItem.IsEnabled = modeActionEnabled;
        }

        foreach (var (button, hasPersistentAction) in agentOverflowButtons)
        {
            button.IsEnabled = hasPersistentAction || modeActionEnabled;
        }

        foreach (var button in narratorActionButtons)
        {
            button.IsEnabled = ModeActionEnabled(busy, isAutoChatRunning());
        }
    }

    private void Clear()
    {
        agentItems.Children.Clear();
        agentTurnButtons.Clear();
        agentModeButtons.Clear();
        agentModeMenuItems.Clear();
        agentOverflowButtons.Clear();
        narratorActionButtons.Clear();
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

    private Border CreateAgentCard(AgentState agent, string? currentAgentId)
    {
        var isActive = agent.Active;
        var isPaused = !isActive || agent.Status.Equals("muted", StringComparison.OrdinalIgnoreCase);
        var isCurrent = isActive && string.Equals(agent.Id, currentAgentId, StringComparison.OrdinalIgnoreCase);
        var isWorkingStatus = IsAgentWorkingStatus(agent.Status);
        var isRunning = isActive && isWorkingStatus;
        var showActivitySweep = isRunning;
        var speakerLabel = displayStatusValue(agent.Id);
        var activityLabel = isRunning ? "thinking" : isCurrent ? "current" : isPaused ? "paused" : "waiting";
        var identityAccent = accentForSpeaker(agent.Id);
        var primaryActionButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE768", 12),
            IsEnabled = !string.IsNullOrWhiteSpace(agent.Id)
                && (isPaused
                    ? ModeActionEnabled(isArenaBusy(), isAutoChatRunning())
                    : !isArenaBusy()),
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 13,
            Background = isPaused
                ? blendBrush(resourceBrush("InputBrush"), resourceBrush("Arena.Brush.Warning"), 0.24)
                : isCurrent
                    ? resourceBrush("PrimaryBrush")
                    : blendBrush(resourceBrush("InputBrush"), identityAccent, 0.10),
            BorderBrush = isPaused
                ? resourceBrush("Arena.Brush.Warning")
                : isCurrent
                    ? resourceBrush("PrimaryBorderBrush")
                    : blendBrush(resourceBrush("DisabledBorderBrush"), identityAccent, 0.28),
            Foreground = isPaused ? resourceBrush("Arena.Brush.Warning") : resourceBrush("TextBrush"),
            ToolTip = isPaused
                ? $"Resume {agent.Name}"
                : $"Run one turn for {agent.Name}"
        };
        SetButtonAutomation(
            primaryActionButton,
            isPaused ? $"Resume {agent.Name}" : $"Run one turn for {agent.Name}",
            isPaused
                ? $"Returns {agent.Name} to the active roster."
                : $"Runs one turn for {agent.Name}.");
        if (isPaused)
        {
            primaryActionButton.Click += async (_, _) => await SetAgentMuteAsync(agent.Id, mute: false);
            agentModeButtons.Add(primaryActionButton);
        }
        else
        {
            primaryActionButton.Click += async (_, _) => await runAgentTurnAsync(agent);
            agentTurnButtons.Add(primaryActionButton);
        }

        var stateAccent = resourceBrush("PrimaryBorderBrush");
        var card = new Border
        {
            Background = isRunning
                ? blendBrush(resourceBrush("InputBrush"), stateAccent, 0.18)
                : isCurrent
                    ? blendBrush(resourceBrush("InputBrush"), stateAccent, 0.1)
                    : isPaused
                        ? blendBrush(resourceBrush("InputBrush"), resourceBrush("DisabledBorderBrush"), 0.12)
                        : resourceBrush("InputBrush"),
            BorderBrush = isPaused
                ? blendBrush(resourceBrush("DisabledBorderBrush"), resourceBrush("MutedTextBrush"), 0.18)
                : blendBrush(
                    resourceBrush("DisabledBorderBrush"),
                    isRunning || isCurrent ? stateAccent : identityAccent,
                    isRunning || isCurrent ? 0.68 : 0.24),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 5),
            ClipToBounds = true
        };

        var cardLayer = new Grid();
        cardLayer.Children.Add(new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = isPaused
                ? resourceBrush("DisabledBorderBrush")
                : isRunning || isCurrent ? stateAccent : identityAccent,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            Opacity = isPaused ? 0.55 : isRunning || isCurrent ? 0.95 : 0.72,
            IsHitTestVisible = false
        });
        if (showActivitySweep)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(stateAccent, isRunning));
        }

        var grid = new Grid
        {
            Margin = new Thickness(4, 0, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = speakerLabel,
            Foreground = resourceBrush(isPaused ? "MutedTextBrush" : "TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = agent.Name
        });
        var modelText = string.IsNullOrWhiteSpace(agent.Model) ? "model not set" : agent.Model;
        text.Children.Add(new TextBlock
        {
            Text = $"{activityLabel}  ·  {modelText}",
            Foreground = isPaused
                ? resourceBrush("DisabledTextBrush")
                : isRunning || isCurrent
                ? stateAccent
                : resourceBrush("MutedTextBrush"),
            FontSize = 10.5,
            LineHeight = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"State: {activityLabel}\nModel: {modelText}\nName: {agent.Name}"
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var overflowButton = CreateAgentOverflowButton(agent, isPaused);
        Grid.SetColumn(overflowButton, 1);
        grid.Children.Add(overflowButton);

        Grid.SetColumn(primaryActionButton, 2);
        grid.Children.Add(primaryActionButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        if (!isActive)
        {
            card.ToolTip = $"{agent.Name} is paused. Choose Resume to return this agent to the active roster.";
        }

        return card;
    }

    private Button CreateAgentOverflowButton(AgentState agent, bool isPaused)
    {
        var overflowButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE712", 12),
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = blendBrush(resourceBrush("InputBrush"), resourceBrush("DisabledBorderBrush"), 0.16),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            Foreground = resourceBrush("MutedTextBrush"),
            ToolTip = $"More actions for {agent.Name}"
        };
        SetButtonAutomation(
            overflowButton,
            $"More actions for {agent.Name}",
            $"Opens pause, solo, and available source actions for {agent.Name}.");

        var menu = new ContextMenu
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = overflowButton,
            MinWidth = 184,
            Padding = new Thickness(3),
            Background = resourceBrush("CardBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Foreground = resourceBrush("TextBrush")
        };
        AutomationProperties.SetName(menu, $"Actions for {agent.Name}");

        if (!isPaused)
        {
            var pauseItem = CreateAgentModeMenuItem(
                "Pause agent",
                $"Pause {agent.Name}",
                $"Pause {agent.Name} without changing other agents.");
            pauseItem.Click += async (_, _) => await SetAgentMuteAsync(agent.Id, mute: true);
            menu.Items.Add(pauseItem);
        }

        var soloItem = CreateAgentModeMenuItem(
            "Solo agent",
            $"Solo {agent.Name}",
            $"Mute other agents and keep {agent.Name} active.");
        soloItem.Click += async (_, _) => await SoloAgentAsync(agent.Id);
        menu.Items.Add(soloItem);

        var hasSources = agent.InternetSources is { Sources.Count: > 0 };
        if (agent.InternetSources is { Sources.Count: > 0 } internetSources)
        {
            menu.Items.Add(new Separator());
            var sourcesItem = CreateAgentMenuItem(
                $"Internet sources ({internetSources.Sources.Count})",
                $"Show internet sources for {agent.Name}",
                $"Shows the sources found by {agent.Name}'s latest internet search.");
            sourcesItem.Click += (_, _) => AgentInternetSourcesPresenter.ShowSourcesPopup(
                overflowButton,
                internetSources,
                resourceBrush,
                blendBrush);
            menu.Items.Add(sourcesItem);
        }

        overflowButton.ContextMenu = menu;
        overflowButton.Click += (_, e) =>
        {
            e.Handled = true;
            menu.PlacementTarget = overflowButton;
            menu.IsOpen = true;
        };
        menu.Opened += (_, _) => menu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(item => item.IsEnabled)
            ?.Focus();

        agentOverflowButtons.Add((overflowButton, hasSources));
        return overflowButton;
    }

    private MenuItem CreateAgentModeMenuItem(string header, string automationName, string helpText)
    {
        var menuItem = CreateAgentMenuItem(header, automationName, helpText);
        menuItem.IsEnabled = ModeActionEnabled(isArenaBusy(), isAutoChatRunning());
        agentModeMenuItems.Add(menuItem);
        return menuItem;
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

    private Border CreateNarratorCard(ArenaViewSnapshot snapshot)
    {
        var accent = resourceBrush("NarratorAccentBrush");
        var isRunning = IsAgentWorkingStatus(snapshot.NarratorStatus);
        var modelText = string.IsNullOrWhiteSpace(snapshot.NarratorModel) ? "model not set" : snapshot.NarratorModel;
        var status = string.IsNullOrWhiteSpace(snapshot.NarratorStatus) ? "idle" : snapshot.NarratorStatus;
        var buttonEnabled = !isArenaBusy() || isAutoChatRunning();
        var playButton = new Button
        {
            Content = CreateAgentButtonGlyph("\uE768", 12),
            IsEnabled = buttonEnabled,
            Width = 32,
            MinWidth = 32,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 13,
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.5),
            BorderBrush = accent,
            Foreground = resourceBrush("TextBrush"),
            ToolTip = "Narrate now"
        };
        SetButtonAutomation(playButton, "Narrate now", "Ask the narrator to speak without advancing the participant turn order.");
        playButton.Click += narrateNowHandler;
        narratorActionButtons.Add(playButton);

        var card = new Border
        {
            Background = isRunning
                ? blendBrush(resourceBrush("InputBrush"), accent, 0.18)
                : resourceBrush("InputBrush"),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, isRunning ? 0.68 : 0.24),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 5),
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(snapshot.NarratorPersona)
                ? "Narrator"
                : snapshot.NarratorPersona
        };

        var cardLayer = new Grid();
        cardLayer.Children.Add(new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent,
            CornerRadius = new CornerRadius(7, 0, 0, 7),
            Opacity = isRunning ? 0.95 : 0.72,
            IsHitTestVisible = false
        });
        if (isRunning)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(accent, isRunning: true));
        }

        var grid = new Grid
        {
            Margin = new Thickness(4, 0, 0, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = "Narrator",
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Narrator"
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{DisplayInlineStatus(status)}  ·  {modelText}",
            Foreground = isRunning ? accent : resourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = modelText
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        Grid.SetColumn(playButton, 1);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        return card;
    }

    public static string DisplayInlineStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "-" : status.Trim().ToLowerInvariant();
    }

    internal static bool ModeActionEnabled(bool busy, bool autoChatRunning)
    {
        return !busy || autoChatRunning;
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
}
