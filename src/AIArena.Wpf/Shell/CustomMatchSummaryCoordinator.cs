using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class CustomMatchSummaryCoordinator
{
    private readonly Panel scenarioPreviewItems;
    private readonly Panel castPreviewItems;
    private readonly ShellCardFactory shellCards;
    private readonly MatchLockCoordinator matchLock;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;

    public CustomMatchSummaryCoordinator(
        Panel scenarioPreviewItems,
        Panel castPreviewItems,
        ShellCardFactory shellCards,
        MatchLockCoordinator matchLock,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<Brush, Brush, double, Brush> blendBrush)
    {
        this.scenarioPreviewItems = scenarioPreviewItems;
        this.castPreviewItems = castPreviewItems;
        this.shellCards = shellCards;
        this.matchLock = matchLock;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.blendBrush = blendBrush;
    }

    public void Populate(ArenaViewSnapshot snapshot)
    {
        scenarioPreviewItems.Children.Clear();
        castPreviewItems.Children.Clear();
        matchLock.ClearControls();

        PopulateScenario(snapshot);
        PopulateCast(snapshot);
    }

    internal static string ScenarioTopicText(string topic)
    {
        return string.IsNullOrWhiteSpace(topic) ? "No topic is set for this match yet." : topic;
    }

    internal static string ScenarioGlobalText(string global)
    {
        return string.IsNullOrWhiteSpace(global) ? "No global instruction is set for this match yet." : global;
    }

    internal static string AgentPersonaText(string persona)
    {
        return string.IsNullOrWhiteSpace(persona) ? "(no persona)" : persona;
    }

    internal static string NarratorPersonaText(string persona)
    {
        return string.IsNullOrWhiteSpace(persona) ? "(no narrator persona)" : persona;
    }

    private void PopulateScenario(ArenaViewSnapshot snapshot)
    {
        scenarioPreviewItems.Children.Add(matchLock.CreateLockCard(
            "topic",
            "Topic",
            ScenarioTopicText(snapshot.ScenarioTopic),
            resourceBrush("CardBrush"),
            snapshot.TopicLocked ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush"),
            snapshot.TopicLocked));
        scenarioPreviewItems.Children.Add(matchLock.CreateLockCard(
            "global",
            "Global",
            ScenarioGlobalText(snapshot.ScenarioGlobal),
            resourceBrush("CardBrush"),
            snapshot.GlobalLocked ? resourceBrush("TextBrush") : resourceBrush("MutedTextBrush"),
            snapshot.GlobalLocked));
    }

    private void PopulateCast(ArenaViewSnapshot snapshot)
    {
        if (snapshot.Agents.Count == 0)
        {
            castPreviewItems.Children.Add(shellCards.CreateEmptyStateCard(
                "Cast",
                "No active cast is available for this session yet.",
                resourceBrush("MutedTextBrush")));
        }
        else
        {
            foreach (var agent in snapshot.Agents)
            {
                var accent = accentForSpeaker(agent.Id);
                castPreviewItems.Children.Add(matchLock.CreateLockCard(
                    agent.Id,
                    MatchLockCoordinator.FormatCastPreviewTitle(agent.Id, agent.Name),
                    AgentPersonaText(agent.Persona),
                    blendBrush(resourceBrush("CardBrush"), accent, 0.16),
                    accent,
                    agent.Locked,
                    agent.VoiceStyle,
                    agent.PressureProfile));
            }
        }

        castPreviewItems.Children.Add(matchLock.CreateLockCard(
            "narrator",
            "Narrator",
            NarratorPersonaText(snapshot.NarratorPersona),
            blendBrush(resourceBrush("CardBrush"), resourceBrush("NarratorAccentBrush"), 0.16),
            resourceBrush("NarratorAccentBrush"),
            snapshot.NarratorLocked,
            snapshot.NarratorVoiceStyle));
    }
}
