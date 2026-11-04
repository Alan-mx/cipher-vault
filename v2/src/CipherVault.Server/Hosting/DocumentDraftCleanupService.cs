using CipherVault.Application.Drafts;
using CipherVault.Application.Relations;
using CipherVault.Core.Errors;

namespace CipherVault.Server.Hosting;

public sealed class DocumentDraftCleanupService(
    DocumentDraftService drafts,
    RelationMaintenanceService relations,
    ILogger<DocumentDraftCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                _ = await drafts.CleanupAsync(stoppingToken);
                _ = await relations.CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (VaultException exception)
                when (exception.Code is
                    VaultErrorCode.VaultLocked
                    or VaultErrorCode.VaultNotInitialized
                    or VaultErrorCode.VaultBusy)
            {
                // Cleanup runs only while the Vault is available.
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Document draft cleanup failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
