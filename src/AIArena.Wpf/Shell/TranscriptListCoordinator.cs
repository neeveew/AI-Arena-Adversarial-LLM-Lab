using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class TranscriptListCoordinator
{
    private readonly Dispatcher dispatcher;
    private readonly Panel transcriptItems;
    private readonly ScrollViewer transcriptScrollViewer;
    private readonly CheckBox followChatCheckBox;
    private readonly ShellCardFactory shellCards;
    private readonly TranscriptActionCoordinator transcriptActions;
    private readonly TranscriptSearchCoordinator transcriptSearch;
    private readonly TranscriptInsightCoordinator transcriptInsight;
    private readonly TranscriptCardRenderer transcriptCards;
    private readonly TranscriptAdjunctCoordinator transcriptAdjunct;
    private readonly AgentMemoryCoordinator agentMemory;
    private readonly MatchQualityTimelineCoordinator matchQualityTimeline;
    private readonly ProviderQuickSetupCoordinator providerQuickSetup;
    private readonly Func<WpfSettings> settings;
    private readonly Func<ArenaViewSnapshot?> lastRenderedSnapshot;
    private readonly Action<IReadOnlyList<TranscriptMessage>> setLastRenderedMessages;
    private readonly Func<bool> isDiagnosticsDisplayed;
    private readonly Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics;
    private readonly Func<bool> shouldShowDecisionCard;
    private readonly Func<string, bool> isAgentSpeaker;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, string> shortModelName;
    private readonly Func<string, string> displayStatusValue;

    public TranscriptListCoordinator(
        Dispatcher dispatcher,
        Panel transcriptItems,
        ScrollViewer transcriptScrollViewer,
        CheckBox followChatCheckBox,
        ShellCardFactory shellCards,
        TranscriptActionCoordinator transcriptActions,
        TranscriptSearchCoordinator transcriptSearch,
        TranscriptInsightCoordinator transcriptInsight,
        TranscriptCardRenderer transcriptCards,
        TranscriptAdjunctCoordinator transcriptAdjunct,
        AgentMemoryCoordinator agentMemory,
        MatchQualityTimelineCoordinator matchQualityTimeline,
        ProviderQuickSetupCoordinator providerQuickSetup,
        Func<WpfSettings> settings,
        Func<ArenaViewSnapshot?> lastRenderedSnapshot,
        Action<IReadOnlyList<TranscriptMessage>> setLastRenderedMessages,
        Func<bool> isDiagnosticsDisplayed,
        Action<IReadOnlyList<TranscriptMessage>> updateDiagnostics,
        Func<bool> shouldShowDecisionCard,
        Func<string, bool> isAgentSpeaker,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, string> shortModelName,
        Func<string, string> displayStatusValue)
    {
        this.dispatcher = dispatcher;
        this.transcriptItems = transcriptItems;
        this.transcriptScrollViewer = transcriptScrollViewer;
        this.followChatCheckBox = followChatCheckBox;
        this.shellCards = shellCards;
        this.transcriptActions = transcriptActions;
        this.transcriptSearch = transcriptSearch;
        this.transcriptInsight = transcriptInsight;
        this.transcriptCards = transcriptCards;
        this.transcriptAdjunct = transcriptAdjunct;
        this.agentMemory = agentMemory;
        this.matchQualityTimeline = matchQualityTimeline;
        this.providerQuickSetup = providerQuickSetup;
        this.settings = settings;
        this.lastRenderedSnapshot = lastRenderedSnapshot;
        this.setLastRenderedMessages = setLastRenderedMessages;
        this.isDiagnosticsDisplayed = isDiagnosticsDisplayed;
        this.updateDiagnostics = updateDiagnostics;
        this.shouldShowDecisionCard = shouldShowDecisionCard;
        this.isAgentSpeaker = isAgentSpeaker;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.blendBrush = blendBrush;
        this.shortModelName = shortModelName;
        this.displayStatusValue = displayStatusValue;
    }

    public void Populate(IReadOnlyList<TranscriptMessage> messages)
    {
        setLastRenderedMessages(messages);
        transcriptItems.Children.Clear();
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
        if (messages.Count == 0)
        {
            AddMemoryPanelIfNeeded(currentSettings, snapshot);
            transcriptItems.Children.Add(CreateArenaReadyCard(snapshot));
            return;
        }

        if (visibleMessages.Length == 0)
        {
            AddTimelineIfNeeded(currentSettings, messages);
            AddMemoryPanelIfNeeded(currentSettings, snapshot);
            transcriptItems.Children.Add(shellCards.CreateEmptyStateCard(
                "No transcript matches",
                "Adjust the search text, turn preset, or speaker filters to widen the view.",
                resourceBrush("MutedTextBrush")));
            return;
        }

        AddTimelineIfNeeded(currentSettings, messages);
        if (shouldShowDecisionCard() && snapshot is not null)
        {
            transcriptItems.Children.Add(transcriptAdjunct.CreateDecisionCardPanel(snapshot));
        }
        if (currentSettings.TurnCompareMode)
        {
            transcriptInsight.EnsureTurnCompareSelection(visibleMessages);
            transcriptItems.Children.Add(transcriptAdjunct.CreateTurnComparePanel(visibleMessages));
        }
        AddMemoryPanelIfNeeded(currentSettings, snapshot);
        var moderatorPanel = transcriptAdjunct.CreateAutoModeratorPanel(messages);
        if (moderatorPanel is not null)
        {
            transcriptItems.Children.Add(moderatorPanel);
        }

        var retryableTurns = RetryableTurns(visibleMessages, isAgentSpeaker);
        var latestTurn = visibleMessages.Max(message => message.Turn);
        foreach (var message in visibleMessages.OrderByDescending(message => message.Turn))
        {
            transcriptItems.Children.Add(transcriptCards.CreateCard(
                message,
                retryableTurns.Contains(message.Turn),
                transcriptSearch.HasActiveSearch,
                message.Turn == latestTurn));
        }

        if (followChatCheckBox.IsChecked == true)
        {
            dispatcher.BeginInvoke(() => transcriptScrollViewer.ScrollToTop(), DispatcherPriority.Background);
        }
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

    private void AddTimelineIfNeeded(WpfSettings currentSettings, IReadOnlyList<TranscriptMessage> messages)
    {
        if (currentSettings.ShowMatchQualityTimeline)
        {
            transcriptItems.Children.Add(matchQualityTimeline.CreatePanel(messages));
        }
    }

    private void AddMemoryPanelIfNeeded(WpfSettings currentSettings, ArenaViewSnapshot? snapshot)
    {
        if (currentSettings.ShowAgentMemoryNotes && snapshot is not null)
        {
            transcriptItems.Children.Add(agentMemory.CreatePanel(snapshot));
        }
    }

    private Border CreateArenaReadyCard(ArenaViewSnapshot? snapshot)
    {
        var accent = resourceBrush("AlphaAccentBrush");
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        if (snapshot is not null)
        {
            var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
            var current = SessionOverviewCoordinator.CurrentTurnAgent(snapshot);
            var setup = new WrapPanel();
            setup.Children.Add(shellCards.CreateSetupChip("Match", displayStatusValue(snapshot.MatchType), resourceBrush("TextBrush")));
            setup.Children.Add(shellCards.CreateSetupChip("Agents", $"{activeAgents.Length} + narrator", resourceBrush("PrimaryBorderBrush")));
            setup.Children.Add(shellCards.CreateSetupChip("Turn", current is null ? "-" : displayStatusValue(current.Id), current is null ? resourceBrush("MutedTextBrush") : accentForSpeaker(current.Id)));
            setup.Children.Add(shellCards.CreateSetupChip("Model", shortModelName(SessionOverviewCoordinator.CurrentTurnModel(snapshot, current)), resourceBrush("MutedTextBrush")));
            panel.Children.Add(setup);

            if (ProviderQuickSetupCoordinator.ShouldShowProviderSetup(snapshot, current))
            {
                panel.Children.Add(providerQuickSetup.CreateCard(snapshot, current));
            }
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Quiet state",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        });

        return shellCards.CreateCard(
            "Arena is ready",
            "Start with 1 TURN, AUTO CHAT, an agent turn, or write directly in Operator Turn.",
            blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            accent,
            panel);
    }
}
