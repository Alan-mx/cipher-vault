using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;
using CipherVault.Core.Storage.Drafts;

namespace CipherVault.Application.Drafts;

public sealed class IdempotencyService
{
    private const int ResultLimitBytes = 65_536;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static void RequireKey(Guid idempotencyKey)
    {
        if (idempotencyKey == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.IdempotencyKeyRequired,
                "An idempotency key is required.");
        }
    }

    public async Task<IdempotencyRecord?> TryReplayAsync(
        VaultOperationContext context,
        Guid idempotencyKey,
        IdempotencyOperation operation,
        VaultActorContext actor,
        Guid? sessionId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        RequireKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        var existing = await IdempotencyStore.FindAsync(
            context,
            idempotencyKey,
            operation,
            actor.Kind,
            cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(
                existing.RequestHash,
                requestHash,
                StringComparison.Ordinal)
            || existing.SessionId != sessionId)
        {
            throw IdempotencyStore.KeyReused();
        }

        return existing;
    }

    public async Task SaveAsync(
        VaultOperationContext context,
        Guid idempotencyKey,
        IdempotencyOperation operation,
        VaultActorContext actor,
        Guid? sessionId,
        string requestHash,
        object result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        RequireKey(idempotencyKey);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > ResultLimitBytes)
        {
            throw new VaultException(
                VaultErrorCode.DraftPayloadTooLarge,
                "The idempotent result exceeds the stored result limit.");
        }

        await IdempotencyStore.InsertAsync(
            context,
            new IdempotencyRecord(
                idempotencyKey,
                actor.Kind,
                operation,
                sessionId,
                requestHash,
                json,
                now,
                now.Add(Retention)),
            cancellationToken);
    }

    public Task<int> CleanupExpiredAsync(
        VaultOperationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        IdempotencyStore.DeleteExpiredAsync(context, now, cancellationToken);

    public static T Replay<T>(IdempotencyRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(record.ResultJson, JsonOptions)
                ?? throw new VaultException(
                    VaultErrorCode.CorruptVault,
                    "The idempotent result is empty.");
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "The idempotent result is invalid.",
                exception);
        }
    }
}
