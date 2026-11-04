using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Storage.Semantic;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

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
            requestedDocumentId: null,
            DocumentIdentityMode.NewDocument,
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);

    internal static async Task<DocumentWriteResult> CreateForDraftAsync(
        VaultOperationContext context,
        Guid documentId,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            context,
            request,
            actor,
            now,
            documentId,
            DocumentIdentityMode.ExistingDocument,
            DocumentContentLimitMode.ExistingDocumentAuthoring,
            cancellationToken);

    internal static async Task<DocumentWriteResult> CreateTrustedAsync(
        VaultOperationContext context,
        Guid documentId,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        DocumentContentLimitMode limitMode,
        CancellationToken cancellationToken) =>
        await CreateCoreAsync(
            context,
            request,
            actor,
            now,
            documentId,
            DocumentIdentityMode.TrustedImport,
            limitMode,
            cancellationToken);

    public static async Task<DocumentWriteResult> CopyAsync(
        VaultOperationContext context,
        Guid sourceDocumentId,
        int expectedRevision,
        CopyDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only UserUi can copy a document.");
        }

        var current = await ReadCurrentForMutationAsync(
            context,
            sourceDocumentId,
            cancellationToken);
        ThrowIfRevisionMismatch(expectedRevision, current.Revision);
        var format = VaultDocumentFormatExtensions.ParseDatabaseValue(
            current.Format);
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? BuildDefaultCopyTitle(current.Title)
            : TitleNormalizer.NormalizeTitle(request.Title);
        var path = request.VirtualPath is not null
            ? VirtualPath.Normalize(request.VirtualPath, format).Path
            : await BuildAvailableCopyPathAsync(
                context,
                current,
                title,
                format,
                cancellationToken);
        var documentId = Guid.NewGuid();
        var content = format == VaultDocumentFormat.Markdown
            ? DocumentIdentity.EnsureMarkdownIdentity(
                current.Content,
                documentId,
                DocumentIdentityMode.CopyAsNew)
            : current.Content;
        return await CreateTrustedAsync(
            context,
            documentId,
            new CreateDocumentRequest(
                title,
                path,
                Folder: null,
                format,
                content,
                current.TagNames),
            actor,
            now,
            DocumentContentLimitMode.StandardWrite,
            cancellationToken);
    }

    private static async Task<string> BuildAvailableCopyPathAsync(
        VaultOperationContext context,
        CurrentDocumentState source,
        string title,
        VaultDocumentFormat format,
        CancellationToken cancellationToken)
    {
        var folder = source.Path.Contains('/')
            ? source.Path[..source.Path.LastIndexOf('/')]
            : null;
        for (var suffix = 0; suffix < 1_000; suffix++)
        {
            var candidateTitle = suffix == 0
                ? title
                : $"{title} {suffix + 1}";
            var candidatePath = VirtualPath.Build(
                candidateTitle,
                folder,
                format).Path;
            using var query = context.CreateCommand();
            query.Command.CommandText =
                """
                SELECT count(*)
                FROM documents
                WHERE deleted_at IS NULL AND path_key = $pathKey;
                """;
            query.Command.Parameters.AddWithValue(
                "$pathKey",
                candidatePath);
            if (Convert.ToInt32(
                    await query.Command.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                return candidatePath;
            }
        }
        throw new VaultException(
            VaultErrorCode.DocumentPathConflict,
            "No available copy path could be generated.");
    }

    private static string BuildDefaultCopyTitle(string title)
    {
        const string suffix = " 副本";
        const int maxTitleLength = 200;
        var prefix = new StringBuilder(maxTitleLength);
        var count = 0;
        foreach (var rune in title.EnumerateRunes())
        {
            if (count >= maxTitleLength - suffix.Length)
            {
                break;
            }
            prefix.Append(rune);
            count++;
        }
        return TitleNormalizer.NormalizeTitle(prefix.ToString() + suffix);
    }

    private static async Task<DocumentWriteResult> CreateCoreAsync(
        VaultOperationContext context,
        CreateDocumentRequest request,
        VaultActorContext actor,
        DateTimeOffset now,
        Guid? requestedDocumentId,
        DocumentIdentityMode identityMode,
        DocumentContentLimitMode limitMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        EnsureDocumentWriteActor(actor);

        var documentId = requestedDocumentId ?? Guid.NewGuid();
        var content = request.Format == VaultDocumentFormat.Markdown
            ? DocumentIdentity.EnsureMarkdownIdentity(
                request.Content,
                documentId,
                identityMode)
            : request.Content;
        var parsed = DocumentContentParser.Parse(
            request.Title,
            request.VirtualPath,
            request.Folder,
            request.Format,
            content,
            request.Tags,
            limitMode);
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
                parsed.Path,
                parsed.HeadingsJson,
                parsed.Content,
                parsed.Tags,
                parsed.Links,
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
            await SemanticIndexQueue.QueueIndexAsync(
                context,
                documentId,
                1,
                parsed.Content,
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
        catch (SqliteException exception) when (IsDocumentPathConstraint(exception))
        {
            throw new VaultException(
                VaultErrorCode.DocumentPathConflict,
                "An active document already uses this virtual path.");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw ConstraintFailure(exception);
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
        var requestedContent =
            VaultDocumentFormatExtensions.ParseDatabaseValue(current.Format)
                == VaultDocumentFormat.Markdown
                ? DocumentIdentity.EnsureMarkdownIdentity(
                    request.Content,
                    current.DocumentId,
                    DocumentIdentityMode.ExistingDocument)
                : request.Content;
        var parsed = DocumentContentParser.Parse(
            request.Title,
            request.VirtualPath ?? current.Path,
            null,
            VaultDocumentFormatExtensions.ParseDatabaseValue(current.Format),
            requestedContent,
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

        var titleChanged = !string.Equals(
            current.Title,
            parsed.Title,
            StringComparison.Ordinal);
        var pathChanged = !string.Equals(
            current.Path,
            parsed.Path.Path,
            StringComparison.Ordinal);
        var oldAliases = DocumentIndexWriter.ResolutionAliases(
            current.Title,
            current.Path,
            current.Format);
        var incomingRows = titleChanged || pathChanged
            ? await ReadIncomingRowsForRewriteAsync(
                context,
                documentId,
                cancellationToken)
            : [];
        var outgoingRows = pathChanged
            ? await ReadOutgoingMarkdownRowsForRewriteAsync(
                context,
                documentId,
                current.Revision,
                cancellationToken)
            : [];
        var selfRewrites = BuildSelfRewrites(
            current,
            parsed,
            incomingRows,
            outgoingRows,
            titleChanged,
            pathChanged);
        if (selfRewrites.Count > 0)
        {
            var content = string.Equals(
                requestedContent,
                current.Content,
                StringComparison.Ordinal)
                ? ApplyReplacements(current.Content, selfRewrites)
                : ApplyRelocatedReplacements(
                    requestedContent,
                    selfRewrites);
            parsed = DocumentContentParser.Parse(
                request.Title,
                parsed.Path.Path,
                null,
                VaultDocumentFormatExtensions.ParseDatabaseValue(current.Format),
                content,
                request.Tags,
                limitMode);
        }

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
                parsed.Path,
                parsed.HeadingsJson,
                parsed.Content,
                parsed.Tags,
                parsed.Links,
                cancellationToken);
            if (titleChanged || pathChanged)
            {
                await DocumentIndexWriter.ReconcileIncomingLinksAsync(
                    context,
                    oldAliases,
                    cancellationToken);
                await DocumentIndexWriter.ReconcileIncomingLinksAsync(
                    context,
                    DocumentIndexWriter.ResolutionAliases(
                        parsed.Title,
                        parsed.Path.Path,
                        parsed.Format),
                    cancellationToken);
            }
            var rewrittenLinks = selfRewrites.Count;
            rewrittenLinks += await RewriteIncomingLinksAsync(
                context,
                incomingRows
                    .Where(row => row.SourceDocumentId != documentId)
                    .ToArray(),
                parsed.Title,
                parsed.Path.Path,
                titleChanged,
                pathChanged,
                actor,
                now,
                cancellationToken);

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
            await SemanticIndexQueue.QueueIndexAsync(
                context,
                documentId,
                newRevision,
                parsed.Content,
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
                    backlinkRewrites = rewrittenLinks
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
        catch (SqliteException exception) when (IsDocumentPathConstraint(exception))
        {
            throw new VaultException(
                VaultErrorCode.DocumentPathConflict,
                "An active document already uses this virtual path.");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw ConstraintFailure(exception);
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
        await SemanticIndexQueue.QueueDeleteAsync(
            context,
            documentId,
            newRevision,
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

    private static IReadOnlyList<TextReplacement> BuildSelfRewrites(
        CurrentDocumentState current,
        ParsedDocumentInput parsed,
        IReadOnlyList<IncomingLinkRewriteRow> incomingRows,
        IReadOnlyList<OutgoingMarkdownRewriteRow> outgoingRows,
        bool titleChanged,
        bool pathChanged)
    {
        var replacements = new List<TextReplacement>();
        if (titleChanged)
        {
            replacements.AddRange(
                incomingRows
                    .Where(row =>
                        row.SourceDocumentId == current.DocumentId
                        && row.Kind == DocumentLinkKind.Wiki)
                    .Select(row => BuildWikiReplacement(row, parsed.Title)));
        }
        if (pathChanged)
        {
            replacements.AddRange(
                outgoingRows.Select(row => BuildMarkdownReplacement(
                    row,
                    parsed.Path.Path,
                    row.TargetDocumentId == current.DocumentId
                        ? parsed.Path.Path
                        : row.TargetVirtualPath)));
        }
        return replacements
            .Select(replacement => replacement with
            {
                ExpectedSourceText = current.Content[
                    replacement.StartOffset..replacement.EndOffset]
            })
            .ToArray();
    }

    private static async Task<IReadOnlyList<IncomingLinkRewriteRow>>
        ReadIncomingRowsForRewriteAsync(
            VaultOperationContext context,
            Guid targetDocumentId,
            CancellationToken cancellationToken)
    {
        var rows = new List<IncomingLinkRewriteRow>();
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT l.link_id, l.source_document_id, l.source_revision,
                   l.link_kind, l.raw_target, l.display_text,
                   l.target_heading, l.start_offset, l.end_offset,
                   l.destination_start_offset, l.destination_end_offset,
                   d.path
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
            rows.Add(new IncomingLinkRewriteRow(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2),
                ParseLinkKind(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<OutgoingMarkdownRewriteRow>>
        ReadOutgoingMarkdownRowsForRewriteAsync(
            VaultOperationContext context,
            Guid sourceDocumentId,
            int sourceRevision,
            CancellationToken cancellationToken)
    {
        var rows = new List<OutgoingMarkdownRewriteRow>();
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            SELECT l.target_document_id, l.raw_target, l.target_heading,
                   l.destination_start_offset, l.destination_end_offset,
                   t.path
            FROM document_links AS l
            JOIN documents AS t ON t.document_id = l.target_document_id
            WHERE l.source_document_id = $sourceId
              AND l.source_revision = $sourceRevision
              AND l.link_kind = 'markdown'
              AND l.resolution_status = 'resolved'
              AND t.deleted_at IS NULL
            ORDER BY l.start_offset DESC;
            """;
        read.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceDocumentId.ToString("D"));
        read.Command.Parameters.AddWithValue(
            "$sourceRevision",
            sourceRevision);
        await using var reader = await read.Command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OutgoingMarkdownRewriteRow(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }
        return rows;
    }

    private static async Task<int> RewriteIncomingLinksAsync(
        VaultOperationContext context,
        IReadOnlyList<IncomingLinkRewriteRow> rows,
        string newTitle,
        string newTargetPath,
        bool titleChanged,
        bool pathChanged,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rewrittenLinks = 0;
        foreach (var group in rows.GroupBy(row => row.SourceDocumentId))
        {
            var current = await ReadCurrentForMutationAsync(
                context,
                group.Key,
                cancellationToken);
            var replacements = new List<TextReplacement>();
            foreach (var row in group)
            {
                if (row.Kind == DocumentLinkKind.Wiki && titleChanged)
                {
                    replacements.Add(BuildWikiReplacement(row, newTitle));
                }
                else if (row.Kind == DocumentLinkKind.Markdown && pathChanged)
                {
                    replacements.Add(BuildMarkdownReplacement(
                        row,
                        row.SourceVirtualPath,
                        newTargetPath));
                }
            }
            if (replacements.Count == 0)
            {
                continue;
            }

            var content = ApplyReplacements(current.Content, replacements);
            _ = await UpdateCoreAsync(
                context,
                group.Key,
                current.Revision,
                new UpdateDocumentRequest(
                    current.Title,
                    current.Path,
                    content,
                    current.TagNames),
                actor,
                now,
                "update",
                DocumentContentLimitMode.ExistingDocumentAuthoring,
                cancellationToken);
            rewrittenLinks += replacements.Count;
        }
        return rewrittenLinks;
    }

    private static TextReplacement BuildWikiReplacement(
        IncomingLinkRewriteRow row,
        string newTitle) =>
        new(
            row.StartOffset,
            row.EndOffset,
            BuildWikiLink(row, newTitle));

    private static TextReplacement BuildMarkdownReplacement(
        IncomingLinkRewriteRow row,
        string sourceVirtualPath,
        string targetVirtualPath)
    {
        if (row.DestinationStartOffset is not { } start
            || row.DestinationEndOffset is not { } end)
        {
            throw CorruptLinkOffsets();
        }
        return BuildMarkdownReplacementCore(
            start,
            end,
            sourceVirtualPath,
            targetVirtualPath,
            row.TargetHeading);
    }

    private static TextReplacement BuildMarkdownReplacement(
        OutgoingMarkdownRewriteRow row,
        string sourceVirtualPath,
        string targetVirtualPath) =>
        BuildMarkdownReplacementCore(
            row.DestinationStartOffset,
            row.DestinationEndOffset,
            sourceVirtualPath,
            targetVirtualPath,
            row.TargetHeading);

    private static TextReplacement BuildMarkdownReplacementCore(
        int start,
        int end,
        string sourceVirtualPath,
        string targetVirtualPath,
        string? targetHeading) =>
        new(
            start,
            end,
            MarkdownLinkWriter.BuildRelativeDestination(
                sourceVirtualPath,
                targetVirtualPath,
                targetHeading));

    private static string BuildWikiLink(
        IncomingLinkRewriteRow row,
        string newTitle)
    {
        var heading = row.TargetHeading is null
            ? string.Empty
            : $"#{row.TargetHeading}";
        var display = string.Equals(
            row.DisplayText,
            row.RawTarget,
            StringComparison.Ordinal)
            ? string.Empty
            : $"|{row.DisplayText}";
        return $"[[{newTitle}{heading}{display}]]";
    }

    private static string ApplyReplacements(
        string content,
        IReadOnlyList<TextReplacement> replacements)
    {
        var result = content;
        var previousStart = content.Length + 1;
        foreach (var replacement in replacements
                     .OrderByDescending(item => item.StartOffset))
        {
            if (replacement.StartOffset < 0
                || replacement.EndOffset <= replacement.StartOffset
                || replacement.EndOffset > result.Length
                || replacement.EndOffset > previousStart)
            {
                throw CorruptLinkOffsets();
            }
            result = MarkdownLinkWriter.ReplaceDestination(
                result,
                replacement.StartOffset,
                replacement.EndOffset,
                replacement.Value);
            previousStart = replacement.StartOffset;
        }
        return result;
    }

    private static string ApplyRelocatedReplacements(
        string editedContent,
        IReadOnlyList<TextReplacement> replacements)
    {
        var relocated = new List<TextReplacement>();
        var searchStart = 0;
        foreach (var replacement in replacements
                     .OrderBy(item => item.StartOffset))
        {
            if (string.IsNullOrEmpty(replacement.ExpectedSourceText))
            {
                continue;
            }
            var offset = editedContent.IndexOf(
                replacement.ExpectedSourceText,
                searchStart,
                StringComparison.Ordinal);
            if (offset < 0)
            {
                continue;
            }
            relocated.Add(new TextReplacement(
                offset,
                offset + replacement.ExpectedSourceText.Length,
                replacement.Value));
            searchStart = offset + replacement.ExpectedSourceText.Length;
        }
        return ApplyReplacements(editedContent, relocated);
    }

    private static DocumentLinkKind ParseLinkKind(string value) =>
        value switch
        {
            "wiki" => DocumentLinkKind.Wiki,
            "markdown" => DocumentLinkKind.Markdown,
            _ => throw CorruptLinkOffsets()
        };

    private static VaultException CorruptLinkOffsets() =>
        new(
            VaultErrorCode.CorruptVault,
            "Document link offsets are invalid.");

    private static bool IsDocumentPathConstraint(SqliteException exception) =>
        exception.SqliteErrorCode == 19
        && exception.Message.Contains(
            "documents.path_key",
            StringComparison.OrdinalIgnoreCase);

    private static VaultException ConstraintFailure(
        SqliteException exception) =>
        new(
            VaultErrorCode.DatabaseError,
            "A database constraint prevented the document write.",
            exception);

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

    private sealed record IncomingLinkRewriteRow(
        Guid LinkId,
        Guid SourceDocumentId,
        int SourceRevision,
        DocumentLinkKind Kind,
        string RawTarget,
        string DisplayText,
        string? TargetHeading,
        int StartOffset,
        int EndOffset,
        int? DestinationStartOffset,
        int? DestinationEndOffset,
        string SourceVirtualPath);

    private sealed record OutgoingMarkdownRewriteRow(
        Guid TargetDocumentId,
        string RawTarget,
        string? TargetHeading,
        int DestinationStartOffset,
        int DestinationEndOffset,
        string TargetVirtualPath);

    private sealed record TextReplacement(
        int StartOffset,
        int EndOffset,
        string Value,
        string ExpectedSourceText = "");
}
