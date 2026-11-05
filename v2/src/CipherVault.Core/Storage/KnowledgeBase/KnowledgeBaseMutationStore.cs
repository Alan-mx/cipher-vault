using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Core.Storage.KnowledgeBase;

public static class KnowledgeBaseMutationStore
{
    public static async Task<int> GetModelVersionAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "SELECT version FROM knowledge_model_state WHERE id = 1;";
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken));
    }

    public static async Task<JsonElement?> GetBaseSnapshotAsync(
        VaultOperationContext context,
        DraftTargetObjectType type,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        return type switch
        {
            DraftTargetObjectType.Source => await GetSourceSnapshotAsync(
                context,
                targetId,
                cancellationToken),
            DraftTargetObjectType.Knowledge => await GetKnowledgeSnapshotAsync(
                context,
                targetId,
                cancellationToken),
            DraftTargetObjectType.Relation => await GetRelationSnapshotAsync(
                context,
                targetId,
                cancellationToken),
            _ => null
        };
    }

    public static async Task<(Guid Id, int Revision)> ApplySourceAsync(
        VaultOperationContext context,
        DraftTargetObjectType targetType,
        DraftOperationType operation,
        Guid? targetId,
        int? baseRevision,
        SourceMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            DraftOperationType.Create => await CreateSourceAsync(
                context,
                payload,
                now,
                cancellationToken),
            DraftOperationType.Update => await UpdateSourceAsync(
                context,
                targetId ?? throw InvalidTarget(),
                baseRevision ?? throw InvalidTarget(),
                payload,
                now,
                cancellationToken),
            _ => throw new VaultException(
                VaultErrorCode.DraftOperationInvalid,
                "Source operation is invalid.")
        };
    }

    public static async Task<(Guid Id, int Revision)> ApplyKnowledgeAsync(
        VaultOperationContext context,
        DraftOperationType operation,
        Guid? targetId,
        int? baseRevision,
        KnowledgeMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            DraftOperationType.Create => await CreateKnowledgeAsync(
                context,
                payload,
                now,
                cancellationToken),
            DraftOperationType.Update => await UpdateKnowledgeAsync(
                context,
                targetId ?? throw InvalidTarget(),
                baseRevision ?? throw InvalidTarget(),
                payload,
                now,
                cancellationToken),
            _ => throw new VaultException(
                VaultErrorCode.DraftOperationInvalid,
                "Knowledge operation is invalid.")
        };
    }

    public static async Task<(Guid Id, int Revision)> ApplyRelationAsync(
        VaultOperationContext context,
        DraftOperationType operation,
        Guid? targetId,
        int? baseRevision,
        RelationMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return operation switch
        {
            DraftOperationType.Create => await CreateRelationAsync(
                context,
                payload,
                now,
                cancellationToken),
            DraftOperationType.Update => await UpdateRelationAsync(
                context,
                targetId ?? throw InvalidTarget(),
                baseRevision ?? throw InvalidTarget(),
                payload,
                now,
                cancellationToken),
            _ => throw new VaultException(
                VaultErrorCode.DraftOperationInvalid,
                "Relation operation is invalid.")
        };
    }

    public static async Task<(Guid Id, int Revision)> ApplyArchiveAsync(
        VaultOperationContext context,
        DraftTargetObjectType targetType,
        DraftOperationType operation,
        Guid targetId,
        int baseRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return targetType switch
        {
            DraftTargetObjectType.Source => await SetSourceStatusAsync(
                context,
                targetId,
                baseRevision,
                operation,
                now,
                cancellationToken),
            DraftTargetObjectType.Knowledge => await SetKnowledgeStatusAsync(
                context,
                targetId,
                baseRevision,
                operation,
                now,
                cancellationToken),
            DraftTargetObjectType.Relation => await SetRelationStatusAsync(
                context,
                targetId,
                baseRevision,
                operation,
                null,
                now,
                cancellationToken),
            _ => throw InvalidTarget()
        };
    }

    public static async Task<(Guid Id, int Revision)> RestoreRelationAsync(
        VaultOperationContext context,
        Guid relationId,
        int baseRevision,
        RelationMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SetRelationStatusAsync(
            context,
            relationId,
            baseRevision,
            DraftOperationType.Restore,
            payload,
            now,
            cancellationToken);

    private static async Task<(Guid Id, int Revision)> CreateSourceAsync(
        VaultOperationContext context,
        SourceMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sourceId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var normalizedContent = NormalizeText(payload.ContentText);
        var nowText = now.ToUniversalTime().ToString("O");
        var modelVersion = await GetModelVersionAsync(
            context,
            cancellationToken);
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                INSERT INTO sources(
                    source_id, source_kind, title, title_key, metadata_json,
                    status, current_revision_id, created_at, updated_at, archived_at)
                VALUES(
                    $sourceId, $kind, $title, $titleKey, $metadata,
                    'active', $revisionId, $now, $now, NULL);

                INSERT INTO source_revisions(
                    source_revision_id, source_id, revision, source_kind, title,
                    metadata_json, content_format, content_text, content_hash,
                    knowledge_model_version, locator_index_version, created_at)
                VALUES(
                    $revisionId, $sourceId, 1, $kind, $title, $metadata,
                    $contentFormat, $content, $contentHash, $modelVersion, 1, $now);
                """;
            AddSourceParameters(
                command.Command,
                sourceId,
                revisionId,
                payload,
                normalizedContent,
                nowText);
            command.Command.Parameters.AddWithValue(
                "$modelVersion",
                modelVersion);
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertSourceLocatorsAsync(
            context,
            revisionId,
            normalizedContent,
            payload.ContentFormat,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "source.create",
            DraftTargetObjectType.Source,
            sourceId,
            1,
            now,
            cancellationToken);
        return (sourceId, 1);
    }

    private static async Task<(Guid Id, int Revision)> UpdateSourceAsync(
        VaultOperationContext context,
        Guid sourceId,
        int baseRevision,
        SourceMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetSourceCurrentAsync(
            context,
            sourceId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Source revision changed.");
        }
        if (current.Kind != payload.Kind)
        {
            throw new VaultException(
                VaultErrorCode.ImmutableDefinitionConflict,
                "SourceKind cannot be changed.");
        }
        var revisionId = Guid.NewGuid();
        var revision = baseRevision + 1;
        var normalizedContent = NormalizeText(payload.ContentText);
        var nowText = now.ToUniversalTime().ToString("O");
        var modelVersion = await GetModelVersionAsync(
            context,
            cancellationToken);
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                INSERT INTO source_revisions(
                    source_revision_id, source_id, revision, source_kind, title,
                    metadata_json, content_format, content_text, content_hash,
                    knowledge_model_version, locator_index_version, created_at)
                VALUES(
                    $revisionId, $sourceId, $revision, $kind, $title, $metadata,
                    $contentFormat, $content, $contentHash, $modelVersion, 1, $now);

                UPDATE sources
                SET
                    title = $title,
                    title_key = $titleKey,
                    metadata_json = $metadata,
                    current_revision_id = $revisionId,
                    updated_at = $now
                WHERE source_id = $sourceId;
                """;
            AddSourceParameters(
                command.Command,
                sourceId,
                revisionId,
                payload,
                normalizedContent,
                nowText);
            command.Command.Parameters.AddWithValue("$revision", revision);
            command.Command.Parameters.AddWithValue(
                "$modelVersion",
                modelVersion);
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertSourceLocatorsAsync(
            context,
            revisionId,
            normalizedContent,
            payload.ContentFormat,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "source.update",
            DraftTargetObjectType.Source,
            sourceId,
            revision,
            now,
            cancellationToken);
        return (sourceId, revision);
    }

    private static async Task<(Guid Id, int Revision)> SetSourceStatusAsync(
        VaultOperationContext context,
        Guid sourceId,
        int baseRevision,
        DraftOperationType operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetSourceCurrentAsync(
            context,
            sourceId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Source revision changed.");
        }
        var status = operation == DraftOperationType.Archive
            ? SourceStatus.Archived
            : SourceStatus.Active;
        var nowText = now.ToUniversalTime().ToString("O");
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE sources
                SET
                    status = $status,
                    updated_at = $now,
                    archived_at = CASE WHEN $status = 'archived' THEN $now ELSE NULL END
                WHERE source_id = $sourceId;
                """;
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
            command.Command.Parameters.AddWithValue("$now", nowText);
            command.Command.Parameters.AddWithValue(
                "$sourceId",
                sourceId.ToString("D"));
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(
            context,
            operation == DraftOperationType.Archive
                ? "source.archive"
                : "source.restore",
            DraftTargetObjectType.Source,
            sourceId,
            current.Revision,
            now,
            cancellationToken);
        return (sourceId, current.Revision);
    }

    private static async Task<(Guid Id, int Revision)> CreateKnowledgeAsync(
        VaultOperationContext context,
        KnowledgeMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var knowledgeId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        await InsertKnowledgeAsync(
            context,
            knowledgeId,
            revisionId,
            1,
            payload,
            now,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "knowledge.create",
            DraftTargetObjectType.Knowledge,
            knowledgeId,
            1,
            now,
            cancellationToken);
        return (knowledgeId, 1);
    }

    private static async Task<(Guid Id, int Revision)> UpdateKnowledgeAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int baseRevision,
        KnowledgeMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetKnowledgeCurrentAsync(
            context,
            knowledgeId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Knowledge revision changed.");
        }
        var revision = baseRevision + 1;
        var revisionId = Guid.NewGuid();
        await InsertKnowledgeAsync(
            context,
            knowledgeId,
            revisionId,
            revision,
            payload,
            now,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "knowledge.update",
            DraftTargetObjectType.Knowledge,
            knowledgeId,
            revision,
            now,
            cancellationToken);
        return (knowledgeId, revision);
    }

    private static async Task InsertKnowledgeAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        Guid revisionId,
        int revision,
        KnowledgeMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowText = now.ToUniversalTime().ToString("O");
        var normalizedContent = NormalizeText(payload.ContentMarkdown);
        var contentHash = Sha256(normalizedContent);
        var tagsJson = JsonSerializer.Serialize(payload.Tags);
        var modelVersion = await GetModelVersionAsync(
            context,
            cancellationToken);
        var exists = await KnowledgeExistsAsync(
            context,
            knowledgeId,
            cancellationToken);
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText = exists
                ?
                """
                INSERT INTO knowledge_revisions(
                    knowledge_revision_id, knowledge_id, revision,
                    knowledge_type_id, template_id, knowledge_model_version,
                    title, summary, content_markdown, content_hash,
                    properties_json, tags_json, created_at)
                VALUES(
                    $revisionId, $knowledgeId, $revision, $typeId, $templateId,
                    $modelVersion, $title, $summary, $content, $contentHash,
                    $properties, $tags, $now);

                UPDATE knowledge_items
                SET
                    title = $title,
                    title_key = $titleKey,
                    summary = $summary,
                    knowledge_type_id = $typeId,
                    current_revision_id = $revisionId,
                    updated_at = $now
                WHERE knowledge_id = $knowledgeId;
                """
                :
                """
                INSERT INTO knowledge_items(
                    knowledge_id, title, title_key, summary, knowledge_type_id,
                    status, current_revision_id, created_at, updated_at,
                    archived_at)
                VALUES(
                    $knowledgeId, $title, $titleKey, $summary, $typeId,
                    'active', $revisionId, $now, $now, NULL);

                INSERT INTO knowledge_revisions(
                    knowledge_revision_id, knowledge_id, revision,
                    knowledge_type_id, template_id, knowledge_model_version,
                    title, summary, content_markdown, content_hash,
                    properties_json, tags_json, created_at)
                VALUES(
                    $revisionId, $knowledgeId, $revision, $typeId, $templateId,
                    $modelVersion, $title, $summary, $content, $contentHash,
                    $properties, $tags, $now);
                """;
            command.Command.Parameters.AddWithValue(
                "$revisionId",
                revisionId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            command.Command.Parameters.AddWithValue("$revision", revision);
            command.Command.Parameters.AddWithValue(
                "$typeId",
                payload.KnowledgeTypeId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$templateId",
                payload.TemplateId?.ToString("D") ?? (object)DBNull.Value);
            command.Command.Parameters.AddWithValue(
                "$modelVersion",
                modelVersion);
            command.Command.Parameters.AddWithValue("$title", payload.Title);
            command.Command.Parameters.AddWithValue(
                "$titleKey",
                payload.Title.ToUpperInvariant());
            command.Command.Parameters.AddWithValue(
                "$summary",
                payload.Summary);
            command.Command.Parameters.AddWithValue(
                "$content",
                normalizedContent);
            command.Command.Parameters.AddWithValue(
                "$contentHash",
                contentHash);
            command.Command.Parameters.AddWithValue(
                "$properties",
                payload.Properties.GetRawText());
            command.Command.Parameters.AddWithValue("$tags", tagsJson);
            command.Command.Parameters.AddWithValue("$now", nowText);
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var tags = context.CreateCommand())
        {
            tags.Command.CommandText =
                "DELETE FROM knowledge_tags WHERE knowledge_id = $knowledgeId;";
            tags.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            await tags.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var tag in TagNormalizer.NormalizeMany(payload.Tags))
        {
            using var tagCommand = context.CreateCommand();
            tagCommand.Command.CommandText =
                """
                INSERT INTO knowledge_tags(knowledge_id, tag_key, tag_label)
                VALUES($knowledgeId, $tagKey, $tagLabel);
                """;
            tagCommand.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            tagCommand.Command.Parameters.AddWithValue(
                "$tagKey",
                tag.NormalizedKey);
            tagCommand.Command.Parameters.AddWithValue(
                "$tagLabel",
                tag.Name);
            await tagCommand.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var reference in payload.SourceReferences)
        {
            await InsertSourceReferenceAsync(
                context,
                revisionId,
                reference,
                now,
                cancellationToken);
        }
        await UpsertFtsAsync(
            context,
            knowledgeId,
            revisionId,
            payload,
            normalizedContent,
            cancellationToken);
    }

    private static async Task<(Guid Id, int Revision)> SetKnowledgeStatusAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int baseRevision,
        DraftOperationType operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetKnowledgeCurrentAsync(
            context,
            knowledgeId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Knowledge revision changed.");
        }
        var status = operation == DraftOperationType.Archive
            ? KnowledgeStatus.Archived
            : KnowledgeStatus.Active;
        var nowText = now.ToUniversalTime().ToString("O");
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                UPDATE knowledge_items
                SET
                    status = $status,
                    updated_at = $now,
                    archived_at = CASE WHEN $status = 'archived' THEN $now ELSE NULL END
                WHERE knowledge_id = $knowledgeId;
                """;
            command.Command.Parameters.AddWithValue(
                "$status",
                status.ToDatabaseValue());
            command.Command.Parameters.AddWithValue("$now", nowText);
            command.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(
            context,
            operation == DraftOperationType.Archive
                ? "knowledge.archive"
                : "knowledge.restore",
            DraftTargetObjectType.Knowledge,
            knowledgeId,
            current.Revision,
            now,
            cancellationToken);
        return (knowledgeId, current.Revision);
    }

    private static async Task<(Guid Id, int Revision)> CreateRelationAsync(
        VaultOperationContext context,
        RelationMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var relationId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        await InsertRelationAsync(
            context,
            relationId,
            revisionId,
            1,
            payload,
            KnowledgeRelationStatus.Active,
            now,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "relation.create",
            DraftTargetObjectType.Relation,
            relationId,
            1,
            now,
            cancellationToken);
        return (relationId, 1);
    }

    private static async Task<(Guid Id, int Revision)> UpdateRelationAsync(
        VaultOperationContext context,
        Guid relationId,
        int baseRevision,
        RelationMutationPayload payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetRelationCurrentAsync(
            context,
            relationId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Relation revision changed.");
        }
        if (current.TypeId != payload.RelationTypeId
            || current.SourceId != payload.SourceKnowledgeId
            || current.TargetId != payload.TargetKnowledgeId)
        {
            throw new VaultException(
                VaultErrorCode.ImmutableDefinitionConflict,
                "Relation identity cannot be changed.");
        }
        var revision = baseRevision + 1;
        var revisionId = Guid.NewGuid();
        await InsertRelationAsync(
            context,
            relationId,
            revisionId,
            revision,
            payload,
            KnowledgeRelationStatus.Active,
            now,
            cancellationToken);
        await InsertAuditAsync(
            context,
            "relation.update",
            DraftTargetObjectType.Relation,
            relationId,
            revision,
            now,
            cancellationToken);
        return (relationId, revision);
    }

    private static async Task<(Guid Id, int Revision)> SetRelationStatusAsync(
        VaultOperationContext context,
        Guid relationId,
        int baseRevision,
        DraftOperationType operation,
        RelationMutationPayload? payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await GetRelationCurrentAsync(
            context,
            relationId,
            cancellationToken);
        if (current.Revision != baseRevision)
        {
            throw new VaultException(
                VaultErrorCode.DraftBaseRevisionConflict,
                "Relation revision changed.");
        }
        var status = operation is DraftOperationType.Retract
            ? KnowledgeRelationStatus.Retracted
            : KnowledgeRelationStatus.Active;
        var relationPayload = payload ?? new RelationMutationPayload(
            current.TypeId,
            current.SourceId,
            current.TargetId,
            current.SourceRevision,
            current.TargetRevision,
            current.Rationale,
            current.Evidence);
        if (status == KnowledgeRelationStatus.Active
            && operation == DraftOperationType.Restore)
        {
            await EnsureRelationUniqueAsync(
                context,
                relationId,
                relationPayload,
                cancellationToken);
        }
        var revision = baseRevision + 1;
        var revisionId = Guid.NewGuid();
        await InsertRelationAsync(
            context,
            relationId,
            revisionId,
            revision,
            relationPayload,
            status,
            now,
            cancellationToken);
        await InsertAuditAsync(
            context,
            operation == DraftOperationType.Retract
                ? "relation.retract"
                : "relation.restore",
            DraftTargetObjectType.Relation,
            relationId,
            revision,
            now,
            cancellationToken);
        return (relationId, revision);
    }

    private static async Task InsertRelationAsync(
        VaultOperationContext context,
        Guid relationId,
        Guid revisionId,
        int revision,
        RelationMutationPayload payload,
        KnowledgeRelationStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureRelationEndpointsAsync(
            context,
            payload.SourceKnowledgeId,
            payload.SourceKnowledgeRevision,
            payload.TargetKnowledgeId,
            payload.TargetKnowledgeRevision,
            cancellationToken);
        if (status == KnowledgeRelationStatus.Active)
        {
            await EnsureRelationUniqueAsync(
                context,
                revision == 1 ? null : relationId,
                payload,
                cancellationToken);
        }
        var sourceRevisionId = await ReadKnowledgeRevisionIdAsync(
            context,
            payload.SourceKnowledgeId,
            payload.SourceKnowledgeRevision,
            cancellationToken);
        var targetRevisionId = await ReadKnowledgeRevisionIdAsync(
            context,
            payload.TargetKnowledgeId,
            payload.TargetKnowledgeRevision,
            cancellationToken);
        var modelVersion = await GetModelVersionAsync(
            context,
            cancellationToken);
        var nowText = now.ToUniversalTime().ToString("O");
        var exists = revision > 1;
        using var command = context.CreateCommand();
        command.Command.CommandText = exists
            ?
            """
            INSERT INTO knowledge_relation_revisions(
                relation_revision_id, relation_id, revision, relation_type_id,
                source_knowledge_id, target_knowledge_id,
                source_knowledge_revision_id, target_knowledge_revision_id,
                status, rationale, evidence_json, knowledge_model_version,
                created_at)
            VALUES(
                $revisionId, $relationId, $revision, $typeId, $sourceId,
                $targetId, $sourceRevisionId, $targetRevisionId, $status,
                $rationale, $evidence, $modelVersion, $now);

            UPDATE knowledge_relations
            SET
                status = $status,
                current_revision_id = $revisionId,
                updated_at = $now
            WHERE relation_id = $relationId;
            """
            :
            """
            INSERT INTO knowledge_relations(
                relation_id, relation_type_id, source_knowledge_id,
                target_knowledge_id, status, current_revision_id,
                created_at, updated_at)
            VALUES(
                $relationId, $typeId, $sourceId, $targetId, $status,
                $revisionId, $now, $now);
            INSERT INTO knowledge_relation_revisions(
                relation_revision_id, relation_id, revision, relation_type_id,
                source_knowledge_id, target_knowledge_id,
                source_knowledge_revision_id, target_knowledge_revision_id,
                status, rationale, evidence_json, knowledge_model_version,
                created_at)
            VALUES(
                $revisionId, $relationId, $revision, $typeId, $sourceId,
                $targetId, $sourceRevisionId, $targetRevisionId, $status,
                $rationale, $evidence, $modelVersion, $now);
            """;
        command.Command.Parameters.AddWithValue("$revision", revision);
        command.Command.Parameters.AddWithValue(
            "$revisionId",
            revisionId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$relationId",
            relationId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$typeId",
            payload.RelationTypeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            payload.SourceKnowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$targetId",
            payload.TargetKnowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$sourceRevisionId",
            sourceRevisionId);
        command.Command.Parameters.AddWithValue(
            "$targetRevisionId",
            targetRevisionId);
        command.Command.Parameters.AddWithValue(
            "$status",
            status == KnowledgeRelationStatus.Active
                ? "active"
                : "retracted");
        command.Command.Parameters.AddWithValue(
            "$rationale",
            payload.Rationale);
        command.Command.Parameters.AddWithValue(
            "$evidence",
            payload.Evidence.GetRawText());
        command.Command.Parameters.AddWithValue(
            "$modelVersion",
            modelVersion);
        command.Command.Parameters.AddWithValue("$now", nowText);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureRelationEndpointsAsync(
        VaultOperationContext context,
        Guid sourceId,
        int sourceRevision,
        Guid targetId,
        int targetRevision,
        CancellationToken cancellationToken)
    {
        _ = await ReadKnowledgeRevisionIdAsync(
            context,
            sourceId,
            sourceRevision,
            cancellationToken);
        _ = await ReadKnowledgeRevisionIdAsync(
            context,
            targetId,
            targetRevision,
            cancellationToken);
    }

    private static async Task EnsureRelationUniqueAsync(
        VaultOperationContext context,
        Guid? excludedRelationId,
        RelationMutationPayload payload,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT count(*)
            FROM knowledge_relations
            WHERE status = 'active'
              AND relation_type_id = $typeId
              AND source_knowledge_id = $sourceId
              AND target_knowledge_id = $targetId
              AND ($excludedId IS NULL OR relation_id <> $excludedId);
            """;
        command.Command.Parameters.AddWithValue(
            "$typeId",
            payload.RelationTypeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            payload.SourceKnowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$targetId",
            payload.TargetKnowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$excludedId",
            excludedRelationId?.ToString("D") ?? (object)DBNull.Value);
        var count = Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken));
        if (count > 0)
        {
            throw new VaultException(
                VaultErrorCode.RelationAlreadyExists,
                "An active relation already exists.");
        }
    }

    private static async Task<string> ReadKnowledgeRevisionIdAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        int revision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT knowledge_revision_id
            FROM knowledge_revisions
            WHERE knowledge_id = $knowledgeId AND revision = $revision;
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string id
            ? id
            : throw new VaultException(
                VaultErrorCode.KnowledgeRevisionNotFound,
                "Knowledge revision was not found.");
    }

    private static async Task InsertSourceReferenceAsync(
        VaultOperationContext context,
        Guid knowledgeRevisionId,
        SourceReferenceInput reference,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var locatorKey = reference.LocatorValue.GetRawText();
        string locatorId;
        string sourceId;
        string sourceRevisionId;
        int sourceRevision;
        using (var command = context.CreateCommand())
        {
            command.Command.CommandText =
                """
                SELECT
                    l.source_locator_id,
                    r.source_id,
                    r.source_revision_id,
                    r.revision,
                    r.content_text,
                    l.start_offset,
                    l.end_offset
                FROM source_locators AS l
                JOIN source_revisions AS r
                  ON r.source_revision_id = l.source_revision_id
                WHERE r.source_id = $sourceId
                  AND r.revision = $sourceRevision
                  AND l.locator_type = $locatorType
                  AND l.locator_key = $locatorKey;
                """;
            command.Command.Parameters.AddWithValue(
                "$sourceId",
                reference.SourceId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$sourceRevision",
                reference.SourceRevision);
            command.Command.Parameters.AddWithValue(
                "$locatorType",
                reference.LocatorType.ToDatabaseValue());
            command.Command.Parameters.AddWithValue("$locatorKey", locatorKey);
            await using var reader =
                await command.Command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new VaultException(
                    VaultErrorCode.SourceReferenceInvalid,
                    "Source reference locator was not found.");
            }
            locatorId = reader.GetString(0);
            sourceId = reader.GetString(1);
            sourceRevisionId = reader.GetString(2);
            sourceRevision = reader.GetInt32(3);
            var content = reader.GetString(4);
            var start = reader.GetInt32(5);
            var end = reader.GetInt32(6);
            if (!content.AsSpan(start, end - start)
                    .Contains(reference.Excerpt, StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.SourceReferenceInvalid,
                    "Source excerpt cannot be located in the selected revision.");
            }
        }

        using var insert = context.CreateCommand();
        insert.Command.CommandText =
            """
            INSERT INTO source_references(
                source_reference_id, knowledge_revision_id, source_id,
                source_revision_id, source_revision, source_locator_id,
                locator_type, locator_key, locator_value_json, excerpt,
                extraction_metadata_json, created_at)
            VALUES(
                $id, $knowledgeRevisionId, $sourceId, $sourceRevisionId,
                $sourceRevision, $locatorId, $locatorType, $locatorKey,
                $locatorValue, $excerpt, $metadata, $now);
            """;
        insert.Command.Parameters.AddWithValue(
            "$id",
            Guid.NewGuid().ToString("D"));
        insert.Command.Parameters.AddWithValue(
            "$knowledgeRevisionId",
            knowledgeRevisionId.ToString("D"));
        insert.Command.Parameters.AddWithValue("$sourceId", sourceId);
        insert.Command.Parameters.AddWithValue(
            "$sourceRevisionId",
            sourceRevisionId);
        insert.Command.Parameters.AddWithValue(
            "$sourceRevision",
            sourceRevision);
        insert.Command.Parameters.AddWithValue("$locatorId", locatorId);
        insert.Command.Parameters.AddWithValue(
            "$locatorType",
            reference.LocatorType.ToDatabaseValue());
        insert.Command.Parameters.AddWithValue("$locatorKey", locatorKey);
        insert.Command.Parameters.AddWithValue(
            "$locatorValue",
            reference.LocatorValue.GetRawText());
        insert.Command.Parameters.AddWithValue(
            "$excerpt",
            reference.Excerpt);
        insert.Command.Parameters.AddWithValue(
            "$metadata",
            reference.ExtractionMetadata.GetRawText());
        insert.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        await insert.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> ReadSourceRevisionIdAsync(
        VaultOperationContext context,
        Guid sourceId,
        int revision,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT source_revision_id
            FROM source_revisions
            WHERE source_id = $sourceId AND revision = $revision;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string id
            ? id
            : throw new VaultException(
                VaultErrorCode.SourceRevisionNotFound,
                "Source revision was not found.");
    }

    private static async Task UpsertFtsAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        Guid revisionId,
        KnowledgeMutationPayload payload,
        string content,
        CancellationToken cancellationToken)
    {
        using (var delete = context.CreateCommand())
        {
            delete.Command.CommandText =
                "DELETE FROM knowledge_fts WHERE knowledge_id = $knowledgeId;";
            delete.Command.Parameters.AddWithValue(
                "$knowledgeId",
                knowledgeId.ToString("D"));
            await delete.Command.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO knowledge_fts(
                knowledge_id, title, summary, type_key, tags, content)
            SELECT
                $knowledgeId, $title, $summary, t.type_key, $tags, $content
            FROM knowledge_types AS t
            WHERE t.type_id = $typeId;
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        command.Command.Parameters.AddWithValue("$title", payload.Title);
        command.Command.Parameters.AddWithValue("$summary", payload.Summary);
        command.Command.Parameters.AddWithValue(
            "$tags",
            string.Join(' ', payload.Tags));
        command.Command.Parameters.AddWithValue("$content", content);
        command.Command.Parameters.AddWithValue(
            "$typeId",
            payload.KnowledgeTypeId.ToString("D"));
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSourceLocatorsAsync(
        VaultOperationContext context,
        Guid revisionId,
        string content,
        SourceContentFormat format,
        CancellationToken cancellationToken)
    {
        var locators = SourceLocatorFactory.Build(content, format);
        var ids = new Dictionary<int, string>();
        foreach (var locator in locators)
        {
            var id = Guid.NewGuid().ToString("D");
            ids[locator.Ordinal] = id;
            using var command = context.CreateCommand();
            command.Command.CommandText =
                """
                INSERT INTO source_locators(
                    source_locator_id, source_revision_id, locator_type,
                    locator_key, locator_value_json, label, start_offset,
                    end_offset, ordinal, parent_locator_id)
                VALUES(
                    $id, $revisionId, $type, $key, $value, $label,
                    $start, $end, $ordinal, NULL);
                """;
            command.Command.Parameters.AddWithValue("$id", id);
            command.Command.Parameters.AddWithValue(
                "$revisionId",
                revisionId.ToString("D"));
            command.Command.Parameters.AddWithValue(
                "$type",
                locator.Type.ToDatabaseValue());
            command.Command.Parameters.AddWithValue("$key", locator.Key);
            command.Command.Parameters.AddWithValue(
                "$value",
                locator.Value.GetRawText());
            command.Command.Parameters.AddWithValue(
                "$label",
                locator.Label ?? (object)DBNull.Value);
            command.Command.Parameters.AddWithValue(
                "$start",
                locator.StartOffset);
            command.Command.Parameters.AddWithValue("$end", locator.EndOffset);
            command.Command.Parameters.AddWithValue(
                "$ordinal",
                locator.Ordinal);
            await command.Command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<SourceCurrent> GetSourceCurrentAsync(
        VaultOperationContext context,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT r.revision, s.source_kind
            FROM sources AS s
            JOIN source_revisions AS r
              ON r.source_revision_id = s.current_revision_id
            WHERE s.source_id = $sourceId;
            """;
        command.Command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.SourceNotFound,
                "Source was not found.");
        }
        return new SourceCurrent(
            reader.GetInt32(0),
            KnowledgeBaseValueMapping.ParseSourceKind(reader.GetString(1)));
    }

    private static async Task<KnowledgeCurrent> GetKnowledgeCurrentAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT revision
            FROM knowledge_revisions
            WHERE knowledge_revision_id = (
                SELECT current_revision_id
                FROM knowledge_items
                WHERE knowledge_id = $knowledgeId
            );
            """;
        command.Command.Parameters.AddWithValue(
            "$knowledgeId",
            knowledgeId.ToString("D"));
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return new KnowledgeCurrent(
            value is null
                ? throw new VaultException(
                    VaultErrorCode.KnowledgeNotFound,
                    "Knowledge was not found.")
                : Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    private static async Task<RelationCurrent> GetRelationCurrentAsync(
        VaultOperationContext context,
        Guid relationId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                rv.revision,
                r.relation_type_id,
                r.source_knowledge_id,
                r.target_knowledge_id,
                source_r.revision,
                target_r.revision,
                rv.rationale,
                rv.evidence_json
            FROM knowledge_relations AS r
            JOIN knowledge_relation_revisions AS rv
              ON rv.relation_revision_id = r.current_revision_id
            JOIN knowledge_revisions AS source_r
              ON source_r.knowledge_revision_id =
                 rv.source_knowledge_revision_id
            JOIN knowledge_revisions AS target_r
              ON target_r.knowledge_revision_id =
                 rv.target_knowledge_revision_id
            WHERE r.relation_id = $relationId;
            """;
        command.Command.Parameters.AddWithValue(
            "$relationId",
            relationId.ToString("D"));
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new VaultException(
                VaultErrorCode.RelationNotFound,
                "Relation was not found.");
        }
        return new RelationCurrent(
            reader.GetInt32(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            ParseJson(reader.GetString(7)));
    }

    private static async Task<bool> KnowledgeExistsAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "SELECT count(*) FROM knowledge_items WHERE knowledge_id = $id;";
        command.Command.Parameters.AddWithValue(
            "$id",
            knowledgeId.ToString("D"));
        return Convert.ToInt32(
            await command.Command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<JsonElement?> GetSourceSnapshotAsync(
        VaultOperationContext context,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT json_object(
                'sourceId', s.source_id,
                'title', s.title,
                'metadata', json(s.metadata_json),
                'kind', s.source_kind,
                'status', s.status,
                'revision', r.revision
            )
            FROM sources AS s
            JOIN source_revisions AS r
              ON r.source_revision_id = s.current_revision_id
            WHERE s.source_id = $id;
            """;
        command.Command.Parameters.AddWithValue("$id", sourceId.ToString("D"));
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? ParseJson(json) : null;
    }

    private static async Task<JsonElement?> GetKnowledgeSnapshotAsync(
        VaultOperationContext context,
        Guid knowledgeId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT json_object(
                'knowledgeId', k.knowledge_id,
                'title', k.title,
                'summary', k.summary,
                'knowledgeTypeId', k.knowledge_type_id,
                'status', k.status,
                'revision', r.revision,
                'properties', json(r.properties_json),
                'tags', json(r.tags_json)
            )
            FROM knowledge_items AS k
            JOIN knowledge_revisions AS r
              ON r.knowledge_revision_id = k.current_revision_id
            WHERE k.knowledge_id = $id;
            """;
        command.Command.Parameters.AddWithValue("$id", knowledgeId.ToString("D"));
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? ParseJson(json) : null;
    }

    private static async Task<JsonElement?> GetRelationSnapshotAsync(
        VaultOperationContext context,
        Guid relationId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT json_object(
                'relationId', r.relation_id,
                'relationTypeId', r.relation_type_id,
                'sourceKnowledgeId', r.source_knowledge_id,
                'targetKnowledgeId', r.target_knowledge_id,
                'status', r.status,
                'revision', rv.revision,
                'rationale', rv.rationale,
                'evidence', json(rv.evidence_json)
            )
            FROM knowledge_relations AS r
            JOIN knowledge_relation_revisions AS rv
              ON rv.relation_revision_id = r.current_revision_id
            WHERE r.relation_id = $id;
            """;
        command.Command.Parameters.AddWithValue("$id", relationId.ToString("D"));
        var value = await command.Command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? ParseJson(json) : null;
    }

    private static void AddSourceParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        Guid sourceId,
        Guid revisionId,
        SourceMutationPayload payload,
        string content,
        string nowText)
    {
        command.Parameters.AddWithValue(
            "$sourceId",
            sourceId.ToString("D"));
        command.Parameters.AddWithValue(
            "$revisionId",
            revisionId.ToString("D"));
        command.Parameters.AddWithValue(
            "$kind",
            payload.Kind.ToDatabaseValue());
        command.Parameters.AddWithValue("$title", payload.Title.Trim());
        command.Parameters.AddWithValue(
            "$titleKey",
            payload.Title.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue(
            "$metadata",
            payload.Metadata.GetRawText());
        command.Parameters.AddWithValue(
            "$contentFormat",
            payload.ContentFormat.ToDatabaseValue());
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$contentHash", Sha256(content));
        command.Parameters.AddWithValue("$now", nowText);
    }

    private static async Task InsertAuditAsync(
        VaultOperationContext context,
        string action,
        DraftTargetObjectType type,
        Guid targetId,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at, actor_kind, action, target_object_type,
                target_id, target_revision, correlation_id, result, details_json)
            VALUES(
                $eventId, $occurredAt, 'user_ui', $action, $targetType,
                $targetId, $revision, NULL, 'success', '{}');
            """;
        command.Command.Parameters.AddWithValue(
            "$eventId",
            Guid.NewGuid().ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$occurredAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue("$action", action);
        command.Command.Parameters.AddWithValue(
            "$targetType",
            type.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$targetId",
            targetId.ToString("D"));
        command.Command.Parameters.AddWithValue("$revision", revision);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string Sha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static VaultException InvalidTarget() =>
        new(
            VaultErrorCode.DraftTargetInvalid,
            "Draft target or base revision is invalid.");

    private sealed record SourceCurrent(int Revision, SourceKind Kind);
    private sealed record KnowledgeCurrent(int Revision);
    private sealed record RelationCurrent(
        int Revision,
        Guid TypeId,
        Guid SourceId,
        Guid TargetId,
        int SourceRevision,
        int TargetRevision,
        string Rationale,
        JsonElement Evidence);
}
