using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AIArena.Wpf.Controls;

namespace AIArena.Wpf;

public partial class MainWindow
{
    // Compatibility aliases let the existing coordinators retain their behavioral
    // contracts while the reusable control owns the top-bar visual tree.
    private Grid TopBarLayoutGrid => ShellTopBar.LayoutGridTarget;
    private Grid TopBarStatus => ShellTopBar.StatusHostTarget;
    private Grid ArenaTopBarMetrics => ShellTopBar.ArenaMetricsTarget;
    private Grid AgentTopBarMetrics => ShellTopBar.AgentMetricsTarget;
    private Grid CollaborateTopBarMetrics => ShellTopBar.CollaborateMetricsTarget;
    private TextBlock TopMatchValue => ShellTopBar.TopMatchValueTarget;
    private Border TopProviderStatusButton => ShellTopBar.TopProviderStatusButtonTarget;
    private TextBlock TopProviderValue => ShellTopBar.TopProviderValueTarget;
    private TextBlock TopCurrentTurnValue => ShellTopBar.TopCurrentTurnValueTarget;
    private TextBlock TopTurnsValue => ShellTopBar.TopTurnsValueTarget;
    private TextBlock AgentTopWorkspaceValue => ShellTopBar.AgentTopWorkspaceValueTarget;
    private Border AgentTopProviderStatusButton => ShellTopBar.AgentTopProviderStatusButtonTarget;
    private TextBlock AgentTopProviderValue => ShellTopBar.AgentTopProviderValueTarget;
    private TextBlock AgentTopModeValue => ShellTopBar.AgentTopModeValueTarget;
    private Border CollaborateTopProviderStatusButton => ShellTopBar.CollaborateTopProviderStatusButtonTarget;
    private TextBlock CollaborateTopProviderValue => ShellTopBar.CollaborateTopProviderValueTarget;
    private TextBlock CollaborateTopModeValue => ShellTopBar.CollaborateTopModeValueTarget;
    private TextBlock CollaborateTopTeamValue => ShellTopBar.CollaborateTopTeamValueTarget;
    private Popup ProviderHealthPopup => ShellTopBar.ProviderHealthPopupTarget;
    private Border ProviderHealthPopupContent => ShellTopBar.ProviderHealthPopupContentTarget;
    private TextBlock ProviderHealthStatusText => ShellTopBar.ProviderHealthStatusTextTarget;
    private Button ProviderHealthCloseButton => ShellTopBar.ProviderHealthCloseButtonTarget;
    private TextBlock ProviderHealthBaseUrlText => ShellTopBar.ProviderHealthBaseUrlTextTarget;
    private TextBlock ProviderHealthModelCountText => ShellTopBar.ProviderHealthModelCountTextTarget;
    private TextBlock ProviderHealthDefaultModelText => ShellTopBar.ProviderHealthDefaultModelTextTarget;
    private TextBlock ProviderHealthLastCheckText => ShellTopBar.ProviderHealthLastCheckTextTarget;
    private Border ProviderHealthModelWarning => ShellTopBar.ProviderHealthModelWarningTarget;
    private TextBlock ProviderHealthModelWarningText => ShellTopBar.ProviderHealthModelWarningTextTarget;
    private TextBlock ProviderHealthLastErrorText => ShellTopBar.ProviderHealthLastErrorTextTarget;
    private Button ProviderHealthTestButton => ShellTopBar.ProviderHealthTestButtonTarget;
    private Button ProviderHealthRefreshModelsButton => ShellTopBar.ProviderHealthRefreshModelsButtonTarget;
    private TextBlock ArenaRunStatus => ShellTopBar.ArenaRunStatusTarget;
    private TextBlock SaveStatusText => ShellTopBar.SaveStatusTextTarget;
    private WrapPanel TopBarCommandPanel => ShellTopBar.CommandPanelTarget;
    private TextBlock ExportStatusText => ShellTopBar.ExportStatusTextTarget;
    private Border LabViewToggleGroup => ShellTopBar.LabViewToggleGroupTarget;
    private Button LabTranscriptViewButton => ShellTopBar.LabTranscriptViewButtonTarget;
    private Button LabWorldViewButton => ShellTopBar.LabWorldViewButtonTarget;
    private Button MatchSetupButton => ShellTopBar.MatchSetupButtonTarget;
    private Grid SearchCommandHost => ShellTopBar.SearchCommandHostTarget;
    private Button TranscriptSearchButton => ShellTopBar.TranscriptSearchButtonTarget;
    private Popup TranscriptSearchPopup => ShellTopBar.TranscriptSearchPopupTarget;
    private Border TranscriptSearchPopupContent => ShellTopBar.TranscriptSearchPopupContentTarget;
    private Grid TranscriptSearchPopupFrame => ShellTopBar.TranscriptSearchPopupFrameTarget;
    private Border TranscriptSearchDragHandle => ShellTopBar.TranscriptSearchDragHandleTarget;
    private TextBox TranscriptSearchText => ShellTopBar.TranscriptSearchTextTarget;
    private Button ClearTranscriptSearchButton => ShellTopBar.ClearTranscriptSearchButtonTarget;
    private TextBlock TranscriptSearchResultsHeader => ShellTopBar.TranscriptSearchResultsHeaderTarget;
    private StackPanel TranscriptRecentSearchItems => ShellTopBar.TranscriptRecentSearchItemsTarget;
    private Button ExportTranscriptBottomButton => ShellTopBar.ExportTranscriptBottomButtonTarget;
    private Button TopUserGuideButton => ShellTopBar.TopUserGuideButtonTarget;
    private Grid ViewMenuHost => ShellTopBar.ViewMenuHostTarget;
    private Button ViewMenuButton => ShellTopBar.ViewMenuButtonTarget;
    private Popup ViewMenuPopup => ShellTopBar.ViewMenuPopupTarget;
    private Border ViewMenuPopupContent => ShellTopBar.ViewMenuPopupContentTarget;
    private Button ViewPresetFocusedButton => ShellTopBar.ViewPresetFocusedButtonTarget;
    private Button ViewPresetDiagnosticsButton => ShellTopBar.ViewPresetDiagnosticsButtonTarget;
    private Button ViewPresetCompactButton => ShellTopBar.ViewPresetCompactButtonTarget;
    private Button ViewPresetReviewButton => ShellTopBar.ViewPresetReviewButtonTarget;
    private TextBlock ViewActivePresetText => ShellTopBar.ViewActivePresetTextTarget;
    private CheckBox AgentPerformanceFullCardsCheckBox => ShellTopBar.AgentPerformanceFullCardsCheckBoxTarget;
    private CheckBox CompactTranscriptCheckBox => ShellTopBar.CompactTranscriptCheckBoxTarget;
    private CheckBox TurnCompareCheckBox => ShellTopBar.TurnCompareCheckBoxTarget;
    private CheckBox MatchQualityTimelineCheckBox => ShellTopBar.MatchQualityTimelineCheckBoxTarget;
    private CheckBox BattleReviewCheckBox => ShellTopBar.BattleReviewCheckBoxTarget;
    private CheckBox MemoryNotesCheckBox => ShellTopBar.MemoryNotesCheckBoxTarget;
    private CheckBox FollowChatCheckBox => ShellTopBar.FollowChatCheckBoxTarget;
    private Grid DebugMenuHost => ShellTopBar.DebugMenuHostTarget;
    private Button DebugMenuButton => ShellTopBar.DebugMenuButtonTarget;
    private Popup DebugMenuPopup => ShellTopBar.DebugMenuPopupTarget;
    private Border DebugMenuPopupContent => ShellTopBar.DebugMenuPopupContentTarget;
    private CheckBox DecisionCardCheckBox => ShellTopBar.DecisionCardCheckBoxTarget;
    private CheckBox AutoModeratorCheckBox => ShellTopBar.AutoModeratorCheckBoxTarget;
    private CheckBox StyleFitCheckBox => ShellTopBar.StyleFitCheckBoxTarget;
    private CheckBox VoiceDriftEnforcementCheckBox => ShellTopBar.VoiceDriftEnforcementCheckBoxTarget;
    private CheckBox TranscriptInternetDetailsCheckBox => ShellTopBar.TranscriptInternetDetailsCheckBoxTarget;
    private CheckBox WorldDebugCheckBox => ShellTopBar.WorldDebugCheckBoxTarget;
    private Button RightRailToggleButton => ShellTopBar.RightRailToggleButtonTarget;
    private TextBlock RightRailToggleGlyph => ShellTopBar.RightRailToggleGlyphTarget;
    private Button AppSettingsButton => ShellTopBar.AppSettingsButtonTarget;
    private Path SettingsGearIcon => ShellTopBar.SettingsGearIconTarget;
    private RotateTransform SettingsGearRotate => ShellTopBar.SettingsGearRotateTarget;

    private void ShellTopBar_InteractionRequested(object sender, ShellTopBarInteractionEventArgs e)
    {
        switch (e.Action)
        {
            case ShellTopBarAction.ProviderPointerActivated:
                TopProviderValue_MouseLeftButtonUp(e.SourceElement, Args<MouseButtonEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderKeyboardActivated:
                TopProviderValue_KeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderPopupOpened:
                ProviderHealthPopup_Opened(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.ProviderPopupClosed:
                ProviderHealthPopup_Closed(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.ProviderPopupPreviewKeyDown:
                ProviderHealthPopup_PreviewKeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderPopupCloseRequested:
                ProviderHealthCloseButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderTestRequested:
                ProviderHealthTestButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderModelsRefreshRequested:
                ProviderHealthRefreshModelsButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ProviderSettingsRequested:
                ProviderHealthSettingsButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.LabViewRequested:
                LabViewToggle_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.MatchSetupRequested:
                MatchSetupButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.SearchRequested:
                TranscriptSearchButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.SearchPopupPreviewKeyDown:
                TranscriptSearchPopup_PreviewKeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.SearchDragStarted:
                TranscriptSearchDragHandle_MouseLeftButtonDown(e.SourceElement, Args<MouseButtonEventArgs>(e));
                break;
            case ShellTopBarAction.SearchDragMoved:
                TranscriptSearchDragHandle_MouseMove(e.SourceElement, Args<MouseEventArgs>(e));
                break;
            case ShellTopBarAction.SearchDragCompleted:
                TranscriptSearchDragHandle_MouseLeftButtonUp(e.SourceElement, Args<MouseButtonEventArgs>(e));
                break;
            case ShellTopBarAction.SearchDragCaptureLost:
                TranscriptSearchDragHandle_LostMouseCapture(e.SourceElement, Args<MouseEventArgs>(e));
                break;
            case ShellTopBarAction.SearchTextChanged:
                TranscriptFilter_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.SearchTextKeyDown:
                TranscriptSearchText_KeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.SearchTextPointerPressed:
                TranscriptSearchText_PreviewMouseDown(e.SourceElement, Args<MouseButtonEventArgs>(e));
                break;
            case ShellTopBarAction.SearchClearRequested:
                ClearTranscriptSearchButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.SearchAllSessionsRequested:
                SearchAllSessionsButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.TranscriptExportRequested:
                ExportTranscriptButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.CommandPaletteRequested:
                ShowCommandPalette();
                break;
            case ShellTopBarAction.UserGuideRequested:
                OpenUserGuideButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ViewMenuRequested:
                ViewMenuButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ViewMenuOpened:
                ViewMenuPopup_Opened(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.ViewMenuClosed:
                ViewMenuPopup_Closed(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.ViewMenuPreviewKeyDown:
                ViewMenuPopup_PreviewKeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.ViewPresetFocusedRequested:
                ViewPresetFocused_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ViewPresetDiagnosticsRequested:
                ViewPresetDiagnostics_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ViewPresetCompactRequested:
                ViewPresetCompact_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.ViewPresetReviewRequested:
                ViewPresetReview_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.FullAgentCardsChanged:
                AgentPerformanceFullCardsCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.CompactTranscriptChanged:
                CompactTranscriptCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.TurnCompareChanged:
                TurnCompareCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.QualityTimelineChanged:
                MatchQualityTimelineCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.BattleReviewChanged:
                BattleReviewCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.MemoryNotesChanged:
                MemoryNotesCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.FollowChatChanged:
                FollowChatCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.DebugMenuRequested:
                DebugMenuButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.DebugMenuOpened:
                DebugMenuPopup_Opened(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.DebugMenuClosed:
                DebugMenuPopup_Closed(e.SourceElement, e.OriginalEventArgs);
                break;
            case ShellTopBarAction.DebugMenuPreviewKeyDown:
                DebugMenuPopup_PreviewKeyDown(e.SourceElement, Args<KeyEventArgs>(e));
                break;
            case ShellTopBarAction.DecisionCardChanged:
                DecisionCardCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.AutoModeratorChanged:
                AutoModeratorCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.StyleFitChanged:
                StyleFitCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.VoiceDriftChanged:
                VoiceDriftEnforcementCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.InternetDetailsChanged:
                TranscriptInternetDetailsCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.WorldDebugChanged:
                WorldDebugCheckBox_Changed(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.RightRailToggleRequested:
                RightRailToggleButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
            case ShellTopBarAction.AppSettingsRequested:
                AppSettingsButton_Click(e.SourceElement, Args<RoutedEventArgs>(e));
                break;
        }
    }

    private static TEventArgs Args<TEventArgs>(ShellTopBarInteractionEventArgs e)
        where TEventArgs : EventArgs => (TEventArgs)e.OriginalEventArgs;
}
