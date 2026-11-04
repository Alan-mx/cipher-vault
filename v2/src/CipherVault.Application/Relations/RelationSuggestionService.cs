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

public sealed class RelationSuggestionService
{
    private const int MaxNonTerminalSuggestions = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly VaultSessionManager sessionManager;
    private readonly IdempotencyService idempotency;
    private readonly TimeProvider timeProvider;

    public RelationSuggestionService(
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

    public async Task<RelationSuggestionDetail> CreateAsync(
        CreateRelationSuggestionRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureMcp(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        ValidateIdentity(request);
        var reason = RelationSuggestionValidator.NormalizeReason(
            request.Reason);
        var submitter = RelationSuggestionValidator.NormalizeSubmitter(
            request.ModelName,
            request.AgentName,
            request.ClientName);
        var requestHash = ComputeHash(
            IdempotencyOperation.RelationSuggestionCreate,
            actor,
            new
            {
                relationType = request.RelationType.ToDatabaseValue(),
                sourceDocumentId = request.SourceDocumentId,
                sourceDocumentRevision = request.SourceDocumentRevision,
                targetDocumentId = request.TargetDocumentId,
                targetDocumentRevision = request.TargetDocumentRevision,
                reason,
                submitter.ModelName,
                submitter.AgentName,
                submitter.ClientName,
                evidence = request.Evidence
            });

        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay =
                        await idempotency.TryReplayAsync(
                            context,
                            idempotencyKey,
                            IdempotencyOperation.RelationSuggestionCreate,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<RelationSuggestionDetail>(replay);
                    }

                    var documents = await ReadActiveDocumentsAsync(
                        context,
                        request.SourceDocumentId,
                        request.TargetDocumentId,
                        request.SourceDocumentRevision,
                        request.TargetDocumentRevision,
                        token);
                    var evidence =
                        RelationSuggestionValidator.NormalizeEvidence(
                            request.Evidence,
                            request.SourceDocumentId,
                            request.SourceDocumentRevision,
                            request.TargetDocumentId,
                            request.TargetDocumentRevision);
                    RelationSuggestionValidator.ValidatePayloadSize(
                        reason,
                        submitter.ModelName,
                        submitter.AgentName,
                        submitter.ClientName,
                        evidence);

                    var duplicate =
                        await RelationSuggestionStore.FindPendingEdgeAsync(
                            context,
                            request.RelationType,
                            request.SourceDocumentId,
                            request.TargetDocumentId,
                            token);
                    if (duplicate is not null)
                    {
                        throw SuggestionExists(duplicate);
                    }
                    var count =
                        await RelationSuggestionStore.CountNonTerminalAsync(
                            context,
                            token);
                    if (count >= MaxNonTerminalSuggestions)
                    {
                        throw new VaultException(
                            VaultErrorCode.RelationSuggestionLimitExceeded,
                            "The active relation suggestion limit was reached.",
                            details: new Dictionary<string, object?>
                            {
                                ["limit"] = MaxNonTerminalSuggestions,
                                ["actual"] = count
                            });
                    }

                    var now = timeProvider.GetUtcNow();
                    var suggestion = new RelationSuggestion(
                        Guid.NewGuid(),
                        1,
                        1,
                        request.RelationType,
                        request.SourceDocumentId,
                        request.SourceDocumentRevision,
                        request.TargetDocumentId,
                        request.TargetDocumentRevision,
                        reason,
                        submitter.ModelName,
                        submitter.AgentName,
                        submitter.ClientName,
                        evidence,
                        RelationSuggestionStatus.PendingReview,
                        "mcp",
                        null,
                        RelationSuggestionApplyResult.None,
                        null,
                        now,
                        now,
                        null,
                        null);
                    await RelationSuggestionStore.InsertAsync(
                        context,
                        suggestion,
                        token);
                    await AuditStore.AppendAsync(
                        context,
                        actor,
                        "relation_suggestion.create",
                        documentId: suggestion.SourceDocumentId,
                        revision: suggestion.SourceDocumentRevision,
                        result: "success",
                        details: AuditDetails(context, suggestion),
                        occurredAt: now,
                        cancellationToken: token);
                    var result = BuildDetail(
                        suggestion,
                        documents[suggestion.SourceDocumentId],
                        documents[suggestion.TargetDocumentId]);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.RelationSuggestionCreate,
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
            cancellationToken);
    }

    public Task<RelationSuggestionPage> ListAsync(
        RelationSuggestionQuery query,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureReader(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
                await RelationSuggestionStore.ListAsync(
                    context,
                    query,
                    timeProvider.GetUtcNow(),
                    token),
            cancellationToken).AsTask();
    }

    public Task<RelationSuggestionDetail> GetAsync(
        Guid suggestionId,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureReader(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var suggestion = await RelationSuggestionStore.GetAsync(
                    context,
                    suggestionId,
                    token);
                var documents = await RelationDocumentStore.ReadAsync(
                    context,
                    [suggestion.SourceDocumentId, suggestion.TargetDocumentId],
                    token);
                return BuildDetail(
                    suggestion,
                    RequireDocument(documents, suggestion.SourceDocumentId),
                    RequireDocument(documents, suggestion.TargetDocumentId));
            },
            cancellationToken).AsTask();
    }

    public Task<RelationSuggestionSummary> GetSummaryAsync(
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        return sessionManager.ExecuteAsync(
            async (context, token) =>
                await RelationSuggestionStore.GetSummaryAsync(
                    context,
                    token),
            cancellationToken).AsTask();
    }

    public Task<RelationSuggestionDetail> UpdateAsync(
        Guid suggestionId,
        int expectedSuggestionRevision,
        UpdateRelationSuggestionRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureMcp(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var reason = RelationSuggestionValidator.NormalizeReason(
            request.Reason);
        var submitter = RelationSuggestionValidator.NormalizeSubmitter(
            request.ModelName,
            request.AgentName,
            request.ClientName);
        var requestHash = ComputeHash(
            IdempotencyOperation.RelationSuggestionUpdate,
            actor,
            new
            {
                suggestionId,
                expectedSuggestionRevision,
                reason,
                submitter.ModelName,
                submitter.AgentName,
                submitter.ClientName,
                evidence = request.Evidence
            });

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
                            IdempotencyOperation.RelationSuggestionUpdate,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<RelationSuggestionDetail>(replay);
                    }
                    var current = await RelationSuggestionStore.GetAsync(
                        context,
                        suggestionId,
                        token);
                    EnsureSuggestionRevision(
                        expectedSuggestionRevision,
                        current);
                    if (current.Status is not (
                        RelationSuggestionStatus.PendingReview
                        or RelationSuggestionStatus.Stale))
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
                    var source = RequireActiveDocument(
                        documents,
                        current.SourceDocumentId);
                    var target = RequireActiveDocument(
                        documents,
                        current.TargetDocumentId);
                    var evidence =
                        RelationSuggestionValidator.NormalizeEvidence(
                            request.Evidence,
                            source.DocumentId,
                            source.Revision,
                            target.DocumentId,
                            target.Revision);
                    RelationSuggestionValidator.ValidatePayloadSize(
                        reason,
                        submitter.ModelName,
                        submitter.AgentName,
                        submitter.ClientName,
                        evidence);
                    var now = timeProvider.GetUtcNow();
                    var next = current with
                    {
                        SuggestionRevision =
                            expectedSuggestionRevision + 1,
                        SourceDocumentRevision = source.Revision,
                        TargetDocumentRevision = target.Revision,
                        Reason = reason,
                        ModelName = submitter.ModelName,
                        AgentName = submitter.AgentName,
                        ClientName = submitter.ClientName,
                        Evidence = evidence,
                        Status = RelationSuggestionStatus.PendingReview,
                        RelationId = null,
                        ApplyResult = RelationSuggestionApplyResult.None,
                        StaleReason = null,
                        UpdatedAt = now,
                        TerminalAt = null,
                        AppliedAt = null
                    };
                    await RelationSuggestionStore.UpdateAsync(
                        context,
                        expectedSuggestionRevision,
                        next,
                        token);
                    await AuditStore.AppendAsync(
                        context,
                        actor,
                        "relation_suggestion.update",
                        documentId: next.SourceDocumentId,
                        revision: next.SourceDocumentRevision,
                        result: "success",
                        details: AuditDetails(context, next),
                        occurredAt: now,
                        cancellationToken: token);
                    var result = BuildDetail(next, source, target);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.RelationSuggestionUpdate,
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

    public Task<RelationSuggestionDiscardResult> DiscardAsync(
        Guid suggestionId,
        int expectedSuggestionRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureMcp(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeHash(
            IdempotencyOperation.RelationSuggestionDiscard,
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
                            IdempotencyOperation.RelationSuggestionDiscard,
                            actor,
                            sessionId: null,
                            requestHash,
                            token);
                    if (replay is not null)
                    {
                        return IdempotencyService
                            .Replay<RelationSuggestionDiscardResult>(replay);
                    }
                    var current = await RelationSuggestionStore.GetAsync(
                        context,
                        suggestionId,
                        token);
                    EnsureSuggestionRevision(
                        expectedSuggestionRevision,
                        current);
                    if (current.Status is not (
                        RelationSuggestionStatus.PendingReview
                        or RelationSuggestionStatus.Stale))
                    {
                        throw NotEditable(current);
                    }
                    var now = timeProvider.GetUtcNow();
                    if (!await RelationSuggestionStore.DeleteAsync(
                            context,
                            suggestionId,
                            expectedSuggestionRevision,
                            token))
                    {
                        throw NotEditable(
                            await RelationSuggestionStore.GetAsync(
                                context,
                                suggestionId,
                                token));
                    }
                    await AuditStore.AppendAsync(
                        context,
                        actor,
                        "relation_suggestion.discard",
                        documentId: current.SourceDocumentId,
                        revision: current.SourceDocumentRevision,
                        result: "success",
                        details: AuditDetails(context, current),
                        occurredAt: now,
                        cancellationToken: token);
                    var result = new RelationSuggestionDiscardResult(
                        suggestionId,
                        now);
                    await idempotency.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.RelationSuggestionDiscard,
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

    private async Task<IReadOnlyDictionary<Guid, RelationDocumentSnapshot>>
        ReadActiveDocumentsAsync(
            VaultOperationContext context,
            Guid sourceDocumentId,
            Guid targetDocumentId,
            int expectedSourceRevision,
            int expectedTargetRevision,
            CancellationToken cancellationToken)
    {
        var documents = await RelationDocumentStore.ReadAsync(
            context,
            [sourceDocumentId, targetDocumentId],
            cancellationToken);
        var source = RequireActiveDocument(documents, sourceDocumentId);
        var target = RequireActiveDocument(documents, targetDocumentId);
        if (source.Revision != expectedSourceRevision
            || target.Revision != expectedTargetRevision)
        {
            throw new VaultException(
                VaultErrorCode.RelationSuggestionStale,
                "Relation suggestion document revisions changed.",
                details: new Dictionary<string, object?>
                {
                    ["currentSourceRevision"] = source.Revision,
                    ["currentTargetRevision"] = target.Revision,
                    ["status"] = "stale"
                });
        }
        return documents;
    }

    private static RelationDocumentSnapshot RequireActiveDocument(
        IReadOnlyDictionary<Guid, RelationDocumentSnapshot> documents,
        Guid documentId)
    {
        if (!documents.TryGetValue(documentId, out var document)
            || document.IsDeleted)
        {
            throw new VaultException(
                VaultErrorCode.DocumentNotFound,
                "Relation document was not found.",
                details: new Dictionary<string, object?>
                {
                    ["documentId"] = documentId
                });
        }
        return document;
    }

    private static RelationSuggestionDetail BuildDetail(
        RelationSuggestion suggestion,
        RelationDocumentSnapshot source,
        RelationDocumentSnapshot target) =>
        new(
            new RelationSuggestionListItem(
                suggestion.SuggestionId,
                suggestion.SuggestionRevision,
                suggestion.RelationType,
                suggestion.Status,
                suggestion.SourceDocumentRevision,
                suggestion.TargetDocumentRevision,
                ToReference(source),
                ToReference(target),
                suggestion.SourceActor,
                suggestion.RelationId,
                suggestion.ApplyResult,
                suggestion.StaleReason,
                suggestion.CreatedAt,
                suggestion.UpdatedAt,
                suggestion.TerminalAt),
            suggestion.Reason,
            suggestion.ModelName,
            suggestion.AgentName,
            suggestion.ClientName,
            suggestion.Evidence,
            suggestion.AppliedAt);

    private static RelationDocumentReference ToReference(
        RelationDocumentSnapshot document) =>
        new(
            document.DocumentId,
            document.Title,
            document.VirtualPath,
            document.Revision);

    private static void ValidateIdentity(
        CreateRelationSuggestionRequest request)
    {
        if (!Enum.IsDefined(request.RelationType))
        {
            throw InvalidInput("relationType is invalid.");
        }
        if (request.SourceDocumentId == Guid.Empty
            || request.TargetDocumentId == Guid.Empty)
        {
            throw InvalidInput("sourceDocumentId and targetDocumentId are required.");
        }
        if (request.SourceDocumentId == request.TargetDocumentId)
        {
            throw InvalidInput(
                "Source and target documents must be different.");
        }
        if (request.SourceDocumentRevision < 1
            || request.TargetDocumentRevision < 1)
        {
            throw InvalidInput("Document revisions must be at least 1.");
        }
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

    private static VaultException SuggestionExists(
        RelationSuggestion existing) =>
        new(
            VaultErrorCode.RelationSuggestionExists,
            "A pending relation suggestion already exists.",
            details: new Dictionary<string, object?>
            {
                ["suggestionId"] = existing.SuggestionId,
                ["status"] = existing.Status.ToDatabaseValue()
            });

    private static VaultException NotEditable(RelationSuggestion current) =>
        new(
            VaultErrorCode.RelationSuggestionNotEditable,
            "Relation suggestion status does not allow this operation.",
            details: new Dictionary<string, object?>
            {
                ["status"] = current.Status.ToDatabaseValue()
            });

    private static void EnsureReader(VaultActorContext actor)
    {
        if (actor.Kind is not (VaultActorKind.UserUi or VaultActorKind.Mcp))
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "This actor cannot read relation suggestions.");
        }
    }

    private static void EnsureMcp(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.Mcp)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only MCP can modify relation suggestions.");
        }
    }

    private static void EnsureUserUi(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web UI can read relation suggestion summary.");
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

    private static RelationDocumentSnapshot RequireDocument(
        IReadOnlyDictionary<Guid, RelationDocumentSnapshot> documents,
        Guid documentId) =>
        documents.TryGetValue(documentId, out var document)
            ? document
            : throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Relation suggestion document is missing.");

    private static VaultException InvalidInput(string message) =>
        new(VaultErrorCode.InvalidInput, message);
}
