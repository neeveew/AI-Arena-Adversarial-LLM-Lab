using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIArena.Wpf;

internal sealed class TranscriptActionCoordinator
{
    private readonly Func<bool> compactTranscriptMode;
    private readonly Func<bool> isArenaBusy;
    private readonly Func<string, Brush> resourceBrush;
    private readonly List<Button> actionButtons = [];

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
        var iconSize = compact ? 24 : 30;
        var background = resourceBrush("InputBrush");
        var border = kind switch
        {
            TranscriptActionKind.Primary => resourceBrush("PrimaryBorderBrush"),
            TranscriptActionKind.Danger => resourceBrush("DangerBorderBrush"),
            _ => resourceBrush("ControlBorderBrush")
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
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = compact ? 13 : 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
                : text,
            IsEnabled = enabled && !isArenaBusy(),
            Tag = enabled,
            Background = iconMode ? Brushes.Transparent : background,
            BorderBrush = iconMode ? Brushes.Transparent : border,
            Foreground = foreground,
            FontSize = iconMode ? (compact ? 13 : 15) : (compact ? 12 : 13),
            FontWeight = FontWeights.SemiBold,
            MinWidth = iconMode ? iconSize : 0,
            MinHeight = iconMode ? iconSize : (compact ? 26 : 32),
            Width = iconMode ? iconSize : double.NaN,
            Height = iconMode ? iconSize : double.NaN,
            Padding = iconMode ? new Thickness(0) : compact ? new Thickness(8, 3, 8, 3) : new Thickness(10, 5, 10, 5),
            Margin = iconMode ? new Thickness(0, 0, compact ? 3 : 5, compact ? 2 : 4) : new Thickness(0, 0, compact ? 5 : 8, compact ? 5 : 8),
            Opacity = enabled ? 1.0 : 0.55,
            ToolTip = text
        };
        if (handler is not null)
        {
            button.Click += handler;
        }
        actionButtons.Add(button);
        return button;
    }

    public void Clear()
    {
        actionButtons.Clear();
    }

    public void UpdateBusyState(bool busy)
    {
        foreach (var button in actionButtons)
        {
            button.IsEnabled = !busy && button.Tag is true;
        }
    }
}
