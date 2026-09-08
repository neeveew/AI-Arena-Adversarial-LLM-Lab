using System.IO;
using System.Text.Json;
using AIArena.Core.Models;
using AIArena.Core.Persistence;
using AIArena.Wpf.Models;
using CoreSessionSummary = AIArena.Core.Models.SessionSummary;

namespace AIArena.Wpf;

/// <summary>
/// Finds transcript matches across every stored session, not just the one on
/// screen. The cache deliberately retains only the compact fields that are
/// searchable; it never retains a full render projection or raw snapshot.
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

    /// <summary>
    /// Search-only transcript projection. Keep this contract in lockstep with
    /// <see cref="TranscriptSearchCoordinator.TranscriptMatchesSearch"/>.
    /// </summary>
    internal sealed record SearchMessage(
        int Turn,
        string Speaker,
        string SpeakerId,
        string Model,
        string Status,
        string Kind,
        string Text,
        string Reasoning,
        string InternetQuery,
        string InternetUrl,
        IReadOnlyList<string> InternetSources);

    internal sealed record CacheSnapshot(
        int SessionCount,
        int MessageCount,
        long CharacterCount,
        long SnapshotLoads,
        long CacheHits,
        long CompactProjections);

    private sealed record SearchableSession(CoreSessionSummary Summary, SnapshotStamp Generation);

    private sealed record LoadedSession(
        CoreSessionSummary Summary,
        IReadOnlyList<SearchMessage> Messages);

    private sealed class CacheEntry
    {
        public required SnapshotStamp Generation { get; init; }
        public required IReadOnlyList<SearchMessage> Messages { get; init; }
        public required int MessageCount { get; init; }
        public required long CharacterCount { get; init; }
        public required LinkedListNode<string> RecencyNode { get; init; }
    }

    internal const int DefaultMaxHits = 200;
    internal const int DefaultMaxCachedSessions = 32;
    internal const int DefaultMaxCachedMessages = 20_000;
    internal const long DefaultMaxCachedCharacters = 4L * 1024 * 1024;

    private readonly SessionStore sessionStore;
    private readonly int maxCachedSessions;
    private readonly int maxCachedMessages;
    private readonly long maxCachedCharacters;
    private readonly SnapshotStampReader stampReader;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> cacheRecency = new();
    private int retainedMessageCount;
    private long retainedCharacterCount;
    private long snapshotLoads;
    private long cacheHits;
    private long compactProjections;

    public CrossSessionSearchService(SessionStore sessionStore)
        : this(
            sessionStore,
            DefaultMaxCachedSessions,
            DefaultMaxCachedMessages,
            DefaultMaxCachedCharacters)
    {
    }

    internal CrossSessionSearchService(
        SessionStore sessionStore,
        int maxCachedSessions,
        int maxCachedMessages,
        long maxCachedCharacters,
        bool forceContentHashGeneration = false,
        Action<int>? hashChunkObserved = null,
        TimeProvider? timeProvider = null)
    {
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.maxCachedSessions = Math.Max(0, maxCachedSessions);
        this.maxCachedMessages = Math.Max(0, maxCachedMessages);
        this.maxCachedCharacters = Math.Max(0, maxCachedCharacters);
        stampReader = new SnapshotStampReader(forceContentHashGeneration, timeProvider, hashChunkObserved);
    }

    internal CacheSnapshot Diagnostics
    {
        get
        {
            lock (cacheGate)
            {
                return new CacheSnapshot(
                    cache.Count,
                    retainedMessageCount,
                    retainedCharacterCount,
                    Interlocked.Read(ref snapshotLoads),
                    Interlocked.Read(ref cacheHits),
                    Interlocked.Read(ref compactProjections));
            }
        }
    }

    /// <summary>
    /// Scans newest-modified sessions first and stops once the hit cap is
    /// reached, so a broad query on a large history stays responsive.
    /// </summary>
    public Task<IReadOnlyList<Hit>> SearchAsync(
        string query,
        int maxHits = DefaultMaxHits,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SearchCoreAsync(query, maxHits, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<Hit>> SearchCoreAsync(
        string query,
        int maxHits = DefaultMaxHits,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxHits <= 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var search = query.Trim();

        // ListSessionsAsync inspects every session to build its summaries. This
        // path needs only stable file-generation evidence, so enumerate once and
        // deserialize only cache misses.
        var sessions = EnumerateSearchableSessions(cancellationToken)
            .OrderByDescending(session => session.Summary.LastModified)
            .ToArray();
        PruneCache(sessions);

        var hits = new List<Hit>();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= maxHits)
            {
                break;
            }

            var loaded = await LoadMessagesAsync(session, cancellationToken).ConfigureAwait(false);
            CollectSessionHits(loaded.Summary, loaded.Messages, search, maxHits, hits, cancellationToken);
        }

        return hits;
    }

    /// <summary>
    /// Explicitly releases cached transcript text, for lifecycle boundaries and
    /// tests. File-generation checks still invalidate every mutated session.
    /// </summary>
    internal void ClearCache()
    {
        lock (cacheGate)
        {
            cache.Clear();
            cacheRecency.Clear();
            retainedMessageCount = 0;
            retainedCharacterCount = 0;
        }
    }

    /// <summary>
    /// Sessions that have a snapshot worth searching, described only by what the
    /// file system can answer cheaply. Counts are deliberately left at zero:
    /// nothing in the search path reads them.
    /// </summary>
    private IEnumerable<SearchableSession> EnumerateSearchableSessions(CancellationToken cancellationToken)
    {
        var sessionsRoot = NativeDataPaths.SessionsRoot(sessionStore.DataRoot);
        if (!Directory.Exists(sessionsRoot))
        {
            yield break;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(sessionsRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = DescribeSession(
                Path.GetFileName(directory),
                Path.Combine(directory, "snapshot.json"),
                cancellationToken);
            if (session is not null)
            {
                yield return session;
            }
        }
    }

    private SearchableSession? DescribeSession(
        string sessionId,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var stamp = stampReader.Capture(snapshotPath,
            () => sessionStore.SnapshotMutationGeneration(sessionId), cancellationToken);
        return stamp is { } generation
            ? new SearchableSession(new CoreSessionSummary(sessionId, snapshotPath, true, 0, 0, 0,
                generation.LastWriteTimeUtc), generation)
            : null;
    }

    private async Task<LoadedSession> LoadMessagesAsync(
        SearchableSession initial,
        CancellationToken cancellationToken)
    {
        var candidate = initial;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetCached(candidate, out var cached))
            {
                return new LoadedSession(candidate.Summary, cached);
            }

            ArenaSnapshot? snapshot;
            try
            {
                Interlocked.Increment(ref snapshotLoads);
                snapshot = await sessionStore
                    .LoadSnapshotAsync(candidate.Summary.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                RemoveCached(candidate.Summary.Id);
                return new LoadedSession(candidate.Summary, []);
            }

            if (snapshot is null)
            {
                RemoveCached(candidate.Summary.Id);
                return new LoadedSession(candidate.Summary, []);
            }

            var current = DescribeSession(
                candidate.Summary.Id,
                candidate.Summary.SnapshotPath,
                cancellationToken);
            if (current is null)
            {
                RemoveCached(candidate.Summary.Id);
                return new LoadedSession(candidate.Summary, []);
            }

            // A save raced this read. Reload once against the new generation;
            // repeated churn yields no stale result and will be retried by the
            // next search request.
            if (!GenerationMatches(candidate.Generation, current.Generation))
            {
                RemoveCached(candidate.Summary.Id);
                candidate = current;
                continue;
            }

            var messages = ProjectSearchMessages(snapshot, cancellationToken);
            Interlocked.Increment(ref compactProjections);
            StoreCached(candidate, messages);
            return new LoadedSession(candidate.Summary, messages);
        }

        return new LoadedSession(candidate.Summary, []);
    }

    private bool TryGetCached(SearchableSession session, out IReadOnlyList<SearchMessage> messages)
    {
        lock (cacheGate)
        {
            if (!cache.TryGetValue(session.Summary.Id, out var entry)
                || !GenerationMatches(entry.Generation, session.Generation))
            {
                messages = [];
                return false;
            }

            cacheRecency.Remove(entry.RecencyNode);
            cacheRecency.AddFirst(entry.RecencyNode);
            Interlocked.Increment(ref cacheHits);
            messages = entry.Messages;
            return true;
        }
    }

    private void StoreCached(SearchableSession session, IReadOnlyList<SearchMessage> messages)
    {
        var characterCount = CharacterCount(messages);
        if (maxCachedSessions == 0
            || messages.Count > maxCachedMessages
            || characterCount > maxCachedCharacters)
        {
            RemoveCached(session.Summary.Id);
            return;
        }

        lock (cacheGate)
        {
            RemoveCachedLocked(session.Summary.Id);
            var node = new LinkedListNode<string>(session.Summary.Id);
            cacheRecency.AddFirst(node);
            cache[session.Summary.Id] = new CacheEntry
            {
                Generation = session.Generation,
                Messages = messages,
                MessageCount = messages.Count,
                CharacterCount = characterCount,
                RecencyNode = node
            };
            retainedMessageCount += messages.Count;
            retainedCharacterCount += characterCount;

            while (cache.Count > maxCachedSessions
                || retainedMessageCount > maxCachedMessages
                || retainedCharacterCount > maxCachedCharacters)
            {
                var oldest = cacheRecency.Last;
                if (oldest is null)
                {
                    break;
                }

                RemoveCachedLocked(oldest.Value);
            }
        }
    }

    private void PruneCache(IReadOnlyList<SearchableSession> sessions)
    {
        var current = sessions.ToDictionary(
            session => session.Summary.Id,
            session => session.Generation,
            StringComparer.OrdinalIgnoreCase);

        lock (cacheGate)
        {
            foreach (var pair in cache.ToArray())
            {
                if (!current.TryGetValue(pair.Key, out var generation)
                    || !GenerationMatches(pair.Value.Generation, generation))
                {
                    RemoveCachedLocked(pair.Key);
                }
            }
        }
    }

    private void RemoveCached(string sessionId)
    {
        lock (cacheGate)
        {
            RemoveCachedLocked(sessionId);
        }
    }

    private void RemoveCachedLocked(string sessionId)
    {
        if (!cache.Remove(sessionId, out var entry))
        {
            return;
        }

        cacheRecency.Remove(entry.RecencyNode);
        retainedMessageCount -= entry.MessageCount;
        retainedCharacterCount -= entry.CharacterCount;
    }

    /// <summary>
    /// Native and process evidence must match exactly. Hash presence also forms
    /// part of the generation: dropping a recent hash after its stamp ages
    /// causes one bounded reload, which closes the case where an unseen rewrite
    /// collided with the recent stamp before the next search.
    /// </summary>
    private static bool GenerationMatches(SnapshotStamp expected, SnapshotStamp observed) => expected == observed;

    private static long CharacterCount(IReadOnlyList<SearchMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            total += message.Speaker.Length;
            total += message.SpeakerId.Length;
            total += message.Model.Length;
            total += message.Status.Length;
            total += message.Kind.Length;
            total += message.Text.Length;
            total += message.Reasoning.Length;
            total += message.InternetQuery.Length;
            total += message.InternetUrl.Length;
            foreach (var source in message.InternetSources)
            {
                total += source.Length;
            }
        }

        return total;
    }

    internal static IReadOnlyList<SearchMessage> ProjectSearchMessages(
        ArenaSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var messages = new SearchMessage[snapshot.Engine.Messages.Count];
        for (var index = 0; index < snapshot.Engine.Messages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = snapshot.Engine.Messages[index];
            var request = MetadataObject(message, "tool_request");
            var result = MetadataObject(message, "tool_result");
            messages[index] = new SearchMessage(
                message.Turn,
                DisplayValue(string.IsNullOrWhiteSpace(message.Speaker) ? message.SpeakerId : message.Speaker),
                DisplayValue(string.IsNullOrWhiteSpace(message.SpeakerId) ? message.Speaker : message.SpeakerId),
                DisplayValue(message.Model.Model),
                string.IsNullOrWhiteSpace(message.Status) ? "ok" : message.Status,
                string.IsNullOrWhiteSpace(message.Kind) ? "message" : message.Kind,
                message.Text,
                MetadataString(message, "reasoning_content"),
                JsonString(request, "query", JsonString(result, "query")),
                JsonString(request, "url", JsonString(result, "url")),
                ParseInternetSources(JsonProperty(result, "sources")));
        }

        return messages;
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

    private static void CollectSessionHits(
        CoreSessionSummary session,
        IReadOnlyList<SearchMessage> messages,
        string search,
        int maxHits,
        List<Hit> hits,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= maxHits)
            {
                return;
            }

            if (!MatchesSearch(message, search))
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

    internal static bool MatchesSearch(SearchMessage message, string search)
    {
        return ContainsSearch(message.Speaker, search)
            || ContainsSearch(message.SpeakerId, search)
            || ContainsSearch(message.Model, search)
            || ContainsSearch(message.Status, search)
            || ContainsSearch(message.Kind, search)
            || ContainsSearch(message.Text, search)
            || ContainsSearch(message.Reasoning, search)
            || ContainsSearch(message.InternetQuery, search)
            || ContainsSearch(message.InternetUrl, search)
            || message.InternetSources.Any(source => ContainsSearch(source, search));
    }

    private static bool ContainsSearch(string value, string search)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static JsonElement MetadataObject(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    private static string MetadataString(DialogueMessage message, string key)
    {
        return message.Metadata.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static JsonElement JsonProperty(JsonElement element, string key)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value
            : default;
    }

    private static string JsonString(JsonElement element, string key, string fallback = "")
    {
        var value = JsonProperty(element, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static IReadOnlyList<string> ParseInternetSources(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return sources.EnumerateArray()
            .Select(source =>
            {
                var title = JsonString(source, "title");
                var url = JsonString(source, "url");
                var name = JsonString(source, "source");
                var snippet = JsonString(source, "snippet");
                return string.Join(" - ", new[] { name, title, url, snippet }.Where(item => !string.IsNullOrWhiteSpace(item)));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
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
