using System.Text;
using CipherVault.Application.Backup;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Import;
using CipherVault.Core.Storage.Documents;
using CipherVault.Core.Storage.Import;

namespace CipherVault.Application.Import;

public sealed class ImportService
{
    private const int PreviewItemLimit = 500;
    private readonly VaultSessionManager sessionManager;
    private readonly TimeProvider timeProvider;
    private readonly ImportPreviewStore previews = new();
    private readonly IDocumentMutationObserver? mutationObserver;

    public ImportService(
        VaultSessionManager sessionManager,
        TimeProvider? timeProvider = null,
        IDocumentMutationObserver? mutationObserver = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.mutationObserver = mutationObserver;
    }

    public async Task<ImportPreviewResult> PreviewAsync(
        ImportPreviewRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUserUi(actor);

        ImportPreviewStore.PreviewReservation reservation;
        try
        {
            reservation = previews.Reserve(timeProvider.GetUtcNow());
        }
        catch (InvalidOperationException)
        {
            throw new VaultException(
                VaultErrorCode.ImportPreviewInvalid,
                "Too many pending import previews.");
        }

        try
        {
            var completed = await sessionManager.ExecuteAsync(
                async (context, token) =>
                {
                    var sourceRoot = ImportSourceReader.CanonicalizeRoot(
                        request.SourceRoot);
                    var scan = await ImportSourceReader.ScanAsync(
                        sourceRoot,
                        token);
                    var items = await ClassifyAsync(context, scan, token);
                    var readyCount = items.Count(
                        item => item.State == ImportPreviewItemState.Ready);
                    var skippedCount = items.Count(
                        item => item.State == ImportPreviewItemState.SkippedConflict);
                    var rejectedCount = items.Count(
                        item => item.State == ImportPreviewItemState.Rejected);

                    await context.BeginTransactionAsync(token);
                    _ = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "import_start",
                        documentId: null,
                        revision: null,
                        "success",
                        new
                        {
                            readyCount,
                            skippedCount,
                            rejectedCount
                        },
                        timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionAsync(token);
                    return new PreviewOutcome(
                        context.SessionId,
                        sourceRoot,
                        items);
                },
                cancellationToken);

            previews.Complete(
                reservation.Token,
                completed.SourceRoot,
                completed.VaultSessionId,
                reservation.ExpiresAt,
                completed.Items);
            var visibleItems = completed.Items
                .Take(PreviewItemLimit)
                .ToArray();
            return new ImportPreviewResult(
                reservation.Token,
                reservation.ExpiresAt,
                completed.Items.Count(
                    item => item.State == ImportPreviewItemState.Ready),
                completed.Items.Count(
                    item => item.State == ImportPreviewItemState.SkippedConflict),
                completed.Items.Count(
                    item => item.State == ImportPreviewItemState.Rejected),
                visibleItems);
        }
        catch
        {
            previews.Remove(reservation.Token);
            throw;
        }
    }

    public async Task<ImportCommitResult> CommitAsync(
        ImportCommitRequest request,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUserUi(actor);
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            throw new VaultException(
                VaultErrorCode.ImportPreviewInvalid,
                "Import preview token is invalid or expired.");
        }

        var now = timeProvider.GetUtcNow();
        if (!previews.TryConsume(request.Token, now, out var preview)
            || preview is null)
        {
            throw new VaultException(
                VaultErrorCode.ImportPreviewInvalid,
                "Import preview token is invalid or expired.");
        }

        try
        {
            var sessionId = await ReadCurrentSessionIdAsync(cancellationToken);
            if (sessionId != preview.VaultSessionId || now >= preview.ExpiresAt)
            {
                throw new VaultException(
                    VaultErrorCode.ImportPreviewInvalid,
                    "Import preview token is invalid for the current Vault session.");
            }

            var sourceRoot = ImportSourceReader.CanonicalizeRoot(preview.SourceRoot);
            if (!string.Equals(
                    sourceRoot,
                    preview.SourceRoot,
                    StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.ImportSourceInvalid,
                    "Import source root changed after preview.");
            }

            var results = new List<ImportCommitItem>(preview.Items.Count);
            foreach (var item in preview.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.State == ImportPreviewItemState.SkippedConflict)
                {
                    results.Add(new ImportCommitItem(
                        item.RelativePath,
                        DocumentId: null,
                        Revision: null,
                        "skipped",
                        "DocumentPathConflict"));
                    continue;
                }
                if (item.State == ImportPreviewItemState.Rejected)
                {
                    results.Add(new ImportCommitItem(
                        item.RelativePath,
                        DocumentId: null,
                        Revision: null,
                        "failed",
                        item.Reason ?? "ImportSourceInvalid"));
                    continue;
                }

                results.Add(await ImportOneAsync(
                    preview,
                    sourceRoot,
                    item,
                    actor,
                    cancellationToken));
            }

            var imported = results.Count(item => item.Result == "imported");
            var skipped = results.Count(item => item.Result == "skipped");
            var failed = results.Count(item => item.Result == "failed");
            var auditEventId = await AppendImportAuditAsync(
                preview.VaultSessionId,
                actor,
                "import_complete",
                "success",
                new
                {
                    imported,
                    skipped,
                    failed
                },
                cancellationToken);
            var result = new ImportCommitResult(
                imported,
                skipped,
                failed,
                results,
                auditEventId);
            if (imported > 0)
            {
                await mutationObserver.NotifySafelyAsync(
                    DocumentMutationKind.Import);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            await TryAppendImportFailureAsync(
                preview.VaultSessionId,
                actor,
                "cancelled",
                "ImportCancelled");
            throw;
        }
        catch (VaultException exception)
        {
            await TryAppendImportFailureAsync(
                preview.VaultSessionId,
                actor,
                "failed",
                exception.Code.ToString());
            throw;
        }
        catch
        {
            await TryAppendImportFailureAsync(
                preview.VaultSessionId,
                actor,
                "failed",
                "DatabaseError");
            throw;
        }
    }

    private async Task<ImportCommitItem> ImportOneAsync(
        ImportPreviewStore.PendingImportPreview preview,
        string sourceRoot,
        ImportPreviewItem item,
        VaultActorContext actor,
        CancellationToken cancellationToken)
    {
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                if (context.SessionId != preview.VaultSessionId)
                {
                    throw new VaultException(
                        VaultErrorCode.ImportPreviewInvalid,
                        "Import preview token belongs to another Vault session.");
                }

                await context.BeginTransactionAsync(token);
                try
                {
                    var source = await ImportSourceReader.ReadAsync(
                        sourceRoot,
                        item.RelativePath,
                        token);
                    if (source.SizeBytes != item.SizeBytes
                        || !string.Equals(
                            source.Sha256,
                            item.Sha256,
                            StringComparison.Ordinal))
                    {
                        return FailedItem(item, "ImportSourceChanged");
                    }

                    if (!TryDecodeUtf8(source.Bytes, out var content))
                    {
                        return FailedItem(item, "InvalidUtf8");
                    }

                    var created = await DocumentMutationWriter.CreateAsync(
                        context,
                        new CreateDocumentRequest(
                            Path.GetFileNameWithoutExtension(item.RelativePath),
                            item.VirtualPath,
                            Folder: null,
                            item.Format,
                            content,
                            Tags: null),
                        actor,
                        timeProvider.GetUtcNow(),
                        token);
                    await context.CommitTransactionAsync(token);
                    return new ImportCommitItem(
                        item.RelativePath,
                        created.DocumentId,
                        created.Revision,
                        "imported",
                        ErrorCode: null);
                }
                catch (VaultException exception)
                {
                    return exception.Code == VaultErrorCode.DocumentPathConflict
                        ? new ImportCommitItem(
                            item.RelativePath,
                            DocumentId: null,
                            Revision: null,
                            "skipped",
                            exception.Code.ToString())
                        : FailedItem(item, exception.Code.ToString());
                }
            },
            cancellationToken);
    }

    private async Task<Guid> AppendImportAuditAsync(
        Guid vaultSessionId,
        VaultActorContext actor,
        string action,
        string result,
        object details,
        CancellationToken cancellationToken) =>
        await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                if (context.SessionId != vaultSessionId)
                {
                    throw new VaultException(
                        VaultErrorCode.ImportPreviewInvalid,
                        "Import preview token belongs to another Vault session.");
                }

                await context.BeginTransactionAsync(token);
                var eventId = await AuditStore.AppendAsync(
                    context,
                    actor,
                    action,
                    documentId: null,
                    revision: null,
                    result,
                    details,
                    timeProvider.GetUtcNow(),
                    token);
                await context.CommitTransactionAsync(token);
                return eventId;
            },
            cancellationToken).AsTask();

    private async Task TryAppendImportFailureAsync(
        Guid vaultSessionId,
        VaultActorContext actor,
        string result,
        string errorCode)
    {
        try
        {
            _ = await AppendImportAuditAsync(
                vaultSessionId,
                actor,
                "import_failed",
                result,
                new { errorCode },
                CancellationToken.None);
        }
        catch
        {
            // Preserve the original import failure.
        }
    }

    private async Task<Guid> ReadCurrentSessionIdAsync(
        CancellationToken cancellationToken) =>
        await sessionManager.ExecuteAsync(
            (context, _) => ValueTask.FromResult(context.SessionId),
            cancellationToken).AsTask();

    private static async Task<IReadOnlyList<ImportPreviewItem>> ClassifyAsync(
        VaultOperationContext context,
        IReadOnlyList<ImportSourceFile> scan,
        CancellationToken cancellationToken)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<PreviewCandidate>(scan.Count);
        foreach (var file in scan)
        {
            var reason = file.RejectionReason;
            if (reason is null && !seenPaths.Add(file.VirtualPath))
            {
                reason = "duplicate_path";
            }
            candidates.Add(new PreviewCandidate(file, reason));
        }

        var activePaths = await ImportConflictStore.FindActivePathsAsync(
            context,
            candidates
                .Where(candidate => candidate.Reason is null)
                .Select(candidate => candidate.File.VirtualPath),
            cancellationToken);
        return candidates.Select(candidate =>
        {
            var state = candidate.Reason switch
            {
                not null => ImportPreviewItemState.Rejected,
                _ when activePaths.Contains(candidate.File.VirtualPath) =>
                    ImportPreviewItemState.SkippedConflict,
                _ => ImportPreviewItemState.Ready
            };
            var reason = state switch
            {
                ImportPreviewItemState.Rejected => candidate.Reason,
                ImportPreviewItemState.SkippedConflict => "active_path_conflict",
                _ => null
            };
            return new ImportPreviewItem(
                candidate.File.RelativePath,
                candidate.File.VirtualPath,
                candidate.File.Format,
                candidate.File.SizeBytes,
                candidate.File.Sha256,
                state,
                reason);
        }).ToArray();
    }

    private static bool TryDecodeUtf8(byte[] bytes, out string content)
    {
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                span = span[3..];
            }

            content = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            content = string.Empty;
            return false;
        }
    }

    private static ImportCommitItem FailedItem(
        ImportPreviewItem item,
        string errorCode) =>
        new(
            item.RelativePath,
            DocumentId: null,
            Revision: null,
            "failed",
            errorCode);

    private static void EnsureUserUi(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the UserUi actor can import files.");
        }
    }

    private sealed record PreviewOutcome(
        Guid VaultSessionId,
        string SourceRoot,
        IReadOnlyList<ImportPreviewItem> Items);

    private sealed record PreviewCandidate(
        ImportSourceFile File,
        string? Reason);
}
