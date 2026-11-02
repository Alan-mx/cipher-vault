using CipherVault.Application.Exports;

namespace CipherVault.Server.Api;

internal sealed class PlaintextExportAuditFailureSink(
    ILogger<PlaintextExportAuditFailureSink> logger)
    : IPlaintextExportAuditFailureSink
{
    public void Report(PlaintextExportAuditFailure failure)
    {
        logger.LogWarning(
            "Plaintext export audit failed. Action={Action} ExportId={ExportId} Phase={Phase} Outcome={Outcome} ErrorCode={ErrorCode} ExceptionType={ExceptionType}",
            "export.plaintext",
            failure.ExportId,
            failure.Phase,
            failure.Outcome,
            failure.ErrorCode,
            failure.ExceptionType);
    }
}
