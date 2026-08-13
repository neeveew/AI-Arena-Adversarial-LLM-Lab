using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AIArena.Wpf.Help.Presentation;

internal sealed class HelpHighlightedTextBlock : TextBlock
{
    public static readonly DependencyProperty HighlightTextProperty = DependencyProperty.Register(
        nameof(HighlightText),
        typeof(string),
        typeof(HelpHighlightedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnHighlightChanged));

    public static readonly DependencyProperty HighlightRangesProperty = DependencyProperty.Register(
        nameof(HighlightRanges),
        typeof(IReadOnlyList<HelpTextRange>),
        typeof(HelpHighlightedTextBlock),
        new FrameworkPropertyMetadata(null, OnHighlightChanged));

    public string HighlightText
    {
        get => (string)GetValue(HighlightTextProperty);
        set => SetValue(HighlightTextProperty, value);
    }

    public IReadOnlyList<HelpTextRange>? HighlightRanges
    {
        get => (IReadOnlyList<HelpTextRange>?)GetValue(HighlightRangesProperty);
        set => SetValue(HighlightRangesProperty, value);
    }

    private static void OnHighlightChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((HelpHighlightedTextBlock)dependencyObject).RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines.Clear();
        var text = HighlightText ?? string.Empty;
        var ranges = (HighlightRanges ?? [])
            .Where(range => range.Start >= 0 && range.Length > 0 && range.Start < text.Length)
            .Select(range => new HelpTextRange(range.Start, Math.Min(range.Length, text.Length - range.Start)))
            .OrderBy(range => range.Start)
            .ToArray();
        if (ranges.Length == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Start < cursor)
            {
                continue;
            }

            if (range.Start > cursor)
            {
                Inlines.Add(new Run(text[cursor..range.Start]));
            }

            var highlighted = new Run(text.Substring(range.Start, range.Length))
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = TryFindResource("PrimaryBorderBrush") as Brush ?? Foreground
            };
            Inlines.Add(highlighted);
            cursor = range.Start + range.Length;
        }

        if (cursor < text.Length)
        {
            Inlines.Add(new Run(text[cursor..]));
        }
    }
}
