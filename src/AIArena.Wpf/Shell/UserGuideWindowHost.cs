using AIArena.Wpf.Services;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOPath = System.IO.Path;

namespace AIArena.Wpf;

internal sealed class UserGuideWindowHost
{
    private const string GuideWindowTitle = "AI Arena: Adversarial LLM Lab - User Guide";
    private const string AppIconResourceKey = "assets/ai-arena-icon.ico";
    private const string GuideHeaderIconResourceKey = "assets/ai-arena-guide-icon.png";
    private const string SearchPlaceholderText = "Search the guide...";

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
        typeof(TextBox),
        typeof(ComboBox),
        typeof(CheckBox)
    ];

    private Window? _window;

    public void Close()
    {
        if (_window is { IsVisible: true } window)
        {
            window.Close();
        }
    }

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
            Title = GuideWindowTitle,
            Owner = owner,
            Width = 1180,
            Height = 780,
            MinWidth = 920,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            Background = BrushFrom("#08111F"),
            Foreground = ResourceBrush(owner, "TextBrush"),
            Icon = CreateAppIconImageSource()
        };
        dialog.SourceInitialized += (_, _) => WindowChromeService.ApplySubtleNativeChromeColor(dialog);
        CopyThemeResources(owner, dialog);
        dialog.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        _window = dialog;
        dialog.Closed += (_, _) => _window = null;

        var chrome = new Border
        {
            Background = CreateWindowBackgroundBrush(),
            BorderBrush = BrushFrom("#243C5E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 4, 14, 14),
            SnapsToDevicePixels = true
        };
        var root = new DockPanel { LastChildFill = true };
        chrome.Child = root;

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
                ?? new UserGuideSection("Guide", string.Empty, "\uE82D");
            context.ContentTitle.Text = DisplayTitleForSection(section);
            context.GuideViewer.Document = BuildGuideDocument(dialog, section);
        }
    }

    private static Grid CreateHeader(Window dialog)
    {
        var header = new Grid
        {
            Background = Brushes.Transparent,
            MinHeight = 72,
            Margin = new Thickness(0, 0, 0, 12)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, args) => DialogChrome.DragMoveIfPossible(dialog, args);

        var logo = CreateGuideLogo();
        logo.Margin = new Thickness(10, 12, 16, 8);
        Grid.SetColumn(logo, 0);
        header.Children.Add(logo);

        var heading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(heading, 1);

        var titleText = new TextBlock
        {
            Text = GuideWindowTitle,
            Foreground = ResourceBrush(dialog, "TextBrush"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        heading.Children.Add(titleText);

        var subtitleText = new TextBlock
        {
            Text = "Readable reference for setup, controls, diagnostics, and troubleshooting.",
            Foreground = ResourceBrush(dialog, "MutedTextBrush"),
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        heading.Children.Add(subtitleText);
        header.Children.Add(heading);

        var windowControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 20, 8, 0)
        };
        Grid.SetColumn(windowControls, 2);

        var closeButton = CreateWindowControlButton(dialog, "\uE8BB", "Close guide", closeButton: true);
        closeButton.Click += (_, _) => dialog.Close();
        windowControls.Children.Add(closeButton);

        header.Children.Add(windowControls);
        return header;
    }

    private static Grid CreateFooter(Window dialog, string guidePath)
    {
        var footer = new Grid { Margin = new Thickness(14, 12, 14, 2) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var openFileButton = CreateGuideButton(dialog, "OPEN FILE", "\uE8E5");
        openFileButton.Click += (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = guidePath,
            UseShellExecute = true
        });
        footer.Children.Add(openFileButton);

        var closeButton = CreateGuideButton(dialog, "CLOSE", null);
        closeButton.Click += (_, _) => dialog.Close();
        Grid.SetColumn(closeButton, 2);
        footer.Children.Add(closeButton);
        return footer;
    }

    private static Border CreateBody(Window dialog, IReadOnlyList<UserGuideSection> sections, string guideText)
    {
        var frame = new Border
        {
            Background = CreatePanelBackgroundBrush(),
            BorderBrush = BrushFrom("#22395A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            SnapsToDevicePixels = true
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        frame.Child = body;

        var guideViewer = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            IsToolBarVisible = false,
            Document = BuildGuideDocument(dialog, sections.FirstOrDefault() ?? new UserGuideSection("Guide", guideText, "\uE82D"))
        };

        var contentTitle = new TextBlock
        {
            Text = DisplayTitleForSection(sections.FirstOrDefault() ?? new UserGuideSection("Guide", guideText, "\uE82D")),
            Foreground = BrushFrom("#37D8FF"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        var sectionList = CreateSectionList(dialog, sections);
        var searchBox = CreateSearchBox(dialog, sectionList, sections, guideViewer, contentTitle, out var searchPlaceholder);

        sectionList.SelectionChanged += (_, _) =>
        {
            if (sectionList.SelectedItem is UserGuideSection section)
            {
                contentTitle.Text = DisplayTitleForSection(section);
                guideViewer.Document = BuildGuideDocument(dialog, section);
            }
        };
        sectionList.SelectedIndex = 0;

        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                searchBox.Focus();
                searchBox.SelectAll();
                args.Handled = true;
            }
        };

        body.Children.Add(CreateNavigationPanel(searchBox, searchPlaceholder, sectionList));
        var contentPanel = CreateContentPanel(contentTitle, guideViewer);
        Grid.SetColumn(contentPanel, 2);
        body.Children.Add(contentPanel);

        dialog.Tag = new UserGuideWindowContext(sections, sectionList, guideViewer, contentTitle);
        return frame;
    }

    private static ListBox CreateSectionList(Window dialog, IReadOnlyList<UserGuideSection> sections)
    {
        var itemForegroundBinding = new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1)
        };

        var itemTemplate = new DataTemplate(typeof(UserGuideSection));
        var itemDock = new FrameworkElementFactory(typeof(DockPanel));
        itemDock.SetValue(FrameworkElement.MinHeightProperty, 34.0);
        itemDock.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 2, 0));
        itemDock.SetValue(DockPanel.LastChildFillProperty, true);

        var arrowFactory = new FrameworkElementFactory(typeof(TextBlock));
        arrowFactory.SetValue(TextBlock.TextProperty, "\uE76C");
        arrowFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        arrowFactory.SetValue(TextBlock.FontSizeProperty, 11.0);
        arrowFactory.SetValue(FrameworkElement.WidthProperty, 18.0);
        arrowFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrowFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        arrowFactory.SetBinding(TextBlock.ForegroundProperty, itemForegroundBinding);
        arrowFactory.SetValue(DockPanel.DockProperty, Dock.Right);
        itemDock.AppendChild(arrowFactory);

        var iconFactory = new FrameworkElementFactory(typeof(TextBlock));
        iconFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(UserGuideSection.IconGlyph)));
        iconFactory.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        iconFactory.SetValue(TextBlock.FontSizeProperty, 17.0);
        iconFactory.SetValue(FrameworkElement.WidthProperty, 36.0);
        iconFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 8, 0));
        iconFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        iconFactory.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        iconFactory.SetBinding(TextBlock.ForegroundProperty, itemForegroundBinding);
        iconFactory.SetValue(DockPanel.DockProperty, Dock.Left);
        itemDock.AppendChild(iconFactory);

        var titleFactory = new FrameworkElementFactory(typeof(TextBlock));
        titleFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(UserGuideSection.Title)));
        titleFactory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        titleFactory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        titleFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        titleFactory.SetValue(TextBlock.FontSizeProperty, 13.0);
        titleFactory.SetBinding(TextBlock.ForegroundProperty, itemForegroundBinding);
        itemDock.AppendChild(titleFactory);
        itemTemplate.VisualTree = itemDock;

        var sectionList = new ListBox
        {
            ItemsSource = sections,
            ItemTemplate = itemTemplate,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = ResourceBrush(dialog, "TextBrush"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        sectionList.ItemContainerStyle = CreateGuideListItemStyle(dialog);
        sectionList.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(sectionList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(sectionList, ScrollBarVisibility.Auto);
        return sectionList;
    }

    private static Border CreateNavigationPanel(TextBox searchBox, TextBlock searchPlaceholder, ListBox sectionList)
    {
        var panel = new Border
        {
            Background = CreateNavBackgroundBrush(),
            BorderBrush = BrushFrom("#243E61"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = grid;

        grid.Children.Add(CreateSearchShell(searchBox, searchPlaceholder));

        sectionList.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(sectionList, 1);
        grid.Children.Add(sectionList);

        return panel;
    }

    private static TextBox CreateSearchBox(
        Window dialog,
        ListBox sectionList,
        IReadOnlyList<UserGuideSection> sections,
        FlowDocumentScrollViewer guideViewer,
        TextBlock contentTitle,
        out TextBlock placeholder)
    {
        placeholder = new TextBlock
        {
            Text = SearchPlaceholderText,
            Foreground = BrushFrom("#7F8FA8"),
            FontSize = 13,
            Margin = new Thickness(34, 0, 84, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var searchBox = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ResourceBrush(dialog, "TextBrush"),
            CaretBrush = BrushFrom("#37D8FF"),
            FontSize = 13,
            Padding = new Thickness(34, 0, 84, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var placeholderText = placeholder;
        searchBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        searchBox.TextChanged += (_, _) =>
        {
            placeholderText.Visibility = string.IsNullOrWhiteSpace(searchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyGuideFilter(dialog, sections, searchBox.Text, sectionList, guideViewer, contentTitle);
        };

        return searchBox;
    }

    private static Border CreateSearchShell(TextBox searchBox, TextBlock placeholder)
    {
        var shell = new Border
        {
            Height = 42,
            Background = BrushFrom("#0C1A2D"),
            BorderBrush = BrushFrom("#213A5D"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            SnapsToDevicePixels = true
        };

        var searchGrid = new Grid();
        shell.Child = searchGrid;

        searchGrid.Children.Add(searchBox);
        searchGrid.Children.Add(placeholder);

        var searchIcon = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = BrushFrom("#91A4BF"),
            FontSize = 16,
            Width = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        searchGrid.Children.Add(searchIcon);

        var shortcut = new Border
        {
            Background = BrushFrom("#14243A"),
            BorderBrush = BrushFrom("#253E60"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Ctrl+F",
                Foreground = BrushFrom("#8E9CB2"),
                FontSize = 11
            }
        };
        searchGrid.Children.Add(shortcut);

        return shell;
    }

    private static Border CreateContentPanel(TextBlock contentTitle, FlowDocumentScrollViewer guideViewer)
    {
        var panel = new Border
        {
            Background = CreateArticleBackgroundBrush(),
            BorderBrush = BrushFrom("#284360"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = grid;

        var header = new Border
        {
            Background = CreateArticleHeaderBrush(),
            Padding = new Thickness(24, 18, 24, 18),
            CornerRadius = new CornerRadius(9, 9, 0, 0)
        };
        var headerStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Child = headerStack;

        var iconTile = new Border
        {
            Width = 46,
            Height = 46,
            Background = CreateIconTileBrush(),
            BorderBrush = BrushFrom("#2A79B9"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 18, 0),
            Child = new TextBlock
            {
                Text = "\uE82D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = BrushFrom("#A8F3FF"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        headerStack.Children.Add(iconTile);
        headerStack.Children.Add(contentTitle);
        grid.Children.Add(header);

        var divider = new Border
        {
            Background = CreateAccentDividerBrush(),
            Height = 2
        };
        Grid.SetRow(divider, 1);
        grid.Children.Add(divider);

        guideViewer.Margin = new Thickness(0);
        Grid.SetRow(guideViewer, 2);
        grid.Children.Add(guideViewer);

        return panel;
    }

    private static Button CreateGuideButton(FrameworkElement resources, string text, string? iconGlyph)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!string.IsNullOrWhiteSpace(iconGlyph))
        {
            content.Children.Add(new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = BrushFrom("#F0C36A"),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(label);

        var button = new Button
        {
            Content = content,
            MinWidth = text.Equals("CLOSE", StringComparison.OrdinalIgnoreCase) ? 146 : 164,
            MinHeight = 46,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(0),
            Background = BrushFrom("#12223B"),
            BorderBrush = BrushFrom("#2F80ED"),
            Foreground = ResourceBrush(resources, "TextBrush"),
            FontWeight = FontWeights.SemiBold,
            Style = CreateRoundedButtonStyle(
                new CornerRadius(6),
                BrushFrom("#12223B"),
                BrushFrom("#193253"),
                BrushFrom("#0E1C31"),
                BrushFrom("#2F80ED"))
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        return button;
    }

    private static Button CreateWindowControlButton(FrameworkElement resources, string glyph, string tooltip, bool closeButton = false)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = closeButton ? 12 : 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Width = closeButton ? 36 : 34,
            Height = 36,
            MinWidth = closeButton ? 36 : 34,
            MinHeight = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(5, 0, 0, 0),
            Background = closeButton ? BrushFrom("#18223B") : Brushes.Transparent,
            BorderBrush = closeButton ? BrushFrom("#B04A66") : Brushes.Transparent,
            Foreground = closeButton ? ResourceBrush(resources, "DangerTextBrush") : ResourceBrush(resources, "MutedTextBrush"),
            ToolTip = tooltip,
            Style = CreateRoundedButtonStyle(
                new CornerRadius(closeButton ? 7 : 5),
                closeButton ? BrushFrom("#18223B") : Brushes.Transparent,
                closeButton ? BrushFrom("#2B2744") : BrushFrom("#15253B"),
                closeButton ? BrushFrom("#3A1C34") : BrushFrom("#0D1A2C"),
                closeButton ? BrushFrom("#B04A66") : Brushes.Transparent)
        };
        button.SetResourceReference(Control.ForegroundProperty, closeButton ? "DangerTextBrush" : "MutedTextBrush");
        return button;
    }

    private static FrameworkElement CreateGuideLogo()
    {
        var tile = new Border
        {
            Width = 46,
            Height = 46,
            Background = BrushFrom("#0B1528"),
            BorderBrush = BrushFrom("#203A5F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            Child = new Image
            {
                Source = CreateGuideHeaderIconImageSource(),
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true
            }
        };

        return tile;
    }

    internal static ImageSource CreateAppIconImageSource()
    {
        return CreateResourceImageSource(AppIconResourceKey, new Uri("/Assets/ai-arena-icon.ico", UriKind.Relative));
    }

    internal static ImageSource CreateGuideHeaderIconImageSource()
    {
        return CreateResourceImageSource(GuideHeaderIconResourceKey, new Uri("/Assets/ai-arena-guide-icon.png", UriKind.Relative));
    }

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

        return BitmapFrame.Create(fallbackUri);
    }

    private static Style CreateGuideListItemStyle(FrameworkElement resources)
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ResourceBrush(resources, "TextBrush")));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));

        var template = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemChrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, BrushFrom("#132A45")));
        hoverTrigger.Setters.Add(new Setter(Control.ForegroundProperty, BrushFrom("#E8F2FF")));
        template.Triggers.Add(hoverTrigger);

        var selectedTrigger = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, CreateSelectionBrush()));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(selectedTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateRoundedButtonStyle(
        CornerRadius cornerRadius,
        Brush normalBackground,
        Brush hoverBackground,
        Brush pressedBackground,
        Brush borderBrush)
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, normalBackground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ButtonChrome";
        border.SetValue(Border.CornerRadiusProperty, cornerRadius);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground) { TargetName = "ButtonChrome" });
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, pressedBackground) { TargetName = "ButtonChrome" });
        template.Triggers.Add(pressedTrigger);

        var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
        template.Triggers.Add(disabledTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static void ApplyGuideFilter(
        FrameworkElement resources,
        IReadOnlyList<UserGuideSection> sections,
        string query,
        ListBox sectionList,
        FlowDocumentScrollViewer guideViewer,
        TextBlock contentTitle)
    {
        var filtered = FilterSections(sections, query).ToArray();
        if (filtered.Length == 0)
        {
            filtered =
            [
                new UserGuideSection(
                    "No results",
                    string.IsNullOrWhiteSpace(query)
                        ? "No guide sections are available."
                        : $"No guide sections match \"{query.Trim()}\".",
                    "\uE721")
            ];
        }

        sectionList.ItemsSource = filtered;
        sectionList.SelectedIndex = 0;
        var section = filtered[0];
        contentTitle.Text = DisplayTitleForSection(section);
        guideViewer.Document = BuildGuideDocument(resources, section);
    }

    private static IEnumerable<UserGuideSection> FilterSections(IReadOnlyList<UserGuideSection> sections, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return sections;
        }

        var normalized = query.Trim();
        return sections.Where(section =>
            section.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || section.Text.Contains(normalized, StringComparison.OrdinalIgnoreCase));
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
            Background = Brushes.Transparent,
            Foreground = ResourceBrush(resources, "TextBrush"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            PagePadding = new Thickness(36, 34, 52, 34),
            LineHeight = 28
        };

        var displayTitle = DisplayTitleForSection(section);
        var skippedTitle = false;
        var insertedLeadDivider = false;
        foreach (var rawLine in section.Text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = line.TrimStart('#', ' ').Trim();
                if (!skippedTitle && heading.Equals(displayTitle, StringComparison.OrdinalIgnoreCase))
                {
                    skippedTitle = true;
                    continue;
                }

                document.Blocks.Add(new Paragraph(new Run(heading))
                {
                    Foreground = BrushFrom("#37D8FF"),
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 14)
                });
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                document.Blocks.Add(new Paragraph(new Run(line.TrimStart('#', ' ').Trim()))
                {
                    Foreground = ResourceBrush(resources, "TextBrush"),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 18, 0, 8)
                });
                continue;
            }

            var isBullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            document.Blocks.Add(CreateGuideParagraph(resources, isBullet ? line[2..].Trim() : line.Trim(), isBullet));

            if (!insertedLeadDivider && !isBullet)
            {
                document.Blocks.Add(CreateGuideDivider());
                insertedLeadDivider = true;
            }
        }

        return document;
    }

    private static Paragraph CreateGuideParagraph(FrameworkElement resources, string text, bool isBullet)
    {
        var paragraph = new Paragraph
        {
            Foreground = ResourceBrush(resources, "TextBrush"),
            Margin = new Thickness(isBullet ? 18 : 0, 0, 0, isBullet ? 6 : 12),
            TextAlignment = TextAlignment.Left
        };

        if (isBullet)
        {
            paragraph.Inlines.Add(new Run($"\u2022 {text}"));
            return paragraph;
        }

        if (text.StartsWith("AI Arena ", StringComparison.Ordinal))
        {
            paragraph.Inlines.Add(new Run("AI Arena")
            {
                Foreground = BrushFrom("#37D8FF"),
                FontWeight = FontWeights.SemiBold
            });
            paragraph.Inlines.Add(new Run(text["AI Arena".Length..]));
            return paragraph;
        }

        paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }

    private static BlockUIContainer CreateGuideDivider()
    {
        var grid = new Grid
        {
            Height = 34,
            Margin = new Thickness(0, 12, 0, 16)
        };

        grid.Children.Add(new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(
                [
                    new GradientStop(ColorFrom("#00162A44"), 0),
                    new GradientStop(ColorFrom("#334A78A8"), 0.26),
                    new GradientStop(ColorFrom("#9955B6FF"), 0.5),
                    new GradientStop(ColorFrom("#334A78A8"), 0.74),
                    new GradientStop(ColorFrom("#00162A44"), 1)
                ],
                new Point(0, 0.5),
                new Point(1, 0.5))
        });

        grid.Children.Add(new Border
        {
            Width = 74,
            Height = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(
                ColorFrom("#0037D8FF"),
                ColorFrom("#FF5AA6FF"),
                new Point(0, 0.5),
                new Point(1, 0.5))
        });

        return new BlockUIContainer(grid);
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
                    sections.Add(CreateSection(title, content));
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
            sections.Add(CreateSection(title, content));
        }

        return sections.Count > 0 ? sections : [new UserGuideSection("Guide", guideText, "\uE82D")];
    }

    private static UserGuideSection CreateSection(string title, List<string> content)
    {
        return new UserGuideSection(title, string.Join(Environment.NewLine, content).Trim(), GuideIconForTitle(title));
    }

    private static string DisplayTitleForSection(UserGuideSection section)
    {
        using var reader = new StringReader(section.Text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal))
            {
                return line.TrimStart('#', ' ').Trim();
            }
        }

        return section.Title;
    }

    private static string GuideIconForTitle(string title)
    {
        if (title.Contains("Quick", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE7C1";
        }

        if (title.Contains("Concept", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8D2";
        }

        if (title.Contains("Layout", StringComparison.OrdinalIgnoreCase))
        {
            return "\uECA5";
        }

        if (title.Contains("Rail", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE9D2";
        }

        if (title.Contains("Transcript", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8A5";
        }

        if (title.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE9D9";
        }

        if (title.Contains("Control", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8B0";
        }

        if (title.Contains("Performance", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE9D2";
        }

        if (title.Contains("Operator", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE13D";
        }

        if (title.Contains("Custom", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE7C3";
        }

        if (title.Contains("Session", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Template", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8B7";
        }

        if (title.Contains("Setting", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE713";
        }

        if (title.Contains("Licens", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE8D7";
        }

        if (title.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Troubleshooting", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE90F";
        }

        if (title.Contains("Tip", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE734";
        }

        return "\uE82D";
    }

    private static string? ResolveUserGuidePath()
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

    private static Brush CreateWindowBackgroundBrush()
    {
        return new LinearGradientBrush(
            ColorFrom("#08111F"),
            ColorFrom("#0A172A"),
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Brush CreatePanelBackgroundBrush()
    {
        return new LinearGradientBrush(
            ColorFrom("#0A1628"),
            ColorFrom("#0C1A2D"),
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Brush CreateNavBackgroundBrush()
    {
        return new LinearGradientBrush(
            ColorFrom("#0B182B"),
            ColorFrom("#0A1424"),
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Brush CreateArticleBackgroundBrush()
    {
        return new LinearGradientBrush(
            [
                new GradientStop(ColorFrom("#0C1B30"), 0),
                new GradientStop(ColorFrom("#0A1628"), 0.52),
                new GradientStop(ColorFrom("#0B1227"), 1)
            ],
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Brush CreateArticleHeaderBrush()
    {
        return new LinearGradientBrush(
            [
                new GradientStop(ColorFrom("#143A54"), 0),
                new GradientStop(ColorFrom("#101F42"), 0.56),
                new GradientStop(ColorFrom("#24143F"), 1)
            ],
            new Point(0, 0.5),
            new Point(1, 0.5));
    }

    private static Brush CreateAccentDividerBrush()
    {
        return new LinearGradientBrush(
            [
                new GradientStop(ColorFrom("#FF28E1FF"), 0),
                new GradientStop(ColorFrom("#FF5C8CFF"), 0.65),
                new GradientStop(ColorFrom("#FF7E47DE"), 1)
            ],
            new Point(0, 0.5),
            new Point(1, 0.5));
    }

    private static Brush CreateIconTileBrush()
    {
        return new LinearGradientBrush(
            ColorFrom("#123D69"),
            ColorFrom("#1B346D"),
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Brush CreateSelectionBrush()
    {
        return new LinearGradientBrush(
            [
                new GradientStop(ColorFrom("#FF2D83F4"), 0),
                new GradientStop(ColorFrom("#FF6142D9"), 1)
            ],
            new Point(0, 0.5),
            new Point(1, 0.5));
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        return new SolidColorBrush(ColorFrom(hex));
    }

    private static Color ColorFrom(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex)!;
    }

    private static Brush ResourceBrush(FrameworkElement resources, string key)
    {
        return resources.TryFindResource(key) as Brush ?? Brushes.White;
    }

    private sealed record UserGuideSection(string Title, string Text, string IconGlyph);

    private sealed record UserGuideWindowContext(
        IReadOnlyList<UserGuideSection> Sections,
        ListBox SectionList,
        FlowDocumentScrollViewer GuideViewer,
        TextBlock ContentTitle);
}
