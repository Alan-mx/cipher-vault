using CipherVault.Application.Vault;
using CipherVault.Application.Drafts;
using CipherVault.Core.Audit;
using CipherVault.Core.Database;
using CipherVault.Core.Drafts;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;
using System.Text.Json;

namespace CipherVault.Application.Semantic;

public sealed class SemanticIndexService
{
    private readonly VaultSessionManager sessionManager;
    private readonly IEmbeddingProvider? provider;
    private readonly int profileVersion;
    private readonly bool remoteDeploymentAllowed;
    private readonly string? endpointDisplay;
    private readonly TimeProvider timeProvider;
    private readonly IdempotencyService? idempotency;

    public SemanticIndexService(
        VaultSessionManager sessionManager,
        IEmbeddingProvider? provider,
        int profileVersion,
        bool remoteDeploymentAllowed,
        string? endpointDisplay,
        TimeProvider? timeProvider = null,
        IdempotencyService? idempotency = null)
    {
        this.sessionManager = sessionManager ??
            throw new ArgumentNullException(nameof(sessionManager));
        this.provider = provider;
        this.profileVersion = profileVersion;
        this.remoteDeploymentAllowed = remoteDeploymentAllowed;
        this.endpointDisplay = endpointDisplay;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.idempotency = idempotency;
    }

    public bool IsConfigured => provider is not null;

    public bool RemoteDeploymentAllowed => remoteDeploymentAllowed;

    public IEmbeddingProvider? Provider => provider;

    public async Task EnsureProfileAsync(
        CancellationToken cancellationToken = default)
    {
        if (provider is null)
        {
            return;
        }
        await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    await EnsureProfileCoreAsync(context, token);
                    await context.CommitTransactionAsync(token);
                    return 1;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<SemanticIndexStatus> GetStatusAsync(
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                CipherVault.Core.Errors.VaultErrorCode.Forbidden,
                "Semantic index status requires a user session.");
        }
        if (provider is null)
        {
            return NotConfiguredStatus();
        }
        await EnsureProfileAsync(cancellationToken);
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
                await BuildStatusCoreAsync(context, token),
            cancellationToken);
    }

    public async Task<SemanticIndexStatus> GrantRemoteConsentAsync(
        Guid idempotencyKey,
        string profileId,
        string endpointFingerprint,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        var idempotencyService = RequireIdempotency();
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.SemanticRemoteConsentChange,
            actor,
            new { action = "grant", profileId, endpointFingerprint });
        await EnsureProfileAsync(cancellationToken);
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay = await TryReplayAsync<SemanticIndexStatus>(
                        context,
                        idempotencyService,
                        idempotencyKey,
                        IdempotencyOperation.SemanticRemoteConsentChange,
                        actor,
                        requestHash,
                        token);
                    if (replay is not null)
                    {
                        return replay;
                    }
                    var profile = await RequireRemoteProfileAsync(
                        context,
                        profileId,
                        endpointFingerprint,
                        token);
                    if (!remoteDeploymentAllowed)
                    {
                        throw new VaultException(
                            VaultErrorCode.SemanticRemoteNotAllowed,
                            "Remote embedding is not allowed for this deployment.");
                    }
                    var now = timeProvider.GetUtcNow();
                    await SemanticConsentStore.GrantAsync(
                        context,
                        profile.ProfileId,
                        profile.Provider.EndpointFingerprint,
                        now,
                        token);
                    await SemanticProfileStore.UpdateStatusAsync(
                        context,
                        profile.ProfileId,
                        SemanticProfileStatus.Building,
                        null,
                        now,
                        token);
                    await AppendSemanticAuditAsync(
                        context,
                        actor,
                        "embedding.remote_consent",
                        profile,
                        "granted",
                        now,
                        token);
                    var status = await BuildStatusCoreAsync(context, token);
                    await idempotencyService.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.SemanticRemoteConsentChange,
                        actor,
                        sessionId: null,
                        requestHash,
                        status,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return status;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<SemanticIndexStatus> RevokeRemoteConsentAsync(
        Guid idempotencyKey,
        string profileId,
        string endpointFingerprint,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        var idempotencyService = RequireIdempotency();
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.SemanticRemoteConsentChange,
            actor,
            new { action = "revoke", profileId, endpointFingerprint });
        await EnsureProfileAsync(cancellationToken);
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay = await TryReplayAsync<SemanticIndexStatus>(
                        context,
                        idempotencyService,
                        idempotencyKey,
                        IdempotencyOperation.SemanticRemoteConsentChange,
                        actor,
                        requestHash,
                        token);
                    if (replay is not null)
                    {
                        return replay;
                    }
                    var profile = await RequireRemoteProfileAsync(
                        context,
                        profileId,
                        endpointFingerprint,
                        token);
                    var now = timeProvider.GetUtcNow();
                    await SemanticConsentStore.RevokeAsync(
                        context,
                        profile.ProfileId,
                        now,
                        token);
                    await SemanticProfileStore.UpdateStatusAsync(
                        context,
                        profile.ProfileId,
                        SemanticProfileStatus.Disabled,
                        null,
                        now,
                        token);
                    await AppendSemanticAuditAsync(
                        context,
                        actor,
                        "embedding.remote_consent",
                        profile,
                        "revoked",
                        now,
                        token);
                    var status = await BuildStatusCoreAsync(context, token);
                    await idempotencyService.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.SemanticRemoteConsentChange,
                        actor,
                        sessionId: null,
                        requestHash,
                        status,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return status;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<SemanticIndexStatus> RebuildAsync(
        Guid idempotencyKey,
        string profileId,
        VaultActorContext actor,
        CancellationToken cancellationToken = default)
    {
        EnsureUserUi(actor);
        var idempotencyService = RequireIdempotency();
        IdempotencyService.RequireKey(idempotencyKey);
        var requestHash = ComputeRequestHash(
            IdempotencyOperation.SemanticIndexRebuild,
            actor,
            new { profileId });
        await EnsureProfileAsync(cancellationToken);
        return await sessionManager.ExecuteAsync(
            async (context, token) =>
            {
                await context.BeginTransactionAsync(token);
                try
                {
                    var replay = await TryReplayAsync<SemanticIndexStatus>(
                        context,
                        idempotencyService,
                        idempotencyKey,
                        IdempotencyOperation.SemanticIndexRebuild,
                        actor,
                        requestHash,
                        token);
                    if (replay is not null)
                    {
                        return replay;
                    }
                    var profile = await SemanticProfileStore.GetActiveAsync(
                        context,
                        token);
                    if (profile is null
                        || !string.Equals(
                            profile.ProfileId,
                            profileId,
                            StringComparison.Ordinal))
                    {
                        throw ProfileMismatch();
                    }
                    if (profile.Status == SemanticProfileStatus.Building)
                    {
                        throw new VaultException(
                            VaultErrorCode.SemanticRebuildInProgress,
                            "Semantic index rebuild is already in progress.");
                    }
                    var now = timeProvider.GetUtcNow();
                    var consent = profile.Provider.IsRemote
                        ? await SemanticConsentStore.GetAsync(
                            context,
                            profile.ProfileId,
                            token)
                        : null;
                    var canBuild = !profile.Provider.IsRemote
                        || remoteDeploymentAllowed
                        && consent is { RevokedAt: null };
                    await SemanticChunkStore.DeleteProfileAsync(
                        context,
                        profile.ProfileId,
                        token);
                    await SemanticIndexJobStore.DeleteProfileJobsAsync(
                        context,
                        profile.ProfileId,
                        token);
                    await SemanticProfileStore.UpdateStatusAsync(
                        context,
                        profile.ProfileId,
                        canBuild
                            ? SemanticProfileStatus.Building
                            : SemanticProfileStatus.Disabled,
                        null,
                        now,
                        token);
                    await SemanticIndexJobStore.EnqueueInitialBackfillAsync(
                        context,
                        profile.ProfileId,
                        now,
                        token);
                    await AppendSemanticAuditAsync(
                        context,
                        actor,
                        "embedding.rebuild",
                        profile,
                        "started",
                        now,
                        token);
                    var status = await BuildStatusCoreAsync(context, token);
                    await idempotencyService.SaveAsync(
                        context,
                        idempotencyKey,
                        IdempotencyOperation.SemanticIndexRebuild,
                        actor,
                        sessionId: null,
                        requestHash,
                        status,
                        now,
                        token);
                    await context.CommitTransactionAsync(token);
                    return status;
                }
                catch
                {
                    await context.RollbackTransactionAsync(token);
                    throw;
                }
            },
            cancellationToken);
    }

    internal async Task EnsureProfileCoreAsync(
        CipherVault.Core.Database.VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return;
        }
        var descriptor = provider.Descriptor;
        var profileId = SemanticProfileIdentity.CreateProfileId(
            descriptor,
            profileVersion);
        var now = timeProvider.GetUtcNow();
        var active = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        var existing = await SemanticProfileStore.FindAsync(
            context,
            profileId,
            cancellationToken);
        var consent = descriptor.IsRemote
            ? await SemanticConsentStore.GetAsync(
                context,
                profileId,
                cancellationToken)
            : null;
        var consentGranted = !descriptor.IsRemote
            || remoteDeploymentAllowed
            && consent is { RevokedAt: null };
        var desiredStatus = consentGranted
            ? existing?.Status == SemanticProfileStatus.Ready
                ? SemanticProfileStatus.Ready
                : SemanticProfileStatus.Building
            : SemanticProfileStatus.Disabled;

        if (active?.ProfileId == profileId)
        {
            if (active.Status != desiredStatus
                && (active.Status is SemanticProfileStatus.Disabled
                    or SemanticProfileStatus.Building
                    or SemanticProfileStatus.Ready))
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profileId,
                    desiredStatus,
                    active.LastErrorCode,
                    now,
                    cancellationToken);
            }
            return;
        }

        if (active is not null)
        {
            await SemanticChunkStore.DeleteProfileAsync(
                context,
                active.ProfileId,
                cancellationToken);
            await SemanticIndexJobStore.DeleteProfileJobsAsync(
                context,
                active.ProfileId,
                cancellationToken);
        }
        var profile = new SemanticProfile(
            profileId,
            descriptor,
            profileVersion,
            SemanticProfileIdentity.ChunkerVersion,
            IsActive: true,
            desiredStatus,
            LastErrorCode: null,
            existing?.CreatedAt ?? now,
            now,
            ReadyAt: null);
        await SemanticProfileStore.ActivateAsync(
            context,
            profile,
            now,
            cancellationToken);
        await SemanticIndexJobStore.EnqueueInitialBackfillAsync(
            context,
            profileId,
            now,
            cancellationToken);
        await AuditStore.AppendAsync(
            context,
            new VaultActorContext(VaultActorKind.System),
            "embedding.profile_change",
            documentId: null,
            revision: null,
            result: "success",
            details: new
            {
                profileId,
                providerId = descriptor.ProviderId,
                modelId = descriptor.ModelId,
                endpointFingerprint = descriptor.EndpointFingerprint,
                status = desiredStatus.ToDatabaseValue(),
                sessionGeneration = context.SessionGeneration
            },
            occurredAt: now,
            cancellationToken: cancellationToken);
    }

    private async Task<SemanticIndexStatus> BuildStatusCoreAsync(
        VaultOperationContext context,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        if (profile is null)
        {
            return NotConfiguredStatus();
        }
        var consent = profile.Provider.IsRemote
            ? await SemanticConsentStore.GetAsync(
                context,
                profile.ProfileId,
                cancellationToken)
            : null;
        var counts = await SemanticIndexJobStore.GetCountsAsync(
            context,
            profile.ProfileId,
            cancellationToken);
        var chunks = await SemanticChunkStore.CountAsync(
            context,
            profile.ProfileId,
            cancellationToken);
        var documents = await SemanticChunkStore.CountCurrentDocumentsAsync(
            context,
            profile.ProfileId,
            cancellationToken);
        return new SemanticIndexStatus(
            ToState(profile.Status),
            profile,
            endpointDisplay,
            remoteDeploymentAllowed,
            profile.Provider.IsRemote,
            consent is { RevokedAt: null },
            consent?.GrantedAt,
            consent?.RevokedAt,
            documents,
            chunks,
            counts.Pending,
            counts.Failed,
            profile.LastErrorCode,
            profile.UpdatedAt);
    }

    private static async Task<SemanticProfile> RequireRemoteProfileAsync(
        VaultOperationContext context,
        string profileId,
        string endpointFingerprint,
        CancellationToken cancellationToken)
    {
        var profile = await SemanticProfileStore.GetActiveAsync(
            context,
            cancellationToken);
        if (profile is null
            || !string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.Ordinal)
            || !string.Equals(
                profile.Provider.EndpointFingerprint,
                endpointFingerprint,
                StringComparison.Ordinal))
        {
            throw ProfileMismatch();
        }
        if (!profile.Provider.IsRemote)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "The active embedding profile is not remote.");
        }
        return profile;
    }

    private static async Task AppendSemanticAuditAsync(
        VaultOperationContext context,
        VaultActorContext actor,
        string action,
        SemanticProfile profile,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await AuditStore.AppendAsync(
            context,
            actor,
            action,
            documentId: null,
            revision: null,
            result: "success",
            details: new
            {
                profileId = profile.ProfileId,
                providerId = profile.Provider.ProviderId,
                modelId = profile.Provider.ModelId,
                endpointFingerprint = profile.Provider.EndpointFingerprint,
                status,
                sessionGeneration = context.SessionGeneration
            },
            occurredAt: now,
            cancellationToken: cancellationToken);

    private static async Task<T?> TryReplayAsync<T>(
        VaultOperationContext context,
        IdempotencyService idempotencyService,
        Guid key,
        IdempotencyOperation operation,
        VaultActorContext actor,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var record = await idempotencyService.TryReplayAsync(
            context,
            key,
            operation,
            actor,
            sessionId: null,
            requestHash,
            cancellationToken);
        return record is null ? default : IdempotencyService.Replay<T>(record);
    }

    private static string ComputeRequestHash(
        IdempotencyOperation operation,
        VaultActorContext actor,
        object request)
    {
        var element = JsonSerializer.SerializeToElement(
            request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return IdempotencyRequestHasher.Compute(
            operation,
            actor.Kind,
            sessionId: null,
            element);
    }

    private IdempotencyService RequireIdempotency() =>
        idempotency
        ?? throw new InvalidOperationException(
            "Semantic index mutations require idempotency.");

    private static void EnsureUserUi(VaultActorContext actor)
    {
        if (actor.Kind != VaultActorKind.UserUi)
        {
            throw new VaultException(
                VaultErrorCode.Forbidden,
                "Only the Web UI can manage semantic embedding.");
        }
    }

    private static VaultException ProfileMismatch() =>
        new(
            VaultErrorCode.SemanticProfileMismatch,
            "Semantic profile or endpoint changed.");

    internal SemanticIndexStatus NotConfiguredStatus() =>
        new(
            SemanticIndexState.NotConfigured,
            Profile: null,
            EndpointDisplay: null,
            remoteDeploymentAllowed,
            RemoteConsentRequired: false,
            RemoteConsentGranted: false,
            ConsentGrantedAt: null,
            ConsentRevokedAt: null,
            DocumentCount: 0,
            ChunkCount: 0,
            PendingJobCount: 0,
            FailedJobCount: 0,
            LastErrorCode: null,
            LastUpdatedAt: null);

    private static SemanticIndexState ToState(
        SemanticProfileStatus status) =>
        status switch
        {
            SemanticProfileStatus.Disabled => SemanticIndexState.Disabled,
            SemanticProfileStatus.Building => SemanticIndexState.Building,
            SemanticProfileStatus.Ready => SemanticIndexState.Ready,
            SemanticProfileStatus.Degraded => SemanticIndexState.Degraded,
            SemanticProfileStatus.Failed => SemanticIndexState.Failed,
            _ => SemanticIndexState.Failed
        };
}
