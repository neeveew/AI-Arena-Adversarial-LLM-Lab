using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Controls;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class TranscriptListCoordinator
{
    internal enum EmptyStateAction
    {
        RunOneTurn,
        OpenMatchSetup,
        OpenProviderSettings,
        OpenModelSettings,
        ClearFilters
    }

    internal sealed record EmptyStatePresentation(
        string Eyebrow,
        string Title,
        string Body,
        string Status,
        IReadOnlyList<EmptyStateAction> Actions);

    // Row data for the virtualized transcript list. Adjunct rows wrap a pre-built panel;
    // card rows are lightweight descriptors whose card is built on demand when realized.
    private sealed record AdjunctRow(UIElement Element);

    private sealed record CardRow(TranscriptMessage Message, bool Retryable, bool SearchMatch, bool IsLatest);

    private readonly Dispatcher dispatcher;
    private readonly TranscriptListBox transcriptItems;
    private readonly CheckBox followChatCheckBox;
    private readonly ShellCardFactory shellCards;
    private readonly TranscriptActionCoordinator transcriptActions;
    private readonly TranscriptSearchCoordinator transcriptSearch;
    private readonly TranscriptInsightCoordinator transcriptInsight;
    private readonly TranscriptCardRenderer transcriptCards;
    private readonly TranscriptAdjunctCoordinator transcriptAdjunct;
    private readonly AgentMemoryCoordinator agentMemory;
    private readonly MatchQualityTimelineCoordinator matchQualityTimeline;
    private readonly Func<WpfSettings> settings;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Action<IReadOnlyList<TranscriptMessage>> setLastRenderedMessages;
    private readonly Func<bool> isDiagnosticsDisplayed;
    private readonly Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics;
    private readonly Func<bool> shouldShowDecisionCard;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> shortModelName;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<Task> runOneTurnAsync;
    private readonly Action openMatchSetup;
    private readonly Action openProviderSettings;
    private readonly Action clearFilters;

    public TranscriptListCoordinator(
        Dispatcher dispatcher,
        TranscriptListBox transcriptItems,
        CheckBox followChatCheckBox,
        ShellCardFactory shellCards,
        TranscriptActionCoordinator transcriptActions,
        TranscriptSearchCoordinator transcriptSearch,
        TranscriptInsightCoordinator transcriptInsight,
        TranscriptCardRenderer transcriptCards,
        TranscriptAdjunctCoordinator transcriptAdjunct,
        AgentMemoryCoordinator agentMemory,
        MatchQualityTimelineCoordinator matchQualityTimeline,
        Func<WpfSettings> settings,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Action<IReadOnlyList<TranscriptMessage>> setLastRenderedMessages,
        Func<bool> isDiagnosticsDisplayed,
        Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics,
        Func<bool> shouldShowDecisionCard,
        Func<string, bool> isAgentSpeaker,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> shortModelName,
        Func<string, string> displayStatusValue,
        Func<Task> runOneTurnAsync,
        Action openMatchSetup,
        Action openProviderSettings,
        Action clearFilters)
    {
        this.dispatcher = dispatcher;
        this.transcriptItems = transcriptItems;
        this.transcriptItems.ContentFactory = BuildRowContent;
        this.followChatCheckBox = followChatCheckBox;
        this.shellCards = shellCards;
        this.transcriptActions = transcriptActions;
        this.transcriptSearch = transcriptSearch;
        this.transcriptInsight = transcriptInsight;
        this.transcriptCards = transcriptCards;
        this.transcriptAdjunct = transcriptAdjunct;
        this.agentMemory = agentMemory;
        this.matchQualityTimeline = matchQualityTimeline;
        this.settings = settings;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.setLastRenderedMessages = setLastRenderedMessages;
        this.isDiagnosticsDisplayed = isDiagnosticsDisplayed;
        this.updateDiagnostics = updateDiagnostics;
        this.shouldShowDecisionCard = shouldShowDecisionCard;
        this.isAgentSpeaker = isAgentSpeaker;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.shortModelName = shortModelName;
        this.displayStatusValue = displayStatusValue;
        this.runOneTurnAsync = runOneTurnAsync;
        this.openMatchSetup = openMatchSetup;
        this.openProviderSettings = openProviderSettings;
        this.clearFilters = clearFilters;
    }

    public void Populate(IReadOnlyList<TranscriptMessage> messages)
    {
        setLastRenderedMessages(messages);
        transcriptActions.Clear();
        transcriptInsight.ClearTimelineFilterIfMissing(messages);

        var visibleMessages = transcriptSearch.FilterMessages(messages).ToArray();
        transcriptSearch.UpdateResultCount(visibleMessages.Length, messages.Count);
        transcriptSearch.UpdateSearchState();
        if (isDiagnosticsDisplayed())
        {
            updateDiagnostics(messages);
        }

        var currentSettings = settings();
        var snapshot = lastRenderedSnapshot();
        var rows = new List<object>();
        if (messages.Count == 0)
        {
            rows.Add(new AdjunctRow(CreateArenaReadyCard(snapshot)));
            AddMemoryRowIfNeeded(rows, currentSettings, snapshot);
            SetRows(rows, follow: false);
            return;
        }

        if (visibleMessages.Length == 0)
        {
            rows.Add(new AdjunctRow(CreateFilteredEmptyCard(messages.Count)));
            AddTimelineRowIfNeeded(rows, currentSettings, messages);
            AddMemoryRowIfNeeded(rows, currentSettings, snapshot);
            SetRows(rows, follow: false);
            return;
        }

        AddTimelineRowIfNeeded(rows, currentSettings, messages);
        if (currentSettings.ShowBattleReview)
        {
            rows.Add(new AdjunctRow(transcriptAdjunct.CreateBattleReviewPanel(messages)));
        }

        if (shouldShowDecisionCard() && snapshot is not null)
        {
            rows.Add(new AdjunctRow(transcriptAdjunct.CreateDecisionCardPanel(snapshot)));
        }
        if (currentSettings.TurnCompareMode)
        {
            transcriptInsight.EnsureTurnCompareSelection(visibleMessages);
            rows.Add(new AdjunctRow(transcriptAdjunct.CreateTurnComparePanel(visibleMessages)));
        }
        AddMemoryRowIfNeeded(rows, currentSettings, snapshot);
        if (currentSettings.ShowAutoModerator)
        {
            var moderatorPanel = transcriptAdjunct.CreateAutoModeratorPanel(messages);
            if (moderatorPanel is not null)
            {
                rows.Add(new AdjunctRow(moderatorPanel));
            }
        }

        var retryableTurns = RetryableTurns(visibleMessages, isAgentSpeaker);
        var latestTurn = visibleMessages.Max(message => message.Turn);
        var hasActiveSearch = transcriptSearch.HasActiveSearch;
        foreach (var message in visibleMessages.OrderByDescending(message => message.Turn))
        {
            rows.Add(new CardRow(
                message,
                retryableTurns.Contains(message.Turn),
                hasActiveSearch,
                message.Turn == latestTurn));
        }

        SetRows(rows, follow: followChatCheckBox.IsChecked == true);
    }

    private void SetRows(List<object> rows, bool follow)
    {
        transcriptItems.ItemsSource = rows;
        if (follow)
        {
            dispatcher.BeginInvoke(() => transcriptItems.ScrollToTop(), DispatcherPriority.Background);
        }
    }

    private UIElement BuildRowContent(object item)
    {
        return item switch
        {
            AdjunctRow adjunct => adjunct.Element,
            CardRow card => transcriptCards.CreateCard(card.Message, card.Retryable, card.SearchMatch, card.IsLatest),
            UIElement element => element,
            _ => new FrameworkElement()
        };
    }

    internal static HashSet<int> RetryableTurns(IEnumerable<TranscriptMessage> visibleMessages, Func<string, bool> isAgentSpeaker)
    {
        return visibleMessages
            .Where(message => isAgentSpeaker(message.SpeakerId))
            .OrderByDescending(message => message.Turn)
            .Take(3)
            .Select(message => message.Turn)
            .ToHashSet();
    }

    private void AddTimelineRowIfNeeded(List<object> rows, WpfSettings currentSettings, IReadOnlyList<TranscriptMessage> messages)
    {
        if (currentSettings.ShowMatchQualityTimeline)
        {
            rows.Add(new AdjunctRow(matchQualityTimeline.CreatePanel(messages)));
        }
    }

    private void AddMemoryRowIfNeeded(List<object> rows, WpfSettings currentSettings, ArenaViewSnapshot? snapshot)
    {
        if (currentSettings.ShowAgentMemoryNotes && snapshot is not null)
        {
            rows.Add(new AdjunctRow(HasMemoryNotes(snapshot)
                ? agentMemory.CreatePanel(snapshot)
                : CreateEmptyMemorySummary(snapshot)));
        }
    }

    private Expander CreateEmptyMemorySummary(ArenaViewSnapshot snapshot)
    {
        var activeAgentCount = snapshot.Agents.Count(agent => agent.Active);
        var content = StyledText(
            $"No private memory notes across {activeAgentCount} active agent(s). Add notes when persistent private context is useful.",
            "Arena.Text.Body",
            resourceBrush("MutedTextBrush"),
            new Thickness(0, 4, 0, 2),
            720);
        var expander = new Expander
        {
            Header = "Agent memory • Empty",
            IsExpanded = false,
            Content = content,
            Margin = new Thickness(0, 8, 0, 0),
            Style = FindStyle("Arena.Expander.Section")
        };
        AutomationProperties.SetName(expander, "Agent memory, no notes");
        AutomationProperties.SetHelpText(expander, "Collapsed empty memory summary. Expand for an explanation.");
        return expander;
    }

    internal static bool HasMemoryNotes(ArenaViewSnapshot? snapshot)
    {
        return snapshot?.Agents.Any(agent => agent.PrivateNotes.Any(note => !string.IsNullOrWhiteSpace(note))) == true;
    }

    private Border CreateArenaReadyCard(ArenaViewSnapshot? snapshot)
    {
        var activeAgents = snapshot?.Agents.Where(agent => agent.Active).ToArray() ?? [];
        var current = snapshot is null ? null : SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
        var providerReachable = snapshot?.ProviderOnline == true;
        var currentModel = snapshot is null
            ? "-"
            : SessionOverviewCoordinator.CurrentTurnModel(snapshot, current);
        var modelSelected = !string.IsNullOrWhiteSpace(currentModel) && currentModel != "-";
        var providerReady = providerReachable && modelSelected;
        var presentation = DescribeEmptyState(
            totalMessages: 0,
            providerReachable,
            modelSelected,
            activeAgents.Length,
            current?.Name);

        return CreateEmptyStateCard(
            presentation,
            providerReady ? resourceBrush("BetaAccentBrush") : resourceBrush("AlphaAccentBrush"));
    }

    private Border CreateFilteredEmptyCard(int totalMessages)
    {
        return CreateEmptyStateCard(
            DescribeEmptyState(totalMessages, providerReachable: true, modelSelected: true, activeAgentCount: 0, currentAgentName: null),
            resourceBrush("AlphaAccentBrush"));
    }

    private Border CreateEmptyStateCard(
        EmptyStatePresentation presentation,
        Brush accent,
        UIElement? details = null)
    {
        var content = new StackPanel();
        content.Children.Add(StyledText(
            presentation.Eyebrow,
            "Arena.Text.Label",
            accent,
            new Thickness(0, 0, 0, 6)));
        content.Children.Add(StyledText(
            presentation.Title,
            "Arena.Text.Title",
            resourceBrush("TextBrush"),
            new Thickness(0, 0, 0, 6)));
        content.Children.Add(StyledText(
            presentation.Body,
            "Arena.Text.Secondary",
            resourceBrush("MutedTextBrush"),
            new Thickness(0),
            maxWidth: 520));

        if (details is not null)
        {
            content.Children.Add(details);
        }

        var statusText = StyledText(
            presentation.Status,
            "Arena.Text.Caption",
            resourceBrush("MutedTextBrush"),
            new Thickness(8, 0, 0, 0));
        AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
        var statusRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusRow.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(statusText, 1);
        statusRow.Children.Add(statusText);

        statusRow.Margin = new Thickness(0, 9, 0, 0);
        content.Children.Add(statusRow);

        var actionRow = new WrapPanel { Margin = new Thickness(0, 12, 0, -6) };
        for (var index = 0; index < presentation.Actions.Count; index++)
        {
            var action = presentation.Actions[index];
            var button = CreateEmptyStateAction(action, index == 0);
            KeyboardNavigation.SetTabIndex(button, index);
            actionRow.Children.Add(button);
        }
        content.Children.Add(actionRow);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Opacity = 0.9
        });
        Grid.SetColumn(content, 2);
        layout.Children.Add(content);

        var card = new Border
        {
            Style = FindStyle("Arena.Surface.Panel"),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 20, 0, 14),
            Padding = new Thickness(12),
            Child = layout,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
        AutomationProperties.SetName(card, presentation.Title);
        AutomationProperties.SetHelpText(card, presentation.Body);
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Local);
        return card;
    }

    private Button CreateEmptyStateAction(EmptyStateAction action, bool primary)
    {
        var (label, helpText, glyph, handler) = action switch
        {
            EmptyStateAction.RunOneTurn => (
                "Run 1 turn",
                "Run the next single agent turn and begin the transcript.",
                "\uE72A",
                new RoutedEventHandler(async (_, _) => await runOneTurnAsync())),
            EmptyStateAction.OpenMatchSetup => (
                "Match setup",
                "Open Match Setup to configure the scenario and cast.",
                "\uE713",
                new RoutedEventHandler((_, _) => openMatchSetup())),
            EmptyStateAction.OpenProviderSettings => (
                "Set up provider",
                "Open model provider settings.",
                "\uE774",
                new RoutedEventHandler((_, _) => openProviderSettings())),
            EmptyStateAction.OpenModelSettings => (
                "Choose model",
                "Open model provider settings and select a model.",
                "\uE8B7",
                new RoutedEventHandler((_, _) => openProviderSettings())),
            EmptyStateAction.ClearFilters => (
                "Clear filters",
                "Clear transcript search, turn, timeline, and speaker filters.",
                "\uE71C",
                new RoutedEventHandler((_, _) => clearFilters())),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };

        var button = transcriptActions.CreateLabeledButton(
            label,
            handler,
            enabled: true,
            primary ? TranscriptActionKind.Primary : TranscriptActionKind.Neutral,
            glyph);
        button.Style = FindStyle(primary ? "Arena.Button.Primary" : "Arena.Button.Secondary");
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.PaddingProperty);
        button.ClearValue(Control.OpacityProperty);
        button.MinHeight = 34;
        button.MinWidth = 120;
        button.Padding = new Thickness(12, 6, 12, 6);
        button.Margin = new Thickness(0, 0, 6, 6);
        button.ToolTip = helpText;
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, helpText);
        return button;
    }

    private TextBlock StyledText(
        string text,
        string styleKey,
        Brush foreground,
        Thickness margin,
        double maxWidth = double.PositiveInfinity)
    {
        return new TextBlock
        {
            Text = text,
            Style = FindStyle(styleKey),
            Foreground = foreground,
            Margin = margin,
            MaxWidth = maxWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private Style? FindStyle(string key) => transcriptItems.TryFindResource(key) as Style;

    internal static string EmptyStateModelLabel(string? model)
    {
        return string.IsNullOrWhiteSpace(model) || model.Trim() == "-"
            ? "Not selected"
            : model.Trim();
    }

    internal static EmptyStatePresentation DescribeEmptyState(
        int totalMessages,
        bool providerReachable,
        bool modelSelected,
        int activeAgentCount,
        string? currentAgentName)
    {
        if (totalMessages > 0)
        {
            var messageNoun = totalMessages == 1 ? "message" : "messages";
            var verb = totalMessages == 1 ? "is" : "are";
            return new EmptyStatePresentation(
                "FILTERED VIEW",
                "No messages in this view",
                $"The transcript still contains {totalMessages} {messageNoun}. Clear the active filters to bring the conversation back.",
                $"{totalMessages} {messageNoun} {verb} hidden • Match data is unchanged",
                [EmptyStateAction.ClearFilters]);
        }

        if (activeAgentCount == 0)
        {
            var setupStatus = !providerReachable
                ? "Provider and cast setup needed"
                : !modelSelected
                    ? "Provider connected • Model and cast setup needed"
                    : "Provider connected • Cast needed";
            IReadOnlyList<EmptyStateAction> actions = !providerReachable
                ? new[] { EmptyStateAction.OpenMatchSetup, EmptyStateAction.OpenProviderSettings }
                : !modelSelected
                    ? new[] { EmptyStateAction.OpenMatchSetup, EmptyStateAction.OpenModelSettings }
                    : [EmptyStateAction.OpenMatchSetup];
            return new EmptyStatePresentation(
                "SETUP NEEDED",
                "Finish match setup",
                "Add at least one active agent and confirm the match before starting the transcript.",
                setupStatus,
                actions);
        }

        if (!providerReachable)
        {
            return new EmptyStatePresentation(
                "SETUP NEEDED",
                "Connect a provider to begin",
                "Your cast is ready. Connect the configured provider before starting the opening turn.",
                $"{activeAgentCount} active agents • Provider offline",
                [EmptyStateAction.OpenProviderSettings, EmptyStateAction.OpenMatchSetup]);
        }

        if (!modelSelected)
        {
            return new EmptyStatePresentation(
                "SETUP NEEDED",
                "Select a model to begin",
                "Your provider is connected. Choose a model before starting the opening turn.",
                $"{activeAgentCount} active agents • Provider connected • Model selection needed",
                [EmptyStateAction.OpenModelSettings, EmptyStateAction.OpenMatchSetup]);
        }

        var next = string.IsNullOrWhiteSpace(currentAgentName) ? "next agent" : currentAgentName.Trim();
        return new EmptyStatePresentation(
            "READY",
            "Ready for the first turn",
            "The cast and model provider are ready. Run one turn to begin the match.",
            $"{activeAgentCount} active agents • {next} speaks next • Provider connected",
            [EmptyStateAction.RunOneTurn, EmptyStateAction.OpenMatchSetup]);
    }
}
