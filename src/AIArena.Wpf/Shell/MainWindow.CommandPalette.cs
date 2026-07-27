using System.Windows;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class MainWindow
{
    // The app carries a lot of surface, and most of it was reachable only by
    // knowing which rail, flyout or menu held it. The palette is deliberately
    // not a second implementation of any of it: every entry calls the same
    // handler the button or shortcut calls, so there is one behaviour per
    // action and the palette cannot drift away from the UI.

    private void ShowCommandPalette()
    {
        var chosen = CommandPaletteDialog.Show(this, _theme, BuildShellCommands());

        // Invoked after the dialog closes so an action is free to open another
        // window without fighting a modal that is still up.
        chosen?.Invoke();
    }

    private IReadOnlyList<ShellCommand> BuildShellCommands()
    {
        var commands = new List<ShellCommand>
        {
            new(
                "view.lab",
                "Go to AI Lab",
                "Navigate",
                "Ctrl+1",
                "arena transcript match",
                () => ArenaNavButton_Click(ShellNavigationRail, new RoutedEventArgs())),
            new(
                "view.agent",
                "Go to Agent",
                "Navigate",
                "Ctrl+2",
                "workspace builder planner reviewer",
                () => AgentNavButton_Click(ShellNavigationRail, new RoutedEventArgs()),
                () => IsAgentWorkspaceEnabled(_wpfSettings)),
            new(
                "view.collaborate",
                "Go to AI Collaborate",
                "Navigate",
                "Ctrl+3",
                "chat team synthesis",
                () => CollaborateNavButton_Click(ShellNavigationRail, new RoutedEventArgs())),
            new(
                "match.setup",
                "Open Match Setup",
                "Match",
                "F2",
                "scenario cast matrix seed persona",
                () => MatchSetupButton_Click(MatchSetupButton, new RoutedEventArgs())),
            new(
                "match.turn",
                "Run one turn",
                "Match",
                "Ctrl+Enter",
                "step advance",
                () => OneTurnButton_Click(OneTurnButton, new RoutedEventArgs()),
                () => OneTurnButton.IsEnabled),
            new(
                "match.autochat",
                "Start Auto Chat, or pause it",
                "Match",
                "F9",
                "run loop stop",
                ToggleAutoChatFromShortcut),
            new(
                "transcript.search",
                "Search the transcript",
                "Transcript",
                "Ctrl+F",
                "find filter",
                () => TranscriptSearchButton_Click(TranscriptSearchButton, new RoutedEventArgs()),
                () => SearchCommandHost.Visibility == Visibility.Visible),
            new(
                "transcript.export",
                "Export the transcript",
                "Transcript",
                "Ctrl+E",
                "save markdown copy",
                () => ExportTranscriptButton_Click(ExportTranscriptBottomButton, new RoutedEventArgs()),
                () => ExportTranscriptBottomButton.Visibility == Visibility.Visible),
            new(
                "transcript.viewmenu",
                "Open the transcript view menu",
                "Transcript",
                "F8",
                "display options",
                () => ViewMenuButton_Click(ViewMenuButton, new RoutedEventArgs()),
                () => ViewMenuHost.Visibility == Visibility.Visible),
            new(
                "session.reload",
                "Reload the session from disk",
                "Session",
                "F5",
                "refresh revert",
                () => _ = RefreshActiveSessionAsync("Reloaded the session from disk.")),
            new(
                "shell.rail",
                "Show or hide the right rail",
                "Shell",
                "F7",
                "panel sidebar inspector",
                () => RightRailToggleButton_Click(RightRailToggleButton, new RoutedEventArgs())),
            new(
                "shell.settings",
                "Open App Settings",
                "Shell",
                "F10",
                "preferences provider models options",
                () => AppSettingsButton_Click(AppSettingsButton, new RoutedEventArgs())),
            new(
                "shell.shortcuts",
                "Show keyboard shortcuts",
                "Shell",
                "F1",
                "keys help bindings",
                ShowShortcutsOverlay)
        };

        AddViewPresetCommands(commands);
        AddThemeCommands(commands);
        return commands;
    }

    private void AddViewPresetCommands(List<ShellCommand> commands)
    {
        if (_transcriptViewCoordinator is not { } view)
        {
            return;
        }

        commands.Add(new ShellCommand(
            "preset.focused", "View preset: Focused", "Transcript", "", "hide panels minimal", view.ApplyFocusedPreset));
        commands.Add(new ShellCommand(
            "preset.diagnostics", "View preset: Diagnostics", "Transcript", "", "friction drift claims", view.ApplyDiagnosticsPreset));
        commands.Add(new ShellCommand(
            "preset.compact", "View preset: Compact", "Transcript", "", "dense small", view.ApplyCompactPreset));
        commands.Add(new ShellCommand(
            "preset.review", "View preset: Review", "Transcript", "", "battle verdict score", view.ApplyReviewPreset));
    }

    private void AddThemeCommands(List<ShellCommand> commands)
    {
        foreach (var theme in ThemePalette.BuiltIn)
        {
            // Captured per iteration so each command applies its own theme.
            var id = theme.Id;
            commands.Add(new ShellCommand(
                $"theme.{id}",
                $"Theme: {theme.Name}",
                "Appearance",
                "",
                "colour color dark light appearance",
                () => ShellNavigation.ApplyTheme(id, persist: true, rerender: true)));
        }
    }
}
