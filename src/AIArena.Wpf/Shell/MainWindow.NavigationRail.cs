using System.Windows.Controls;

namespace AIArena.Wpf;

public partial class MainWindow
{
    // Compatibility aliases keep the existing coordinators focused on behavior while
    // the rail owns its visual tree and can evolve independently from MainWindow.
    private Button ArenaNavButton => ShellNavigationRail.ArenaNavigationButton;
    private Button AgentNavButton => ShellNavigationRail.AgentNavigationButton;
    private Button CustomMatchNavButton => ShellNavigationRail.MatchSetupNavigationButton;
    private Button CollaborateNavButton => ShellNavigationRail.CollaborateNavigationButton;
    private Border ArenaSessionOverviewPanel => ShellNavigationRail.ArenaSessionOverviewPanel;
    private Border ArenaLiveAgentsPanel => ShellNavigationRail.ArenaLiveAgentsPanel;
    private Border AgentLeftRailContextPanel => ShellNavigationRail.AgentContextPanel;
    private Border CollaborateLeftRailContextPanel => ShellNavigationRail.CollaborateContextPanel;
    private TextBlock SessionOverviewMatchText => ShellNavigationRail.SessionMatchText;
    private TextBlock SessionOverviewTurnsText => ShellNavigationRail.SessionTurnsText;
    private TextBlock SessionOverviewParticipantsText => ShellNavigationRail.SessionParticipantsText;
    private TextBlock SessionOverviewTokensText => ShellNavigationRail.SessionTokensText;
    private TextBlock SessionOverviewProviderText => ShellNavigationRail.SessionProviderText;
    private TextBlock SessionOverviewContextText => ShellNavigationRail.SessionContextText;
    private ScrollViewer AgentItemsScrollViewer => ShellNavigationRail.AgentItemsScrollViewer;
    private StackPanel AgentItems => ShellNavigationRail.AgentItems;
    private TextBlock AgentLeftWorkspacePathText => ShellNavigationRail.AgentWorkspacePathText;
    private TextBlock AgentLeftBoundaryText => ShellNavigationRail.AgentBoundaryText;
    private StackPanel AgentLeftRoleItems => ShellNavigationRail.AgentRoleItems;
    private Button CollaborateNewChatButton => ShellNavigationRail.CollaborateNewChatButton;
    private StackPanel CollaborateRecentItems => ShellNavigationRail.CollaborateRecentItems;
}
