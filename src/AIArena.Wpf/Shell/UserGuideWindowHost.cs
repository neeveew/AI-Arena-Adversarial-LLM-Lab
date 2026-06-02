using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AIArena.Wpf;

internal sealed class UserGuideWindowHost
{
    private static readonly string[] ThemeResourceKeys =
    [
        "AppBackgroundBrush",
        "TopBarBrush",
        "PanelBrush",
        "CardBrush",
        "InputBrush",
        "TranscriptHeaderBrush",
        "TranscriptBodyBrush",
        "ControlBorderBrush",
        "TextBrush",
        "MutedTextBrush",
        "PrimaryBrush",
        "PrimaryBorderBrush",
        "AssistBrush",
        "AssistBorderBrush",
        "HoverBorderBrush",
        "NavHoverBrush",
        "NavActiveBrush",
        "NavPressedBrush",
        "PressedPrimaryBrush",
        "DangerBorderBrush",
        "DangerBrush",
        "DangerTextBrush",
        "DisabledBrush",
        "DisabledBorderBrush",
        "DisabledTextBrush",
        "OverlayBrush",
        "AlphaAccentBrush",
        "BetaAccentBrush",
        "GammaAccentBrush",
        "DeltaAccentBrush",
        "NarratorAccentBrush",
        "OperatorAccentBrush"
    ];

    private static readonly Type[] ThemeStyleKeys =
    [
        typeof(Button),
        typeof(TextBox),
        typeof(ComboBox),
        typeof(CheckBox)
    ];

    private Window? _window;

    public bool Show(Window owner)
    {
        if (_window is { IsVisible: true })
        {
            _window.Activate();
            return true;
        }

        var guidePath = ResolveUserGuidePath();
        if (guidePath is null)
        {
            return false;
        }

        var guideText = File.ReadAllText(guidePath);
        var sections = BuildUserGuideSections(guideText);
        var dialog = new Window
        {
            Title = "AI Arena User Guide",
            Owner = owner,
            Width = 1040,
            Height = 720,
            MinWidth = 780,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            Background = ResourceBrush(owner, "AppBackgroundBrush"),
            Foreground = ResourceBrush(owner, "TextBrush")
        };
        CopyThemeResources(owner, dialog);
        dialog.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        _window = dialog;
        dialog.Closed += (_, _) => _window = null;

        var chrome = new Border
        {
            Background = ResourceBrush(dialog, "AppBackgroundBrush"),
            BorderBrush = ResourceBrush(dialog, "ControlBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8)
        };
        chrome.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");
        var chromeGrid = new Grid();
        chrome.Child = chromeGrid;
        var root = new DockPanel();
        chromeGrid.Children.Add(root);

        var header = CreateHeader(dialog);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = CreateFooter(dialog, guidePath);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = CreateBody(dialog, sections, guideText);
        root.Children.Add(body);

        dialog.Content = chrome;
        dialog.Show();
        return true;
    }

    public void RefreshTheme(Window owner)
    {
        if (_window is not { IsVisible: true } dialog)
        {
            return;
        }

        CopyThemeResources(owner, dialog);
        if (dialog.Tag is UserGuideWindowContext context)
        {
            var section = context.SectionList.SelectedItem as UserGuideSection
                ?? context.Sections.FirstOrDefault()
                ?? new UserGuideSection("Guide", string.Empty);
            context.GuideViewer.Document = BuildGuideDocument(dialog, section);
        }
    }

    private static Grid CreateHeader(Window dialog)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, args) => DialogChrome.DragMoveIfPossible(dialog, args);

        var heading = new StackPanel();
        var titleText = new TextBlock
        {
            Text = "AI Arena User Guide",
            Foreground = ResourceBrush(dialog, "TextBrush"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        heading.Children.Add(titleText);

        var subtitleText = new TextBlock
        {
            Text = "Readable reference for setup, controls, diagnostics, and troubleshooting.",
            Foreground = ResourceBrush(dialog, "MutedTextBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0)
        };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        heading.Children.Add(subtitleText);
        header.Children.Add(heading);

        var headerCloseButton = CreateGuideCloseButton(dialog);
        headerCloseButton.Click += (_, _) => dialog.Close();
        Grid.SetColumn(headerCloseButton, 1);
        header.Children.Add(headerCloseButton);
        return header;
    }

    private static Grid CreateFooter(Window dialog, string guidePath)
    {
        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var openFileButton = CreateGuideButton(dialog, "OPEN FILE");
        openFileButton.Margin = new Thickness(0);
        openFileButton.Click += (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = guidePath,
            UseShellExecute = true
        });
        footer.Children.Add(openFileButton);

        var closeButton = CreateGuideButton(dialog, "CLOSE");
        closeButton.Margin = new Thickness(0);
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetColumn(closeButton, 2);
        footer.Children.Add(closeButton);

        var resizeGrip = new ResizeGrip
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 0),
            Opacity = 0.55
        };
        Grid.SetColumn(resizeGrip, 3);
        footer.Children.Add(resizeGrip);
        return footer;
    }

    private static Grid CreateBody(Window dialog, IReadOnlyList<UserGuideSection> sections, string guideText)
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleTemplate = new DataTemplate(typeof(UserGuideSection));
        var titleFactory = new FrameworkElementFactory(typeof(TextBlock));
        titleFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(UserGuideSection.Title)));
        titleFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        titleFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        titleFactory.SetValue(TextBlock.MarginProperty, new Thickness(3, 2, 3, 2));
        titleTemplate.VisualTree = titleFactory;

        var sectionList = new ListBox
        {
            ItemsSource = sections,
            ItemTemplate = titleTemplate,
            Background = ResourceBrush(dialog, "InputBrush"),
            Foreground = ResourceBrush(dialog, "TextBrush"),
            BorderBrush = ResourceBrush(dialog, "ControlBorderBrush"),
            Padding = new Thickness(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        sectionList.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        sectionList.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        sectionList.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(sectionList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(sectionList, ScrollBarVisibility.Auto);
        body.Children.Add(sectionList);

        var guideViewer = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = ResourceBrush(dialog, "InputBrush"),
            Padding = new Thickness(12),
            Document = BuildGuideDocument(dialog, sections.FirstOrDefault() ?? new UserGuideSection("Guide", guideText))
        };
        guideViewer.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        sectionList.SelectionChanged += (_, _) =>
        {
            if (sectionList.SelectedItem is UserGuideSection section)
            {
                guideViewer.Document = BuildGuideDocument(dialog, section);
            }
        };
        Grid.SetColumn(guideViewer, 2);
        body.Children.Add(guideViewer);

        dialog.Tag = new UserGuideWindowContext(sections, sectionList, guideViewer);
        return body;
    }

    private static Button CreateGuideButton(FrameworkElement resources, string text)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 90,
            MinHeight = 32,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ResourceBrush(resources, "InputBrush"),
            BorderBrush = ResourceBrush(resources, "ControlBorderBrush"),
            Foreground = ResourceBrush(resources, "TextBrush"),
            FontWeight = FontWeights.SemiBold
        };
        button.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, "ControlBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return button;
    }

    private static Button CreateGuideCloseButton(FrameworkElement resources)
    {
        var button = new Button
        {
            Content = "X",
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = ResourceBrush(resources, "InputBrush"),
            BorderBrush = ResourceBrush(resources, "DangerBorderBrush"),
            Foreground = ResourceBrush(resources, "DangerTextBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Close guide"
        };
        button.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        button.SetResourceReference(Control.BorderBrushProperty, "DangerBorderBrush");
        button.SetResourceReference(Control.ForegroundProperty, "DangerTextBrush");
        return button;
    }

    private static void CopyThemeResources(Window owner, FrameworkElement target)
    {
        foreach (var key in ThemeResourceKeys)
        {
            if (owner.Resources.Contains(key))
            {
                target.Resources[key] = owner.Resources[key];
            }
        }

        foreach (var key in ThemeStyleKeys)
        {
            if (owner.Resources.Contains(key))
            {
                target.Resources[key] = owner.Resources[key];
            }
        }
    }

    private static FlowDocument BuildGuideDocument(FrameworkElement resources, UserGuideSection section)
    {
        var document = new FlowDocument
        {
            Background = ResourceBrush(resources, "InputBrush"),
            Foreground = ResourceBrush(resources, "TextBrush"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            LineHeight = 19
        };

        foreach (var rawLine in section.Text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run(line.TrimStart('#', ' ').Trim()))
                {
                    Foreground = ResourceBrush(resources, "PrimaryBorderBrush"),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 12)
                });
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run(line.TrimStart('#', ' ').Trim()))
                {
                    Foreground = ResourceBrush(resources, "TextBrush"),
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 6)
                });
                continue;
            }

            var isBullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            document.Blocks.Add(new Paragraph(new Run(isBullet ? $"\u2022 {line[2..].Trim()}" : line.Trim()))
            {
                Foreground = ResourceBrush(resources, "TextBrush"),
                Margin = new Thickness(isBullet ? 12 : 0, 0, 0, 6)
            });
        }

        return document;
    }

    private static IReadOnlyList<UserGuideSection> BuildUserGuideSections(string guideText)
    {
        var sections = new List<UserGuideSection>();
        using var reader = new StringReader(guideText);
        var title = "Overview";
        var content = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (content.Count > 0)
                {
                    sections.Add(new UserGuideSection(title, string.Join(Environment.NewLine, content).Trim()));
                    content.Clear();
                }

                title = line.TrimStart('#', ' ').Trim();
                content.Add(line);
                continue;
            }

            content.Add(line);
        }

        if (content.Count > 0)
        {
            sections.Add(new UserGuideSection(title, string.Join(Environment.NewLine, content).Trim()));
        }

        return sections.Count > 0 ? sections : [new UserGuideSection("Guide", guideText)];
    }

    private static string? ResolveUserGuidePath()
    {
        var installedGuide = Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.md");
        if (File.Exists(installedGuide))
        {
            return installedGuide;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var sourceGuide = Path.Combine(current.FullName, "docs", "USER_GUIDE.md");
            if (File.Exists(sourceGuide))
            {
                return sourceGuide;
            }

            current = current.Parent;
        }

        return null;
    }

    private static Brush ResourceBrush(FrameworkElement resources, string key)
    {
        return resources.TryFindResource(key) as Brush ?? Brushes.White;
    }

    private sealed record UserGuideSection(string Title, string Text);

    private sealed record UserGuideWindowContext(
        IReadOnlyList<UserGuideSection> Sections,
        ListBox SectionList,
        FlowDocumentScrollViewer GuideViewer);
}
