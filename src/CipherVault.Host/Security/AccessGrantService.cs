using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Grants;
using CipherVault.Host.Contracts;
using CipherVault.Host.Session;

namespace CipherVault.Host.Security;

public sealed class AccessGrantService : IDisposable
{
    public static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultHardLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan DefaultDenialCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RevokeAllPreviewLifetime =
        TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private const int MaxTerminalTombstones = 256;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _hardLifetime;
    private readonly TimeSpan _denialCooldown;
    private readonly ISecurityEventLog? _securityLog;
    private readonly IAccessGrantStore? _store;
    private readonly ApprovalStore? _approvals;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<Guid, AccessGrantRecord> _grants = [];
    private readonly Dictionary<AccessGrantSlotKey, AccessGrantRecord> _activeSlots = [];
    private readonly Dictionary<AccessGrantSlotKey, PendingGrant> _pendingSlots = [];
    private readonly Dictionary<string, HashSet<Guid>> _clientGrants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccessGrantCooldown> _cooldowns =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AccessGrantRecord> _terminalGrants = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastPersistedUse = [];
    private readonly Dictionary<string, RevokeAllSnapshot> _revokeAllSnapshots =
        new(StringComparer.Ordinal);
    private readonly Queue<Guid> _terminalGrantOrder = new();
    private readonly Dictionary<string, HashSet<Guid>> _clientTerminalGrants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _expirationSweeper;
    private readonly TimeSpan _sweepInterval;
    private int _sweepInProgress;

    public AccessGrantService(
        TimeProvider? timeProvider = null,
        TimeSpan? idleLifetime = null,
        TimeSpan? hardLifetime = null,
        TimeSpan? denialCooldown = null,
        ISecurityEventLog? securityLog = null,
        TimeSpan? sweepInterval = null,
        IAccessGrantStore? store = null,
        ApprovalStore? approvals = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleLifetime = idleLifetime ?? DefaultIdleLifetime;
        _hardLifetime = hardLifetime ?? DefaultHardLifetime;
        _denialCooldown = denialCooldown ?? DefaultDenialCooldown;
        _securityLog = securityLog;
        _store = store;
        _approvals = approvals;
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
        _expirationSweeper = new Timer(
            _ => _ = SweepExpiredAsync(),
            state: null,
            dueTime: _sweepInterval,
            period: _sweepInterval);
        if (_idleLifetime <= TimeSpan.Zero ||
            _hardLifetime <= TimeSpan.Zero ||
            _denialCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleLifetime),
                "Access Grant lifetimes must be positive.");
        }
    }

    public TimeSpan IdleLifetime => _idleLifetime;
    public TimeSpan HardLifetime => _hardLifetime;

    public void Dispose()
    {
        _expirationSweeper.Dispose();
    }

    public void SweepExpired()
    {
        if (Interlocked.Exchange(ref _sweepInProgress, 1) != 0)
        {
            return;
        }
        try
        {
            PruneExpired();
            if (_store is not null)
            {
                _ = PersistExpiredAsync();
            }
        }
        finally
        {
            Volatile.Write(ref _sweepInProgress, 0);
        }
    }

    public async Task SweepExpiredAsync()
    {
        if (Interlocked.Exchange(ref _sweepInProgress, 1) != 0)
        {
            return;
        }
        try
        {
            PruneExpired();
            if (_store is not null)
            {
                await PersistExpiredAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _sweepInProgress, 0);
        }
    }

    private async Task PersistExpiredAsync()
    {
        if (_store is null)
        {
            return;
        }
        try
        {
            await _store.ExpireAsync(
                _timeProvider.GetUtcNow()).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            if (_securityLog is not null)
            {
                await SecurityLogSafety.TryWriteAsync(
                    _securityLog,
                    new HostSecurityLogEvent(
                        DateTimeOffset.UtcNow,
                        "grant_expiration_sweep_failed",
                        ClientId: null,
                        ConnectionId: null,
                        ClientInstanceId: null,
                        GrantId: null,
                        GrantScopes: null,
                        RequestId: null,
                        "failed",
                        "storage_error"),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<AccessGrantRequestOutcome> RequestAsync(
        string clientId,
        Guid clientInstanceId,
        McpAccessMode tokenAccessMode,
        AccessGrantScope requestedScopes,
        AccessGrantVaultGeneration vaultGeneration,
        Func<AccessGrantApprovalRequest, CancellationToken, Task<AccessGrantApprovalResult>>
            approvalHandler,
        Guid verifiedProcessSessionId,
        CancellationToken cancellationToken = default)
    {
        return await RequestAsync(
            clientId,
            clientInstanceId,
            tokenAccessMode,
            requestedScopes,
            AccessGrantPathPolicy.Unrestricted,
            vaultGeneration,
            approvalHandler,
            verifiedProcessSessionId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccessGrantRequestOutcome> RequestAsync(
        string clientId,
        Guid clientInstanceId,
        McpAccessMode tokenAccessMode,
        AccessGrantScope requestedScopes,
        AccessGrantPathPolicy pathPolicy,
        AccessGrantVaultGeneration vaultGeneration,
        Func<AccessGrantApprovalRequest, CancellationToken, Task<AccessGrantApprovalResult>>
            approvalHandler,
        Guid verifiedProcessSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(approvalHandler);
        if (verifiedProcessSessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "Bridge grants require a process session id.",
                nameof(verifiedProcessSessionId));
        }

        var allowed = AccessGrantScopes.Ceiling(tokenAccessMode);
        var effectiveRequest = AccessGrantScopes.Normalize(requestedScopes) & allowed;
        PruneExpired();
        if (effectiveRequest == AccessGrantScope.None)
        {
            return new AccessGrantRequestOutcome(
                AccessGrantRequestStatus.Denied,
                IssuedGrant: null,
                RetryAfter: null,
                ReasonCode: "no_allowed_scopes");
        }

        PendingGrant pending;
        var isOwner = false;
        var now = _timeProvider.GetUtcNow();
        var slot = new AccessGrantSlotKey(clientId, clientInstanceId);
        lock (_sync)
        {
            if (_cooldowns.TryGetValue(clientId, out var cooldown))
            {
                if (cooldown.BlockedUntil > now)
                {
                    return new AccessGrantRequestOutcome(
                        AccessGrantRequestStatus.Cooldown,
                        IssuedGrant: null,
                        RetryAfter: cooldown.BlockedUntil,
                        ReasonCode: cooldown.ReasonCode);
                }
                _cooldowns.Remove(clientId);
            }

            if (_activeSlots.ContainsKey(slot))
            {
                return new AccessGrantRequestOutcome(
                    AccessGrantRequestStatus.Conflict,
                    IssuedGrant: null,
                    RetryAfter: null,
                    ReasonCode: "access_grant_already_active");
            }

            if (!_pendingSlots.TryGetValue(slot, out pending!))
            {
                pending = new PendingGrant(
                    new AccessGrantRecord(
                        Guid.NewGuid(),
                        clientId,
                        clientInstanceId,
                        SecretHash: null,
                        Scopes: AccessGrantScope.None,
                        RequestedScopes: effectiveRequest,
                        State: AccessGrantState.Pending,
                        CreatedAt: now,
                        ApprovedAt: null,
                        LastUsedAt: null,
                        IdleExpiresAt: null,
                        HardExpiresAt: null,
                        VaultSessionId: null,
                        DenialReason: null,
                        VerifiedProcessSessionId: verifiedProcessSessionId,
                        PathPolicy: pathPolicy),
                    vaultGeneration);
                _pendingSlots[slot] = pending;
                isOwner = true;
            }
            else if (pending.Record.VerifiedProcessSessionId != verifiedProcessSessionId)
            {
                return new AccessGrantRequestOutcome(
                    AccessGrantRequestStatus.Conflict,
                    IssuedGrant: null,
                    RetryAfter: null,
                    ReasonCode: "client_identity_mismatch");
            }
        }

        if (!isOwner)
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await LogGrantAsync(
            "grant_requested",
            pending.Record,
            "pending",
            ReasonCode: null).ConfigureAwait(false);
        AccessGrantRequestOutcome outcome =
            DeniedWithoutCooldown("approval-cancelled");
        var propagateCancellation = false;
        CancellationTokenSource? pendingCancellation = null;
        try
        {
            lock (_sync)
            {
                if (pending.Canceled)
                {
                    outcome = DeniedWithoutCooldown("approval-cancelled");
                }
                else
                {
                    pendingCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                    pending.Cancellation = pendingCancellation;
                }
            }

            if (pendingCancellation is null)
            {
                return outcome;
            }

            var approval = await approvalHandler(
                new AccessGrantApprovalRequest(
                    pending.Record.GrantId,
                    clientId,
                    clientInstanceId,
                    tokenAccessMode,
                    effectiveRequest,
                    allowed,
                    pathPolicy),
                pendingCancellation.Token).ConfigureAwait(false);
            if (!approval.Approved)
            {
                var reason = approval.DenialReason ?? "user-denied";
                outcome = string.Equals(
                        reason,
                        "user-denied",
                        StringComparison.Ordinal)
                    ? DenyPending(pending, reason)
                    : DeniedWithoutCooldown(reason);
            }
            else
            {
                outcome = await ApprovePendingAsync(
                    pending,
                    approval.Scopes & effectiveRequest,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            outcome = DeniedWithoutCooldown("cancelled");
            propagateCancellation = true;
        }
        catch (OperationCanceledException) when (
            pending.Cancellation?.IsCancellationRequested ?? false)
        {
            outcome = DeniedWithoutCooldown("cancelled");
        }
        catch (AdminPipeRpcException exception) when (
            string.Equals(
                exception.Code,
                "approval_broker_unavailable",
                StringComparison.Ordinal))
        {
            outcome = DeniedWithoutCooldown(exception.Code);
        }
        catch
        {
            outcome = DeniedWithoutCooldown("approval-failed");
        }
        finally
        {
            lock (_sync)
            {
                if (_pendingSlots.TryGetValue(slot, out var current) &&
                    ReferenceEquals(current, pending))
                {
                    _pendingSlots.Remove(slot);
                }
                pending.Cancellation = null;
            }
            pendingCancellation?.Dispose();
        }

        pending.Completion.TrySetResult(outcome);
        if (outcome.Status == AccessGrantRequestStatus.Approved)
        {
            await LogGrantAsync(
                "grant_approved",
                outcome.IssuedGrant!.Record,
                "success",
                ReasonCode: null).ConfigureAwait(false);
        }
        else if (string.Equals(
                     outcome.ReasonCode,
                     "user-denied",
                     StringComparison.Ordinal))
        {
            await LogGrantAsync(
                "grant_denied",
                pending.Record,
                "denied",
                outcome.ReasonCode).ConfigureAwait(false);
        }
        if (propagateCancellation)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return outcome;
    }

    public async Task<AccessGrantValidationResult> ValidateAsync(
        string clientId,
        string? plaintextSecret,
        AccessGrantVaultGeneration vaultGeneration)
    {
        PruneExpired();
        var result = ValidateCore(clientId, plaintextSecret, vaultGeneration);
        await LogValidationAsync(clientId, result).ConfigureAwait(false);
        return result;
    }

    private AccessGrantValidationResult ValidateCore(
        string clientId,
        string? plaintextSecret,
        AccessGrantVaultGeneration vaultGeneration)
    {
        if (string.IsNullOrWhiteSpace(plaintextSecret))
        {
            return new AccessGrantValidationResult(
                AccessGrantValidationStatus.Missing,
                Grant: null,
                ReasonCode: "access_grant_required");
        }

        var presentedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(plaintextSecret));
        try
        {
            lock (_sync)
            {
                if (!_clientGrants.TryGetValue(clientId, out var grantIds))
                {
                    return ValidateTerminal(clientId, presentedHash) ?? Invalid();
                }

                AccessGrantRecord? matched = null;
                foreach (var grantId in grantIds.ToArray())
                {
                    if (!_grants.TryGetValue(grantId, out var record))
                    {
                        grantIds.Remove(grantId);
                        continue;
                    }
                    if (!CryptographicOperations.FixedTimeEquals(
                            presentedHash,
                            record.SecretHash ?? []))
                    {
                        continue;
                    }
                    matched = record;
                    break;
                }

                if (matched is null)
                {
                    return ValidateTerminal(clientId, presentedHash) ?? Invalid();
                }

                var now = _timeProvider.GetUtcNow();
                if (matched.State != AccessGrantState.Active ||
                    matched.HardExpiresAt is null ||
                    matched.IdleExpiresAt is null ||
                    now >= matched.HardExpiresAt ||
                    now >= matched.IdleExpiresAt)
                {
                    RemoveGrant(matched, AccessGrantState.Expired);
                    return new AccessGrantValidationResult(
                        AccessGrantValidationStatus.Expired,
                        Grant: null,
                        ReasonCode: "access_grant_expired",
                        Record: matched with { State = AccessGrantState.Expired });
                }

                if (matched.VaultSessionId != vaultGeneration.SessionId)
                {
                    RemoveGrant(matched, AccessGrantState.Revoked);
                    return new AccessGrantValidationResult(
                        AccessGrantValidationStatus.Revoked,
                        Grant: null,
                        ReasonCode: "access_grant_revoked",
                        Record: matched with { State = AccessGrantState.Revoked });
                }

                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Valid,
                    new AccessGrantContext(
                        matched.GrantId,
                        matched.ClientId,
                        matched.ClientInstanceId,
                        matched.Scopes,
                        matched.VaultSessionId.Value,
                        matched.VerifiedProcessSessionId,
                        matched.PathPolicy),
                    ReasonCode: null,
                    Record: matched);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedHash);
        }
    }

    public bool RecordUse(AccessGrantContext grant, DateTimeOffset? usedAt = null)
    {
        lock (_sync)
        {
            if (!_grants.TryGetValue(grant.GrantId, out var record) ||
                record.State != AccessGrantState.Active ||
                record.VaultSessionId != grant.VaultSessionId ||
                record.VerifiedProcessSessionId != grant.VerifiedProcessSessionId ||
                record.HardExpiresAt is null ||
                record.IdleExpiresAt is null)
            {
                return false;
            }

            var now = usedAt ?? _timeProvider.GetUtcNow();
            if (now >= record.HardExpiresAt || now >= record.IdleExpiresAt)
            {
                RemoveGrant(record, AccessGrantState.Expired);
                return false;
            }

            var idleExpiry = now + _idleLifetime;
            if (idleExpiry > record.HardExpiresAt)
            {
                idleExpiry = record.HardExpiresAt.Value;
            }
            var updated = record with
            {
                LastUsedAt = now,
                IdleExpiresAt = idleExpiry
            };
            _grants[grant.GrantId] = updated;
            _activeSlots[new AccessGrantSlotKey(
                record.ClientId,
                record.ClientInstanceId)] = updated;
            return true;
        }
    }

    public async Task RecordSuccessfulUseAsync(
        AccessGrantContext grant,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (!RecordUse(grant, now) || _store is null)
        {
            return;
        }
        DateTimeOffset idleExpiresAt;
        lock (_sync)
        {
            if (!_grants.TryGetValue(grant.GrantId, out var updated) ||
                updated.IdleExpiresAt is null)
            {
                return;
            }
            idleExpiresAt = updated.IdleExpiresAt.Value;
        }
        lock (_sync)
        {
            if (_lastPersistedUse.TryGetValue(grant.GrantId, out var last) &&
                now - last < TimeSpan.FromMinutes(1))
            {
                return;
            }
            _lastPersistedUse[grant.GrantId] = now;
        }
        try
        {
            await _store.UpdateLastUsedAsync(
                grant.GrantId,
                now,
                idleExpiresAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _lastPersistedUse.Remove(grant.GrantId);
            }
            throw;
        }
        catch
        {
            lock (_sync)
            {
                _lastPersistedUse.Remove(grant.GrantId);
            }
            if (_securityLog is null)
            {
                return;
            }
            await SecurityLogSafety.TryWriteAsync(
                _securityLog,
                new HostSecurityLogEvent(
                    DateTimeOffset.UtcNow,
                    "grant_last_used_update_failed",
                    grant.ClientId,
                    ConnectionId: null,
                    grant.ClientInstanceId,
                    grant.GrantId,
                    ScopeNames(grant.Scopes, grant.PathPolicy),
                    RequestId: null,
                    "failed",
                    "storage_error"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AppendDeniedAccessAuditAsync(
        AccessDeniedAudit audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (_store is not null)
        {
            try
            {
                await _store.AppendDeniedAccessAsync(
                    audit,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
        if (_securityLog is null)
        {
            return;
        }
        await SecurityLogSafety.TryWriteAsync(
            _securityLog,
            new HostSecurityLogEvent(
                DateTimeOffset.UtcNow,
                "grant_rejected",
                audit.ClientId,
                ConnectionId: null,
                audit.ClientInstanceId,
                audit.GrantId,
                audit.GrantScopes,
                audit.RequestId,
                "denied",
                audit.ReasonCode),
            cancellationToken).ConfigureAwait(false);
    }

    public Task EnsureClockAsync(
        CancellationToken cancellationToken = default)
    {
        return _store is null
            ? Task.CompletedTask
            : _store.EnsureClockAsync(
                _timeProvider.GetUtcNow(),
                cancellationToken);
    }

    public AccessGrantValidationResult UseBridgeGrant(
        AccessGrantContext grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_sync)
        {
            if (!_grants.TryGetValue(grant.GrantId, out var record) ||
                !string.Equals(
                    record.ClientId,
                    grant.ClientId,
                    StringComparison.OrdinalIgnoreCase) ||
                record.ClientInstanceId != grant.ClientInstanceId ||
                record.VerifiedProcessSessionId !=
                    grant.VerifiedProcessSessionId)
            {
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: "access_grant_revoked");
            }

            if (record.State != AccessGrantState.Active)
            {
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: "access_grant_revoked",
                    Record: record);
            }

            if (record.VaultSessionId != grant.VaultSessionId)
            {
                RemoveGrant(record, AccessGrantState.Revoked);
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: "access_grant_revoked",
                    Record: record with { State = AccessGrantState.Revoked });
            }

            var now = _timeProvider.GetUtcNow();
            if (record.HardExpiresAt is null ||
                record.IdleExpiresAt is null ||
                now >= record.HardExpiresAt ||
                now >= record.IdleExpiresAt)
            {
                RemoveGrant(record, AccessGrantState.Expired);
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Expired,
                    Grant: null,
                    ReasonCode: "access_grant_expired",
                    Record: record with { State = AccessGrantState.Expired });
            }

            var idleExpiry = now + _idleLifetime;
            if (idleExpiry > record.HardExpiresAt)
            {
                idleExpiry = record.HardExpiresAt.Value;
            }
            var updated = record with
            {
                LastUsedAt = now,
                IdleExpiresAt = idleExpiry
            };
            _grants[record.GrantId] = updated;
            _activeSlots[new AccessGrantSlotKey(
                record.ClientId,
                record.ClientInstanceId)] = updated;
            return new AccessGrantValidationResult(
                AccessGrantValidationStatus.Valid,
                grant with
                {
                    Scopes = updated.Scopes
                },
                ReasonCode: null,
                Record: updated);
        }
    }

    public AccessGrantValidationResult PeekBridgeGrant(
        AccessGrantContext grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_sync)
        {
            if (!_grants.TryGetValue(grant.GrantId, out var record) ||
                !string.Equals(
                    record.ClientId,
                    grant.ClientId,
                    StringComparison.OrdinalIgnoreCase) ||
                record.ClientInstanceId != grant.ClientInstanceId ||
                record.VerifiedProcessSessionId !=
                    grant.VerifiedProcessSessionId)
            {
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: "access_grant_revoked");
            }

            if (record.State != AccessGrantState.Active ||
                record.VaultSessionId != grant.VaultSessionId)
            {
                return new AccessGrantValidationResult(
                    record.State == AccessGrantState.Expired
                        ? AccessGrantValidationStatus.Expired
                        : AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: record.State == AccessGrantState.Expired
                        ? "access_grant_expired"
                        : "access_grant_revoked",
                    Record: record);
            }

            var now = _timeProvider.GetUtcNow();
            if (record.HardExpiresAt is null ||
                record.IdleExpiresAt is null ||
                now >= record.HardExpiresAt ||
                now >= record.IdleExpiresAt)
            {
                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Expired,
                    Grant: null,
                    ReasonCode: "access_grant_expired",
                    Record: record with { State = AccessGrantState.Expired });
            }

            return new AccessGrantValidationResult(
                AccessGrantValidationStatus.Valid,
                grant with { Scopes = record.Scopes },
                ReasonCode: null,
                Record: record);
        }
    }

    public async Task<AccessGrantRevokeStatus> RevokeAsync(
        string clientId,
        Guid grantId,
        string plaintextSecret)
    {
        if (_store is not null)
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var current = await _store.GetAsync(
                    grantId,
                    _timeProvider.GetUtcNow()).ConfigureAwait(false);
                if (current is null ||
                    current.State != StoredAccessGrantState.Active)
                {
                    return AccessGrantRevokeStatus.AlreadyRemoved;
                }
                var hash = SHA256.HashData(
                    Encoding.UTF8.GetBytes(plaintextSecret));
                try
                {
                    if (!string.Equals(
                            current.ClientId,
                            clientId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !CryptographicOperations.FixedTimeEquals(
                            hash,
                            current.SecretHash))
                    {
                        return AccessGrantRevokeStatus.Invalid;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hash);
                }
                _approvals?.RevokeByScope(
                    current.ProcessSessionId,
                    current.VaultSessionId);
                var outcome = await _store.RevokeAsync(
                    grantId,
                    "client-requested",
                    _timeProvider.GetUtcNow(),
                    new VaultAuditContext(
                        VaultActorKind.Mcp,
                        current.ClientId,
                        ClientInstanceId: current.ClientInstanceId,
                        GrantId: current.GrantId,
                        GrantScopes: ScopeNames((AccessGrantScope)current.Scopes)),
                    CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_grants.TryGetValue(grantId, out var cached))
                    {
                        RemoveGrant(cached, AccessGrantState.Revoked);
                    }
                }
                return outcome.Status == StoredAccessGrantRevokeStatus.Revoked
                    ? AccessGrantRevokeStatus.Revoked
                    : AccessGrantRevokeStatus.AlreadyRemoved;
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        AccessGrantRecord? revoked = null;
        AccessGrantRevokeStatus status;
        var presentedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(plaintextSecret));
        try
        {
            lock (_sync)
            {
                if (!_grants.TryGetValue(grantId, out var record))
                {
                    status = AccessGrantRevokeStatus.AlreadyRemoved;
                    return status;
                }
                if (!string.Equals(
                        record.ClientId,
                        clientId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !CryptographicOperations.FixedTimeEquals(
                        presentedHash,
                        record.SecretHash ?? []))
                {
                    status = AccessGrantRevokeStatus.Invalid;
                    return status;
                }

                revoked = record with { State = AccessGrantState.Revoked };
                RemoveGrant(record, AccessGrantState.Revoked);
                status = AccessGrantRevokeStatus.Revoked;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedHash);
        }
        await LogGrantAsync("grant_revoked", revoked!, "success", "client-requested")
            .ConfigureAwait(false);
        return status;
    }

    public async Task<AccessGrantRevokeStatus> RevokeByIdAsync(
        Guid grantId,
        string reasonCode = "admin-requested")
    {
        if (_store is not null)
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                PendingGrantView? pendingView;
                lock (_sync)
                {
                    var pending = _pendingSlots.Values.FirstOrDefault(item =>
                        item.Record.GrantId == grantId);
                    pendingView = pending is null
                        ? null
                        : new PendingGrantView(
                            pending.Record,
                            pending.VaultGeneration.SessionId);
                }
                if (pendingView is not null)
                {
                    return await RevokePersistedAsync(
                        [],
                        [pendingView],
                        reasonCode,
                        CancellationToken.None).ConfigureAwait(false) == 1
                        ? AccessGrantRevokeStatus.Revoked
                        : AccessGrantRevokeStatus.AlreadyRemoved;
                }
                var current = await _store.GetAsync(
                    grantId,
                    _timeProvider.GetUtcNow()).ConfigureAwait(false);
                if (current is null)
                {
                    return AccessGrantRevokeStatus.AlreadyRemoved;
                }
                if (current.State != StoredAccessGrantState.Active)
                {
                    lock (_sync)
                    {
                        if (_grants.TryGetValue(grantId, out var cached))
                        {
                            RemoveGrant(cached, AccessGrantState.Revoked);
                        }
                    }
                    return AccessGrantRevokeStatus.AlreadyRemoved;
                }
                return await RevokePersistedAsync(
                    [current],
                    [],
                    reasonCode,
                    CancellationToken.None).ConfigureAwait(false) == 1
                    ? AccessGrantRevokeStatus.Revoked
                    : AccessGrantRevokeStatus.AlreadyRemoved;
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        AccessGrantRecord? revoked = null;
        lock (_sync)
        {
            var pending = _pendingSlots.Values.FirstOrDefault(item =>
                item.Record.GrantId == grantId);
            if (pending is not null)
            {
                revoked = pending.Record with { State = AccessGrantState.Revoked };
                CancelPending(pending, "revoked");
            }
            else if (!_grants.TryGetValue(grantId, out var record))
            {
                return AccessGrantRevokeStatus.AlreadyRemoved;
            }
            else
            {
                revoked = record with { State = AccessGrantState.Revoked };
                RemoveGrant(record, AccessGrantState.Revoked);
            }
        }
        await LogGrantAsync("grant_revoked", revoked!, "success", reasonCode)
            .ConfigureAwait(false);
        return AccessGrantRevokeStatus.Revoked;
    }

    public async Task<int> RevokeByProcessSessionAsync(
        Guid verifiedProcessSessionId,
        string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        if (_store is not null)
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var active = (await _store.GetActiveGrantsAsync(now)
                        .ConfigureAwait(false))
                    .Where(item =>
                        item.ProcessSessionId == verifiedProcessSessionId)
                    .ToArray();
                PendingGrantView[] pending;
                lock (_sync)
                {
                    pending = _pendingSlots.Values
                        .Where(item =>
                            item.Record.VerifiedProcessSessionId ==
                                verifiedProcessSessionId)
                        .Select(item => new PendingGrantView(
                            item.Record,
                            item.VaultGeneration.SessionId))
                        .ToArray();
                }
                return await RevokePersistedAsync(
                    active,
                    pending,
                    reasonCode,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (VaultException exception) when (
                exception.Code == VaultErrorCode.SessionLocked)
            {
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        var revoked = new List<AccessGrantRecord>();
        lock (_sync)
        {
            foreach (var pending in _pendingSlots.Values
                         .Where(item =>
                             item.Record.VerifiedProcessSessionId ==
                                 verifiedProcessSessionId)
                         .ToArray())
            {
                revoked.Add(pending.Record with
                {
                    State = AccessGrantState.Revoked
                });
                CancelPending(pending, reasonCode);
            }

            foreach (var record in _grants.Values
                         .Where(item =>
                             item.VerifiedProcessSessionId ==
                                 verifiedProcessSessionId)
                         .ToArray())
            {
                revoked.Add(record with { State = AccessGrantState.Revoked });
                RemoveGrant(record, AccessGrantState.Revoked);
            }
        }

        foreach (var record in revoked)
        {
            await LogGrantAsync(
                "grant_revoked",
                record,
                "success",
                reasonCode).ConfigureAwait(false);
        }
        return revoked.Count;
    }

    public IReadOnlyList<AccessGrantRecord> List()
    {
        PruneExpired();
        lock (_sync)
        {
            return _grants.Values
                .Concat(_pendingSlots.Values.Select(item => item.Record))
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();
        }
    }

    public async Task<AdminAccessGrantPage> ListStoredAsync(
        string? state,
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        var page = await _store.ListAsync(
            new StoredAccessGrantQuery(state, search, cursor, limit),
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return new AdminAccessGrantPage(
            page.Items.Select(ToListItem).ToArray(),
            page.NextCursor,
            page.TotalCount,
            new AdminAccessGrantStatusCounts(
                page.Counts.Total,
                page.Counts.Active,
                page.Counts.Revoked,
                page.Counts.Expired));
    }

    public async Task<AdminAccessGrantDetail> GetStoredAsync(
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        var grant = await _store.GetAsync(
            grantId,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return grant is null
            ? throw new KeyNotFoundException(
                "The requested Access Grant was not found.")
            : ToDetail(grant);
    }

    public async Task<AdminAccessGrantRevokeResult> RevokeStoredAsync(
        Guid grantId,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingGrant? pending;
            lock (_sync)
            {
                pending = _pendingSlots.Values.FirstOrDefault(item =>
                    item.Record.GrantId == grantId);
            }
            if (pending is not null)
            {
                _approvals?.RevokeByScope(
                    pending.Record.VerifiedProcessSessionId,
                    pending.VaultGeneration.SessionId);
                lock (_sync)
                {
                    CancelPending(pending, "revoked");
                }
                var now = _timeProvider.GetUtcNow();
                return new AdminAccessGrantRevokeResult(
                    grantId,
                    "cancelled",
                    now,
                    "admin-requested");
            }

            var current = await _store.GetAsync(
                grantId,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    "The requested Access Grant was not found.");
            _approvals?.RevokeByScope(
                current.ProcessSessionId,
                current.VaultSessionId);

            var outcome = await _store.RevokeAsync(
                grantId,
                "admin-requested",
                _timeProvider.GetUtcNow(),
                auditContext,
                cancellationToken).ConfigureAwait(false);
            if (outcome.Status == StoredAccessGrantRevokeStatus.NotFound)
            {
                throw new KeyNotFoundException(
                    "The requested Access Grant was not found.");
            }

            lock (_sync)
            {
                if (_grants.TryGetValue(grantId, out var cached))
                {
                    RemoveGrant(
                        cached,
                        outcome.Status == StoredAccessGrantRevokeStatus.AlreadyExpired
                            ? AccessGrantState.Expired
                            : AccessGrantState.Revoked);
                }
            }
            return new AdminAccessGrantRevokeResult(
                grantId,
                outcome.Status switch
                {
                    StoredAccessGrantRevokeStatus.Revoked => "revoked",
                    StoredAccessGrantRevokeStatus.AlreadyRevoked => "alreadyRevoked",
                    StoredAccessGrantRevokeStatus.AlreadyExpired => "alreadyExpired",
                    _ => "unknown"
                },
                outcome.RevokedAt,
                outcome.Reason);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<AdminAccessGrantRevokeAllPreview>
        PreviewRevokeAllStoredAsync(
            Guid adminSessionId,
            Guid vaultSessionId,
            CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        var now = _timeProvider.GetUtcNow();
        PruneRevokeAllSnapshots(now);
        var active = await _store.GetActiveGrantsAsync(
            now,
            cancellationToken).ConfigureAwait(false);
        PendingGrantView[] pending;
        lock (_sync)
        {
            pending = _pendingSlots.Values
                .Select(item => new PendingGrantView(
                    item.Record,
                    item.VaultGeneration.SessionId))
                .ToArray();
        }

        var groups = new Dictionary<(string ClientId, string DisplayName),
            ImpactAccumulator>();
        foreach (var grant in active)
        {
            AddImpact(
                groups,
                grant.ClientId,
                grant.ClientDisplayName,
                (AccessGrantScope)grant.Scopes,
                ParsePathPolicy(grant.PathPolicyJson),
                isPending: false);
        }
        foreach (var grant in pending)
        {
            AddImpact(
                groups,
                grant.Record.ClientId,
                "MCP",
                grant.Record.RequestedScopes,
                grant.Record.PathPolicy ?? AccessGrantPathPolicy.Unrestricted,
                isPending: true);
        }
        var token = CreatePreviewToken();
        var expiresAt = now + RevokeAllPreviewLifetime;
        var snapshot = new RevokeAllSnapshot(
            HashPreviewToken(token),
            adminSessionId,
            vaultSessionId,
            expiresAt,
            active.Select(grant => grant.GrantId).ToArray(),
            pending.Select(grant => grant.Record.GrantId).ToArray(),
            active.Select(grant => new ApprovalScope(
                    grant.ProcessSessionId,
                    grant.VaultSessionId))
                .Concat(pending.Select(grant => new ApprovalScope(
                    grant.Record.VerifiedProcessSessionId,
                    grant.VaultSessionId)))
                .ToArray());
        lock (_sync)
        {
            _revokeAllSnapshots[snapshot.TokenHash] = snapshot;
        }
        return new AdminAccessGrantRevokeAllPreview(
            token,
            expiresAt,
            groups.Values
                .OrderBy(item => item.ClientDisplayName, StringComparer.Ordinal)
                .ThenBy(item => item.ClientId, StringComparer.Ordinal)
                .Select(item => item.ToContract())
                .ToArray(),
            active.Count,
            pending.Length);
    }

    public async Task<AdminAccessGrantRevokeAllResult>
        ExecuteRevokeAllStoredAsync(
            string previewToken,
            Guid adminSessionId,
            Guid vaultSessionId,
            VaultAuditContext auditContext,
            CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(previewToken);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var tokenHash = HashPreviewToken(previewToken);
            RevokeAllSnapshot snapshot;
            lock (_sync)
            {
                PruneRevokeAllSnapshots(now);
                if (!_revokeAllSnapshots.Remove(
                        tokenHash,
                        out snapshot!))
                {
                    throw new AccessGrantPreviewException(
                        "invalid_input",
                        "The revocation preview is invalid or already used.");
                }
            }
            if (snapshot.ExpiresAt <= now)
            {
                throw new AccessGrantPreviewException(
                    "invalid_input",
                    "The revocation preview has expired.");
            }
            if (snapshot.AdminSessionId != adminSessionId ||
                snapshot.VaultSessionId != vaultSessionId)
            {
                throw new AccessGrantPreviewException(
                    "invalid_input",
                    "The revocation preview does not match this session.");
            }

            var active = await _store.GetActiveGrantsAsync(
                now,
                cancellationToken).ConfigureAwait(false);
            PendingGrantView[] pending;
            lock (_sync)
            {
                pending = _pendingSlots.Values
                    .Select(item => new PendingGrantView(
                        item.Record,
                        item.VaultGeneration.SessionId))
                    .ToArray();
            }
            var expectedActive = snapshot.ActiveGrantIds.ToHashSet();
            var expectedPending = snapshot.PendingGrantIds.ToHashSet();
            if (active.Any(grant => !expectedActive.Contains(grant.GrantId)) ||
                pending.Any(grant =>
                    !expectedPending.Contains(grant.Record.GrantId)))
            {
                throw new AccessGrantPreviewException(
                    "grants_changed",
                    "Access Grants changed after the revocation preview.");
            }

            foreach (var scope in snapshot.ApprovalScopes.Distinct())
            {
                _approvals?.RevokeByScope(
                    scope.ProcessSessionId,
                    scope.VaultSessionId);
            }
            var cancelledPending = 0;
            lock (_sync)
            {
                foreach (var pendingGrant in pending
                             .Where(item =>
                                 expectedPending.Contains(item.Record.GrantId))
                             .ToArray())
                {
                    var pendingSlot = _pendingSlots.Values.FirstOrDefault(item =>
                        item.Record.GrantId == pendingGrant.Record.GrantId);
                    if (pendingSlot is null)
                    {
                        continue;
                    }
                    CancelPending(pendingSlot, "revoked");
                    cancelledPending++;
                }
            }

            var outcome = await _store.RevokeAllAsync(
                snapshot.ActiveGrantIds,
                "admin-requested",
                now,
                auditContext,
                cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                foreach (var grantId in snapshot.ActiveGrantIds)
                {
                    if (_grants.TryGetValue(grantId, out var cached))
                    {
                        RemoveGrant(cached, AccessGrantState.Revoked);
                    }
                }
            }
            return new AdminAccessGrantRevokeAllResult(
                outcome.RevokedCount,
                cancelledPending,
                outcome.AlreadyRevokedCount,
                outcome.AlreadyExpiredCount);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<int> RevokeClientAsync(string clientId, string reasonCode)
    {
        if (_store is not null)
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var active = (await _store.GetActiveGrantsAsync(now)
                        .ConfigureAwait(false))
                    .Where(item => string.Equals(
                        item.ClientId,
                        clientId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                PendingGrantView[] pending;
                lock (_sync)
                {
                    pending = _pendingSlots.Values
                        .Where(item => string.Equals(
                            item.Record.ClientId,
                            clientId,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(item => new PendingGrantView(
                            item.Record,
                            item.VaultGeneration.SessionId))
                        .ToArray();
                }
                return await RevokePersistedAsync(
                    active,
                    pending,
                    reasonCode,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        var revoked = new List<AccessGrantRecord>();
        lock (_sync)
        {
            foreach (var pending in _pendingSlots.Values
                         .Where(item => string.Equals(
                             item.Record.ClientId,
                             clientId,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                revoked.Add(pending.Record with
                {
                    State = AccessGrantState.Revoked
                });
                CancelPending(pending, reasonCode);
            }

            if (_clientGrants.TryGetValue(clientId, out var grantIds))
            {
                foreach (var grantId in grantIds.ToArray())
                {
                    if (_grants.TryGetValue(grantId, out var record))
                    {
                        revoked.Add(record with { State = AccessGrantState.Revoked });
                        RemoveGrant(record, AccessGrantState.Revoked);
                    }
                }
            }
        }
        foreach (var record in revoked)
        {
            await LogGrantAsync(
                "grant_revoked",
                record,
                "success",
                reasonCode).ConfigureAwait(false);
        }
        return revoked.Count;
    }

    public async Task<int> RevokeAllAsync(string reasonCode)
    {
        if (_store is not null)
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var active = await _store.GetActiveGrantsAsync(now)
                    .ConfigureAwait(false);
                PendingGrantView[] pending;
                lock (_sync)
                {
                    pending = _pendingSlots.Values
                        .Select(item => new PendingGrantView(
                            item.Record,
                            item.VaultGeneration.SessionId))
                        .ToArray();
                }
                return await RevokePersistedAsync(
                    active,
                    pending,
                    reasonCode,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        var revoked = new List<AccessGrantRecord>();
        lock (_sync)
        {
            foreach (var pending in _pendingSlots.Values.ToArray())
            {
                revoked.Add(pending.Record with
                {
                    State = AccessGrantState.Revoked
                });
                CancelPending(pending, reasonCode);
            }

            foreach (var record in _grants.Values.ToArray())
            {
                revoked.Add(record with { State = AccessGrantState.Revoked });
                RemoveGrant(record, AccessGrantState.Revoked);
            }
        }
        foreach (var record in revoked)
        {
            await LogGrantAsync(
                "grant_revoked",
                record,
                "success",
                reasonCode).ConfigureAwait(false);
        }
        return revoked.Count;
    }

    public async Task InitializeStoredSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return;
        }
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                foreach (var pending in _pendingSlots.Values.ToArray())
                {
                    CancelPending(pending, "host-restarted");
                }
                _grants.Clear();
                _activeSlots.Clear();
                _pendingSlots.Clear();
                _clientGrants.Clear();
                _cooldowns.Clear();
                _terminalGrants.Clear();
                _terminalGrantOrder.Clear();
                _clientTerminalGrants.Clear();
                _revokeAllSnapshots.Clear();
                _lastPersistedUse.Clear();
            }
            var now = _timeProvider.GetUtcNow();
            await _store.RevokeStaleActiveAsync(
                "host-restarted",
                now,
                new VaultAuditContext(
                    VaultActorKind.System,
                    "CipherVault.Host"),
                cancellationToken).ConfigureAwait(false);
            await _store.ExpireAsync(
                now,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<int> RevokePersistedAsync(
        IReadOnlyList<StoredAccessGrant> active,
        IReadOnlyList<PendingGrantView> pending,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            throw new InvalidOperationException(
                "Access Grant persistence is not available.");
        }
        foreach (var scope in active
                     .Select(item => new ApprovalScope(
                         item.ProcessSessionId,
                         item.VaultSessionId))
                     .Concat(pending.Select(item => new ApprovalScope(
                         item.Record.VerifiedProcessSessionId,
                         item.VaultSessionId)))
                     .Distinct())
        {
            _approvals?.RevokeByScope(
                scope.ProcessSessionId,
                scope.VaultSessionId);
        }

        var cancelledPending = 0;
        var pendingRecords = new List<AccessGrantRecord>(pending.Count);
        lock (_sync)
        {
            foreach (var view in pending)
            {
                var item = _pendingSlots.Values.FirstOrDefault(candidate =>
                    candidate.Record.GrantId == view.Record.GrantId);
                if (item is null)
                {
                    continue;
                }
                pendingRecords.Add(item.Record with
                {
                    State = AccessGrantState.Revoked
                });
                CancelPending(item, reasonCode);
                cancelledPending++;
            }
        }

        var outcome = await _store.RevokeAllAsync(
            active.Select(item => item.GrantId).ToArray(),
            reasonCode,
            _timeProvider.GetUtcNow(),
            new VaultAuditContext(VaultActorKind.System, "CipherVault.Host"),
            cancellationToken).ConfigureAwait(false);
        var activeRecords = new List<AccessGrantRecord>(active.Count);
        lock (_sync)
        {
            foreach (var grant in active)
            {
                if (_grants.TryGetValue(grant.GrantId, out var cached))
                {
                    activeRecords.Add(cached with
                    {
                        State = AccessGrantState.Revoked
                    });
                    RemoveGrant(cached, AccessGrantState.Revoked);
                }
            }
        }
        foreach (var record in activeRecords.Concat(pendingRecords))
        {
            await LogGrantAsync(
                "grant_revoked",
                record,
                "success",
                reasonCode).ConfigureAwait(false);
        }
        return outcome.RevokedCount + cancelledPending;
    }

    private async Task<AccessGrantRequestOutcome> ApprovePendingAsync(
        PendingGrant pending,
        AccessGrantScope approvedScopes,
        CancellationToken cancellationToken)
    {
        var scopes = AccessGrantScopes.Normalize(approvedScopes);
        if (scopes == AccessGrantScope.None)
        {
            return DenyPending(pending, "no_allowed_scopes");
        }

        var now = _timeProvider.GetUtcNow();
        var hardExpiresAt = now + _hardLifetime;
        if (hardExpiresAt > pending.VaultGeneration.HardExpiresAt)
        {
            hardExpiresAt = pending.VaultGeneration.HardExpiresAt;
        }
        if (hardExpiresAt <= now)
        {
            return DenyPending(pending, "vault_session_expired");
        }

        var idleExpiresAt = now + _idleLifetime;
        if (idleExpiresAt > hardExpiresAt)
        {
            idleExpiresAt = hardExpiresAt;
        }

        var (plaintextSecret, secretHash) = GenerateSecret();
        var record = pending.Record with
        {
            SecretHash = secretHash,
            Scopes = scopes,
            State = AccessGrantState.Active,
            ApprovedAt = now,
            IdleExpiresAt = idleExpiresAt,
            HardExpiresAt = hardExpiresAt,
            VaultSessionId = pending.VaultGeneration.SessionId
        };

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (pending.Canceled)
                {
                    return DeniedWithoutCooldown(
                        "access_grant_revoked");
                }
            }

            if (_store is not null)
            {
                try
                {
                    await _store.CreateAsync(
                        ToStoredGrant(record),
                        GrantAuditContext(record),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return DeniedWithoutCooldown("storage_error");
                }
            }

            lock (_sync)
            {
                if (pending.Canceled)
                {
                    return DeniedWithoutCooldown(
                        "access_grant_revoked");
                }
                _grants[record.GrantId] = record;
                _activeSlots[new AccessGrantSlotKey(
                    record.ClientId,
                    record.ClientInstanceId)] = record;
                if (!_clientGrants.TryGetValue(record.ClientId, out var grantIds))
                {
                    grantIds = [];
                    _clientGrants[record.ClientId] = grantIds;
                }
                grantIds.Add(record.GrantId);
            }
            return new AccessGrantRequestOutcome(
                AccessGrantRequestStatus.Approved,
                new IssuedAccessGrant(record, plaintextSecret),
                RetryAfter: null,
                ReasonCode: null);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private AccessGrantRequestOutcome DenyPending(
        PendingGrant pending,
        string reasonCode)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            _cooldowns[pending.Record.ClientId] = new AccessGrantCooldown(
                pending.Record.ClientId,
                now + _denialCooldown,
                reasonCode);
        }
        return new AccessGrantRequestOutcome(
            AccessGrantRequestStatus.Denied,
            IssuedGrant: null,
            RetryAfter: now + _denialCooldown,
            ReasonCode: reasonCode);
    }

    private static AccessGrantRequestOutcome DeniedWithoutCooldown(
        string reasonCode)
    {
        return new AccessGrantRequestOutcome(
            AccessGrantRequestStatus.Denied,
            IssuedGrant: null,
            RetryAfter: null,
            ReasonCode: reasonCode);
    }

    private void CancelPending(PendingGrant pending, string reasonCode)
    {
        if (pending.Canceled)
        {
            return;
        }
        pending.Canceled = true;
        _pendingSlots.Remove(new AccessGrantSlotKey(
            pending.Record.ClientId,
            pending.Record.ClientInstanceId));
        pending.Completion.TrySetResult(
            DeniedWithoutCooldown(reasonCode));
        pending.Cancellation?.Cancel();
    }

    private void RemoveGrant(
        AccessGrantRecord record,
        AccessGrantState terminalState)
    {
        _grants.Remove(record.GrantId);
        _lastPersistedUse.Remove(record.GrantId);
        _activeSlots.Remove(new AccessGrantSlotKey(
            record.ClientId,
            record.ClientInstanceId));
        if (_clientGrants.TryGetValue(record.ClientId, out var grantIds))
        {
            grantIds.Remove(record.GrantId);
            if (grantIds.Count == 0)
            {
                _clientGrants.Remove(record.ClientId);
            }
        }
        if (terminalState is AccessGrantState.Revoked or AccessGrantState.Expired)
        {
            AddTerminalTombstone(record with { State = terminalState });
        }
    }

    private void PruneExpired()
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var record in _grants.Values.ToArray())
            {
                if ((record.IdleExpiresAt.HasValue && now >= record.IdleExpiresAt) ||
                    (record.HardExpiresAt.HasValue && now >= record.HardExpiresAt))
                {
                    RemoveGrant(record, AccessGrantState.Expired);
                }
            }
        }
    }

    private AccessGrantValidationResult? ValidateTerminal(
        string clientId,
        byte[] presentedHash)
    {
        if (!_clientTerminalGrants.TryGetValue(clientId, out var grantIds))
        {
            return null;
        }

        foreach (var grantId in grantIds)
        {
            if (_terminalGrants.TryGetValue(grantId, out var record) &&
                record.SecretHash is not null &&
                CryptographicOperations.FixedTimeEquals(
                    presentedHash,
                    record.SecretHash))
            {
                return new AccessGrantValidationResult(
                    record.State == AccessGrantState.Expired
                        ? AccessGrantValidationStatus.Expired
                        : AccessGrantValidationStatus.Revoked,
                    Grant: null,
                    ReasonCode: record.State == AccessGrantState.Expired
                        ? "access_grant_expired"
                        : "access_grant_revoked",
                    Record: record);
            }
        }
        return null;
    }

    private void AddTerminalTombstone(AccessGrantRecord record)
    {
        if (_terminalGrants.TryAdd(record.GrantId, record))
        {
            _terminalGrantOrder.Enqueue(record.GrantId);
            if (!_clientTerminalGrants.TryGetValue(
                    record.ClientId,
                    out var grantIds))
            {
                grantIds = [];
                _clientTerminalGrants[record.ClientId] = grantIds;
            }
            grantIds.Add(record.GrantId);
        }

        while (_terminalGrantOrder.Count > MaxTerminalTombstones)
        {
            var expiredId = _terminalGrantOrder.Dequeue();
            if (!_terminalGrants.Remove(expiredId, out var expiredRecord))
            {
                continue;
            }
            if (_clientTerminalGrants.TryGetValue(
                    expiredRecord.ClientId,
                    out var grantIds))
            {
                grantIds.Remove(expiredId);
                if (grantIds.Count == 0)
                {
                    _clientTerminalGrants.Remove(expiredRecord.ClientId);
                }
            }
        }
    }

    private static AccessGrantValidationResult Invalid()
    {
        return new AccessGrantValidationResult(
            AccessGrantValidationStatus.Invalid,
            Grant: null,
            ReasonCode: "access_grant_invalid",
            Record: null);
    }

    private Task LogValidationAsync(
        string clientId,
        AccessGrantValidationResult result)
    {
        if (result.Status == AccessGrantValidationStatus.Valid ||
            _securityLog is null)
        {
            return Task.CompletedTask;
        }

        var eventType = result.Status == AccessGrantValidationStatus.Expired
            ? "grant_expired"
            : "grant_rejected";
        var record = result.Record;
        return SecurityLogSafety.TryWriteAsync(
            _securityLog,
            new HostSecurityLogEvent(
                DateTimeOffset.UtcNow,
                eventType,
                record?.ClientId ?? clientId,
                ConnectionId: null,
                record?.ClientInstanceId,
                record?.GrantId,
                record is null
                    ? null
                    : ScopeNames(record.Scopes),
                RequestId: null,
                "denied",
                result.ReasonCode),
            CancellationToken.None);
    }

    private Task LogGrantAsync(
        string eventType,
        AccessGrantRecord record,
        string result,
        string? ReasonCode)
    {
        if (_securityLog is null)
        {
            return Task.CompletedTask;
        }
        return SecurityLogSafety.TryWriteAsync(
            _securityLog,
            new HostSecurityLogEvent(
                DateTimeOffset.UtcNow,
                eventType,
                record.ClientId,
                ConnectionId: null,
                record.ClientInstanceId,
                record.GrantId,
                ScopeNames(
                    record.Scopes == AccessGrantScope.None
                        ? record.RequestedScopes
                        : record.Scopes,
                    record.PathPolicy),
                RequestId: null,
                result,
                ReasonCode),
            CancellationToken.None);
    }

    private static string ScopeNames(
        AccessGrantScope scopes,
        AccessGrantPathPolicy? paths = null)
    {
        return string.Join(
            ",",
            (paths ?? AccessGrantPathPolicy.Unrestricted)
                .ToScopeNames(scopes)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static StoredAccessGrant ToStoredGrant(AccessGrantRecord record)
    {
        if (record.SecretHash is null ||
            record.ApprovedAt is null ||
            record.IdleExpiresAt is null ||
            record.HardExpiresAt is null ||
            record.VaultSessionId is null)
        {
            throw new InvalidOperationException(
                "Only fully issued Access Grants can be persisted.");
        }
        return new StoredAccessGrant(
            record.GrantId,
            record.ClientId.ToLowerInvariant(),
            "MCP",
            record.ClientInstanceId,
            record.VerifiedProcessSessionId,
            Guid.Empty,
            record.VaultSessionId.Value,
            record.State switch
            {
                AccessGrantState.Active => StoredAccessGrantState.Active,
                AccessGrantState.Revoked => StoredAccessGrantState.Revoked,
                AccessGrantState.Expired => StoredAccessGrantState.Expired,
                _ => throw new InvalidOperationException(
                    "Pending Access Grants cannot be persisted.")
            },
            (int)record.Scopes,
            (int)record.RequestedScopes,
            JsonSerializer.Serialize(
                record.PathPolicy ?? AccessGrantPathPolicy.Unrestricted),
            record.CreatedAt,
            record.ApprovedAt.Value,
            record.LastUsedAt,
            record.IdleExpiresAt.Value,
            record.HardExpiresAt.Value,
            record.State == AccessGrantState.Revoked
                ? record.ApprovedAt
                : null,
            record.State == AccessGrantState.Revoked
                ? "client-requested"
                : null,
            record.SecretHash);
    }

    private static AdminAccessGrantListItem ToListItem(
        StoredAccessGrant grant)
    {
        var paths = ParsePathPolicy(grant.PathPolicyJson);
        var scopes = (AccessGrantScope)grant.Scopes;
        return new AdminAccessGrantListItem(
            grant.GrantId,
            grant.ClientId,
            grant.ClientDisplayName,
            grant.ClientInstanceId,
            grant.State switch
            {
                StoredAccessGrantState.Active => "active",
                StoredAccessGrantState.Revoked => "revoked",
                StoredAccessGrantState.Expired => "expired",
                _ => throw new InvalidOperationException(
                    "Stored Access Grant state is invalid.")
            },
            AccessGrantScopes.ToNames(scopes)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            paths.ToScopeNames(scopes)
                .Select(ToDisplayScope)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            grant.CreatedAt,
            grant.HardExpiresAt,
            grant.LastUsedAt);
    }

    private static AdminAccessGrantDetail ToDetail(StoredAccessGrant grant)
    {
        var paths = ParsePathPolicy(grant.PathPolicyJson);
        var scopes = (AccessGrantScope)grant.Scopes;
        var requestedScopes = (AccessGrantScope)grant.RequestedScopes;
        return new AdminAccessGrantDetail(
            grant.GrantId,
            grant.ClientId,
            grant.ClientDisplayName,
            grant.ClientInstanceId,
            grant.State switch
            {
                StoredAccessGrantState.Active => "active",
                StoredAccessGrantState.Revoked => "revoked",
                StoredAccessGrantState.Expired => "expired",
                _ => throw new InvalidOperationException(
                    "Stored Access Grant state is invalid.")
            },
            AccessGrantScopes.ToNames(requestedScopes)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            AccessGrantScopes.ToNames(scopes)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            paths.ToScopeNames(scopes)
                .Select(ToDisplayScope)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            grant.CreatedAt,
            grant.ApprovedAt,
            grant.HardExpiresAt,
            grant.IdleExpiresAt,
            grant.LastUsedAt,
            grant.RevokedAt,
            grant.RevocationReason);
    }

    private static AccessGrantPathPolicy ParsePathPolicy(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AccessGrantPathPolicy>(json)
                ?? throw new InvalidOperationException(
                    "Access Grant PathPolicy was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Access Grant PathPolicy is invalid.",
                exception);
        }
    }

    private static string ToDisplayScope(string value)
    {
        return value is "read" or "write" or "delete"
            ? value + ":*"
            : value;
    }

    private static void AddImpact(
        IDictionary<(string ClientId, string DisplayName), ImpactAccumulator> groups,
        string clientId,
        string displayName,
        AccessGrantScope scopes,
        AccessGrantPathPolicy paths,
        bool isPending)
    {
        var key = (clientId.ToLowerInvariant(), displayName);
        if (!groups.TryGetValue(key, out var accumulator))
        {
            accumulator = new ImpactAccumulator(clientId, displayName);
            groups[key] = accumulator;
        }
        accumulator.Add(
            scopes,
            paths.ToScopeNames(scopes).Select(ToDisplayScope),
            isPending);
    }

    private static string CreatePreviewToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashPreviewToken(string token)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private void PruneRevokeAllSnapshots(DateTimeOffset now)
    {
        lock (_sync)
        {
            foreach (var pair in _revokeAllSnapshots.ToArray())
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    _revokeAllSnapshots.Remove(pair.Key);
                }
            }
        }
    }

    private static VaultAuditContext GrantAuditContext(
        AccessGrantRecord record)
    {
        return new VaultAuditContext(
            VaultActorKind.Mcp,
            record.ClientId,
            ClientInstanceId: record.ClientInstanceId,
            GrantId: record.GrantId,
            GrantScopes: ScopeNames(record.Scopes, record.PathPolicy));
    }

    private static (string PlaintextSecret, byte[] SecretHash) GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var plaintext = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return (plaintext, SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class PendingGrant(
        AccessGrantRecord record,
        AccessGrantVaultGeneration vaultGeneration)
    {
        public AccessGrantRecord Record { get; } = record;
        public AccessGrantVaultGeneration VaultGeneration { get; } = vaultGeneration;
        public TaskCompletionSource<AccessGrantRequestOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed record PendingGrantView(
        AccessGrantRecord Record,
        Guid VaultSessionId);

    private sealed record ApprovalScope(
        Guid ProcessSessionId,
        Guid? VaultSessionId);

    private sealed record RevokeAllSnapshot(
        string TokenHash,
        Guid AdminSessionId,
        Guid VaultSessionId,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<Guid> ActiveGrantIds,
        IReadOnlyList<Guid> PendingGrantIds,
        IReadOnlyList<ApprovalScope> ApprovalScopes);

    private sealed class ImpactAccumulator(
        string clientId,
        string clientDisplayName)
    {
        private const int MaxResourceScopes = 64;
        private readonly HashSet<string> _operations =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _resourceScopes =
            new(StringComparer.Ordinal);
        private int _activeCount;
        private int _pendingCount;
        private bool _isTruncated;

        public string ClientId { get; } = clientId.ToLowerInvariant();
        public string ClientDisplayName { get; } = clientDisplayName;

        public void Add(
            AccessGrantScope scopes,
            IEnumerable<string> resourceScopes,
            bool isPending)
        {
            if (isPending)
            {
                _pendingCount++;
            }
            else
            {
                _activeCount++;
            }
            _operations.UnionWith(AccessGrantScopes.ToNames(scopes));
            foreach (var scope in resourceScopes)
            {
                if (_resourceScopes.Count >= MaxResourceScopes)
                {
                    _isTruncated = true;
                    break;
                }
                _resourceScopes.Add(scope);
            }
        }

        public AdminAccessGrantImpactGroup ToContract()
        {
            return new AdminAccessGrantImpactGroup(
                ClientId,
                ClientDisplayName,
                _activeCount,
                _pendingCount,
                _operations.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                _resourceScopes.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                _isTruncated);
        }
    }

}
