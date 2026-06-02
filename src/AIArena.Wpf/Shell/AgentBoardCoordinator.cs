using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
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
    private readonly List<Button> agentTurnButtons = [];
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
        Action<string> setArenaRunStatus)
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
        foreach (var button in narratorActionButtons)
        {
            button.IsEnabled = !busy || isAutoChatRunning();
        }
    }

    private void Clear()
    {
        agentItems.Children.Clear();
        agentTurnButtons.Clear();
        narratorActionButtons.Clear();
    }

    private Border CreateAgentStatusCard(string title, string status, Brush accent)
    {
        var card = new Border
        {
            Background = resourceBrush("CardBrush"),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 6)
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
        var playButton = new Button
        {
            Content = "▶",
            IsEnabled = isActive && !isArenaBusy() && !string.IsNullOrWhiteSpace(agent.Id),
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = isActive ? resourceBrush("PrimaryBrush") : resourceBrush("DisabledBrush"),
            BorderBrush = isActive ? resourceBrush("PrimaryBorderBrush") : resourceBrush("DisabledBorderBrush"),
            Foreground = isActive ? Brushes.White : resourceBrush("DisabledTextBrush"),
            ToolTip = isActive
                ? $"Run one turn for {agent.Name}"
                : "Inactive: increase Active participants in App Settings to include this agent."
        };
        playButton.Click += async (_, _) => await runAgentTurnAsync(agent);
        agentTurnButtons.Add(playButton);

        var identityAccent = accentForSpeaker(agent.Id);
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
                : blendBrush(resourceBrush("DisabledBorderBrush"), isRunning || isCurrent ? stateAccent : identityAccent, 0.75),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true
        };

        var cardLayer = new Grid();
        if (showActivitySweep)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(stateAccent, isRunning));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"{speakerLabel} - {activityLabel}",
            Foreground = isPaused ? resourceBrush("MutedTextBrush") : Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = agent.Name
        });
        var modelText = string.IsNullOrWhiteSpace(agent.Model) ? "model not set" : agent.Model;
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isPaused
                ? resourceBrush("DisabledTextBrush")
                : isRunning || isCurrent
                ? stateAccent
                : resourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"{modelText}\n{agent.Name}"
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = isPaused
                ? resourceBrush("DisabledBorderBrush")
                : isRunning || isCurrent ? stateAccent : identityAccent,
            Opacity = isRunning ? 1.0 : isCurrent ? 0.85 : isPaused ? 0.35 : 0.45,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = activityLabel
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        var routeControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        var muteButton = CreateAgentModeButton("⏸", isActive ? "Pause agent" : "Activate paused agent", isPaused);
        muteButton.Click += async (_, _) => await SetAgentMuteAsync(agent.Id, mute: isActive);
        routeControls.Children.Add(muteButton);
        var soloButton = CreateAgentModeButton("S", "Solo this agent");
        soloButton.Click += async (_, _) => await SoloAgentAsync(agent.Id);
        routeControls.Children.Add(soloButton);
        Grid.SetColumn(routeControls, 2);
        grid.Children.Add(routeControls);

        Grid.SetColumn(playButton, 3);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        if (!isActive)
        {
            card.ToolTip = "Paused: click the pause button to activate this agent.";
        }

        return card;
    }

    private Button CreateAgentModeButton(string content, string tooltip, bool highlighted = false)
    {
        return new Button
        {
            Content = content,
            Width = 24,
            MinWidth = 24,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = content == "⏸" ? 12 : 9.5,
            FontFamily = content == "⏸" ? new FontFamily("Segoe UI Symbol") : SystemFonts.MessageFontFamily,
            Background = highlighted ? blendBrush(resourceBrush("InputBrush"), resourceBrush("BetaAccentBrush"), 0.28) : resourceBrush("InputBrush"),
            BorderBrush = highlighted ? resourceBrush("BetaAccentBrush") : resourceBrush("DisabledBorderBrush"),
            Foreground = highlighted ? resourceBrush("BetaAccentBrush") : resourceBrush("MutedTextBrush"),
            ToolTip = tooltip
        };
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
            Content = "▶",
            IsEnabled = buttonEnabled,
            Width = 30,
            MinWidth = 30,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 13,
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.5),
            BorderBrush = accent,
            Foreground = Brushes.White,
            ToolTip = "Narrate now"
        };
        playButton.Click += narrateNowHandler;
        narratorActionButtons.Add(playButton);

        var card = new Border
        {
            Background = isRunning
                ? blendBrush(resourceBrush("InputBrush"), accent, 0.18)
                : resourceBrush("InputBrush"),
            BorderBrush = blendBrush(resourceBrush("DisabledBorderBrush"), accent, 0.58),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 10, 8),
            Margin = new Thickness(0, -1, 0, 0),
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(snapshot.NarratorPersona)
                ? "Narrator"
                : snapshot.NarratorPersona
        };

        var cardLayer = new Grid();
        if (isRunning)
        {
            cardLayer.Children.Add(CreateAgentActivitySweep(accent, isRunning: true));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = $"Narrator - {DisplayInlineStatus(status)}",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            LineHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Narrator"
        });
        text.Children.Add(new TextBlock
        {
            Text = modelText,
            Foreground = isRunning ? accent : resourceBrush("MutedTextBrush"),
            FontSize = 11,
            LineHeight = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = modelText
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var activityDot = new Border
        {
            Width = isRunning ? 8 : 6,
            Height = isRunning ? 8 : 6,
            CornerRadius = new CornerRadius(4),
            Background = accent,
            Opacity = isRunning ? 1.0 : 0.45,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = status
        };
        Grid.SetColumn(activityDot, 1);
        grid.Children.Add(activityDot);

        Grid.SetColumn(playButton, 2);
        grid.Children.Add(playButton);

        cardLayer.Children.Add(grid);
        card.Child = cardLayer;
        return card;
    }

    public static string DisplayInlineStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "-" : status.Trim().ToLowerInvariant();
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
