namespace AIArena.Wpf;

public partial class MainWindow
{
    // Shell state changes arrive from two directions: a PowerShell command, and
    // a person clicking or pressing a key. Publishing from the command handlers
    // alone meant a watcher only ever saw the half the operator caused - open
    // Match Setup with F2 and nothing was announced at all - so anyone watching
    // a human drive the app saw an empty stream. These publish from the shared
    // paths both routes run through, and the command handlers no longer publish
    // for themselves.
    //
    // Every publisher is gated on the state actually changing. That is not just
    // tidiness: ApplyRightRailCollapsed runs on every window resize, so an
    // ungated publisher would flood the stream while someone drags a corner.

    private bool _shellEventsArmed;
    private string? _lastNavigationView;
    private string? _lastMatchSetupOverlayKey;
    private string? _lastSettingsOverlayKey;
    private string? _lastRailKey;
    private string? _lastThemeId;
    private string? _lastViewPreset;

    /// <summary>
    /// Startup applies the saved view mode, rail state and theme before anyone
    /// can be listening. Arming once the window is loaded keeps that initial
    /// burst out of the stream, so the first event a watcher sees is a real one.
    /// </summary>
    private void ArmShellEvents()
    {
        _lastNavigationView = SelectedControlPlaneView();
        _lastRailKey = RightRailStateKey();
        _lastThemeId = _theme.Id;

        // Subscribing here rather than at construction keeps the ordering simple:
        // by Loaded every coordinator exists, and nothing published during startup.
        if (_appSettingsCoordinator is { } appSettings)
        {
            appSettings.VisibilityChanged = visible =>
                PublishSettingsOverlayChanged(visible ? "Settings opened." : "Settings closed.");

            // Match Setup already hides settings when it opens. Without the
            // mirror image, settings opened over Match Setup and covered its
            // close button, so the only way out was to close settings first.
            appSettings.Opening = () =>
            {
                // Qualified: MainWindow inherits a Visibility property, which
                // shadows the enum type name here.
                if (CustomMatchPanel.Visibility == System.Windows.Visibility.Visible)
                {
                    CloseMatchSetupFlyout();
                }
            };
        }

        if (_shellNavigationCoordinator is { } navigation)
        {
            navigation.ThemeChanged = PublishThemeChanged;
        }

        if (_transcriptViewCoordinator is { } transcriptView)
        {
            transcriptView.PresetChanged = PublishViewPresetChanged;
        }

        ArenaRun.RunLifecycle = PublishArenaLifecycle;
        InternetWorkflow.EnabledChanged = () =>
            _controlPlaneEvents.Publish("internet.changed", "Internet setting changed.", InternetWorkflow.ControlState);
        InternetWorkflow.DiagnosticCompleted = () =>
            _controlPlaneEvents.Publish("internet.test.completed", "Internet diagnostic completed.", InternetWorkflow.ControlState);

        _shellEventsArmed = true;
    }

    /// <summary>
    /// Run-loop transitions are discrete events rather than state, so unlike the
    /// shell publishers these are not change-gated: running two turns should be
    /// reported twice.
    /// </summary>
    private void PublishArenaLifecycle(string transition)
    {
        if (!_shellEventsArmed)
        {
            return;
        }

        switch (transition)
        {
            case "started":
                _controlPlaneEvents.Publish("arena.run.started", "Arena auto-chat start requested.");
                break;
            case "stopped":
                _controlPlaneEvents.Publish("arena.run.stopped", "Arena auto-chat stop requested.");
                break;
            case "turn":
                _controlPlaneEvents.Publish("arena.turn.completed", "Arena one-turn request completed.");
                break;
            case "narration":
                _controlPlaneEvents.Publish("arena.narration.completed", "Arena narration request completed.");
                break;
        }
    }

    private void PublishNavigationChanged()
    {
        if (!_shellEventsArmed)
        {
            return;
        }

        var view = SelectedControlPlaneView();
        if (!ShouldPublishChange(_lastNavigationView, view))
        {
            return;
        }

        _lastNavigationView = view;
        _controlPlaneEvents.Publish("navigation.changed", "AI Arena view changed.", new { view });
    }

    private void PublishMatchSetupOverlayChanged(string message)
    {
        if (!_shellEventsArmed)
        {
            return;
        }

        var state = _shellOverlayControlService.CaptureMatchSetup();
        var key = $"{state.Open}:{state.Section}";
        if (!ShouldPublishChange(_lastMatchSetupOverlayKey, key))
        {
            return;
        }

        _lastMatchSetupOverlayKey = key;
        _controlPlaneEvents.Publish("shell.overlay.changed", message, state);
    }

    private void PublishSettingsOverlayChanged(string message)
    {
        if (!_shellEventsArmed)
        {
            return;
        }

        var state = _shellOverlayControlService.CaptureSettings();
        var key = $"{state.Open}:{state.SearchQuery}";
        if (!ShouldPublishChange(_lastSettingsOverlayKey, key))
        {
            return;
        }

        _lastSettingsOverlayKey = key;
        _controlPlaneEvents.Publish("shell.overlay.changed", message, state);
    }

    private void PublishRailChanged()
    {
        if (!_shellEventsArmed)
        {
            return;
        }

        var key = RightRailStateKey();
        if (!ShouldPublishChange(_lastRailKey, key))
        {
            return;
        }

        _lastRailKey = key;
        _controlPlaneEvents.Publish(
            "navigation.rail.changed",
            "Right rail visibility changed.",
            BuildRightRailControlState());
    }

    private void PublishThemeChanged(string themeId)
    {
        if (!_shellEventsArmed || !ShouldPublishChange(_lastThemeId, themeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastThemeId = themeId;
        _controlPlaneEvents.Publish("navigation.theme.changed", "AI Arena theme changed.", new { theme = themeId });
    }

    private void PublishViewPresetChanged(string preset)
    {
        if (!_shellEventsArmed || !ShouldPublishChange(_lastViewPreset, preset, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastViewPreset = preset;
        _controlPlaneEvents.Publish("view.preset.changed", "Transcript view preset changed.", new { preset });
    }

    /// <summary>
    /// True when <paramref name="next"/> is a real change worth announcing.
    /// Pulled out as a static so the gate can be tested without a window: the
    /// resize case in particular is easy to regress and awkward to reproduce.
    /// </summary>
    internal static bool ShouldPublishChange(
        string? previous,
        string next,
        StringComparison comparison = StringComparison.Ordinal)
    {
        return !string.IsNullOrWhiteSpace(next) && !string.Equals(previous, next, comparison);
    }

    private string RightRailStateKey()
    {
        return IsRightRailEffectivelyCollapsed(
            _wpfSettings.RightRailCollapsed,
            _rightRailAutoCollapseActive,
            _rightRailNarrowRevealRequested,
            _rightRailWidthCollapseLatched)
            ? "collapsed"
            : "expanded";
    }
}
