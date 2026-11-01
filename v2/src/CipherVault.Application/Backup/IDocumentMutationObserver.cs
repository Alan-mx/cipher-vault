namespace CipherVault.Application.Backup;

public enum DocumentMutationKind
{
    Create,
    Update,
    Patch,
    Delete,
    Restore,
    Purge,
    RevisionRestore,
    Import
}

public interface IDocumentMutationObserver
{
    ValueTask NotifyAsync(
        DocumentMutationKind kind,
        CancellationToken cancellationToken = default);
}

public static class DocumentMutationObserverExtensions
{
    public static async ValueTask NotifySafelyAsync(
        this IDocumentMutationObserver? observer,
        DocumentMutationKind kind)
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
            // Backup notifications must never turn a committed document
            // mutation into a failed user operation.
        }
    }
}
