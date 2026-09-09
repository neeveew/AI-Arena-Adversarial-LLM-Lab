using System.Windows;

namespace AIArena.Wpf;

public partial class MainWindow
{
    private ConversationStartCoordinator? _conversationStartCoordinator;

    private void InitializeConversationStart()
    {
        _conversationStartCoordinator = new ConversationStartCoordinator(
            OperatorComposerCard, OperatorComposerDock, ConversationStartComposerHost,
            ConversationStartPanel, ConversationStartHintText, SendAndStartConversationButton,
            OperatorTurnText, OperatorTurn, () => _lastRenderedSnapshot, () => _activeSession,
            () => ArenaRun.StartAutoChatAsync(), () => ShowTranscriptPanel(clearFilters: false),
            SetArenaRunStatus,
            opening =>
            {
                OperatorSteeringAids.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
                TranscriptItems.Visibility = opening ? Visibility.Collapsed : Visibility.Visible;
                OperatorTurnText.MinHeight = opening ? 140 : 64;
            });
    }

    private async void SendAndStartConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationStartCoordinator is not null)
            await _conversationStartCoordinator.SendAndStartAsync();
    }
}
