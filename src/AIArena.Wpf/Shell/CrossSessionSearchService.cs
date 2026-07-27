using System.IO;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using AIArena.Wpf.Services;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

/// <summary>
/// Finds transcript matches across every stored session, not just the one on
/// screen. Sessions accumulate as comparable runs, so "which run argued about
/// latency thresholds" is a question the shell should be able to answer.
/// </summary>
internal sealed class CrossSessionSearchService
{
    /// <summary>A single matching turn, attributed to the session it came from.</summary>
    internal sealed record Hit(
        string SessionId,
        DateTimeOffset SessionLastModified,
        int Turn,
        string Speaker,
        string Excerpt);

    internal const int DefaultMaxHits = 200;

    private readonly SessionStore sessionStore;

    public CrossSessionSearchService(SessionStore sessionStore)
    {
        this.sessionStore = sessionStore;
    }

    /// <summary>
    /// Scans newest-modified sessions first and stops once the hit cap is
    /// reached, so a broad query on a large history stays responsive.
    /// </summary>
    public async Task<IReadOnlyList<Hit>> SearchAsync(
        string query,
        int maxHits = DefaultMaxHits,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var search = query.Trim();
        var sessions = await sessionStore.ListSessionsAsync(cancellationToken);
        var ordered = sessions
            .Where(session => session.HasSnapshot)
            .OrderByDescending(session => session.LastModified)
            .ToArray();

        var hits = new List<Hit>();
        foreach (var session in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= maxHits)
            {
                break;
            }

            CollectSessionHits(session, await LoadMessagesAsync(session, cancellationToken), search, maxHits, hits);
        }

        return hits;
    }

    private async Task<IReadOnlyList<TranscriptMessage>> LoadMessagesAsync(
        CoreSessionSummary session,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await sessionStore.LoadSnapshotAsync(session.Id, cancellationToken);
            return snapshot is null
                ? []
                : SnapshotViewMapper.FromCore(session, snapshot).Messages;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // A corrupt or locked session should not abort the whole search.
            return [];
        }
    }

    internal static void CollectSessionHits(
        CoreSessionSummary session,
        IReadOnlyList<TranscriptMessage> messages,
        string search,
        int maxHits,
        List<Hit> hits)
    {
        foreach (var message in messages)
        {
            if (hits.Count >= maxHits)
            {
                return;
            }

            if (!TranscriptSearchCoordinator.TranscriptMatchesSearch(message, search))
            {
                continue;
            }

            hits.Add(new Hit(
                session.Id,
                session.LastModified,
                message.Turn,
                string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker,
                Excerpt(message.Text, search)));
        }
    }

    /// <summary>
    /// Returns a window of text around the first match so results read as
    /// context rather than as the opening words of every turn.
    /// </summary>
    internal static string Excerpt(string text, string search, int radius = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var index = collapsed.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return collapsed.Length <= radius * 2 ? collapsed : $"{collapsed[..(radius * 2)]}...";
        }

        var start = Math.Max(0, index - radius);
        var end = Math.Min(collapsed.Length, index + search.Length + radius);
        var window = collapsed[start..end];
        return $"{(start > 0 ? "..." : "")}{window}{(end < collapsed.Length ? "..." : "")}";
    }
}
