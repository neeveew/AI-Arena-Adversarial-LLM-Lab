using System.Windows;
using System.Windows.Controls.Primitives;
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
        ApplyButton(CloseButton, input, border, muted);
        CloseButton.FontSize = 13;
        CloseButton.Padding = new Thickness(0);
        CloseButton.MinHeight = 28;
        ApplyButton(DenyButton, danger, dangerBorder, Brushes.White);
        ApplyButton(AlwaysApproveButton, input, primaryBorder, text);
        ApplyButton(ApproveOnceButton, primary, primaryBorder, Brushes.White);
    }

    private static void ApplyButton(System.Windows.Controls.Button button, Brush background, Brush border, Brush foreground)
    {
        button.Background = background;
        button.BorderBrush = border;
        button.Foreground = foreground;
        button.FontWeight = FontWeights.SemiBold;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.MinHeight = 38;
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
        if (e.ButtonState == MouseButtonState.Pressed && !StartedOnButton(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private static bool StartedOnButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
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
