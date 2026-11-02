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
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(*), coalesce(sum(length(CAST(content AS BLOB))), 0)
            FROM documents
            WHERE deleted_at IS NULL;
            """;
        await using var reader = await command.Command.ExecuteReaderAsync(
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Unable to inspect plaintext export documents.");
        }

        var count = reader.GetInt32(0);
        var bytes = reader.GetInt64(1);
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
              SELECT document_id, path_key, path, format, revision, content
              FROM documents
              WHERE deleted_at IS NULL
              ORDER BY path_key COLLATE BINARY,
                       document_id COLLATE BINARY
              LIMIT 1;
              """
            : """
              SELECT document_id, path_key, path, format, revision, content
              FROM documents
              WHERE deleted_at IS NULL
                AND (
                    path_key COLLATE BINARY > $afterPath
                    OR (
                        path_key COLLATE BINARY = $afterPath
                        AND document_id COLLATE BINARY > $afterDocumentId
                    )
                )
              ORDER BY path_key COLLATE BINARY,
                       document_id COLLATE BINARY
              LIMIT 1;
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
            pathKey);
    }
}
