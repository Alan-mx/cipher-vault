using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Configuration;
using CipherVault.Host.Contracts;

namespace CipherVault.Host.Security;

public sealed class AccessGrantService : IDisposable
{
    public static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultHardLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan DefaultDenialCooldown = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private const int MaxTerminalTombstones = 256;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _hardLifetime;
    private readonly TimeSpan _denialCooldown;
    private readonly ISecurityEventLog? _securityLog;
    private readonly Dictionary<Guid, AccessGrantRecord> _grants = [];
    private readonly Dictionary<AccessGrantSlotKey, AccessGrantRecord> _activeSlots = [];
    private readonly Dictionary<AccessGrantSlotKey, PendingGrant> _pendingSlots = [];
    private readonly Dictionary<string, HashSet<Guid>> _clientGrants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccessGrantCooldown> _cooldowns =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AccessGrantRecord> _terminalGrants = [];
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
        TimeSpan? sweepInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleLifetime = idleLifetime ?? DefaultIdleLifetime;
        _hardLifetime = hardLifetime ?? DefaultHardLifetime;
        _denialCooldown = denialCooldown ?? DefaultDenialCooldown;
        _securityLog = securityLog;
        _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
        _expirationSweeper = new Timer(
            _ => SweepExpired(),
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
        }
        finally
        {
            Volatile.Write(ref _sweepInProgress, 0);
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
                        VerifiedProcessSessionId: verifiedProcessSessionId),
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
                    allowed),
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
                outcome = ApprovePending(
                    pending,
                    approval.Scopes & effectiveRequest);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            (pending.Cancellation?.IsCancellationRequested ?? false))
        {
            outcome = DeniedWithoutCooldown("cancelled");
            propagateCancellation = true;
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
                        matched.VerifiedProcessSessionId),
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

    public async Task<int> RevokeClientAsync(string clientId, string reasonCode)
    {
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

    private AccessGrantRequestOutcome ApprovePending(
        PendingGrant pending,
        AccessGrantScope approvedScopes)
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
                        : record.Scopes),
                RequestId: null,
                result,
                ReasonCode),
            CancellationToken.None);
    }

    private static string ScopeNames(AccessGrantScope scopes)
    {
        return string.Join(
            ",",
            AccessGrantScopes.ToNames(scopes)
                .OrderBy(value => value, StringComparer.Ordinal));
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

}
