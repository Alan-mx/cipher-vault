using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Documents;

internal static class DocumentLinkResolver
{
    private const int MaxCandidates = 20;
    private const int QueryChunkSize = 400;

    public static async Task<LinkResolution> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string normalizedTarget,
        CancellationToken cancellationToken)
    {
        var results = await ResolveManyAsync(
            connection,
            transaction,
            [normalizedTarget],
            cancellationToken).ConfigureAwait(false);
        return results.TryGetValue(normalizedTarget, out var resolution)
            ? resolution
            : new LinkResolution(
                DocumentLinkResolutionStatus.Unresolved,
                null,
                []);
    }

    public static async Task<IReadOnlyDictionary<string, LinkResolution>>
        ResolveManyAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            IReadOnlyCollection<string> normalizedTargets,
            CancellationToken cancellationToken)
    {
        var targets = normalizedTargets
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (targets.Length == 0)
        {
            return new Dictionary<string, LinkResolution>(
                StringComparer.Ordinal);
        }

        var pathCandidates = NewCandidateMap();
        var titleCandidates = NewCandidateMap();
        var pathAliasCandidates = NewCandidateMap();
        var titleAliasCandidates = NewCandidateMap();
        foreach (var chunk in Chunk(targets))
        {
            await LoadCandidatesAsync(
                connection,
                transaction,
                chunk,
                pathCandidates,
                """
                SELECT target_key, document_id, title, source_path
                FROM (
                    SELECT link_path_norm AS target_key,
                           document_id, title, source_path,
                           ROW_NUMBER() OVER (
                               PARTITION BY link_path_norm
                               ORDER BY source_path_norm, document_id
                           ) AS row_number
                    FROM documents
                    WHERE deleted_at_ms IS NULL
                      AND link_path_norm IN ({0})
                )
                WHERE row_number <= 20
                ORDER BY target_key, source_path, document_id;
                """,
                cancellationToken).ConfigureAwait(false);
            await LoadCandidatesAsync(
                connection,
                transaction,
                chunk,
                titleCandidates,
                """
                SELECT target_key, document_id, title, source_path
                FROM (
                    SELECT title_norm AS target_key,
                           document_id, title, source_path,
                           ROW_NUMBER() OVER (
                               PARTITION BY title_norm
                               ORDER BY source_path_norm, document_id
                           ) AS row_number
                    FROM documents
                    WHERE deleted_at_ms IS NULL
                      AND title_norm IN ({0})
                )
                WHERE row_number <= 20
                ORDER BY target_key, source_path, document_id;
                """,
                cancellationToken).ConfigureAwait(false);
            await LoadAliasCandidatesAsync(
                connection,
                transaction,
                chunk,
                "path",
                pathAliasCandidates,
                cancellationToken).ConfigureAwait(false);
            await LoadAliasCandidatesAsync(
                connection,
                transaction,
                chunk,
                "title",
                titleAliasCandidates,
                cancellationToken).ConfigureAwait(false);
        }

        var result = new Dictionary<string, LinkResolution>(
            targets.Length,
            StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var candidates = GetCandidates(
                pathCandidates,
                target,
                titleCandidates,
                pathAliasCandidates,
                titleAliasCandidates);
            result[target] = ToResolution(candidates);
        }
        return result;
    }

    private static async Task LoadCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> targets,
        IDictionary<string, List<VaultLinkTarget>> destination,
        string sqlTemplate,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = AddTargetParameters(command, targets);
        command.CommandText = string.Format(
            sqlTemplate,
            string.Join(", ", parameters));
        await ReadCandidatesAsync(
            command,
            destination,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task LoadAliasCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<string> targets,
        string aliasKind,
        IDictionary<string, List<VaultLinkTarget>> destination,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = AddTargetParameters(command, targets);
        command.CommandText =
            $"""
            SELECT target_key, document_id, title, source_path
            FROM (
                SELECT document_link_aliases.alias_norm AS target_key,
                       documents.document_id, documents.title,
                       documents.source_path,
                       ROW_NUMBER() OVER (
                           PARTITION BY document_link_aliases.alias_norm
                           ORDER BY documents.source_path_norm,
                                    documents.document_id
                       ) AS row_number
                FROM document_link_aliases
                JOIN documents
                  ON documents.document_id = document_link_aliases.document_id
                WHERE documents.deleted_at_ms IS NULL
                  AND document_link_aliases.alias_kind = $aliasKind
                  AND document_link_aliases.alias_norm IN (
                      {string.Join(", ", parameters)}
                  )
            )
            WHERE row_number <= 20
            ORDER BY target_key, source_path, document_id;
            """;
        command.Parameters.AddWithValue("$aliasKind", aliasKind);
        await ReadCandidatesAsync(
            command,
            destination,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> AddTargetParameters(
        SqliteCommand command,
        IReadOnlyList<string> targets)
    {
        var parameters = new string[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            parameters[index] = $"$target{index}";
            command.Parameters.AddWithValue(parameters[index], targets[index]);
        }
        return parameters;
    }

    private static async Task ReadCandidatesAsync(
        SqliteCommand command,
        IDictionary<string, List<VaultLinkTarget>> destination,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var targetKey = reader.GetString(0);
            if (!destination.TryGetValue(targetKey, out var candidates))
            {
                candidates = [];
                destination[targetKey] = candidates;
            }
            if (candidates.Count >= MaxCandidates)
            {
                continue;
            }
            candidates.Add(new VaultLinkTarget(
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3)));
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Chunk(
        IReadOnlyList<string> values)
    {
        for (var offset = 0; offset < values.Count; offset += QueryChunkSize)
        {
            yield return values
                .Skip(offset)
                .Take(QueryChunkSize)
                .ToArray();
        }
    }

    private static Dictionary<string, List<VaultLinkTarget>>
        NewCandidateMap()
    {
        return new Dictionary<string, List<VaultLinkTarget>>(
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<VaultLinkTarget> GetCandidates(
        IReadOnlyDictionary<string, List<VaultLinkTarget>> pathCandidates,
        string target,
        IReadOnlyDictionary<string, List<VaultLinkTarget>> titleCandidates,
        IReadOnlyDictionary<string, List<VaultLinkTarget>> pathAliasCandidates,
        IReadOnlyDictionary<string, List<VaultLinkTarget>> titleAliasCandidates)
    {
        if (pathCandidates.TryGetValue(target, out var candidates) &&
            candidates.Count > 0)
        {
            return candidates;
        }
        if (titleCandidates.TryGetValue(target, out candidates) &&
            candidates.Count > 0)
        {
            return candidates;
        }
        if (pathAliasCandidates.TryGetValue(target, out candidates) &&
            candidates.Count > 0)
        {
            return candidates;
        }
        return titleAliasCandidates.TryGetValue(target, out candidates)
            ? candidates
            : [];
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
