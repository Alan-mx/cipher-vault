using System.Text;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;

namespace CipherVault.Core.Storage.Exports;

public static class PlaintextExportStore
{
    public static async Task<PlaintextExportPreflight> PreflightAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        PlaintextExportCursor? cursor = null;
        while (true)
        {
            var document = await ReadNextAsync(
                context,
                cursor,
                cancellationToken);
            if (document is null)
            {
                break;
            }
            count++;
            var transformed = PlaintextExportContentTransformer.Transform(
                document.Content,
                document.VirtualPath,
                document.Links);
            bytes = checked(
                bytes + Encoding.UTF8.GetByteCount(transformed));
            if (count > PlaintextExportLimits.MaxDocuments
                || bytes > PlaintextExportLimits.MaxContentBytes)
            {
                throw new VaultException(
                    VaultErrorCode.ExportTooLarge,
                    "Plaintext export exceeds the supported limit.",
                    details: new Dictionary<string, object?>
                    {
                        ["limit"] = count > PlaintextExportLimits.MaxDocuments
                            ? "documents"
                            : "contentBytes",
                        ["maximum"] = count > PlaintextExportLimits.MaxDocuments
                            ? PlaintextExportLimits.MaxDocuments
                            : PlaintextExportLimits.MaxContentBytes
                    });
            }
            cursor = new PlaintextExportCursor(
                document.PathKey,
                document.DocumentId);
        }

        return new PlaintextExportPreflight(count, bytes);
    }

    public static async Task<PlaintextExportDocument?> ReadNextAsync(
        VaultOperationContext context,
        PlaintextExportCursor? cursor,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText = cursor is null
            ? """
              SELECT d.document_id, d.path_key, d.path, d.format, d.revision,
                     d.content, l.link_kind, l.resolution_status, t.path,
                     l.display_text, l.target_heading, l.start_offset,
                     l.end_offset
              FROM documents AS d
              LEFT JOIN document_links AS l
                ON l.source_document_id = d.document_id
               AND l.source_revision = d.revision
              LEFT JOIN documents AS t
                ON t.document_id = l.target_document_id
               AND t.deleted_at IS NULL
              WHERE d.deleted_at IS NULL
              ORDER BY d.path_key COLLATE BINARY,
                       d.document_id COLLATE BINARY,
                       l.start_offset;
              """
            : """
              SELECT d.document_id, d.path_key, d.path, d.format, d.revision,
                     d.content, l.link_kind, l.resolution_status, t.path,
                     l.display_text, l.target_heading, l.start_offset,
                     l.end_offset
              FROM documents AS d
              LEFT JOIN document_links AS l
                ON l.source_document_id = d.document_id
               AND l.source_revision = d.revision
              LEFT JOIN documents AS t
                ON t.document_id = l.target_document_id
               AND t.deleted_at IS NULL
              WHERE d.deleted_at IS NULL
                AND (
                    d.path_key COLLATE BINARY > $afterPath
                    OR (
                        d.path_key COLLATE BINARY = $afterPath
                        AND d.document_id COLLATE BINARY > $afterDocumentId
                    )
                )
              ORDER BY d.path_key COLLATE BINARY,
                       d.document_id COLLATE BINARY,
                       l.start_offset;
              """;
        if (cursor is not null)
        {
            command.Command.Parameters.AddWithValue(
                "$afterPath",
                cursor.PathKey);
            command.Command.Parameters.AddWithValue(
                "$afterDocumentId",
                cursor.DocumentId.ToString("D"));
        }
        await using var reader = await command.Command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!Guid.TryParse(reader.GetString(0), out var documentId)
            || documentId == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Plaintext export document identity is invalid.");
        }

        var pathKey = reader.GetString(1);
        var path = reader.GetString(2);
        var format = VaultDocumentFormatExtensions.ParseDatabaseValue(
            reader.GetString(3));
        var revision = reader.GetInt32(4);
        var content = reader.GetString(5);
        var links = new List<ExportDocumentLink>();
        do
        {
            if (reader.IsDBNull(6))
            {
                continue;
            }
            links.Add(new ExportDocumentLink(
                ParseLinkKind(reader.GetString(6)),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt32(11),
                reader.GetInt32(12)));
        }
        while (await reader.ReadAsync(cancellationToken)
               && string.Equals(
                   reader.GetString(0),
                   documentId.ToString("D"),
                   StringComparison.Ordinal));

        try
        {
            _ = VirtualPath.Normalize(path, format);
            _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetBytes(content);
        }
        catch (Exception exception) when (
            exception is VaultException
                or ArgumentException
                or EncoderFallbackException)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Plaintext export document path or content is invalid.",
                exception);
        }

        return new PlaintextExportDocument(
            documentId,
            path,
            format,
            revision,
            content,
            links.ToArray(),
            pathKey);
    }

    private static DocumentLinkKind ParseLinkKind(string value) =>
        value switch
        {
            "wiki" => DocumentLinkKind.Wiki,
            "markdown" => DocumentLinkKind.Markdown,
            _ => throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Plaintext export document link type is invalid.")
        };
}
