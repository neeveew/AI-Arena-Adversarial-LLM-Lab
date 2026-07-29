using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIArena.Wpf.ViewModels;

namespace AIArena.Wpf.Controls;

/// <summary>
/// Owns the persistent shell navigation rail while exposing the existing presentation
/// targets needed by the shell coordinators during the incremental MVVM migration.
/// </summary>
public partial class ShellNavigationRailControl : UserControl
{
    private ShellTopBarPresentationViewModel? presentation;

    public ShellNavigationRailControl()
    {
        InitializeComponent();
    }

    internal ShellTopBarPresentationViewModel? Presentation
    {
        get => presentation;
        set
        {
            if (ReferenceEquals(presentation, value))
            {
                return;
            }

            presentation = value;
            DataContext = value;
        }
    }

    public event RoutedEventHandler? ArenaNavigationRequested;
    public event RoutedEventHandler? AgentNavigationRequested;
    public event RoutedEventHandler? MatchSetupNavigationRequested;
    public event RoutedEventHandler? CollaborateNavigationRequested;
    public event RoutedEventHandler? CollaborateNewChatRequested;
    public event MouseButtonEventHandler? SessionMatchRequested;
    public event MouseButtonEventHandler? SessionTurnsRequested;
    public event MouseButtonEventHandler? SessionPerformanceRequested;
    public event MouseButtonEventHandler? SessionProviderRequested;

    public Button ArenaNavigationButton => ArenaNavButtonElement;
    public Button AgentNavigationButton => AgentNavButtonElement;
    public Button MatchSetupNavigationButton => CustomMatchNavButtonElement;
    public Button CollaborateNavigationButton => CollaborateNavButtonElement;
    public Border ArenaSessionOverviewPanel => ArenaSessionOverviewPanelElement;
    public Border ArenaLiveAgentsPanel => ArenaLiveAgentsPanelElement;
    public Border AgentContextPanel => AgentLeftRailContextPanelElement;
    public Border CollaborateContextPanel => CollaborateLeftRailContextPanelElement;
    public TextBlock SessionMatchText => SessionOverviewMatchTextElement;
    public TextBlock SessionTurnsText => SessionOverviewTurnsTextElement;
    public TextBlock SessionParticipantsText => SessionOverviewParticipantsTextElement;
    public TextBlock SessionTokensText => SessionOverviewTokensTextElement;
    public TextBlock SessionProviderText => SessionOverviewProviderTextElement;
    public TextBlock SessionContextText => SessionOverviewContextTextElement;
    public ScrollViewer AgentItemsScrollViewer => AgentItemsScrollViewerElement;
    public StackPanel AgentItems => AgentItemsElement;
    public TextBlock AgentWorkspacePathText => AgentLeftWorkspacePathTextElement;
    public TextBlock AgentBoundaryText => AgentLeftBoundaryTextElement;
    public StackPanel AgentRoleItems => AgentLeftRoleItemsElement;
    public Button CollaborateNewChatButton => CollaborateNewChatButtonElement;
    public StackPanel CollaborateRecentItems => CollaborateRecentItemsElement;

    private void ArenaNavButton_Click(object sender, RoutedEventArgs e) =>
        ArenaNavigationRequested?.Invoke(sender, e);

    private void AgentNavButton_Click(object sender, RoutedEventArgs e) =>
        AgentNavigationRequested?.Invoke(sender, e);

    private void CustomMatchNavButton_Click(object sender, RoutedEventArgs e) =>
        MatchSetupNavigationRequested?.Invoke(sender, e);

    private void CollaborateNavButton_Click(object sender, RoutedEventArgs e) =>
        CollaborateNavigationRequested?.Invoke(sender, e);

    private void CollaborateNewChatButton_Click(object sender, RoutedEventArgs e) =>
        CollaborateNewChatRequested?.Invoke(sender, e);

    private void SessionOverviewMatch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        SessionMatchRequested?.Invoke(sender, e);

    private void SessionOverviewTurns_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        SessionTurnsRequested?.Invoke(sender, e);

    private void SessionOverviewPerformance_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        SessionPerformanceRequested?.Invoke(sender, e);

    private void SessionOverviewProvider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        SessionProviderRequested?.Invoke(sender, e);
}
