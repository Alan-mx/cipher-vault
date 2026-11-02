using System.Diagnostics;
using System.Security.Cryptography;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;
using CipherVault.Core.Storage.Exports;

namespace CipherVault.Application.Exports;

public sealed class PlaintextExportService
{
    private static readonly TimeSpan TerminalAuditTimeout =
        TimeSpan.FromSeconds(2);
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly IPlaintextExportAuditFailureSink? failureSink;

    public PlaintextExportService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null)
        : this(sessionManager, timeProvider, failureSink: null)
    {
    }

    internal PlaintextExportService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider,
        IPlaintextExportAuditFailureSink? failureSink)
    {
        this.sessionManager = sessionManager
            ?? throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.failureSink = failureSink;
    }

    public async Task<PlaintextExportPreflight> PreflightAsync(
        CancellationToken cancellationToken = default) =>
        await sessionManager.ExecuteAsync(
            async (context, token) =>
                await PlaintextExportStore.PreflightAsync(
                    context,
                    token),
            cancellationToken);

    public async Task<PlaintextExportResult> WriteAsync(
        Stream destination,
        Action<PlaintextExportMetadata> onPrepared,
        VaultActorContext actor,
        CancellationToken cancellationToken = default,
        Guid? expectedSessionId = null,
        long? expectedSessionGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(onPrepared);
        ArgumentNullException.ThrowIfNull(actor);
        var exportId = Guid.NewGuid();

        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                if ((expectedSessionId is not null
                        && context.SessionId != expectedSessionId.Value)
                    || (expectedSessionGeneration is not null
                        && context.SessionGeneration
                            != expectedSessionGeneration.Value))
                {
                    throw new VaultException(
                        VaultErrorCode.Forbidden,
                        "Export ticket Session does not match the active Session.");
                }

                var preflight = await PlaintextExportStore.PreflightAsync(
                    context,
                    token);
                var exportedAt = timeProvider.GetUtcNow().ToUniversalTime();
                var metadata = new PlaintextExportMetadata(
                    exportId,
                    CreateFileName(exportedAt),
                    preflight.DocumentCount,
                    preflight.TotalContentBytes);

                await context.BeginTransactionAsync(token);
                try
                {
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "export.plaintext",
                        documentId: null,
                        revision: null,
                        result: "started",
                        details: new
                        {
                            exportId = exportId.ToString("D"),
                            phase = "started",
                            documentCount = preflight.DocumentCount,
                            totalContentBytes = preflight.TotalContentBytes
                        },
                        occurredAt: timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionNowAsync(token);
                }
                catch
                {
                    await TryRollbackAsync(context);
                    throw;
                }

                token.ThrowIfCancellationRequested();
                try
                {
                    onPrepared(metadata);
                    var result = await PlaintextExportWriter.WriteAsync(
                        context,
                        destination,
                        metadata,
                        exportedAt,
                        token);
                    _ = await TryAppendTerminalAuditAsync(
                        context,
                        actor,
                        exportId,
                        "success",
                        new
                        {
                            exportId = exportId.ToString("D"),
                            phase = "completed",
                            documentCount = result.DocumentCount,
                            totalContentBytes = result.TotalContentBytes
                        },
                        errorCode: null);
                    return result;
                }
                catch (Exception exception)
                {
                    var outcome = ClassifyOutcome(exception);
                    var errorCode = AuditErrorCode(exception);
                    _ = await TryAppendTerminalAuditAsync(
                        context,
                        actor,
                        exportId,
                        outcome,
                        new
                        {
                            exportId = exportId.ToString("D"),
                            phase = "completed",
                            errorCode
                        },
                        errorCode);
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task<bool> TryAppendTerminalAuditAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        Guid exportId,
        string outcome,
        object details,
        string? errorCode)
    {
        using var timeout = new CancellationTokenSource(
            TerminalAuditTimeout);
        try
        {
            await context.BeginTransactionAsync(timeout.Token);
            try
            {
                _ = await AuditStore.AppendAsync(
                    context,
                    actor,
                    "export.plaintext",
                    documentId: null,
                    revision: null,
                    result: outcome,
                    details,
                    timeProvider.GetUtcNow(),
                    timeout.Token);
                await context.CommitTransactionNowAsync(timeout.Token);
                return true;
            }
            catch
            {
                await TryRollbackAsync(context);
                throw;
            }
        }
        catch (Exception exception)
        {
            ReportAuditFailure(
                new PlaintextExportAuditFailure(
                    exportId,
                    PlaintextExportAuditPhase.Terminal,
                    outcome,
                    errorCode ?? "export_audit_failed",
                    exception.GetType().Name));
            return false;
        }
    }

    private async Task TryRollbackAsync(VaultOperationContext context)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(500));
        try
        {
            await context.RollbackTransactionAsync(timeout.Token);
        }
        catch
        {
            // The runtime still owns final transaction cleanup.
        }
    }

    private void ReportAuditFailure(PlaintextExportAuditFailure failure)
    {
        if (failureSink is not null)
        {
            try
            {
                failureSink.Report(failure);
            }
            catch
            {
                // Logging must not alter export or lock behavior.
            }

            return;
        }

        Trace.TraceWarning(
            "Plaintext export audit failed. Action={0} ExportId={1} Phase={2} Outcome={3} ErrorCode={4} ExceptionType={5}",
            "export.plaintext",
            failure.ExportId,
            failure.Phase,
            failure.Outcome,
            failure.ErrorCode,
            failure.ExceptionType);
    }

    private string ClassifyOutcome(Exception exception) =>
        exception is OperationCanceledException
        || exception is VaultException
        {
            Code: VaultErrorCode.VaultLocked
                    or VaultErrorCode.VaultLockFailed
        }
        || sessionManager.State is
            VaultSessionState.Locking
            or VaultSessionState.LockFailed
            ? "cancelled"
            : "failed";

    private static string AuditErrorCode(Exception exception) =>
        exception switch
        {
            OperationCanceledException => "export_cancelled",
            VaultException vaultException =>
                AuditErrorCode(vaultException.Code),
            _ => "export_generation_failed"
        };

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
            VaultErrorCode.VaultLockFailed => "vault_lock_failed",
            VaultErrorCode.VaultNotInitialized => "vault_not_initialized",
            VaultErrorCode.InvalidInput => "invalid_input",
            VaultErrorCode.StorageError => "export_generation_failed",
            VaultErrorCode.DatabaseError => "export_generation_failed",
            _ => "export_generation_failed"
        };
}
