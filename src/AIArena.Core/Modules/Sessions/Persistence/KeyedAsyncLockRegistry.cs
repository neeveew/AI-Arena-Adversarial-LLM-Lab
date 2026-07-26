using System.Collections.Concurrent;

namespace AIArena.Core.Persistence;

/// <summary>
/// Serializes in-process work by key without retaining every key for the lifetime
/// of the process. Entries remain alive while an owner or waiter references them
/// and are removed as soon as the final lease is released or canceled.
/// </summary>
internal sealed class KeyedAsyncLockRegistry
{
    private readonly ConcurrentDictionary<string, Entry> entries;

    public KeyedAsyncLockRegistry(IEqualityComparer<string>? comparer = null)
    {
        entries = new ConcurrentDictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
    }

    internal int EntryCount => entries.Count;

    public async ValueTask<Lease> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Entry entry;
        while (true)
        {
            entry = entries.GetOrAdd(key, static _ => new Entry());
            lock (entry)
            {
                if (entry.Removed)
                {
                    continue;
                }

                entry.ReferenceCount++;
                break;
            }
        }

        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            return new Lease(this, key, entry);
        }
        finally
        {
            if (!acquired)
            {
                ReleaseReference(key, entry, ownsSemaphore: false);
            }
        }
    }

    private void ReleaseReference(string key, Entry entry, bool ownsSemaphore)
    {
        if (ownsSemaphore)
        {
            entry.Semaphore.Release();
        }

        var dispose = false;
        lock (entry)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.Removed = true;
                if (entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                {
                    dispose = true;
                }
                else if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    // Defensive recovery: keep a still-mapped entry usable if an
                    // unexpected collection removal failure occurs.
                    entry.Removed = false;
                }
                else
                {
                    dispose = true;
                }
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Removed { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private KeyedAsyncLockRegistry? owner;
        private readonly string key;
        private readonly Entry entry;

        internal Lease(KeyedAsyncLockRegistry owner, string key, Entry entry)
        {
            this.owner = owner;
            this.key = key;
            this.entry = entry;
        }

        public void Dispose()
        {
            var leaseOwner = Interlocked.Exchange(ref owner, null);
            leaseOwner?.ReleaseReference(key, entry, ownsSemaphore: true);
        }
    }
}
