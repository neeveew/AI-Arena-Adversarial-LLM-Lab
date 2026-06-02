using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AIArena.Wpf;

internal sealed class ShellCardFactory
{
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;

    public ShellCardFactory(Func<string, Brush> resourceBrush, Func<Brush, Brush, double, Brush> blendBrush)
    {
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
    }

    public Border CreateCard(string title, string body, Brush background, Brush accent)
    {
        return CreateCard(title, body, background, accent, null);
    }

    public Border CreateCard(string title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        return CreateCard(CreateCardTitle(title), body, background, accent, extraContent);
    }

    public Border CreateCard(UIElement title, string body, Brush background, Brush accent, UIElement? extraContent)
    {
        var border = new Border
        {
            Style = null,
            Background = background,
            BorderBrush = resourceBrush("ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(8, 0, 0, 8),
            Margin = new Thickness(-16, -16, 10, -16)
        };
        Grid.SetColumn(strip, 0);
        grid.Children.Add(strip);

        var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(stack, 1);
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = resourceBrush("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15
        });
        if (extraContent is not null)
        {
            stack.Children.Add(extraContent);
        }
        grid.Children.Add(stack);

        border.Child = grid;
        return border;
    }

    public Border CreateEmptyStateCard(string title, string body, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Quiet state",
            Foreground = resourceBrush("MutedTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

        return CreateCard(title, body, blendBrush(resourceBrush("CardBrush"), accent, 0.08), accent, panel);
    }

    public Border CreateSetupChip(string label, string value, Brush accent)
    {
        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.35),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private static TextBlock CreateCardTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }
}
