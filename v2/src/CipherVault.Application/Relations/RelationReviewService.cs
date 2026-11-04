using System.Text.Json;
using CipherVault.Application.Drafts;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;
using CipherVault.Core.Storage.Relations;

namespace CipherVault.Application.Relations;

public sealed class RelationReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly VaultSessionManager sessionManager;
    private readonly IdempotencyService idempotency;
    private readonly TimeProvider timeProvider;

    public RelationReviewService(
        VaultSessionManager sessionManager,
        IdempotencyService idempotency,
        TimeProvider? timeProvider = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.idempotency = idempotency ??
            throw new ArgumentNullException(nameof(idempotency));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RelationSuggestionApplyOutcome> ApplyAsync(
        Guid suggestionId,
        int expectedSuggestionRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeHash(
            IdempotencyOperation.RelationSuggestionApply,
            actor,
            new { suggestionId, expectedSuggestionRevision });

        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay =
                        await idempotency.TryReplayAsync(
                            context,
                            idempotencyKey,
                            IdempotencyOperation.RelationSuggestionApply,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<RelationSuggestionApplyOutcome>(replay);
                    }
                    var current = await RelationSuggestionStore.GetAsync(
                        context,
                        suggestionId,
                        token);
                    EnsureSuggestionRevision(
                        expectedSuggestionRevision,
                        current);
                    if (current.Status !=
                        RelationSuggestionStatus.PendingReview)
                    {
                        throw NotEditable(current);
                    }

                    var documents = await RelationDocumentStore.ReadAsync(
                        context,
                        [
                            current.SourceDocumentId,
                            current.TargetDocumentId
                        ],
                        token);
                    var staleReason = DetectStaleReason(current, documents);
                    var now = timeProvider.GetUtcNow();
                    if (staleReason is not null)
                    {
                        var stale = current with
                        {
                            SuggestionRevision =
                                expectedSuggestionRevision + 1,
                            Status = RelationSuggestionStatus.Stale,
                            StaleReason = staleReason,
                            UpdatedAt = now,
                            TerminalAt = null,
                            AppliedAt = null,
                            RelationId = null,
                            ApplyResult =
                                RelationSuggestionApplyResult.None
                        };
                        await RelationSuggestionStore.UpdateAsync(
                            context,
                            expectedSuggestionRevision,
                            stale,
                            token);
                        await AuditStore.AppendAsync(
                            context,
                            actor,
                            "relation_suggestion.stale",
                            documentId: stale.SourceDocumentId,
                            revision: stale.SourceDocumentRevision,
                            result: "success",
                            details: AuditDetails(context, stale),
                            occurredAt: now,
                            cancellationToken: token);
                        int? sourceRevision = documents.TryGetValue(
                            current.SourceDocumentId,
                            out var source)
                            ? source.Revision
                            : null;
                        int? targetRevision = documents.TryGetValue(
                            current.TargetDocumentId,
                            out var target)
                            ? target.Revision
                            : null;
                        var staleOutcome = new RelationSuggestionApplyOutcome(
                            stale.Status,
                            stale.SuggestionId,
                            stale.SuggestionRevision,
                            RelationId: null,
                            RelationSuggestionApplyResult.None,
                            AppliedAt: null,
                            sourceRevision,
                            targetRevision,
                            staleReason);
                        await idempotency.SaveAsync(
                            context,
                            idempotencyKey,
                            IdempotencyOperation.RelationSuggestionApply,
                            actor,
                            sessionId: null,
                            requestHash,
                            staleOutcome,
                            now,
                            token);
                        await context.CommitTransactionAsync(token);
                        return staleOutcome;
                    }

                    var (relation, inserted) =
                        await DocumentRelationStore.EnsureActiveAsync(
                            context,
                            current.RelationType,
                            current.SourceDocumentId,
                            current.TargetDocumentId,
                            now,
                            token);
                    var applyResult = inserted
                        ? RelationSuggestionApplyResult.Inserted
                        : RelationSuggestionApplyResult.AlreadyExists;
                    var next = current with
                    {
                        SuggestionRevision =
                            expectedSuggestionRevision + 1,
                        Status = RelationSuggestionStatus.Applied,
                        RelationId = relation.RelationId,
                        ApplyResult = applyResult,
                        StaleReason = null,
                        UpdatedAt = now,
                        TerminalAt = now,
                        AppliedAt = now
                    };
                    await RelationSuggestionStore.UpdateAsync(
                        context,
                        expectedSuggestionRevision,
                        next,
                        token);
                    await AuditStore.AppendAsync(
                        context,
                        actor,
                        "relation_suggestion.apply",
                        documentId: next.SourceDocumentId,
                        revision: next.SourceDocumentRevision,
                        result: "success",
                        details: AuditDetails(context, next),
                        occurredAt: now,
                        cancellationToken: token);
                    var outcome = new RelationSuggestionApplyOutcome(
                        next.Status,
                        next.SuggestionId,
                        next.SuggestionRevision,
                        relation.RelationId,
                        applyResult,
                        now,
                        CurrentSourceRevision: null,
                        CurrentTargetRevision: null,
                        StaleReason: null);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.RelationSuggestionApply,
                        actor,
                        sessionId: null,
                        requestHash,
                        outcome,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return outcome;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken).AsTask();
    }

    public Task<RelationSuggestionMutationResult> RejectAsync(
        Guid suggestionId,
        int expectedSuggestionRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeHash(
            IdempotencyOperation.RelationSuggestionReject,
            actor,
            new { suggestionId, expectedSuggestionRevision });

        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay =
                        await idempotency.TryReplayAsync(
                            context,
                            idempotencyKey,
                            IdempotencyOperation.RelationSuggestionReject,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<RelationSuggestionMutationResult>(replay);
                    }
                    var current = await RelationSuggestionStore.GetAsync(
                        context,
                        suggestionId,
                        token);
                    EnsureSuggestionRevision(
                        expectedSuggestionRevision,
                        current);
                    if (current.Status !=
                        RelationSuggestionStatus.PendingReview)
                    {
                        throw NotEditable(current);
                    }
                    var now = timeProvider.GetUtcNow();
                    var next = current with
                    {
                        SuggestionRevision =
                            expectedSuggestionRevision + 1,
                        Status = RelationSuggestionStatus.Rejected,
                        StaleReason = null,
                        UpdatedAt = now,
                        TerminalAt = now,
                        AppliedAt = null,
                        RelationId = null,
                        ApplyResult = RelationSuggestionApplyResult.None
                    };
                    await RelationSuggestionStore.UpdateAsync(
                        context,
                        expectedSuggestionRevision,
                        next,
                        token);
                    await AuditStore.AppendAsync(
                        context,
                        actor,
                        "relation_suggestion.reject",
                        documentId: next.SourceDocumentId,
                        revision: next.SourceDocumentRevision,
                        result: "success",
                        details: AuditDetails(context, next),
                        occurredAt: now,
                        cancellationToken: token);
                    var result = new RelationSuggestionMutationResult(
                        next.SuggestionId,
                        next.SuggestionRevision,
                        next.Status,
                        now);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.RelationSuggestionReject,
                        actor,
                        sessionId: null,
                        requestHash,
                        result,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return result;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken).AsTask();
    }

    public Task<DocumentRelationRetractResult> RetractAsync(
        Guid relationId,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeHash(
            IdempotencyOperation.DocumentRelationRetract,
            actor,
            new { relationId });

        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay =
                        await idempotency.TryReplayAsync(
                            context,
                            idempotencyKey,
                            IdempotencyOperation.DocumentRelationRetract,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<DocumentRelationRetractResult>(replay);
                    }
                    var now = timeProvider.GetUtcNow();
                    var relation =
                        await DocumentRelationStore.RetractAsync(
                            context,
                            relationId,
                            now,
                            token);
                    var auditEventId = await AuditStore.AppendAsync(
                        context,
                        actor,
                        "document_relation.retract",
                        documentId: relation.SourceDocumentId,
                        revision: null,
                        result: "success",
                        details: new Dictionary<string, object?>
                        {
                            ["relationId"] = relation.RelationId,
                            ["relationType"] =
                                relation.RelationType.ToDatabaseValue(),
                            ["status"] =
                                relation.Status.ToDatabaseValue(),
                            ["sessionGeneration"] =
                                context.SessionGeneration
                        },
                        occurredAt: now,
                        cancellationToken: token);
                    var result = new DocumentRelationRetractResult(
                        relation.RelationId,
                        relation.Status,
                        relation.RetractedAt!.Value,
                        auditEventId);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.DocumentRelationRetract,
                        actor,
                        sessionId: null,
                        requestHash,
                        result,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return result;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken).AsTask();
    }

    public Task<DocumentRelationsView> ListDocumentRelationsAsync(
        Guid documentId,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var documents = await RelationDocumentStore.ReadAsync(
                    context,
                    [documentId],
                    token);
                if (!documents.TryGetValue(documentId, out var document)
                    || document.IsDeleted)
                {
                    throw new VaultException(
                        VaultErrorCode.DocumentNotFound,
                        "Document was not found.");
                }
                return await DocumentRelationStore.ListForDocumentAsync(
                    context,
                    documentId,
                    token);
            },
            cancellationToken).AsTask();
    }

    private static RelationStaleReason? DetectStaleReason(
        RelationSuggestion suggestion,
        IReadOnlyDictionary<Guid, RelationDocumentSnapshot> documents)
    {
        documents.TryGetValue(suggestion.SourceDocumentId, out var source);
        documents.TryGetValue(suggestion.TargetDocumentId, out var target);
        if (source is null || target is null)
        {
            return source is null
                ? RelationStaleReason.SourceMissing
                : RelationStaleReason.TargetMissing;
        }
        var sourceChanged = source.IsDeleted
            || source.Revision != suggestion.SourceDocumentRevision;
        var targetChanged = target.IsDeleted
            || target.Revision != suggestion.TargetDocumentRevision;
        if (!sourceChanged && !targetChanged)
        {
            return null;
        }
        if (source.IsDeleted && target.IsDeleted)
        {
            return RelationStaleReason.SourceAndTargetTrashed;
        }
        if (source.IsDeleted)
        {
            return RelationStaleReason.SourceTrashed;
        }
        if (target.IsDeleted)
        {
            return RelationStaleReason.TargetTrashed;
        }
        if (source.Revision != suggestion.SourceDocumentRevision
            && target.Revision != suggestion.TargetDocumentRevision)
        {
            return RelationStaleReason.SourceAndTargetRevisionChanged;
        }
        return source.Revision != suggestion.SourceDocumentRevision
            ? RelationStaleReason.SourceRevisionChanged
            : RelationStaleReason.TargetRevisionChanged;
    }

    private static void EnsureSuggestionRevision(
        int expected,
        RelationSuggestion current)
    {
        if (expected == current.SuggestionRevision)
        {
            return;
        }
        throw new VaultException(
            VaultErrorCode.RelationSuggestionRevisionConflict,
            "Relation suggestion revision changed.",
            details: new Dictionary<string, object?>
            {
                ["currentSuggestionRevision"] =
                    current.SuggestionRevision,
                ["status"] = current.Status.ToDatabaseValue()
            });
    }

    private static VaultException NotEditable(RelationSuggestion current) =>
        new(
            VaultErrorCode.RelationSuggestionNotEditable,
            "Relation suggestion status does not allow this operation.",
            details: new Dictionary<string, object?>
            {
                ["status"] = current.Status.ToDatabaseValue()
            });

    private static void EnsureUserUi(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web UI can review relation suggestions.");
        }
    }

    private static string ComputeHash(
        IdempotencyOperation operation,
        VaultActorContext actor,
        object request)
    {
        var element = JsonSerializer.SerializeToElement(
            request,
            JsonOptions);
        return IdempotencyRequestHasher.Compute(
            operation,
            actor.Kind,
            sessionId: null,
            element);
    }

    private static IReadOnlyDictionary<string, object?> AuditDetails(
        VaultOperationContext context,
        RelationSuggestion suggestion) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["suggestionId"] = suggestion.SuggestionId,
            ["suggestionRevision"] = suggestion.SuggestionRevision,
            ["relationId"] = suggestion.RelationId,
            ["sourceDocumentId"] = suggestion.SourceDocumentId,
            ["sourceDocumentRevision"] =
                suggestion.SourceDocumentRevision,
            ["targetDocumentId"] = suggestion.TargetDocumentId,
            ["targetDocumentRevision"] =
                suggestion.TargetDocumentRevision,
            ["relationType"] =
                suggestion.RelationType.ToDatabaseValue(),
            ["status"] = suggestion.Status.ToDatabaseValue(),
            ["applyResult"] =
                suggestion.ApplyResult.ToDatabaseValue(),
            ["sessionGeneration"] = context.SessionGeneration
        };
}
