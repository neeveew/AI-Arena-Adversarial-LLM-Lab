using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AIArena.Wpf.ViewModels;

namespace AIArena.Wpf.Controls;

public enum ShellTopBarAction
{
    ProviderPointerActivated,
    ProviderKeyboardActivated,
    ProviderPopupOpened,
    ProviderPopupClosed,
    ProviderPopupPreviewKeyDown,
    ProviderPopupCloseRequested,
    ProviderTestRequested,
    ProviderModelsRefreshRequested,
    ProviderSettingsRequested,
    LabViewRequested,
    MatchSetupRequested,
    SearchRequested,
    SearchPopupPreviewKeyDown,
    SearchDragStarted,
    SearchDragMoved,
    SearchDragCompleted,
    SearchDragCaptureLost,
    SearchTextChanged,
    SearchTextKeyDown,
    SearchTextPointerPressed,
    SearchClearRequested,
    SearchAllSessionsRequested,
    TranscriptExportRequested,
    UserGuideRequested,
    ViewMenuRequested,
    ViewMenuOpened,
    ViewMenuClosed,
    ViewMenuPreviewKeyDown,
    ViewPresetFocusedRequested,
    ViewPresetDiagnosticsRequested,
    ViewPresetCompactRequested,
    ViewPresetReviewRequested,
    FullAgentCardsChanged,
    CompactTranscriptChanged,
    TurnCompareChanged,
    QualityTimelineChanged,
    BattleReviewChanged,
    MemoryNotesChanged,
    FollowChatChanged,
    DebugMenuRequested,
    DebugMenuOpened,
    DebugMenuClosed,
    DebugMenuPreviewKeyDown,
    DecisionCardChanged,
    AutoModeratorChanged,
    StyleFitChanged,
    VoiceDriftChanged,
    InternetDetailsChanged,
    WorldDebugChanged,
    RightRailToggleRequested,
    AppSettingsRequested
}

public delegate void ShellTopBarInteractionEventHandler(object sender, ShellTopBarInteractionEventArgs e);

public sealed class ShellTopBarInteractionEventArgs : RoutedEventArgs
{
    internal ShellTopBarInteractionEventArgs(
        RoutedEvent routedEvent,
        ShellTopBarAction action,
        object sourceElement,
        EventArgs originalEventArgs)
        : base(routedEvent)
    {
        Action = action;
        SourceElement = sourceElement;
        OriginalEventArgs = originalEventArgs;
    }

    public ShellTopBarAction Action { get; }

    public object SourceElement { get; }

    public EventArgs OriginalEventArgs { get; }
}

/// <summary>
/// Owns the persistent application status and command bar. The compatibility targets
/// keep existing coordinators stable while visible arena status is supplied through a
/// small bindable presentation model.
/// </summary>
public partial class ShellTopBarControl : UserControl
{
    public static readonly RoutedEvent InteractionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(InteractionRequested),
        RoutingStrategy.Bubble,
        typeof(ShellTopBarInteractionEventHandler),
        typeof(ShellTopBarControl));

    private readonly TextBlock topMatchValue = new() { Text = "-" };
    private readonly TextBlock topProviderValue = new() { Text = "-" };
    private readonly TextBlock topCurrentTurnValue = new() { Text = "-" };
    private readonly TextBlock topTurnsValue = new() { Text = "0" };
    private readonly TextBlock arenaRunStatus = new() { Text = "Ready." };

    public ShellTopBarControl()
    {
        Presentation = new ShellTopBarPresentationViewModel();
        InitializeComponent();
        DataContext = Presentation;
        MirrorText(topMatchValue, value => Presentation.MatchValue = value);
        MirrorText(topProviderValue, value => Presentation.ProviderValue = value);
        MirrorText(topCurrentTurnValue, value => Presentation.CurrentTurnValue = value);
        MirrorText(topTurnsValue, value => Presentation.TurnCountValue = value);
        MirrorText(arenaRunStatus, value => Presentation.ArenaStatus = value);
        MirrorText(ViewActivePresetText, value => Presentation.ViewButtonLabel = ViewButtonLabel(value));
        Loaded += AttachCaptionStateTracking;
    }

    private Window? _captionWindow;

    private void AttachCaptionStateTracking(object sender, RoutedEventArgs e)
    {
        if (_captionWindow is not null)
        {
            return;
        }

        _captionWindow = Window.GetWindow(this);
        if (_captionWindow is null)
        {
            return;
        }

        _captionWindow.StateChanged += (_, _) => UpdateCaptionMaximizeGlyph();
        UpdateCaptionMaximizeGlyph();
        Services.CaptionSnapLayoutService.Attach(_captionWindow, CaptionMaximizeButton, ToggleCaptionMaximize);
    }

    private void ToggleCaptionMaximize()
    {
        if ((_captionWindow ?? Window.GetWindow(this)) is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateCaptionMaximizeGlyph()
    {
        var maximized = _captionWindow?.WindowState == WindowState.Maximized;
        CaptionMaximizeGlyph.Text = maximized ? "" : "";
        CaptionMaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void CaptionMinimizeRequested(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void CaptionMaximizeRequested(object sender, RoutedEventArgs e)
    {
        ToggleCaptionMaximize();
    }

    private void CaptionCloseRequested(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }

    /// <summary>
    /// A custom caption band loses the native right-click system menu, so it is
    /// reopened manually at the pointer.
    /// </summary>
    private void CaptionSystemMenuRequested(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
        {
            return;
        }

        var location = window.PointToScreen(e.GetPosition(window));
        SystemCommands.ShowSystemMenu(window, location);
        e.Handled = true;
    }

    public event ShellTopBarInteractionEventHandler InteractionRequested
    {
        add => AddHandler(InteractionRequestedEvent, value);
        remove => RemoveHandler(InteractionRequestedEvent, value);
    }

    public ShellTopBarPresentationViewModel Presentation { get; }

    public Grid LayoutGridTarget => TopBarLayoutGrid;
    public Grid StatusHostTarget => TopBarStatus;
    public Grid ArenaMetricsTarget => ArenaTopBarMetrics;
    public Grid AgentMetricsTarget => AgentTopBarMetrics;
    public Grid CollaborateMetricsTarget => CollaborateTopBarMetrics;
    public TextBlock TopMatchValueTarget => topMatchValue;
    public Border TopProviderStatusButtonTarget => TopProviderStatusButton;
    public TextBlock TopProviderValueTarget => topProviderValue;
    public TextBlock TopCurrentTurnValueTarget => topCurrentTurnValue;
    public TextBlock TopTurnsValueTarget => topTurnsValue;
    public TextBlock AgentTopWorkspaceValueTarget => AgentTopWorkspaceValue;
    public Border AgentTopProviderStatusButtonTarget => AgentTopProviderStatusButton;
    public TextBlock AgentTopProviderValueTarget => AgentTopProviderValue;
    public TextBlock AgentTopModeValueTarget => AgentTopModeValue;
    public Border CollaborateTopProviderStatusButtonTarget => CollaborateTopProviderStatusButton;
    public TextBlock CollaborateTopProviderValueTarget => CollaborateTopProviderValue;
    public TextBlock CollaborateTopModeValueTarget => CollaborateTopModeValue;
    public TextBlock CollaborateTopTeamValueTarget => CollaborateTopTeamValue;
    public Popup ProviderHealthPopupTarget => ProviderHealthPopup;
    public Border ProviderHealthPopupContentTarget => ProviderHealthPopupContent;
    public TextBlock ProviderHealthStatusTextTarget => ProviderHealthStatusText;
    public Button ProviderHealthCloseButtonTarget => ProviderHealthCloseButton;
    public TextBlock ProviderHealthBaseUrlTextTarget => ProviderHealthBaseUrlText;
    public TextBlock ProviderHealthModelCountTextTarget => ProviderHealthModelCountText;
    public TextBlock ProviderHealthDefaultModelTextTarget => ProviderHealthDefaultModelText;
    public TextBlock ProviderHealthLastCheckTextTarget => ProviderHealthLastCheckText;
    public Border ProviderHealthModelWarningTarget => ProviderHealthModelWarning;
    public TextBlock ProviderHealthModelWarningTextTarget => ProviderHealthModelWarningText;
    public TextBlock ProviderHealthLastErrorTextTarget => ProviderHealthLastErrorText;
    public Button ProviderHealthTestButtonTarget => ProviderHealthTestButton;
    public Button ProviderHealthRefreshModelsButtonTarget => ProviderHealthRefreshModelsButton;
    public TextBlock ArenaRunStatusTarget => arenaRunStatus;
    public TextBlock SaveStatusTextTarget => SaveStatusText;
    public WrapPanel CommandPanelTarget => TopBarCommandPanel;
    public TextBlock ExportStatusTextTarget => ExportStatusText;
    public Border LabViewToggleGroupTarget => LabViewToggleGroup;
    public Button LabTranscriptViewButtonTarget => LabTranscriptViewButton;
    public Button LabWorldViewButtonTarget => LabWorldViewButton;
    public Button MatchSetupButtonTarget => MatchSetupButton;
    public Grid SearchCommandHostTarget => SearchCommandHost;
    public Button TranscriptSearchButtonTarget => TranscriptSearchButton;
    public Popup TranscriptSearchPopupTarget => TranscriptSearchPopup;
    public Border TranscriptSearchPopupContentTarget => TranscriptSearchPopupContent;
    public Grid TranscriptSearchPopupFrameTarget => TranscriptSearchPopupFrame;
    public Border TranscriptSearchDragHandleTarget => TranscriptSearchDragHandle;
    public TextBox TranscriptSearchTextTarget => TranscriptSearchText;
    public Button ClearTranscriptSearchButtonTarget => ClearTranscriptSearchButton;
    public TextBlock TranscriptSearchResultsHeaderTarget => TranscriptSearchResultsHeader;
    public StackPanel TranscriptRecentSearchItemsTarget => TranscriptRecentSearchItems;
    public Button ExportTranscriptBottomButtonTarget => ExportTranscriptBottomButton;
    public Button TopUserGuideButtonTarget => TopUserGuideButton;
    public Grid ViewMenuHostTarget => ViewMenuHost;
    public Button ViewMenuButtonTarget => ViewMenuButton;
    public Popup ViewMenuPopupTarget => ViewMenuPopup;
    public Border ViewMenuPopupContentTarget => ViewMenuPopupContent;
    public Button ViewPresetFocusedButtonTarget => ViewPresetFocusedButton;
    public Button ViewPresetDiagnosticsButtonTarget => ViewPresetDiagnosticsButton;
    public Button ViewPresetCompactButtonTarget => ViewPresetCompactButton;
    public Button ViewPresetReviewButtonTarget => ViewPresetReviewButton;
    public TextBlock ViewActivePresetTextTarget => ViewActivePresetText;
    public CheckBox AgentPerformanceFullCardsCheckBoxTarget => AgentPerformanceFullCardsCheckBox;
    public CheckBox CompactTranscriptCheckBoxTarget => CompactTranscriptCheckBox;
    public CheckBox TurnCompareCheckBoxTarget => TurnCompareCheckBox;
    public CheckBox MatchQualityTimelineCheckBoxTarget => MatchQualityTimelineCheckBox;
    public CheckBox BattleReviewCheckBoxTarget => BattleReviewCheckBox;
    public CheckBox MemoryNotesCheckBoxTarget => MemoryNotesCheckBox;
    public CheckBox FollowChatCheckBoxTarget => FollowChatCheckBox;
    public Grid DebugMenuHostTarget => DebugMenuHost;
    public Button DebugMenuButtonTarget => DebugMenuButton;
    public Popup DebugMenuPopupTarget => DebugMenuPopup;
    public Border DebugMenuPopupContentTarget => DebugMenuPopupContent;
    public CheckBox DecisionCardCheckBoxTarget => DecisionCardCheckBox;
    public CheckBox AutoModeratorCheckBoxTarget => AutoModeratorCheckBox;
    public CheckBox StyleFitCheckBoxTarget => StyleFitCheckBox;
    public CheckBox VoiceDriftEnforcementCheckBoxTarget => VoiceDriftEnforcementCheckBox;
    public CheckBox TranscriptInternetDetailsCheckBoxTarget => TranscriptInternetDetailsCheckBox;
    public CheckBox WorldDebugCheckBoxTarget => WorldDebugCheckBox;
    public Button RightRailToggleButtonTarget => RightRailToggleButton;
    public TextBlock RightRailToggleGlyphTarget => RightRailToggleGlyph;
    public Button AppSettingsButtonTarget => AppSettingsButton;
    public Path SettingsGearIconTarget => SettingsGearIcon;
    public RotateTransform SettingsGearRotateTarget => SettingsGearRotate;

    private static void MirrorText(TextBlock source, Action<string> update)
    {
        update(source.Text);
        DependencyPropertyDescriptor
            .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(source, (_, _) => update(source.Text));
    }

    private static string ViewButtonLabel(string value)
    {
        const string prefix = "Active:";
        var preset = string.IsNullOrWhiteSpace(value)
            ? "Custom"
            : value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value[prefix.Length..].Trim()
                : value.Trim();
        return $"View: {(string.IsNullOrWhiteSpace(preset) ? "Custom" : preset)}";
    }

    private void Forward(ShellTopBarAction action, object source, EventArgs eventArgs) =>
        RaiseEvent(new ShellTopBarInteractionEventArgs(InteractionRequestedEvent, action, source, eventArgs));

    private void ProviderPointerActivated(object sender, MouseButtonEventArgs e) => Forward(ShellTopBarAction.ProviderPointerActivated, sender, e);
    private void ProviderKeyboardActivated(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.ProviderKeyboardActivated, sender, e);
    private void ProviderPopupOpened(object? sender, EventArgs e) => Forward(ShellTopBarAction.ProviderPopupOpened, sender ?? this, e);
    private void ProviderPopupClosed(object? sender, EventArgs e) => Forward(ShellTopBarAction.ProviderPopupClosed, sender ?? this, e);
    private void ProviderPopupPreviewKeyDown(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.ProviderPopupPreviewKeyDown, sender, e);
    private void ProviderPopupCloseRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ProviderPopupCloseRequested, sender, e);
    private void ProviderTestRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ProviderTestRequested, sender, e);
    private void ProviderModelsRefreshRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ProviderModelsRefreshRequested, sender, e);
    private void ProviderSettingsRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ProviderSettingsRequested, sender, e);
    private void LabViewRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.LabViewRequested, sender, e);
    private void MatchSetupRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.MatchSetupRequested, sender, e);
    private void SearchRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.SearchRequested, sender, e);
    private void SearchPopupPreviewKeyDown(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.SearchPopupPreviewKeyDown, sender, e);
    private void SearchDragStarted(object sender, MouseButtonEventArgs e) => Forward(ShellTopBarAction.SearchDragStarted, sender, e);
    private void SearchDragMoved(object sender, MouseEventArgs e) => Forward(ShellTopBarAction.SearchDragMoved, sender, e);
    private void SearchDragCompleted(object sender, MouseButtonEventArgs e) => Forward(ShellTopBarAction.SearchDragCompleted, sender, e);
    private void SearchDragCaptureLost(object sender, MouseEventArgs e) => Forward(ShellTopBarAction.SearchDragCaptureLost, sender, e);
    private void SearchTextChanged(object sender, TextChangedEventArgs e) => Forward(ShellTopBarAction.SearchTextChanged, sender, e);
    private void SearchTextKeyDown(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.SearchTextKeyDown, sender, e);
    private void SearchTextPointerPressed(object sender, MouseButtonEventArgs e) => Forward(ShellTopBarAction.SearchTextPointerPressed, sender, e);
    private void SearchClearRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.SearchClearRequested, sender, e);
    private void SearchAllSessionsRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.SearchAllSessionsRequested, sender, e);
    private void TranscriptExportRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.TranscriptExportRequested, sender, e);
    private void UserGuideRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.UserGuideRequested, sender, e);
    private void ViewMenuRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ViewMenuRequested, sender, e);
    private void ViewMenuOpened(object? sender, EventArgs e) => Forward(ShellTopBarAction.ViewMenuOpened, sender ?? this, e);
    private void ViewMenuClosed(object? sender, EventArgs e) => Forward(ShellTopBarAction.ViewMenuClosed, sender ?? this, e);
    private void ViewMenuPreviewKeyDown(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.ViewMenuPreviewKeyDown, sender, e);
    private void ViewPresetFocusedRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ViewPresetFocusedRequested, sender, e);
    private void ViewPresetDiagnosticsRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ViewPresetDiagnosticsRequested, sender, e);
    private void ViewPresetCompactRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ViewPresetCompactRequested, sender, e);
    private void ViewPresetReviewRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.ViewPresetReviewRequested, sender, e);
    private void FullAgentCardsChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.FullAgentCardsChanged, sender, e);
    private void CompactTranscriptChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.CompactTranscriptChanged, sender, e);
    private void TurnCompareChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.TurnCompareChanged, sender, e);
    private void QualityTimelineChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.QualityTimelineChanged, sender, e);
    private void BattleReviewChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.BattleReviewChanged, sender, e);
    private void MemoryNotesChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.MemoryNotesChanged, sender, e);
    private void FollowChatChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.FollowChatChanged, sender, e);
    private void DebugMenuRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.DebugMenuRequested, sender, e);
    private void DebugMenuOpened(object? sender, EventArgs e) => Forward(ShellTopBarAction.DebugMenuOpened, sender ?? this, e);
    private void DebugMenuClosed(object? sender, EventArgs e) => Forward(ShellTopBarAction.DebugMenuClosed, sender ?? this, e);
    private void DebugMenuPreviewKeyDown(object sender, KeyEventArgs e) => Forward(ShellTopBarAction.DebugMenuPreviewKeyDown, sender, e);
    private void DecisionCardChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.DecisionCardChanged, sender, e);
    private void AutoModeratorChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.AutoModeratorChanged, sender, e);
    private void StyleFitChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.StyleFitChanged, sender, e);
    private void VoiceDriftChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.VoiceDriftChanged, sender, e);
    private void InternetDetailsChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.InternetDetailsChanged, sender, e);
    private void WorldDebugChanged(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.WorldDebugChanged, sender, e);
    private void RightRailToggleRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.RightRailToggleRequested, sender, e);
    private void AppSettingsRequested(object sender, RoutedEventArgs e) => Forward(ShellTopBarAction.AppSettingsRequested, sender, e);
}
