using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

internal sealed class ShellNavigationCoordinator
{
    private readonly Window owner;
    private readonly WpfSettingsStore settingsStore;
    private readonly Func<WpfSettings> settings;
    private readonly ComboBox themePicker;
    private readonly Button arenaNavButton;
    private readonly Button experimentLabNavButton;
    private readonly Button customMatchNavButton;
    private readonly Button agentNavButton;
    private readonly Button collaborateNavButton;
    private readonly Button appSettingsButton;
    private readonly FrameworkElement transcriptPanel;
    private readonly FrameworkElement experimentLabPanel;
    private readonly FrameworkElement customMatchPanel;
    private readonly FrameworkElement providerModelsPanel;
    private readonly FrameworkElement agentWorldPanel;
    private readonly FrameworkElement agentWorkspacePanel;
    private readonly FrameworkElement collaboratePanel;
    private readonly FrameworkElement arenaTopBarMetrics;
    private readonly FrameworkElement agentTopBarMetrics;
    private readonly FrameworkElement collaborateTopBarMetrics;
    private readonly FrameworkElement arenaRightRailPanel;
    private readonly FrameworkElement experimentRightRailPanel;
    private readonly FrameworkElement agentRightRailPanel;
    private readonly FrameworkElement collaborateRightRailPanel;
    private readonly FrameworkElement arenaSessionOverviewPanel;
    private readonly FrameworkElement arenaLiveAgentsPanel;
    private readonly FrameworkElement agentLeftRailContextPanel;
    private readonly FrameworkElement collaborateLeftRailContextPanel;
    private readonly FrameworkElement experimentLeftRailContextPanel;
    private readonly FrameworkElement appSettingsPanel;
    private readonly Action<ThemePalette> setTheme;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action refreshUserGuideTheme;
    private readonly Func<bool> hasActiveSession;
    private readonly Action<string> refreshActiveSession;

    private bool isSelectingTheme;

    /// <summary>
    /// Raised with the theme id after a theme is applied, whichever route asked.
    /// </summary>
    public Action<string>? ThemeChanged { get; set; }

    public ShellNavigationCoordinator(
        Window owner,
        WpfSettingsStore settingsStore,
        Func<WpfSettings> settings,
        ComboBox themePicker,
        Button arenaNavButton,
        Button experimentLabNavButton,
        Button customMatchNavButton,
        Button agentNavButton,
        Button collaborateNavButton,
        Button appSettingsButton,
        FrameworkElement transcriptPanel,
        FrameworkElement experimentLabPanel,
        FrameworkElement customMatchPanel,
        FrameworkElement providerModelsPanel,
        FrameworkElement agentWorldPanel,
        FrameworkElement agentWorkspacePanel,
        FrameworkElement collaboratePanel,
        FrameworkElement arenaTopBarMetrics,
        FrameworkElement agentTopBarMetrics,
        FrameworkElement collaborateTopBarMetrics,
        FrameworkElement arenaRightRailPanel,
        FrameworkElement experimentRightRailPanel,
        FrameworkElement agentRightRailPanel,
        FrameworkElement collaborateRightRailPanel,
        FrameworkElement arenaSessionOverviewPanel,
        FrameworkElement arenaLiveAgentsPanel,
        FrameworkElement agentLeftRailContextPanel,
        FrameworkElement collaborateLeftRailContextPanel,
        FrameworkElement experimentLeftRailContextPanel,
        FrameworkElement appSettingsPanel,
        Action<ThemePalette> setTheme,
        Func<string, Brush> resourceBrush,
        Action refreshUserGuideTheme,
        Func<bool> hasActiveSession,
        Action<string> refreshActiveSession)
    {
        this.owner = owner;
        this.settingsStore = settingsStore;
        this.settings = settings;
        this.themePicker = themePicker;
        this.arenaNavButton = arenaNavButton;
        this.experimentLabNavButton = experimentLabNavButton;
        this.customMatchNavButton = customMatchNavButton;
        this.agentNavButton = agentNavButton;
        this.collaborateNavButton = collaborateNavButton;
        this.appSettingsButton = appSettingsButton;
        this.transcriptPanel = transcriptPanel;
        this.experimentLabPanel = experimentLabPanel;
        this.customMatchPanel = customMatchPanel;
        this.providerModelsPanel = providerModelsPanel;
        this.agentWorldPanel = agentWorldPanel;
        this.agentWorkspacePanel = agentWorkspacePanel;
        this.collaboratePanel = collaboratePanel;
        this.arenaTopBarMetrics = arenaTopBarMetrics;
        this.agentTopBarMetrics = agentTopBarMetrics;
        this.collaborateTopBarMetrics = collaborateTopBarMetrics;
        this.arenaRightRailPanel = arenaRightRailPanel;
        this.experimentRightRailPanel = experimentRightRailPanel;
        this.agentRightRailPanel = agentRightRailPanel;
        this.collaborateRightRailPanel = collaborateRightRailPanel;
        this.arenaSessionOverviewPanel = arenaSessionOverviewPanel;
        this.arenaLiveAgentsPanel = arenaLiveAgentsPanel;
        this.agentLeftRailContextPanel = agentLeftRailContextPanel;
        this.collaborateLeftRailContextPanel = collaborateLeftRailContextPanel;
        this.experimentLeftRailContextPanel = experimentLeftRailContextPanel;
        this.appSettingsPanel = appSettingsPanel;
        this.setTheme = setTheme;
        this.resourceBrush = resourceBrush;
        this.refreshUserGuideTheme = refreshUserGuideTheme;
        this.hasActiveSession = hasActiveSession;
        this.refreshActiveSession = refreshActiveSession;
        ApplyAppSettingsButtonState(appSettingsButton, appSettingsPanel.Visibility == Visibility.Visible);
    }

    public void InitializeThemePicker()
    {
        var currentSettings = settings();
        var themeId = ThemePalette.NormalizeId(currentSettings.ThemeId);
        currentSettings.ThemeId = themeId;
        var themes = ThemePalette.BuiltIn.ToArray();
        isSelectingTheme = true;
        themePicker.ItemsSource = themes;
        themePicker.SelectedValue = SelectedThemeId(themes, themeId);
        isSelectingTheme = false;
    }

    public void OnThemeSelectionChanged()
    {
        if (isSelectingTheme)
        {
            return;
        }

        var themeId = themePicker.SelectedItem is ThemePalette selectedTheme
            ? selectedTheme.Id
            : ThemePalette.NormalizeId(themePicker.SelectedValue?.ToString());
        ApplyTheme(themeId, persist: true, rerender: true);
    }

    public void ApplyTheme(string themeId, bool persist, bool rerender)
    {
        var theme = ThemePalette.Resolve(themeId);
        setTheme(theme);
        ApplyThemeResources(owner.Resources, theme);

        if (persist)
        {
            var currentSettings = settings();
            currentSettings.ThemeId = theme.Id;
            settingsStore.Save(currentSettings);
        }

        UpdateNavigationTheme();
        refreshUserGuideTheme();
        if (rerender && hasActiveSession())
        {
            refreshActiveSession($"Theme applied: {theme.Name}");
        }

        ThemeChanged?.Invoke(theme.Id);
    }

    public void ShowTranscriptPanel()
    {
        transcriptPanel.Visibility = Visibility.Visible;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        providerModelsPanel.Visibility = Visibility.Collapsed;
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: false);
        UpdateNavigationTheme();
    }

    public void ShowCustomMatchPanel()
    {
        transcriptPanel.Visibility = Visibility.Visible;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        ArenaMotion.RevealOverlay(customMatchPanel);
        providerModelsPanel.Visibility = Visibility.Collapsed;
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: false);
        UpdateNavigationTheme();
    }

    public void ShowProviderModelsPanel()
    {
        transcriptPanel.Visibility = Visibility.Visible;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        ArenaMotion.RevealOverlay(providerModelsPanel);
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: false);
        UpdateNavigationTheme();
    }

    public void ShowWorldPanel()
    {
        transcriptPanel.Visibility = Visibility.Collapsed;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        providerModelsPanel.Visibility = Visibility.Collapsed;
        agentWorldPanel.Visibility = Visibility.Visible;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: false);
        UpdateNavigationTheme();
    }

    public void ShowAgentPanel()
    {
        transcriptPanel.Visibility = Visibility.Collapsed;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        providerModelsPanel.Visibility = Visibility.Collapsed;
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Visible;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: true);
        UpdateNavigationTheme();
    }

    public void ShowCollaboratePanel()
    {
        transcriptPanel.Visibility = Visibility.Collapsed;
        experimentLabPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        providerModelsPanel.Visibility = Visibility.Collapsed;
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Visible;
        SetSectionChromeVisible(collaborate: true, agent: false);
        UpdateNavigationTheme();
    }

    public void ShowExperimentLabPanel()
    {
        transcriptPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        providerModelsPanel.Visibility = Visibility.Collapsed;
        experimentLabPanel.Visibility = Visibility.Visible;
        agentWorldPanel.Visibility = Visibility.Collapsed;
        agentWorkspacePanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        SetSectionChromeVisible(collaborate: false, agent: false, experiment: true);
        UpdateNavigationTheme();
    }

    public void SetAppSettingsVisible(bool visible)
    {
        if (visible)
        {
            ArenaMotion.RevealOverlay(appSettingsPanel);
        }
        else
        {
            ArenaMotion.HideOverlay(appSettingsPanel);
        }

        ApplyAppSettingsButtonState(appSettingsButton, visible);
    }

    public void ToggleAppSettings()
    {
        SetAppSettingsVisible(appSettingsPanel.Visibility != Visibility.Visible);
    }

    public void UpdateNavigationTheme()
    {
        ApplyNavigationButtonState(
            arenaNavButton,
            transcriptPanel.Visibility == Visibility.Visible
                || customMatchPanel.Visibility == Visibility.Visible
                || providerModelsPanel.Visibility == Visibility.Visible
                || agentWorldPanel.Visibility == Visibility.Visible,
            resourceBrush);
        ApplyNavigationButtonState(experimentLabNavButton, experimentLabPanel.Visibility == Visibility.Visible, resourceBrush);
        ApplyNavigationButtonState(customMatchNavButton, false, resourceBrush);
        ApplyNavigationButtonState(agentNavButton, agentWorkspacePanel.Visibility == Visibility.Visible, resourceBrush);
        ApplyNavigationButtonState(collaborateNavButton, collaboratePanel.Visibility == Visibility.Visible, resourceBrush);
    }

    internal static string SelectedThemeId(IReadOnlyList<ThemePalette> themes, string themeId)
    {
        return themes.Any(item => item.Id == themeId)
            ? themeId
            : "dark-blue";
    }

    internal static void ApplyNavigationButtonState(Button button, bool active, Func<string, Brush> resourceBrush)
    {
        var wasActive = AutomationProperties.GetItemStatus(button).Equals("current page", StringComparison.Ordinal);
        button.Background = active
            ? resourceBrush("NavActiveBrush")
            : Brushes.Transparent;
        button.BorderBrush = active ? resourceBrush("PrimaryBorderBrush") : Brushes.Transparent;
        button.Foreground = active ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush");
        AutomationProperties.SetItemStatus(button, active ? "current page" : "not current page");
        if (active && !wasActive)
        {
            ArenaMotion.NavigationSelected(button);
        }
    }

    internal static void ApplyAppSettingsButtonState(Button button, bool visible)
    {
        button.SetResourceReference(
            Control.BackgroundProperty,
            visible ? "NavActiveBrush" : "InputBrush");
        button.SetResourceReference(
            Control.BorderBrushProperty,
            visible ? "PrimaryBorderBrush" : "DisabledBorderBrush");
        button.ToolTip = visible ? "Hide Settings" : "App Settings";
        AutomationProperties.SetName(button, visible ? "Close app settings" : "Open app settings");
        AutomationProperties.SetHelpText(
            button,
            visible
                ? "Close app settings and return to the current workspace."
                : "Open provider, internet, appearance, and app behavior settings.");
        AutomationProperties.SetItemStatus(button, visible ? "expanded" : "collapsed");
    }

    private void SetSectionChromeVisible(bool collaborate, bool agent, bool experiment = false)
    {
        var arenaVisible = !collaborate && !agent;
        arenaTopBarMetrics.Visibility = arenaVisible ? Visibility.Visible : Visibility.Collapsed;
        agentTopBarMetrics.Visibility = agent ? Visibility.Visible : Visibility.Collapsed;
        collaborateTopBarMetrics.Visibility = collaborate ? Visibility.Visible : Visibility.Collapsed;
        arenaRightRailPanel.Visibility = arenaVisible && !experiment ? Visibility.Visible : Visibility.Collapsed;
        experimentRightRailPanel.Visibility = experiment ? Visibility.Visible : Visibility.Collapsed;
        agentRightRailPanel.Visibility = agent ? Visibility.Visible : Visibility.Collapsed;
        collaborateRightRailPanel.Visibility = collaborate ? Visibility.Visible : Visibility.Collapsed;
        arenaSessionOverviewPanel.Visibility = arenaVisible && !experiment ? Visibility.Visible : Visibility.Collapsed;
        arenaLiveAgentsPanel.Visibility = arenaVisible && !experiment ? Visibility.Visible : Visibility.Collapsed;
        agentLeftRailContextPanel.Visibility = agent ? Visibility.Visible : Visibility.Collapsed;
        collaborateLeftRailContextPanel.Visibility = collaborate ? Visibility.Visible : Visibility.Collapsed;
        experimentLeftRailContextPanel.Visibility = experiment ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Updates both the owner scope and the application scope. Context menus,
    /// tooltips, and nested popups live in detached presentation sources, so
    /// they cannot see a palette that is present only on MainWindow.Resources.
    /// </summary>
    internal static void ApplyThemeResources(ResourceDictionary ownerResources, ThemePalette theme)
    {
        ArgumentNullException.ThrowIfNull(ownerResources);
        ArgumentNullException.ThrowIfNull(theme);

        ApplyThemeResourcesCore(ownerResources, theme);
        if (Application.Current?.Resources is { } applicationResources
            && !ReferenceEquals(applicationResources, ownerResources))
        {
            ApplyThemeResourcesCore(applicationResources, theme);
        }
    }

    private static void ApplyThemeResourcesCore(ResourceDictionary resources, ThemePalette theme)
    {
        SetBrush(resources, "AppBackgroundBrush", theme.AppBackground);
        SetBrush(resources, "TopBarBrush", theme.TopBar);
        SetBrush(resources, "PanelBrush", theme.Panel);
        SetBrush(resources, "CardBrush", theme.Card);
        SetBrush(resources, "InputBrush", theme.Input);
        SetBrush(resources, "TranscriptHeaderBrush", theme.Panel);
        SetBrush(resources, "TranscriptBodyBrush", theme.Card);
        SetBrush(resources, "ControlBorderBrush", theme.Border);
        SetBrush(resources, "TextBrush", theme.Text);
        SetBrush(resources, "MutedTextBrush", theme.MutedText);
        SetBrush(resources, "PrimaryBrush", theme.Primary);
        SetBrush(resources, "PrimaryBorderBrush", theme.PrimaryBorder);
        SetBrush(resources, "AssistBrush", theme.Assist);
        SetBrush(resources, "AssistBorderBrush", theme.AssistBorder);
        SetBrush(resources, "DangerBrush", theme.Danger);
        SetBrush(resources, "DangerBorderBrush", theme.DangerBorder);
        SetBrush(resources, "DangerTextBrush", theme.DangerText);
        SetBrush(resources, "DisabledBrush", theme.Disabled);
        SetBrush(resources, "DisabledBorderBrush", theme.DisabledBorder);
        SetBrush(resources, "DisabledTextBrush", theme.DisabledText);
        SetBrush(resources, "HoverBorderBrush", theme.HoverBorder);
        SetBrush(resources, "NavHoverBrush", theme.NavHover);
        SetBrush(resources, "NavActiveBrush", theme.NavActive);
        SetBrush(resources, "NavPressedBrush", theme.NavPressed);
        SetBrush(resources, "PressedPrimaryBrush", theme.PressedPrimary);
        SetBrush(resources, "OverlayBrush", theme.Overlay);
        SetBrush(resources, "AlphaAccentBrush", theme.AlphaAccent);
        SetBrush(resources, "BetaAccentBrush", theme.BetaAccent);
        SetBrush(resources, "GammaAccentBrush", theme.GammaAccent);
        SetBrush(resources, "DeltaAccentBrush", theme.DeltaAccent);
        SetBrush(resources, "NarratorAccentBrush", theme.NarratorAccent);
        SetBrush(resources, "OperatorAccentBrush", theme.OperatorAccent);

        // Semantic accents must follow the active palette. Fixed pastels were
        // readable on Dark Blue but fell below non-text contrast on Light.
        SetBrush(resources, "Arena.Brush.FocusRing", theme.PrimaryBorder);
        SetBrush(resources, "Arena.Brush.FocusRingInner", theme.Text);
        SetBrush(resources, "Arena.Brush.Info", theme.StatusInfo);
        SetBrush(resources, "Arena.Brush.Success", theme.StatusSuccess);
        SetBrush(resources, "Arena.Brush.Warning", theme.StatusWarning);
        SetBrush(resources, "Arena.Brush.Critical", theme.StatusCritical);
    }

    private static void SetBrush(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
