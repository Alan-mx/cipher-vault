using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Core.Session;
using CipherVault.Core.Storage;

namespace CipherVault.Core.ImportExport;

internal static class ImportExportService
{
    private const int ImportBatchSize = 100;
    private const string ImportItemSavepoint = "import_item";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly TimeSpan StaleExportDirectoryAge = TimeSpan.FromHours(24);

    public static async Task<VaultImportPreview> PreviewImportAsync(
        string vaultPath,
        SqliteConnection connection,
        Guid sessionId,
        string sourceRoot,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Import source directory was not found.");
        }
        if (IsUnderPath(fullRoot, vaultPath))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Import source cannot be inside the Vault.");
        }

        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT source_path_norm FROM documents WHERE deleted_at_ms IS NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                activePaths.Add(reader.GetString(0));
            }
        }

        var supported = new List<ImportCandidate>();
        var unsupportedItems = new List<VaultImportPreviewItem>();
        var unsupported = 0;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", enumeration))
        {
            var relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            if (IsIgnoredPath(relative))
            {
                continue;
            }
            var extension = Path.GetExtension(file);
            var format = extension.ToLowerInvariant() switch
            {
                ".md" => "md",
                ".txt" => "txt",
                _ => null
            };
            if (format is null)
            {
                unsupported++;
                continue;
            }

            var title = Path.GetFileNameWithoutExtension(file);
            var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/');
            var normalized = relative.ToLowerInvariant();
            var conflict = activePaths.Contains(normalized);
            var rejectionReason = ValidateImportCandidate(title, folder, format, file);
            if (rejectionReason is not null)
            {
                unsupported++;
                unsupportedItems.Add(new VaultImportPreviewItem(
                    relative,
                    format,
                    GetFileLength(file),
                    "unsupported",
                    rejectionReason));
                continue;
            }

            var source = await ReadMetadataAndHashAsync(file, cancellationToken)
                .ConfigureAwait(false);
            if (source.SizeBytes > DocumentWriter.MaxContentBytes)
            {
                unsupported++;
                unsupportedItems.Add(new VaultImportPreviewItem(
                    relative,
                    format,
                    source.SizeBytes,
                    "unsupported",
                    "content_too_large"));
                continue;
            }
            supported.Add(new ImportCandidate(
                relative,
                format,
                source.Hash,
                source.SizeBytes,
                conflict));
        }

        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using (var job = connection.CreateCommand())
        {
            job.Transaction = (SqliteTransaction)transaction;
            job.CommandText =
                """
                INSERT INTO import_jobs(
                    job_id, source_root, state, total_documents,
                    processed_documents, skipped_documents, started_at_ms,
                    finished_at_ms, error_json)
                VALUES(
                    $id, $root, 'preview', $total,
                    0, $skipped, $started, NULL, '{}');
                """;
            job.Parameters.AddWithValue("$id", jobId.ToString("D"));
            job.Parameters.AddWithValue("$root", fullRoot);
            job.Parameters.AddWithValue("$total", supported.Count);
            job.Parameters.AddWithValue("$skipped", supported.Count(item => item.Conflict));
            job.Parameters.AddWithValue("$started", now);
            await job.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var candidate in supported)
        {
            using var item = connection.CreateCommand();
            item.Transaction = (SqliteTransaction)transaction;
            item.CommandText =
                """
                INSERT INTO import_items(
                    job_id, source_path_norm, payload_hmac, document_id, state, error_json)
                VALUES(
                    $jobId, $path, $hash, NULL, $state, $error);
                """;
            item.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            item.Parameters.AddWithValue("$path", candidate.RelativePath.ToLowerInvariant());
            item.Parameters.AddWithValue("$hash", candidate.SourceHash);
            item.Parameters.AddWithValue(
                "$state",
                candidate.Conflict ? "skipped" : "pending");
            item.Parameters.AddWithValue(
                "$error",
                candidate.Conflict ? "{\"reason\":\"active_path_conflict\"}" : "{}");
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AuditWriter.AppendAsync(
            connection,
            (SqliteTransaction)transaction,
            actor,
            sessionId,
            "import_preview",
            null,
            "success",
            null,
            JsonSerializer.Serialize(new { jobId, supported = supported.Count, unsupported }),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var items = supported
            .Select(candidate => new VaultImportPreviewItem(
                candidate.RelativePath,
                candidate.Format,
                candidate.SizeBytes,
                candidate.Conflict ? "skipped" : "pending",
                candidate.Conflict ? "active_path_conflict" : null))
            .Concat(unsupportedItems)
            .ToList();
        return new VaultImportPreview(
            jobId,
            fullRoot,
            supported.Count + unsupported,
            supported.Count,
            unsupported,
            supported.Count(item => item.Conflict),
            items);
    }

    public static async Task<VaultImportResult> CommitImportAsync(
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        Guid jobId,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        var job = await ReadJobAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
        if (job.State == "completed")
        {
            return new VaultImportResult(jobId, job.Processed, job.Skipped, job.Failed, job.State);
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                "UPDATE import_jobs SET state = 'importing' WHERE job_id = $id;";
            update.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var existing = await ReadImportItemCountsAsync(connection, jobId, cancellationToken)
            .ConfigureAwait(false);
        var imported = existing.Imported;
        var skipped = existing.Skipped;
        var failed = existing.Failed;
        var pending = await ReadPendingItemsAsync(connection, jobId, cancellationToken)
            .ConfigureAwait(false);
        for (var batchStart = 0; batchStart < pending.Count; batchStart += ImportBatchSize)
        {
            var batchEnd = Math.Min(batchStart + ImportBatchSize, pending.Count);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var sqliteTransaction = (SqliteTransaction)transaction;
            var batchResolutionKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = batchStart; index < batchEnd; index++)
            {
                var itemResult = await ImportItemAsync(
                    connection,
                    sqliteTransaction,
                    sessionId,
                    integrityKey,
                    job,
                    jobId,
                    pending[index],
                    actor,
                    cancellationToken).ConfigureAwait(false);
                switch (itemResult.Outcome)
                {
                    case ImportItemOutcome.Imported:
                        imported++;
                        batchResolutionKeys.UnionWith(itemResult.ResolutionKeys);
                        break;
                    case ImportItemOutcome.Skipped:
                        skipped++;
                        break;
                    case ImportItemOutcome.Failed:
                        failed++;
                        break;
                }
            }
            if (batchResolutionKeys.Count > 0)
            {
                await DocumentLinkIndexer.ReconcileAsync(
                    connection,
                    sqliteTransaction,
                    batchResolutionKeys,
                    cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await PauseAfterBatchForTestAsync(cancellationToken).ConfigureAwait(false);
        }

        var finalState = failed == 0 ? "completed" : "failed";
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE import_jobs
                SET state = $state,
                    processed_documents = $processed,
                    skipped_documents = $skipped,
                    finished_at_ms = $finished
                WHERE job_id = $id;
                """;
            update.Parameters.AddWithValue("$state", finalState);
            update.Parameters.AddWithValue("$processed", imported);
            update.Parameters.AddWithValue("$skipped", skipped);
            update.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return new VaultImportResult(jobId, imported, skipped, failed, finalState);
    }

    private static async Task PauseAfterBatchForTestAsync(
        CancellationToken cancellationToken)
    {
        var markerPath = Environment.GetEnvironmentVariable(
            "CIPHER_VAULT_TEST_IMPORT_BATCH_MARKER");
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return;
        }

        var fullMarkerPath = Path.GetFullPath(markerPath);
        var temporaryPath =
            $"{fullMarkerPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                "batch-committed",
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullMarkerPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<VaultExportResult> ExportAsync(
        string vaultPath,
        SqliteConnection connection,
        Guid sessionId,
        byte[] integrityKey,
        string destination,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        var fullDestination = Path.GetFullPath(destination);
        if (IsUnderPath(fullDestination, vaultPath))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Export destination cannot be inside the Vault.");
        }
        if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
        {
            throw new VaultException(VaultErrorCode.AlreadyExists, "Export destination must not exist.");
        }
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new VaultException(VaultErrorCode.InvalidInput, "Export destination has no parent.");
        Directory.CreateDirectory(parent);
        var temporaryDirectory = Path.Combine(
            parent,
            $".{Path.GetFileName(fullDestination)}.export-{Guid.NewGuid():N}");
        CleanupStaleExportDirectories(parent, Path.GetFileName(fullDestination));
        Directory.CreateDirectory(temporaryDirectory);
        var exported = new List<ExportedFile>();
        try
        {
            VaultFileSecurity.ProtectDirectory(temporaryDirectory);
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT document_id, title, source_path, format, content,
                           headings_json, tags_json, payload_hmac, revision
                    FROM documents
                    WHERE deleted_at_ms IS NULL
                    ORDER BY source_path_norm, document_id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var title = reader.GetString(1);
                    var sourcePath = reader.GetString(2);
                    var format = reader.GetString(3);
                    var content = reader.GetString(4);
                    var headings = reader.GetString(5);
                    var tags = reader.GetString(6);
                    var hmac = (byte[])reader["payload_hmac"];
                    var revision = reader.GetInt32(8);
                    if (!DocumentHmac.Verify(
                        integrityKey, hmac, id, revision, title, sourcePath, format,
                        headings, tags, content))
                    {
                        throw new VaultException(
                            VaultErrorCode.CorruptVault,
                            "Document integrity check failed during export.");
                    }

                    var (safePath, _) = VirtualPathPolicy.NormalizeDocumentPath(sourcePath, format);
                    var outputPath = Path.Combine(
                        temporaryDirectory,
                        safePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    await using (var output = new FileStream(
                        outputPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
                    {
                        await writer.WriteAsync(content.AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(flushToDisk: true);
                    }
                    var bytes = new FileInfo(outputPath).Length;
                    exported.Add(new ExportedFile(
                        safePath,
                        bytes,
                        Convert.ToHexString(
                            await ComputeSha256Async(outputPath, cancellationToken)
                                .ConfigureAwait(false))));
                }
            }

            var manifest = new ExportManifest(
                "cipher-vault-export",
                1,
                DateTimeOffset.UtcNow,
                exported);
            var manifestPath = Path.Combine(temporaryDirectory, "vault-export-manifest.json");
            var checksumsPath = Path.Combine(temporaryDirectory, "vault-export-checksums.txt");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllLinesAsync(
                checksumsPath,
                exported.Select(item => $"{item.Sha256}  {item.RelativePath}"),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(temporaryDirectory, fullDestination);
            var auditRecorded = await AuditWriter.TryAppendAsync(
                connection,
                actor,
                sessionId,
                "export_snapshot",
                null,
                "success",
                null,
                JsonSerializer.Serialize(new
                {
                    exportedDocuments = exported.Count,
                    totalBytes = exported.Sum(item => item.SizeBytes)
                }),
                cancellationToken).ConfigureAwait(false);
            return new VaultExportResult(
                fullDestination,
                exported.Count,
                Path.Combine(fullDestination, "vault-export-manifest.json"),
                auditRecorded);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            throw;
        }
    }

    private static async Task<ImportJob> ReadJobAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_root, state, processed_documents, skipped_documents
            FROM import_jobs WHERE job_id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new VaultException(VaultErrorCode.NotFound, "Import job was not found.");
        }
        return new ImportJob(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task<List<ImportItem>> ReadPendingItemsAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_path_norm, payload_hmac
            FROM import_items
            WHERE job_id = $id AND state = 'pending'
            ORDER BY source_path_norm;
            """;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        var items = new List<ImportItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ImportItem(reader.GetString(0), (byte[])reader["payload_hmac"]));
        }
        return items;
    }

    private static async Task<(int Imported, int Skipped, int Failed)> ReadImportItemCountsAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                SUM(CASE WHEN state = 'imported' THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 'skipped' THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = 'failed' THEN 1 ELSE 0 END)
            FROM import_items
            WHERE job_id = $id;
            """;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, 0, 0);
        }
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }

    private static async Task<ImportItemResult> ImportItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        byte[] integrityKey,
        ImportJob job,
        Guid jobId,
        ImportItem item,
        VaultActorKind actor,
        CancellationToken cancellationToken)
    {
        await ExecuteSavepointCommandAsync(
            connection,
            transaction,
            $"SAVEPOINT {ImportItemSavepoint};",
            cancellationToken).ConfigureAwait(false);
        try
        {
            var sourcePath = ResolveSourcePath(job.SourceRoot, item.RelativePath);
            if (!File.Exists(sourcePath))
            {
                throw new VaultException(VaultErrorCode.NotFound, "Source file disappeared.");
            }
            var source = await ReadTextAndHashAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(source.Hash, item.SourceHash))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "Source file changed after preview.");
            }
            var extension = Path.GetExtension(sourcePath);
            var format = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? "md"
                : "txt";
            var folder = Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/');
            var title = Path.GetFileNameWithoutExtension(sourcePath);
            var documentId = Guid.NewGuid();
            await DocumentWriter.CreateInTransactionAsync(
                connection,
                transaction,
                sessionId,
                integrityKey,
                documentId,
                title,
                folder,
                format,
                source.Content,
                null,
                new VaultAuditContext(actor),
                reconcileLinks: false,
                cancellationToken).ConfigureAwait(false);
            var (virtualPath, _) = VirtualPathPolicy.BuildDocumentPath(
                title,
                folder,
                format);
            var resolutionKeys = DocumentLinkIndexer.ResolutionKeys(title, virtualPath);
            await UpdateImportItemAsync(
                connection,
                transaction,
                jobId,
                item.RelativePath,
                "imported",
                documentId,
                "{}",
                cancellationToken).ConfigureAwait(false);
            await ExecuteSavepointCommandAsync(
                connection,
                transaction,
                $"RELEASE SAVEPOINT {ImportItemSavepoint};",
                cancellationToken).ConfigureAwait(false);
            return new ImportItemResult(
                ImportItemOutcome.Imported,
                resolutionKeys);
        }
        catch (VaultException exception)
        {
            await RollbackImportItemAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            var state = exception.Code == VaultErrorCode.Conflict ? "skipped" : "failed";
            await UpdateImportItemAsync(
                connection,
                transaction,
                jobId,
                item.RelativePath,
                state,
                null,
                JsonSerializer.Serialize(new { error = exception.Message }),
                cancellationToken).ConfigureAwait(false);
            return new ImportItemResult(
                state == "skipped"
                    ? ImportItemOutcome.Skipped
                    : ImportItemOutcome.Failed,
                []);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await RollbackImportItemAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);
            await UpdateImportItemAsync(
                connection,
                transaction,
                jobId,
                item.RelativePath,
                "failed",
                null,
                JsonSerializer.Serialize(new { error = exception.Message }),
                cancellationToken).ConfigureAwait(false);
            return new ImportItemResult(ImportItemOutcome.Failed, []);
        }
    }

    private static async Task RollbackImportItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteSavepointCommandAsync(
            connection,
            transaction,
            $"ROLLBACK TO SAVEPOINT {ImportItemSavepoint};",
            cancellationToken).ConfigureAwait(false);
        await ExecuteSavepointCommandAsync(
            connection,
            transaction,
            $"RELEASE SAVEPOINT {ImportItemSavepoint};",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteSavepointCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateImportItemAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid jobId,
        string relativePath,
        string state,
        Guid? documentId,
        string errorJson,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE import_items
            SET state = $state, document_id = $documentId, error_json = $error
            WHERE job_id = $jobId AND source_path_norm = $path;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$documentId", documentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", errorJson);
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$path", relativePath.ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveSourcePath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(
            Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException(VaultErrorCode.InvalidInput, "Import path escapes the source root.");
        }
        return fullPath;
    }

    private static bool IsUnderPath(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Any(segment =>
            segment is ".git" or ".obsidian" or ".svn" or "node_modules");
    }

    private static async Task<byte[]> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string Content, byte[] Hash, long SizeBytes)> ReadTextAndHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > DocumentWriter.MaxContentBytes)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Document content exceeds 256 KB.");
        }
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false),
            SHA256.HashData(bytes),
            bytes.LongLength);
    }

    private static async Task<(byte[] Hash, long SizeBytes)> ReadMetadataAndHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (hash, stream.Length);
    }

    private static string? ValidateImportCandidate(
        string title,
        string? folder,
        string format,
        string path)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            return "invalid_title";
        }
        try
        {
            VirtualPathPolicy.BuildDocumentPath(title, folder, format);
        }
        catch (VaultException)
        {
            return "invalid_path";
        }

        return GetFileLength(path) > DocumentWriter.MaxContentBytes
            ? "content_too_large"
            : null;
    }

    private static long GetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void CleanupStaleExportDirectories(string parent, string destinationName)
    {
        var cutoff = DateTime.UtcNow - StaleExportDirectoryAge;
        var pattern = $".{destinationName}.export-*";
        foreach (var directory in Directory.EnumerateDirectories(
            parent,
            pattern,
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) >= cutoff)
                {
                    continue;
                }
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A stale directory is best-effort cleanup and must not block a new export.
            }
        }
    }

    private sealed record ImportCandidate(
        string RelativePath,
        string Format,
        byte[] SourceHash,
        long SizeBytes,
        bool Conflict);

    private sealed record ImportJob(
        string SourceRoot,
        string State,
        int Processed,
        int Skipped)
    {
        public int Failed { get; init; }
    }

    private sealed record ImportItem(string RelativePath, byte[] SourceHash);

    private sealed record ImportItemResult(
        ImportItemOutcome Outcome,
        IReadOnlyList<string> ResolutionKeys);

    private enum ImportItemOutcome
    {
        Imported,
        Skipped,
        Failed
    }

    private sealed record ExportedFile(
        string RelativePath,
        long SizeBytes,
        string Sha256);

    private sealed record ExportManifest(
        string Format,
        int FormatVersion,
        DateTimeOffset CreatedAt,
        IReadOnlyList<ExportedFile> Files);
}
