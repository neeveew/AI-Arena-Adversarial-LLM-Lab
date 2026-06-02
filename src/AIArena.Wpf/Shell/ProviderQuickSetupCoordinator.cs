using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIArena.Wpf.Models;

namespace AIArena.Wpf;

internal sealed class ProviderQuickSetupCoordinator
{
    private const string DefaultBaseUrl = "http://127.0.0.1:1234/v1";

    private readonly TranscriptActionCoordinator transcriptActions;
    private readonly Func<IEnumerable<string>> advertisedModels;
    private readonly Func<string, Brush> resourceBrush;
    private readonly Func<Brush, Brush, double, Brush> blendBrush;
    private readonly Func<string, string, TextBlock, Task> saveAndTestProviderQuickSetupAsync;
    private readonly Action<string?, string?> openModelProviderSettings;

    public ProviderQuickSetupCoordinator(
        TranscriptActionCoordinator transcriptActions,
        Func<IEnumerable<string>> advertisedModels,
        Func<string, Brush> resourceBrush,
        Func<Brush, Brush, double, Brush> blendBrush,
        Func<string, string, TextBlock, Task> saveAndTestProviderQuickSetupAsync,
        Action<string?, string?> openModelProviderSettings)
    {
        this.transcriptActions = transcriptActions;
        this.advertisedModels = advertisedModels;
        this.resourceBrush = resourceBrush;
        this.blendBrush = blendBrush;
        this.saveAndTestProviderQuickSetupAsync = saveAndTestProviderQuickSetupAsync;
        this.openModelProviderSettings = openModelProviderSettings;
    }

    public Border CreateCard(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var accent = snapshot.ProviderOnline ? resourceBrush("BetaAccentBrush") : resourceBrush("DangerBorderBrush");
        var baseUrlBox = new TextBox
        {
            Text = QuickBaseUrl(snapshot),
            Background = resourceBrush("InputBrush"),
            Foreground = resourceBrush("TextBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Padding = new Thickness(8),
            MinWidth = 230,
            ToolTip = "OpenAI-compatible provider base URL."
        };

        var modelBox = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            Text = QuickModelText(snapshot, current),
            Background = resourceBrush("InputBrush"),
            Foreground = resourceBrush("TextBrush"),
            BorderBrush = resourceBrush("ControlBorderBrush"),
            Padding = new Thickness(8),
            MinWidth = 230,
            ToolTip = "Pick an advertised model or type one manually."
        };
        foreach (var model in advertisedModels())
        {
            modelBox.Items.Add(model);
        }

        var statusText = new TextBlock
        {
            Text = SessionOverviewCoordinator.ProviderSetupStatus(snapshot),
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        };

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(transcriptActions.CreateButton("Save + test", async (_, _) =>
        {
            await saveAndTestProviderQuickSetupAsync(baseUrlBox.Text, modelBox.Text, statusText);
        }, true, TranscriptActionKind.Primary));
        actions.Children.Add(transcriptActions.CreateButton("Open settings", (_, _) =>
        {
            openModelProviderSettings(baseUrlBox.Text, modelBox.Text);
        }, true));

        var fields = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var baseUrlStack = CreateFieldStack("Provider base URL", baseUrlBox);
        fields.Children.Add(baseUrlStack);

        var modelStack = CreateFieldStack("Default model", modelBox);
        Grid.SetColumn(modelStack, 2);
        fields.Children.Add(modelStack);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Provider setup",
            Foreground = resourceBrush("TextBrush"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Connect LM Studio or another OpenAI-compatible /v1 provider before running turns.",
            Foreground = resourceBrush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0)
        });
        panel.Children.Add(fields);
        panel.Children.Add(actions);
        panel.Children.Add(statusText);

        return new Border
        {
            Background = blendBrush(resourceBrush("InputBrush"), accent, 0.1),
            BorderBrush = blendBrush(resourceBrush("ControlBorderBrush"), accent, 0.5),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 2),
            Child = panel
        };
    }

    internal static bool ShouldShowProviderSetup(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var currentModel = SessionOverviewCoordinator.CurrentTurnModel(snapshot, current);
        return !snapshot.ProviderOnline
            || string.IsNullOrWhiteSpace(currentModel)
            || currentModel == "-"
            || string.IsNullOrWhiteSpace(snapshot.ProviderModel)
            || snapshot.ProviderModel == "-";
    }

    internal static string QuickBaseUrl(ArenaViewSnapshot snapshot)
    {
        return string.IsNullOrWhiteSpace(snapshot.ProviderBaseUrl) || snapshot.ProviderBaseUrl == "-"
            ? DefaultBaseUrl
            : snapshot.ProviderBaseUrl;
    }

    internal static string QuickModelText(ArenaViewSnapshot snapshot, AgentState? current)
    {
        var model = SessionOverviewCoordinator.CurrentTurnModel(snapshot, current);
        return model == "-" ? "" : model;
    }

    private StackPanel CreateFieldStack(string label, Control input)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = resourceBrush("MutedTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        stack.Children.Add(input);
        return stack;
    }
}
