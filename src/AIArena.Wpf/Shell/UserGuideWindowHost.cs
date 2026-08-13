using System.Collections;
using AIArena.Wpf.Help;
using AIArena.Wpf.Help.Presentation;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace AIArena.Wpf;

/// <summary>
/// Owns the single modeless Help Center instance while preserving the shell's
/// established User Guide entry point.
/// </summary>
internal sealed class UserGuideWindowHost
{
    private const string AppIconResourceKey = "assets/ai-arena-icon.ico";
    private const string GuideHeaderIconResourceKey = "assets/ai-arena-guide-icon.png";
    private readonly HelpCenterRunState runState = new();
    private HelpCenterWindow? window;

    internal event EventHandler<HelpAppRouteRequestedEventArgs>? AppRouteRequested;

    internal string? CurrentArticleId => window?.CurrentArticleId;

    public void Close()
    {
        if (window is { IsVisible: true })
        {
            window.Close();
        }
    }

    public bool Show(Window owner, string? articleId = null, IInputElement? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (window is { IsVisible: true } existing)
        {
            existing.SetLauncher(launcher);
            if (!string.IsNullOrWhiteSpace(articleId))
            {
                existing.NavigateTo(articleId);
            }

            existing.Activate();
            if (!string.IsNullOrWhiteSpace(articleId))
            {
                existing.Dispatcher.BeginInvoke(existing.FocusCurrentDestination, DispatcherPriority.Input);
            }

            return true;
        }

        var manifestPath = OfflineHelpContentService.ResolveDefaultManifestPath();
        if (manifestPath is null)
        {
            return false;
        }

        try
        {
            var dialog = new HelpCenterWindow(owner, new OfflineHelpContentService(manifestPath), launcher, runState);
            dialog.AppRouteRequested += Window_AppRouteRequested;
            dialog.Closed += Window_Closed;
            window = dialog;
            dialog.NavigateTo(string.IsNullOrWhiteSpace(articleId) ? runState.LastArticleId ?? "home" : articleId);
            dialog.Show();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            window = null;
            return false;
        }
    }

    public void RefreshTheme(Window owner)
    {
        if (window is not { IsVisible: true } dialog)
        {
            return;
        }

        DialogChrome.ImportOwnerResources(owner, dialog);
        var currentArticle = dialog.CurrentArticleId;
        if (!string.IsNullOrWhiteSpace(currentArticle))
        {
            dialog.NavigateTo(currentArticle);
        }
    }

    private void Window_AppRouteRequested(object? sender, HelpAppRouteRequestedEventArgs e) =>
        AppRouteRequested?.Invoke(this, e);

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (sender is HelpCenterWindow dialog)
        {
            dialog.AppRouteRequested -= Window_AppRouteRequested;
            dialog.Closed -= Window_Closed;
        }

        window = null;
    }

    internal static ImageSource CreateAppIconImageSource() =>
        CreateResourceImageSource(AppIconResourceKey, new Uri("/Assets/ai-arena-icon.ico", UriKind.Relative));

    internal static ImageSource CreateGuideHeaderIconImageSource() =>
        CreateResourceImageSource(GuideHeaderIconResourceKey, new Uri("/Assets/ai-arena-guide-icon.png", UriKind.Relative));

    private static ImageSource CreateResourceImageSource(string resourceKey, Uri fallbackUri)
    {
        var resourceName = $"{typeof(UserGuideWindowHost).Assembly.GetName().Name}.g.resources";
        using var resources = typeof(UserGuideWindowHost).Assembly.GetManifestResourceStream(resourceName);
        if (resources is not null)
        {
            using var reader = new ResourceReader(resources);
            foreach (DictionaryEntry entry in reader)
            {
                if (entry.Key is not string key
                    || !key.Equals(resourceKey, StringComparison.OrdinalIgnoreCase)
                    || entry.Value is not Stream iconStream)
                {
                    continue;
                }

                if (iconStream.CanSeek)
                {
                    iconStream.Position = 0;
                }

                var icon = BitmapFrame.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                icon.Freeze();
                return icon;
            }
        }

        using var embeddedIcon = typeof(UserGuideWindowHost).Assembly.GetManifestResourceStream(resourceKey);
        if (embeddedIcon is not null)
        {
            var icon = BitmapFrame.Create(embeddedIcon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            icon.Freeze();
            return icon;
        }

        return BitmapFrame.Create(fallbackUri);
    }

    internal static bool TryReadGuideText(string guidePath, out string guideText)
    {
        try
        {
            guideText = File.ReadAllText(guidePath);
            return !string.IsNullOrWhiteSpace(guideText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            guideText = string.Empty;
            return false;
        }
    }

    internal static string? ResolveUserGuidePathForHelpCenter()
    {
        var installedGuide = IOPath.Combine(AppContext.BaseDirectory, "USER_GUIDE.md");
        if (File.Exists(installedGuide))
        {
            return installedGuide;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var sourceGuide = IOPath.Combine(current.FullName, "docs", "USER_GUIDE.md");
            if (File.Exists(sourceGuide))
            {
                return sourceGuide;
            }

            current = current.Parent;
        }

        return null;
    }

    internal static IReadOnlyList<string> DebugFilteredGuideSectionTitles(string guideText, string query)
    {
        var sections = ParseLegacySections(guideText);
        var tokens = (query ?? string.Empty)
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return sections
            .Where(section => tokens.Length == 0 || tokens.All(token => section.Text.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .Select(section => section.Title)
            .ToArray();
    }

    internal static (Rect TitleBounds, Rect PanelBounds) DebugMeasureContentHeaderTitle(double width, string title)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = ArenaTokens.PageTitleFontSize,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new System.Windows.Controls.Grid { Margin = new Thickness(24, 18, 24, 14) };
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(64) });
        header.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        System.Windows.Controls.Grid.SetColumn(text, 1);
        header.Children.Add(text);
        var panel = new System.Windows.Controls.Border { Child = header };
        panel.Measure(new Size(width, 260));
        panel.Arrange(new Rect(0, 0, width, 260));
        panel.UpdateLayout();
        return (new Rect(text.TranslatePoint(new Point(), panel), text.RenderSize), new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
    }

    private static IReadOnlyList<(string Title, string Text)> ParseLegacySections(string guideText)
    {
        var sections = new List<(string, string)>();
        using var reader = new StringReader(guideText);
        var title = "Overview";
        var content = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (content.Count > 0)
                {
                    sections.Add((title, string.Join(Environment.NewLine, content)));
                    content.Clear();
                }

                title = line.TrimStart('#', ' ').Trim();
            }

            content.Add(line);
        }

        if (content.Count > 0)
        {
            sections.Add((title, string.Join(Environment.NewLine, content)));
        }

        return sections;
    }
}
