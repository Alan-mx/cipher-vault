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
                    var manifest = await ImportManifestReader.ReadAsync(
                        sourceRoot,
                        token);
                    var items = await ClassifyAsync(
                        context,
                        sourceRoot,
                        scan,
                        manifest,
                        token);
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
                        manifest?.ManifestSha256,
                        items);
                },
                cancellationToken);

            previews.Complete(
                reservation.Token,
                completed.SourceRoot,
                completed.VaultSessionId,
                reservation.ExpiresAt,
                completed.ManifestSha256,
                completed.Items);
            var visibleItems = completed.Items
                .Take(PreviewItemLimit)
                .ToArray();
            return new ImportPreviewResult(
                reservation.Token,
                reservation.ExpiresAt,
                ManifestVersion: completed.ManifestSha256 is null ? null : 2,
                ManifestSha256: completed.ManifestSha256,
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
            var manifest = await ImportManifestReader.ReadAsync(
                sourceRoot,
                cancellationToken);
            if (!string.Equals(
                    manifest?.ManifestSha256,
                    preview.ManifestSha256,
                    StringComparison.Ordinal))
            {
                throw InvalidManifest(
                    "Import Manifest changed after preview.");
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
                        item.Reason ?? "active_path_conflict"));
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
                    manifest,
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
        ImportManifest? manifest,
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

                    if (item.DocumentId is not { } documentId)
                    {
                        return FailedItem(item, "invalid_document_id");
                    }
                    var manifestDocument = default(ImportManifestDocument);
                    if (manifest is not null
                        && !manifest.DocumentsByPath.TryGetValue(
                            item.VirtualPath,
                            out manifestDocument))
                    {
                        return FailedItem(item, "invalid_manifest");
                    }
                    if (manifestDocument is not null
                        && (manifestDocument.DocumentId != documentId
                            || manifestDocument.Format != item.Format
                            || manifestDocument.Bytes != source.SizeBytes
                            || !string.Equals(
                                manifestDocument.Sha256,
                                source.Sha256,
                                StringComparison.Ordinal)))
                    {
                        return FailedItem(item, "invalid_manifest");
                    }
                    var contentHash = DocumentContentFingerprint.Compute(
                        content,
                        item.Format);
                    if (!string.Equals(
                            contentHash,
                            item.ContentHash,
                            StringComparison.Ordinal))
                    {
                        return FailedItem(item, "import_source_changed");
                    }
                    if (item.Format == VaultDocumentFormat.Markdown)
                    {
                        var identity = DocumentIdentity.InspectMarkdown(content);
                        if (item.IdentitySource
                                == ImportIdentitySource.Frontmatter
                            && identity.DocumentId != documentId)
                        {
                            throw new VaultException(
                                VaultErrorCode.DocumentIdMismatch,
                                "Imported Markdown identity no longer matches Preview.");
                        }
                        if (item.IdentitySource
                                == ImportIdentitySource.Generated
                            && identity.HasId)
                        {
                            throw new VaultException(
                                VaultErrorCode.DocumentIdMismatch,
                                "Imported Markdown gained an unexpected identity.");
                        }
                    }

                    var existing = await ImportConflictStore.FindExistingAsync(
                        context,
                        [documentId],
                        [item.VirtualPath],
                        token);
                    if (existing.TrashedIds.Contains(documentId))
                    {
                        throw new VaultException(
                            VaultErrorCode.TrashedDocumentIdConflict,
                            "Imported document identity belongs to a trashed document.");
                    }
                    if (existing.ActiveById.ContainsKey(documentId))
                    {
                        throw new VaultException(
                            VaultErrorCode.DocumentIdConflict,
                            "Imported document identity is already active.");
                    }
                    if (existing.ActivePaths.Contains(item.VirtualPath))
                    {
                        throw new VaultException(
                            VaultErrorCode.DocumentPathConflict,
                            "Imported document path is already active.");
                    }

                    var created = await DocumentMutationWriter.CreateTrustedAsync(
                        context,
                        documentId,
                        new CreateDocumentRequest(
                            item.Title,
                            item.VirtualPath,
                            Folder: null,
                            item.Format,
                            content,
                            Tags: null),
                        actor,
                        timeProvider.GetUtcNow(),
                        DocumentContentLimitMode.ExistingDocumentAuthoring,
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
        string sourceRoot,
        IReadOnlyList<ImportSourceFile> scan,
        ImportManifest? manifest,
        CancellationToken cancellationToken)
    {
        if (manifest is not null)
        {
            var scannedPaths = scan
                .Select(file => file.VirtualPath)
                .ToHashSet(StringComparer.Ordinal);
            if (!scannedPaths.SetEquals(manifest.DocumentsByPath.Keys))
            {
                throw InvalidManifest(
                    "Manifest document paths do not match the import source.");
            }
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<PreviewCandidate>(scan.Count);
        foreach (var file in scan)
        {
            var reason = file.RejectionReason;
            if (reason is null && !seenPaths.Add(file.VirtualPath))
            {
                reason = "duplicate_path";
            }
            ImportManifestDocument? manifestDocument = null;
            if (manifest is not null)
            {
                _ = manifest.DocumentsByPath.TryGetValue(
                    file.VirtualPath,
                    out manifestDocument);
            }
            candidates.Add(await BuildPreviewCandidateAsync(
                sourceRoot,
                file,
                reason,
                manifestDocument,
                cancellationToken));
        }

        var duplicateIds = candidates
            .Where(candidate =>
                candidate.Reason is null && candidate.DocumentId is not null)
            .GroupBy(candidate => candidate.DocumentId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var existing = await ImportConflictStore.FindExistingAsync(
            context,
            candidates
                .Where(candidate =>
                    candidate.Reason is null
                    && candidate.DocumentId is not null)
                .Select(candidate => candidate.DocumentId!.Value),
            candidates
                .Where(candidate => candidate.Reason is null)
                .Select(candidate => candidate.File.VirtualPath),
            cancellationToken);

        return candidates.Select(candidate =>
        {
            var state = ImportPreviewItemState.Rejected;
            var reason = candidate.Reason;
            if (reason is null
                && candidate.DocumentId is { } documentId
                && duplicateIds.Contains(documentId))
            {
                reason = "duplicate_document_id_in_import";
            }
            else if (reason is null
                     && candidate.DocumentId is { } trashedId
                     && existing.TrashedIds.Contains(trashedId))
            {
                reason = "trashed_document_id_conflict";
            }
            else if (reason is null
                     && candidate.DocumentId is { } existingId
                     && existing.ActiveById.TryGetValue(
                         existingId,
                         out var active))
            {
                if (active.Format == candidate.File.Format
                    && string.Equals(
                        active.VirtualPath,
                        candidate.File.VirtualPath,
                        StringComparison.Ordinal)
                    && string.Equals(
                        active.ContentHash,
                        candidate.ContentHash,
                        StringComparison.Ordinal))
                {
                    state = ImportPreviewItemState.SkippedConflict;
                    reason = "duplicate_document_id";
                }
                else
                {
                    reason = "document_id_conflict";
                }
            }
            else if (reason is null
                     && existing.ActivePaths.Contains(
                         candidate.File.VirtualPath))
            {
                state = ImportPreviewItemState.SkippedConflict;
                reason = "active_path_conflict";
            }
            else if (reason is null)
            {
                state = ImportPreviewItemState.Ready;
            }

            return new ImportPreviewItem(
                candidate.File.RelativePath,
                candidate.File.VirtualPath,
                candidate.Title,
                candidate.File.Format,
                candidate.File.SizeBytes,
                candidate.File.Sha256,
                candidate.DocumentId,
                candidate.IdentitySource,
                candidate.ContentHash,
                state,
                reason);
        }).ToArray();
    }

    private static async Task<PreviewCandidate> BuildPreviewCandidateAsync(
        string sourceRoot,
        ImportSourceFile file,
        string? reason,
        ImportManifestDocument? manifestDocument,
        CancellationToken cancellationToken)
    {
        var title = manifestDocument?.Title ?? file.Title;
        if (reason is not null)
        {
            return new PreviewCandidate(
                file,
                title,
                null,
                null,
                null,
                reason);
        }

        ImportSourceContent source;
        try
        {
            source = await ImportSourceReader.ReadAsync(
                sourceRoot,
                file.RelativePath,
                cancellationToken);
        }
        catch (VaultException exception)
        {
            return new PreviewCandidate(
                file,
                title,
                null,
                null,
                null,
                ImportReason(exception.Code));
        }
        if (source.SizeBytes != file.SizeBytes
            || !string.Equals(
                source.Sha256,
                file.Sha256,
                StringComparison.Ordinal))
        {
            return new PreviewCandidate(
                file,
                title,
                null,
                null,
                null,
                "import_source_changed");
        }
        if (!TryDecodeUtf8(source.Bytes, out var content))
        {
            return new PreviewCandidate(
                file,
                title,
                null,
                null,
                null,
                "invalid_utf8");
        }

        string contentHash;
        DocumentFrontMatterIdentity? identity = null;
        try
        {
            contentHash = DocumentContentFingerprint.Compute(
                content,
                file.Format);
            if (file.Format == VaultDocumentFormat.Markdown)
            {
                identity = DocumentIdentity.InspectMarkdown(content);
            }
        }
        catch (VaultException exception)
        {
            return new PreviewCandidate(
                file,
                title,
                null,
                null,
                null,
                ImportReason(exception.Code));
        }
        Guid? documentId;
        ImportIdentitySource identitySource;
        var identityReason = (string?)null;
        if (file.Format == VaultDocumentFormat.Markdown)
        {
            var markdownIdentity = identity
                ?? throw new InvalidOperationException(
                    "Markdown identity was not inspected.");
            if (manifestDocument is not null)
            {
                if (manifestDocument.Format != file.Format
                    || manifestDocument.Bytes != source.SizeBytes
                    || !string.Equals(
                        manifestDocument.Sha256,
                        source.Sha256,
                        StringComparison.Ordinal))
                {
                    return new PreviewCandidate(
                        file,
                        title,
                        manifestDocument.DocumentId,
                        ImportIdentitySource.Frontmatter,
                        contentHash,
                        "invalid_manifest");
                }
                if (!markdownIdentity.HasId)
                {
                    return new PreviewCandidate(
                        file,
                        title,
                        manifestDocument.DocumentId,
                        ImportIdentitySource.Frontmatter,
                        contentHash,
                        "invalid_manifest");
                }
                if (markdownIdentity.DocumentId != manifestDocument.DocumentId)
                {
                    return new PreviewCandidate(
                        file,
                        title,
                        markdownIdentity.DocumentId,
                        ImportIdentitySource.Frontmatter,
                        contentHash,
                        "document_id_mismatch");
                }
                documentId = manifestDocument.DocumentId;
                identitySource = ImportIdentitySource.Frontmatter;
            }
            else if (markdownIdentity.HasId)
            {
                documentId = markdownIdentity.DocumentId;
                identitySource = ImportIdentitySource.Frontmatter;
            }
            else
            {
                documentId = Guid.NewGuid();
                identitySource = ImportIdentitySource.Generated;
            }
        }
        else if (manifestDocument is not null)
        {
            if (manifestDocument.Format != file.Format
                || manifestDocument.Bytes != source.SizeBytes
                || !string.Equals(
                    manifestDocument.Sha256,
                    source.Sha256,
                    StringComparison.Ordinal))
            {
                identityReason = "invalid_manifest";
            }
            documentId = manifestDocument.DocumentId;
            identitySource = ImportIdentitySource.Manifest;
        }
        else
        {
            documentId = Guid.NewGuid();
            identitySource = ImportIdentitySource.Generated;
        }

        return new PreviewCandidate(
            file,
            title,
            documentId,
            identitySource,
            contentHash,
            identityReason);
    }

    private static string ImportReason(VaultErrorCode code) =>
        code switch
        {
            VaultErrorCode.InvalidDocumentId => "invalid_document_id",
            VaultErrorCode.DocumentIdMismatch => "document_id_mismatch",
            VaultErrorCode.InvalidManifest => "invalid_manifest",
            VaultErrorCode.UnsupportedManifestVersion =>
                "unsupported_manifest_version",
            _ => code.ToString()
        };

    private static VaultException InvalidManifest(string message) =>
        new(VaultErrorCode.InvalidManifest, message);

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
        string? ManifestSha256,
        IReadOnlyList<ImportPreviewItem> Items);

    private sealed record PreviewCandidate(
        ImportSourceFile File,
        string Title,
        Guid? DocumentId,
        ImportIdentitySource? IdentitySource,
        string? ContentHash,
        string? Reason);
}
