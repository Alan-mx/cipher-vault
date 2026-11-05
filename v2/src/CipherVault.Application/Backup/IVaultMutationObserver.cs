namespace CipherVault.Application.Backup;

public enum VaultMutationKind
{
    KnowledgeChanged,
    SourceChanged,
    RelationChanged,
    DraftApplied
}

public interface IVaultMutationObserver
{
    ValueTask NotifyAsync(
        VaultMutationKind kind,
        CancellationToken cancellationToken = default);
}

public static class VaultMutationObserverExtensions
{
    public static async ValueTask NotifySafelyAsync(
        this IVaultMutationObserver? observer,
        VaultMutationKind kind)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            await observer.NotifyAsync(kind, CancellationToken.None);
        }
        catch
        {
            // Backup notifications must never turn a committed mutation into
            // a failed user operation.
        }
    }
}
