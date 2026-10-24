using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Storage;

internal static class DocumentWriter
{
    internal const int MaxContentBytes = 256 * 1024;
    private const int MaxDocumentBytes = 4 * 1024 * 1024;
    private const int MaxTags = 50;
    private static readonly TimeSpan RevisionLifetime = TimeSpan.FromDays(90);

    public static async Task<VaultWriteResult> CreateAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        string title,
        string? folder,
        string format,
        string content,
        IReadOnlyList<string>? tags,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        Validate(title, format, content, tags);
        var normalizedTags = (tags ?? [])
            .Select(VirtualPathPolicy.NormalizeTag)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (normalizedTags.Count > MaxTags)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "A document may have at most 50 tags.");
        }

        var (sourcePath, sourcePathNorm) = VirtualPathPolicy.BuildDocumentPath(title, folder, format);
        var headingsJson = format == "md"
            ? MarkdownHeadings.Extract(content)
            : "[]";
        var tagsJson = JsonSerializer.Serialize(normalizedTags);
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeMilliseconds();
        var hmac = DocumentHmac.Compute(
            integrityKey,
            documentIdValue,
            1,
            title,
            sourcePath,
            format,
            headingsJson,
            tagsJson,
            content);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT INTO documents(
                        document_id, title, title_norm, source_path, source_path_norm,
                        link_path_norm, format,
                        content, headings_json, tags_json, payload_hmac, revision,
                        created_at_ms, updated_at_ms, deleted_at_ms, purge_after_ms)
                    VALUES(
                        $id, $title, $titleNorm, $path, $pathNorm, $linkPathNorm, $format,
                        $content, $headings, $tags, $hmac, 1,
                        $created, $updated, NULL, NULL);
                    """;
                command.Parameters.AddWithValue("$id", documentIdValue);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue(
                    "$titleNorm",
                    DocumentLinkKeys.NormalizeTitle(title));
                command.Parameters.AddWithValue("$path", sourcePath);
                command.Parameters.AddWithValue("$pathNorm", sourcePathNorm);
                command.Parameters.AddWithValue(
                    "$linkPathNorm",
                    DocumentLinkKeys.NormalizeDocumentPath(sourcePath));
                command.Parameters.AddWithValue("$format", format);
                command.Parameters.AddWithValue("$content", content);
                command.Parameters.AddWithValue("$headings", headingsJson);
                command.Parameters.AddWithValue("$tags", tagsJson);
                command.Parameters.AddWithValue("$hmac", hmac);
                command.Parameters.AddWithValue("$created", timestamp);
                command.Parameters.AddWithValue("$updated", timestamp);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertRevisionAsync(
                connection,
                (SqliteTransaction)transaction,
                documentIdValue,
                title,
                sourcePath,
                format,
                content,
                headingsJson,
                tagsJson,
                hmac,
                1,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await UpsertTagsAsync(
                connection,
                (SqliteTransaction)transaction,
                documentIdValue,
                normalizedTags,
                cancellationToken).ConfigureAwait(false);
            await InsertFtsAsync(
                connection,
                (SqliteTransaction)transaction,
                documentIdValue,
                title,
                sourcePath,
                headingsJson,
                tagsJson,
                content,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                (SqliteTransaction)transaction,
                documentId,
                1,
                title,
                sourcePath,
                format,
                content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.ReconcileAsync(
                connection,
                (SqliteTransaction)transaction,
                DocumentLinkIndexer.ResolutionKeys(title, sourcePath),
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                (SqliteTransaction)transaction,
                auditContext,
                sessionId,
                "create",
                documentId,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultWriteResult(documentId, 1, now);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new VaultException(
                VaultErrorCode.Conflict,
                "An active document already uses this virtual path.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultWriteResult> AppendAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int expectedRevision,
        string appendedContent,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(appendedContent) > MaxContentBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Appended content exceeds 256 KB.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(VaultErrorCode.Conflict, "Document revision changed.");
            }

            var content = current.Content + appendedContent;
            if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
            {
                throw new VaultException(VaultErrorCode.InvalidInput, "Document exceeds 4 MB.");
            }

            var newRevision = current.Revision + 1;
            var headingsJson = current.Format == "md"
                ? MarkdownHeadings.Extract(content)
                : "[]";
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentIdValue,
                newRevision,
                current.Title,
                current.SourcePath,
                current.Format,
                headingsJson,
                current.TagsJson,
                content);

            await UpdateCurrentAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                newRevision,
                headingsJson,
                hmac,
                content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                current.SourcePath,
                current.Format,
                content,
                headingsJson,
                current.TagsJson,
                hmac,
                newRevision,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await ReplaceFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                current.SourcePath,
                headingsJson,
                current.TagsJson,
                content,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                sqliteTransaction,
                documentId,
                newRevision,
                current.Title,
                current.SourcePath,
                current.Format,
                content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                auditContext,
                sessionId,
                "append",
                documentId,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultWriteResult(documentId, newRevision, now);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultWriteResult> UpdateAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int expectedRevision,
        VaultNotePatch patch,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        if (patch.Title is null &&
            patch.Folder is null &&
            patch.Content is null &&
            patch.Tags is null)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Update patch is empty.");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(VaultErrorCode.Conflict, "Document revision changed.");
            }

            var newTitle = patch.Title ?? current.Title;
            if (newTitle.Length is < 1 or > 200)
            {
                throw new VaultException(VaultErrorCode.InvalidInput, "Title must contain 1 to 200 characters.");
            }
            var newContent = patch.Content ?? current.Content;
            if (patch.Content is not null &&
                Encoding.UTF8.GetByteCount(newContent) > MaxContentBytes)
            {
                throw new VaultException(VaultErrorCode.InvalidInput, "Document content exceeds 256 KB.");
            }
            if (Encoding.UTF8.GetByteCount(newContent) > MaxDocumentBytes)
            {
                throw new VaultException(VaultErrorCode.InvalidInput, "Document exceeds 4 MB.");
            }

            var currentFolder = current.SourcePath.Contains('/')
                ? current.SourcePath[..current.SourcePath.LastIndexOf('/')]
                : null;
            var newFolder = patch.Folder ?? currentFolder;
            var (sourcePath, sourcePathNorm) = VirtualPathPolicy.BuildDocumentPath(
                newTitle,
                newFolder,
                current.Format);
            var normalizedTags = patch.Tags is null
                ? ParseTags(current.TagsJson)
                : NormalizeTags(patch.Tags);
            var tagsJson = JsonSerializer.Serialize(normalizedTags);
            var headingsJson = patch.Content is not null && current.Format == "md"
                ? MarkdownHeadings.Extract(newContent)
                : current.HeadingsJson;
            var newRevision = current.Revision + 1;
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentIdValue,
                newRevision,
                newTitle,
                sourcePath,
                current.Format,
                headingsJson,
                tagsJson,
                newContent);

            await UpdateCurrentFullAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                newTitle,
                sourcePath,
                sourcePathNorm,
                newRevision,
                headingsJson,
                tagsJson,
                hmac,
                newContent,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                newTitle,
                sourcePath,
                current.Format,
                newContent,
                headingsJson,
                tagsJson,
                hmac,
                newRevision,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await ReplaceTagsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                normalizedTags,
                cancellationToken).ConfigureAwait(false);
            await ReplaceFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                newTitle,
                sourcePath,
                headingsJson,
                tagsJson,
                newContent,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.RecordAliasesAsync(
                connection,
                sqliteTransaction,
                documentId,
                current.Title,
                current.SourcePath,
                newTitle,
                sourcePath,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                sqliteTransaction,
                documentId,
                newRevision,
                newTitle,
                sourcePath,
                current.Format,
                newContent,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.ReconcileAsync(
                connection,
                sqliteTransaction,
                DocumentLinkIndexer.ResolutionKeys(
                    current.Title,
                    current.SourcePath)
                    .Concat(DocumentLinkIndexer.ResolutionKeys(
                        newTitle,
                        sourcePath)),
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                auditContext,
                sessionId,
                "update",
                documentId,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultWriteResult(documentId, newRevision, now);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new VaultException(
                VaultErrorCode.Conflict,
                "An active document already uses this virtual path.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultPatchResult> PatchAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int expectedRevision,
        VaultNotePatchCommand command,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(
                    VaultErrorCode.Conflict,
                    "Document revision changed.");
            }
            if (current.Format != "md")
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Precise patching is supported only for Markdown documents.");
            }

            var computation = DocumentPatchEngine.Apply(
                current.Content,
                command);
            if (command.DryRun || !computation.Changed)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new VaultPatchResult(
                    documentId,
                    current.Revision,
                    current.Revision,
                    command.DryRun,
                    computation.Changed,
                    computation.UnifiedDiff,
                    computation.DiffTruncated,
                    true);
            }

            if (Encoding.UTF8.GetByteCount(computation.Content) > MaxDocumentBytes)
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Document exceeds 4 MB.");
            }
            var newContent = computation.Content;
            var headingsJson = MarkdownHeadings.Extract(newContent);
            var newRevision = current.Revision + 1;
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentIdValue,
                newRevision,
                current.Title,
                current.SourcePath,
                current.Format,
                headingsJson,
                current.TagsJson,
                newContent);
            var sourcePathNorm = current.SourcePath
                .Normalize(NormalizationForm.FormC)
                .ToLowerInvariant();

            await UpdateCurrentFullAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                current.SourcePath,
                sourcePathNorm,
                newRevision,
                headingsJson,
                current.TagsJson,
                hmac,
                newContent,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                current.SourcePath,
                current.Format,
                newContent,
                headingsJson,
                current.TagsJson,
                hmac,
                newRevision,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await ReplaceFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                current.SourcePath,
                headingsJson,
                current.TagsJson,
                newContent,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                sqliteTransaction,
                documentId,
                newRevision,
                current.Title,
                current.SourcePath,
                current.Format,
                newContent,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                auditContext,
                sessionId,
                "patch",
                documentId,
                "success",
                null,
                JsonSerializer.Serialize(new
                {
                    targetKind = command.TargetKind,
                    operation = command.Operation
                }),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultPatchResult(
                documentId,
                current.Revision,
                newRevision,
                false,
                true,
                computation.UnifiedDiff,
                computation.DiffTruncated,
                true);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultDeleteResult> SoftDeleteAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int expectedRevision,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(VaultErrorCode.Conflict, "Document revision changed.");
            }

            var now = DateTimeOffset.UtcNow;
            var purgeAfter = now.AddDays(30);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = sqliteTransaction;
                command.CommandText =
                    """
                    UPDATE documents
                    SET deleted_at_ms = $deleted,
                        purge_after_ms = $purgeAfter,
                        updated_at_ms = $updated
                    WHERE document_id = $id;
                    """;
                command.Parameters.AddWithValue("$deleted", now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$purgeAfter", purgeAfter.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$updated", now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$id", documentId.ToString("D").ToLowerInvariant());
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await RemoveFtsAsync(
                connection,
                sqliteTransaction,
                documentId.ToString("D").ToLowerInvariant(),
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                auditContext,
                sessionId,
                "delete",
                documentId,
                "success",
                null,
                JsonSerializer.Serialize(new { purgeAfter }),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultDeleteResult(documentId, current.Revision, purgeAfter);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultRestoreResult> RestoreTrashAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadTrashedCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(VaultErrorCode.Conflict, "Document revision changed.");
            }

            var (sourcePath, sourcePathNorm) = renamePath is null
                ? (
                    current.SourcePath,
                    current.SourcePath.Normalize(NormalizationForm.FormC).ToLowerInvariant())
                : VirtualPathPolicy.NormalizeDocumentPath(renamePath, current.Format);
            var newRevision = current.Revision + 1;
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentIdValue,
                newRevision,
                current.Title,
                sourcePath,
                current.Format,
                current.HeadingsJson,
                current.TagsJson,
                current.Content);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = sqliteTransaction;
                command.CommandText =
                    """
                    UPDATE documents
                    SET source_path = $path,
                        source_path_norm = $pathNorm,
                        link_path_norm = $linkPathNorm,
                        payload_hmac = $hmac,
                        revision = $revision,
                        links_indexed_revision = 0,
                        updated_at_ms = $updated,
                        deleted_at_ms = NULL,
                        purge_after_ms = NULL
                    WHERE document_id = $id;
                    """;
                command.Parameters.AddWithValue("$path", sourcePath);
                command.Parameters.AddWithValue("$pathNorm", sourcePathNorm);
                command.Parameters.AddWithValue(
                    "$linkPathNorm",
                    DocumentLinkKeys.NormalizeDocumentPath(sourcePath));
                command.Parameters.AddWithValue("$hmac", hmac);
                command.Parameters.AddWithValue("$revision", newRevision);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.Parameters.AddWithValue("$id", documentIdValue);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await InsertRevisionAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                sourcePath,
                current.Format,
                current.Content,
                current.HeadingsJson,
                current.TagsJson,
                hmac,
                newRevision,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await ReplaceFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                current.Title,
                sourcePath,
                current.HeadingsJson,
                current.TagsJson,
                current.Content,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.RecordAliasesAsync(
                connection,
                sqliteTransaction,
                documentId,
                current.Title,
                current.SourcePath,
                current.Title,
                sourcePath,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                sqliteTransaction,
                documentId,
                newRevision,
                current.Title,
                sourcePath,
                current.Format,
                current.Content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.ReconcileAsync(
                connection,
                sqliteTransaction,
                DocumentLinkIndexer.ResolutionKeys(
                    current.Title,
                    current.SourcePath)
                    .Concat(DocumentLinkIndexer.ResolutionKeys(
                        current.Title,
                        sourcePath)),
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                actor,
                sessionId,
                "restore",
                documentId,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultRestoreResult(documentId, newRevision, sourcePath);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new VaultException(
                VaultErrorCode.Conflict,
                "An active document already uses this virtual path.",
                exception);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultPurgeResult> PurgeAsync(
        SqliteConnection connection,
        Guid sessionId,
        Guid documentId,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            using (var check = connection.CreateCommand())
            {
                check.Transaction = sqliteTransaction;
                check.CommandText =
                    "SELECT 1 FROM documents WHERE document_id = $id AND deleted_at_ms IS NOT NULL;";
                check.Parameters.AddWithValue("$id", documentIdValue);
                if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new VaultException(
                        VaultErrorCode.NotFound,
                        "Trashed document was not found.");
                }
            }
            await RemoveFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                cancellationToken).ConfigureAwait(false);
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = sqliteTransaction;
                delete.CommandText = "DELETE FROM documents WHERE document_id = $id;";
                delete.Parameters.AddWithValue("$id", documentIdValue);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            using (var cleanup = connection.CreateCommand())
            {
                cleanup.Transaction = sqliteTransaction;
                cleanup.CommandText =
                    """
                    DELETE FROM tags
                    WHERE NOT EXISTS (
                        SELECT 1 FROM document_tags
                        WHERE document_tags.tag_id = tags.tag_id
                    );
                    """;
                await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                actor,
                sessionId,
                "purge",
                documentId,
                "success",
                null,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultPurgeResult(documentId, true);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<VaultWriteResult> RestoreRevisionAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            var current = await ReadCurrentAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new VaultException(VaultErrorCode.Conflict, "Document revision changed.");
            }
            var revision = await ReadRevisionAsync(
                connection,
                sqliteTransaction,
                integrityKey,
                documentId,
                revisionId,
                cancellationToken).ConfigureAwait(false);
            var newRevision = current.Revision + 1;
            var documentIdValue = documentId.ToString("D").ToLowerInvariant();
            var normalizedTags = ParseTags(revision.TagsJson);
            var now = DateTimeOffset.UtcNow;
            var timestamp = now.ToUnixTimeMilliseconds();
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentIdValue,
                newRevision,
                revision.Title,
                current.SourcePath,
                current.Format,
                revision.HeadingsJson,
                revision.TagsJson,
                revision.Content);

            await UpdateCurrentFullAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                revision.Title,
                current.SourcePath,
                current.SourcePath.Normalize(NormalizationForm.FormC).ToLowerInvariant(),
                newRevision,
                revision.HeadingsJson,
                revision.TagsJson,
                hmac,
                revision.Content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await InsertRevisionAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                revision.Title,
                current.SourcePath,
                current.Format,
                revision.Content,
                revision.HeadingsJson,
                revision.TagsJson,
                hmac,
                newRevision,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await ReplaceTagsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                normalizedTags,
                cancellationToken).ConfigureAwait(false);
            await ReplaceFtsAsync(
                connection,
                sqliteTransaction,
                documentIdValue,
                revision.Title,
                current.SourcePath,
                revision.HeadingsJson,
                revision.TagsJson,
                revision.Content,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.RecordAliasesAsync(
                connection,
                sqliteTransaction,
                documentId,
                current.Title,
                current.SourcePath,
                revision.Title,
                current.SourcePath,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.IndexSourceAsync(
                connection,
                sqliteTransaction,
                documentId,
                newRevision,
                revision.Title,
                current.SourcePath,
                current.Format,
                revision.Content,
                timestamp,
                cancellationToken).ConfigureAwait(false);
            await DocumentLinkIndexer.ReconcileAsync(
                connection,
                sqliteTransaction,
                DocumentLinkIndexer.ResolutionKeys(
                    current.Title,
                    current.SourcePath)
                    .Concat(DocumentLinkIndexer.ResolutionKeys(
                        revision.Title,
                        current.SourcePath)),
                cancellationToken).ConfigureAwait(false);
            await AuditWriter.AppendAsync(
                connection,
                sqliteTransaction,
                actor,
                sessionId,
                "restore_revision",
                documentId,
                "success",
                null,
                JsonSerializer.Serialize(new { revisionId }),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VaultWriteResult(documentId, newRevision, now);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void Validate(
        string title,
        string format,
        string content,
        IReadOnlyList<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Title must contain 1 to 200 characters.");
        }
        if (format is not ("md" or "txt"))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Only md and txt formats are supported.");
        }
        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Document content exceeds 256 KB.");
        }
        if (tags?.Count > MaxTags)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "A document may have at most 50 tags.");
        }
    }

    private static async Task InsertRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string title,
        string sourcePath,
        string format,
        string content,
        string headingsJson,
        string tagsJson,
        byte[] hmac,
        int revision,
        long timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO document_revisions(
                revision_id, document_id, revision, title, source_path, format,
                content, headings_json, tags_json, payload_hmac,
                created_at_ms, retain_until_ms)
            VALUES(
                $revisionId, $documentId, $revision, $title, $path, $format,
                $content, $headings, $tags, $hmac,
                $created, $retainUntil);
            """;
        command.Parameters.AddWithValue("$revisionId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$documentId", documentId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$headings", headingsJson);
        command.Parameters.AddWithValue("$tags", tagsJson);
        command.Parameters.AddWithValue("$hmac", hmac);
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue(
            "$retainUntil",
            DateTimeOffset.UtcNow.Add(RevisionLifetime).ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            var normalized = tag.ToLowerInvariant();
            using (var insertTag = connection.CreateCommand())
            {
                insertTag.Transaction = transaction;
                insertTag.CommandText =
                    """
                    INSERT INTO tags(tag_id, name, normalized_name)
                    VALUES($id, $name, $normalized)
                    ON CONFLICT(normalized_name) DO NOTHING;
                    """;
                insertTag.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insertTag.Parameters.AddWithValue("$name", tag);
                insertTag.Parameters.AddWithValue("$normalized", normalized);
                await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText =
                """
                INSERT OR IGNORE INTO document_tags(document_id, tag_id)
                SELECT $documentId, tag_id FROM tags WHERE normalized_name = $normalized;
                """;
            link.Parameters.AddWithValue("$documentId", documentId);
            link.Parameters.AddWithValue("$normalized", normalized);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string title,
        string sourcePath,
        string headingsJson,
        string tagsJson,
        string content,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO documents_fts(document_id, title, source_path, headings, tags, content)
            VALUES($id, $title, $path, $headings, $tags, $content);
            """;
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$headings", headingsJson);
        command.Parameters.AddWithValue("$tags", tagsJson);
        command.Parameters.AddWithValue("$content", content);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CurrentDocument> ReadCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] integrityKey,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT title, source_path, format, content, headings_json,
                   tags_json, payload_hmac, revision
            FROM documents
            WHERE document_id = $id AND deleted_at_ms IS NULL;
            """;
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        command.Parameters.AddWithValue("$id", documentIdValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Document was not found.");
        }

        var current = new CurrentDocument(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (byte[])reader["payload_hmac"],
            reader.GetInt32(7));
        if (!DocumentHmac.Verify(
            integrityKey,
            current.PayloadHmac,
            documentIdValue,
            current.Revision,
            current.Title,
            current.SourcePath,
            current.Format,
            current.HeadingsJson,
            current.TagsJson,
            current.Content))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Document integrity check failed.");
        }
        return current;
    }

    private static async Task<CurrentDocument> ReadTrashedCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] integrityKey,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT title, source_path, format, content, headings_json,
                   tags_json, payload_hmac, revision
            FROM documents
            WHERE document_id = $id AND deleted_at_ms IS NOT NULL;
            """;
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        command.Parameters.AddWithValue("$id", documentIdValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Trashed document was not found.");
        }

        var current = new CurrentDocument(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (byte[])reader["payload_hmac"],
            reader.GetInt32(7));
        if (!DocumentHmac.Verify(
            integrityKey,
            current.PayloadHmac,
            documentIdValue,
            current.Revision,
            current.Title,
            current.SourcePath,
            current.Format,
            current.HeadingsJson,
            current.TagsJson,
            current.Content))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Document integrity check failed.");
        }
        return current;
    }

    private static async Task<RevisionDocument> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] integrityKey,
        Guid documentId,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT title, source_path, format, content, headings_json,
                   tags_json, payload_hmac, revision
            FROM document_revisions
            WHERE revision_id = $revisionId AND document_id = $documentId;
            """;
        var documentIdValue = documentId.ToString("D").ToLowerInvariant();
        command.Parameters.AddWithValue("$revisionId", revisionId.ToString("D"));
        command.Parameters.AddWithValue("$documentId", documentIdValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Document revision was not found.");
        }

        var revision = new RevisionDocument(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (byte[])reader["payload_hmac"],
            reader.GetInt32(7));
        if (!DocumentHmac.Verify(
            integrityKey,
            revision.PayloadHmac,
            documentIdValue,
            revision.Revision,
            revision.Title,
            revision.SourcePath,
            revision.Format,
            revision.HeadingsJson,
            revision.TagsJson,
            revision.Content))
        {
            throw new VaultException(VaultErrorCode.CorruptVault, "Document revision integrity check failed.");
        }
        return revision;
    }

    private static async Task UpdateCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        int revision,
        string headingsJson,
        byte[] hmac,
        string content,
        long timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE documents
            SET content = $content,
                headings_json = $headings,
                payload_hmac = $hmac,
                revision = $revision,
                links_indexed_revision = 0,
                updated_at_ms = $updated
            WHERE document_id = $id;
            """;
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$headings", headingsJson);
        command.Parameters.AddWithValue("$hmac", hmac);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string title,
        string sourcePath,
        string headingsJson,
        string tagsJson,
        string content,
        CancellationToken cancellationToken)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM documents_fts WHERE document_id = $id;";
            delete.Parameters.AddWithValue("$id", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertFtsAsync(
            connection,
            transaction,
            documentId,
            title,
            sourcePath,
            headingsJson,
            tagsJson,
            content,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM documents_fts WHERE document_id = $id;";
        command.Parameters.AddWithValue("$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateCurrentFullAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        string title,
        string sourcePath,
        string sourcePathNorm,
        int revision,
        string headingsJson,
        string tagsJson,
        byte[] hmac,
        string content,
        long timestamp,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE documents
            SET title = $title,
                title_norm = $titleNorm,
                source_path = $path,
                source_path_norm = $pathNorm,
                link_path_norm = $linkPathNorm,
                content = $content,
                headings_json = $headings,
                tags_json = $tags,
                payload_hmac = $hmac,
                revision = $revision,
                links_indexed_revision = 0,
                updated_at_ms = $updated
            WHERE document_id = $id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue(
            "$titleNorm",
            DocumentLinkKeys.NormalizeTitle(title));
        command.Parameters.AddWithValue("$path", sourcePath);
        command.Parameters.AddWithValue("$pathNorm", sourcePathNorm);
        command.Parameters.AddWithValue(
            "$linkPathNorm",
            DocumentLinkKeys.NormalizeDocumentPath(sourcePath));
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$headings", headingsJson);
        command.Parameters.AddWithValue("$tags", tagsJson);
        command.Parameters.AddWithValue("$hmac", hmac);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceTagsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string documentId,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM document_tags WHERE document_id = $id;";
            delete.Parameters.AddWithValue("$id", documentId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpsertTagsAsync(
            connection,
            transaction,
            documentId,
            tags,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        if (tags.Count > MaxTags)
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "A document may have at most 50 tags.");
        }
        return tags
            .Select(VirtualPathPolicy.NormalizeTag)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Document tags are invalid.",
                exception);
        }
    }

    private sealed record CurrentDocument(
        string Title,
        string SourcePath,
        string Format,
        string Content,
        string HeadingsJson,
        string TagsJson,
        byte[] PayloadHmac,
        int Revision);

    private sealed record RevisionDocument(
        string Title,
        string SourcePath,
        string Format,
        string Content,
        string HeadingsJson,
        string TagsJson,
        byte[] PayloadHmac,
        int Revision);
}
