using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Configuration;

namespace CipherVault.Host.Security;

public sealed class AccessGrantService
{
    public static readonly TimeSpan DefaultIdleLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultHardLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan DefaultDenialCooldown = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleLifetime;
    private readonly TimeSpan _hardLifetime;
    private readonly TimeSpan _denialCooldown;
    private readonly Dictionary<Guid, AccessGrantRecord> _grants = [];
    private readonly Dictionary<AccessGrantSlotKey, AccessGrantRecord> _activeSlots = [];
    private readonly Dictionary<AccessGrantSlotKey, PendingGrant> _pendingSlots = [];
    private readonly Dictionary<string, HashSet<Guid>> _clientGrants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AccessGrantCooldown> _cooldowns =
        new(StringComparer.OrdinalIgnoreCase);

    public AccessGrantService(
        TimeProvider? timeProvider = null,
        TimeSpan? idleLifetime = null,
        TimeSpan? hardLifetime = null,
        TimeSpan? denialCooldown = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleLifetime = idleLifetime ?? DefaultIdleLifetime;
        _hardLifetime = hardLifetime ?? DefaultHardLifetime;
        _denialCooldown = denialCooldown ?? DefaultDenialCooldown;
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

    public async Task<AccessGrantRequestOutcome> RequestAsync(
        string clientId,
        Guid clientInstanceId,
        McpAccessMode tokenAccessMode,
        AccessGrantScope requestedScopes,
        AccessGrantVaultGeneration vaultGeneration,
        Func<AccessGrantApprovalRequest, CancellationToken, Task<AccessGrantApprovalResult>>
            approvalHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(approvalHandler);

        var allowed = AccessGrantScopes.Ceiling(tokenAccessMode);
        var effectiveRequest = AccessGrantScopes.Normalize(requestedScopes) & allowed;
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
                        DenialReason: null),
                    vaultGeneration);
                _pendingSlots[slot] = pending;
                isOwner = true;
            }
        }

        if (!isOwner)
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        AccessGrantRequestOutcome outcome;
        var propagateCancellation = false;
        try
        {
            var approval = await approvalHandler(
                new AccessGrantApprovalRequest(
                    pending.Record.GrantId,
                    clientId,
                    clientInstanceId,
                    tokenAccessMode,
                    effectiveRequest,
                    allowed),
                cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            }
        }

        pending.Completion.TrySetResult(outcome);
        if (propagateCancellation)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        return outcome;
    }

    public AccessGrantValidationResult Validate(
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
                    return Invalid();
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
                    return Invalid();
                }

                var now = _timeProvider.GetUtcNow();
                if (matched.State != AccessGrantState.Active ||
                    matched.HardExpiresAt is null ||
                    matched.IdleExpiresAt is null ||
                    now >= matched.HardExpiresAt ||
                    now >= matched.IdleExpiresAt)
                {
                    RemoveGrant(matched);
                    return new AccessGrantValidationResult(
                        AccessGrantValidationStatus.Expired,
                        Grant: null,
                        ReasonCode: "access_grant_expired");
                }

                if (matched.VaultSessionId != vaultGeneration.SessionId)
                {
                    RemoveGrant(matched);
                    return new AccessGrantValidationResult(
                        AccessGrantValidationStatus.Revoked,
                        Grant: null,
                        ReasonCode: "access_grant_revoked");
                }

                return new AccessGrantValidationResult(
                    AccessGrantValidationStatus.Valid,
                    new AccessGrantContext(
                        matched.GrantId,
                        matched.ClientId,
                        matched.ClientInstanceId,
                        matched.Scopes,
                        matched.VaultSessionId.Value),
                    ReasonCode: null);
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
                record.HardExpiresAt is null)
            {
                return false;
            }

            var now = usedAt ?? _timeProvider.GetUtcNow();
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

    public AccessGrantRevokeStatus Revoke(
        string clientId,
        Guid grantId,
        string plaintextSecret,
        out AccessGrantRecord? revoked)
    {
        var presentedHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(plaintextSecret));
        try
        {
            lock (_sync)
            {
                if (!_grants.TryGetValue(grantId, out var record))
                {
                    revoked = null;
                    return AccessGrantRevokeStatus.AlreadyRemoved;
                }
                if (!string.Equals(
                        record.ClientId,
                        clientId,
                        StringComparison.OrdinalIgnoreCase) ||
                    !CryptographicOperations.FixedTimeEquals(
                        presentedHash,
                        record.SecretHash ?? []))
                {
                    revoked = null;
                    return AccessGrantRevokeStatus.Invalid;
                }

                revoked = record with { State = AccessGrantState.Revoked };
                RemoveGrant(record);
                return AccessGrantRevokeStatus.Revoked;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedHash);
        }
    }

    public bool TryRevokeById(Guid grantId, out AccessGrantRecord? revoked)
    {
        lock (_sync)
        {
            var pending = _pendingSlots.Values.FirstOrDefault(item =>
                item.Record.GrantId == grantId);
            if (pending is not null)
            {
                revoked = pending.Record with { State = AccessGrantState.Revoked };
                CancelPending(pending, "revoked");
                return true;
            }

            if (!_grants.TryGetValue(grantId, out var record))
            {
                revoked = null;
                return false;
            }
            revoked = record with { State = AccessGrantState.Revoked };
            RemoveGrant(record);
            return true;
        }
    }

    public IReadOnlyList<AccessGrantRecord> List()
    {
        lock (_sync)
        {
            return _grants.Values
                .Concat(_pendingSlots.Values.Select(item => item.Record))
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();
        }
    }

    public int RevokeClient(string clientId, string reasonCode)
    {
        lock (_sync)
        {
            foreach (var pending in _pendingSlots.Values
                         .Where(item => string.Equals(
                             item.Record.ClientId,
                             clientId,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                CancelPending(pending, reasonCode);
            }

            if (!_clientGrants.TryGetValue(clientId, out var grantIds))
            {
                return 0;
            }

            var count = 0;
            foreach (var grantId in grantIds.ToArray())
            {
                if (_grants.TryGetValue(grantId, out var record))
                {
                    RemoveGrant(record);
                    count++;
                }
            }
            _ = reasonCode;
            return count;
        }
    }

    public int RevokeAll(string reasonCode)
    {
        lock (_sync)
        {
            foreach (var pending in _pendingSlots.Values.ToArray())
            {
                CancelPending(pending, reasonCode);
            }

            var count = 0;
            foreach (var record in _grants.Values.ToArray())
            {
                RemoveGrant(record);
                count++;
            }
            return count;
        }
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
    }

    private void RemoveGrant(AccessGrantRecord record)
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
    }

    private static AccessGrantValidationResult Invalid()
    {
        return new AccessGrantValidationResult(
            AccessGrantValidationStatus.Invalid,
            Grant: null,
            ReasonCode: "access_grant_invalid");
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
    }
}
