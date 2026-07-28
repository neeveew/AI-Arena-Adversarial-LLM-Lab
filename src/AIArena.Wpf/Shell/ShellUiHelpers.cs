using System.Windows.Controls;
using System.Windows.Media;

namespace AIArena.Wpf;

internal static class ShellUiHelpers
{
    /// <summary>Matches the ellipsis WPF renders for CharacterEllipsis trimming.</summary>
    public const string EllipsisSuffix = "…";

    /// <summary>
    /// Used where the reader needs to know content was cut rather than ended,
    /// such as captured command output.
    /// </summary>
    public const string TruncatedNoticeSuffix = "... [truncated]";

    /// <summary>
    /// Shortens text to at most <paramref name="maxChars"/> including the
    /// suffix.
    ///
    /// This replaces five separate copies that disagreed on both the suffix
    /// ("...", "…", "... [truncated]") and on whether the limit was a hard cap:
    /// one reserved a single character for a three-character suffix and so
    /// overshot by two.
    /// </summary>
    public static string Truncate(string value, int maxChars, string suffix = EllipsisSuffix)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        if (maxChars <= suffix.Length)
        {
            return value[..Math.Max(0, maxChars)];
        }

        return value[..(maxChars - suffix.Length)] + suffix;
    }

    public static string SelectedComboTag(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;
    }

    public static void SelectComboTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    /// <summary>
    /// Flattens text to a single line and shortens it to <paramref name="maxLength"/>.
    ///
    /// This was missed when the five truncation copies were consolidated: it cut
    /// at maxLength and then appended three more characters, so it returned
    /// maxLength + 3 and quietly broke the "limit is a hard cap" rule the shared
    /// helper documents, while also ending in "..." where everything else had
    /// settled on a single ellipsis.
    /// </summary>
    public static string CompactPreview(string? text, int maxLength, string fallback)
    {
        var cleaned = string.IsNullOrWhiteSpace(text)
            ? fallback
            : string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Truncate(cleaned, maxLength);
    }

    public static Brush BlendBrush(Brush baseBrush, Brush accentBrush, double accentAmount)
    {
        var baseColor = BrushColor(baseBrush, Colors.Transparent);
        var accentColor = BrushColor(accentBrush, baseColor);
        var amount = Math.Clamp(accentAmount, 0, 1);
        return new SolidColorBrush(Color.FromRgb(
            BlendChannel(baseColor.R, accentColor.R, amount),
            BlendChannel(baseColor.G, accentColor.G, amount),
            BlendChannel(baseColor.B, accentColor.B, amount)));
    }

    internal static Color BrushColor(Brush brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    internal static byte BlendChannel(byte baseline, byte accent, double amount)
    {
        return (byte)Math.Round(baseline + ((accent - baseline) * amount));
    }
}
