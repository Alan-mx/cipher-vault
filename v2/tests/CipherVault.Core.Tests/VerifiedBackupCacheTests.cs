using CipherVault.Core.Backup;

namespace CipherVault.Core.Tests;

public sealed class VerifiedBackupCacheTests
{
    [Fact]
    public void IdentityChangeInvalidatesCachedVerification()
    {
        var cache = new VerifiedBackupCache(capacity: 2);
        var original = Identity("one", bytes: 10);
        cache.Set("one", Entry(original));

        Assert.True(cache.TryGet("one", original, out _));
        Assert.False(cache.TryGet("one", original with { Bytes = 11 }, out _));
    }

    [Fact]
    public void CapacityEvictsLeastRecentlyUsedEntry()
    {
        var cache = new VerifiedBackupCache(capacity: 2);
        var one = Identity("one", bytes: 1);
        var two = Identity("two", bytes: 2);
        var three = Identity("three", bytes: 3);
        cache.Set("one", Entry(one));
        cache.Set("two", Entry(two));
        Assert.True(cache.TryGet("one", one, out _));

        cache.Set("three", Entry(three));

        Assert.True(cache.TryGet("one", one, out _));
        Assert.False(cache.TryGet("two", two, out _));
        Assert.True(cache.TryGet("three", three, out _));
    }

    private static RegularFileIdentity Identity(string name, long bytes) =>
        new(
            Path.Combine(Path.GetTempPath(), name),
            bytes,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            bytes);

    private static VerifiedBackupCacheEntry Entry(
        RegularFileIdentity identity) =>
        new(
            identity,
            Guid.NewGuid(),
            new string('a', 64),
            DateTimeOffset.UnixEpoch);
}
