namespace CipherVault.Application.Vault;

internal sealed record VaultActivitySnapshot(
    long MonotonicTimestamp,
    DateTimeOffset LastActivityAtUtc,
    long Generation);

internal sealed class VaultActivityState
{
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private long monotonicTimestamp;
    private DateTimeOffset lastActivityAtUtc;
    private long generation;

    internal VaultActivityState(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        monotonicTimestamp = timeProvider.GetTimestamp();
        lastActivityAtUtc = timeProvider.GetUtcNow();
    }

    internal VaultActivitySnapshot Snapshot()
    {
        lock (sync)
        {
            return new VaultActivitySnapshot(
                monotonicTimestamp,
                lastActivityAtUtc,
                generation);
        }
    }

    internal VaultActivitySnapshot Refresh()
    {
        lock (sync)
        {
            monotonicTimestamp = timeProvider.GetTimestamp();
            lastActivityAtUtc = timeProvider.GetUtcNow();
            generation++;
            return new VaultActivitySnapshot(
                monotonicTimestamp,
                lastActivityAtUtc,
                generation);
        }
    }
}
