using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class SessionOverviewCoordinator
{
    private readonly TextBlock sessionOverviewMatchText;
    private readonly TextBlock sessionOverviewTurnsText;
    private readonly TextBlock sessionOverviewParticipantsText;
    private readonly TextBlock sessionOverviewTokensText;
    private readonly TextBlock sessionOverviewProviderText;
    private readonly TextBlock sessionOverviewContextText;
    private readonly TextBlock topMatchValue;
    private readonly TextBlock topProviderValue;
    private readonly TextBlock topCurrentTurnValue;
    private readonly TextBlock topTurnsValue;
    private readonly FrameworkElement topBarStatus;
    private readonly TextBlock arenaRunStatus;
    private readonly TextBlock settingsProviderStatusText;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<bool> isAutoChatRunning;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<int, string> formatCompactNumber;
    private readonly Func<string, string> shortModelName;
    private readonly Action<ArenaViewSnapshot> populateAgentPerformance;
    private readonly Action<ArenaViewSnapshot> updateProviderHealthPopup;

    public SessionOverviewCoordinator(
        TextBlock sessionOverviewMatchText,
        TextBlock sessionOverviewTurnsText,
        TextBlock sessionOverviewParticipantsText,
        TextBlock sessionOverviewTokensText,
        TextBlock sessionOverviewProviderText,
        TextBlock sessionOverviewContextText,
        TextBlock topMatchValue,
        TextBlock topProviderValue,
        TextBlock topCurrentTurnValue,
        TextBlock topTurnsValue,
        FrameworkElement topBarStatus,
        TextBlock arenaRunStatus,
        TextBlock settingsProviderStatusText,
        Func<bool> isArenaBusy,
        Func<bool> isAutoChatRunning,
        Func<string, Brush> resourceBrush,
        Func<string, Brush> accentForSpeaker,
        Func<int, string> formatCompactNumber,
        Func<string, string> shortModelName,
        Action<ArenaViewSnapshot> populateAgentPerformance,
        Action<ArenaViewSnapshot> updateProviderHealthPopup)
    {
        this.sessionOverviewMatchText = sessionOverviewMatchText;
        this.sessionOverviewTurnsText = sessionOverviewTurnsText;
        this.sessionOverviewParticipantsText = sessionOverviewParticipantsText;
        this.sessionOverviewTokensText = sessionOverviewTokensText;
        this.sessionOverviewProviderText = sessionOverviewProviderText;
        this.sessionOverviewContextText = sessionOverviewContextText;
        this.topMatchValue = topMatchValue;
        this.topProviderValue = topProviderValue;
        this.topCurrentTurnValue = topCurrentTurnValue;
        this.topTurnsValue = topTurnsValue;
        this.topBarStatus = topBarStatus;
        this.arenaRunStatus = arenaRunStatus;
        this.settingsProviderStatusText = settingsProviderStatusText;
        this.isArenaBusy = isArenaBusy;
        this.isAutoChatRunning = isAutoChatRunning;
        this.resourceBrush = resourceBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.formatCompactNumber = formatCompactNumber;
        this.shortModelName = shortModelName;
        this.populateAgentPerformance = populateAgentPerformance;
        this.updateProviderHealthPopup = updateProviderHealthPopup;
    }

    public void UpdateSessionOverview(ArenaViewSnapshot snapshot)
    {
        sessionOverviewMatchText.Text = DisplayStatusValue(snapshot.MatchType);
        sessionOverviewTurnsText.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        sessionOverviewParticipantsText.Text = ParticipantSummary(snapshot);
        sessionOverviewTokensText.Text = formatCompactNumber(TotalCompletionTokens(snapshot));
        sessionOverviewProviderText.Text = ProviderLabel(snapshot.ProviderOnline);
        sessionOverviewProviderText.Foreground = ProviderAccent(snapshot.ProviderOnline);
        var context = MaxPromptContext(snapshot);
        sessionOverviewContextText.Text = context > 0 ? formatCompactNumber(context) : "-";
        populateAgentPerformance(snapshot);
    }

    public void UpdateTopBarStatus(ArenaViewSnapshot snapshot)
    {
        topMatchValue.Text = DisplayStatusValue(snapshot.MatchType);
        topProviderValue.Text = ProviderLabel(snapshot.ProviderOnline);
        topProviderValue.Foreground = ProviderAccent(snapshot.ProviderOnline);
        topProviderValue.ToolTip = $"Provider details - {snapshot.ProviderBaseUrl}";
        var current = CurrentTurnAgent(snapshot);
        topCurrentTurnValue.Text = current?.Id.ToUpperInvariant() ?? "-";
        topCurrentTurnValue.Foreground = current is null ? resourceBrush("TextBrush") : accentForSpeaker(current.Id);
        topCurrentTurnValue.ToolTip = current is null
            ? "No active turn participant."
            : $"{DisplayStatusValue(current.Id)}: {current.Name}\nModel: {CurrentTurnModel(snapshot, current)}";
        topTurnsValue.Text = snapshot.TurnCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        topBarStatus.ToolTip = $"Session: {snapshot.SessionId}\nModel: {CurrentTurnModel(snapshot, current)}";
        if (!isArenaBusy() && !isAutoChatRunning())
        {
            arenaRunStatus.Text = TopRunStateSummary(snapshot, current, shortModelName);
        }

        UpdateSettingsProviderStatus(snapshot);
    }

    public void UpdateSettingsProviderStatus(ArenaViewSnapshot snapshot)
    {
        var status = ProviderLabel(snapshot.ProviderOnline);
        var detail = snapshot.ProviderOnline
            ? snapshot.ProviderModel
            : string.IsNullOrWhiteSpace(snapshot.ProviderLastError)
                ? snapshot.ProviderBaseUrl
                : snapshot.ProviderLastError;
        settingsProviderStatusText.Text = $"Provider {status} - {detail}";
        settingsProviderStatusText.Foreground = ProviderAccent(snapshot.ProviderOnline);
        updateProviderHealthPopup(snapshot);
    }

    internal static AgentState? CurrentTurnAgent(ArenaViewSnapshot snapshot)
    {
        var active = snapshot.Agents.Where(agent => agent.Active).ToArray();
        return active.Length == 0
            ? null
            : active[Math.Clamp(snapshot.TurnIndex, 0, int.MaxValue) % active.Length];
    }

    internal static string CurrentTurnModel(ArenaViewSnapshot snapshot, AgentState? current)
    {
        if (!string.IsNullOrWhiteSpace(current?.Model))
        {
            return current.Model;
        }

        return !string.IsNullOrWhiteSpace(snapshot.ProviderModel)
            ? snapshot.ProviderModel
            : "-";
    }

    internal static string DisplayStatusValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-"
            ? "-"
            : value.Trim().ToUpperInvariant();
    }

    internal static string TopRunStateSummary(ArenaViewSnapshot snapshot, AgentState? current, Func<string, string> shortModelName)
    {
        var provider = snapshot.ProviderOnline ? "provider online" : "provider offline";
        if (current is null)
        {
            return $"Ready: no active turn participant; {provider}.";
        }

        var model = shortModelName(CurrentTurnModel(snapshot, current));
        return $"Ready: next {DisplayStatusValue(current.Id)} using {model}; {provider}.";
    }

    internal static string ProviderSetupStatus(ArenaViewSnapshot snapshot)
    {
        if (snapshot.ProviderOnline)
        {
            return "Provider is online. Choose a model, then run 1 TURN.";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ProviderLastError) && snapshot.ProviderLastError != "-")
        {
            return snapshot.ProviderLastError;
        }

        return "Provider is offline. Start LM Studio server, then save and test.";
    }

    internal static string ParticipantSummary(ArenaViewSnapshot snapshot)
    {
        return $"{snapshot.Agents.Count(agent => agent.Active)} agents + operator";
    }

    internal static int TotalCompletionTokens(ArenaViewSnapshot snapshot)
    {
        return snapshot.Messages.Sum(message => Math.Max(message.CompletionTokens, 0));
    }

    internal static int MaxPromptContext(ArenaViewSnapshot snapshot)
    {
        return snapshot.Messages.Select(message => Math.Max(message.PromptTokens, 0)).DefaultIfEmpty(0).Max();
    }

    private Brush ProviderAccent(bool providerOnline)
    {
        return providerOnline
            ? resourceBrush("PrimaryBorderBrush")
            : resourceBrush("DangerTextBrush");
    }

    private static string ProviderLabel(bool providerOnline)
    {
        return providerOnline ? "ONLINE" : "OFFLINE";
    }
}
