using System.Windows;
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
    private readonly Button customMatchNavButton;
    private readonly Button collaborateNavButton;
    private readonly Button appSettingsButton;
    private readonly FrameworkElement transcriptPanel;
    private readonly FrameworkElement customMatchPanel;
    private readonly FrameworkElement collaboratePanel;
    private readonly FrameworkElement newsPanel;
    private readonly FrameworkElement arenaTopBarMetrics;
    private readonly FrameworkElement collaborateTopBarMetrics;
    private readonly FrameworkElement arenaRightRailPanel;
    private readonly FrameworkElement collaborateRightRailPanel;
    private readonly FrameworkElement arenaSessionOverviewPanel;
    private readonly FrameworkElement arenaLiveAgentsPanel;
    private readonly FrameworkElement collaborateLeftRailContextPanel;
    private readonly FrameworkElement appSettingsPanel;
    private readonly Action<ThemePalette> setTheme;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Action refreshUserGuideTheme;
    private readonly Func<bool> hasActiveSession;
    private readonly Action<string> refreshActiveSession;

    private bool isSelectingTheme;

    public ShellNavigationCoordinator(
        Window owner,
        WpfSettingsStore settingsStore,
        Func<WpfSettings> settings,
        ComboBox themePicker,
        Button arenaNavButton,
        Button customMatchNavButton,
        Button collaborateNavButton,
        Button appSettingsButton,
        FrameworkElement transcriptPanel,
        FrameworkElement customMatchPanel,
        FrameworkElement collaboratePanel,
        FrameworkElement newsPanel,
        FrameworkElement arenaTopBarMetrics,
        FrameworkElement collaborateTopBarMetrics,
        FrameworkElement arenaRightRailPanel,
        FrameworkElement collaborateRightRailPanel,
        FrameworkElement arenaSessionOverviewPanel,
        FrameworkElement arenaLiveAgentsPanel,
        FrameworkElement collaborateLeftRailContextPanel,
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
        this.customMatchNavButton = customMatchNavButton;
        this.collaborateNavButton = collaborateNavButton;
        this.appSettingsButton = appSettingsButton;
        this.transcriptPanel = transcriptPanel;
        this.customMatchPanel = customMatchPanel;
        this.collaboratePanel = collaboratePanel;
        this.newsPanel = newsPanel;
        this.arenaTopBarMetrics = arenaTopBarMetrics;
        this.collaborateTopBarMetrics = collaborateTopBarMetrics;
        this.arenaRightRailPanel = arenaRightRailPanel;
        this.collaborateRightRailPanel = collaborateRightRailPanel;
        this.arenaSessionOverviewPanel = arenaSessionOverviewPanel;
        this.arenaLiveAgentsPanel = arenaLiveAgentsPanel;
        this.collaborateLeftRailContextPanel = collaborateLeftRailContextPanel;
        this.appSettingsPanel = appSettingsPanel;
        this.setTheme = setTheme;
        this.resourceBrush = resourceBrush;
        this.refreshUserGuideTheme = refreshUserGuideTheme;
        this.hasActiveSession = hasActiveSession;
        this.refreshActiveSession = refreshActiveSession;
    }

    public void InitializeThemePicker()
    {
        var currentSettings = settings();
        var themeId = ThemePalette.NormalizeId(currentSettings.ThemeId);
        currentSettings.ThemeId = themeId;
        var themes = ThemePalette.BuiltIn
            .Where(item => !item.Id.Equals("system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
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
        SetBrush("AppBackgroundBrush", theme.AppBackground);
        SetBrush("TopBarBrush", theme.TopBar);
        SetBrush("PanelBrush", theme.Panel);
        SetBrush("CardBrush", theme.Card);
        SetBrush("InputBrush", theme.Input);
        SetBrush("TranscriptHeaderBrush", theme.Panel);
        SetBrush("TranscriptBodyBrush", theme.Card);
        SetBrush("ControlBorderBrush", theme.Border);
        SetBrush("TextBrush", theme.Text);
        SetBrush("MutedTextBrush", theme.MutedText);
        SetBrush("PrimaryBrush", theme.Primary);
        SetBrush("PrimaryBorderBrush", theme.PrimaryBorder);
        SetBrush("AssistBrush", theme.Assist);
        SetBrush("AssistBorderBrush", theme.AssistBorder);
        SetBrush("DangerBrush", theme.Danger);
        SetBrush("DangerBorderBrush", theme.DangerBorder);
        SetBrush("DangerTextBrush", theme.DangerText);
        SetBrush("DisabledBrush", theme.Disabled);
        SetBrush("DisabledBorderBrush", theme.DisabledBorder);
        SetBrush("DisabledTextBrush", theme.DisabledText);
        SetBrush("HoverBorderBrush", theme.HoverBorder);
        SetBrush("NavHoverBrush", ShellUiHelpers.BlendBrush(new SolidColorBrush(theme.Input), new SolidColorBrush(theme.PrimaryBorder), 0.18));
        SetBrush("NavActiveBrush", ShellUiHelpers.BlendBrush(new SolidColorBrush(theme.Input), new SolidColorBrush(theme.PrimaryBorder), 0.24));
        SetBrush("NavPressedBrush", ShellUiHelpers.BlendBrush(new SolidColorBrush(theme.Input), new SolidColorBrush(theme.PrimaryBorder), 0.12));
        SetBrush("PressedPrimaryBrush", theme.PressedPrimary);
        SetBrush("OverlayBrush", theme.Overlay);
        SetBrush("AlphaAccentBrush", theme.AlphaAccent);
        SetBrush("BetaAccentBrush", theme.BetaAccent);
        SetBrush("GammaAccentBrush", theme.GammaAccent);
        SetBrush("DeltaAccentBrush", theme.DeltaAccent);
        SetBrush("NarratorAccentBrush", theme.NarratorAccent);
        SetBrush("OperatorAccentBrush", theme.OperatorAccent);

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
    }

    public void ShowTranscriptPanel()
    {
        transcriptPanel.Visibility = Visibility.Visible;
        customMatchPanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Collapsed;
        newsPanel.Visibility = Visibility.Collapsed;
        SetCollaborateChromeVisible(false);
        UpdateNavigationTheme();
    }

    public void ShowCustomMatchPanel()
    {
        transcriptPanel.Visibility = Visibility.Visible;
        customMatchPanel.Visibility = Visibility.Visible;
        collaboratePanel.Visibility = Visibility.Collapsed;
        newsPanel.Visibility = Visibility.Collapsed;
        SetCollaborateChromeVisible(false);
        UpdateNavigationTheme();
    }

    public void ShowCollaboratePanel()
    {
        transcriptPanel.Visibility = Visibility.Collapsed;
        customMatchPanel.Visibility = Visibility.Collapsed;
        collaboratePanel.Visibility = Visibility.Visible;
        newsPanel.Visibility = Visibility.Collapsed;
        SetCollaborateChromeVisible(true);
        UpdateNavigationTheme();
    }

    public void SetAppSettingsVisible(bool visible)
    {
        appSettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        appSettingsButton.ToolTip = visible ? "Hide Settings" : "App Settings";
    }

    public void ToggleAppSettings()
    {
        SetAppSettingsVisible(appSettingsPanel.Visibility != Visibility.Visible);
    }

    public void UpdateNavigationTheme()
    {
        ApplyNavigationButtonState(
            arenaNavButton,
            transcriptPanel.Visibility == Visibility.Visible || customMatchPanel.Visibility == Visibility.Visible,
            resourceBrush);
        ApplyNavigationButtonState(customMatchNavButton, false, resourceBrush);
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
        button.Background = active
            ? resourceBrush("NavActiveBrush")
            : Brushes.Transparent;
        button.BorderBrush = active ? resourceBrush("PrimaryBorderBrush") : Brushes.Transparent;
        button.Foreground = active ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush");
    }

    private void SetCollaborateChromeVisible(bool visible)
    {
        arenaTopBarMetrics.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        collaborateTopBarMetrics.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        arenaRightRailPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        collaborateRightRailPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        arenaSessionOverviewPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        arenaLiveAgentsPanel.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        collaborateLeftRailContextPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetBrush(string key, Color color)
    {
        owner.Resources[key] = new SolidColorBrush(color);
    }

    private void SetBrush(string key, Brush brush)
    {
        owner.Resources[key] = brush;
    }
}
