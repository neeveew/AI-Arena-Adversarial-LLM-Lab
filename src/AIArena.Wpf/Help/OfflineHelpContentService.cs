using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace AIArena.Wpf.Help;

internal sealed partial class OfflineHelpContentService : IHelpContentService
{
    internal const string SupportedSchemaVersion = "ai_arena.help_manifest.v1";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumArticleBytes = 512 * 1024;
    private const int MaximumArticles = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };

    private readonly HelpCatalog catalog;
    private readonly HelpMarkdownRenderer renderer;

    public OfflineHelpContentService(string manifestPath)
    {
        catalog = LoadFromManifest(manifestPath);
        renderer = new HelpMarkdownRenderer(catalog, TryResolveLink, Path.GetDirectoryName(Path.GetFullPath(manifestPath)));
    }

    internal OfflineHelpContentService(HelpCatalog catalog)
    {
        this.catalog = ValidateCatalog(catalog);
        renderer = new HelpMarkdownRenderer(this.catalog, TryResolveLink);
    }

    public HelpCatalog LoadCatalog() => catalog;

    public IReadOnlyList<HelpSearchResult> Search(string? query, int maxResults = 20)
    {
        return HelpSearchEngine.Search(catalog, query, maxResults);
    }

    public FlowDocument BuildDocument(string articleId, FrameworkElement resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var article = catalog.FindArticle(articleId)
            ?? throw new KeyNotFoundException($"Unknown help article '{articleId}'.");
        return renderer.Render(article, resources);
    }

    public bool TryResolveLink(string? rawLink, out HelpLinkTarget target)
    {
        var articleIds = catalog.Articles.Select(article => article.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return HelpDeepLink.TryParse(rawLink, articleIds, out target);
    }

    public static string? ResolveDefaultManifestPath()
    {
        return ResolveManifestPath(AppContext.BaseDirectory);
    }

    internal static string? ResolveManifestPath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
        }

        var resolvedBaseDirectory = Path.GetFullPath(baseDirectory);
        var installed = Path.Combine(resolvedBaseDirectory, "Help", "Content", "guide-manifest.json");
        if (File.Exists(installed))
        {
            return installed;
        }

        var current = new DirectoryInfo(resolvedBaseDirectory);
        while (current is not null)
        {
            var source = Path.Combine(current.FullName, "src", "AIArena.Wpf", "Help", "Content", "guide-manifest.json");
            if (File.Exists(source))
            {
                return source;
            }

            current = current.Parent;
        }

        return null;
    }

    internal static HelpCatalog LoadFromManifest(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("A help manifest path is required.", nameof(manifestPath));
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifestRoot = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException("The help manifest has no containing directory.");
        var manifestText = ReadBoundedUtf8(fullManifestPath, MaximumManifestBytes, "help manifest");
        ManifestDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(manifestText, JsonOptions)
                ?? throw new InvalidDataException("The help manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The help manifest is not valid JSON.", ex);
        }

        if (dto.Articles is null || dto.Articles.Count > MaximumArticles)
        {
            throw new InvalidDataException($"The help manifest must contain at most {MaximumArticles} articles.");
        }

        var articles = new List<HelpArticle>(dto.Articles.Count);
        foreach (var article in dto.Articles)
        {
            var contentPath = ResolveContentPath(manifestRoot, article.ContentFile);
            var markdown = ReadBoundedUtf8(contentPath, MaximumArticleBytes, $"help article '{article.Id}'");
            articles.Add(new HelpArticle(
                article.Id ?? string.Empty,
                article.GroupId ?? string.Empty,
                article.Title ?? string.Empty,
                article.Summary ?? string.Empty,
                CleanList(article.Keywords),
                CleanList(article.Aliases),
                article.IconGlyph ?? string.Empty,
                article.Route ?? string.Empty,
                article.Order,
                article.IntroducedVersion ?? string.Empty,
                article.ReviewedVersion ?? string.Empty,
                article.ContentFile ?? string.Empty,
                CleanList(article.RelatedArticleIds),
                (article.Actions ?? []).Select(action => new HelpAction(action.Label ?? string.Empty, action.Route ?? string.Empty)).ToArray(),
                markdown,
                HelpMarkdownRenderer.ExtractHeadings(markdown)));
        }

        DateTimeOffset reviewedUtc;
        if (!DateTimeOffset.TryParse(dto.ReviewedUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out reviewedUtc))
        {
            throw new InvalidDataException("The help manifest reviewedUtc must be an ISO-8601 timestamp.");
        }

        var catalog = new HelpCatalog(
            dto.SchemaVersion ?? string.Empty,
            dto.GuideVersion ?? string.Empty,
            reviewedUtc,
            (dto.Journeys ?? []).Select(journey => new HelpJourney(
                journey.Id ?? string.Empty,
                journey.Title ?? string.Empty,
                journey.Summary ?? string.Empty,
                journey.StartArticleId ?? string.Empty,
                CleanList(journey.ArticleIds),
                journey.Order,
                journey.IconGlyph ?? string.Empty)).ToArray(),
            (dto.Groups ?? []).Select(group => new HelpGroup(
                group.Id ?? string.Empty,
                group.Title ?? string.Empty,
                group.Order,
                group.IconGlyph ?? string.Empty)).ToArray(),
            articles);
        return ValidateCatalog(catalog);
    }

    private static HelpCatalog ValidateCatalog(HelpCatalog value)
    {
        if (!value.SchemaVersion.Equals(SupportedSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported help manifest schema '{value.SchemaVersion}'.");
        }

        RequireText(value.GuideVersion, "guideVersion");
        if (value.Groups.Count == 0 || value.Articles.Count == 0)
        {
            throw new InvalidDataException("The help catalog must contain groups and articles.");
        }

        var groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in value.Groups)
        {
            RequireStableId(group.Id, "group ID");
            RequireText(group.Title, $"group '{group.Id}' title");
            if (!groupIds.Add(group.Id))
            {
                throw new InvalidDataException($"Duplicate help group ID '{group.Id}'.");
            }
        }

        var articleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in value.Articles)
        {
            RequireStableId(article.Id, "article ID");
            RequireText(article.Title, $"article '{article.Id}' title");
            RequireText(article.Summary, $"article '{article.Id}' summary");
            RequireText(article.IntroducedVersion, $"article '{article.Id}' introducedVersion");
            RequireText(article.ReviewedVersion, $"article '{article.Id}' reviewedVersion");
            if (!groupIds.Contains(article.GroupId))
            {
                throw new InvalidDataException($"Article '{article.Id}' references unknown group '{article.GroupId}'.");
            }

            if (!articleIds.Add(article.Id))
            {
                throw new InvalidDataException($"Duplicate help article ID '{article.Id}'.");
            }
        }

        foreach (var article in value.Articles)
        {
            if (!article.Route.Equals($"help/{article.Id}", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Article '{article.Id}' must use route 'help/{article.Id}'.");
            }

            foreach (var relatedId in article.RelatedArticleIds)
            {
                if (!articleIds.Contains(relatedId) || relatedId.Equals(article.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Article '{article.Id}' has an invalid related article '{relatedId}'.");
                }
            }

            foreach (var action in article.Actions)
            {
                RequireText(action.Label, $"article '{article.Id}' action label");
                if (!HelpDeepLink.TryParse(action.Route, articleIds, out var target)
                    || target.Kind is HelpLinkKind.Anchor or HelpLinkKind.External)
                {
                    throw new InvalidDataException($"Article '{article.Id}' has an unsafe action route '{action.Route}'.");
                }
            }

            ValidateMarkdownLinks(article, articleIds, value.Articles);
        }

        var journeyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var journey in value.Journeys)
        {
            RequireStableId(journey.Id, "journey ID");
            RequireText(journey.Title, $"journey '{journey.Id}' title");
            RequireText(journey.Summary, $"journey '{journey.Id}' summary");
            if (!journeyIds.Add(journey.Id)
                || !articleIds.Contains(journey.StartArticleId)
                || journey.ArticleIds.Count == 0
                || !journey.ArticleIds.Contains(journey.StartArticleId, StringComparer.OrdinalIgnoreCase)
                || journey.ArticleIds.Any(id => !articleIds.Contains(id)))
            {
                throw new InvalidDataException($"Journey '{journey.Id}' has invalid or duplicate article references.");
            }
        }

        var normalizedGroups = value.Groups.OrderBy(group => group.Order).ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        var groupOrder = normalizedGroups.Select((group, index) => (group.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.OrdinalIgnoreCase);
        var normalizedArticles = value.Articles
            .OrderBy(article => groupOrder[article.GroupId])
            .ThenBy(article => article.Order)
            .ThenBy(article => article.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedJourneys = value.Journeys
            .OrderBy(journey => journey.Order)
            .ThenBy(journey => journey.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return value with { Journeys = normalizedJourneys, Groups = normalizedGroups, Articles = normalizedArticles };
    }

    private static void ValidateMarkdownLinks(
        HelpArticle article,
        IReadOnlySet<string> articleIds,
        IReadOnlyList<HelpArticle> articles)
    {
        var anchors = article.Headings.Select(heading => heading.Anchor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in MarkdownLinkRegex().Matches(article.Markdown))
        {
            var route = match.Groups["route"].Value.Trim();
            if (!HelpDeepLink.TryParse(route, articleIds, out var target))
            {
                throw new InvalidDataException($"Article '{article.Id}' contains unsafe link '{route}'.");
            }

            if (target.Kind == HelpLinkKind.Anchor && !anchors.Contains(target.Anchor!))
            {
                throw new InvalidDataException($"Article '{article.Id}' links to missing anchor '#{target.Anchor}'.");
            }

            if (target.Kind == HelpLinkKind.Article && !string.IsNullOrWhiteSpace(target.Anchor))
            {
                var targetArticle = articles.First(targetArticle =>
                    targetArticle.Id.Equals(target.ArticleId, StringComparison.OrdinalIgnoreCase));
                if (!targetArticle.Headings.Any(heading => heading.Anchor.Equals(target.Anchor, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Article '{article.Id}' links to missing anchor '{route}'.");
                }
            }
        }
    }

    private static string ResolveContentPath(string manifestRoot, string? contentFile)
    {
        RequireText(contentFile, "article contentFile");
        var relative = contentFile!.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".." or "."))
        {
            throw new InvalidDataException($"Unsafe help content path '{contentFile}'.");
        }

        var root = Path.GetFullPath(manifestRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Help content path escapes its manifest directory: '{contentFile}'.");
        }

        return resolved;
    }

    private static string ReadBoundedUtf8(string path, int maximumBytes, string label)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Missing {label}.", path);
        }

        if (file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException($"The {label} must contain 1-{maximumBytes} bytes.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try
        {
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text)
                ? throw new InvalidDataException($"The {label} is blank.")
                : text;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"The {label} is not valid UTF-8.", ex);
        }
    }

    private static IReadOnlyList<string> CleanList(List<string?>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void RequireStableId(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !StableIdRegex().IsMatch(value))
        {
            throw new InvalidDataException($"Invalid {label} '{value}'.");
        }
    }

    private static void RequireText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing {label}.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();

    [GeneratedRegex(@"(?<!!)\[[^\]\r\n]+\]\((?<route>[^\)\r\n]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    private sealed record ManifestDto(
        string? SchemaVersion,
        string? GuideVersion,
        string? ReviewedUtc,
        List<JourneyDto>? Journeys,
        List<GroupDto>? Groups,
        List<ArticleDto>? Articles);

    private sealed record JourneyDto(
        string? Id,
        string? Title,
        string? Summary,
        string? StartArticleId,
        List<string?>? ArticleIds,
        int Order,
        string? IconGlyph);

    private sealed record GroupDto(string? Id, string? Title, int Order, string? IconGlyph);

    private sealed record ArticleDto(
        string? Id,
        string? GroupId,
        string? Title,
        string? Summary,
        List<string?>? Keywords,
        List<string?>? Aliases,
        string? IconGlyph,
        string? Route,
        int Order,
        string? IntroducedVersion,
        string? ReviewedVersion,
        string? ContentFile,
        List<string?>? RelatedArticleIds,
        List<ActionDto>? Actions);

    private sealed record ActionDto(string? Label, string? Route);
}
