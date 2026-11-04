using System.Globalization;
using CipherVault.Core.Database;
using CipherVault.Core.Semantic;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Storage.Semantic;

public sealed record SemanticIndexJobCounts(
    int Pending,
    int Running,
    int Failed);

public static class SemanticIndexJobStore
{
    public static Task UpsertIndexAsync(
        VaultOperationContext context,
        string profileId,
        Guid documentId,
        int revision,
        string? contentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpsertAsync(
            context,
            profileId,
            documentId,
            SemanticIndexJobType.Index,
            revision,
            contentHash,
            now,
            cancellationToken);

    public static Task UpsertDeleteAsync(
        VaultOperationContext context,
        string profileId,
        Guid documentId,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpsertAsync(
            context,
            profileId,
            documentId,
            SemanticIndexJobType.Delete,
            revision,
            contentHash: null,
            now,
            cancellationToken);

    public static Task UpsertPurgeAsync(
        VaultOperationContext context,
        string profileId,
        Guid documentId,
        int revision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpsertAsync(
            context,
            profileId,
            documentId,
            SemanticIndexJobType.Purge,
            revision,
            contentHash: null,
            now,
            cancellationToken);

    public static async Task EnqueueInitialBackfillAsync(
        VaultOperationContext context,
        string profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO semantic_index_jobs(
                document_id,
                profile_id,
                job_type,
                status,
                requested_revision,
                document_content_hash,
                attempt_count,
                available_at,
                last_error_code,
                created_at,
                updated_at,
                completed_at)
            SELECT
                document_id,
                $profileId,
                'index',
                'pending',
                revision,
                NULL,
                0,
                $now,
                NULL,
                $now,
                $now,
                NULL
            FROM documents
            WHERE deleted_at IS NULL
            ON CONFLICT(document_id, profile_id) DO UPDATE SET
                job_type = 'index',
                status = 'pending',
                requested_revision = excluded.requested_revision,
                document_content_hash = NULL,
                attempt_count = 0,
                available_at = excluded.available_at,
                last_error_code = NULL,
                updated_at = excluded.updated_at,
                completed_at = NULL;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> DeleteProfileJobsAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            "DELETE FROM semantic_index_jobs WHERE profile_id = $profileId;";
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> RecoverRunningAsync(
        VaultOperationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_index_jobs
            SET status = 'pending',
                available_at = $now,
                updated_at = $now
            WHERE status = 'running';
            """;
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        return await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<SemanticIndexJob?> ClaimNextAsync(
        VaultOperationContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SemanticIndexJob? candidate;
        using (var select = context.CreateCommand())
        {
            select.Command.CommandText =
                $"""
                 {SelectColumns}
                 FROM semantic_index_jobs
                 WHERE status IN ('pending', 'failed')
                   AND attempt_count < 20
                   AND available_at <= $now
                 ORDER BY available_at, updated_at, document_id
                 LIMIT 1;
                 """;
            select.Command.Parameters.AddWithValue(
                "$now",
                now.ToUniversalTime().ToString("O"));
            await using var reader =
                await select.Command.ExecuteReaderAsync(cancellationToken);
            candidate = await reader.ReadAsync(cancellationToken)
                ? ReadJob(reader)
                : null;
        }
        if (candidate is null)
        {
            return null;
        }

        using var update = context.CreateCommand();
        update.Command.CommandText =
            """
            UPDATE semantic_index_jobs
            SET status = 'running',
                attempt_count = attempt_count + 1,
                updated_at = $now
            WHERE document_id = $documentId
              AND profile_id = $profileId
              AND status IN ('pending', 'failed')
              AND available_at <= $now
              AND attempt_count < 20;
            """;
        update.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        update.Command.Parameters.AddWithValue(
            "$documentId",
            candidate.DocumentId.ToString("D"));
        update.Command.Parameters.AddWithValue(
            "$profileId",
            candidate.ProfileId);
        if (await update.Command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return null;
        }
        return candidate with
        {
            Status = SemanticIndexJobStatus.Running,
            AttemptCount = candidate.AttemptCount + 1,
            UpdatedAt = now
        };
    }

    public static async Task<SemanticIndexJob?> GetAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            $"""
             {SelectColumns}
             FROM semantic_index_jobs
             WHERE document_id = $documentId
               AND profile_id = $profileId;
             """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadJob(reader)
            : null;
    }

    public static async Task CompleteAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_index_jobs
            SET status = 'completed',
                last_error_code = NULL,
                updated_at = $now,
                completed_at = $now
            WHERE document_id = $documentId
              AND profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task DeleteAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            DELETE FROM semantic_index_jobs
            WHERE document_id = $documentId
              AND profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task FailAsync(
        VaultOperationContext context,
        Guid documentId,
        string profileId,
        string errorCode,
        DateTimeOffset availableAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_index_jobs
            SET status = 'failed',
                last_error_code = $errorCode,
                available_at = $availableAt,
                updated_at = $now,
                completed_at = NULL
            WHERE document_id = $documentId
              AND profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue("$errorCode", errorCode);
        command.Command.Parameters.AddWithValue(
            "$availableAt",
            availableAt.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task RequeueWithLatestDocumentAsync(
        VaultOperationContext context,
        SemanticIndexJob job,
        int latestRevision,
        string latestContentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            UPDATE semantic_index_jobs
            SET job_type = 'index',
                status = 'pending',
                requested_revision = $latestRevision,
                document_content_hash = $latestContentHash,
                attempt_count = max(attempt_count - 1, 0),
                available_at = $now,
                last_error_code = NULL,
                updated_at = $now,
                completed_at = NULL
            WHERE document_id = $documentId
              AND profile_id = $profileId
              AND status = 'running'
              AND requested_revision = $expectedRevision;
            """;
        command.Command.Parameters.AddWithValue(
            "$latestRevision",
            latestRevision);
        command.Command.Parameters.AddWithValue(
            "$latestContentHash",
            latestContentHash);
        command.Command.Parameters.AddWithValue(
            "$now",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$documentId",
            job.DocumentId.ToString("D"));
        command.Command.Parameters.AddWithValue(
            "$profileId",
            job.ProfileId);
        command.Command.Parameters.AddWithValue(
            "$expectedRevision",
            job.RequestedRevision);
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<SemanticIndexJobCounts> GetCountsAsync(
        VaultOperationContext context,
        string profileId,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            SELECT
                coalesce(sum(CASE WHEN status = 'pending' THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status = 'running' THEN 1 ELSE 0 END), 0),
                coalesce(sum(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0)
            FROM semantic_index_jobs
            WHERE profile_id = $profileId;
            """;
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader =
            await command.Command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SemanticIndexJobCounts(0, 0, 0);
        }
        return new SemanticIndexJobCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task UpsertAsync(
        VaultOperationContext context,
        string profileId,
        Guid documentId,
        SemanticIndexJobType type,
        int revision,
        string? contentHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var command = context.CreateCommand();
        command.Command.CommandText =
            """
            INSERT INTO semantic_index_jobs(
                document_id,
                profile_id,
                job_type,
                status,
                requested_revision,
                document_content_hash,
                attempt_count,
                available_at,
                last_error_code,
                created_at,
                updated_at,
                completed_at)
            VALUES(
                $documentId,
                $profileId,
                $jobType,
                'pending',
                $requestedRevision,
                $contentHash,
                0,
                $availableAt,
                NULL,
                $createdAt,
                $updatedAt,
                NULL)
            ON CONFLICT(document_id, profile_id) DO UPDATE SET
                job_type = excluded.job_type,
                status = 'pending',
                requested_revision = excluded.requested_revision,
                document_content_hash = excluded.document_content_hash,
                attempt_count = 0,
                available_at = excluded.available_at,
                last_error_code = NULL,
                updated_at = excluded.updated_at,
                completed_at = NULL;
            """;
        command.Command.Parameters.AddWithValue(
            "$documentId",
            documentId.ToString("D"));
        command.Command.Parameters.AddWithValue("$profileId", profileId);
        command.Command.Parameters.AddWithValue(
            "$jobType",
            type.ToDatabaseValue());
        command.Command.Parameters.AddWithValue(
            "$requestedRevision",
            revision);
        command.Command.Parameters.AddWithValue(
            "$contentHash",
            contentHash ?? (object)DBNull.Value);
        command.Command.Parameters.AddWithValue(
            "$availableAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$createdAt",
            now.ToUniversalTime().ToString("O"));
        command.Command.Parameters.AddWithValue(
            "$updatedAt",
            now.ToUniversalTime().ToString("O"));
        await command.Command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectColumns =
        """
        SELECT
            document_id,
            profile_id,
            job_type,
            status,
            requested_revision,
            document_content_hash,
            attempt_count,
            available_at,
            last_error_code,
            created_at,
            updated_at,
            completed_at
        """;

    private static SemanticIndexJob ReadJob(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            SemanticValueMapping.ParseJobType(reader.GetString(2)),
            SemanticValueMapping.ParseJobStatus(reader.GetString(3)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            ParseTimestamp(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();
}
