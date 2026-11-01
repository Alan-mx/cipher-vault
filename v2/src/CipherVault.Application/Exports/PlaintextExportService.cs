using System.Security.Cryptography;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;
using CipherVault.Core.Storage.Exports;

namespace CipherVault.Application.Exports;

public sealed class PlaintextExportService
{
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;

    public PlaintextExportService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager
            ?? throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PlaintextExportResult> WriteAsync(
        Stream destination,
        Action<PlaintextExportMetadata> onPrepared,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(onPrepared);
        ArgumentNullException.ThrowIfNull(actor);

        try
        {
            var result = await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    var preflight = await PlaintextExportStore.PreflightAsync(
                        context,
                        token);
                    var exportedAt = timeProvider.GetUtcNow().ToUniversalTime();
                    var metadata = new PlaintextExportMetadata(
                        CreateFileName(exportedAt),
                        preflight.DocumentCount,
                        preflight.TotalContentBytes);
                    onPrepared(metadata);
                    return await PlaintextExportWriter.WriteAsync(
                        context,
                        destination,
                        metadata,
                        exportedAt,
                        token);
                },
                cancellationToken);

            await TryAppendAuditAsync(
                actor,
                result: "success",
                new
                {
                    documentCount = result.DocumentCount,
                    totalContentBytes = result.TotalContentBytes
                });
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryAppendAuditAsync(
                actor,
                result: "cancelled",
                new { errorCode = "export_cancelled" });
            throw;
        }
        catch (VaultException exception)
        {
            await TryAppendAuditAsync(
                actor,
                result: "failed",
                new { errorCode = AuditErrorCode(exception.Code) });
            throw;
        }
        catch
        {
            await TryAppendAuditAsync(
                actor,
                result: "failed",
                new { errorCode = "export_generation_failed" });
            throw;
        }
    }

    private async Task TryAppendAuditAsync(
        VaultActorContext actor,
        string result,
        object details)
    {
        try
        {
            await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    await context.BeginTransactionAsync(token);
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "export.plaintext",
                        documentId: null,
                        revision: null,
                        result,
                        details,
                        timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionAsync(token);
                    return true;
                },
                CancellationToken.None);
        }
        catch
        {
            // Export result is already represented by the HTTP stream.
        }
    }

    private static string CreateFileName(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        var timestamp = new DateTimeOffset(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond),
            TimeSpan.Zero);
        var suffix = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        return $"cipher-vault-export-{timestamp:yyyyMMdd-HHmmss}-{suffix}.zip";
    }

    private static string AuditErrorCode(VaultErrorCode code) =>
        code switch
        {
            VaultErrorCode.ExportTooLarge => "export_too_large",
            VaultErrorCode.VaultLocked => "vault_locked",
            VaultErrorCode.VaultNotInitialized => "vault_not_initialized",
            VaultErrorCode.InvalidInput => "invalid_input",
            VaultErrorCode.StorageError => "export_generation_failed",
            VaultErrorCode.DatabaseError => "export_generation_failed",
            _ => "export_generation_failed"
        };
}
