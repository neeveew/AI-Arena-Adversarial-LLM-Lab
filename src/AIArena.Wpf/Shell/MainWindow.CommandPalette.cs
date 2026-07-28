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

    // Most-recently-used ids, newest first. Kept for the session rather than
    // persisted: the point is that the handful of things you are doing right now
    // stay at the top, and that intent rarely survives a restart.
    private readonly List<string> _recentCommandIds = [];

    private const int RecentCommandLimit = 8;

    private void ShowCommandPalette()
    {
        var chosen = CommandPaletteDialog.Show(this, _theme, BuildShellCommands(), _recentCommandIds);
        if (chosen is null)
        {
            return;
        }

        _recentCommandIds.Remove(chosen.Id);
        _recentCommandIds.Insert(0, chosen.Id);
        if (_recentCommandIds.Count > RecentCommandLimit)
        {
            _recentCommandIds.RemoveRange(RecentCommandLimit, _recentCommandIds.Count - RecentCommandLimit);
        }

        // Invoked after the dialog closes so an action is free to open another
        // window without fighting a modal that is still up.
        chosen.Invoke();
    }

    /// <summary>
    /// The palette's contents, for the control plane. Everything else in the
    /// shell can be driven without focus; the palette could only be reached by
    /// simulating Ctrl+K, and simulated keystrokes follow whatever window the
    /// operating system considers foreground rather than the one you meant.
    /// </summary>
    internal object ControlListPaletteCommands()
    {
        var available = ShellCommandPalette.Filter(BuildShellCommands(), "", _recentCommandIds);
        return new
        {
            surface = SelectedControlPlaneView(),
            count = available.Count,
            commands = available
                .Select(command => new { command.Id, command.Title, command.Group, command.Keys })
                .ToList()
        };
    }

    internal (bool Ok, string Message) ControlRunPaletteCommand(string id)
    {
        var match = BuildShellCommands().FirstOrDefault(
            command => command.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return (false, $"No palette command with id '{id}'.");
        }

        // Gated commands are refused rather than forced: the gate is the same one
        // that hides them in the palette, and running Stop on a surface that has
        // nothing running is not a thing the reader could have asked for.
        if (!match.Available)
        {
            return (false, $"Palette command '{id}' is not available on the current surface.");
        }

        _recentCommandIds.Remove(match.Id);
        _recentCommandIds.Insert(0, match.Id);
        if (_recentCommandIds.Count > RecentCommandLimit)
        {
            _recentCommandIds.RemoveRange(RecentCommandLimit, _recentCommandIds.Count - RecentCommandLimit);
        }

        match.Invoke();
        return (true, $"Palette command '{match.Title}' ran.");
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

        AddSurfaceCommands(commands);
        AddViewPresetCommands(commands);
        AddThemeCommands(commands);
        return commands;
    }

    /// <summary>
    /// Actions that only mean something on one surface. They are gated rather
    /// than always listed, so the palette does not offer to stop a Collaborate
    /// run while you are looking at the transcript.
    ///
    /// Reset is deliberately absent. It stays a pointer action for the same
    /// reason it has no keyboard shortcut: nothing that discards a run should be
    /// two keystrokes away from a search box.
    /// </summary>
    private void AddSurfaceCommands(List<ShellCommand> commands)
    {
        commands.Add(new ShellCommand(
            "arena.narrate",
            "Narrate the match now",
            "Match",
            "",
            "narrator commentary observe",
            () => _ = ArenaRun.NarrateNowAsync(),
            () => _activeShellSurface == ShellSurface.Lab && NarrateNowButton.IsEnabled));

        commands.Add(new ShellCommand(
            "arena.speak",
            "Speak the latest narrator message",
            "Match",
            "",
            "voice tts read aloud",
            () => SpeakLatestNarratorButton_Click(SpeakLatestNarratorButton, new RoutedEventArgs()),
            () => _activeShellSurface == ShellSurface.Lab && SpeakLatestNarratorButton.IsEnabled));

        commands.Add(new ShellCommand(
            "collaborate.new",
            "Start a new Collaborate chat",
            "Collaborate",
            "",
            "fresh clear conversation",
            () => CollaborateNewChatButton_Click(CollaborateNewChatButton, new RoutedEventArgs()),
            () => _activeShellSurface == ShellSurface.Collaborate));

        commands.Add(new ShellCommand(
            "collaborate.stop",
            "Stop the Collaborate run",
            "Collaborate",
            "",
            "cancel halt",
            () => CollaborateStopButton_Click(CollaborateStopButton, new RoutedEventArgs()),
            () => _activeShellSurface == ShellSurface.Collaborate && CollaborateStopButton.IsEnabled));

        commands.Add(new ShellCommand(
            "agent.clear-history",
            "Clear the Agent command history",
            "Agent",
            "",
            "commands log wipe",
            () => AgentClearHistoryButton_Click(AgentClearHistoryButton, new RoutedEventArgs()),
            () => _activeShellSurface == ShellSurface.Agent && IsAgentWorkspaceEnabled(_wpfSettings)));
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
