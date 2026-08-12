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
        sessionOverviewTokensText.ToolTip =
            $"{formatCompactNumber(TotalCompletionTokens(snapshot))} generated · {formatCompactNumber(TotalSessionTokens(snapshot))} total including prompts";
        sessionOverviewProviderText.Text = ProviderLabel(snapshot.ProviderOnline);
        sessionOverviewProviderText.Foreground = ProviderAccent(snapshot.ProviderOnline);
        sessionOverviewContextText.Text = ContextPressureLabel(snapshot, formatCompactNumber);
        var pressure = ContextPressure(snapshot);
        sessionOverviewContextText.Foreground = pressure >= ContextPressureWarningThreshold
            ? resourceBrush("Arena.Brush.Warning")
            : resourceBrush("TextBrush");
        sessionOverviewContextText.ToolTip = pressure is null
            ? "Context pressure is unknown until a causal Arena history receipt or configured model context is available."
            : ContextPressureTooltip(snapshot, pressure.Value, formatCompactNumber);
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
        if (!string.IsNullOrWhiteSpace(current?.Model) && current.Model != "-")
        {
            return current.Model;
        }

        // The shared provider model is also used by Agent Workspace and provider
        // diagnostics, so its presence is not proof that an Arena role can use it.
        // Only treat it as the current role's model while fallback is enabled.
        // With no current participant, retaining the shared model in shell status
        // remains useful and cannot enable an Arena action because the cast gate
        // is evaluated first.
        return (current is null || snapshot.DefaultForUnassignedAgentsEnabled)
            && !string.IsNullOrWhiteSpace(snapshot.ProviderModel)
            && snapshot.ProviderModel != "-"
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
        if (snapshot.MatchEnded)
        {
            return "Match ended. Reset or fork the session to continue.";
        }

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
        var latestReceipt = LatestHistoryReceipt(snapshot);
        return latestReceipt is null
            ? snapshot.Messages.Select(message => Math.Max(message.PromptTokens, 0)).DefaultIfEmpty(0).Max()
            : Math.Max(0, latestReceipt.EstimatedPromptTokens);
    }

    /// <summary>
    /// Every token the session has spent, prompt and completion together. The
    /// per-turn counts are visible but never added up, so a long run gives no
    /// sense of its own cost.
    /// </summary>
    internal static int TotalSessionTokens(ArenaViewSnapshot snapshot)
    {
        return snapshot.Messages.Sum(message => Math.Max(message.PromptTokens, 0) + Math.Max(message.CompletionTokens, 0));
    }

    /// <summary>
    /// How close the largest prompt has come to the configured context window,
    /// as a fraction. Returns null when the window is unknown, so callers can
    /// tell "no pressure" apart from "cannot say".
    /// </summary>
    internal static double? ContextPressure(ArenaViewSnapshot snapshot)
    {
        var receipt = LatestHistoryReceipt(snapshot);
        var limit = receipt?.ConfiguredContextWindow > 0
            ? receipt.ConfiguredContextWindow
            : snapshot.ProviderConfiguredContextWindow > 0
                ? snapshot.ProviderConfiguredContextWindow
                : snapshot.ProviderContextLength;
        if (limit <= 0)
        {
            return null;
        }

        var used = MaxPromptContext(snapshot);
        return used <= 0 ? 0 : (double)used / limit;
    }

    /// <summary>Short label for the context cell, including pressure when known.</summary>
    internal static string ContextPressureLabel(ArenaViewSnapshot snapshot, Func<int, string> formatCompactNumber)
    {
        var context = MaxPromptContext(snapshot);
        if (context <= 0)
        {
            return "-";
        }

        var pressure = ContextPressure(snapshot);
        return pressure is null
            ? $"{formatCompactNumber(context)} (unknown)"
            : $"{formatCompactNumber(context)} ({pressure.Value * 100:0}%)";
    }

    private static ArenaHistoryBudgetReceiptView? LatestHistoryReceipt(ArenaViewSnapshot snapshot) =>
        snapshot.Messages
            .Where(message => message.HistoryBudgetReceipt is not null)
            .OrderByDescending(message => message.Turn)
            .ThenByDescending(message => message.CreatedAt)
            .Select(message => message.HistoryBudgetReceipt)
            .FirstOrDefault();

    private static string ContextPressureTooltip(
        ArenaViewSnapshot snapshot,
        double pressure,
        Func<int, string> formatCompactNumber)
    {
        var receipt = LatestHistoryReceipt(snapshot);
        if (receipt is not null)
        {
            var omitted = receipt.OmittedEntryCount > 0
                ? $" {receipt.OmittedEntryCount} older whole entries were omitted."
                : "";
            return $"Estimated Arena prompt is {pressure * 100:0}% of the {formatCompactNumber(receipt.ConfiguredContextWindow)} configured context; output reserve {formatCompactNumber(receipt.OutputTokenReserve)} tokens.{omitted}";
        }

        var limit = snapshot.ProviderConfiguredContextWindow > 0
            ? snapshot.ProviderConfiguredContextWindow
            : snapshot.ProviderContextLength;
        return $"Largest provider-reported prompt is {pressure * 100:0}% of the {formatCompactNumber(limit)} configured context. No Arena history-budget receipt is available.";
    }

    /// <summary>Pressure at or above this fraction is worth flagging.</summary>
    internal const double ContextPressureWarningThreshold = 0.85;

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
