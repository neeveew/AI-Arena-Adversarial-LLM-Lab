using System.Windows;
using System.Windows.Documents;

namespace AIArena.Wpf.Help;

internal interface IHelpContentService
{
    HelpCatalog LoadCatalog();

    IReadOnlyList<HelpSearchResult> Search(string? query, int maxResults = 20);

    FlowDocument BuildDocument(string articleId, FrameworkElement resources);

    bool TryResolveLink(string? rawLink, out HelpLinkTarget target);
}

internal sealed record HelpGroup(
    string Id,
    string Title,
    int Order,
    string IconGlyph);

internal sealed record HelpAction(
    string Label,
    string Route);

internal sealed record HelpJourney(
    string Id,
    string Title,
    string Summary,
    string StartArticleId,
    IReadOnlyList<string> ArticleIds,
    int Order,
    string IconGlyph);

internal sealed record HelpArticle(
    string Id,
    string GroupId,
    string Title,
    string Summary,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Aliases,
    string IconGlyph,
    string Route,
    int Order,
    string IntroducedVersion,
    string ReviewedVersion,
    string ContentFile,
    IReadOnlyList<string> RelatedArticleIds,
    IReadOnlyList<HelpAction> Actions,
    string Markdown,
    IReadOnlyList<HelpHeading> Headings);

internal sealed record HelpHeading(
    int Level,
    string Title,
    string Anchor);

internal sealed record HelpCatalog(
    string SchemaVersion,
    string GuideVersion,
    DateTimeOffset ReviewedUtc,
    IReadOnlyList<HelpJourney> Journeys,
    IReadOnlyList<HelpGroup> Groups,
    IReadOnlyList<HelpArticle> Articles)
{
    public HelpArticle? FindArticle(string? articleId)
    {
        return Articles.FirstOrDefault(article =>
            article.Id.Equals(articleId, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record HelpTextRange(int Start, int Length);

internal sealed record HelpSearchResult(
    HelpArticle Article,
    double Score,
    string Snippet,
    IReadOnlyList<HelpTextRange> SnippetMatches,
    IReadOnlyList<HelpTextRange> TitleMatches);

internal enum HelpLinkKind
{
    Article,
    App,
    Anchor,
    External
}

internal sealed record HelpLinkTarget(
    HelpLinkKind Kind,
    string Route,
    string? ArticleId = null,
    string? Anchor = null,
    Uri? ExternalUri = null);

internal sealed record HelpLocation(string ArticleId, string? Anchor = null)
{
    public string Route => string.IsNullOrWhiteSpace(Anchor)
        ? $"help/{ArticleId}"
        : $"help/{ArticleId}#{Anchor}";
}
