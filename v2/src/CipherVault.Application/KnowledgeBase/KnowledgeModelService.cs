using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CipherVault.Application.KnowledgeBase;

public sealed class KnowledgeModelService(
    VaultSessionManager sessionManager,
    TimeProvider timeProvider)
{
    private const int ResultLimitBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<KnowledgeModelSnapshot> GetAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) => await KnowledgeModelStore.GetAsync(
                context,
                token),
            cancellationToken).AsTask();

    public async Task<KnowledgeModelSnapshot> ReplaceAsync(
        int expectedVersion,
        KnowledgeModelUpdate update,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (idempotencyKey == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.IdempotencyKeyRequired,
                "An idempotency key is required.");
        }
        var requestHash = RequestHash(expectedVersion, update);
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var existing =
                    await KnowledgeBaseIdempotencyStore.FindAsync(
                        context,
                        idempotencyKey,
                        "knowledge_model.change",
                        actor.Kind,
                        timeProvider.GetUtcNow(),
                        token);
                if (existing is not null)
                {
                    if (!string.Equals(
                            existing.RequestHash,
                            requestHash,
                            StringComparison.Ordinal))
                    {
                        throw KnowledgeBaseIdempotencyStore.KeyReused();
                    }
                    return DeserializeResult(existing.ResultJson);
                }

                await context.BeginTransactionAsync(token);
                var now = timeProvider.GetUtcNow();
                var model = await KnowledgeModelStore.ReplaceAsync(
                    context,
                    expectedVersion,
                    update,
                    now,
                    token);
                var resultJson = JsonSerializer.Serialize(model, JsonOptions);
                if (Encoding.UTF8.GetByteCount(resultJson) > ResultLimitBytes)
                {
                    throw new VaultException(
                        VaultErrorCode.PayloadTooLarge,
                        "Knowledge Model result exceeds the storage limit.");
                }
                await KnowledgeBaseIdempotencyStore.InsertAsync(
                    context,
                    new KnowledgeBaseIdempotencyRecord(
                        idempotencyKey,
                        actor.Kind == VaultActorKind.UserUi
                            ? "user_ui"
                            : "mcp",
                        "knowledge_model.change",
                        requestHash,
                        resultJson,
                        now,
                        now.AddDays(30)),
                    token);
                await context.CommitTransactionAsync(token);
                return model;
            },
            cancellationToken).AsTask();
    }

    private static string RequestHash(
        int expectedVersion,
        KnowledgeModelUpdate update)
    {
        var json = JsonSerializer.Serialize(
            new { expectedVersion, update },
            JsonOptions);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static KnowledgeModelSnapshot DeserializeResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<KnowledgeModelSnapshot>(
                    json,
                    JsonOptions)
                ?? throw new JsonException("Result was empty.");
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Knowledge Model idempotency result is invalid.",
                exception);
        }
    }
}
