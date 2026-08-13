using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIArena.Wpf.Help.Presentation;

internal enum HelpCenterLayoutMode
{
    Compact,
    Standard,
    Wide
}

internal static class HelpCenterLayout
{
    public const double StandardBreakpoint = 960;
    public const double WideBreakpoint = 1200;

    public static HelpCenterLayoutMode ForWidth(double width) => width switch
    {
        < StandardBreakpoint => HelpCenterLayoutMode.Compact,
        < WideBreakpoint => HelpCenterLayoutMode.Standard,
        _ => HelpCenterLayoutMode.Wide
    };
}

internal sealed class HelpNavigationGroup
{
    public HelpNavigationGroup(HelpGroup group, IEnumerable<HelpArticle> articles)
    {
        Id = group.Id;
        Title = group.Title;
        IconGlyph = group.IconGlyph;
        Articles = articles.OrderBy(article => article.Order).ToArray();
    }

    public string Id { get; }

    public string Title { get; }

    public string IconGlyph { get; }

    public IReadOnlyList<HelpArticle> Articles { get; }
}

internal sealed record HelpTaskCard(string Title, string Summary, string ArticleId, string IconGlyph, int Order);

internal sealed record HelpHeadingLink(string Title, string Anchor, int Level);

internal sealed class HelpCenterRunState
{
    private readonly Dictionary<string, double> articleScrollOffsets = new(StringComparer.OrdinalIgnoreCase);

    public string? LastArticleId { get; set; }

    public IReadOnlyDictionary<string, double> ArticleScrollOffsets => articleScrollOffsets;

    public void SaveScrollOffset(string articleId, double offset)
    {
        if (string.IsNullOrWhiteSpace(articleId) || !double.IsFinite(offset))
        {
            return;
        }

        articleScrollOffsets[articleId] = Math.Max(0, offset);
    }

    public double ScrollOffsetFor(string articleId) =>
        articleScrollOffsets.TryGetValue(articleId, out var offset) ? offset : 0;
}

internal sealed class HelpCenterViewModel : INotifyPropertyChanged
{
    private readonly HelpCatalog catalog;
    private readonly IHelpContentService contentService;
    private readonly HelpNavigationHistory history = new();
    private HelpArticle? currentArticle;
    private string searchText = string.Empty;
    private IReadOnlyList<HelpSearchResult> searchResults = [];
    private bool isSearchMode;

    public HelpCenterViewModel(IHelpContentService contentService)
    {
        this.contentService = contentService;
        catalog = contentService.LoadCatalog();
        Groups = catalog.Groups
            .OrderBy(group => group.Order)
            .Select(group => new HelpNavigationGroup(
                group,
                catalog.Articles.Where(article => article.GroupId.Equals(group.Id, StringComparison.OrdinalIgnoreCase))))
            .Where(group => group.Articles.Count > 0)
            .ToArray();
        HomeCards = BuildHomeCards(catalog);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HelpCatalog Catalog => catalog;

    public IReadOnlyList<HelpNavigationGroup> Groups { get; }

    public IReadOnlyList<HelpTaskCard> HomeCards { get; }

    public HelpArticle? CurrentArticle
    {
        get => currentArticle;
        private set
        {
            if (ReferenceEquals(currentArticle, value))
            {
                return;
            }

            currentArticle = value;
            Raise();
            Raise(nameof(CurrentTitle));
            Raise(nameof(CurrentSummary));
            Raise(nameof(CurrentGroupTitle));
            Raise(nameof(PreviousArticle));
            Raise(nameof(NextArticle));
            Raise(nameof(CurrentHeadings));
        }
    }

    public string CurrentTitle => CurrentArticle?.Title ?? "Help Center";

    public string CurrentSummary => CurrentArticle?.Summary ?? "Choose a guide topic.";

    public string CurrentGroupTitle => CurrentArticle is null
        ? "Guide"
        : catalog.Groups.FirstOrDefault(group => group.Id.Equals(CurrentArticle.GroupId, StringComparison.OrdinalIgnoreCase))?.Title ?? "Guide";

    public string SearchText
    {
        get => searchText;
        set
        {
            value ??= string.Empty;
            if (searchText.Equals(value, StringComparison.Ordinal))
            {
                return;
            }

            searchText = value;
            Raise();
            UpdateSearch();
        }
    }

    public IReadOnlyList<HelpSearchResult> SearchResults
    {
        get => searchResults;
        private set
        {
            searchResults = value;
            Raise();
            Raise(nameof(SearchResultStatus));
            Raise(nameof(HasSearchResults));
        }
    }

    public bool IsSearchMode
    {
        get => isSearchMode;
        private set
        {
            if (isSearchMode == value)
            {
                return;
            }

            isSearchMode = value;
            Raise();
            Raise(nameof(IsBrowseMode));
        }
    }

    public bool IsBrowseMode => !IsSearchMode;

    public bool HasSearchResults => SearchResults.Count > 0;

    public string SearchResultStatus => SearchResults.Count switch
    {
        0 when IsSearchMode => $"No results for {SearchText.Trim()}",
        1 => "1 result",
        _ => $"{SearchResults.Count} results"
    };

    public bool CanGoBack => history.CanGoBack;

    public bool CanGoForward => history.CanGoForward;

    public HelpArticle? PreviousArticle => AdjacentArticle(-1);

    public HelpArticle? NextArticle => AdjacentArticle(1);

    public IReadOnlyList<HelpHeadingLink> CurrentHeadings => CurrentArticle?.Headings
        .Where(heading => heading.Level is 2 or 3)
        .Select(heading => new HelpHeadingLink(heading.Title, heading.Anchor, heading.Level))
        .ToArray() ?? [];

    public bool Navigate(string? articleId, string? anchor = null, bool recordHistory = true)
    {
        var article = catalog.FindArticle(articleId) ?? catalog.FindArticle("home") ?? catalog.Articles.FirstOrDefault();
        if (article is null)
        {
            return false;
        }

        if (recordHistory)
        {
            history.Navigate(new HelpLocation(article.Id, anchor));
        }

        CurrentArticle = article;
        RaiseNavigationState();
        return true;
    }

    public HelpLocation? GoBack()
    {
        if (!history.TryGoBack(out var location) || location is null)
        {
            return null;
        }

        Navigate(location.ArticleId, location.Anchor, recordHistory: false);
        return location;
    }

    public HelpLocation? GoForward()
    {
        if (!history.TryGoForward(out var location) || location is null)
        {
            return null;
        }

        Navigate(location.ArticleId, location.Anchor, recordHistory: false);
        return location;
    }

    public void ClearSearch()
    {
        SearchText = string.Empty;
    }

    private void UpdateSearch()
    {
        IsSearchMode = !string.IsNullOrWhiteSpace(SearchText);
        SearchResults = IsSearchMode ? contentService.Search(SearchText, 40) : [];
    }

    private HelpArticle? AdjacentArticle(int offset)
    {
        if (CurrentArticle is null)
        {
            return null;
        }

        var articles = catalog.Articles.OrderBy(article =>
            catalog.Groups.FirstOrDefault(group => group.Id.Equals(article.GroupId, StringComparison.OrdinalIgnoreCase))?.Order ?? int.MaxValue)
            .ThenBy(article => article.Order)
            .ToArray();
        var index = Array.FindIndex(articles, article => article.Id.Equals(CurrentArticle.Id, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        return target >= 0 && target < articles.Length ? articles[target] : null;
    }

    private void RaiseNavigationState()
    {
        Raise(nameof(CanGoBack));
        Raise(nameof(CanGoForward));
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static IReadOnlyList<HelpTaskCard> BuildHomeCards(HelpCatalog catalog)
    {
        return catalog.Journeys
            .OrderBy(journey => journey.Order)
            .Select(journey => (Journey: journey, Article: catalog.FindArticle(journey.StartArticleId)))
            .Where(item => item.Article is not null)
            .Select(item => new HelpTaskCard(
                item.Journey.Title,
                item.Journey.Summary,
                item.Article!.Id,
                string.IsNullOrWhiteSpace(item.Journey.IconGlyph) ? item.Article.IconGlyph : item.Journey.IconGlyph,
                item.Journey.Order))
            .ToArray();
    }
}
