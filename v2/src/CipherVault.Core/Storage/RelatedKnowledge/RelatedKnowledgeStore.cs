using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.RelatedKnowledge;

namespace CipherVault.Core.Storage.RelatedKnowledge;

public static class RelatedKnowledgeStore
{
    private const int CandidateLimit = 200;
    private const int MetadataBatchSize = 200;

    private const string DirectNeighborExclusion =
        """
        AND NOT EXISTS (
            SELECT 1
            FROM document_links AS direct_out
            JOIN documents AS direct_target
              ON direct_target.document_id = direct_out.target_document_id
            WHERE direct_out.source_document_id = $documentId
              AND direct_out.source_revision = $sourceRevision
              AND direct_out.resolution_status = 'resolved'
              AND direct_target.deleted_at IS NULL
              AND direct_out.target_document_id = d.document_id
        )
        AND NOT EXISTS (
            SELECT 1
            FROM document_links AS direct_in
            JOIN documents AS direct_source
              ON direct_source.document_id = direct_in.source_document_id
            WHERE direct_in.target_document_id = $documentId
              AND direct_in.source_revision = direct_source.revision
              AND direct_in.resolution_status = 'resolved'
              AND direct_source.deleted_at IS NULL
              AND direct_in.source_document_id = d.document_id
        )
        """;

    public static async Task<IReadOnlyList<RelatedDocumentItem>> GetAsync(
        VaultOperationContext context,
        Guid documentId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (limit is < 1 or > 20)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Related document limit must be between 1 and 20.");
        }

        var source = await ReadDocumentAsync(
            context,
            documentId,
            cancellationToken);
        if (source is null)
        {
            throw new VaultException(
                VaultErrorCode.DocumentNotFound,
                "Related document source was not found.");
        }

        var sourceProfile = RelatedFrontMatterSignals.Read(source.FrontMatter);
        var candidates = await FindCandidatePoolAsync(
            context,
            source,
            sourceProfile,
            cancellationToken);
        var candidateIds = candidates
            .SameProject
            .Concat(candidates.SameType)
            .Concat(candidates.SharedTags)
            .Concat(candidates.SharedProperties)
            .Concat(candidates.SimilarTitle)
            .Concat(candidates.TwoHop)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (candidateIds.Length == 0)
        {
            return [];
        }

        var documents = await ReadCandidateDocumentsAsync(
            context,
            candidateIds,
            cancellationToken);
        var scored = new List<RelatedScoredCandidate>();
        foreach (var candidate in documents.Values)
        {
            var signals = RelatedKnowledgeScorer.BuildSignals(
                source,
                candidate,
                candidates.TwoHop.Contains(candidate.DocumentId));
            if (!signals.HasNonTitleSignal)
            {
                continue;
            }

            var result = RelatedKnowledgeScorer.Score(candidate, signals);
            if (result.Score >= RelatedKnowledgeScorer.MinimumScore)
            {
                scored.Add(result);
            }
        }

        return scored
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Document.UpdatedAt)
            .ThenBy(
                candidate => candidate.Document.DocumentId)
            .Take(limit)
            .Select(candidate => new RelatedDocumentItem(
                candidate.Document.DocumentId,
                candidate.Document.Title,
                candidate.Document.VirtualPath,
                candidate.Document.Revision,
                candidate.Document.Tags
                    .Select(tag => tag.Name)
                    .ToArray(),
                candidate.Score,
                candidate.Reasons
                    .Select(reason => new RelatedReason(
                        reason.Code,
                        reason.Label))
                    .ToArray(),
                candidate.Document.UpdatedAt))
            .ToArray();
    }

    private static async Task<RelatedKnowledgeDocument?> ReadDocumentAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var documents = await ReadDocumentsCoreAsync(
            context,
            [documentId],
            cancellationToken);
        return documents.GetValueOrDefault(documentId);
    }

    private static async Task<RelatedCandidatePool> FindCandidatePoolAsync(
        VaultOperationContext context,
        RelatedKnowledgeDocument source,
        RelatedFrontMatterProfile sourceProfile,
        CancellationToken cancellationToken)
    {
        var sameProject = sourceProfile.Project is null
            ? []
            : await FindTextFieldCandidatesAsync(
                context,
                source,
                "project",
                sourceProfile.Project,
                cancellationToken);
        var sameType = sourceProfile.Type is null
            ? []
            : await FindTextFieldCandidatesAsync(
                context,
                source,
                "type",
                sourceProfile.Type,
                cancellationToken);
        var sharedTags = source.Tags.Count == 0
            ? []
            : await FindSharedTagCandidatesAsync(
                context,
                source,
                cancellationToken);
        var sharedProperties = sourceProfile.Properties.Count == 0
            ? []
            : await FindSharedPropertyCandidatesAsync(
                context,
                source,
                sourceProfile.Properties,
                cancellationToken);
        var similarTitle = await FindTitleCandidatesAsync(
            context,
            source,
            cancellationToken);
        var twoHop = await FindTwoHopCandidatesAsync(
            context,
            source,
            cancellationToken);
        return new RelatedCandidatePool(
            sameProject.ToHashSet(),
            sameType.ToHashSet(),
            sharedTags.ToHashSet(),
            sharedProperties.ToHashSet(),
            similarTitle.ToHashSet(),
            twoHop.ToHashSet());
    }

    private static async Task<IReadOnlyList<Guid>> FindTextFieldCandidatesAsync(
        VaultOperationContext context,
        RelatedKnowledgeDocument source,
        string propertyName,
        string propertyValue,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            $"""
             SELECT d.document_id
             FROM documents AS d
             JOIN json_each(d.frontmatter_json) AS field
               ON field.key = $propertyName
             WHERE d.deleted_at IS NULL
               AND d.document_id <> $documentId
               AND field.type = 'text'
               AND length(trim(field.value)) > 0
               AND cv_ordinal_ignore_case_equals(
                     trim(field.value),
                     $propertyValue) = 1
               {DirectNeighborExclusion}
             ORDER BY d.updated_at DESC, d.document_id ASC
             LIMIT $limit;
             """;
        AddSourceParameters(read.Command, source);
        read.Command.Parameters.AddWithValue("$propertyName", propertyName);
        read.Command.Parameters.AddWithValue("$propertyValue", propertyValue);
        read.Command.Parameters.AddWithValue("$limit", CandidateLimit);
        return await ReadIdsAsync(read.Command, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>> FindSharedTagCandidatesAsync(
        VaultOperationContext context,
        RelatedKnowledgeDocument source,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        var tagParameters = new List<string>();
        for (var index = 0; index < source.Tags.Count; index++)
        {
            var name = $"$tag{index}";
            tagParameters.Add(name);
            read.Command.Parameters.AddWithValue(
                name,
                source.Tags[index].NormalizedKey);
        }

        read.Command.CommandText =
            $"""
             SELECT d.document_id
             FROM documents AS d
             JOIN document_tags AS dt ON dt.document_id = d.document_id
             JOIN tags AS t ON t.tag_id = dt.tag_id
             WHERE d.deleted_at IS NULL
               AND d.document_id <> $documentId
               AND t.normalized_key IN ({string.Join(", ", tagParameters)})
               {DirectNeighborExclusion}
             GROUP BY d.document_id, d.updated_at
             ORDER BY count(DISTINCT t.normalized_key) DESC,
                      d.updated_at DESC,
                      d.document_id ASC
             LIMIT $limit;
             """;
        AddSourceParameters(read.Command, source);
        read.Command.Parameters.AddWithValue("$limit", CandidateLimit);
        return await ReadIdsAsync(read.Command, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>>
        FindSharedPropertyCandidatesAsync(
            VaultOperationContext context,
            RelatedKnowledgeDocument source,
            IReadOnlyList<RelatedScalarProperty> properties,
            CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        var desired = properties
            .Select(property => new Dictionary<string, object?>
            {
                ["key"] = property.Key,
                ["type"] = property.Kind switch
                {
                    RelatedScalarKind.String => "string",
                    RelatedScalarKind.Number => "number",
                    RelatedScalarKind.Boolean => "boolean",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(properties))
                },
                ["value"] = property.Kind switch
                {
                    RelatedScalarKind.String => property.StringValue,
                    RelatedScalarKind.Number => property.NumberValue,
                    RelatedScalarKind.Boolean => property.BooleanValue,
                    _ => null
                }
            })
            .ToArray();
        read.Command.CommandText =
            $"""
             WITH desired_properties AS (
                 SELECT json_extract(value, '$.key') AS property_key,
                        json_extract(value, '$.type') AS property_type,
                        json_extract(value, '$.value') AS property_value
                 FROM json_each($propertiesJson)
             )
             SELECT d.document_id
             FROM documents AS d
             JOIN json_each(d.frontmatter_json) AS field
             JOIN desired_properties AS desired
               ON desired.property_key = field.key
             WHERE d.deleted_at IS NULL
               AND d.document_id <> $documentId
               AND (
                   (
                       desired.property_type = 'string'
                       AND field.type = 'text'
                       AND length(trim(field.value)) BETWEEN 1 AND 128
                       AND cv_ordinal_ignore_case_equals(
                           trim(field.value),
                           desired.property_value) = 1
                   )
                   OR
                   (
                       desired.property_type = 'number'
                       AND field.type IN ('integer', 'real')
                       AND field.value = desired.property_value
                   )
                   OR
                   (
                       desired.property_type = 'boolean'
                       AND field.type IN ('true', 'false')
                       AND field.value = desired.property_value
                   )
               )
               {DirectNeighborExclusion}
             GROUP BY d.document_id, d.updated_at
             ORDER BY count(DISTINCT field.key) DESC,
                      d.updated_at DESC,
                      d.document_id ASC
             LIMIT $limit;
             """;
        AddSourceParameters(read.Command, source);
        read.Command.Parameters.AddWithValue(
            "$propertiesJson",
            JsonSerializer.Serialize(desired));
        read.Command.Parameters.AddWithValue("$limit", CandidateLimit);
        return await ReadIdsAsync(read.Command, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>> FindTitleCandidatesAsync(
        VaultOperationContext context,
        RelatedKnowledgeDocument source,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        var searchTitle = RelatedTitleSimilarity.NormalizeForSearch(source.Title);
        var scalarCount = searchTitle.EnumerateRunes().Count();
        if (scalarCount >= 3)
        {
            read.Command.CommandText =
                $"""
                 SELECT d.document_id
                 FROM document_fts
                 JOIN documents AS d
                   ON d.document_id = document_fts.document_id
                 WHERE document_fts MATCH $titlePhrase
                   AND d.deleted_at IS NULL
                   AND d.document_id <> $documentId
                   {DirectNeighborExclusion}
                 ORDER BY bm25(document_fts) ASC,
                          d.updated_at DESC,
                          d.document_id ASC
                 LIMIT $limit;
                 """;
            read.Command.Parameters.AddWithValue(
                "$titlePhrase",
                BuildTitlePhrase(searchTitle));
        }
        else
        {
            read.Command.CommandText =
                $"""
                 SELECT d.document_id
                 FROM documents AS d
                 WHERE d.deleted_at IS NULL
                   AND d.document_id <> $documentId
                   AND d.title_key = $titleKey
                   {DirectNeighborExclusion}
                 ORDER BY d.updated_at DESC, d.document_id ASC
                 LIMIT $limit;
                 """;
            read.Command.Parameters.AddWithValue(
                "$titleKey",
                RelatedTitleSimilarity.Normalize(source.Title));
        }

        AddSourceParameters(read.Command, source);
        read.Command.Parameters.AddWithValue("$limit", CandidateLimit);
        return await ReadIdsAsync(read.Command, cancellationToken);
    }

    private static async Task<IReadOnlyList<Guid>> FindTwoHopCandidatesAsync(
        VaultOperationContext context,
        RelatedKnowledgeDocument source,
        CancellationToken cancellationToken)
    {
        using var read = context.CreateCommand();
        read.Command.CommandText =
            """
            WITH direct_edges AS (
                SELECT direct_out.target_document_id AS neighbor_id
                FROM document_links AS direct_out
                JOIN documents AS direct_target
                  ON direct_target.document_id = direct_out.target_document_id
                WHERE direct_out.source_document_id = $documentId
                  AND direct_out.source_revision = $sourceRevision
                  AND direct_out.resolution_status = 'resolved'
                  AND direct_target.deleted_at IS NULL
                UNION ALL
                SELECT direct_in.source_document_id AS neighbor_id
                FROM document_links AS direct_in
                JOIN documents AS direct_source
                  ON direct_source.document_id = direct_in.source_document_id
                WHERE direct_in.target_document_id = $documentId
                  AND direct_in.source_revision = direct_source.revision
                  AND direct_in.resolution_status = 'resolved'
                  AND direct_source.deleted_at IS NULL
            ),
            direct_neighbors AS (
                SELECT DISTINCT neighbor_id FROM direct_edges
            ),
            second_edges AS (
                SELECT second_out.target_document_id AS candidate_id,
                       direct_neighbors.neighbor_id AS middle_id
                FROM direct_neighbors
                JOIN document_links AS second_out
                  ON second_out.source_document_id =
                     direct_neighbors.neighbor_id
                JOIN documents AS second_source
                  ON second_source.document_id =
                     second_out.source_document_id
                JOIN documents AS second_target
                  ON second_target.document_id =
                     second_out.target_document_id
                WHERE second_out.resolution_status = 'resolved'
                  AND second_source.deleted_at IS NULL
                  AND second_target.deleted_at IS NULL
                  AND second_source.revision = second_out.source_revision
                UNION ALL
                SELECT second_in.source_document_id AS candidate_id,
                       direct_neighbors.neighbor_id AS middle_id
                FROM direct_neighbors
                JOIN document_links AS second_in
                  ON second_in.target_document_id =
                     direct_neighbors.neighbor_id
                JOIN documents AS second_source
                  ON second_source.document_id =
                     second_in.source_document_id
                JOIN documents AS second_target
                  ON second_target.document_id =
                     second_in.target_document_id
                WHERE second_in.resolution_status = 'resolved'
                  AND second_source.deleted_at IS NULL
                  AND second_target.deleted_at IS NULL
                  AND second_source.revision = second_in.source_revision
            )
            SELECT second_edges.candidate_id
            FROM second_edges
            JOIN documents AS d
              ON d.document_id = second_edges.candidate_id
            WHERE d.deleted_at IS NULL
              AND d.document_id <> $documentId
              AND second_edges.candidate_id NOT IN (
                  SELECT neighbor_id FROM direct_neighbors
              )
            GROUP BY second_edges.candidate_id, d.updated_at
            ORDER BY count(DISTINCT second_edges.middle_id) DESC,
                     d.updated_at DESC,
                     d.document_id ASC
            LIMIT $limit;
            """;
        AddSourceParameters(read.Command, source);
        read.Command.Parameters.AddWithValue("$limit", CandidateLimit);
        return await ReadIdsAsync(read.Command, cancellationToken);
    }

    private static async Task<Dictionary<Guid, RelatedKnowledgeDocument>>
        ReadCandidateDocumentsAsync(
            VaultOperationContext context,
            IReadOnlyList<Guid> candidateIds,
            CancellationToken cancellationToken)
    {
        var documents = new Dictionary<Guid, RelatedKnowledgeDocument>();
        foreach (var batch in candidateIds.Chunk(MetadataBatchSize))
        {
            var batchDocuments = await ReadDocumentsCoreAsync(
                context,
                batch,
                cancellationToken);
            foreach (var pair in batchDocuments)
            {
                documents[pair.Key] = pair.Value;
            }
        }
        return documents;
    }

    private static async Task<Dictionary<Guid, RelatedKnowledgeDocument>>
        ReadDocumentsCoreAsync(
            VaultOperationContext context,
            IReadOnlyList<Guid> documentIds,
            CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var documents = new Dictionary<Guid, RelatedKnowledgeDocument>();
        using (var read = context.CreateCommand())
        {
            var parameters = AddIdParameters(read.Command, documentIds);
            read.Command.CommandText =
            $"""
             SELECT d.document_id, d.title, d.path, d.revision,
                    d.updated_at, d.frontmatter_json
             FROM documents AS d
             WHERE d.deleted_at IS NULL
               AND d.document_id IN ({string.Join(", ", parameters)});
             """;
            await using var reader = await read.Command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                documents[id] = new RelatedKnowledgeDocument(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    ParseFrontMatter(reader.GetString(5)),
                    [],
                    ParseTimestamp(reader.GetString(4)));
            }
        }

        using var tagsRead = context.CreateCommand();
        var tagParameters = AddIdParameters(tagsRead.Command, documentIds);
        tagsRead.Command.CommandText =
            $"""
             SELECT dt.document_id, t.name, t.normalized_key
             FROM document_tags AS dt
             JOIN tags AS t ON t.tag_id = dt.tag_id
             JOIN documents AS d ON d.document_id = dt.document_id
             WHERE d.deleted_at IS NULL
               AND dt.document_id IN ({string.Join(", ", tagParameters)})
             ORDER BY dt.document_id, t.normalized_key;
             """;
        var tags = documents.Keys.ToDictionary(
            id => id,
            _ => new List<RelatedTag>());
        await using (var reader = await tagsRead.Command.ExecuteReaderAsync(
                         cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                if (tags.TryGetValue(id, out var documentTags))
                {
                    documentTags.Add(new RelatedTag(
                        reader.GetString(1),
                        reader.GetString(2)));
                }
            }
        }

        foreach (var pair in tags)
        {
            documents[pair.Key] = documents[pair.Key] with
            {
                Tags = pair.Value.ToArray()
            };
        }
        return documents;
    }

    private static void AddSourceParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        RelatedKnowledgeDocument source)
    {
        command.Parameters.AddWithValue(
            "$documentId",
            source.DocumentId.ToString("D"));
        command.Parameters.AddWithValue("$sourceRevision", source.Revision);
    }

    private static List<string> AddIdParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        IReadOnlyList<Guid> documentIds)
    {
        var parameters = new List<string>(documentIds.Count);
        for (var index = 0; index < documentIds.Count; index++)
        {
            var name = $"$id{index}";
            parameters.Add(name);
            command.Parameters.AddWithValue(
                name,
                documentIds[index].ToString("D"));
        }
        return parameters;
    }

    private static async Task<IReadOnlyList<Guid>> ReadIdsAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(Guid.Parse(reader.GetString(0)));
            }
            return ids;
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Related candidate query failed.",
                exception);
        }
    }

    private static JsonElement ParseFrontMatter(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new VaultException(
                    VaultErrorCode.DatabaseError,
                    "Document Frontmatter is not a JSON object.");
            }
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.DatabaseError,
                "Document Frontmatter could not be read.",
                exception);
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind)
        .ToUniversalTime();

    private static string BuildTitlePhrase(string title)
    {
        var escaped = title.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"title : \"{escaped}\"";
    }
}
