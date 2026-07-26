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
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.42),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.MediumRadius,
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(ArenaTokens.MediumRadiusValue, 0, 0, ArenaTokens.MediumRadiusValue),
            Margin = new Thickness(-18, -18, 11, -18)
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
            FontSize = ArenaTokens.HeadingFontSize,
            LineHeight = 21
        });
        if (extraContent is not null)
        {
            stack.Children.Add(extraContent);
        }
        grid.Children.Add(stack);

        border.Child = grid;
        return border;
    }

    public Border CreateEmptyStateCard(string title, string body, Brush accent, string statusLabel = "No results")
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        panel.Children.Add(new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.08),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.34),
            BorderThickness = new Thickness(1),
            CornerRadius = ArenaTokens.SmallRadius,
            Padding = new Thickness(8, 2, 8, 3),
            Child = new TextBlock
            {
                Text = statusLabel,
                Foreground = accent,
                FontSize = ArenaTokens.LabelFontSize,
                FontWeight = FontWeights.SemiBold
            }
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
            CornerRadius = ArenaTokens.SmallRadius,
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text = $"{label}: {value}",
                Foreground = accent,
                FontSize = ArenaTokens.LabelFontSize,
                FontWeight = FontWeights.SemiBold
            }
        };
    }

    private TextBlock CreateCardTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            Foreground = resourceBrush("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = ArenaTokens.TitleFontSize,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }
}
