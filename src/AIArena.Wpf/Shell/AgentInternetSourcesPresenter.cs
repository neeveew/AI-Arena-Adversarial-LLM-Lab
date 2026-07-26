using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal static class AgentInternetSourcesPresenter
{
    private static readonly Color InternetBlueColor = Color.FromRgb(0x38, 0xBD, 0xF8);

    public static bool HasSources(AgentState agent)
    {
        return agent.InternetSources?.Sources.Count > 0;
    }

    public static bool HasSources(AgentInternetSourceSummary? sources)
    {
        return sources?.Sources.Count > 0;
    }

    public static Button CreateButton(
        AgentInternetSourceSummary sources,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        string automationName,
        double size = 24)
    {
        var blue = InternetBlueBrush();
        var targetSize = Math.Max(36, size);
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = "\uE774",
                FontFamily = ArenaTokens.IconFontFamily,
                FontSize = Math.Max(12, size - 11),
                Foreground = blue,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = targetSize,
            MinWidth = targetSize,
            Height = targetSize,
            MinHeight = targetSize,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = blendBrush(resourceBrush("InputBrush"), blue, 0.16),
            BorderBrush = blue,
            Foreground = blue,
            BorderThickness = new Thickness(1),
            ToolTip = $"Internet sources: {sources.Sources.Count}"
        };
        AutomationProperties.SetName(button, automationName);
        AutomationProperties.SetHelpText(button, "Shows the sources found by the agent's latest internet search.");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            ShowSourcesPopup(button, sources, resourceBrush, blendBrush);
        };

        return button;
    }

    internal static void ShowSourcesPopup(
        Button target,
        AgentInternetSourceSummary sources,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush)
    {
        if (target.Tag is Popup existingPopup)
        {
            existingPopup.IsOpen = false;
        }

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -8,
            VerticalOffset = 5,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = CreatePopupContent(sources, resourceBrush, blendBrush)
        };
        target.Tag = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(target.Tag, popup))
            {
                target.Tag = null;
            }
        };
        popup.IsOpen = true;
    }

    private static Border CreatePopupContent(
        AgentInternetSourceSummary sources,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush)
    {
        var blue = InternetBlueBrush();
        var stack = new StackPanel();

        var statusText = new TextBlock
        {
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 7)
        };

        var header = new Grid
        {
            Margin = new Thickness(0, 0, 0, 5)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Internet sources",
            Foreground = blue,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var copyAllButton = CreatePopupIconButton("\uE8C8", "Copy all internet sources", resourceBrush, blue);
        copyAllButton.Click += (_, e) =>
        {
            e.Handled = true;
            statusText.Text = ShellClipboard.TrySetText(FormatSourcesForCopy(sources))
                ? "Sources copied."
                : "Clipboard unavailable.";
        };
        Grid.SetColumn(copyAllButton, 1);
        header.Children.Add(copyAllButton);
        stack.Children.Add(header);

        if (!string.IsNullOrWhiteSpace(sources.Query))
        {
            stack.Children.Add(new TextBlock
            {
                Text = sources.Query,
                Foreground = resourceBrush("TextBrush"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        if (!string.IsNullOrWhiteSpace(sources.CheckedAt))
        {
            stack.Children.Add(new TextBlock
            {
                Text = sources.CheckedAt,
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
        stack.Children.Add(statusText);

        foreach (var source in sources.Items)
        {
            stack.Children.Add(CreateSourceRow(source, statusText, resourceBrush, blendBrush, blue));
        }

        return new Border
        {
            Width = 380,
            MaxHeight = 340,
            Background = resourceBrush("CardBrush"),
            BorderBrush = blue,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            }
        };
    }

    internal static string FormatSourcesForCopy(AgentInternetSourceSummary sources)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(sources.Query))
        {
            lines.Add($"Query: {sources.Query}");
        }

        if (!string.IsNullOrWhiteSpace(sources.CheckedAt))
        {
            lines.Add($"Checked: {sources.CheckedAt}");
        }

        lines.AddRange(sources.Items.Select((source, index) => $"{index + 1}. {FormatSourceForCopy(source)}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static Border CreateSourceRow(
        AgentInternetSourceItem source,
        TextBlock statusText,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Brush blue)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = SourceTitle(source),
            Foreground = resourceBrush("TextBrush"),
            FontSize = 10.8,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        var location = SourceLocation(source);
        if (!string.IsNullOrWhiteSpace(location))
        {
            text.Children.Add(new TextBlock
            {
                Text = location,
                Foreground = blue,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        if (!string.IsNullOrWhiteSpace(source.Snippet))
        {
            text.Children.Add(new TextBlock
            {
                Text = source.Snippet,
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10,
                LineHeight = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        else if (!string.IsNullOrWhiteSpace(source.DisplayText)
            && !source.DisplayText.Equals(source.Url, StringComparison.OrdinalIgnoreCase)
            && !source.DisplayText.Equals(source.Title, StringComparison.OrdinalIgnoreCase))
        {
            text.Children.Add(new TextBlock
            {
                Text = source.DisplayText,
                Foreground = resourceBrush("MutedTextBrush"),
                FontSize = 10,
                LineHeight = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var copyButton = CreatePopupIconButton("\uE8C8", "Copy source", resourceBrush, blue);
        copyButton.Click += (_, e) =>
        {
            e.Handled = true;
            statusText.Text = ShellClipboard.TrySetText(FormatSourceForCopy(source))
                ? "Source copied."
                : "Clipboard unavailable.";
        };
        actions.Children.Add(copyButton);

        var openButton = CreatePopupIconButton("\uE8A7", "Open source", resourceBrush, blue);
        openButton.IsEnabled = TryNormalizeWebSourceUrl(source.Url, out var normalizedSourceUrl);
        openButton.Opacity = openButton.IsEnabled ? 1.0 : 0.45;
        openButton.Click += (_, e) =>
        {
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(normalizedSourceUrl))
            {
                return;
            }

            var launched = ShellProcessLauncher.TryStart(
                new ProcessStartInfo(normalizedSourceUrl)
                {
                    UseShellExecute = true
                },
                out var error);
            statusText.Text = launched ? "Source opened." : $"Open failed: {error}";
        };
        actions.Children.Add(openButton);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), blue, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), blue, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 7),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row
        };
    }

    private static Button CreatePopupIconButton(string glyph, string label, Func<string, Brush> resourceBrush, Brush blue)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = ArenaTokens.IconFontFamily,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = 36,
            MinWidth = 36,
            Height = 36,
            MinHeight = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = resourceBrush("InputBrush"),
            BorderBrush = blue,
            Foreground = blue,
            ToolTip = label
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, label);
        return button;
    }

    internal static bool TryNormalizeWebSourceUrl(string? value, out string normalizedUrl)
    {
        normalizedUrl = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static string SourceTitle(AgentInternetSourceItem source)
    {
        if (!string.IsNullOrWhiteSpace(source.Title))
        {
            return source.Title;
        }

        if (!string.IsNullOrWhiteSpace(source.Domain))
        {
            return source.Domain;
        }

        return string.IsNullOrWhiteSpace(source.DisplayText) ? "Source" : source.DisplayText;
    }

    private static string SourceLocation(AgentInternetSourceItem source)
    {
        var parts = new[] { source.Domain, source.PublishedAt, source.Url }
            .Where(item => !string.IsNullOrWhiteSpace(item));
        return string.Join(" - ", parts);
    }

    private static string FormatSourceForCopy(AgentInternetSourceItem source)
    {
        var parts = new List<string>
        {
            SourceTitle(source)
        };
        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            parts.Add(source.Url);
        }
        if (!string.IsNullOrWhiteSpace(source.Snippet))
        {
            parts.Add(source.Snippet);
        }
        else if (!string.IsNullOrWhiteSpace(source.DisplayText)
            && !parts.Any(part => part.Equals(source.DisplayText, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add(source.DisplayText);
        }

        return string.Join(" - ", parts);
    }

    private static SolidColorBrush InternetBlueBrush()
    {
        return new SolidColorBrush(InternetBlueColor);
    }
}
