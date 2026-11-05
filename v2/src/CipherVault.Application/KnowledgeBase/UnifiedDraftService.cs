using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;
using CipherVault.Core.Storage.KnowledgeBase;

namespace CipherVault.Application.KnowledgeBase;

public sealed class UnifiedDraftService(
    VaultSessionManager sessionManager,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<DraftSummary> GetSummaryAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await UnifiedDraftStore.GetSummaryAsync(context, token),
            cancellationToken).AsTask();

    public Task<PagedResult<DraftListItem>> ListAsync(
        DraftQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
                await UnifiedDraftStore.ListAsync(context, query, token),
            cancellationToken).AsTask();

    public Task<DraftDetail> GetAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var record = await UnifiedDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                var model = await KnowledgeModelStore.GetAsync(context, token);
                return new DraftDetail(
                    record,
                    BuildPreview(record, model));
            },
            cancellationToken).AsTask();

    public Task<DraftMutation> CreateAsync(
        CreateDraftInput input,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            Hash("create", input),
            (_, _, _) => Task.FromResult(input),
            actor,
            idempotencyKey,
            cancellationToken);

    internal async Task<DraftMutation> CreateAsync(
        string requestHash,
        Func<
            CipherVault.Core.Database.VaultOperationContext,
            KnowledgeModelSnapshot,
            CancellationToken,
            Task<CreateDraftInput>> inputFactory,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputFactory);
        return await ExecuteIdempotentAsync(
            idempotencyKey,
            "draft.create",
            actor,
            requestHash,
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var model = await KnowledgeModelStore.GetAsync(context, token);
                var input = await inputFactory(context, model, token)
                    ?? throw new VaultException(
                        VaultErrorCode.DraftOperationInvalid,
                        "Generated Draft input is missing.");
                var now = timeProvider.GetUtcNow();
                var baseSnapshot = default(JsonElement?);
                if (input.OperationType == DraftOperationType.Create)
                {
                    if (input.TargetId is not null || input.BaseRevision is not null)
                    {
                        throw new VaultException(
                            VaultErrorCode.DraftTargetInvalid,
                            "Create Draft must not include target or base revision.");
                    }
                }
                else
                {
                    if (input.TargetId is null || input.BaseRevision is null)
                    {
                        throw new VaultException(
                            VaultErrorCode.DraftTargetInvalid,
                            "Draft target and base revision are required.");
                    }
                    baseSnapshot =
                        await KnowledgeBaseMutationStore.GetBaseSnapshotAsync(
                            context,
                            input.TargetObjectType,
                            input.TargetId.Value,
                            token)
                        ?? throw new VaultException(
                            VaultErrorCode.DraftTargetInvalid,
                            "Draft target was not found.");
                    if (ReadRevision(baseSnapshot.Value) != input.BaseRevision)
                    {
                        throw new VaultException(
                            VaultErrorCode.DraftBaseRevisionConflict,
                            "Draft base revision changed.");
                    }
                }
                ValidatePayload(
                    input.TargetObjectType,
                    input.OperationType,
                    input.Payload,
                    model);
                var record = new DraftRecord(
                    Guid.NewGuid(),
                    1,
                    input.TargetObjectType,
                    input.OperationType,
                    input.TargetId,
                    input.BaseRevision,
                    model.Version,
                    1,
                    input.Payload.Clone(),
                    baseSnapshot,
                    DraftStatus.Draft,
                    ActorValue(actor),
                    now,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null);
                await UnifiedDraftStore.InsertAsync(context, record, token);
                await InsertDraftAuditAsync(
                    context,
                    "draft.create",
                    record,
                    actor,
                    now,
                    token);
                var result = ToMutation(record);
                await SaveIdempotencyAsync(
                    context,
                    idempotencyKey,
                    "draft.create",
                    actor,
                    requestHash,
                    result,
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);
    }

    public async Task<DraftMutation> UpdateAsync(
        Guid draftId,
        int expectedRevision,
        JsonElement payload,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var requestHash = Hash("update", draftId, expectedRevision, payload);
        return await ExecuteIdempotentAsync(
            idempotencyKey,
            "draft.update",
            actor,
            requestHash,
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var current = await UnifiedDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                EnsureEditable(current, actor);
                var model = await KnowledgeModelStore.GetAsync(context, token);
                ValidatePayload(
                    current.TargetObjectType,
                    current.OperationType,
                    payload,
                    model);
                var updated = await UnifiedDraftStore.UpdateAsync(
                    context,
                    draftId,
                    expectedRevision,
                    payload.Clone(),
                    DraftStatus.Draft,
                    null,
                    null,
                    null,
                    timeProvider.GetUtcNow(),
                    token);
                var result = ToMutation(updated);
                await SaveIdempotencyAsync(
                    context,
                    idempotencyKey,
                    "draft.update",
                    actor,
                    requestHash,
                    result,
                    updated.UpdatedAt,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);
    }

    public async Task<DraftMutation> RebaseAsync(
        Guid draftId,
        int expectedRevision,
        int observedTargetRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var requestHash = Hash(
            "rebase",
            draftId,
            expectedRevision,
            observedTargetRevision);
        return await ExecuteIdempotentAsync(
            idempotencyKey,
            "draft.rebase",
            actor,
            requestHash,
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var current = await UnifiedDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                if (current.Status is not (DraftStatus.Draft or DraftStatus.Conflict)
                    || current.TargetId is null)
                {
                    throw new VaultException(
                        VaultErrorCode.DraftStateConflict,
                        "Draft cannot be rebased.");
                }
                var snapshot =
                    await KnowledgeBaseMutationStore.GetBaseSnapshotAsync(
                        context,
                        current.TargetObjectType,
                        current.TargetId.Value,
                        token)
                    ?? throw new VaultException(
                        VaultErrorCode.DraftTargetInvalid,
                        "Draft target was not found.");
                if (ReadRevision(snapshot) != observedTargetRevision)
                {
                    throw new VaultException(
                        VaultErrorCode.DraftBaseRevisionConflict,
                        "Observed target revision is stale.");
                }
                var model = await KnowledgeModelStore.GetAsync(context, token);
                ValidatePayload(
                    current.TargetObjectType,
                    current.OperationType,
                    current.Payload,
                    model);
                var updated = await UnifiedDraftStore.UpdateAsync(
                    context,
                    draftId,
                    expectedRevision,
                    current.Payload,
                    DraftStatus.Draft,
                    snapshot,
                    observedTargetRevision,
                    model.Version,
                    timeProvider.GetUtcNow(),
                    token);
                var result = ToMutation(updated);
                await SaveIdempotencyAsync(
                    context,
                    idempotencyKey,
                    "draft.rebase",
                    actor,
                    requestHash,
                    result,
                    updated.UpdatedAt,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);
    }

    public Task<DraftMutation> SubmitAsync(
        Guid draftId,
        int expectedRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeStateAsync(
            draftId,
            expectedRevision,
            DraftStatus.PendingReview,
            "draft.submit_review",
            actor,
            idempotencyKey,
            cancellationToken);

    public Task<DraftMutation> DiscardAsync(
        Guid draftId,
        int expectedRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeStateAsync(
            draftId,
            expectedRevision,
            DraftStatus.Discarded,
            "draft.discard",
            actor,
            idempotencyKey,
            cancellationToken);

    public Task<DraftMutation> RejectAsync(
        Guid draftId,
        int expectedRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeStateAsync(
            draftId,
            expectedRevision,
            DraftStatus.Rejected,
            "draft.reject",
            actor,
            idempotencyKey,
            cancellationToken);

    public async Task<DraftApplyResult> ApplyAsync(
        Guid draftId,
        int expectedRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web user can apply drafts.");
        }
        var requestHash = Hash("apply", draftId, expectedRevision);
        return await ExecuteIdempotentAsync(
            idempotencyKey,
            "draft.apply",
            actor,
            requestHash,
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var current = await UnifiedDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                if (current.Status != DraftStatus.PendingReview)
                {
                    throw new VaultException(
                        VaultErrorCode.DraftStateConflict,
                        "Draft is not pending review.");
                }
                var model = await KnowledgeModelStore.GetAsync(context, token);
                if (model.Version != current.BaseModelVersion)
                {
                    throw new VaultException(
                        VaultErrorCode.DraftModelConflict,
                        "Knowledge Model changed.");
                }
                ValidatePayload(
                    current.TargetObjectType,
                    current.OperationType,
                    current.Payload,
                    model);
                var now = timeProvider.GetUtcNow();
                var applied =
                    current.OperationType == DraftOperationType.Restore
                    && current.TargetObjectType == DraftTargetObjectType.Relation
                        ? await ApplyMutationAsync(
                            context,
                            current,
                            model,
                            now,
                            token)
                        : current.OperationType switch
                        {
                            DraftOperationType.Create
                                or DraftOperationType.Update =>
                                await ApplyMutationAsync(
                                    context,
                                    current,
                                    model,
                                    now,
                                    token),
                            DraftOperationType.Archive
                                or DraftOperationType.Restore
                                or DraftOperationType.Retract =>
                                await KnowledgeBaseMutationStore
                                    .ApplyArchiveAsync(
                                        context,
                                        current.TargetObjectType,
                                        current.OperationType,
                                        current.TargetId ?? throw InvalidTarget(),
                                        current.BaseRevision ?? throw InvalidTarget(),
                                        now,
                                        token),
                            _ => throw new VaultException(
                                VaultErrorCode.DraftOperationInvalid,
                                "Draft operation is invalid.")
                        };
                var appliedDraft = await UnifiedDraftStore.MarkAppliedAsync(
                    context,
                    draftId,
                    expectedRevision,
                    current.TargetObjectType,
                    applied.Id,
                    applied.Revision,
                    now,
                    token);
                await InsertDraftAuditAsync(
                    context,
                    "draft.apply",
                    appliedDraft,
                    actor,
                    now,
                    token);
                var result = new DraftApplyResult(
                    appliedDraft.DraftId,
                    appliedDraft.DraftRevision,
                    appliedDraft.Status,
                    current.TargetObjectType,
                    applied.Id,
                    applied.Revision);
                await SaveIdempotencyAsync(
                    context,
                    idempotencyKey,
                    "draft.apply",
                    actor,
                    requestHash,
                    result,
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);
    }

    private Task<DraftMutation> ChangeStateAsync(
        Guid draftId,
        int expectedRevision,
        DraftStatus status,
        string operation,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken) =>
        ExecuteIdempotentAsync(
            idempotencyKey,
            operation,
            actor,
            Hash(operation, draftId, expectedRevision),
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var current = await UnifiedDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                if (status == DraftStatus.PendingReview)
                {
                    var model = await KnowledgeModelStore.GetAsync(context, token);
                    if (model.Version != current.BaseModelVersion)
                    {
                        throw new VaultException(
                            VaultErrorCode.DraftModelConflict,
                            "Knowledge Model changed.");
                    }
                    ValidatePayload(
                        current.TargetObjectType,
                        current.OperationType,
                        current.Payload,
                        model);
                }
                var changed = await UnifiedDraftStore.SetStatusAsync(
                    context,
                    draftId,
                    expectedRevision,
                    status,
                    timeProvider.GetUtcNow(),
                    token);
                await InsertDraftAuditAsync(
                    context,
                    operation,
                    changed,
                    actor,
                    changed.UpdatedAt,
                    token);
                var result = ToMutation(changed);
                await SaveIdempotencyAsync(
                    context,
                    idempotencyKey,
                    operation,
                    actor,
                    Hash(operation, draftId, expectedRevision),
                    result,
                    changed.UpdatedAt,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);

    private static async Task<(Guid Id, int Revision)> ApplyMutationAsync(
        CipherVault.Core.Database.VaultOperationContext context,
        DraftRecord draft,
        KnowledgeModelSnapshot model,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return draft.TargetObjectType switch
        {
            DraftTargetObjectType.Source =>
                await KnowledgeBaseMutationStore.ApplySourceAsync(
                    context,
                    draft.TargetObjectType,
                    draft.OperationType,
                    draft.TargetId,
                    draft.BaseRevision,
                    KnowledgeBasePayloadParser.ParseSource(
                        draft.Payload,
                        model),
                    now,
                    cancellationToken),
            DraftTargetObjectType.Knowledge =>
                await KnowledgeBaseMutationStore.ApplyKnowledgeAsync(
                    context,
                    draft.OperationType,
                    draft.TargetId,
                    draft.BaseRevision,
                    KnowledgeBasePayloadParser.ParseKnowledge(
                        draft.Payload,
                        model),
                    now,
                    cancellationToken),
            DraftTargetObjectType.Relation =>
                await ApplyRelationMutationAsync(
                    context,
                    draft,
                    model,
                    now,
                    cancellationToken),
            _ => throw InvalidTarget()
        };
    }

    private static async Task<(Guid Id, int Revision)> ApplyRelationMutationAsync(
        CipherVault.Core.Database.VaultOperationContext context,
        DraftRecord draft,
        KnowledgeModelSnapshot model,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = KnowledgeBasePayloadParser.ParseRelation(
            draft.Payload,
            model);
        if (draft.OperationType == DraftOperationType.Restore)
        {
            return await KnowledgeBaseMutationStore.RestoreRelationAsync(
                context,
                draft.TargetId ?? throw InvalidTarget(),
                draft.BaseRevision ?? throw InvalidTarget(),
                payload,
                now,
                cancellationToken);
        }
        return await KnowledgeBaseMutationStore.ApplyRelationAsync(
            context,
            draft.OperationType,
            draft.TargetId,
            draft.BaseRevision,
            payload,
            now,
            cancellationToken);
    }

    private static void ValidatePayload(
        DraftTargetObjectType targetType,
        DraftOperationType operation,
        JsonElement payload,
        KnowledgeModelSnapshot model)
    {
        if (operation is DraftOperationType.Archive
            or DraftOperationType.Retract
            || (operation == DraftOperationType.Restore
                && targetType != DraftTargetObjectType.Relation))
        {
            KnowledgeBasePayloadParser.RequireEmptyPayload(payload);
            return;
        }
        switch (targetType)
        {
            case DraftTargetObjectType.Source:
                _ = KnowledgeBasePayloadParser.ParseSource(payload, model);
                break;
            case DraftTargetObjectType.Knowledge:
                _ = KnowledgeBasePayloadParser.ParseKnowledge(payload, model);
                break;
            case DraftTargetObjectType.Relation:
                _ = KnowledgeBasePayloadParser.ParseRelation(payload, model);
                break;
        }
    }

    private static DraftPreview BuildPreview(
        DraftRecord record,
        KnowledgeModelSnapshot model)
    {
        var payload = record.Payload.GetRawText();
        var title = ReadString(record.Payload, "title")
            ?? $"{record.TargetObjectType} {record.OperationType}";
        IReadOnlyList<SourceReferenceView> sourceRefs;
        try
        {
            sourceRefs =
                KnowledgeBasePayloadParser.PreviewSourceReferences(record.Payload);
        }
        catch (VaultException)
        {
            sourceRefs = [];
        }
        var (changes, changesTruncated) =
            DraftFieldDiffBuilder.Build(record);
        return new DraftPreview(
            title,
            payload.Length <= 16_384
                ? payload
                : payload[..16_384],
            payload.Length > 16_384,
            sourceRefs,
            changes,
            changesTruncated,
            DraftValidationService.Validate(record, model));
    }

    private static void EnsureEditable(
        DraftRecord draft,
        VaultActorContext actor)
    {
        var editable = draft.Status is DraftStatus.Draft or DraftStatus.Conflict
            || (draft.Status == DraftStatus.PendingReview
                && actor.Kind == VaultActorKind.UserUi);
        if (!editable)
        {
            throw new VaultException(
                VaultErrorCode.DraftStateConflict,
                "Draft is not editable.");
        }
    }

    private static int ReadRevision(JsonElement snapshot) =>
        snapshot.TryGetProperty("revision", out var value)
        && value.TryGetInt32(out var revision)
            ? revision
            : throw new VaultException(
                VaultErrorCode.DraftTargetInvalid,
                "Target snapshot revision is invalid.");

    private static string ActorValue(VaultActorContext actor) =>
        actor.Kind switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Draft actor is invalid.")
        };

    private static DraftMutation ToMutation(DraftRecord record) =>
        new(
            record.DraftId,
            record.DraftRevision,
            record.Status,
            record.TargetObjectType,
            record.OperationType,
            record.UpdatedAt);

    private async Task<T> ExecuteIdempotentAsync<T>(
        Guid key,
        string operation,
        VaultActorContext actor,
        string requestHash,
        Func<
            CipherVault.Core.Database.VaultOperationContext,
            CancellationToken,
            Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (key == Guid.Empty)
        {
            throw new VaultException(
                VaultErrorCode.IdempotencyKeyRequired,
                "An idempotency key is required.");
        }
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var existing = await KnowledgeBaseIdempotencyStore.FindAsync(
                    context,
                    key,
                    operation,
                    actor.Kind,
                    token);
                if (existing is not null)
                {
                    if (!string.Equals(
                            existing.RequestHash,
                            requestHash,
                            StringComparison.Ordinal))
                    {
                        throw KnowledgeBaseIdempotencyStore.KeyReused();
                    }
                    return Deserialize<T>(existing.ResultJson);
                }
                return await action(context, token);
            },
            cancellationToken).AsTask();
    }

    private static async Task SaveIdempotencyAsync<T>(
        CipherVault.Core.Database.VaultOperationContext context,
        Guid key,
        string operation,
        VaultActorContext actor,
        string requestHash,
        T result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024)
        {
            throw new VaultException(
                VaultErrorCode.PayloadTooLarge,
                "Idempotency result is too large.");
        }
        await KnowledgeBaseIdempotencyStore.InsertAsync(
            context,
            new KnowledgeBaseIdempotencyRecord(
                key,
                ActorValue(actor),
                operation,
                requestHash,
                json,
                now,
                now.AddDays(30)),
            cancellationToken);
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new JsonException("Result was empty.");
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Idempotency result is invalid.",
                exception);
        }
    }

    internal static string Hash(params object?[] values)
    {
        var json = JsonSerializer.Serialize(values, JsonOptions);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static async Task InsertDraftAuditAsync(
        CipherVault.Core.Database.VaultOperationContext context,
        string action,
        DraftRecord draft,
        VaultActorContext actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.Serialize(
            new
            {
                targetObjectType = draft.TargetObjectType.ToDatabaseValue(),
                operationType = draft.OperationType.ToDatabaseValue(),
                hasSourceReferences = HasSourceReferences(draft.Payload)
            },
            JsonOptions);
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO audit_events(
                event_id, occurred_at, actor_kind, action, target_object_type,
                target_id, target_revision, correlation_id, result, details_json)
            VALUES(
                $eventId, $occurredAt, $actorKind, $action, 'draft',
                $draftId, $revision, $correlationId, 'success', $details);
            """;
        command.Command.Parameters.AddWithValue(
            "$eventId",
            Guid.NewGuid().ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$occurredAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$actorKind",
            ActorValue(actor));
        command.Command.Parameters.AddWithValue("$action", action);
        command.Command.Parameters.AddWithValue(
            "$draftId",
            draft.DraftId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$revision",
            draft.DraftRevision);
        command.Command.Parameters.AddWithValue(
            "$correlationId",
            actor.CorrelationId ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue("$details", details);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool HasSourceReferences(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("sourceReferences", out var references)
        && references.ValueKind == JsonValueKind.Array
        && references.GetArrayLength() > 0;

    private static string? ReadString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static VaultException InvalidTarget() =>
        new(
            VaultErrorCode.DraftTargetInvalid,
            "Draft target is invalid.");
}
