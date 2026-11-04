using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CipherVault.Core.Storage.Documents;

public static class DocumentMutationWriter
{
    public static async Task<DocumentWriteResult> CreateAsync(
        VaultOperationContext context,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            context,
            request,
            actor,
            now,
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);

    internal static async Task<DocumentWriteResult> CreateForDraftAsync(
        VaultOperationContext context,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            context,
            request,
            actor,
            now,
            DocumentContentLimitMode.ExistingDocumentAuthoring,
            cancellationToken);

    private static async Task<DocumentWriteResult> CreateCoreAsync(
        VaultOperationContext context,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        DocumentContentLimitMode limitMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDocumentWriteActor(actor);

        var parsed = DocumentContentParser.Parse(
            request.Title,
            request.VirtualPath,
            request.Folder,
            request.Format,
            request.Content,
            request.Tags,
            limitMode);
        var documentId = Guid.NewGuid();
        try
        {
            using (var insert = context.CreateCommand())
            {
                insert.Command.CommandText =
                    """
                    INSERT INTO documents(
                        document_id, title, title_key, path, path_key, format, content,
                        frontmatter_json, headings_json, revision,
                        created_at, updated_at, deleted_at, purge_eligible_at)
                    VALUES(
                        $documentId, $title, $titleKey, $path, $pathKey, $format, $content,
                        $frontMatter, $headings, 1,
                        $createdAt, $updatedAt, NULL, NULL);
                    """;
                insert.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                insert.Command.Parameters.AddWithValue("$title", parsed.Title);
                insert.Command.Parameters.AddWithValue("$titleKey", parsed.TitleKey);
                insert.Command.Parameters.AddWithValue("$path", parsed.Path.Path);
                insert.Command.Parameters.AddWithValue(
                    "$pathKey",
                    parsed.Path.PathKey);
                insert.Command.Parameters.AddWithValue("$format", parsed.Format);
                insert.Command.Parameters.AddWithValue("$content", parsed.Content);
                insert.Command.Parameters.AddWithValue(
                    "$frontMatter",
                    parsed.FrontMatterJson);
                insert.Command.Parameters.AddWithValue("$headings", parsed.HeadingsJson);
                insert.Command.Parameters.AddWithValue(
                    "$createdAt",
                    now.ToUniversalTime().ToString("O"));
                insert.Command.Parameters.AddWithValue(
                    "$updatedAt",
                    now.ToUniversalTime().ToString("O"));
                await insert.Command.ExecuteNonQueryAsync(cancellationToken);
            }

            await DocumentIndexWriter.ReplaceAllAsync(
                context,
                documentId,
                1,
                parsed.Title,
                parsed.Path.Path,
                parsed.HeadingsJson,
                parsed.Content,
                parsed.Tags,
                parsed.WikiLinks,
                cancellationToken);
            await DocumentIndexWriter.ReconcileIncomingLinksAsync(
                context,
                DocumentIndexWriter.ResolutionAliases(
                    parsed.Title,
                    parsed.Path.Path,
                    parsed.Format),
                cancellationToken);
            _ = await DocumentRevisionWriter.InsertAsync(
                context,
                documentId,
                1,
                parsed.Title,
                parsed.Path.Path,
                parsed.Format,
                parsed.Content,
                parsed.FrontMatterJson,
                parsed.HeadingsJson,
                parsed.Tags,
                now,
                cancellationToken);

            var auditEventId = await AuditStore.AppendAsync(
                context,
                actor,
                "create",
                documentId,
                1,
                "success",
                new { format = parsed.Format, tags = parsed.Tags.Count },
                now,
                cancellationToken);
            return new DocumentWriteResult(
                documentId,
                1,
                parsed.Path.Path,
                now,
                auditEventId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new VaultException(
                VaultErrorCode.DocumentPathConflict,
                "An active document already uses this virtual path.");
        }
    }

    public static Task<DocumentWriteResult> UpdateAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateCoreAsync(
            context,
            documentId,
            expectedRevision,
            request,
            actor,
            now,
            "update",
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);

    public static Task<DocumentWriteResult> UpdateAuthoringAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateCoreAsync(
            context,
            documentId,
            expectedRevision,
            request,
            actor,
            now,
            "update",
            DocumentContentLimitMode.ExistingDocumentAuthoring,
            cancellationToken);

    internal static async Task<DocumentWriteResult> UpdateCoreAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        UpdateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        string auditAction,
        DocumentContentLimitMode limitMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDocumentWriteActor(actor);

        var current = await ReadCurrentForMutationAsync(
            context,
            documentId,
            cancellationToken);
        ThrowIfRevisionMismatch(expectedRevision, current.Revision);
        var parsed = DocumentContentParser.Parse(
            request.Title,
            request.VirtualPath ?? current.Path,
            null,
            VaultDocumentFormatExtensions.ParseDatabaseValue(current.Format),
            request.Content,
            request.Tags,
            limitMode);
        if (IsNoOp(current, parsed))
        {
            return new DocumentWriteResult(
                documentId,
                current.Revision,
                current.Path,
                now,
                null);
        }

        var oldAliases = DocumentIndexWriter.ResolutionAliases(
            current.Title,
            current.Path,
            current.Format);
        var backlinkRows = request.UpdateBacklinks
            ? await ReadBacklinkRowsForRewriteAsync(
                context,
                documentId,
                cancellationToken)
            : [];
        try
        {
            var newRevision = current.Revision + 1;
            using (var update = context.CreateCommand())
            {
                update.Command.CommandText =
                    """
                    UPDATE documents
                    SET title = $title,
                        title_key = $titleKey,
                        path = $path,
                        path_key = $pathKey,
                        content = $content,
                        frontmatter_json = $frontMatter,
                        headings_json = $headings,
                        revision = $revision,
                        updated_at = $updatedAt
                    WHERE document_id = $documentId
                      AND revision = $expectedRevision
                      AND deleted_at IS NULL;
                    """;
                update.Command.Parameters.AddWithValue("$title", parsed.Title);
                update.Command.Parameters.AddWithValue("$titleKey", parsed.TitleKey);
                update.Command.Parameters.AddWithValue("$path", parsed.Path.Path);
                update.Command.Parameters.AddWithValue("$pathKey", parsed.Path.PathKey);
                update.Command.Parameters.AddWithValue("$content", parsed.Content);
                update.Command.Parameters.AddWithValue(
                    "$frontMatter",
                    parsed.FrontMatterJson);
                update.Command.Parameters.AddWithValue(
                    "$headings",
                    parsed.HeadingsJson);
                update.Command.Parameters.AddWithValue("$revision", newRevision);
                update.Command.Parameters.AddWithValue(
                    "$updatedAt",
                    now.ToUniversalTime().ToString("O"));
                update.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                update.Command.Parameters.AddWithValue(
                    "$expectedRevision",
                    expectedRevision);
                if (await update.Command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    var latest = await ReadCurrentForMutationAsync(
                        context,
                        documentId,
                        cancellationToken);
                    throw RevisionConflict(latest.Revision);
                }
            }

            await DocumentIndexWriter.ReplaceAllAsync(
                context,
                documentId,
                newRevision,
                parsed.Title,
                parsed.Path.Path,
                parsed.HeadingsJson,
                parsed.Content,
                parsed.Tags,
                parsed.WikiLinks,
                cancellationToken);
            var aliases = oldAliases.Concat(
                DocumentIndexWriter.ResolutionAliases(
                    parsed.Title,
                    parsed.Path.Path,
                    parsed.Format));
            await DocumentIndexWriter.ReconcileIncomingLinksAsync(
                context,
                aliases,
                cancellationToken);
            if (request.UpdateBacklinks && !string.Equals(
                    current.Title,
                    parsed.Title,
                    StringComparison.Ordinal))
            {
                await RewriteBacklinksAsync(
                    context,
                    backlinkRows,
                    current.Title,
                    parsed.Title,
                    actor,
                    now,
                    cancellationToken);
            }

            _ = await DocumentRevisionWriter.InsertAsync(
                context,
                documentId,
                newRevision,
                parsed.Title,
                parsed.Path.Path,
                parsed.Format,
                parsed.Content,
                parsed.FrontMatterJson,
                parsed.HeadingsJson,
                parsed.Tags,
                now,
                cancellationToken);
            var auditEventId = await AuditStore.AppendAsync(
                context,
                actor,
                auditAction,
                documentId,
                newRevision,
                "success",
                new
                {
                    previousRevision = current.Revision,
                    tags = parsed.Tags.Count,
                    backlinkRewrites = backlinkRows.Count
                },
                now,
                cancellationToken);
            return new DocumentWriteResult(
                documentId,
                newRevision,
                parsed.Path.Path,
                now,
                auditEventId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new VaultException(
                VaultErrorCode.DocumentPathConflict,
                "An active document already uses this virtual path.");
        }
    }

    public static async Task<TrashDeleteResult> DeleteAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDocumentWriteActor(actor);
        var current = await ReadCurrentForMutationAsync(
            context,
            documentId,
            cancellationToken);
        ThrowIfRevisionMismatch(expectedRevision, current.Revision);
        var newRevision = current.Revision + 1;
        var purgeEligibleAt = now.AddDays(30);
        var aliases = DocumentIndexWriter.ResolutionAliases(
            current.Title,
            current.Path,
            current.Format);

        using (var update = context.CreateCommand())
        {
            update.Command.CommandText =
                """
                UPDATE documents
                SET revision = $revision,
                    updated_at = $deletedAt,
                    deleted_at = $deletedAt,
                    purge_eligible_at = $purgeEligibleAt
                WHERE document_id = $documentId
                  AND revision = $expectedRevision
                  AND deleted_at IS NULL;
                """;
            update.Command.Parameters.AddWithValue("$revision", newRevision);
            update.Command.Parameters.AddWithValue(
                "$deletedAt",
                now.ToUniversalTime().ToString("O"));
            update.Command.Parameters.AddWithValue(
                "$purgeEligibleAt",
                purgeEligibleAt.ToUniversalTime().ToString("O"));
            update.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            update.Command.Parameters.AddWithValue(
                "$expectedRevision",
                expectedRevision);
            if (await update.Command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                var latest = await ReadCurrentForMutationAsync(
                    context,
                    documentId,
                    cancellationToken);
                throw RevisionConflict(latest.Revision);
            }
        }

        await DocumentIndexWriter.RemoveFtsAsync(
            context,
            documentId,
            cancellationToken);
        using (var deleteLinks = context.CreateCommand())
        {
            deleteLinks.Command.CommandText =
                "DELETE FROM document_links WHERE source_document_id = $documentId;";
            deleteLinks.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await deleteLinks.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await DocumentIndexWriter.ReconcileIncomingLinksAsync(
            context,
            aliases,
            cancellationToken);
        var tags = TagNormalizer.NormalizeMany(current.TagNames);
        _ = await DocumentRevisionWriter.InsertAsync(
            context,
            documentId,
            newRevision,
            current.Title,
            current.Path,
            current.Format,
            current.Content,
            current.FrontMatterJson,
            current.HeadingsJson,
            tags,
            now,
            cancellationToken);
        var auditEventId = await AuditStore.AppendAsync(
            context,
            actor,
            "delete",
            documentId,
            newRevision,
            "success",
            new { purgeEligibleAt },
            now,
            cancellationToken);
        return new TrashDeleteResult(
            documentId,
            newRevision,
            now,
            purgeEligibleAt,
            auditEventId);
    }

    internal static async Task<CurrentDocumentState> ReadCurrentForMutationAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        CurrentDocumentState state;
        using (var read = context.CreateCommand())
        {
            read.Command.CommandText =
                """
                SELECT document_id, title, title_key, path, path_key, format,
                       content, frontmatter_json, headings_json, revision
                FROM documents
                WHERE document_id = $documentId AND deleted_at IS NULL;
                """;
            read.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new VaultException(
                    VaultErrorCode.DocumentNotFound,
                    "Document was not found.");
            }

            state = new CurrentDocumentState(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                []);
        }

        var tags = new List<string>();
        using (var tagRead = context.CreateCommand())
        {
            tagRead.Command.CommandText =
                """
                SELECT t.name
                FROM document_tags AS dt
                JOIN tags AS t ON t.tag_id = dt.tag_id
                WHERE dt.document_id = $documentId
                ORDER BY t.normalized_key;
                """;
            tagRead.Command.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            await using var reader = await tagRead.Command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tags.Add(reader.GetString(0));
            }
        }
        return state with { TagNames = tags.ToArray() };
    }

    internal static void EnsureDocumentWriteActor(VaultActorContext actor)
    {
        if (actor.Kind is not (VaultActorKind.UserUi or VaultActorKind.Mcp))
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "This actor cannot modify documents.");
        }
    }

    private static bool IsNoOp(
        CurrentDocumentState current,
        ParsedDocumentInput parsed)
    {
        var currentTagKeys = TagNormalizer.NormalizeMany(current.TagNames)
            .Select(tag => tag.NormalizedKey);
        return string.Equals(current.Title, parsed.Title, StringComparison.Ordinal)
            && string.Equals(current.Path, parsed.Path.Path, StringComparison.Ordinal)
            && string.Equals(current.Content, parsed.Content, StringComparison.Ordinal)
            && string.Equals(
                current.FrontMatterJson,
                parsed.FrontMatterJson,
                StringComparison.Ordinal)
            && string.Equals(
                current.HeadingsJson,
                parsed.HeadingsJson,
                StringComparison.Ordinal)
            && currentTagKeys.SequenceEqual(
                parsed.Tags.Select(tag => tag.NormalizedKey),
                StringComparer.Ordinal);
    }

    private static async Task<IReadOnlyList<BacklinkRewriteRow>>
        ReadBacklinkRowsForRewriteAsync(
            VaultOperationContext context,
            Guid targetDocumentId,
            CancellationToken cancellationToken)
    {
        var rows = new List<BacklinkRewriteRow>();
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT l.link_id, l.source_document_id, l.source_revision,
                   l.raw_target, l.display_text, l.target_heading,
                   l.start_offset, l.end_offset
            FROM document_links AS l
            JOIN documents AS d ON d.document_id = l.source_document_id
            WHERE l.target_document_id = $targetId
              AND l.resolution_status = 'resolved'
              AND d.deleted_at IS NULL
              AND l.source_revision = d.revision
            ORDER BY l.source_document_id, l.start_offset DESC;
            """;
        read.Command.Parameters.AddWithValue(
            "$targetId",
            targetDocumentId.ToString("D"));
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BacklinkRewriteRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }
        return rows;
    }

    private static async Task RewriteBacklinksAsync(
        VaultOperationContext context,
        IReadOnlyList<BacklinkRewriteRow> rows,
        string oldTitle,
        string newTitle,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var group in rows.GroupBy(row => row.SourceDocumentId))
        {
            var current = await ReadCurrentForMutationAsync(
                context,
                group.Key,
                cancellationToken);
            var content = current.Content;
            foreach (var row in group.OrderByDescending(row => row.StartOffset))
            {
                if (row.EndOffset > content.Length
                    || row.StartOffset < 0
                    || row.EndOffset <= row.StartOffset)
                {
                    throw new VaultException(
                        VaultErrorCode.CorruptVault,
                        "Backlink offset is invalid.");
                }

                var heading = row.TargetHeading is null ? string.Empty : $"#{row.TargetHeading}";
                var display = string.Equals(
                    row.DisplayText,
                    row.RawTarget,
                    StringComparison.Ordinal)
                    ? string.Empty
                    : $"|{row.DisplayText}";
                content = string.Concat(
                    content.AsSpan(0, row.StartOffset),
                    $"[[{newTitle}{heading}{display}]]",
                    content.AsSpan(row.EndOffset));
            }

            _ = await UpdateAsync(
                context,
                group.Key,
                current.Revision,
                new UpdateDocumentRequest(
                    current.Title,
                    current.Path,
                    content,
                    current.TagNames,
                    UpdateBacklinks: false),
                actor,
                now,
                cancellationToken);
        }
    }

    internal static void ThrowIfRevisionMismatch(
        int expectedRevision,
        int currentRevision)
    {
        if (expectedRevision != currentRevision)
        {
            throw RevisionConflict(currentRevision);
        }
    }

    private static VaultException RevisionConflict(int currentRevision) =>
        new(
            VaultErrorCode.RevisionConflict,
            "Document revision changed.",
            details: new Dictionary<string, object?>
            {
                ["currentRevision"] = currentRevision
            });

    internal sealed record CurrentDocumentState(
        Guid DocumentId,
        string Title,
        string TitleKey,
        string Path,
        string PathKey,
        string Format,
        string Content,
        string FrontMatterJson,
        string HeadingsJson,
        int Revision,
        IReadOnlyList<string> TagNames);

    private sealed record BacklinkRewriteRow(
        Guid LinkId,
        Guid SourceDocumentId,
        int SourceRevision,
        string RawTarget,
        string DisplayText,
        string? TargetHeading,
        int StartOffset,
        int EndOffset);
}
