using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Grants;
using CipherVault.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core;

public sealed partial class VaultService
{
    private const string AccessGrantClockKey =
        "access_grant_last_observed_at_ms";
    private static readonly TimeSpan AccessGrantClockTolerance =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AccessGrantClockWriteInterval =
        TimeSpan.FromMinutes(1);

    public async Task CreateAsync(
        StoredAccessGrant grant,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(auditContext);
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        grant = grant with { VaultId = session.VaultId };
        ValidateStoredGrant(grant, session);

        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureClockAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            await ExpireActiveAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            using var command = session.Connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO access_grants(
                    grant_id, client_id, client_display_name, client_instance_id,
                    process_session_id, vault_id, vault_session_id, state, scopes,
                    requested_scopes, path_policy_json, created_at_ms,
                    approved_at_ms, last_used_at_ms, idle_expires_at_ms,
                    hard_expires_at_ms, revoked_at_ms, revocation_reason,
                    secret_hash)
                VALUES(
                    $grantId, $clientId, $clientDisplayName, $clientInstanceId,
                    $processSessionId, $vaultId, $vaultSessionId, $state, $scopes,
                    $requestedScopes, $pathPolicy, $createdAt,
                    $approvedAt, $lastUsedAt, $idleExpiresAt,
                    $hardExpiresAt, $revokedAt, $revocationReason,
                    $secretHash);
                """;
            AddGrantParameters(command, grant);
            await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await AppendGrantAuditAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                grant,
                "grant_created",
                "success",
                ReasonCode: null,
                auditContext,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 19)
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "An active Access Grant already exists for this client instance.",
                exception);
        }
    }

    public async Task<StoredAccessGrantPage> ListAsync(
        StoredAccessGrantQuery query,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 200)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Access Grant page limit must be between 1 and 200.");
        }
        var state = NormalizeStateFilter(query.State);
        var search = NormalizeSearch(query.Search);
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        var fingerprint = CursorCodec.Fingerprint(
            $"{state}\n{search}\n{query.Limit}");
        AccessGrantCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            CursorCodec.TryDecode<AccessGrantCursor>(
                query.Cursor,
                session.SessionId,
                "access-grants",
                fingerprint,
                out var decoded);
            cursor = decoded;
        }

        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);

        var counts = await ReadCountsAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            search,
            cancellationToken).ConfigureAwait(false);
        var totalCount = state is null
            ? counts.Total
            : state switch
            {
                "active" => counts.Active,
                "revoked" => counts.Revoked,
                "expired" => counts.Expired,
                _ => 0
            };

        var items = await ReadPageAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            state,
            search,
            cursor,
            query.Limit + 1,
            cancellationToken).ConfigureAwait(false);
        string? nextCursor = null;
        if (items.Count > query.Limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            nextCursor = CursorCodec.Encode(
                session.SessionId,
                "access-grants",
                fingerprint,
                new AccessGrantCursor(
                    last.CreatedAt.ToUnixTimeMilliseconds(),
                    last.GrantId));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredAccessGrantPage(
            items,
            nextCursor,
            totalCount,
            counts);
    }

    public async Task<StoredAccessGrant?> GetAsync(
        Guid grantId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        var grant = await ReadGrantAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            grantId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return grant;
    }

    public async Task<IReadOnlyList<StoredAccessGrant>> GetActiveGrantsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        var active = await ReadActiveGrantsAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<StoredAccessGrantRevokeOutcome> RevokeAsync(
        Guid grantId,
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(auditContext);
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        var grant = await ReadGrantAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            grantId,
            cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            await transaction.RollbackAsync(cancellationToken)
                .ConfigureAwait(false);
            return new StoredAccessGrantRevokeOutcome(
                grantId,
                StoredAccessGrantRevokeStatus.NotFound,
                RevokedAt: null,
                Reason: null);
        }
        if (grant.State != StoredAccessGrantState.Active)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoredAccessGrantRevokeOutcome(
                grantId,
                grant.State == StoredAccessGrantState.Expired
                    ? StoredAccessGrantRevokeStatus.AlreadyExpired
                    : StoredAccessGrantRevokeStatus.AlreadyRevoked,
                grant.RevokedAt,
                grant.RevocationReason);
        }

        var revokedAt = now < grant.ApprovedAt ? grant.ApprovedAt : now;
        await UpdateGrantTerminalStateAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            grantId,
            StoredAccessGrantState.Revoked,
            reason,
            revokedAt,
            cancellationToken).ConfigureAwait(false);
        var revokedGrant = grant with
        {
            State = StoredAccessGrantState.Revoked,
            RevokedAt = revokedAt,
            RevocationReason = reason
        };
        await AppendGrantAuditAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            revokedGrant,
            "grant_revoked",
            "success",
            reason,
            WithGrantContext(auditContext, revokedGrant),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredAccessGrantRevokeOutcome(
            grantId,
            StoredAccessGrantRevokeStatus.Revoked,
            revokedAt,
            reason);
    }

    public async Task<StoredAccessGrantRevokeAllOutcome> RevokeAllAsync(
        IReadOnlyCollection<Guid> activeGrantIds,
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeGrantIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(auditContext);
        var requested = activeGrantIds.Distinct().ToArray();
        var requestedSet = requested.ToHashSet();
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);

        var currentActive = await ReadActiveIdsAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            cancellationToken).ConfigureAwait(false);
        if (currentActive.Any(id => !requestedSet.Contains(id)))
        {
            await transaction.RollbackAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Access Grants changed after the revocation preview.");
        }

        var revokedCount = 0;
        var alreadyRevokedCount = 0;
        var alreadyExpiredCount = 0;
        foreach (var grantId in requested)
        {
            var grant = await ReadGrantAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                grantId,
                cancellationToken).ConfigureAwait(false);
            if (grant is null)
            {
                await transaction.RollbackAsync(cancellationToken)
                    .ConfigureAwait(false);
                throw new VaultException(
                    VaultErrorCode.Conflict,
                    "The revocation preview contained an unknown Access Grant.");
            }
            if (grant.State == StoredAccessGrantState.Revoked)
            {
                alreadyRevokedCount++;
                continue;
            }
            if (grant.State == StoredAccessGrantState.Expired)
            {
                alreadyExpiredCount++;
                continue;
            }

            var revokedAt = now < grant.ApprovedAt ? grant.ApprovedAt : now;
            await UpdateGrantTerminalStateAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                grantId,
                StoredAccessGrantState.Revoked,
                reason,
                revokedAt,
                cancellationToken).ConfigureAwait(false);
            var revokedGrant = grant with
            {
                State = StoredAccessGrantState.Revoked,
                RevokedAt = revokedAt,
                RevocationReason = reason
            };
            await AppendGrantAuditAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                revokedGrant,
                "grant_revoked",
                "success",
                reason,
                WithGrantContext(auditContext, revokedGrant),
                cancellationToken).ConfigureAwait(false);
            revokedCount++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredAccessGrantRevokeAllOutcome(
            revokedCount,
            alreadyRevokedCount,
            alreadyExpiredCount);
    }

    public Task<IReadOnlyList<Guid>> ExpireAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return ExecuteExpirationAsync(now, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> RevokeStaleActiveAsync(
        string reason,
        DateTimeOffset now,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(auditContext);
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        var active = await ReadActiveGrantsAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            cancellationToken).ConfigureAwait(false);
        var revokedIds = new List<Guid>(active.Count);
        foreach (var grant in active)
        {
            var revokedAt = now < grant.ApprovedAt ? grant.ApprovedAt : now;
            await UpdateGrantTerminalStateAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                grant.GrantId,
                StoredAccessGrantState.Revoked,
                reason,
                revokedAt,
                cancellationToken).ConfigureAwait(false);
            var revokedGrant = grant with
            {
                State = StoredAccessGrantState.Revoked,
                RevokedAt = revokedAt,
                RevocationReason = reason
            };
            await AppendGrantAuditAsync(
                session.Connection,
                (SqliteTransaction)transaction,
                revokedGrant,
                "grant_revoked",
                "success",
                reason,
                WithGrantContext(auditContext, revokedGrant),
                cancellationToken).ConfigureAwait(false);
            revokedIds.Add(grant.GrantId);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return revokedIds;
    }

    public async Task<bool> UpdateLastUsedAsync(
        Guid grantId,
        DateTimeOffset usedAt,
        DateTimeOffset idleExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            usedAt,
            cancellationToken).ConfigureAwait(false);
        using var command = session.Connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE access_grants
            SET last_used_at_ms = $lastUsedAt,
                idle_expires_at_ms = MIN(hard_expires_at_ms, $idleExpiresAt)
            WHERE grant_id = $grantId AND state = 'active';
            """;
        command.Parameters.AddWithValue(
            "$lastUsedAt",
            usedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$idleExpiresAt",
            idleExpiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task EnsureClockAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendDeniedAccessAsync(
        AccessDeniedAudit audit,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await AuditWriter.AppendAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            new VaultAuditContext(
                VaultActorKind.Mcp,
                audit.ClientId,
                audit.RequestId,
                audit.ClientInstanceId,
                audit.GrantId,
                audit.GrantScopes),
            audit.VaultSessionId,
            "access_denied",
            documentId: null,
            "denied",
            bytesReturned: null,
            JsonSerializer.Serialize(new
            {
                reasonCode = audit.ReasonCode,
                processSessionId = audit.ProcessSessionId.ToString("D")
            }),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Guid>> ExecuteExpirationAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var sessionLease = await AcquireGrantSessionAsync(
            cancellationToken).ConfigureAwait(false);
        var session = sessionLease.Session;
        await using var transaction = await session.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureClockAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        var expired = await ExpireActiveAsync(
            session.Connection,
            (SqliteTransaction)transaction,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired;
    }

    private static void ValidateStoredGrant(
        StoredAccessGrant grant,
        Session.VaultSession session)
    {
        if (grant.GrantId == Guid.Empty ||
            grant.ClientInstanceId == Guid.Empty ||
            grant.ProcessSessionId == Guid.Empty ||
            grant.VaultSessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(grant.ClientId) ||
            string.IsNullOrWhiteSpace(grant.ClientDisplayName) ||
            grant.SecretHash.Length != 32 ||
            grant.State != StoredAccessGrantState.Active)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Stored Access Grant fields are invalid.");
        }
        if (grant.VaultId != session.VaultId ||
            grant.VaultSessionId != session.SessionId)
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Stored Access Grant does not belong to the active Vault session.");
        }
    }

    private async Task<StoredAccessGrantCounts> ReadCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? search,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN state = 'active' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = 'revoked' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN state = 'expired' THEN 1 ELSE 0 END), 0)
            FROM access_grants
            WHERE ($search IS NULL
                OR instr(lower(client_id), lower($search)) > 0
                OR instr(lower(client_display_name), lower($search)) > 0);
            """;
        command.Parameters.AddWithValue(
            "$search",
            search is null ? DBNull.Value : search);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StoredAccessGrantCounts(0, 0, 0, 0);
        }
        return new StoredAccessGrantCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private async Task<List<StoredAccessGrant>> ReadPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? state,
        string? search,
        AccessGrantCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            {SelectGrantColumns}
            WHERE ($state IS NULL OR state = $state)
              AND ($search IS NULL
                OR instr(lower(client_id), lower($search)) > 0
                OR instr(lower(client_display_name), lower($search)) > 0)
              AND ($cursorCreatedAt IS NULL
                OR created_at_ms < $cursorCreatedAt
                OR (created_at_ms = $cursorCreatedAt AND grant_id < $cursorGrantId))
            ORDER BY created_at_ms DESC, grant_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", state ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$search", search ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$cursorCreatedAt",
            cursor?.CreatedAtMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$cursorGrantId",
            cursor?.GrantId.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var items = new List<StoredAccessGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(MapGrant(reader));
        }
        return items;
    }

    private async Task<StoredAccessGrant?> ReadGrantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            {SelectGrantColumns}
            WHERE grant_id = $grantId;
            """;
        command.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapGrant(reader)
            : null;
    }

    private async Task<List<Guid>> ReadActiveIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT grant_id FROM access_grants WHERE state = 'active';";
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }
        return ids;
    }

    private async Task<List<StoredAccessGrant>> ReadActiveGrantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            {SelectGrantColumns}
            WHERE state = 'active'
            ORDER BY created_at_ms, grant_id;
            """;
        var grants = new List<StoredAccessGrant>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            grants.Add(MapGrant(reader));
        }
        return grants;
    }

    private async Task<List<Guid>> ExpireActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var expired = new List<StoredAccessGrant>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                {SelectGrantColumns}
                WHERE state = 'active'
                  AND (idle_expires_at_ms <= $now OR hard_expires_at_ms <= $now)
                ORDER BY created_at_ms, grant_id;
                """;
            command.Parameters.AddWithValue("$now", nowMs);
            await using var reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add(MapGrant(reader));
            }
        }

        var expiredIds = new List<Guid>(expired.Count);
        foreach (var grant in expired)
        {
            await UpdateGrantTerminalStateAsync(
                connection,
                transaction,
                grant.GrantId,
                StoredAccessGrantState.Expired,
                reason: null,
                now,
                cancellationToken).ConfigureAwait(false);
            var expiredGrant = grant with { State = StoredAccessGrantState.Expired };
            await AppendGrantAuditAsync(
                connection,
                transaction,
                expiredGrant,
                "grant_expired",
                "success",
                "access_grant_expired",
                new VaultAuditContext(
                    VaultActorKind.System,
                    grant.ClientId,
                    ClientInstanceId: grant.ClientInstanceId,
                    GrantId: grant.GrantId,
                    GrantScopes: ScopeNames(grant.Scopes)),
                cancellationToken).ConfigureAwait(false);
            expiredIds.Add(grant.GrantId);
        }
        return expiredIds;
    }

    private static async Task UpdateGrantTerminalStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid grantId,
        StoredAccessGrantState state,
        string? reason,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE access_grants
            SET state = $state,
                revoked_at_ms = CASE WHEN $state = 'revoked' THEN $occurredAt ELSE NULL END,
                revocation_reason = CASE WHEN $state = 'revoked' THEN $reason ELSE NULL END
            WHERE grant_id = $grantId AND state = 'active';
            """;
        command.Parameters.AddWithValue("$state", ToStateValue(state));
        command.Parameters.AddWithValue(
            "$occurredAt",
            occurredAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$reason",
            reason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (changed != 1)
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "Access Grant state changed concurrently.");
        }
    }

    private static Task AppendGrantAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredAccessGrant grant,
        string action,
        string result,
        string? ReasonCode,
        VaultAuditContext auditContext,
        CancellationToken cancellationToken)
    {
        return AuditWriter.AppendAsync(
            connection,
            transaction,
            WithGrantContext(auditContext, grant),
            grant.VaultSessionId,
            action,
            documentId: null,
            result,
            bytesReturned: null,
            JsonSerializer.Serialize(new
            {
                reasonCode = ReasonCode,
                processSessionId = grant.ProcessSessionId.ToString("D"),
                state = ToStateValue(grant.State)
            }),
            cancellationToken);
    }

    private static VaultAuditContext WithGrantContext(
        VaultAuditContext context,
        StoredAccessGrant grant)
    {
        return context with
        {
            ClientName = string.IsNullOrWhiteSpace(context.ClientName)
                ? grant.ClientId
                : context.ClientName,
            ClientInstanceId = grant.ClientInstanceId,
            GrantId = grant.GrantId,
            GrantScopes = ScopeNames(grant.Scopes)
        };
    }

    private static async Task EnsureClockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT value FROM vault_meta WHERE key = $key;";
        read.Parameters.AddWithValue("$key", AccessGrantClockKey);
        var value = Convert.ToString(
            await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        _ = long.TryParse(value, out var lastObservedMs);
        if (lastObservedMs > 0)
        {
            var lastObserved = DateTimeOffset.FromUnixTimeMilliseconds(
                lastObservedMs);
            if (now < lastObserved - AccessGrantClockTolerance)
            {
                throw new VaultException(
                    VaultErrorCode.ClockRollback,
                    "System clock moved backwards while Access Grants are active.");
            }
        }
        var nowMs = now.ToUnixTimeMilliseconds();
        if (nowMs <= lastObservedMs ||
            nowMs - lastObservedMs <
                AccessGrantClockWriteInterval.TotalMilliseconds)
        {
            return;
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE vault_meta
            SET value = $value
            WHERE key = $key;
            """;
        update.Parameters.AddWithValue("$value", nowMs.ToString());
        update.Parameters.AddWithValue("$key", AccessGrantClockKey);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddGrantParameters(
        SqliteCommand command,
        StoredAccessGrant grant)
    {
        command.Parameters.AddWithValue("$grantId", grant.GrantId.ToString("D"));
        command.Parameters.AddWithValue("$clientId", grant.ClientId.ToLowerInvariant());
        command.Parameters.AddWithValue("$clientDisplayName", grant.ClientDisplayName);
        command.Parameters.AddWithValue(
            "$clientInstanceId",
            grant.ClientInstanceId.ToString("D"));
        command.Parameters.AddWithValue(
            "$processSessionId",
            grant.ProcessSessionId.ToString("D"));
        command.Parameters.AddWithValue("$vaultId", grant.VaultId.ToString("D"));
        command.Parameters.AddWithValue(
            "$vaultSessionId",
            grant.VaultSessionId.ToString("D"));
        command.Parameters.AddWithValue("$state", ToStateValue(grant.State));
        command.Parameters.AddWithValue("$scopes", grant.Scopes);
        command.Parameters.AddWithValue(
            "$requestedScopes",
            grant.RequestedScopes);
        command.Parameters.AddWithValue(
            "$pathPolicy",
            grant.PathPolicyJson);
        command.Parameters.AddWithValue(
            "$createdAt",
            grant.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$approvedAt",
            grant.ApprovedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$lastUsedAt",
            grant.LastUsedAt?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$idleExpiresAt",
            grant.IdleExpiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$hardExpiresAt",
            grant.HardExpiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$revokedAt",
            grant.RevokedAt?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$revocationReason",
            grant.RevocationReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$secretHash", grant.SecretHash);
    }

    private static StoredAccessGrant MapGrant(SqliteDataReader reader)
    {
        return new StoredAccessGrant(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            Guid.Parse(reader.GetString(3)),
            Guid.Parse(reader.GetString(4)),
            Guid.Parse(reader.GetString(5)),
            Guid.Parse(reader.GetString(6)),
            ParseState(reader.GetString(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
            reader.IsDBNull(13)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)),
            reader.IsDBNull(16)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.GetFieldValue<byte[]>(18));
    }

    private static string? NormalizeStateFilter(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }
        var normalized = state.Trim().ToLowerInvariant();
        return normalized is "active" or "revoked" or "expired"
            ? normalized
            : throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Access Grant state filter is invalid.");
    }

    private static string? NormalizeSearch(string? search)
    {
        var normalized = search?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.Length > 128)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Access Grant search text is too long.");
        }
        return normalized;
    }

    private static string ToStateValue(StoredAccessGrantState state)
    {
        return state switch
        {
            StoredAccessGrantState.Active => "active",
            StoredAccessGrantState.Revoked => "revoked",
            StoredAccessGrantState.Expired => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    private static StoredAccessGrantState ParseState(string state)
    {
        return state switch
        {
            "active" => StoredAccessGrantState.Active,
            "revoked" => StoredAccessGrantState.Revoked,
            "expired" => StoredAccessGrantState.Expired,
            _ => throw new VaultException(
                VaultErrorCode.CorruptVault,
                "Access Grant state is invalid.")
        };
    }

    private static string ScopeNames(int scopes)
    {
        var names = new List<string>();
        if ((scopes & 1) != 0)
        {
            names.Add("read");
        }
        if ((scopes & 2) != 0)
        {
            names.Add("write");
        }
        if ((scopes & 4) != 0)
        {
            names.Add("delete");
        }
        return string.Join(',', names);
    }

    private async Task<GrantSessionLease> AcquireGrantSessionAsync(
        CancellationToken cancellationToken)
    {
        var lockGate = !_sessionClosing.Value;
        if (lockGate)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var session = _session
                ?? throw new VaultException(
                    VaultErrorCode.SessionLocked,
                    "Vault session is locked.");
            if (!_sessionClosing.Value)
            {
                RefreshActivity(session);
                if (IsExpired(session))
                {
                    throw new VaultException(
                        VaultErrorCode.SessionExpired,
                        "Vault session expired.");
                }
                if (!session.RecoveryVerified)
                {
                    throw new VaultException(
                        VaultErrorCode.RecoveryVerificationRequired,
                        "The recovery key must be confirmed before accessing Vault content.");
                }
            }
            return new GrantSessionLease(this, session, lockGate);
        }
        catch
        {
            if (lockGate)
            {
                _gate.Release();
            }
            throw;
        }
    }

    private const string SelectGrantColumns =
        """
        SELECT
            grant_id,
            client_id,
            client_display_name,
            client_instance_id,
            process_session_id,
            vault_id,
            vault_session_id,
            state,
            scopes,
            requested_scopes,
            path_policy_json,
            created_at_ms,
            approved_at_ms,
            last_used_at_ms,
            idle_expires_at_ms,
            hard_expires_at_ms,
            revoked_at_ms,
            revocation_reason,
            secret_hash
        FROM access_grants
        """;

    private sealed record AccessGrantCursor(long CreatedAtMs, Guid GrantId);

    private sealed class GrantSessionLease(
        VaultService owner,
        Session.VaultSession session,
        bool lockGate) : IAsyncDisposable
    {
        public Session.VaultSession Session { get; } = session;

        public ValueTask DisposeAsync()
        {
            if (lockGate)
            {
                owner._gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
