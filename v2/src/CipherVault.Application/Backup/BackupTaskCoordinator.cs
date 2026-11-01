namespace CipherVault.Application.Backup;

internal sealed class BackupTaskCoordinator
{
    private int entered;

    public bool TryEnter() =>
        Interlocked.CompareExchange(ref entered, 1, 0) == 0;

    public void Exit() =>
        Volatile.Write(ref entered, 0);
}
