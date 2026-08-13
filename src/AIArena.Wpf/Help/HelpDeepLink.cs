using System.Text;
using System.Text.RegularExpressions;

namespace AIArena.Wpf.Help;

internal static partial class HelpDeepLink
{
    private const int MaxRouteLength = 512;

    private static readonly HashSet<string> AllowedAppRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "app/models",
        "app/settings/provider",
        "app/match-setup",
        "app/view/arena",
        "app/status-center",
        "app/view/agent",
        "app/view/collaborate",
        "app/view/experiment",
        "app/settings/debug",
        "app/settings/internet"
    };

    public static bool TryParse(
        string? rawLink,
        IReadOnlySet<string> articleIds,
        out HelpLinkTarget target)
    {
        target = new HelpLinkTarget(HelpLinkKind.Anchor, string.Empty);
        if (string.IsNullOrWhiteSpace(rawLink))
        {
            return false;
        }

        var route = rawLink.Trim();
        if (route.Length > MaxRouteLength
            || route.Any(char.IsControl)
            || route.Contains('\\', StringComparison.Ordinal)
            || route.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (route.StartsWith('#'))
        {
            var anchor = NormalizeAnchor(route[1..]);
            if (anchor.Length == 0)
            {
                return false;
            }

            target = new HelpLinkTarget(HelpLinkKind.Anchor, $"#{anchor}", Anchor: anchor);
            return true;
        }

        if (route.StartsWith("help/", StringComparison.OrdinalIgnoreCase))
        {
            var routeBody = route[5..];
            var fragmentIndex = routeBody.IndexOf('#');
            var articleId = fragmentIndex < 0 ? routeBody : routeBody[..fragmentIndex];
            var anchor = fragmentIndex < 0 ? null : NormalizeAnchor(routeBody[(fragmentIndex + 1)..]);
            if (!StableIdRegex().IsMatch(articleId)
                || !articleIds.Contains(articleId)
                || (fragmentIndex >= 0 && string.IsNullOrWhiteSpace(anchor)))
            {
                return false;
            }

            articleId = articleIds.First(id => id.Equals(articleId, StringComparison.OrdinalIgnoreCase));
            var canonical = string.IsNullOrWhiteSpace(anchor)
                ? $"help/{articleId}"
                : $"help/{articleId}#{anchor}";
            target = new HelpLinkTarget(HelpLinkKind.Article, canonical, articleId, anchor);
            return true;
        }

        if (route.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            var canonical = AllowedAppRoutes.FirstOrDefault(item => item.Equals(route, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                return false;
            }

            target = new HelpLinkTarget(HelpLinkKind.App, canonical);
            return true;
        }

        if (Uri.TryCreate(route, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            target = new HelpLinkTarget(HelpLinkKind.External, uri.AbsoluteUri, ExternalUri: uri);
            return true;
        }

        return false;
    }

    public static string NormalizeAnchor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(character);
                pendingDash = false;
            }
            else if (character is '-' or '_' || char.IsWhiteSpace(character))
            {
                pendingDash = builder.Length > 0;
            }
        }

        return builder.ToString().Trim('-');
    }

    internal static IReadOnlySet<string> AppRoutes => AllowedAppRoutes;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();
}
