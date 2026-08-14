using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AIArena.Wpf.Help;

internal static partial class HelpSearchEngine
{
    private const int MaximumQueryLength = 256;
    private const int MaximumSnippetLength = 190;

    internal static HelpSearchIndex BuildIndex(HelpCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new HelpSearchIndex(catalog.Articles.Select(IndexArticle).ToArray());
    }

    public static IReadOnlyList<HelpSearchResult> Search(HelpSearchIndex index, string? query, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(index);
        maxResults = Math.Clamp(maxResults, 1, 100);
        var normalizedQuery = Normalize(query ?? string.Empty);
        var tokens = Tokenize(normalizedQuery);
        if (tokens.Count == 0)
        {
            return index.Articles
                .Take(maxResults)
                .Select(item => new HelpSearchResult(item.Article, 0, item.Article.Summary, [], []))
                .ToArray();
        }

        return index.Articles
            .Select(item => Score(item, normalizedQuery, tokens))
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Article.Order)
            .ThenBy(result => result.Article.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Article.Id, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    private static HelpSearchIndexEntry IndexArticle(HelpArticle article)
    {
        var id = Normalize(article.Id.Replace('-', ' '));
        var title = Normalize(article.Title);
        var aliases = article.Aliases.Select(Normalize).ToArray();
        var keywords = article.Keywords.Select(Normalize).ToArray();
        var headings = article.Headings.Select(heading => Normalize(heading.Title)).ToArray();
        var summary = Normalize(article.Summary);
        var strippedMarkdown = StripMarkdown(article.Markdown);
        var body = Normalize(strippedMarkdown);
        var searchable = string.Join(' ', new[] { id, title, summary }.Concat(aliases).Concat(keywords).Concat(headings).Append(body));
        return new HelpSearchIndexEntry(
            article,
            id,
            title,
            aliases,
            keywords,
            headings,
            summary,
            body,
            searchable,
            strippedMarkdown);
    }

    private static HelpSearchResult? Score(HelpSearchIndexEntry item, string phrase, IReadOnlyList<string> tokens)
    {
        var article = item.Article;

        if (tokens.Any(token => !item.Searchable.Contains(token, StringComparison.Ordinal)))
        {
            return null;
        }

        double score = 0;
        if (item.Id.Equals(phrase, StringComparison.Ordinal)) score += 650;
        if (item.Title.Equals(phrase, StringComparison.Ordinal)) score += 600;
        if (item.Title.StartsWith(phrase, StringComparison.Ordinal)) score += 260;
        if (item.Title.Contains(phrase, StringComparison.Ordinal)) score += 180;
        if (item.Aliases.Any(alias => alias.Equals(phrase, StringComparison.Ordinal))) score += 320;
        if (item.Keywords.Any(keyword => keyword.Equals(phrase, StringComparison.Ordinal))) score += 220;
        if (item.Summary.Contains(phrase, StringComparison.Ordinal)) score += 100;
        if (item.Body.Contains(phrase, StringComparison.Ordinal)) score += 35;

        foreach (var token in tokens)
        {
            if (item.Title.Equals(token, StringComparison.Ordinal)) score += 170;
            else if (item.Title.StartsWith(token, StringComparison.Ordinal)) score += 125;
            else if (ContainsWord(item.Title, token)) score += 95;
            else if (item.Title.Contains(token, StringComparison.Ordinal)) score += 65;

            if (item.Aliases.Any(alias => ContainsWord(alias, token))) score += 75;
            if (item.Keywords.Any(keyword => ContainsWord(keyword, token))) score += 65;
            if (item.Headings.Any(heading => ContainsWord(heading, token))) score += 38;
            if (ContainsWord(item.Summary, token)) score += 28;
            if (ContainsWord(item.Body, token)) score += 10;
        }

        var snippetSource = SelectSnippetSource(item, phrase, tokens);
        var snippet = CreateSnippet(snippetSource, phrase, tokens);
        return new HelpSearchResult(
            article,
            score,
            snippet,
            MatchRanges(snippet, tokens),
            MatchRanges(article.Title, tokens));
    }

    private static string SelectSnippetSource(HelpSearchIndexEntry item, string phrase, IReadOnlyList<string> tokens)
    {
        if (ContainsQuery(item.Summary, phrase, tokens, normalized: true))
        {
            return item.Article.Summary;
        }

        for (var index = 0; index < item.Article.Headings.Count; index++)
        {
            var heading = item.Article.Headings[index];
            if (ContainsQuery(item.Headings[index], phrase, tokens, normalized: true))
            {
                return $"{heading.Title}. {item.StrippedMarkdown}";
            }
        }

        return item.StrippedMarkdown;
    }

    private static bool ContainsQuery(
        string value,
        string phrase,
        IReadOnlyList<string> tokens,
        bool normalized = false)
    {
        var searchable = normalized ? value : Normalize(value);
        return searchable.Contains(phrase, StringComparison.Ordinal)
            || tokens.Any(token => searchable.Contains(token, StringComparison.Ordinal));
    }

    private static string CreateSnippet(string source, string phrase, IReadOnlyList<string> tokens)
    {
        var compact = WhitespaceRegex().Replace(source, " ").Trim();
        if (compact.Length <= MaximumSnippetLength)
        {
            return compact;
        }

        var index = FindFirstMatch(compact, phrase, tokens);
        var start = Math.Max(0, index - MaximumSnippetLength / 3);
        if (start > 0)
        {
            var nextSpace = compact.IndexOf(' ', start);
            start = nextSpace >= 0 ? nextSpace + 1 : start;
        }

        var length = Math.Min(MaximumSnippetLength, compact.Length - start);
        var end = start + length;
        if (end < compact.Length)
        {
            var previousSpace = compact.LastIndexOf(' ', end - 1, length);
            if (previousSpace > start)
            {
                end = previousSpace;
            }
        }

        var result = compact[start..end].Trim();
        return $"{(start > 0 ? "…" : string.Empty)}{result}{(end < compact.Length ? "…" : string.Empty)}";
    }

    private static int FindFirstMatch(string source, string phrase, IReadOnlyList<string> tokens)
    {
        var index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(source, phrase, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        if (index >= 0)
        {
            return index;
        }

        return tokens.Select(token => CultureInfo.InvariantCulture.CompareInfo.IndexOf(source, token, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace))
            .Where(match => match >= 0)
            .DefaultIfEmpty(0)
            .Min();
    }

    private static IReadOnlyList<HelpTextRange> MatchRanges(string text, IReadOnlyList<string> tokens)
    {
        var ranges = new List<HelpTextRange>();
        foreach (var token in tokens.OrderByDescending(token => token.Length))
        {
            var start = 0;
            while (start < text.Length)
            {
                var index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                    text,
                    token,
                    start,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                if (index < 0)
                {
                    break;
                }

                if (!ranges.Any(range => index < range.Start + range.Length && index + token.Length > range.Start))
                {
                    ranges.Add(new HelpTextRange(index, Math.Min(token.Length, text.Length - index)));
                }

                start = index + Math.Max(1, token.Length);
            }
        }

        return ranges.OrderBy(range => range.Start).ToArray();
    }

    private static bool ContainsWord(string haystack, string token)
    {
        var index = haystack.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var afterIndex = index + token.Length;
            var after = afterIndex == haystack.Length || !char.IsLetterOrDigit(haystack[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = haystack.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        return WordRegex().Matches(value[..Math.Min(value.Length, MaximumQueryLength)])
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return WhitespaceRegex().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    private static string StripMarkdown(string markdown)
    {
        return MarkdownPunctuationRegex().Replace(markdown, " ");
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[`*_#>|\[\](){}~-]+", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownPunctuationRegex();
}

internal sealed record HelpSearchIndex(IReadOnlyList<HelpSearchIndexEntry> Articles);

internal sealed record HelpSearchIndexEntry(
    HelpArticle Article,
    string Id,
    string Title,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Headings,
    string Summary,
    string Body,
    string Searchable,
    string StrippedMarkdown);
