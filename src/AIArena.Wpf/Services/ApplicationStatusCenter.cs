using System.Collections.ObjectModel;

namespace AIArena.Wpf.Services;

public enum ApplicationStatusState
{
    Running,
    Succeeded,
    Info,
    Warning,
    Failed,
    Cancelled,
    Unconfirmed,
    Blocked
}

public enum ApplicationStatusLifetime
{
    Transient,
    UntilSuperseded,
    UntilResolved
}

public enum ApplicationStatusAnnouncement
{
    None,
    Polite,
    Assertive
}

public sealed record ApplicationStatusIdentity(
    string SessionId = "",
    string ProviderIdentity = "")
{
    public static ApplicationStatusIdentity Empty { get; } = new();

    internal ApplicationStatusIdentity Normalized() => new(
        (SessionId ?? "").Trim(),
        (ProviderIdentity ?? "").Trim());
}

public readonly record struct ApplicationStatusReceipt(
    string Key,
    long Generation,
    ApplicationStatusIdentity Identity,
    long ContextGeneration)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Key) || Generation <= 0;
}

public sealed record ApplicationStatusEntry(
    string Key,
    long Generation,
    string Source,
    ApplicationStatusState State,
    string Summary,
    string Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ApplicationStatusLifetime Lifetime,
    ApplicationStatusIdentity Identity,
    double? Progress = null,
    string? NavigationTarget = null,
    int RepeatCount = 1,
    bool IsBackground = false,
    bool IsResolved = false)
{
    public string Id => $"{Key}:{Generation}";

    public bool IsActive => !IsResolved && State == ApplicationStatusState.Running;

    public bool IsUnresolved => !IsResolved && State is
        ApplicationStatusState.Warning or
        ApplicationStatusState.Failed or
        ApplicationStatusState.Unconfirmed or
        ApplicationStatusState.Blocked;

    public bool CanClear => !IsActive && !IsUnresolved;

    public bool IsTerminal => State != ApplicationStatusState.Running;
}

public sealed record ApplicationStatusSnapshot(
    IReadOnlyList<ApplicationStatusEntry> VisibleEntries,
    IReadOnlyList<ApplicationStatusEntry> History,
    ApplicationStatusEntry Primary,
    int AdditionalCount,
    string AppStatus);

public sealed class ApplicationStatusChangedEventArgs : EventArgs
{
    internal ApplicationStatusChangedEventArgs(
        ApplicationStatusSnapshot snapshot,
        string announcement,
        ApplicationStatusAnnouncement announcementKind,
        bool expiredOnly)
    {
        Snapshot = snapshot;
        Announcement = announcement;
        AnnouncementKind = announcementKind;
        ExpiredOnly = expiredOnly;
    }

    public ApplicationStatusSnapshot Snapshot { get; }

    public string Announcement { get; }

    public ApplicationStatusAnnouncement AnnouncementKind { get; }

    public bool ExpiredOnly { get; }
}

/// <summary>
/// Process-only application operation projection. Domain coordinators remain
/// authoritative; this center coalesces, ranks, sanitizes, and presents their
/// current-run feedback without persisting it into a session.
/// </summary>
public sealed class ApplicationStatusCenter
{
    public const int CompactEntryCount = 4;
    public const int DefaultHistoryCapacity = 100;

    private static readonly TimeSpan DefaultSuccessLifetime = TimeSpan.FromSeconds(6);
    private readonly object sync = new();
    private readonly Func<DateTimeOffset> clock;
    private readonly int historyCapacity;
    private readonly TimeSpan successLifetime;
    private readonly Dictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationStatusEntry> currentByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> heartbeatStateByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApplicationStatusIdentity> heartbeatIdentityByKey = new(StringComparer.Ordinal);
    private readonly List<ApplicationStatusEntry> history = [];
    private ApplicationStatusIdentity currentIdentity = ApplicationStatusIdentity.Empty;
    private long contextGeneration;

    public ApplicationStatusCenter(
        Func<DateTimeOffset>? clock = null,
        int historyCapacity = DefaultHistoryCapacity,
        TimeSpan? successLifetime = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.historyCapacity = Math.Clamp(historyCapacity, 20, 500);
        this.successLifetime = successLifetime is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultSuccessLifetime;
    }

    public event EventHandler<ApplicationStatusChangedEventArgs>? Changed;

    public event EventHandler<string>? NavigationRequested;

    public ApplicationStatusSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public IReadOnlyList<ApplicationStatusEntry> VisibleEntries => Snapshot.VisibleEntries;

    public IReadOnlyList<ApplicationStatusEntry> History => Snapshot.History;

    /// <summary>
    /// Returns the next transient-expiration deadline without mutating status
    /// state. The presentation layer can sleep until meaningful work is due
    /// instead of polling the center every second.
    /// </summary>
    internal TimeSpan? TimeUntilNextExpiration
    {
        get
        {
            lock (sync)
            {
                DateTimeOffset? next = null;
                foreach (var entry in currentByKey.Values)
                {
                    if (entry.Lifetime != ApplicationStatusLifetime.Transient)
                    {
                        continue;
                    }

                    var deadline = entry.UpdatedAt + successLifetime;
                    if (next is null || deadline < next)
                    {
                        next = deadline;
                    }
                }

                var now = clock();
                return next is null
                    ? null
                    : next <= now
                        ? TimeSpan.Zero
                        : next - now;
            }
        }
    }

    public ApplicationStatusEntry Primary => Snapshot.Primary;

    public string AppStatus => Snapshot.AppStatus;

    public ApplicationStatusReceipt Begin(
        string key,
        string source,
        string summary,
        string? detail = null,
        double? progress = null,
        string? navigationTarget = null,
        ApplicationStatusIdentity? identity = null,
        bool background = false)
    {
        return PublishNew(
            key,
            source,
            ApplicationStatusState.Running,
            summary,
            detail,
            progress,
            navigationTarget,
            identity,
            background,
            ApplicationStatusLifetime.UntilSuperseded);
    }

    public bool Update(
        ApplicationStatusReceipt receipt,
        string summary,
        string? detail = null,
        double? progress = null) =>
        Transition(receipt, ApplicationStatusState.Running, summary, detail, progress, null);

    public bool Complete(
        ApplicationStatusReceipt receipt,
        string summary,
        string? detail = null) =>
        Transition(receipt, ApplicationStatusState.Succeeded, summary, detail, null, ApplicationStatusLifetime.Transient);

    public bool Fail(
        ApplicationStatusReceipt receipt,
        string summary,
        string? detail = null,
        bool blocked = false) =>
        Transition(
            receipt,
            blocked ? ApplicationStatusState.Blocked : ApplicationStatusState.Failed,
            summary,
            detail,
            null,
            ApplicationStatusLifetime.UntilResolved);

    public bool Cancel(
        ApplicationStatusReceipt receipt,
        string summary,
        string? detail = null) =>
        Transition(receipt, ApplicationStatusState.Cancelled, summary, detail, null, ApplicationStatusLifetime.Transient);

    public bool MarkUnconfirmed(
        ApplicationStatusReceipt receipt,
        string summary,
        string? detail = null) =>
        Transition(
            receipt,
            ApplicationStatusState.Unconfirmed,
            summary,
            detail,
            null,
            ApplicationStatusLifetime.UntilResolved);

    public ApplicationStatusReceipt PublishNotice(
        string key,
        string source,
        ApplicationStatusState state,
        string summary,
        string? detail = null,
        string? navigationTarget = null,
        ApplicationStatusIdentity? identity = null,
        bool background = false,
        ApplicationStatusLifetime? lifetime = null)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedIdentity = (identity ?? ApplicationStatusIdentity.Empty).Normalized();
        var safeSummary = Safe(summary, 180, "Status updated.");
        var safeDetail = Safe(detail, 800, "");
        ApplicationStatusChangedEventArgs? changed = null;
        ApplicationStatusReceipt receipt;

        lock (sync)
        {
            var now = clock();
            ExpireEntriesLocked(now);
            if (!CanPublishIdentityLocked(normalizedIdentity))
            {
                return default;
            }

            if (currentByKey.TryGetValue(normalizedKey, out var existing)
                && existing.State == state
                && existing.Summary.Equals(safeSummary, StringComparison.Ordinal)
                && existing.Detail.Equals(safeDetail, StringComparison.Ordinal)
                && existing.Identity == normalizedIdentity)
            {
                var repeated = existing with
                {
                    UpdatedAt = now,
                    RepeatCount = existing.RepeatCount + 1,
                    NavigationTarget = NormalizeNavigationTarget(navigationTarget) ?? existing.NavigationTarget
                };
                ReplaceLocked(existing, repeated);
                receipt = ReceiptFor(repeated);
                changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
            }
            else
            {
                receipt = PublishNewLocked(
                    normalizedKey,
                    source,
                    state,
                    safeSummary,
                    safeDetail,
                    progress: null,
                    navigationTarget,
                    normalizedIdentity,
                    background,
                    lifetime ?? LifetimeFor(state),
                    now,
                    out var entry);
                changed = ChangeArgsLocked(
                    background ? "" : AnnouncementText(entry),
                    AnnouncementFor(entry),
                    expiredOnly: false);
            }
        }

        RaiseChanged(changed);
        return receipt;
    }

    /// <summary>
    /// Returns true only when a meaningful health transition was published. The
    /// first healthy observation and repeated observations are deliberately silent.
    /// </summary>
    public bool PublishHeartbeatTransition(
        string key,
        string source,
        bool healthy,
        string? summary = null,
        string? detail = null,
        ApplicationStatusIdentity? identity = null)
    {
        var normalizedKey = NormalizeKey(key);
        var normalizedIdentity = (identity ?? ApplicationStatusIdentity.Empty).Normalized();
        var safeSummary = Safe(
            summary ?? (healthy ? $"{source} is available." : $"{source} is unavailable."),
            180,
            "Status updated.");
        var safeDetail = Safe(detail, 800, "");
        ApplicationStatusChangedEventArgs? identityChanged = null;
        ApplicationStatusChangedEventArgs? evidenceChanged = null;
        var shouldPublish = false;
        lock (sync)
        {
            if (!CanPublishIdentityLocked(normalizedIdentity))
            {
                return false;
            }

            var identityWasReplaced = heartbeatIdentityByKey.TryGetValue(normalizedKey, out var priorIdentity)
                && priorIdentity != normalizedIdentity;
            if (identityWasReplaced)
            {
                heartbeatIdentityByKey[normalizedKey] = normalizedIdentity;
                heartbeatStateByKey.Remove(normalizedKey);
                if (currentByKey.TryGetValue(normalizedKey, out var priorEntry)
                    && priorEntry.Identity != normalizedIdentity)
                {
                    currentByKey.Remove(normalizedKey);
                    ReplaceLocked(priorEntry, priorEntry with { IsResolved = true, UpdatedAt = clock() });
                    identityChanged = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
                }
            }
            else if (!heartbeatIdentityByKey.ContainsKey(normalizedKey))
            {
                heartbeatIdentityByKey[normalizedKey] = normalizedIdentity;
            }

            if (!heartbeatStateByKey.TryGetValue(normalizedKey, out var previous))
            {
                heartbeatStateByKey[normalizedKey] = healthy;
                shouldPublish = !healthy;
            }
            else if (previous != healthy)
            {
                heartbeatStateByKey[normalizedKey] = healthy;
                shouldPublish = true;
            }
            else if (!healthy
                && currentByKey.TryGetValue(normalizedKey, out var current)
                && current.Identity == normalizedIdentity
                && current.State == ApplicationStatusState.Warning
                && (!current.Summary.Equals(safeSummary, StringComparison.Ordinal)
                    || !current.Detail.Equals(safeDetail, StringComparison.Ordinal)))
            {
                var updated = current with
                {
                    Summary = safeSummary,
                    Detail = safeDetail,
                    UpdatedAt = clock(),
                    RepeatCount = current.RepeatCount + 1
                };
                ReplaceLocked(current, updated);
                evidenceChanged = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
            }
        }

        RaiseChanged(identityChanged);
        RaiseChanged(evidenceChanged);
        if (evidenceChanged is not null)
        {
            return true;
        }

        if (!shouldPublish)
        {
            return identityChanged is not null;
        }

        PublishNotice(
            normalizedKey,
            source,
            healthy ? ApplicationStatusState.Succeeded : ApplicationStatusState.Warning,
            safeSummary,
            safeDetail,
            identity: normalizedIdentity,
            background: true,
            lifetime: healthy ? ApplicationStatusLifetime.Transient : ApplicationStatusLifetime.UntilResolved);
        return true;
    }

    public bool Resolve(string key)
    {
        ApplicationStatusChangedEventArgs? changed = null;
        lock (sync)
        {
            var normalizedKey = NormalizeKey(key);
            if (!currentByKey.Remove(normalizedKey, out var existing))
            {
                return false;
            }

            ReplaceLocked(existing, existing with { IsResolved = true, UpdatedAt = clock() });
            TrimHistoryLocked();
            changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
        }

        RaiseChanged(changed);
        return true;
    }

    public bool Resolve(ApplicationStatusReceipt receipt)
    {
        ApplicationStatusChangedEventArgs? changed = null;
        lock (sync)
        {
            if (!TryCurrentLocked(receipt, out var existing))
            {
                return false;
            }

            currentByKey.Remove(existing.Key);
            ReplaceLocked(existing, existing with { IsResolved = true, UpdatedAt = clock() });
            TrimHistoryLocked();
            changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
        }

        RaiseChanged(changed);
        return true;
    }

    public int ClearCompleted()
    {
        ApplicationStatusChangedEventArgs? changed = null;
        int removed;
        lock (sync)
        {
            var clearable = history.Where(entry => entry.CanClear).ToArray();
            foreach (var entry in clearable)
            {
                if (currentByKey.TryGetValue(entry.Key, out var current)
                    && current.Generation == entry.Generation)
                {
                    currentByKey.Remove(entry.Key);
                }
            }

            removed = history.RemoveAll(entry => entry.CanClear);
            if (removed > 0)
            {
                changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
            }
        }

        RaiseChanged(changed);
        return removed;
    }

    public void SetContext(ApplicationStatusIdentity? identity)
    {
        ApplicationStatusChangedEventArgs? changed = null;
        lock (sync)
        {
            var normalized = (identity ?? ApplicationStatusIdentity.Empty).Normalized();
            if (normalized == currentIdentity)
            {
                return;
            }

            currentIdentity = normalized;
            contextGeneration++;
            var now = clock();
            foreach (var pair in currentByKey.ToArray())
            {
                var entry = pair.Value;
                if (entry.Identity == ApplicationStatusIdentity.Empty
                    || IdentityMatchesContext(entry.Identity, normalized))
                {
                    continue;
                }

                currentByKey.Remove(pair.Key);
                ReplaceLocked(entry, entry with { IsResolved = true, UpdatedAt = now });
            }

            TrimHistoryLocked();
            changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
        }

        RaiseChanged(changed);
    }

    public bool RequestNavigation(string? navigationTarget)
    {
        var safeTarget = NormalizeNavigationTarget(navigationTarget);
        if (safeTarget is null)
        {
            return false;
        }

        NavigationRequested?.Invoke(this, safeTarget);
        return true;
    }

    public bool RefreshExpirations()
    {
        ApplicationStatusChangedEventArgs? changed = null;
        bool removed;
        lock (sync)
        {
            removed = ExpireEntriesLocked(clock());
            if (removed)
            {
                TrimHistoryLocked();
                changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: true);
            }
        }

        RaiseChanged(changed);
        return removed;
    }

    private ApplicationStatusReceipt PublishNew(
        string key,
        string source,
        ApplicationStatusState state,
        string summary,
        string? detail,
        double? progress,
        string? navigationTarget,
        ApplicationStatusIdentity? identity,
        bool background,
        ApplicationStatusLifetime lifetime)
    {
        ApplicationStatusChangedEventArgs changed;
        ApplicationStatusReceipt receipt;
        lock (sync)
        {
            var now = clock();
            ExpireEntriesLocked(now);
            var normalizedIdentity = (identity ?? ApplicationStatusIdentity.Empty).Normalized();
            if (!CanPublishIdentityLocked(normalizedIdentity))
            {
                return default;
            }

            receipt = PublishNewLocked(
                NormalizeKey(key),
                source,
                state,
                Safe(summary, 180, "Status updated."),
                Safe(detail, 800, ""),
                progress,
                navigationTarget,
                normalizedIdentity,
                background,
                lifetime,
                now,
                out var entry);
            changed = ChangeArgsLocked(
                background ? "" : AnnouncementText(entry),
                AnnouncementFor(entry),
                expiredOnly: false);
        }

        RaiseChanged(changed);
        return receipt;
    }

    private ApplicationStatusReceipt PublishNewLocked(
        string key,
        string source,
        ApplicationStatusState state,
        string summary,
        string detail,
        double? progress,
        string? navigationTarget,
        ApplicationStatusIdentity identity,
        bool background,
        ApplicationStatusLifetime lifetime,
        DateTimeOffset now,
        out ApplicationStatusEntry entry)
    {
        if (currentByKey.TryGetValue(key, out var previous))
        {
            currentByKey.Remove(key);
            ReplaceLocked(previous, previous with { IsResolved = true, UpdatedAt = now });
        }

        var generation = generations.TryGetValue(key, out var priorGeneration)
            ? priorGeneration + 1
            : 1;
        generations[key] = generation;
        entry = new ApplicationStatusEntry(
            key,
            generation,
            Safe(source, 40, "App"),
            state,
            summary,
            detail,
            now,
            now,
            lifetime,
            identity,
            NormalizeProgress(progress),
            NormalizeNavigationTarget(navigationTarget),
            IsBackground: background);
        history.Insert(0, entry);
        currentByKey[key] = entry;
        TrimHistoryLocked();
        return ReceiptFor(entry);
    }

    private bool Transition(
        ApplicationStatusReceipt receipt,
        ApplicationStatusState state,
        string summary,
        string? detail,
        double? progress,
        ApplicationStatusLifetime? lifetime)
    {
        ApplicationStatusChangedEventArgs? changed = null;
        lock (sync)
        {
            if (!TryCurrentLocked(receipt, out var existing))
            {
                return false;
            }

            // A causal receipt represents one operation phase. Once that phase
            // reaches a terminal state, late progress or completion must not
            // resurrect it. A retry begins a fresh generation instead.
            if (existing.State != ApplicationStatusState.Running)
            {
                return false;
            }

            var now = clock();
            var safeSummary = Safe(summary, 180, "Status updated.");
            var safeDetail = Safe(detail, 800, existing.Detail);
            var normalizedProgress = state == ApplicationStatusState.Running
                ? NormalizeProgress(progress) ?? existing.Progress
                : null;
            var nextLifetime = lifetime ?? existing.Lifetime;
            if (existing.State == state
                && existing.Summary.Equals(safeSummary, StringComparison.Ordinal)
                && existing.Detail.Equals(safeDetail, StringComparison.Ordinal)
                && existing.Progress == normalizedProgress
                && existing.Lifetime == nextLifetime)
            {
                var repeated = existing with
                {
                    UpdatedAt = now,
                    RepeatCount = existing.RepeatCount + 1
                };
                ReplaceLocked(existing, repeated);
                changed = ChangeArgsLocked("", ApplicationStatusAnnouncement.None, expiredOnly: false);
            }
            else
            {
                var updated = existing with
                {
                    State = state,
                    Summary = safeSummary,
                    Detail = safeDetail,
                    Progress = normalizedProgress,
                    UpdatedAt = now,
                    Lifetime = nextLifetime,
                    IsResolved = false
                };
                ReplaceLocked(existing, updated);
                if (updated.IsTerminal)
                {
                    TrimHistoryLocked();
                }
                changed = ChangeArgsLocked(
                    updated.IsBackground ? "" : AnnouncementText(updated),
                    AnnouncementFor(updated),
                    expiredOnly: false);
            }
        }

        RaiseChanged(changed);
        return true;
    }

    private bool TryCurrentLocked(
        ApplicationStatusReceipt receipt,
        out ApplicationStatusEntry entry)
    {
        entry = null!;
        if (receipt.IsEmpty
            || !currentByKey.TryGetValue(receipt.Key, out var current)
            || current.Generation != receipt.Generation
            || current.Identity != receipt.Identity)
        {
            return false;
        }

        if (current.Identity != ApplicationStatusIdentity.Empty
            && currentIdentity != ApplicationStatusIdentity.Empty
            && !IdentityMatchesContext(current.Identity, currentIdentity))
        {
            return false;
        }

        entry = current;
        return true;
    }

    private void ReplaceLocked(ApplicationStatusEntry previous, ApplicationStatusEntry updated)
    {
        var index = history.FindIndex(item =>
            item.Key.Equals(previous.Key, StringComparison.Ordinal)
            && item.Generation == previous.Generation);
        if (index >= 0)
        {
            history[index] = updated;
        }

        if (currentByKey.TryGetValue(previous.Key, out var current)
            && current.Generation == previous.Generation)
        {
            currentByKey[previous.Key] = updated;
        }
    }

    private bool ExpireEntriesLocked(DateTimeOffset now)
    {
        var removed = false;
        foreach (var pair in currentByKey.ToArray())
        {
            var entry = pair.Value;
            if (entry.Lifetime != ApplicationStatusLifetime.Transient
                || now - entry.UpdatedAt < successLifetime)
            {
                continue;
            }

            currentByKey.Remove(pair.Key);
            removed = true;
        }

        return removed;
    }

    private ApplicationStatusSnapshot BuildSnapshotLocked()
    {
        var current = currentByKey.Values
            .Where(entry => !entry.IsResolved)
            .ToArray();
        var selected = current
            .OrderByDescending(Priority)
            .ThenByDescending(entry => entry.UpdatedAt)
            .Take(CompactEntryCount)
            .OrderByDescending(entry => entry.UpdatedAt)
            .ToArray();
        var primary = current
            .OrderByDescending(Priority)
            .ThenByDescending(entry => entry.UpdatedAt)
            .FirstOrDefault()
            ?? ReadyEntryLocked();
        var visible = selected.Length == 0 ? [primary] : selected;
        var historySnapshot = history
            .OrderByDescending(entry => entry.UpdatedAt)
            .ToArray();
        return new ApplicationStatusSnapshot(
            new ReadOnlyCollection<ApplicationStatusEntry>(visible),
            new ReadOnlyCollection<ApplicationStatusEntry>(historySnapshot),
            primary,
            Math.Max(0, current.Length - visible.Length),
            primary.Summary);
    }

    private ApplicationStatusEntry ReadyEntryLocked()
    {
        var now = clock();
        return new ApplicationStatusEntry(
            "app.ready",
            0,
            "App",
            ApplicationStatusState.Info,
            "Ready",
            "No meaningful application activity requires attention.",
            now,
            now,
            ApplicationStatusLifetime.UntilSuperseded,
            ApplicationStatusIdentity.Empty,
            IsResolved: false);
    }

    private ApplicationStatusChangedEventArgs ChangeArgsLocked(
        string announcement,
        ApplicationStatusAnnouncement announcementKind,
        bool expiredOnly) =>
        new(BuildSnapshotLocked(), announcement, announcementKind, expiredOnly);

    private void TrimHistoryLocked()
    {
        while (history.Count > historyCapacity)
        {
            var removable = history.FindLastIndex(entry => entry.CanClear || entry.IsResolved);
            if (removable < 0)
            {
                // The completed-history bound must never erase the only user
                // evidence for active work or unresolved outcomes.
                break;
            }

            history.RemoveAt(removable);
        }
    }

    private ApplicationStatusReceipt ReceiptFor(ApplicationStatusEntry entry) =>
        new(entry.Key, entry.Generation, entry.Identity, contextGeneration);

    private static int Priority(ApplicationStatusEntry entry)
    {
        var statePriority = entry.State switch
        {
            ApplicationStatusState.Blocked => 800,
            ApplicationStatusState.Failed => 750,
            ApplicationStatusState.Unconfirmed => 700,
            ApplicationStatusState.Running => 650,
            ApplicationStatusState.Warning => 600,
            ApplicationStatusState.Succeeded => 400,
            ApplicationStatusState.Cancelled => 350,
            _ => 300
        };
        // Polling is useful evidence, but it cannot become the headline over a
        // result from an operation the user explicitly initiated.
        if (!entry.IsBackground)
        {
            return statePriority + 25;
        }

        return entry.State switch
        {
            ApplicationStatusState.Running => 375,
            ApplicationStatusState.Warning => 575,
            ApplicationStatusState.Succeeded or
            ApplicationStatusState.Cancelled or
            ApplicationStatusState.Info => Math.Min(statePriority, 300),
            _ => statePriority
        };
    }

    private static ApplicationStatusLifetime LifetimeFor(ApplicationStatusState state) => state switch
    {
        ApplicationStatusState.Running => ApplicationStatusLifetime.UntilSuperseded,
        ApplicationStatusState.Warning or
        ApplicationStatusState.Failed or
        ApplicationStatusState.Unconfirmed or
        ApplicationStatusState.Blocked => ApplicationStatusLifetime.UntilResolved,
        _ => ApplicationStatusLifetime.Transient
    };

    private static ApplicationStatusAnnouncement AnnouncementFor(ApplicationStatusEntry entry)
    {
        if (entry.IsBackground)
        {
            return ApplicationStatusAnnouncement.None;
        }

        return entry.State == ApplicationStatusState.Blocked
            ? ApplicationStatusAnnouncement.Assertive
            : ApplicationStatusAnnouncement.Polite;
    }

    private static string AnnouncementText(ApplicationStatusEntry entry) =>
        $"{entry.Source}: {entry.Summary}";

    private static bool IdentityMatchesContext(
        ApplicationStatusIdentity entry,
        ApplicationStatusIdentity context)
    {
        var sessionMatches = entry.SessionId.Length == 0
            || context.SessionId.Length == 0
            || entry.SessionId.Equals(context.SessionId, StringComparison.Ordinal);
        var providerMatches = entry.ProviderIdentity.Length == 0
            || context.ProviderIdentity.Length == 0
            || entry.ProviderIdentity.Equals(context.ProviderIdentity, StringComparison.Ordinal);
        return sessionMatches && providerMatches;
    }

    private bool CanPublishIdentityLocked(ApplicationStatusIdentity identity) =>
        identity == ApplicationStatusIdentity.Empty
        || currentIdentity == ApplicationStatusIdentity.Empty
        || IdentityMatchesContext(identity, currentIdentity);

    private static string NormalizeKey(string? key)
    {
        var normalized = (key ?? "").Trim();
        return normalized.Length == 0 ? "app.notice" : normalized[..Math.Min(120, normalized.Length)];
    }

    private static string? NormalizeNavigationTarget(string? target)
    {
        var normalized = (target ?? "").Trim();
        if (normalized.Length == 0
            || normalized.Length > 768
            || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ':' or '/' or '%')))
        {
            return null;
        }

        return normalized;
    }

    private static double? NormalizeProgress(double? progress)
    {
        if (progress is null || double.IsNaN(progress.Value) || double.IsInfinity(progress.Value))
        {
            return null;
        }

        return Math.Clamp(progress.Value, 0, 100);
    }

    private static string Safe(string? value, int maximumLength, string fallback)
    {
        var safe = ProviderModelCatalogProjectionService.SafeStatusForDisplay(value ?? "");
        if (string.IsNullOrWhiteSpace(safe))
        {
            return fallback;
        }

        return safe.Length <= maximumLength
            ? safe
            : safe[..Math.Max(1, maximumLength - 1)].TrimEnd() + "…";
    }

    private void RaiseChanged(ApplicationStatusChangedEventArgs? args)
    {
        if (args is not null)
        {
            Changed?.Invoke(this, args);
        }
    }
}
