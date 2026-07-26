using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AIArena.Core.Persistence;
using AIArena.Core.Services;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

internal sealed class MatchLockCoordinator
{
    private readonly Window owner;
    private readonly SessionStore sessionStore;
    private readonly EventLogStore eventLogStore;
    private readonly MatchGenerationService matchGeneration;
    private readonly Func<CoreSessionSummary?> activeSession;
    private readonly Func<ThemePalette> theme;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<bool> isRenderingSnapshot;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync;
    private readonly Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync;
    private readonly Func<string, Task> refreshActiveSessionAsync;
    private readonly Action<string> setLoadStatus;
    private readonly Action<string> setArenaRunStatus;

    private readonly List<CheckBox> lockControls = [];
    private readonly List<ComboBox> voiceControls = [];
    private readonly List<ComboBox> pressureControls = [];
    private readonly List<Button> colorControls = [];
    private readonly List<Button> actionControls = [];
    private Style? lockToggleStyle;

    private static readonly string[] RoleDetailLabels =
    [
        "Absurd function:",
        "Arena function:",
        "Expertise leak:",
        "Failure bias:",
        "Role pressure:",
        "Persona mixer:",
        "Expression constraint:",
        "Reasoning distortion:"
    ];

    public MatchLockCoordinator(
        Window owner,
        SessionStore sessionStore,
        EventLogStore eventLogStore,
        MatchGenerationService matchGeneration,
        Func<CoreSessionSummary?> activeSession,
        Func<ThemePalette> theme,
        Func<bool> isArenaBusy,
        Func<bool> isRenderingSnapshot,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, Button?, Func<Task>, bool, Task> runArenaBusyAsync,
        Func<AIArena.Core.Models.ArenaSnapshot, string, Task> saveSnapshotWithFeedbackAsync,
        Func<string, Task> refreshActiveSessionAsync,
        Action<string> setLoadStatus,
        Action<string> setArenaRunStatus)
    {
        this.owner = owner;
        this.sessionStore = sessionStore;
        this.eventLogStore = eventLogStore;
        this.matchGeneration = matchGeneration;
        this.activeSession = activeSession;
        this.theme = theme;
        this.isArenaBusy = isArenaBusy;
        this.isRenderingSnapshot = isRenderingSnapshot;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.runArenaBusyAsync = runArenaBusyAsync;
        this.saveSnapshotWithFeedbackAsync = saveSnapshotWithFeedbackAsync;
        this.refreshActiveSessionAsync = refreshActiveSessionAsync;
        this.setLoadStatus = setLoadStatus;
        this.setArenaRunStatus = setArenaRunStatus;
    }

    public void ClearControls()
    {
        lockControls.Clear();
        voiceControls.Clear();
        pressureControls.Clear();
        colorControls.Clear();
        actionControls.Clear();
    }

    public void UpdateBusyState(bool busy)
    {
        foreach (var checkBox in lockControls)
        {
            checkBox.IsEnabled = !busy;
        }

        foreach (var comboBox in voiceControls)
        {
            comboBox.IsEnabled = !busy;
        }

        foreach (var comboBox in pressureControls)
        {
            comboBox.IsEnabled = !busy;
        }

        foreach (var button in colorControls)
        {
            button.IsEnabled = !busy;
        }

        foreach (var button in actionControls)
        {
            button.IsEnabled = !busy;
        }
    }

    public Border CreateLockCard(
        string lockKey,
        string title,
        string body,
        Brush background,
        Brush accent,
        bool locked,
        string? voiceStyle = null,
        string? pressureProfile = null,
        string? accentColor = null)
    {
        var lockAccent = resourceBrush("BetaAccentBrush");
        var isCastCard = IsParticipant(lockKey) || NormalizeLockKey(lockKey) == "narrator";
        var isAgentCard = IsParticipant(lockKey);
        var cardAccent = locked ? lockAccent : accent;
        var lockBox = new CheckBox
        {
            IsChecked = locked,
            IsEnabled = !isArenaBusy(),
            Tag = lockKey,
            Style = CreateLockToggleStyle(),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = locked ? "Locked. Click to unlock." : "Unlocked. Click to lock."
        };
        lockBox.Checked += MatchLockChanged;
        lockBox.Unchecked += MatchLockChanged;
        lockControls.Add(lockBox);

        var editButton = CreateLockCardActionButton(
            "\uE70F",
            "Edit",
            lockKey,
            blendBrush(resourceBrush("ControlBorderBrush"), cardAccent, 0.5),
            resourceBrush("TextBrush"),
            "Edit this text and lock it");
        editButton.Click += EditLockCardButton_Click;

        var header = new DockPanel { LastChildFill = true };
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 0, 0, 0)
        };
        if (isCastCard)
        {
            actions.Children.Add(CreateAccentColorButton(lockKey, accent, accentColor ?? ""));
        }
        if (isAgentCard)
        {
            actions.Children.Add(CreateAgentPressurePicker(lockKey, pressureProfile ?? ""));
        }
        if (isCastCard)
        {
            actions.Children.Add(CreateVoiceStylePicker(lockKey, voiceStyle ?? ""));
        }
        if (isAgentCard && HasRoleDetails(body))
        {
            var detailsButton = CreateLockCardActionButton(
                "\uE897",
                "Details",
                new RoleDetailPayload(title, body),
                cardAccent,
                cardAccent,
                "Inspect generated role constraints");
            detailsButton.Click += RoleDetailsButton_Click;
            actions.Children.Add(detailsButton);
        }
        actions.Children.Add(editButton);
        actions.Children.Add(lockBox);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titlePanel.Children.Add(CreateLockMetaChip(DisplayLockKey(lockKey), accent));
        var visibleVoiceStyle = RoleStyleCatalog.VoiceStyleChipText(voiceStyle);
        if (isCastCard && !string.IsNullOrWhiteSpace(visibleVoiceStyle))
        {
            titlePanel.Children.Add(CreateLockMetaChip(visibleVoiceStyle, accent));
        }
        var visiblePressure = RoleStyleCatalog.AgentPressureChipText(pressureProfile);
        if (isAgentCard && !string.IsNullOrWhiteSpace(visiblePressure))
        {
            titlePanel.Children.Add(CreateLockMetaChip(visiblePressure, accent));
        }
        if (locked)
        {
            titlePanel.Children.Add(CreateLockMetaChip("Locked", lockAccent));
        }
        header.Children.Add(titlePanel);

        var displayBody = isCastCard ? CompactCastPreviewBody(body) : body;
        var text = new TextBlock
        {
            Text = displayBody,
            Foreground = resourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            MaxHeight = isCastCard ? 58 : double.PositiveInfinity,
            Margin = new Thickness(0, 7, 0, 0),
            ToolTip = body
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(text);

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Background = cardAccent,
            CornerRadius = ArenaTokens.SmallRadius,
            Margin = new Thickness(0, 1, 10, 1)
        });
        Grid.SetColumn(stack, 1);
        layout.Children.Add(stack);

        return new Border
        {
            Background = blendBrush(background, accent, locked ? 0.08 : 0.04),
            BorderBrush = locked ? lockAccent : blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.25),
            BorderThickness = locked ? new Thickness(2) : new Thickness(1),
            CornerRadius = ArenaTokens.MediumRadius,
            Padding = new Thickness(11),
            Margin = new Thickness(0, 0, isCastCard ? 0 : 10, 10),
            Child = layout
        };
    }

    private Button CreateLockCardActionButton(
        string glyph,
        string label,
        object tag,
        Brush border,
        Brush foreground,
        string tooltip)
    {
        var button = new Button
        {
            Content = CreatePopupActionContent(glyph, label),
            Tag = tag,
            MinHeight = 28,
            MinWidth = label.Length > 4 ? 82 : 64,
            Padding = new Thickness(8, 4, 9, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Background = resourceBrush("InputBrush"),
            BorderBrush = border,
            Foreground = foreground,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        actionControls.Add(button);
        return button;
    }

    public static string FormatCastPreviewTitle(string id, string name)
    {
        return FormatParticipantTitle(id, name, uppercaseRole: false);
    }

    public static string FormatParticipantTitle(string id, string name, bool uppercaseRole)
    {
        var role = DisplayLockKey(id);
        var cleaned = string.IsNullOrWhiteSpace(name) ? role : name.Trim();
        var roleLabel = uppercaseRole ? role.ToUpperInvariant() : role;
        var duplicatePrefix = $"{role}:";
        if (cleaned.StartsWith(duplicatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[duplicatePrefix.Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) || cleaned.Equals(role, StringComparison.OrdinalIgnoreCase)
            ? roleLabel
            : $"{roleLabel}: {cleaned}";
    }

    public static string NormalizeLockKey(string key)
    {
        var cleaned = string.IsNullOrWhiteSpace(key) ? "" : key.Trim().ToLowerInvariant();
        return IsParticipant(cleaned) || cleaned is "narrator" or "topic" or "global" or "scenario"
            ? cleaned
            : "scenario";
    }

    public static string DisplayLockKey(string key)
    {
        return NormalizeLockKey(key) switch
        {
            "topic" => "Topic",
            "global" => "Global",
            var agentId when IsParticipant(agentId) => AgentRosterService.DisplayName(agentId),
            "narrator" => "Narrator",
            _ => "Scenario"
        };
    }

    private ComboBox CreateVoiceStylePicker(string lockKey, string voiceStyle)
    {
        var picker = new ComboBox
        {
            Tag = lockKey,
            Width = 132,
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            ToolTip = "Communication style for this model"
        };

        foreach (var option in RoleStyleCatalog.VoiceStyleOptions)
        {
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Tag });
        }

        ShellUiHelpers.SelectComboTag(picker, RoleStyleCatalog.NormalizeVoiceStyleTag(voiceStyle));
        picker.SelectionChanged += VoiceStylePicker_SelectionChanged;
        voiceControls.Add(picker);
        return picker;
    }

    private Button CreateAccentColorButton(string lockKey, Brush accent, string accentColor)
    {
        var button = new Button
        {
            Tag = new AccentColorPayload(lockKey, AgentAccentService.NormalizeColor(accentColor)),
            Width = 32,
            MinWidth = 32,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            Background = resourceBrush("InputBrush"),
            BorderBrush = accent,
            Foreground = accent,
            ToolTip = "Customize this role color"
        };
        button.Content = new Border
        {
            Width = 15,
            Height = 15,
            Background = accent,
            BorderBrush = blendBrush(resourceBrush("TextBrush"), accent, 0.72),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.SmallRadius,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += AccentColorButton_Click;
        colorControls.Add(button);
        return button;
    }

    private ComboBox CreateAgentPressurePicker(string lockKey, string pressureProfile)
    {
        var picker = new ComboBox
        {
            Tag = lockKey,
            Width = 124,
            MinHeight = 28,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 11,
            ToolTip = "Debate pressure for this agent"
        };

        foreach (var option in RoleStyleCatalog.AgentPressureOptions)
        {
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Tag });
        }

        ShellUiHelpers.SelectComboTag(picker, RoleStyleCatalog.NormalizeAgentPressureTag(pressureProfile));
        picker.SelectionChanged += AgentPressurePicker_SelectionChanged;
        pressureControls.Add(picker);
        return picker;
    }

    private void AccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccentColorPayload payload } button)
        {
            return;
        }

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        var root = new Border
        {
            Background = resourceBrush("PanelBrush"),
            BorderBrush = button.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.MediumRadius,
            Padding = new Thickness(12),
            Width = 252,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.35,
                BlurRadius = 16,
                ShadowDepth = 3
            }
        };
        var stack = new StackPanel();
        root.Child = stack;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = $"{DisplayLockKey(payload.Key)} color",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Pick a preset or enter #RRGGBB.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(heading);
        var preview = new Border
        {
            Width = 24,
            Height = 24,
            Background = button.BorderBrush,
            BorderBrush = blendBrush(resourceBrush("TextBrush"), button.BorderBrush, 0.72),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.SmallRadius,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(preview, 1);
        header.Children.Add(preview);
        stack.Children.Add(header);

        var swatches = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        foreach (var option in AgentAccentService.PresetOptions)
        {
            swatches.Children.Add(CreateColorPresetButton(payload.Key, option, popup));
        }
        stack.Children.Add(swatches);

        var hexRow = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        hexRow.Children.Add(new TextBlock
        {
            Text = "Hex",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var hexText = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(payload.Color) ? "" : payload.Color,
            MinHeight = 28,
            Padding = new Thickness(7, 3, 7, 3),
            Background = resourceBrush("InputBrush"),
            Foreground = resourceBrush("TextBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            FontSize = 11,
            ToolTip = "Use #RRGGBB"
        };
        DockPanel.SetDock(hexText, Dock.Right);
        hexRow.Children.Add(hexText);
        stack.Children.Add(hexRow);

        var actions = new DockPanel { LastChildFill = false };
        var reset = new Button
        {
            Content = CreatePopupActionContent("\uE72C", "Reset"),
            MinHeight = 28,
            MinWidth = 72,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("DisabledBorderBrush"),
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        reset.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await UpdateAccentColorAsync(payload.Key, "");
        };
        actions.Children.Add(reset);

        var apply = new Button
        {
            Content = CreatePopupActionContent("\uE74E", "Apply"),
            MinHeight = 28,
            MinWidth = 78,
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = resourceBrush("InputBrush"),
            BorderBrush = button.BorderBrush,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        apply.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await UpdateAccentColorAsync(payload.Key, hexText.Text);
        };
        DockPanel.SetDock(apply, Dock.Right);
        actions.Children.Add(apply);
        stack.Children.Add(actions);

        popup.Child = root;
        popup.IsOpen = true;
        hexText.Focus();
        hexText.SelectAll();
    }

    private Button CreateColorPresetButton(string key, AgentAccentOption option, Popup popup)
    {
        var brush = AgentAccentService.ResolveBrush(key, option.Hex, resourceBrush);
        var button = new Button
        {
            Tag = option.Hex,
            Width = 28,
            MinWidth = 28,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 6),
            Background = resourceBrush("InputBrush"),
            BorderBrush = brush,
            ToolTip = option.Label
        };
        button.Content = new Border
        {
            Width = 16,
            Height = 16,
            Background = brush,
            CornerRadius = ArenaTokens.SmallRadius
        };
        button.Click += async (_, _) =>
        {
            popup.IsOpen = false;
            await UpdateAccentColorAsync(key, option.Hex);
        };
        return button;
    }

    private StackPanel CreatePopupActionContent(string glyph, string label)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = glyph,
                    FontFamily = ArenaTokens.IconFontFamily,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private async void VoiceStylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = activeSession();
        if (isRenderingSnapshot() || session is null || sender is not ComboBox picker || picker.Tag is not string key)
        {
            return;
        }

        var voiceStyle = ShellUiHelpers.SelectedComboTag(picker, "default");
        await runArenaBusyAsync($"Updating {DisplayLockKey(key)} voice...", null, async () =>
        {
            var latest = await sessionStore.LoadSnapshotAsync(session.Id);
            if (latest is null)
            {
                setLoadStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            if (!ApplyVoiceStyle(latest, key, voiceStyle))
            {
                SetBothStatuses($"Could not update {DisplayLockKey(key)} voice.");
                return;
            }

            await saveSnapshotWithFeedbackAsync(latest, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_match_voice_style_changed", new
            {
                key = NormalizeLockKey(key),
                voice_style = voiceStyle
            });
            await refreshActiveSessionAsync($"Updated {DisplayLockKey(key)} voice: {RoleStyleCatalog.VoiceStyleLabel(voiceStyle)}.");
        }, false);
    }

    private async Task UpdateAccentColorAsync(string key, string value)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null)
        {
            return;
        }

        var normalized = AgentAccentService.NormalizeColor(value);
        if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(normalized))
        {
            SetBothStatuses("Color must be a valid #RRGGBB hex value.");
            return;
        }

        await runArenaBusyAsync($"Updating {DisplayLockKey(key)} color...", null, async () =>
        {
            var latest = await sessionStore.LoadSnapshotAsync(session.Id);
            if (latest is null)
            {
                setLoadStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            if (!ApplyAccentColor(latest, key, normalized))
            {
                SetBothStatuses($"Could not update {DisplayLockKey(key)} color.");
                return;
            }

            await saveSnapshotWithFeedbackAsync(latest, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_accent_color_changed", new
            {
                key = NormalizeLockKey(key),
                accent_color = normalized
            });
            var status = string.IsNullOrWhiteSpace(normalized)
                ? $"Reset {DisplayLockKey(key)} color."
                : $"Updated {DisplayLockKey(key)} color: {normalized}.";
            await refreshActiveSessionAsync(status);
        }, false);
    }

    private async void AgentPressurePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var session = activeSession();
        if (isRenderingSnapshot() || session is null || sender is not ComboBox picker || picker.Tag is not string key)
        {
            return;
        }

        var pressure = ShellUiHelpers.SelectedComboTag(picker, "default");
        await runArenaBusyAsync($"Updating {DisplayLockKey(key)} pressure...", null, async () =>
        {
            var latest = await sessionStore.LoadSnapshotAsync(session.Id);
            if (latest is null)
            {
                setLoadStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            if (!ApplyAgentPressure(latest, key, pressure))
            {
                SetBothStatuses($"Could not update {DisplayLockKey(key)} pressure.");
                return;
            }

            await saveSnapshotWithFeedbackAsync(latest, session.Id);
            await eventLogStore.AppendAsync(session.Id, "native_agent_pressure_changed", new
            {
                key = NormalizeLockKey(key),
                pressure_profile = pressure
            });
            await refreshActiveSessionAsync($"Updated {DisplayLockKey(key)} pressure: {RoleStyleCatalog.AgentPressureLabel(pressure)}.");
        }, false);
    }

    private async void MatchLockChanged(object sender, RoutedEventArgs e)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null || sender is not CheckBox checkBox || checkBox.Tag is not string key)
        {
            return;
        }

        var locked = checkBox.IsChecked == true;
        await runArenaBusyAsync($"Updating {key} lock...", null, async () =>
        {
            await matchGeneration.ToggleLockAsync(session.Id, key, locked);
            await refreshActiveSessionAsync($"{key} lock {(locked ? "enabled" : "disabled")}.");
        }, false);
    }

    private async void EditLockCardButton_Click(object sender, RoutedEventArgs e)
    {
        var session = activeSession();
        if (isArenaBusy() || session is null || sender is not Button button || button.Tag is not string key)
        {
            return;
        }

        var snapshot = await sessionStore.LoadSnapshotAsync(session.Id);
        if (snapshot is null)
        {
            SetBothStatuses($"No snapshot found for session {session.Id}.");
            return;
        }

        var current = CurrentMatchText(snapshot, key);
        var edited = TextEditDialog.Show(owner, theme(), $"Edit {DisplayLockKey(key)}", current);
        if (edited is null)
        {
            setLoadStatus("Edit cancelled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(edited))
        {
            SetBothStatuses("Edit text is empty.");
            return;
        }

        await runArenaBusyAsync($"Updating {key}...", null, async () =>
        {
            var latest = await sessionStore.LoadSnapshotAsync(session.Id);
            if (latest is null)
            {
                setLoadStatus($"No snapshot found for session {session.Id}.");
                return;
            }

            if (!ApplyMatchTextEdit(latest, key, edited))
            {
                SetBothStatuses($"Could not edit {key}.");
                return;
            }

            latest.MatchLocks[NormalizeLockKey(key)] = true;
            await saveSnapshotWithFeedbackAsync(latest, session.Id);
            var normalizedKey = NormalizeLockKey(key);
            await eventLogStore.AppendAsync(session.Id, "native_match_text_edited", new
            {
                key = normalizedKey,
                locked = true
            });
            await refreshActiveSessionAsync($"Updated and locked {DisplayLockKey(key)}.");
        }, false);
    }

    private void RoleDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RoleDetailPayload payload })
        {
            return;
        }

        var window = new Window
        {
            Owner = owner,
            Title = $"{payload.Title} details",
            Width = 660,
            Height = 460,
            MinWidth = 540,
            MinHeight = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            AllowsTransparency = true,
            Foreground = resourceBrush("TextBrush")
        };
        DialogChrome.ImportOwnerResources(owner, window);

        var shell = new Border
        {
            Background = resourceBrush("PanelBrush"),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), resourceBrush("PrimaryBorderBrush"), 0.5),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.LargeRadius,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.45,
                BlurRadius = 18,
                ShadowDepth = 4
            }
        };

        var root = new Grid();
        shell.Child = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = resourceBrush("InputBrush"),
            CornerRadius = new CornerRadius(ArenaTokens.LargeRadiusValue - 1, ArenaTokens.LargeRadiusValue - 1, 0, 0),
            Padding = new Thickness(18, 15, 18, 15)
        };
        header.MouseLeftButtonDown += (_, args) => DialogChrome.DragMoveIfPossible(window, args);
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Child = headerGrid;

        var heading = new StackPanel();
        var title = new TextBlock
        {
            Text = payload.Title,
            Foreground = resourceBrush("TextBrush"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var subtitle = new TextBlock
        {
            Text = "Persona details and generated behavior hooks",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        heading.Children.Add(title);
        heading.Children.Add(subtitle);
        headerGrid.Children.Add(heading);

        var headerClose = new Button { ToolTip = "Close details" };
        DialogChrome.ApplyCloseButtonStyle(
            headerClose,
            resourceBrush("InputBrush"),
            resourceBrush("DisabledBorderBrush"),
            resourceBrush("MutedTextBrush"));
        headerClose.Click += (_, _) => window.Close();
        Grid.SetColumn(headerClose, 1);
        headerGrid.Children.Add(headerClose);
        root.Children.Add(header);

        var detailsPanel = new Border
        {
            Background = resourceBrush("InputBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.MediumRadius,
            Padding = new Thickness(1),
            Margin = new Thickness(18)
        };

        var details = new TextBox
        {
            Text = RoleDetailsText(payload.Persona),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = resourceBrush("PanelBrush"),
            Foreground = resourceBrush("TextBrush"),
            BorderBrush = resourceBrush("InputBrush"),
            FontSize = 13,
            Padding = new Thickness(12, 10, 12, 10)
        };
        detailsPanel.Child = details;
        Grid.SetRow(detailsPanel, 1);
        root.Children.Add(detailsPanel);

        var footer = new Grid
        {
            Margin = new Thickness(18, 0, 18, 18)
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Read-only preview. Use Edit on the lock card to change this role.",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(hint);

        var close = new Button
        {
            Content = CreatePopupActionContent("\uE711", "Close"),
            MinWidth = 110,
            Margin = new Thickness(16, 0, 0, 0)
        };
        DialogChrome.ApplyButtonStyle(
            close,
            resourceBrush("InputBrush"),
            resourceBrush("DisabledBorderBrush"),
            resourceBrush("TextBrush"));
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        window.Content = shell;
        DialogChrome.ApplyImplicitControlStyles(window);
        window.Show();
        window.Activate();
    }

    private static string CurrentMatchText(AIArena.Core.Models.ArenaSnapshot snapshot, string key)
    {
        return NormalizeLockKey(key) switch
        {
            "topic" => snapshot.Engine.Steering.Topic,
            "global" => snapshot.Engine.Steering.Global,
            var agentId when IsParticipant(agentId) || agentId == "narrator" =>
                snapshot.Engine.Agents.FirstOrDefault(agent => agent.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase))?.Persona
                ?? (agentId == "narrator" ? snapshot.Engine.Narrator.Persona : ""),
            _ => ""
        };
    }

    private bool ApplyMatchTextEdit(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeLockKey(key);
        switch (normalizedKey)
        {
            case "topic":
                snapshot.Engine.Steering.Topic = value;
                return true;
            case "global":
                snapshot.Engine.Steering.Global = value;
                return true;
            case "narrator":
                snapshot.Engine.Narrator.Persona = value;
                return true;
            case var agentId when IsParticipant(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.Persona = value;
                return true;
            default:
                return false;
        }
    }

    private bool ApplyVoiceStyle(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeLockKey(key);
        var normalizedVoice = RoleStyleCatalog.NormalizeVoiceStyleTag(value);
        switch (normalizedKey)
        {
            case "narrator":
                snapshot.Engine.Narrator.VoiceStyle = normalizedVoice == "default" ? "" : normalizedVoice;
                return true;
            case var agentId when IsParticipant(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.VoiceStyle = normalizedVoice == "default" ? "" : normalizedVoice;
                return true;
            default:
                return false;
        }
    }

    private bool ApplyAgentPressure(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeLockKey(key);
        var normalizedPressure = RoleStyleCatalog.NormalizeAgentPressureTag(value);
        switch (normalizedKey)
        {
            case var agentId when IsParticipant(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.PressureProfile = normalizedPressure == "default" ? "" : normalizedPressure;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyAccentColor(AIArena.Core.Models.ArenaSnapshot snapshot, string key, string value)
    {
        var normalizedKey = NormalizeLockKey(key);
        switch (normalizedKey)
        {
            case "narrator":
                snapshot.Engine.Narrator.AccentColor = value;
                return true;
            case var agentId when IsParticipant(agentId):
                var agent = snapshot.Engine.Agents.FirstOrDefault(item => item.Id.Equals(normalizedKey, StringComparison.OrdinalIgnoreCase));
                if (agent is null)
                {
                    return false;
                }

                agent.AccentColor = value;
                return true;
            default:
                return false;
        }
    }

    private Border CreateLockMetaChip(string text, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.SmallRadius,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private Style CreateLockToggleStyle()
    {
        if (lockToggleStyle is not null)
        {
            return lockToggleStyle;
        }

        var style = new Style(typeof(CheckBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, resourceBrush("MutedTextBrush")));
        style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 44d));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new ControlTemplate(typeof(CheckBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        var track = new FrameworkElementFactory(typeof(Border));
        track.Name = "LockTrack";
        track.SetValue(FrameworkElement.WidthProperty, 42d);
        track.SetValue(FrameworkElement.HeightProperty, 22d);
        track.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        track.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        track.SetValue(Border.BackgroundProperty, resourceBrush("InputBrush"));
        track.SetValue(Border.BorderBrushProperty, resourceBrush("ControlBorderBrush"));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));

        var thumb = new FrameworkElementFactory(typeof(Border));
        thumb.Name = "LockThumb";
        thumb.SetValue(FrameworkElement.WidthProperty, 16d);
        thumb.SetValue(FrameworkElement.HeightProperty, 16d);
        thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        thumb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 0, 0, 0));
        thumb.SetValue(Border.BackgroundProperty, resourceBrush("MutedTextBrush"));
        thumb.SetValue(Border.CornerRadiusProperty, ArenaTokens.MediumRadius);

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.Name = "LockGlyph";
        glyph.SetValue(TextBlock.TextProperty, "\uE785");
        glyph.SetValue(TextBlock.FontFamilyProperty, ArenaTokens.IconFontFamily);
        glyph.SetValue(TextBlock.FontSizeProperty, 9d);
        glyph.SetValue(TextBlock.ForegroundProperty, resourceBrush("DisabledBorderBrush"));
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        thumb.AppendChild(glyph);
        track.AppendChild(thumb);
        root.AppendChild(track);

        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, resourceBrush("TextBrush")));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, blendBrush(resourceBrush("InputBrush"), resourceBrush("BetaAccentBrush"), 0.16), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, resourceBrush("BetaAccentBrush"), "LockTrack"));
        checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, resourceBrush("BetaAccentBrush"), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0), "LockThumb"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.TextProperty, "\uE72E", "LockGlyph"));
        checkedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, resourceBrush("InputBrush"), "LockGlyph"));
        template.Triggers.Add(checkedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, resourceBrush("HoverBorderBrush"), "LockTrack"));
        template.Triggers.Add(hoverTrigger);

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.72));
        template.Triggers.Add(disabledTrigger);

        template.VisualTree = root;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        lockToggleStyle = style;
        return style;
    }

    private static bool HasRoleDetails(string persona)
    {
        return RoleDetailLabels.Any(label => persona.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    private static string CompactCastPreviewBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        var firstDetailIndex = RoleDetailLabels
            .Select(label => body.IndexOf(label, StringComparison.OrdinalIgnoreCase))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();
        var preview = firstDetailIndex > 0 ? body[..firstDetailIndex] : body;
        preview = preview.Trim().Replace("\r", " ").Replace("\n", " ");
        while (preview.Contains("  ", StringComparison.Ordinal))
        {
            preview = preview.Replace("  ", " ", StringComparison.Ordinal);
        }

        return preview.Length <= 300
            ? preview
            : $"{preview[..297].TrimEnd()}...";
    }

    private static string RoleDetailsText(string persona)
    {
        var lines = RoleDetailLabels
            .Select(label => ExtractRoleDetailSegment(persona, label, RoleDetailLabels))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        return lines.Length == 0
            ? persona
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", lines);
    }

    private static string ExtractRoleDetailSegment(string persona, string label, IReadOnlyList<string> labels)
    {
        var start = persona.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        var valueStart = start + label.Length;
        var next = labels
            .Select(candidate => persona.IndexOf(candidate, valueStart, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(persona.Length)
            .Min();
        var value = persona[valueStart..next].Trim(' ', '\r', '\n', '\t', '.');
        return string.IsNullOrWhiteSpace(value) ? "" : $"{label} {value}";
    }

    private void SetBothStatuses(string status)
    {
        setLoadStatus(status);
        setArenaRunStatus(status);
    }

    private static bool IsParticipant(string speakerId)
    {
        return AgentRosterService.IsParticipantId(speakerId);
    }

    private sealed record RoleDetailPayload(string Title, string Persona);
    private sealed record AccentColorPayload(string Key, string Color);
}
