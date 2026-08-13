using AIArena.Wpf.Help;
using AIArena.Wpf;

internal static partial class Program
{
    private static void HelpCenterContextRoutesCoverPrimarySurfaces()
    {
        Require(MainWindow.HelpArticleForContext(ShellSurface.Lab) == "arena-mode", "AI Lab should open Arena guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.Models) == "models-routing", "Models should open routing guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.MatchSetup) == "match-setup", "Match Setup should open setup guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.Agent) == "agent", "Agent should open Agent guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.Collaborate) == "collaborate", "Collaborate should open Collaborate guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.ExperimentLab) == "experiment-lab", "Experiment Lab should open experiment guidance");
        Require(MainWindow.HelpArticleForContext(ShellSurface.World) == "ai-world-debug", "AI World should open debug guidance");
        Require(
            MainWindow.HelpArticleForContext(ShellSurface.Models, settingsOpen: true) == "provider-troubleshooting",
            "Provider Settings should take precedence over the underlying Models surface");
        Require(
            MainWindow.HelpArticleForContext(ShellSurface.Lab, settingsOpen: true, internetSettings: true) == "internet-privacy",
            "Internet Settings should open privacy and internet guidance");
        Require(
            MainWindow.HelpArticleForContext(ShellSurface.Lab, settingsOpen: true, debugSettings: true) == "ai-world-debug",
            "Debug Settings should open AI World and debug guidance");
        Require(
            MainWindow.HelpArticleForContext(ShellSurface.Lab, statusCenterOpen: true) == "status-center",
            "an open Status Center should take precedence over the shell surface");
        Require(
            MainWindow.ShouldUseCollapsedStatusCenterLauncher(
                userCollapsed: true,
                autoCollapseActive: false,
                widthCollapseLatched: false),
            "Help should anchor the Status Center dashboard to the visible collapsed launcher when the user collapsed the rail");
        Require(
            MainWindow.ShouldUseCollapsedStatusCenterLauncher(
                userCollapsed: false,
                autoCollapseActive: false,
                widthCollapseLatched: true),
            "Help should anchor the Status Center dashboard to the visible collapsed launcher while the width collapse is latched");
        Require(
            !MainWindow.ShouldUseCollapsedStatusCenterLauncher(
                userCollapsed: false,
                autoCollapseActive: true,
                widthCollapseLatched: false),
            "Help should use the revealed rail card after temporarily opening an auto-collapsed rail");

        var focusedDebug = MainWindow.SettingsHelpContext(
            internetFocused: false,
            debugFocused: true,
            agentFocused: false,
            internetExpanded: true,
            debugExpanded: true,
            agentExpanded: true);
        Require(!focusedDebug.Internet && focusedDebug.Debug && !focusedDebug.Agent, "focused Debug settings should win over other expanded sections");
        var ambiguousExpanded = MainWindow.SettingsHelpContext(
            internetFocused: false,
            debugFocused: false,
            agentFocused: false,
            internetExpanded: true,
            debugExpanded: true,
            agentExpanded: false);
        Require(!ambiguousExpanded.Internet && !ambiguousExpanded.Debug && !ambiguousExpanded.Agent, "multiple expanded settings without focus should fall back to general provider guidance");

        var mainWindowSource = File.ReadAllText(FindWorkspaceFile("src/AIArena.Wpf/Shell/MainWindow.xaml.cs"));
        Require(mainWindowSource.Contains("case Key.F1 when shift:", StringComparison.Ordinal), "Shift+F1 should invoke contextual help without replacing the F1 shortcut list");
        Require(MainWindow.ShellShortcuts.Any(item => item.Keys == "Shift+F1"), "contextual help should be discoverable in the shortcut list");

        var transcriptSearchHandlerStart = mainWindowSource.IndexOf("private void TranscriptSearchButton_Click", StringComparison.Ordinal);
        var transcriptSearchHandlerEnd = mainWindowSource.IndexOf("private void TopProviderValue_MouseLeftButtonUp", transcriptSearchHandlerStart, StringComparison.Ordinal);
        Require(transcriptSearchHandlerStart >= 0 && transcriptSearchHandlerEnd > transcriptSearchHandlerStart, "transcript search handler should remain discoverable");
        Require(
            !mainWindowSource[transcriptSearchHandlerStart..transcriptSearchHandlerEnd].Contains("_userGuideWindowHost.Close()", StringComparison.Ordinal),
            "opening transcript search should not close the modeless Help Center");

        foreach (var route in HelpDeepLink.AppRoutes)
        {
            Require(
                mainWindowSource.Contains($"case \"{route}\":", StringComparison.Ordinal),
                $"validated in-app Help route '{route}' should have a shell navigation handler");
        }

        var statusRouteStart = mainWindowSource.IndexOf("case \"app/status-center\":", StringComparison.Ordinal);
        var statusRouteEnd = mainWindowSource.IndexOf("case \"app/view/agent\":", statusRouteStart, StringComparison.Ordinal);
        Require(statusRouteStart >= 0 && statusRouteEnd > statusRouteStart, "Status Center Help route should remain discoverable");
        var statusRoute = mainWindowSource[statusRouteStart..statusRouteEnd];
        Require(
            statusRoute.Contains("useCollapsedLauncher ? CollapsedStatusCenterButton : null", StringComparison.Ordinal),
            "Status Center Help should retain a visible placement and focus target when the right rail cannot be revealed");
    }
}
