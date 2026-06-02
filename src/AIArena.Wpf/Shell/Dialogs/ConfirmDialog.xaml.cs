using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(
        Window owner,
        ThemePalette theme,
        string title,
        string message,
        string confirmText,
        string cancelText,
        ConfirmDialogTone tone)
    {
        InitializeComponent();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        Owner = owner;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        ApplyTheme(theme, tone);
    }

    public static bool Show(
        Window owner,
        ThemePalette theme,
        string title,
        string message,
        string confirmText,
        string cancelText = "Cancel",
        ConfirmDialogTone tone = ConfirmDialogTone.Danger)
    {
        var dialog = new ConfirmDialog(owner, theme, title, message, confirmText, cancelText, tone);
        return dialog.ShowDialog() == true;
    }

    private void ApplyTheme(ThemePalette theme, ConfirmDialogTone tone)
    {
        var panel = Brush(theme.Panel);
        var input = Brush(theme.Input);
        var border = Brush(theme.Border);
        var text = Brush(theme.Text);
        var muted = Brush(theme.MutedText);
        var primary = tone == ConfirmDialogTone.Danger ? Brush(theme.Danger) : Brush(theme.Primary);
        var primaryBorder = tone == ConfirmDialogTone.Danger ? Brush(theme.DangerBorder) : Brush(theme.PrimaryBorder);
        var badge = tone == ConfirmDialogTone.Danger ? Brush(theme.DangerBorder) : Brush(theme.PrimaryBorder);

        DialogShell.Background = panel;
        DialogShell.BorderBrush = primaryBorder;
        HeaderBar.Background = input;
        ContentPanel.Background = panel;
        TitleText.Foreground = text;
        MessageText.Foreground = text;
        ToneBadge.Background = badge;
        ToneText.Text = tone == ConfirmDialogTone.Danger ? "!" : "?";

        DialogChrome.ApplyButtonStyle(CloseButton, input, border, muted);
        CloseButton.FontSize = 13;
        CloseButton.Padding = new Thickness(0);
        CloseButton.MinHeight = 28;
        DialogChrome.ApplyButtonStyle(CancelButton, input, border, text);
        DialogChrome.ApplyButtonStyle(ConfirmButton, primary, primaryBorder, Brushes.White);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
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
        DialogChrome.DragMoveIfPossible(this, e);
    }

    private static SolidColorBrush Brush(Color color)
    {
        return new SolidColorBrush(color);
    }
}

public enum ConfirmDialogTone
{
    Normal,
    Danger
}
