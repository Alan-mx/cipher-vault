using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Documents;

internal static class DocumentLinkResolver
{
    private const int MaxCandidates = 20;

    public static async Task<LinkResolution> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string normalizedTarget,
        CancellationToken cancellationToken)
    {
        var candidates = await QueryAsync(
            connection,
            transaction,
            """
            SELECT document_id, title, source_path
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND link_path_norm = $target
            ORDER BY source_path_norm, document_id
            LIMIT 21;
            """,
            normalizedTarget,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0)
        {
            return ToResolution(candidates);
        }

        candidates = await QueryAsync(
            connection,
            transaction,
            """
            SELECT document_id, title, source_path
            FROM documents
            WHERE deleted_at_ms IS NULL
              AND title_norm = $target
            ORDER BY source_path_norm, document_id
            LIMIT 21;
            """,
            normalizedTarget,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0)
        {
            return ToResolution(candidates);
        }

        candidates = await QueryAliasAsync(
            connection,
            transaction,
            "path",
            normalizedTarget,
            cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0)
        {
            return ToResolution(candidates);
        }

        candidates = await QueryAliasAsync(
            connection,
            transaction,
            "title",
            normalizedTarget,
            cancellationToken).ConfigureAwait(false);
        return ToResolution(candidates);
    }

    private static async Task<IReadOnlyList<VaultLinkTarget>> QueryAliasAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string aliasKind,
        string normalizedTarget,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT documents.document_id, documents.title, documents.source_path
            FROM document_link_aliases
            JOIN documents
              ON documents.document_id = document_link_aliases.document_id
            WHERE documents.deleted_at_ms IS NULL
              AND document_link_aliases.alias_kind = $kind
              AND document_link_aliases.alias_norm = $target
            ORDER BY documents.source_path_norm, documents.document_id
            LIMIT 21;
            """;
        command.Parameters.AddWithValue("$kind", aliasKind);
        command.Parameters.AddWithValue("$target", normalizedTarget);
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VaultLinkTarget>> QueryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string normalizedTarget,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$target", normalizedTarget);
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VaultLinkTarget>> ReadCandidatesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var candidates = new List<VaultLinkTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(new VaultLinkTarget(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2)));
            if (candidates.Count == MaxCandidates)
            {
                break;
            }
        }
        return candidates;
    }

    private static LinkResolution ToResolution(
        IReadOnlyList<VaultLinkTarget> candidates)
    {
        return candidates.Count switch
        {
            0 => new LinkResolution(
                DocumentLinkResolutionStatus.Unresolved,
                null,
                []),
            1 => new LinkResolution(
                DocumentLinkResolutionStatus.Resolved,
                candidates[0].DocumentId,
                candidates),
            _ => new LinkResolution(
                DocumentLinkResolutionStatus.Ambiguous,
                null,
                candidates)
        };
    }
}

internal sealed record LinkResolution(
    DocumentLinkResolutionStatus Status,
    Guid? TargetDocumentId,
    IReadOnlyList<VaultLinkTarget> Candidates);
