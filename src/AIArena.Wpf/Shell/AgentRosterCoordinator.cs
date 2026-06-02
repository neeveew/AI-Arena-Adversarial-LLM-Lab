using System.Windows.Controls;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class AgentRosterCoordinator
{
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly ComboBox agentCountPresetPicker;
    private readonly ComboBox agentCountPicker;
    private readonly ComboBox activeParticipantsPicker;
    private readonly Button applyAgentCountButton;
    private readonly TextBlock agentRosterStatusText;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<string?, Task> loadSessionsAsync;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setArenaRunStatus;

    public AgentRosterCoordinator(
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        ComboBox agentCountPresetPicker,
        ComboBox agentCountPicker,
        ComboBox activeParticipantsPicker,
        Button applyAgentCountButton,
        TextBlock agentRosterStatusText,
        Func<bool> isRenderingSnapshot,
        Func<bool> isArenaBusy,
        Func<CoreSessionSummary?> activeSession,
        Func<string?, Task> loadSessionsAsync,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setArenaRunStatus)
    {
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.agentCountPresetPicker = agentCountPresetPicker;
        this.agentCountPicker = agentCountPicker;
        this.activeParticipantsPicker = activeParticipantsPicker;
        this.applyAgentCountButton = applyAgentCountButton;
        this.agentRosterStatusText = agentRosterStatusText;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.isArenaBusy = isArenaBusy;
        this.activeSession = activeSession;
        this.loadSessionsAsync = loadSessionsAsync;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public void ApplySnapshot(int activeCount)
    {
        ShellUiHelpers.SelectComboTag(
            activeParticipantsPicker,
            Math.Clamp(activeCount, 1, AgentRosterService.MaxParticipants).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectAgentCountControls(activeCount);
    }

    public void OnPresetChanged()
    {
        if (isRenderingSnapshot())
        {
            return;
        }

        var preset = ShellUiHelpers.SelectedComboTag(agentCountPresetPicker, "4");
        if (!preset.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            ShellUiHelpers.SelectComboTag(agentCountPicker, preset);
        }
    }

    public async Task ApplyAgentCountAsync()
    {
        if (isArenaBusy())
        {
            return;
        }

        var session = activeSession();
        if (session is null)
        {
            await sessionStore.EnsureDefaultSessionAsync();
            await loadSessionsAsync("default");
            session = activeSession();
        }

        if (session is null)
        {
            setArenaRunStatus("No session is available for agent roster changes.");
            return;
        }

        var count = SelectedAgentCount();
        await runArenaBusyAsync($"Resizing cast to {count} agents...", applyAgentCountButton, async () =>
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id) ?? SessionStore.CreateDefaultSnapshot();
            AgentRosterService.EnsureParticipantCount(snapshot, count);
            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_roster_resized", new
            {
                Count = count,
                Agents = snapshot.Engine.Agents.Where(agent => agent.Active).Select(agent => agent.Id).ToArray()
            });
            await refreshActiveSessionAsync($"Agent roster resized: {count} active agents.");
        }, true);
    }

    public int ParseActiveParticipants()
    {
        var selected = (activeParticipantsPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, 1, AgentRosterService.MaxParticipants)
            : 3;
    }

    public void UpdateBusyState(bool busy)
    {
        activeParticipantsPicker.IsEnabled = !busy;
        agentCountPresetPicker.IsEnabled = !busy;
        agentCountPicker.IsEnabled = !busy;
        applyAgentCountButton.IsEnabled = !busy;
    }

    private void SelectAgentCountControls(int activeCount)
    {
        var count = Math.Clamp(activeCount, AgentRosterService.MinParticipants, AgentRosterService.MaxParticipants);
        ShellUiHelpers.SelectComboTag(agentCountPicker, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var preset = count switch
        {
            2 => "2",
            4 => "4",
            6 => "6",
            8 => "8",
            _ => "custom"
        };
        ShellUiHelpers.SelectComboTag(agentCountPresetPicker, preset);
        agentRosterStatusText.Text = count switch
        {
            1 => "Solo",
            2 => "Duel",
            4 => "Classic",
            6 => "Council",
            8 => "Swarm",
            _ => "Custom"
        };
        agentRosterStatusText.ToolTip = $"{count} active AI agents in this session";
    }

    private int SelectedAgentCount()
    {
        var selected = (agentCountPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selected, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, AgentRosterService.MinParticipants, AgentRosterService.MaxParticipants)
            : 4;
    }
}
