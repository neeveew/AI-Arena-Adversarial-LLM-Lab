using System.Windows.Controls;

namespace AIArena.Wpf;

internal static class ShellUiHelpers
{
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

    public static string CompactPreview(string? text, int maxLength, string fallback)
    {
        var cleaned = string.IsNullOrWhiteSpace(text)
            ? fallback
            : string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Length <= maxLength ? cleaned : $"{cleaned[..maxLength]}...";
    }
}
