using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Core.Models;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class InternetApprovalDialog : Window
{
    public InternetApprovalChoice Choice { get; private set; } = InternetApprovalChoice.Deny;

    private InternetApprovalDialog(Window owner, ThemePalette theme, InternetToolRequest request)
    {
        InitializeComponent();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        Owner = owner;
        RequestText.Text = string.Join(
            Environment.NewLine,
            $"Requester: {request.RequesterId}",
            $"Tool: {request.Tool}",
            string.IsNullOrWhiteSpace(request.Query) ? "" : $"Query: {request.Query}",
            string.IsNullOrWhiteSpace(request.Url) ? "" : $"URL: {request.Url}",
            string.IsNullOrWhiteSpace(request.Reason) ? "" : $"Reason: {request.Reason}",
            $"Max results: {request.MaxResults}").Trim();
        ApplyTheme(theme);
    }

    public static InternetApprovalChoice Show(Window owner, ThemePalette theme, InternetToolRequest request)
    {
        var dialog = new InternetApprovalDialog(owner, theme, request);
        return dialog.ShowDialog() == true ? dialog.Choice : InternetApprovalChoice.Deny;
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
        var danger = Brush(theme.Danger);
        var dangerBorder = Brush(theme.DangerBorder);

        DialogShell.Background = panel;
        DialogShell.BorderBrush = primaryBorder;
        HeaderBar.Background = input;
        RequestPanel.Background = panel;
        RequestPanel.BorderBrush = border;
        TitleText.Foreground = text;
        RequestText.Foreground = text;
        DialogChrome.ApplyButtonStyle(CloseButton, input, border, muted);
        CloseButton.FontSize = 13;
        CloseButton.Padding = new Thickness(0);
        CloseButton.MinHeight = 28;
        DialogChrome.ApplyButtonStyle(DenyButton, danger, dangerBorder, Brushes.White);
        DialogChrome.ApplyButtonStyle(AlwaysApproveButton, input, primaryBorder, text);
        DialogChrome.ApplyButtonStyle(ApproveOnceButton, primary, primaryBorder, Brushes.White);
    }

    private void ApproveOnceButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = InternetApprovalChoice.ApproveOnce;
        DialogResult = true;
        Close();
    }

    private void AlwaysApproveButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = InternetApprovalChoice.AlwaysApprove;
        DialogResult = true;
        Close();
    }

    private void DenyButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = InternetApprovalChoice.Deny;
        DialogResult = true;
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

public enum InternetApprovalChoice
{
    ApproveOnce,
    AlwaysApprove,
    Deny
}
