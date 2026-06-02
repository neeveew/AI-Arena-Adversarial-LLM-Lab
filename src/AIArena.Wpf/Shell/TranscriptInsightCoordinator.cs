using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class TranscriptInsightCoordinator
{
    private readonly Action refreshTranscript;
    private readonly Action scrollTranscriptToTop;
    private readonly List<TranscriptMessage> turnCompareSelection = [];

    private bool turnCompareSuppressAutoSeed;
    private int? timelineSelectedTurnFilter;

    public TranscriptInsightCoordinator(
        Action refreshTranscript,
        Action scrollTranscriptToTop)
    {
        this.refreshTranscript = refreshTranscript;
        this.scrollTranscriptToTop = scrollTranscriptToTop;
    }

    public int? TimelineSelectedTurnFilter => timelineSelectedTurnFilter;

    public bool HasTurnCompareSelection => turnCompareSelection.Count > 0;

    public IReadOnlyList<TranscriptMessage> SelectedTurnCompareMessages => turnCompareSelection.Take(2).ToArray();

    public void SetTurnCompareMode(bool enabled)
    {
        if (!enabled)
        {
            turnCompareSelection.Clear();
        }

        turnCompareSuppressAutoSeed = false;
    }

    public void ClearTurnCompareSelection(bool suppressAutoSeed, bool refresh = false)
    {
        turnCompareSuppressAutoSeed = suppressAutoSeed;
        turnCompareSelection.Clear();
        if (refresh)
        {
            refreshTranscript();
        }
    }

    public void ReselectLatest(IReadOnlyList<TranscriptMessage> visibleMessages)
    {
        turnCompareSuppressAutoSeed = false;
        turnCompareSelection.Clear();
        turnCompareSelection.AddRange(visibleMessages
            .Where(CanCompareMessage)
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Take(2));
        refreshTranscript();
    }

    public void EnsureTurnCompareSelection(IReadOnlyList<TranscriptMessage> visibleMessages)
    {
        var retained = turnCompareSelection
            .Where(selected => visibleMessages.Any(message => SameTranscriptMessage(message, selected)))
            .Take(2)
            .ToList();

        if (!turnCompareSuppressAutoSeed)
        {
            foreach (var message in visibleMessages
                .Where(CanCompareMessage)
                .OrderByDescending(message => message.Turn)
                .ThenByDescending(message => message.CreatedAt))
            {
                if (retained.Count >= 2)
                {
                    break;
                }

                if (!retained.Any(selected => SameTranscriptMessage(selected, message)))
                {
                    retained.Add(message);
                }
            }
        }

        turnCompareSelection.Clear();
        turnCompareSelection.AddRange(retained);
    }

    public bool IsTurnSelectedForCompare(TranscriptMessage message)
    {
        return turnCompareSelection.Any(selected => SameTranscriptMessage(selected, message));
    }

    public void ToggleTurnCompareMessage(TranscriptMessage message)
    {
        if (!CanCompareMessage(message))
        {
            return;
        }

        turnCompareSuppressAutoSeed = true;
        var existing = turnCompareSelection.FindIndex(selected => SameTranscriptMessage(selected, message));
        if (existing >= 0)
        {
            turnCompareSelection.RemoveAt(existing);
        }
        else
        {
            if (turnCompareSelection.Count >= 2)
            {
                turnCompareSelection.RemoveAt(0);
            }

            turnCompareSelection.Add(message);
        }

        refreshTranscript();
    }

    public void ClearTimelineFilterIfMissing(IReadOnlyList<TranscriptMessage> messages)
    {
        if (timelineSelectedTurnFilter is int selectedTurn
            && messages.All(message => message.Turn != selectedTurn))
        {
            timelineSelectedTurnFilter = null;
        }
    }

    public void ToggleTimelineTurnFilter(int turn)
    {
        timelineSelectedTurnFilter = timelineSelectedTurnFilter == turn ? null : turn;
        refreshTranscript();
        scrollTranscriptToTop();
    }

    public void ClearTimelineTurnFilter(bool refresh = true)
    {
        if (timelineSelectedTurnFilter is null)
        {
            return;
        }

        timelineSelectedTurnFilter = null;
        if (refresh)
        {
            refreshTranscript();
        }
    }

    public static bool CanCompareMessage(TranscriptMessage message)
    {
        return message.Turn > 0 && !string.IsNullOrWhiteSpace(message.Text);
    }

    private static bool SameTranscriptMessage(TranscriptMessage left, TranscriptMessage right)
    {
        return left.Turn == right.Turn
            && left.SpeakerId.Equals(right.SpeakerId, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(left.CreatedAt - right.CreatedAt) < 0.001;
    }
}
