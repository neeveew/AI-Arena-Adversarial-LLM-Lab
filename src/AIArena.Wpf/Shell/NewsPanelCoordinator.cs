using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class NewsPanelCoordinator
{
    private readonly Panel newsItems;
    private readonly TextBlock newsSummaryText;
    private readonly ShellCardFactory shellCards;
    private readonly TranscriptAdjunctCoordinator transcriptAdjunct;
    private readonly Func<string, Brush> resourceBrush;

    public NewsPanelCoordinator(
        Panel newsItems,
        TextBlock newsSummaryText,
        ShellCardFactory shellCards,
        TranscriptAdjunctCoordinator transcriptAdjunct,
        Func<string, Brush> resourceBrush)
    {
        this.newsItems = newsItems;
        this.newsSummaryText = newsSummaryText;
        this.shellCards = shellCards;
        this.transcriptAdjunct = transcriptAdjunct;
        this.resourceBrush = resourceBrush;
    }

    public void Populate(IReadOnlyList<TranscriptMessage> messages)
    {
        newsItems.Children.Clear();
        var newsMessages = messages
            .Where(IsNewsMessage)
            .OrderByDescending(message => message.Turn)
            .ToArray();

        newsSummaryText.Text = SummaryText(newsMessages);
        if (newsMessages.Length == 0)
        {
            newsItems.Children.Add(shellCards.CreateEmptyStateCard(
                "News inspector",
                "No fetched or curated news items in this session yet.",
                resourceBrush("NarratorAccentBrush")));
            return;
        }

        foreach (var message in newsMessages)
        {
            newsItems.Children.Add(transcriptAdjunct.CreateNewsInspectorCard(message));
        }
    }

    public void PopulateFallback()
    {
        newsItems.Children.Clear();
        newsSummaryText.Text = "No live snapshot data.";
        newsItems.Children.Add(shellCards.CreateCard(
            "News",
            "No live snapshot data.",
            resourceBrush("CardBrush"),
            resourceBrush("NarratorAccentBrush")));
    }

    internal static bool IsNewsMessage(TranscriptMessage message)
    {
        return message.Kind.Equals("news", StringComparison.OrdinalIgnoreCase)
            || message.Kind.StartsWith("internet", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(message.InternetTool)
            || message.InternetSources.Count > 0;
    }

    internal static string SummaryText(IReadOnlyList<TranscriptMessage> newsMessages)
    {
        if (newsMessages.Count == 0)
        {
            return "No internet activity in this session.";
        }

        var sourceCount = newsMessages.Sum(message => message.InternetSources.Count);
        var pendingCount = newsMessages.Count(message => message.Kind.Equals("internet_approval", StringComparison.OrdinalIgnoreCase)
            && message.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
        return $"{newsMessages.Count} internet item(s), {sourceCount} source(s)"
            + (pendingCount > 0 ? $", {pendingCount} waiting for approval" : "");
    }
}
