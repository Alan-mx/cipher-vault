namespace CipherVault.Core.Backup;

public sealed class VerifiedBackupCache
{
    private readonly int capacity;
    private readonly object sync = new();
    private readonly Dictionary<string, LinkedListNode<CacheItem>> entries =
        new(StringComparer.Ordinal);
    private readonly LinkedList<CacheItem> order = new();

    public VerifiedBackupCache(
        int capacity = BackupConstants.VerifiedCacheCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
    }

    public bool TryGet(
        string canonicalPath,
        RegularFileIdentity current,
        out VerifiedBackupCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(current);

        lock (sync)
        {
            if (!entries.TryGetValue(canonicalPath, out var node)
                || node.Value.Entry.Identity != current)
            {
                entry = null!;
                return false;
            }

            order.Remove(node);
            order.AddFirst(node);
            entry = node.Value.Entry;
            return true;
        }
    }

    public void Set(
        string canonicalPath,
        VerifiedBackupCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentNullException.ThrowIfNull(entry);

        lock (sync)
        {
            if (entries.TryGetValue(canonicalPath, out var existing))
            {
                existing.Value = new CacheItem(canonicalPath, entry);
                order.Remove(existing);
                order.AddFirst(existing);
                return;
            }

            var node = order.AddFirst(new CacheItem(canonicalPath, entry));
            entries.Add(canonicalPath, node);
            while (entries.Count > capacity)
            {
                var last = order.Last!;
                order.RemoveLast();
                entries.Remove(last.Value.CanonicalPath);
            }
        }
    }

    public void Remove(string canonicalPath)
    {
        lock (sync)
        {
            if (entries.Remove(canonicalPath, out var node))
            {
                order.Remove(node);
            }
        }
    }

    private sealed record CacheItem(
        string CanonicalPath,
        VerifiedBackupCacheEntry Entry);
}
