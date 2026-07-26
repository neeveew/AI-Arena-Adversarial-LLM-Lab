using System.Windows;
using System.Windows.Automation;
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
        Title = title;
        TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            SubtitleText.Text = subtitle;
        }

        EditText.Text = value;
        EditText.SelectAll();
        var editorDescription = SubtitleText.Text;
        CloseButton.ToolTip = $"Close {title}";
        AutomationProperties.SetName(TitleText, title);
        AutomationProperties.SetName(SubtitleText, editorDescription);
        AutomationProperties.SetName(EditText, $"{title} text");
        AutomationProperties.SetHelpText(EditText, $"{editorDescription} Press Control+Enter to apply.");
        ApplyTheme(theme);
        DialogChrome.PrepareModalWindow(
            this,
            owner,
            DialogShell,
            EditText,
            title,
            $"{editorDescription} Escape cancels and Control+Enter applies changes.");
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
        EditorPanel.Background = input;
        EditorPanel.BorderBrush = border;
        TitleText.Foreground = text;
        SubtitleText.Foreground = muted;
        EditorBadge.Background = panel;
        EditorBadge.BorderBrush = primaryBorder;
        EditorGlyph.Foreground = primaryBorder;
        EditText.Background = panel;
        EditText.Foreground = text;
        EditText.BorderBrush = border;
        EditText.CaretBrush = text;
        EditText.SelectionBrush = primary;
        EditText.FontSize = 14;
        EditText.Padding = new Thickness(14, 12, 14, 12);
        EditorShortcutText.Foreground = muted;
        EditorCountText.Foreground = muted;
        FooterBar.Background = input;
        FooterBar.BorderBrush = border;

        DialogChrome.ApplyCloseButtonStyle(CloseButton, input, border, muted);
        DialogChrome.ApplyButtonStyle(CancelButton, input, border, text);
        DialogChrome.ApplyButtonStyle(ApplyButton, primary, primaryBorder, text);
        ApplyCloseTargetSize(CloseButton);
        ApplyActionTargetSize(CancelButton);
        ApplyActionTargetSize(ApplyButton);
    }

    private void EditText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        ApplyButton_Click(ApplyButton, new RoutedEventArgs());
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

    private static void ApplyActionTargetSize(System.Windows.Controls.Button button)
    {
        button.Height = 36;
        button.MinHeight = 36;
        button.FontSize = 13;
        button.Padding = new Thickness(18, 0, 18, 0);
    }

    private static void ApplyCloseTargetSize(System.Windows.Controls.Button button)
    {
        button.Width = 32;
        button.Height = 32;
        button.MinWidth = 32;
        button.MinHeight = 32;
    }
}
