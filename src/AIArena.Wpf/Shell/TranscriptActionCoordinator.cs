using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIArena.Wpf;

internal sealed class TranscriptActionCoordinator
{
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Brush> resourceBrush;
    private readonly List<WeakReference<Button>> actionButtons = [];

    public TranscriptActionCoordinator(
        Func<bool> compactTranscriptMode,
        Func<bool> isArenaBusy,
        Func<string, Brush> resourceBrush)
    {
        this.compactTranscriptMode = compactTranscriptMode;
        this.isArenaBusy = isArenaBusy;
        this.resourceBrush = resourceBrush;
    }

    public Button CreateButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind = TranscriptActionKind.Neutral, string? iconGlyph = null)
    {
        var iconMode = !string.IsNullOrWhiteSpace(iconGlyph);
        var compact = compactTranscriptMode();
        var iconSize = compact ? 36 : 40;
        var background = resourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("DisabledBorderBrush")
        };
        var foreground = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("TextBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerTextBrush"),
            _ => resourceBrush("TextBrush")
        };
        var button = new Button
        {
            Content = iconMode
                ? new TextBlock
                {
                    Text = iconGlyph,
                    FontFamily = ArenaTokens.IconFontFamily,
                    FontSize = compact ? 13 : 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : text,
            IsEnabled = enabled && !isArenaBusy(),
            Tag = enabled,
            Background = background,
            BorderBrush = border,
            Foreground = foreground,
            FontSize = iconMode ? (compact ? 13 : 15) : (compact ? 12 : 13),
            FontWeight = FontWeights.SemiBold,
            MinWidth = iconMode ? iconSize : 0,
            MinHeight = iconMode ? iconSize : (compact ? 36 : 40),
            Width = iconMode ? iconSize : double.NaN,
            Height = iconMode ? iconSize : double.NaN,
            Padding = iconMode ? new Thickness(0) : compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5),
            Margin = iconMode ? new Thickness(0, 0, compact ? 4 : 6, compact ? 3 : 5) : new Thickness(0, 0, compact ? 5 : 8, compact ? 5 : 8),
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, text);
        if (handler is not null)
        {
            button.Click += handler;
        }
        TrackButton(button);
        return button;
    }

    public Button CreateLabeledButton(string text, RoutedEventHandler? handler, bool enabled, TranscriptActionKind kind, string iconGlyph)
    {
        var compact = compactTranscriptMode();
        var background = resourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("DisabledBorderBrush")
        };
        var foreground = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("TextBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerTextBrush"),
            _ => resourceBrush("TextBrush")
        };
        var button = new Button
        {
            Content = CreateLabeledContent(text, iconGlyph, compact),
            IsEnabled = enabled && !isArenaBusy(),
            Tag = enabled,
            Background = background,
            BorderBrush = border,
            Foreground = foreground,
            FontSize = compact ? 12 : 13,
            FontWeight = FontWeights.SemiBold,
            MinHeight = compact ? 36 : 40,
            Padding = compact ? new Thickness(8, 4, 9, 4) : new Thickness(10, 6, 12, 6),
            Margin = new Thickness(0, 0, compact ? 5 : 8, compact ? 5 : 8),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetHelpText(button, text);
        if (handler is not null)
        {
            button.Click += handler;
        }
        TrackButton(button);
        return button;
    }

    private static StackPanel CreateLabeledContent(string text, string iconGlyph, bool compact)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = iconGlyph,
            FontFamily = ArenaTokens.IconFontFamily,
            FontSize = compact ? 12 : 14,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 0, compact ? 6 : 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = compact ? 12 : 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    internal int TrackedButtonCount
    {
        get
        {
            Prune();
            return actionButtons.Count;
        }
    }

    public void Prune()
    {
        for (var index = actionButtons.Count - 1; index >= 0; index--)
        {
            if (!actionButtons[index].TryGetTarget(out _))
            {
                actionButtons.RemoveAt(index);
            }
        }
    }

    public void UpdateBusyState(bool busy)
    {
        for (var index = actionButtons.Count - 1; index >= 0; index--)
        {
            if (!actionButtons[index].TryGetTarget(out var button))
            {
                actionButtons.RemoveAt(index);
                continue;
            }

            button.IsEnabled = !busy && button.Tag is true;
        }
    }

    private void TrackButton(Button button)
    {
        Prune();
        button.IsEnabled = !isArenaBusy() && button.Tag is true;
        if (!actionButtons.Any(reference =>
                reference.TryGetTarget(out var existing)
                && ReferenceEquals(existing, button)))
        {
            actionButtons.Add(new WeakReference<Button>(button));
        }

        button.Loaded -= ActionButton_Loaded;
        button.Unloaded -= ActionButton_Unloaded;
        button.Loaded += ActionButton_Loaded;
        button.Unloaded += ActionButton_Unloaded;
    }

    private void UntrackButton(Button button)
    {
        for (var index = actionButtons.Count - 1; index >= 0; index--)
        {
            if (!actionButtons[index].TryGetTarget(out var existing)
                || ReferenceEquals(existing, button))
            {
                actionButtons.RemoveAt(index);
            }
        }
    }

    private void ActionButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            TrackButton(button);
        }
    }

    private void ActionButton_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            UntrackButton(button);
        }
    }
}
