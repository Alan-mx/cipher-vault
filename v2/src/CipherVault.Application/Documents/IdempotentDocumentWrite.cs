using System.Text.Json;
using CipherVault.Application.Drafts;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;

namespace CipherVault.Application.Documents;

internal static class IdempotentDocumentWrite
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public static async Task<T> ExecuteAsync<T>(
        VaultOperationContext context,
        VaultActorContext actor,
        Guid? idempotencyKey,
        IdempotencyOperation operation,
        object request,
        IdempotencyService idempotency,
        DateTimeOffset now,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await context.BeginTransactionAsync(cancellationToken);
        string? requestHash = null;
        if (idempotencyKey is { } key)
        {
            var requestElement = JsonSerializer.SerializeToElement(
                request,
                JsonOptions);
            requestHash = IdempotencyRequestHasher.Compute(
                operation,
                actor.Kind,
                sessionId: null,
                requestElement);
            var replay = await idempotency.TryReplayAsync(
                context,
                key,
                operation,
                actor,
                sessionId: null,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                return IdempotencyService.Replay<T>(replay);
            }
        }

        var result = await action();
        if (idempotencyKey is { } saveKey && requestHash is not null)
        {
            await idempotency.SaveAsync(
                context,
                saveKey,
                operation,
                actor,
                sessionId: null,
                requestHash,
                result!,
                now,
                cancellationToken);
        }
        await context.CommitTransactionAsync(cancellationToken);
        return result;
    }
}
