using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class AiChoicePromptDialog : Window
{
    public string Prompt { get; private set; } = "";

    private AiChoicePromptDialog(Window owner, ThemePalette theme, string initialPrompt)
    {
        InitializeComponent();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        PromptText.Text = initialPrompt.Trim();
        PromptText.SelectAll();
        ApplyTheme(theme);
        DialogChrome.PrepareModalWindow(
            this,
            owner,
            DialogShell,
            PromptText,
            "AI Choice topic prompt",
            "Optionally direct the next AI Choice match, then generate it. Escape cancels and Control+Enter generates.");
    }

    public static string? Show(Window owner, ThemePalette theme, string initialPrompt)
    {
        var dialog = new AiChoicePromptDialog(owner, theme, initialPrompt);
        return dialog.ShowDialog() == true ? dialog.Prompt : null;
    }

    private void ApplyTheme(ThemePalette theme)
    {
        var panel = Brush(theme.Panel);
        var input = Brush(theme.Input);
        var border = Brush(theme.Border);
        var text = Brush(theme.Text);
        var muted = Brush(theme.MutedText);
        var narrator = Brush(theme.NarratorAccent);

        DialogShell.Background = panel;
        DialogShell.BorderBrush = narrator;
        HeaderBar.Background = input;
        ContentPanel.Background = input;
        ContentPanel.BorderBrush = border;
        TitleText.Foreground = text;
        ChoiceBadge.Background = panel;
        ChoiceBadge.BorderBrush = narrator;
        ChoiceGlyph.Foreground = narrator;
        ChoiceLabelText.Foreground = narrator;
        PromptLabelText.Foreground = text;
        PromptCountText.Foreground = muted;
        PromptHintText.Foreground = muted;
        PromptShortcutText.Foreground = muted;
        PromptText.Background = panel;
        PromptText.BorderBrush = border;
        PromptText.Foreground = text;
        PromptText.CaretBrush = text;
        PromptText.SelectionBrush = narrator;
        FooterBar.Background = input;
        FooterBar.BorderBrush = border;

        DialogChrome.ApplyCloseButtonStyle(CloseButton, input, border, muted);
        DialogChrome.ApplyButtonStyle(CancelButton, input, border, text);
        DialogChrome.ApplyButtonStyle(GenerateButton, narrator, narrator, Brushes.White);
        ApplyCloseTargetSize(CloseButton);
        ApplyActionTargetSize(CancelButton);
        ApplyActionTargetSize(GenerateButton);
    }

    private void PromptText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        GenerateButton_Click(GenerateButton, new RoutedEventArgs());
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        Prompt = PromptText.Text.Trim();
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
