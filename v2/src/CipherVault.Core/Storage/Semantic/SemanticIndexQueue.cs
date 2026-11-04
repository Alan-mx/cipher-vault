using CipherVault.Core.Database;
using CipherVault.Core.Semantic;

namespace CipherVault.Core.Storage.Semantic;

public static class SemanticIndexQueue
{
    public static async Task QueueIndexAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        string content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        if (profile is null)
        {
            return;
        }
        await SemanticIndexJobStore.UpsertIndexAsync(
            context,
            profile.ProfileId,
            documentId,
            revision,
            SemanticProfileIdentity.Sha256Hex(content),
            now,
            cancellationToken);
    }

    public static async Task QueueDeleteAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        if (profile is null)
        {
            return;
        }
        await SemanticIndexJobStore.UpsertDeleteAsync(
            context,
            profile.ProfileId,
            documentId,
            revision,
            now,
            cancellationToken);
    }

    public static async Task QueuePurgeAsync(
        VaultOperationContext context,
        Guid documentId,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        if (profile is null)
        {
            return;
        }
        await SemanticIndexJobStore.UpsertPurgeAsync(
            context,
            profile.ProfileId,
            documentId,
            revision,
            now,
            cancellationToken);
    }
}
