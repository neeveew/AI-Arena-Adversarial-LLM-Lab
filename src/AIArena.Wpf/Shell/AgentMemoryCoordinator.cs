using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class AgentMemoryCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly Func<ThemePalette> theme;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Brush> accentForSpeaker;
    private readonly Func<string, string> shortModelName;
    private readonly Func<string, string> displayStatusValue;
    private readonly Func<string, RoutedEventHandler?, bool, TranscriptActionKind, string?, Button> createActionButton;
    private readonly Action refreshTranscript;
    private readonly Func<string, Func<Task>, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setArenaRunStatus;

    public AgentMemoryCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        Func<ThemePalette> theme,
        Func<CoreSessionSummary?> activeSession,
        Func<bool> compactTranscriptMode,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Brush> accentForSpeaker,
        Func<string, string> shortModelName,
        Func<string, string> displayStatusValue,
        Func<string, RoutedEventHandler?, bool, TranscriptActionKind, string?, Button> createActionButton,
        Action refreshTranscript,
        Func<string, Func<Task>, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.theme = theme;
        this.activeSession = activeSession;
        this.compactTranscriptMode = compactTranscriptMode;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.accentForSpeaker = accentForSpeaker;
        this.shortModelName = shortModelName;
        this.displayStatusValue = displayStatusValue;
        this.createActionButton = createActionButton;
        this.refreshTranscript = refreshTranscript;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public Border CreatePanel(ArenaViewSnapshot snapshot)
    {
        var accent = resourceBrush("GammaAccentBrush");
        var panel = new StackPanel();
        var activeAgents = snapshot.Agents.Where(agent => agent.Active).ToArray();
        var noteCount = snapshot.Agents.Sum(agent => agent.PrivateNotes.Count);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock
        {
            Text = "Agent Memory Notes",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"{noteCount} note(s) across {activeAgents.Length} active agent(s). Notes are written into the session snapshot and used by model context windows.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 0);
        header.Children.Add(titleStack);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Refresh", (_, _) => refreshTranscript(), true));
        actions.Children.Add(ActionButton("Clear all", async (_, _) => await ClearAllAgentMemoryNotesAsync(), noteCount > 0, TranscriptActionKind.Danger));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        panel.Children.Add(header);

        var grid = new UniformGrid
        {
            Columns = compactTranscriptMode() ? 1 : 2
        };
        foreach (var agent in snapshot.Agents)
        {
            grid.Children.Add(CreateAgentMemoryCard(agent));
        }

        if (snapshot.Agents.Count == 0)
        {
            grid.Children.Add(CreateMemoryPlaceholder());
        }

        panel.Children.Add(grid);

        return new Border
        {
            Background = blendBrush(resourceBrush("CardBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.45),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    public async Task EditAgentMemoryNotesAsync(AgentState agent)
    {
        var edited = TextEditDialog.Show(
            owner,
            theme(),
            $"Edit {agent.Name} memory",
            string.Join(Environment.NewLine, agent.PrivateNotes),
            "One memory note per line. Empty lines are ignored.");
        if (edited is null)
        {
            return;
        }

        await SaveAgentMemoryNotesAsync(agent.Id, NormalizeMemoryNotes(edited));
    }

    public async Task ClearAllAgentMemoryNotesAsync()
    {
        var confirm = ConfirmDialog.Show(
            owner,
            theme(),
            "Clear Memory Notes",
            "Clear private memory notes for every agent in this session?",
            "Clear",
            tone: ConfirmDialogTone.Danger);
        if (!confirm)
        {
            return;
        }

        await runArenaBusyAsync("Clearing agent memory notes...", async () =>
        {
            var session = activeSession();
            if (session is null)
            {
                return;
            }

            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            if (snapshot is null)
            {
                return;
            }

            foreach (var agent in snapshot.Engine.Agents)
            {
                agent.PrivateNotes.Clear();
            }

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_memory_notes_cleared", new { AllAgents = true });
            await refreshActiveSessionAsync("Cleared agent memory notes.");
        });
    }

    public async Task SaveAgentMemoryNotesAsync(string agentId, IReadOnlyList<string> notes)
    {
        await runArenaBusyAsync($"Saving {agentId} memory notes...", async () =>
        {
            var session = activeSession();
            if (session is null)
            {
                return;
            }

            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
            var agent = snapshot?.Engine.Agents.FirstOrDefault(item => item.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
            if (snapshot is null || agent is null)
            {
                setArenaRunStatus($"Agent {agentId} not found.");
                return;
            }

            agent.PrivateNotes.Clear();
            agent.PrivateNotes.AddRange(notes);

            await saveSnapshotWithFeedbackAsync(snapshot, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_memory_notes_saved", new { Agent = agentId, Count = notes.Count });
            await refreshActiveSessionAsync($"Saved {displayStatusValue(agentId)} memory notes.");
        });
    }

    public static IReadOnlyList<string> NormalizeMemoryNotes(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Select(note => note.Length <= 400 ? note : note[..400])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToArray();
    }

    private Border CreateAgentMemoryCard(AgentState agent)
    {
        var accent = accentForSpeaker(agent.Id);
        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = MatchLockCoordinator.FormatParticipantTitle(agent.Id, agent.Name, uppercaseRole: true),
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{shortModelName(agent.Model)} - {agent.PrivateNotes.Count} note(s)",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Edit", async (_, _) => await EditAgentMemoryNotesAsync(agent), true, TranscriptActionKind.Primary));
        actions.Children.Add(ActionButton("Clear", async (_, _) => await SaveAgentMemoryNotesAsync(agent.Id, []), agent.PrivateNotes.Count > 0, TranscriptActionKind.Danger));
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        stack.Children.Add(header);

        if (agent.PrivateNotes.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No private notes yet.",
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 12,
                FontStyle = FontStyles.Italic
            });
        }
        else
        {
            var list = new StackPanel();
            foreach (var note in agent.PrivateNotes.Take(compactTranscriptMode() ? 4 : 6))
            {
                list.Children.Add(new TextBlock
                {
                    Text = "- " + note,
                    Foreground = resourceBrush("TextBrush"),
                    FontSize = compactTranscriptMode() ? 12 : 13,
                    LineHeight = compactTranscriptMode() ? 17 : 19,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            if (agent.PrivateNotes.Count > (compactTranscriptMode() ? 4 : 6))
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"+ {agent.PrivateNotes.Count - (compactTranscriptMode() ? 4 : 6)} more",
                    Foreground = resourceBrush("MutedTextBrush"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold
                });
            }

            stack.Children.Add(list);
        }

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, compactTranscriptMode() ? 0 : 8, 8),
            MinHeight = compactTranscriptMode() ? 104 : 132,
            Child = stack
        };
    }

    private Border CreateMemoryPlaceholder()
    {
        return new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = "No agents are available in this session.",
                Foreground = resourceBrush("MutedTextBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private Button ActionButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        return createActionButton(text, handler, enabled, kind, iconGlyph);
    }
}
