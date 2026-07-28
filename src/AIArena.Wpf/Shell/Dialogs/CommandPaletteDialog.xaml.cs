using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIArena.Wpf.Services;

namespace AIArena.Wpf;

public partial class CommandPaletteDialog : Window
{
    private readonly IReadOnlyList<ShellCommand> commands;
    private readonly IReadOnlyList<string> recentIds;

    private CommandPaletteDialog(
        Window owner,
        ThemePalette theme,
        IReadOnlyList<ShellCommand> commands,
        IReadOnlyList<string> recentIds)
    {
        InitializeComponent();
        DialogChrome.ImportOwnerResources(owner, this);
        DialogChrome.ApplyImplicitControlStyles(this);
        this.commands = commands;
        this.recentIds = recentIds;
        ApplyTheme(theme);
        ApplyQuery("");
        DialogChrome.PrepareModalWindow(
            this,
            owner,
            DialogShell,
            QueryText,
            "Commands",
            "Type to filter the command list. Up and Down move the selection, Enter runs it, Escape closes.");
    }

    private ShellCommand? Chosen { get; set; }

    /// <summary>
    /// Returns the chosen command, or null when the reader backed out. The
    /// caller invokes it rather than the dialog, so the action runs against a
    /// closed palette and can safely open another window.
    /// </summary>
    // Internal rather than public: the class itself has to be public for the
    // generated XAML partial, but ShellCommand is an internal shell concept.
    internal static ShellCommand? Show(
        Window owner,
        ThemePalette theme,
        IReadOnlyList<ShellCommand> commands,
        IReadOnlyList<string> recentIds)
    {
        var dialog = new CommandPaletteDialog(owner, theme, commands, recentIds);
        return dialog.ShowDialog() == true ? dialog.Chosen : null;
    }

    private void ApplyQuery(string query)
    {
        var matches = ShellCommandPalette.Filter(commands, query, recentIds);
        ResultsList.ItemsSource = matches;
        if (matches.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            ResultsList.ScrollIntoView(matches[0]);
        }

        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueryWatermark.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(
            ResultsList,
            matches.Count == 1 ? "1 command" : $"{matches.Count} commands");
    }

    private void QueryText_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyQuery(QueryText.Text);
    }

    /// <summary>
    /// Arrow keys move the list while the caret stays in the search box, which
    /// is what makes a palette feel like one: type, glance, press Enter.
    /// </summary>
    private void QueryText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                Accept();
                break;
            case Key.Escape:
                e.Handled = true;
                DialogResult = false;
                Close();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0)
        {
            return;
        }

        // Wrapping keeps a long list reachable from either end without the
        // selection sticking silently at a boundary.
        var next = (ResultsList.SelectedIndex + delta + ResultsList.Items.Count) % ResultsList.Items.Count;
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.Items[next]);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Accept();
    }

    private void Accept()
    {
        if (ResultsList.SelectedItem is not ShellCommand command)
        {
            return;
        }

        Chosen = command;
        DialogResult = true;
        Close();
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
        SearchGlyph.Foreground = primaryBorder;
        QueryText.Foreground = text;
        QueryText.CaretBrush = text;
        QueryText.SelectionBrush = primary;
        QueryWatermark.Foreground = muted;
        ResultsList.Foreground = text;
        EmptyText.Foreground = muted;
        FooterBar.Background = input;
        FooterBar.BorderBrush = border;
        FooterText.Foreground = muted;
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
