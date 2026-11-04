using System.Text;
using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;
using CipherVault.Core.Storage.Documents;
using CipherVault.Core.Storage.Drafts;

namespace CipherVault.Application.Drafts;

public sealed class DocumentDraftService
{
    private const int MaxNonTerminalDrafts = 200;
    private const long MaxNonTerminalBytes = 512L * 1024 * 1024;
    private const int MaxSingleJsonBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly VaultSessionManager sessionManager;
    private readonly IdempotencyService idempotency;
    private readonly TimeProvider timeProvider;

    public DocumentDraftService(
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

    public Task<DocumentDraftPage> ListAsync(
        DocumentDraftQuery query,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var now = timeProvider.GetUtcNow();
                var records = await DocumentDraftStore.ListAsync(
                    context,
                    query,
                    now,
                    token);
                return new DocumentDraftPage(
                    records.Items.Select(ToMetadata).ToArray(),
                    records.NextCursor);
            },
            cancellationToken).AsTask();

    public Task<DocumentDraftDetail> GetAsync(
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                var record = await DocumentDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                return ToDetail(record);
            },
            cancellationToken).AsTask();

    public async Task<DocumentDraftMutationResult> CreateAsync(
        CreateDocumentDraftRequest request,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureDraftActor(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.DraftCreate,
            actor,
            new
            {
                draftType = request.DraftType.ToDatabaseValue(),
                targetDocumentId = request.TargetDocumentId,
                baseDocumentRevision = request.BaseDocumentRevision,
                payload = request.Payload
            });

        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var replay = await idempotency.TryReplayAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftCreate,
                    actor,
                    sessionId: null,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return IdempotencyService.Replay<DocumentDraftMutationResult>(
                        replay);
                }

                ValidateCreateShape(request);
                JsonElement persistedPayload = request.Payload;
                var payloadVersion = 1;
                DocumentDraftSnapshot? snapshot = null;
                if (request.DraftType == DocumentDraftType.Create)
                {
                    var prepared = DocumentDraftPayloadEngine.PrepareCreatePayload(
                        request.Payload,
                        Guid.NewGuid());
                    persistedPayload = prepared.PersistedPayload;
                    payloadVersion = 2;
                }
                else
                {
                    if (request.TargetDocumentId is not { } targetDocumentId
                        || request.BaseDocumentRevision is not { } baseRevision)
                    {
                        throw InvalidDraftOperation(
                            "Target document and base revision are required.");
                    }
                    snapshot = await ReadSnapshotAsync(
                        context,
                        targetDocumentId,
                        baseRevision,
                        token);
                }

                var payloadJson = SerializePayloadText(persistedPayload);
                EnsureSingleJsonSize(payloadJson);
                if (snapshot is not null)
                {
                    var temporary = CreateTemporaryRecord(
                        request.DraftType,
                        request.TargetDocumentId!.Value,
                        request.BaseDocumentRevision!.Value,
                        payloadJson,
                        DocumentDraftPayloadEngine.SerializeSnapshot(snapshot));
                    _ = DocumentDraftPayloadEngine.ComputePreview(
                        temporary,
                        persistedPayload,
                        snapshot);
                }
                var snapshotJson = snapshot is null
                    ? null
                    : DocumentDraftPayloadEngine.SerializeSnapshot(snapshot);
                if (snapshotJson is not null)
                {
                    EnsureSingleJsonSize(snapshotJson);
                }
                await EnsureCreateLimitsAsync(
                    context,
                    payloadJson,
                    snapshotJson,
                    token);

                var now = timeProvider.GetUtcNow();
                var record = new DocumentDraftRecord(
                    Guid.NewGuid(),
                    1,
                    request.DraftType,
                    DocumentDraftStatus.Draft,
                    request.TargetDocumentId,
                    request.BaseDocumentRevision,
                    payloadVersion,
                    payloadJson,
                    snapshotJson,
                    SourceActor(actor),
                    now,
                    now,
                    null,
                    null,
                    null,
                    null);
                await DocumentDraftStore.InsertAsync(context, record, token);
                await AuditStore.AppendAsync(
                    context,
                    actor,
                    "draft.create",
                    record.TargetDocumentId,
                    null,
                    "success",
                    DraftAuditDetails(record),
                    now,
                    token);
                var result = ToMutationResult(record);
                await idempotency.SaveAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftCreate,
                    actor,
                    sessionId: null,
                    requestHash,
                    result,
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);
    }

    public async Task<DocumentDraftMutationResult> UpdateAsync(
        Guid draftId,
        int expectedDraftRevision,
        JsonElement payload,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureDraftActor(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.DraftUpdate,
            actor,
            new
            {
                draftId,
                expectedDraftRevision,
                payload
            });

        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var replay = await TryReplayAsync<DocumentDraftMutationResult>(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftUpdate,
                    actor,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await DocumentDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                EnsureRevision(expectedDraftRevision, current.DraftRevision);
                if (current.Status is not (
                    DocumentDraftStatus.Draft
                    or DocumentDraftStatus.PendingReview))
                {
                    throw DraftNotEditable(current.Status);
                }

                JsonElement persistedPayload = payload;
                var payloadVersion = current.PayloadVersion;
                DocumentDraftSnapshot? snapshot;
                if (current.DraftType == DocumentDraftType.Create)
                {
                    var futureDocumentId =
                        DocumentDraftPayloadEngine.RequireFutureDocumentId(current);
                    persistedPayload =
                        DocumentDraftPayloadEngine.PrepareCreatePayload(
                            payload,
                            futureDocumentId,
                            allowMatchingIdentity: true).PersistedPayload;
                    payloadVersion = 2;
                    snapshot = null;
                }
                else
                {
                    persistedPayload = payload;
                    snapshot = ParseRequiredSnapshot(current);
                }
                var payloadJson = SerializePayloadText(persistedPayload);
                EnsureSingleJsonSize(payloadJson);
                var temporary = current with
                {
                    PayloadJson = payloadJson,
                    PayloadVersion = payloadVersion,
                    DraftRevision = expectedDraftRevision + 1
                };
                _ = DocumentDraftPayloadEngine.ComputePreview(
                    temporary,
                    persistedPayload,
                    snapshot);
                await EnsureUpdateStorageLimitAsync(
                    context,
                    current,
                    payloadJson,
                    current.BaseSnapshotJson,
                    token);

                var now = timeProvider.GetUtcNow();
                var next = current with
                {
                    DraftRevision = expectedDraftRevision + 1,
                    Status = DocumentDraftStatus.Draft,
                    PayloadVersion = payloadVersion,
                    PayloadJson = payloadJson,
                    UpdatedAt = now,
                    TerminalAt = null
                };
                await DocumentDraftStore.UpdateAsync(
                    context,
                    expectedDraftRevision,
                    next,
                    token);
                await AppendDraftAuditAsync(
                    context,
                    actor,
                    "draft.update",
                    next,
                    now,
                    token);
                var result = ToMutationResult(next);
                await SaveReplayAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftUpdate,
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

    public async Task<DocumentDraftMutationResult> RebaseAsync(
        Guid draftId,
        int expectedDraftRevision,
        int observedDocumentRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureDraftActor(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.DraftRebase,
            actor,
            new
            {
                draftId,
                expectedDraftRevision,
                observedDocumentRevision
            });

        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var replay = await TryReplayAsync<DocumentDraftMutationResult>(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftRebase,
                    actor,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await DocumentDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                EnsureRevision(expectedDraftRevision, current.DraftRevision);
                if (current.Status != DocumentDraftStatus.Conflict
                    || current.TargetDocumentId is not { } targetDocumentId)
                {
                    throw DraftNotEditable(current.Status);
                }

                var snapshot = await ReadSnapshotAsync(
                    context,
                    targetDocumentId,
                    observedDocumentRevision,
                    token);
                var payload = ParsePayload(current);
                _ = DocumentDraftPayloadEngine.ComputePreview(
                    current,
                    payload,
                    snapshot);
                var snapshotJson =
                    DocumentDraftPayloadEngine.SerializeSnapshot(snapshot);
                EnsureSingleJsonSize(snapshotJson);
                await EnsureUpdateStorageLimitAsync(
                    context,
                    current,
                    current.PayloadJson,
                    snapshotJson,
                    token);

                var now = timeProvider.GetUtcNow();
                var next = current with
                {
                    DraftRevision = expectedDraftRevision + 1,
                    Status = DocumentDraftStatus.Draft,
                    BaseDocumentRevision = snapshot.Revision,
                    BaseSnapshotJson = snapshotJson,
                    UpdatedAt = now,
                    TerminalAt = null
                };
                await DocumentDraftStore.UpdateAsync(
                    context,
                    expectedDraftRevision,
                    next,
                    token);
                await AppendDraftAuditAsync(
                    context,
                    actor,
                    "draft.rebase",
                    next,
                    now,
                    token);
                var result = ToMutationResult(next);
                await SaveReplayAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftRebase,
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

    public Task<DocumentDraftMutationResult> SubmitReviewAsync(
        Guid draftId,
        int expectedDraftRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            draftId,
            expectedDraftRevision,
            actor,
            idempotencyKey,
            IdempotencyOperation.DraftSubmitReview,
            "draft.submit_review",
            [DocumentDraftStatus.Draft],
            DocumentDraftStatus.PendingReview,
            terminal: false,
            cancellationToken);

    public Task<DocumentDraftMutationResult> RejectAsync(
        Guid draftId,
        int expectedDraftRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        return TransitionAsync(
            draftId,
            expectedDraftRevision,
            actor,
            idempotencyKey,
            IdempotencyOperation.DraftReject,
            "draft.reject",
            [DocumentDraftStatus.PendingReview],
            DocumentDraftStatus.Rejected,
            terminal: true,
            cancellationToken);
    }

    public Task<DocumentDraftMutationResult> DiscardAsync(
        Guid draftId,
        int expectedDraftRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureDraftActor(actor);
        return TransitionAsync(
            draftId,
            expectedDraftRevision,
            actor,
            idempotencyKey,
            IdempotencyOperation.DraftDiscard,
            "draft.discard",
            [
                DocumentDraftStatus.Draft,
                DocumentDraftStatus.PendingReview,
                DocumentDraftStatus.Conflict
            ],
            DocumentDraftStatus.Discarded,
            terminal: true,
            cancellationToken);
    }

    public async Task<DocumentDraftApplyResult> ApplyAsync(
        Guid draftId,
        int expectedDraftRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.DraftApply,
            actor,
            new { draftId, expectedDraftRevision });
        var outcome = await sessionManager.ExecuteDocumentWriteAsync(
            actor,
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var replay =
                    await TryReplayAsync<DraftApplyInternalResult>(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.DraftApply,
                        actor,
                        requestHash,
                        token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await DocumentDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                EnsureRevision(expectedDraftRevision, current.DraftRevision);
                if (current.Status != DocumentDraftStatus.PendingReview)
                {
                    throw DraftNotEditable(current.Status);
                }

                var payload = ParsePayload(current);
                var snapshot = ParseRequiredSnapshot(current);
                var preview = DocumentDraftPayloadEngine.ComputePreview(
                    current,
                    payload,
                    snapshot);
                if (current.DraftType != DocumentDraftType.Create)
                {
                    var targetRevision = await ReadCurrentRevisionAsync(
                        context,
                        snapshot!.DocumentId,
                        token);
                    if (targetRevision != current.BaseDocumentRevision)
                    {
                        return await MarkConflictAsync(
                            context,
                            current,
                            expectedDraftRevision,
                            targetRevision,
                            actor,
                            idempotencyKey,
                            requestHash,
                            token);
                    }
                }

                var now = timeProvider.GetUtcNow();
                var writeResult = current.DraftType switch
                {
                    DocumentDraftType.Create =>
                        await DocumentMutationWriter.CreateForDraftAsync(
                            context,
                            preview.WorkingCopy.DocumentId
                                ?? throw new VaultException(
                                    VaultErrorCode.CorruptVault,
                                    "Create draft is missing its future DocumentId."),
                            DocumentDraftPayloadEngine.ToCreateRequest(
                                preview.WorkingCopy),
                            actor,
                            now,
                            token),
                    DocumentDraftType.Update =>
                        await DocumentMutationWriter.UpdateCoreAsync(
                            context,
                            snapshot!.DocumentId,
                            current.BaseDocumentRevision!.Value,
                            DocumentDraftPayloadEngine.ToUpdateRequest(
                                preview.WorkingCopy),
                            actor,
                            now,
                            "update",
                            DocumentContentLimitMode.ExistingDocumentAuthoring,
                            token),
                    DocumentDraftType.Trash =>
                        ToWriteResult(
                            await DocumentMutationWriter.DeleteAsync(
                                context,
                                snapshot!.DocumentId,
                                current.BaseDocumentRevision!.Value,
                                actor,
                                now,
                                token),
                            snapshot.VirtualPath),
                    _ => throw new ArgumentOutOfRangeException(nameof(current))
                };

                var next = current with
                {
                    DraftRevision = expectedDraftRevision + 1,
                    Status = DocumentDraftStatus.Applied,
                    UpdatedAt = now,
                    TerminalAt = now,
                    AppliedDocumentId = writeResult.DocumentId,
                    AppliedDocumentRevision = writeResult.Revision,
                    AppliedAt = now
                };
                await DocumentDraftStore.UpdateAsync(
                    context,
                    expectedDraftRevision,
                    next,
                    token);
                await AuditStore.AppendAsync(
                    context,
                    actor,
                    "draft.apply",
                    writeResult.DocumentId,
                    writeResult.Revision,
                    "success",
                    new
                    {
                        draftId = next.DraftId.ToString("D"),
                        draftRevision = next.DraftRevision,
                        writeResult.AuditEventId
                    },
                    now,
                    token);
                var result = new DraftApplyInternalResult(
                    ToMutationResult(next),
                    writeResult,
                    CurrentDocumentRevision: null);
                await SaveReplayAsync(
                    context,
                    idempotencyKey,
                    IdempotencyOperation.DraftApply,
                    actor,
                    requestHash,
                    result,
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return result;
            },
            cancellationToken);

        if (outcome.CurrentDocumentRevision is { } currentRevision)
        {
            throw DocumentRevisionConflict(currentRevision);
        }
        return new DocumentDraftApplyResult(
            outcome.Mutation.DraftId,
            outcome.Mutation.DraftRevision,
            outcome.Mutation.Status,
            outcome.WriteResult!);
    }

    public Task<DocumentDraftCleanupResult> CleanupAsync(
        CancellationToken cancellationToken = default) =>
        sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var now = timeProvider.GetUtcNow();
                var terminalBefore = now.Subtract(TerminalRetention);
                var drafts = await DocumentDraftStore.DeleteExpiredTerminalAsync(
                    context,
                    terminalBefore,
                    token);
                var idempotencyRecords =
                    await idempotency.CleanupExpiredAsync(
                        context,
                        now,
                        token);
                var actor = new VaultActorContext(VaultActorKind.System);
                await AuditStore.AppendAsync(
                    context,
                    actor,
                    "draft.cleanup",
                    null,
                    null,
                    "success",
                    new
                    {
                        deletedDrafts = drafts,
                        deletedIdempotencyRecords = idempotencyRecords
                    },
                    now,
                    token);
                await context.CommitTransactionAsync(token);
                return new DocumentDraftCleanupResult(
                    drafts,
                    idempotencyRecords);
            },
            cancellationToken).AsTask();

    private async Task<DocumentDraftMutationResult> TransitionAsync(
        Guid draftId,
        int expectedDraftRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        IdempotencyOperation operation,
        string auditAction,
        IReadOnlyCollection<DocumentDraftStatus> allowedStatuses,
        DocumentDraftStatus nextStatus,
        bool terminal,
        CancellationToken cancellationToken)
    {
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            operation,
            actor,
            new { draftId, expectedDraftRevision });
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                var replay = await TryReplayAsync<DocumentDraftMutationResult>(
                    context,
                    idempotencyKey,
                    operation,
                    actor,
                    requestHash,
                    token);
                if (replay is not null)
                {
                    return replay;
                }

                var current = await DocumentDraftStore.GetAsync(
                    context,
                    draftId,
                    token);
                EnsureRevision(expectedDraftRevision, current.DraftRevision);
                if (!allowedStatuses.Contains(current.Status))
                {
                    throw DraftNotEditable(current.Status);
                }

                var now = timeProvider.GetUtcNow();
                var next = current with
                {
                    DraftRevision = expectedDraftRevision + 1,
                    Status = nextStatus,
                    UpdatedAt = now,
                    TerminalAt = terminal ? now : null
                };
                await DocumentDraftStore.UpdateAsync(
                    context,
                    expectedDraftRevision,
                    next,
                    token);
                await AppendDraftAuditAsync(
                    context,
                    actor,
                    auditAction,
                    next,
                    now,
                    token);
                var result = ToMutationResult(next);
                await SaveReplayAsync(
                    context,
                    idempotencyKey,
                    operation,
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

    private async Task<DraftApplyInternalResult> MarkConflictAsync(
        VaultOperationContext context,
        DocumentDraftRecord current,
        int expectedDraftRevision,
        int currentDocumentRevision,
        VaultActorContext actor,
        Guid idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var next = current with
        {
            DraftRevision = expectedDraftRevision + 1,
            Status = DocumentDraftStatus.Conflict,
            UpdatedAt = now,
            TerminalAt = null
        };
        await DocumentDraftStore.UpdateAsync(
            context,
            expectedDraftRevision,
            next,
            cancellationToken);
        await AuditStore.AppendAsync(
            context,
            actor,
            "draft.update",
            next.TargetDocumentId,
            null,
            "failed",
            new
            {
                draftId = next.DraftId.ToString("D"),
                draftRevision = next.DraftRevision,
                reason = "document_revision_conflict",
                currentDocumentRevision
            },
            now,
            cancellationToken);
        var result = new DraftApplyInternalResult(
            ToMutationResult(next),
            WriteResult: null,
            currentDocumentRevision);
        await SaveReplayAsync(
            context,
            idempotencyKey,
            IdempotencyOperation.DraftApply,
            actor,
            requestHash,
            result,
            now,
            cancellationToken);
        await context.CommitTransactionAsync(cancellationToken);
        return result;
    }

    private async Task<DocumentDraftSnapshot> ReadSnapshotAsync(
        VaultOperationContext context,
        Guid documentId,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        var current = await DocumentMutationWriter.ReadCurrentForMutationAsync(
            context,
            documentId,
            cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw DocumentRevisionConflict(current.Revision);
        }
        return new DocumentDraftSnapshot(
            current.DocumentId,
            current.Revision,
            current.Title,
            current.Path,
            DocumentDraftPayloadEngine.FormatFromDatabaseValue(current.Format),
            current.Content,
            current.TagNames);
    }

    private static async Task<int> ReadCurrentRevisionAsync(
        VaultOperationContext context,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        var current = await DocumentMutationWriter.ReadCurrentForMutationAsync(
            context,
            documentId,
            cancellationToken);
        return current.Revision;
    }

    private static DocumentDraftSnapshot? ParseRequiredSnapshot(
        DocumentDraftRecord current) =>
        current.BaseSnapshotJson is null
            ? null
            : DocumentDraftPayloadEngine.ParseSnapshot(current.BaseSnapshotJson);

    private static DocumentDraftMetadata ToMetadata(
        DocumentDraftRecord record)
    {
        var payload = ParsePayload(record);
        var snapshot = ParseRequiredSnapshot(record);
        var preview = DocumentDraftPayloadEngine.ComputePreview(
            record,
            payload,
            snapshot);
        return ToMetadata(record, preview.WorkingCopy);
    }

    private static DocumentDraftDetail ToDetail(
        DocumentDraftRecord record)
    {
        var payload = record.DraftType == DocumentDraftType.Create
            ? DocumentDraftPayloadEngine.ToClientPayload(record)
            : ParsePayload(record);
        var snapshot = ParseRequiredSnapshot(record);
        var preview = DocumentDraftPayloadEngine.ComputePreview(
            record,
            payload,
            snapshot);
        return new DocumentDraftDetail(
            ToMetadata(record, preview.WorkingCopy),
            payload,
            new DocumentDraftPreview(
                preview.WorkingCopy.Content,
                preview.WorkingCopy.Tags,
                preview.UnifiedDiff,
                preview.DiffTruncated,
                preview.OperationCount));
    }

    private static DocumentDraftMetadata ToMetadata(
        DocumentDraftRecord record,
        DocumentDraftWorkingCopy workingCopy) =>
        new(
            record.DraftId,
            record.DraftRevision,
            record.DraftType,
            record.Status,
            record.TargetDocumentId,
            record.BaseDocumentRevision,
            record.DraftType == DocumentDraftType.Create
                ? workingCopy.DocumentId
                : null,
            record.SourceActor,
            workingCopy.Title,
            workingCopy.VirtualPath,
            workingCopy.Format,
            record.CreatedAt,
            record.UpdatedAt,
            record.TerminalAt,
            record.AppliedDocumentId,
            record.AppliedDocumentRevision);

    private static DocumentDraftMutationResult ToMutationResult(
        DocumentDraftRecord record) =>
        new(
            record.DraftId,
            record.DraftRevision,
            record.Status,
            record.DraftType == DocumentDraftType.Create
                ? TryGetFutureDocumentId(record)
                : null,
            record.UpdatedAt);

    private static Guid? TryGetFutureDocumentId(
        DocumentDraftRecord record)
    {
        if (record.DraftType != DocumentDraftType.Create)
        {
            return null;
        }
        try
        {
            return DocumentDraftPayloadEngine.RequireFutureDocumentId(record);
        }
        catch (VaultException)
        {
            return null;
        }
    }

    private static JsonElement ParsePayload(DocumentDraftRecord record)
    {
        try
        {
            using var document = JsonDocument.Parse(record.PayloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Draft payload JSON is invalid.",
                exception);
        }
    }

    private static string SerializePayloadText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw InvalidDraftOperation(
                "Draft payload must be a JSON object.");
        }
        return payload.GetRawText();
    }

    private static void ValidateCreateShape(
        CreateDocumentDraftRequest request)
    {
        if (!Enum.IsDefined(request.DraftType))
        {
            throw InvalidDraftOperation("Draft type is invalid.");
        }
        if (request.DraftType == DocumentDraftType.Create)
        {
            if (request.TargetDocumentId is not null
                || request.BaseDocumentRevision is not null)
            {
                throw InvalidDraftOperation(
                    "Create drafts cannot target an existing document.");
            }
            return;
        }
        if (request.TargetDocumentId is null
            || request.BaseDocumentRevision is null)
        {
            throw InvalidDraftOperation(
                "Update and trash drafts require target document and base revision.");
        }
        if (request.DraftType == DocumentDraftType.Trash)
        {
            DocumentDraftPayloadEngine.ValidateTrashPayload(request.Payload);
        }
        else
        {
            DocumentDraftPayloadEngine.ValidateUpdatePayload(request.Payload);
        }
    }

    private static DocumentDraftRecord CreateTemporaryRecord(
        DocumentDraftType draftType,
        Guid targetDocumentId,
        int baseRevision,
        string payloadJson,
        string snapshotJson)
    {
        var now = DateTimeOffset.UtcNow;
        return new DocumentDraftRecord(
            Guid.NewGuid(),
            1,
            draftType,
            DocumentDraftStatus.Draft,
            targetDocumentId,
            baseRevision,
            1,
            payloadJson,
            snapshotJson,
            "mcp",
            now,
            now,
            null,
            null,
            null,
            null);
    }

    private async Task EnsureCreateLimitsAsync(
        VaultOperationContext context,
        string payloadJson,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        var count = await DocumentDraftStore.CountNonTerminalAsync(
            context,
            cancellationToken);
        if (count >= MaxNonTerminalDrafts)
        {
            throw new VaultException(
                VaultErrorCode.DraftLimitExceeded,
                "The active document draft limit was reached.");
        }
        await EnsureStorageLimitAsync(
            context,
            existing: null,
            payloadJson,
            snapshotJson,
            cancellationToken);
    }

    private async Task EnsureUpdateStorageLimitAsync(
        VaultOperationContext context,
        DocumentDraftRecord existing,
        string payloadJson,
        string? snapshotJson,
        CancellationToken cancellationToken) =>
        await EnsureStorageLimitAsync(
            context,
            existing,
            payloadJson,
            snapshotJson,
            cancellationToken);

    private static async Task EnsureStorageLimitAsync(
        VaultOperationContext context,
        DocumentDraftRecord? existing,
        string payloadJson,
        string? snapshotJson,
        CancellationToken cancellationToken)
    {
        var total = await DocumentDraftStore.SumNonTerminalBytesAsync(
            context,
            cancellationToken);
        if (existing is not null
            && !existing.Status.IsTerminal())
        {
            total -= JsonBytes(existing.PayloadJson)
                + JsonBytes(existing.BaseSnapshotJson);
        }
        total += JsonBytes(payloadJson) + JsonBytes(snapshotJson);
        if (total > MaxNonTerminalBytes)
        {
            throw new VaultException(
                VaultErrorCode.DraftStorageLimitExceeded,
                "The active document draft storage limit was reached.");
        }
    }

    private static long JsonBytes(string? value) =>
        value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    private static void EnsureSingleJsonSize(string value)
    {
        if (JsonBytes(value) > MaxSingleJsonBytes)
        {
            throw new VaultException(
                VaultErrorCode.DraftPayloadTooLarge,
                "A draft JSON field exceeds the allowed size.");
        }
    }

    private async Task<T?> TryReplayAsync<T>(
        VaultOperationContext context,
        Guid key,
        IdempotencyOperation operation,
        VaultActorContext actor,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var record = await idempotency.TryReplayAsync(
            context,
            key,
            operation,
            actor,
            sessionId: null,
            requestHash,
            cancellationToken);
        return record is null ? default : IdempotencyService.Replay<T>(record);
    }

    private async Task SaveReplayAsync(
        VaultOperationContext context,
        Guid key,
        IdempotencyOperation operation,
        VaultActorContext actor,
        string requestHash,
        object result,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await idempotency.SaveAsync(
            context,
            key,
            operation,
            actor,
            sessionId: null,
            requestHash,
            result,
            now,
            cancellationToken);

    private static string ComputeRequestHash(
        IdempotencyOperation operation,
        VaultActorContext actor,
        object request)
    {
        var requestElement = JsonSerializer.SerializeToElement(
            request,
            JsonOptions);
        return IdempotencyRequestHasher.Compute(
            operation,
            actor.Kind,
            sessionId: null,
            requestElement);
    }

    private static void EnsureRevision(int expected, int actual)
    {
        if (expected != actual)
        {
            throw new VaultException(
                VaultErrorCode.DraftRevisionConflict,
                "Document draft revision changed.",
                details: new Dictionary<string, object?>
                {
                    ["currentDraftRevision"] = actual
                });
        }
    }

    private static void EnsureDraftActor(VaultActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind is not (VaultActorKind.UserUi or VaultActorKind.Mcp))
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "This actor cannot access document drafts.");
        }
    }

    private static void EnsureUserUi(VaultActorContext actor)
    {
        EnsureDraftActor(actor);
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web UI can perform this draft operation.");
        }
    }

    private static string SourceActor(VaultActorContext actor) =>
        actor.Kind switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => throw new VaultException(
                VaultErrorCode.Forbidden,
                "This actor cannot create drafts.")
        };

    private static object DraftAuditDetails(DocumentDraftRecord draft) =>
        new
        {
            draftId = draft.DraftId.ToString("D"),
            draftType = draft.DraftType.ToDatabaseValue(),
            draftRevision = draft.DraftRevision
        };

    private static Task AppendDraftAuditAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        string action,
        DocumentDraftRecord draft,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        AuditStore.AppendAsync(
            context,
            actor,
            action,
            draft.TargetDocumentId,
            null,
            "success",
            DraftAuditDetails(draft),
            now,
            cancellationToken);

    private static VaultException InvalidDraftOperation(string message) =>
        new(VaultErrorCode.DraftOperationInvalid, message);

    private static VaultException DraftNotEditable(
        DocumentDraftStatus status) =>
        new(
            VaultErrorCode.DraftNotEditable,
            "The document draft status does not allow this operation.",
            details: new Dictionary<string, object?>
            {
                ["status"] = status.ToDatabaseValue()
            });

    private static VaultException DocumentRevisionConflict(int currentRevision) =>
        new(
            VaultErrorCode.RevisionConflict,
            "Document revision changed.",
            details: new Dictionary<string, object?>
            {
                ["currentRevision"] = currentRevision
            });

    private static DocumentWriteResult ToWriteResult(
        TrashDeleteResult result,
        string virtualPath) =>
        new(
            result.DocumentId,
            result.Revision,
            virtualPath,
            result.DeletedAt,
            result.AuditEventId);

    private sealed record DraftApplyInternalResult(
        DocumentDraftMutationResult Mutation,
        DocumentWriteResult? WriteResult,
        int? CurrentDocumentRevision);
}

public sealed record DocumentDraftCleanupResult(
    int DeletedDrafts,
    int DeletedIdempotencyRecords);
