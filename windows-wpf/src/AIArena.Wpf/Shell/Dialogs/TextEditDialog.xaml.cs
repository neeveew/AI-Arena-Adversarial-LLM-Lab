using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class TextEditDialog : Window
{
    private TextEditDialog(Window owner, ThemePalette theme, string title, string value, string? subtitle)
    {
        InitializeComponent();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        Owner = owner;
        Title = title;
        TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            SubtitleText.Text = subtitle;
        }

        EditText.Text = value;
        EditText.SelectAll();
        ApplyTheme(theme);
    }

    public string TextValue { get; private set; } = "";

    public static string? Show(Window owner, ThemePalette theme, string title, string value, string? subtitle = null)
    {
        var dialog = new TextEditDialog(owner, theme, title, value, subtitle);
        return dialog.ShowDialog() == true ? dialog.TextValue : null;
    }

    private void ApplyTheme(ThemePalette theme)
    {
        var panel = Brush(theme.Panel);
        var input = Brush(theme.Input);
        var border = Brush(theme.Border);
        var text = Brush(theme.Text);
        var muted = Brush(theme.MutedText);
        var primary = Brush(theme.Primary);
        var primaryBorder = Brush(theme.PrimaryBorder);

        DialogShell.Background = panel;
        DialogShell.BorderBrush = primaryBorder;
        HeaderBar.Background = input;
        TitleText.Foreground = text;
        SubtitleText.Foreground = muted;
        EditText.Background = input;
        EditText.Foreground = text;
        EditText.BorderBrush = border;
        EditText.Padding = new Thickness(10);

        DialogChrome.ApplyButtonStyle(CloseButton, input, border, muted);
        CloseButton.FontSize = 13;
        CloseButton.Padding = new Thickness(0);
        CloseButton.MinHeight = 28;
        DialogChrome.ApplyButtonStyle(CancelButton, input, border, text);
        DialogChrome.ApplyButtonStyle(ApplyButton, primary, primaryBorder, Brushes.White);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        TextValue = EditText.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DialogShell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogChrome.DragMoveIfPossible(this, e, ignoreTextInputs: true);
    }

    private static SolidColorBrush Brush(Color color)
    {
        return new SolidColorBrush(color);
    }
}
