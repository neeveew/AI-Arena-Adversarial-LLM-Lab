namespace AIArena.Wpf;

/// <summary>
/// One action the palette can run. <paramref name="Keywords"/> carries the words
/// someone is likely to reach for that are not in the title - "dark", "light"
/// for themes, "keyboard" for the shortcut list - so the palette finds things by
/// intent rather than by exact wording.
/// </summary>
internal sealed record ShellCommand(
    string Id,
    string Title,
    string Group,
    string Keys,
    string Keywords,
    Action Invoke,
    Func<bool>? IsAvailable = null)
{
    public bool Available => IsAvailable is null || IsAvailable();
}

/// <summary>
/// Ranking for the command palette.
///
/// The app carries roughly forty features and fifteen shortcuts, and most of it
/// was previously reachable only by knowing where it lived. Matching is
/// deliberately plain - prefix beats word-prefix beats substring - because a
/// palette that reorders itself in ways the reader cannot predict is worse than
/// one that simply filters.
/// </summary>
internal static class ShellCommandPalette
{
    /// <summary>
    /// Unavailable commands are dropped rather than shown greyed out: a palette
    /// is a search surface, and offering something that cannot run wastes the
    /// reader's attention.
    /// </summary>
    public static IReadOnlyList<ShellCommand> Filter(
        IReadOnlyList<ShellCommand> commands,
        string? query,
        IReadOnlyList<string>? recentIds = null)
    {
        var available = commands.Where(command => command.Available).ToList();
        var trimmed = (query ?? "").Trim();

        // Recency only ever breaks ties. Letting it outrank relevance would mean
        // typing the exact name of a command and watching something else sit
        // above it, which is the unpredictability this ranking exists to avoid.
        int Recency(ShellCommand command)
        {
            var rank = recentIds is null ? -1 : recentIds.ToList().IndexOf(command.Id);
            return rank < 0 ? int.MaxValue : rank;
        }

        if (trimmed.Length == 0)
        {
            return available
                .Select((command, index) => (command, index))
                .OrderBy(entry => Recency(entry.command))
                .ThenBy(entry => entry.index)
                .Select(entry => entry.command)
                .ToList();
        }

        return available
            .Select((command, index) => (command, index, score: Score(command, trimmed)))
            .Where(match => match.score < int.MaxValue)
            .OrderBy(match => match.score)
            .ThenBy(match => Recency(match.command))
            .ThenBy(match => match.index)
            .Select(match => match.command)
            .ToList();
    }

    /// <summary>Lower is better; int.MaxValue means no match.</summary>
    internal static int Score(ShellCommand command, string query)
    {
        var title = command.Title;
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (StartsAWord(title, query))
        {
            return 2;
        }

        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        // "F10" or "ctrl+k" should find their command, and so should a group
        // name like "theme" when someone is browsing rather than searching.
        if (command.Keys.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Group.Contains(query, StringComparison.OrdinalIgnoreCase)
            || command.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// True when the query starts any word in the title, so "set" finds
    /// "Open Match Setup" without also matching every incidental substring.
    /// </summary>
    internal static bool StartsAWord(string title, string query)
    {
        foreach (var word in title.Split([' ', '\t', '-', '/', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
