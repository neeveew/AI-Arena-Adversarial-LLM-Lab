using System.Windows;
using System.Windows.Automation;
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
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CloseButton.ToolTip = $"Close {title}";
        ConfirmButton.ToolTip = confirmText;
        CancelButton.ToolTip = cancelText;
        ConfirmButton.IsDefault = tone != ConfirmDialogTone.Danger;
        CancelButton.IsDefault = tone == ConfirmDialogTone.Danger;

        AutomationProperties.SetName(TitleText, title);
        AutomationProperties.SetName(MessageText, message);
        AutomationProperties.SetName(ToneText, tone == ConfirmDialogTone.Danger ? "Warning" : "Confirmation");
        AutomationProperties.SetName(ConfirmButton, confirmText);
        AutomationProperties.SetHelpText(ConfirmButton, tone == ConfirmDialogTone.Danger
            ? "Applies this destructive action."
            : "Applies this action.");
        AutomationProperties.SetItemStatus(ConfirmButton, tone == ConfirmDialogTone.Danger ? "destructive action" : "confirmation action");
        AutomationProperties.SetName(CancelButton, cancelText);
        AutomationProperties.SetHelpText(CancelButton, "Closes this confirmation without applying the action.");

        ApplyTheme(theme, tone);
        DialogChrome.PrepareModalWindow(
            this,
            owner,
            DialogShell,
            tone == ConfirmDialogTone.Danger ? CancelButton : ConfirmButton,
            title,
            message);
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
        ContentPanel.Background = input;
        ContentPanel.BorderBrush = border;
        TitleText.Foreground = text;
        MessageText.Foreground = text;
        ToneBadge.Background = input;
        ToneBadge.BorderBrush = primaryBorder;
        ToneText.Foreground = badge;
        ToneText.Text = tone == ConfirmDialogTone.Danger ? "\uE7BA" : "\uE897";
        ToneLabelText.Foreground = badge;
        ToneLabelText.Text = tone switch
        {
            ConfirmDialogTone.Danger => "REVIEW CAREFULLY",
            ConfirmDialogTone.Info => "REFERENCE",
            _ => "CONFIRM ACTION"
        };
        ShortcutText.Foreground = muted;
        ShortcutText.Text = tone switch
        {
            ConfirmDialogTone.Danger => $"Enter keeps this unchanged · choose {ConfirmButton.Content} to proceed",
            ConfirmDialogTone.Info => "Esc or Enter closes",
            _ => "Esc cancels · Enter confirms"
        };

        // Reference content has nothing to decline, so the second action is hidden.
        if (tone == ConfirmDialogTone.Info)
        {
            CancelButton.Visibility = Visibility.Collapsed;
        }
        FooterBar.Background = input;
        FooterBar.BorderBrush = border;

        DialogChrome.ApplyCloseButtonStyle(CloseButton, input, border, muted);
        DialogChrome.ApplyButtonStyle(CancelButton, input, border, text);
        DialogChrome.ApplyButtonStyle(ConfirmButton, primary, primaryBorder, text);
        ApplyCloseTargetSize(CloseButton);
        ApplyActionTargetSize(CancelButton);
        ApplyActionTargetSize(ConfirmButton);
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

public enum ConfirmDialogTone
{
    Normal,
    Danger,

    /// <summary>Reference content with nothing to confirm, such as the shortcut list.</summary>
    Info
}
