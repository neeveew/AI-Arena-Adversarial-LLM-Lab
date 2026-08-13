using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIArena.Wpf.Help;

internal sealed partial class HelpMarkdownRenderer
{
    private const int MaximumTableColumns = 8;
    private const int MaximumTableRows = 100;
    private readonly HelpCatalog catalog;
    private readonly TryResolveHelpLink resolveLink;
    private readonly string? contentRoot;

    internal delegate bool TryResolveHelpLink(string? rawLink, out HelpLinkTarget target);

    public HelpMarkdownRenderer(HelpCatalog catalog, TryResolveHelpLink resolveLink, string? contentRoot = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.resolveLink = resolveLink ?? throw new ArgumentNullException(nameof(resolveLink));
        this.contentRoot = string.IsNullOrWhiteSpace(contentRoot) ? null : Path.GetFullPath(contentRoot);
    }

    public FlowDocument Render(HelpArticle article, FrameworkElement resources)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(resources);

        var document = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = Brush(resources, "TextBrush", Colors.White),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            LineHeight = 23,
            PagePadding = new Thickness(30, 26, 42, 38)
        };

        RenderBlocks(document.Blocks, article.Markdown, resources);
        AppendRelatedArticles(document.Blocks, article, resources);
        return document;
    }

    internal static IReadOnlyList<HelpHeading> ExtractHeadings(string markdown)
    {
        var headings = new List<HelpHeading>();
        var usedAnchors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        foreach (var rawLine in NormalizeNewlines(markdown).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var match = HeadingRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var title = PlainInlineText(match.Groups["title"].Value.Trim().TrimEnd('#').Trim());
            var baseAnchor = HelpDeepLink.NormalizeAnchor(title);
            if (baseAnchor.Length == 0)
            {
                continue;
            }

            usedAnchors.TryGetValue(baseAnchor, out var priorCount);
            usedAnchors[baseAnchor] = priorCount + 1;
            var anchor = priorCount == 0 ? baseAnchor : $"{baseAnchor}-{priorCount + 1}";
            headings.Add(new HelpHeading(match.Groups["marks"].Length, title, anchor));
        }

        return headings;
    }

    private void RenderBlocks(BlockCollection blocks, string markdown, FrameworkElement resources)
    {
        var lines = NormalizeNewlines(markdown).Split('\n');
        var headings = ExtractHeadings(markdown);
        var headingIndex = 0;
        for (var index = 0; index < lines.Length;)
        {
            var line = lines[index].TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.AppendLine();
                    code.Append(lines[index]);
                    index++;
                }

                if (index < lines.Length) index++;
                blocks.Add(CreateCodeBlock(code.ToString(), language, resources));
                continue;
            }

            if (line.StartsWith(":::details", StringComparison.OrdinalIgnoreCase))
            {
                var detailsTitle = line[":::details".Length..].Trim();
                index++;
                var detailsMarkdown = new StringBuilder();
                while (index < lines.Length && !lines[index].Trim().Equals(":::", StringComparison.Ordinal))
                {
                    if (detailsMarkdown.Length > 0) detailsMarkdown.AppendLine();
                    detailsMarkdown.Append(lines[index]);
                    index++;
                }

                if (index < lines.Length) index++;
                blocks.Add(CreateDetails(
                    string.IsNullOrWhiteSpace(detailsTitle) ? "Advanced details" : PlainInlineText(detailsTitle),
                    detailsMarkdown.ToString(),
                    resources));
                continue;
            }

            var imageMatch = ImageRegex().Match(line.Trim());
            if (imageMatch.Success)
            {
                blocks.Add(CreateImageBlock(imageMatch.Groups["alt"].Value, imageMatch.Groups["path"].Value, resources));
                index++;
                continue;
            }

            var headingMatch = HeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                var heading = headingIndex < headings.Count
                    ? headings[headingIndex++]
                    : new HelpHeading(headingMatch.Groups["marks"].Length, PlainInlineText(headingMatch.Groups["title"].Value), string.Empty);
                blocks.Add(CreateHeading(heading, resources));
                index++;
                continue;
            }

            if (IsDivider(line))
            {
                blocks.Add(CreateDivider(resources));
                index++;
                continue;
            }

            if (IsTableStart(lines, index))
            {
                blocks.Add(CreateTable(lines, ref index, resources));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    if (quote.Length > 0) quote.Append(' ');
                    quote.Append(lines[index].TrimStart().TrimStart('>').TrimStart());
                    index++;
                }

                blocks.Add(CreateCallout(quote.ToString(), resources));
                continue;
            }

            if (TryGetListItem(line, out var ordered, out _))
            {
                blocks.Add(CreateList(lines, ref index, ordered, resources));
                continue;
            }

            var paragraphText = new StringBuilder(line.Trim());
            index++;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !IsStructuralStart(lines, index))
            {
                paragraphText.Append(' ').Append(lines[index].Trim());
                index++;
            }

            blocks.Add(CreateParagraph(paragraphText.ToString(), resources));
        }
    }

    private Paragraph CreateHeading(HelpHeading heading, FrameworkElement resources)
    {
        var fontSize = heading.Level switch { 1 => 25, 2 => 20, 3 => 17, _ => 15 };
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = heading.Level <= 2
                ? Brush(resources, "PrimaryBorderBrush", Color.FromRgb(77, 212, 239))
                : Brush(resources, "TextBrush", Colors.White),
            Margin = new Thickness(0, heading.Level <= 2 ? 24 : 18, 0, 8),
            KeepWithNext = true,
            Tag = heading.Anchor
        };
        AddInlines(paragraph.Inlines, heading.Title, resources);
        AutomationProperties.SetName(paragraph, heading.Title);
        return paragraph;
    }

    private Paragraph CreateParagraph(string text, FrameworkElement resources)
    {
        var paragraph = new Paragraph
        {
            Foreground = Brush(resources, "TextBrush", Colors.White),
            Margin = new Thickness(0, 0, 0, 12)
        };
        AddInlines(paragraph.Inlines, text, resources);
        return paragraph;
    }

    private Block CreateList(string[] lines, ref int index, bool ordered, FrameworkElement resources)
    {
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            MarkerOffset = 14,
            Margin = new Thickness(18, 0, 0, 12),
            Padding = new Thickness(8, 0, 0, 0)
        };

        while (index < lines.Length
            && TryGetListItem(lines[index], out var currentOrdered, out var itemText)
            && currentOrdered == ordered)
        {
            var paragraph = CreateParagraph(itemText, resources);
            paragraph.Margin = new Thickness(0, 0, 0, 5);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        return list;
    }

    private Block CreateTable(string[] lines, ref int index, FrameworkElement resources)
    {
        var headers = SplitTableRow(lines[index]);
        var columnCount = Math.Min(headers.Count, MaximumTableColumns);
        index += 2;
        var rows = new List<IReadOnlyList<string>>();
        while (index < lines.Length && rows.Count < MaximumTableRows && LooksLikeTableRow(lines[index]))
        {
            rows.Add(SplitTableRow(lines[index]));
            index++;
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 18),
            BorderBrush = Brush(resources, "ControlBorderBrush", Color.FromRgb(63, 86, 110)),
            BorderThickness = new Thickness(1)
        };
        for (var column = 0; column < columnCount; column++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        group.Rows.Add(CreateTableRow(headers, columnCount, true, resources));
        foreach (var row in rows)
        {
            group.Rows.Add(CreateTableRow(row, columnCount, false, resources));
        }

        table.RowGroups.Add(group);
        return table;
    }

    private TableRow CreateTableRow(IReadOnlyList<string> cells, int columnCount, bool isHeader, FrameworkElement resources)
    {
        var row = new TableRow
        {
            Background = isHeader
                ? Brush(resources, "TranscriptHeaderBrush", Color.FromRgb(25, 45, 61))
                : Brushes.Transparent,
            FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal
        };
        for (var column = 0; column < columnCount; column++)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            AddInlines(paragraph.Inlines, column < cells.Count ? cells[column] : string.Empty, resources);
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(9, 7, 9, 7),
                BorderBrush = Brush(resources, "ControlBorderBrush", Color.FromRgb(63, 86, 110)),
                BorderThickness = new Thickness(0, 0, column + 1 < columnCount ? 1 : 0, 1)
            });
        }

        return row;
    }

    private BlockUIContainer CreateCallout(string markdown, FrameworkElement resources)
    {
        var (kind, content, accentKey, fallback) = ClassifyCallout(markdown);
        var paragraph = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(resources, "TextBrush", Colors.White),
            FontSize = 14,
            LineHeight = 21
        };
        paragraph.Inlines.Add(new Bold(new Run($"{kind}: ")));
        AddInlines(paragraph.Inlines, content, resources);
        AutomationProperties.SetName(paragraph, $"{kind} callout");
        return new BlockUIContainer(new Border
        {
            Background = Brush(resources, "InputBrush", Color.FromRgb(13, 23, 32)),
            BorderBrush = Brush(resources, accentKey, fallback),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 4, 0, 16),
            Child = paragraph
        });
    }

    private BlockUIContainer CreateCodeBlock(string code, string language, FrameworkElement resources)
    {
        var text = new TextBlock
        {
            Text = code,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Foreground = Brush(resources, "TextBrush", Colors.White)
        };
        AutomationProperties.SetName(text, string.IsNullOrWhiteSpace(language) ? "Code example" : $"{language} code example");
        var copyButton = new Button
        {
            Content = "Copy",
            MinWidth = 56,
            MinHeight = 44,
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "Copy code to the clipboard"
        };
        AutomationProperties.SetName(copyButton, "Copy code");
        AutomationProperties.SetHelpText(copyButton, "Copies this code example to the clipboard.");
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copyButton.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copyButton.Content = "Copy failed";
            }
        };

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(text);
        Grid.SetColumn(copyButton, 1);
        layout.Children.Add(copyButton);

        return new BlockUIContainer(new Border
        {
            Background = Brush(resources, "InputBrush", Color.FromRgb(13, 23, 32)),
            BorderBrush = Brush(resources, "ControlBorderBrush", Color.FromRgb(63, 86, 110)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 4, 0, 16),
            Child = layout
        });
    }

    private BlockUIContainer CreateDetails(string title, string markdown, FrameworkElement resources)
    {
        var bodyDocument = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = Brush(resources, "TextBrush", Colors.White),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 21
        };
        RenderBlocks(bodyDocument.Blocks, markdown, resources);
        var viewer = new FlowDocumentScrollViewer
        {
            Document = bodyDocument,
            Background = Brushes.Transparent,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0)
        };
        var expander = new Expander
        {
            Header = title,
            Content = viewer,
            IsExpanded = false,
            Margin = new Thickness(0, 4, 0, 16),
            Padding = new Thickness(12, 8, 12, 10),
            Background = Brush(resources, "InputBrush", Color.FromRgb(13, 23, 32)),
            Foreground = Brush(resources, "TextBrush", Colors.White)
        };
        AutomationProperties.SetName(expander, title);
        AutomationProperties.SetHelpText(expander, "Expand or collapse advanced help details.");
        return new BlockUIContainer(expander);
    }

    private BlockUIContainer CreateImageBlock(string altText, string relativePath, FrameworkElement resources)
    {
        var safePath = ResolveLocalImagePath(relativePath);
        if (safePath is null)
        {
            return new BlockUIContainer(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(altText) ? "Image unavailable" : $"Image unavailable: {altText}",
                Foreground = Brush(resources, "MutedTextBrush", Color.FromRgb(180, 192, 207)),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 16)
            });
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(safePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        var image = new Image
        {
            Source = bitmap,
            MaxWidth = 920,
            MaxHeight = 560,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true
        };
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(altText) ? "Help image" : altText);
        return new BlockUIContainer(new Border
        {
            BorderBrush = Brush(resources, "ControlBorderBrush", Color.FromRgb(63, 86, 110)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 4, 0, 16),
            Child = image
        });
    }

    private string? ResolveLocalImagePath(string rawPath)
    {
        if (contentRoot is null
            || string.IsNullOrWhiteSpace(rawPath)
            || rawPath.Length > 260
            || rawPath.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(rawPath)
            || !rawPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            || rawPath.Split('/').Any(segment => segment is "." or ".."))
        {
            return null;
        }

        var extension = Path.GetExtension(rawPath);
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            return null;
        }

        var root = Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, rawPath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    private static (string Kind, string Content, string AccentKey, Color Fallback) ClassifyCallout(string markdown)
    {
        var match = CalloutKindRegex().Match(markdown.Trim());
        var kind = match.Success
            ? (match.Groups["kind"].Success ? match.Groups["kind"].Value : match.Groups["alertKind"].Value).ToUpperInvariant()
            : "NOTE";
        var content = match.Success ? match.Groups["content"].Value.TrimStart() : markdown.Trim();
        return kind switch
        {
            "WARNING" => ("Warning", content, "DangerBorderBrush", Color.FromRgb(255, 107, 122)),
            "TIP" => ("Tip", content, "BetaAccentBrush", Color.FromRgb(255, 205, 91)),
            _ => ("Note", content, "PrimaryBorderBrush", Color.FromRgb(77, 212, 239))
        };
    }

    private static BlockUIContainer CreateDivider(FrameworkElement resources)
    {
        return new BlockUIContainer(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 18),
            Background = Brush(resources, "ControlBorderBrush", Color.FromRgb(63, 86, 110))
        });
    }

    private void AppendRelatedArticles(BlockCollection blocks, HelpArticle article, FrameworkElement resources)
    {
        var related = article.RelatedArticleIds
            .Select(catalog.FindArticle)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        if (related.Length == 0)
        {
            return;
        }

        blocks.Add(CreateHeading(new HelpHeading(2, "Related articles", "related-articles"), resources));
        var list = new List
        {
            MarkerStyle = TextMarkerStyle.Disc,
            Margin = new Thickness(18, 0, 0, 8)
        };
        foreach (var relatedArticle in related)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
            AddSafeLink(paragraph.Inlines, relatedArticle.Title, relatedArticle.Route, resources);
            list.ListItems.Add(new ListItem(paragraph));
        }
        blocks.Add(list);
    }

    private void AddInlines(InlineCollection inlines, string markdown, FrameworkElement resources)
    {
        var index = 0;
        foreach (Match match in InlineRegex().Matches(markdown))
        {
            if (match.Index > index)
            {
                inlines.Add(new Run(Unescape(markdown[index..match.Index])));
            }

            if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["codeText"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 13,
                    Background = Brush(resources, "InputBrush", Color.FromRgb(13, 23, 32)),
                    Foreground = Brush(resources, "BetaAccentBrush", Color.FromRgb(255, 205, 91))
                });
            }
            else if (match.Groups["bold"].Success)
            {
                inlines.Add(new Bold(new Run(Unescape(match.Groups["boldText"].Value))));
            }
            else if (match.Groups["italic"].Success)
            {
                inlines.Add(new Italic(new Run(Unescape(match.Groups["italicText"].Value))));
            }
            else if (match.Groups["link"].Success)
            {
                AddSafeLink(inlines, Unescape(match.Groups["label"].Value), match.Groups["route"].Value.Trim(), resources);
            }

            index = match.Index + match.Length;
        }

        if (index < markdown.Length)
        {
            inlines.Add(new Run(Unescape(markdown[index..])));
        }
    }

    private void AddSafeLink(InlineCollection inlines, string label, string route, FrameworkElement resources)
    {
        if (!resolveLink(route, out var target))
        {
            inlines.Add(new Run(label));
            return;
        }

        var link = new Hyperlink(new Run(label))
        {
            NavigateUri = new Uri(target.Route, UriKind.RelativeOrAbsolute),
            Tag = target,
            Foreground = Brush(resources, "PrimaryBorderBrush", Color.FromRgb(77, 212, 239)),
            ToolTip = target.Kind switch
            {
                HelpLinkKind.App => $"Open {label} in AI Arena",
                HelpLinkKind.External => $"Open external website: {target.ExternalUri?.Host}",
                _ => $"Open {label}"
            }
        };
        AutomationProperties.SetName(link, target.Kind == HelpLinkKind.External ? $"{label}, external link" : label);
        inlines.Add(link);
    }

    private static bool IsStructuralStart(string[] lines, int index)
    {
        var line = lines[index].TrimEnd();
        return line.StartsWith("```", StringComparison.Ordinal)
            || HeadingRegex().IsMatch(line)
            || line.StartsWith(":::details", StringComparison.OrdinalIgnoreCase)
            || ImageRegex().IsMatch(line.Trim())
            || line.TrimStart().StartsWith('>')
            || IsDivider(line)
            || TryGetListItem(line, out _, out _)
            || IsTableStart(lines, index);
    }

    private static bool TryGetListItem(string line, out bool ordered, out string text)
    {
        var match = ListItemRegex().Match(line);
        if (!match.Success)
        {
            ordered = false;
            text = string.Empty;
            return false;
        }

        ordered = match.Groups["number"].Success;
        text = match.Groups["text"].Value.Trim();
        return true;
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        return index + 1 < lines.Length
            && LooksLikeTableRow(lines[index])
            && TableDividerRegex().IsMatch(lines[index + 1].Trim());
    }

    private static bool LooksLikeTableRow(string line)
    {
        return line.Count(character => character == '|') >= 2;
    }

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim().Trim('|');
        return trimmed.Split('|').Select(cell => cell.Trim()).Take(MaximumTableColumns).ToArray();
    }

    private static bool IsDivider(string line)
    {
        var value = line.Trim();
        return value is "---" or "***" or "___";
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string PlainInlineText(string value)
    {
        return Regex.Replace(value, @"(?<!!)\[([^\]]+)\]\([^\)]+\)|[`*_]", match =>
            match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : string.Empty);
    }

    private static string Unescape(string value)
    {
        return Regex.Replace(value, @"\\([\\`*_{}\[\]()#+\-.!>])", "$1");
    }

    private static Brush Brush(FrameworkElement resources, string key, Color fallback)
    {
        return resources.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }

    [GeneratedRegex(@"^(?<marks>#{1,4})\s+(?<title>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:(?<number>\d+)\.|[-*+])\s+(?<text>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableDividerRegex();

    [GeneratedRegex(@"(?<code>`(?<codeText>[^`\r\n]+)`)|(?<bold>\*\*(?<boldText>.+?)\*\*)|(?<link>(?<!!)\[(?<label>[^\]\r\n]+)\]\((?<route>[^\)\r\n]+)\))|(?<italic>(?<!\*)\*(?<italicText>[^*\r\n]+)\*(?!\*))", RegexOptions.CultureInvariant)]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^!\[(?<alt>[^\]\r\n]*)\]\((?<path>[^\)\r\n]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^(?:\*\*(?<kind>NOTE|TIP|WARNING)\s*:?\*\*|\[!(?<alertKind>NOTE|TIP|WARNING)\])\s*:?[ \t]*(?<content>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CalloutKindRegex();
}
