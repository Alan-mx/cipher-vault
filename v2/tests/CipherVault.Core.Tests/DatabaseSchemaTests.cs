using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using Microsoft.Data.Sqlite;

namespace CipherVault.Core.Tests;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task InitializeCreatesSchemaAndVerifiesFts5()
    {
        using var fixture = await Fixture.CreateAsync();

        var initialized = await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);
        var verified = await SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material);

        Assert.Equal(fixture.Material.VaultId, initialized.VaultId);
        Assert.Equal(9, initialized.SchemaVersion);
        Assert.Equal(initialized, verified);
    }

    [Fact]
    public async Task InitializationIsIdempotent()
    {
        using var fixture = await Fixture.CreateAsync();

        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);
        var second = await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using var connection = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);
        using var command = connection.Connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schema_migrations;";
        Assert.Equal(9, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Equal(9, second.SchemaVersion);
    }

    [Fact]
    public async Task HigherSchemaVersionIsRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using (var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false))
        {
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                """
                UPDATE vault_meta SET value = '99' WHERE key = 'schema_version';
                INSERT INTO schema_migrations(version, name, checksum_sha256, applied_at)
                VALUES (99, 'future', lower(hex(randomblob(32))), '2000-01-01T00:00:00Z');
                """,
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material));
        Assert.Equal(VaultErrorCode.UnsupportedVersion, exception.Code);
    }

    [Fact]
    public async Task SchemaVersionMustMatchMigrationHistory()
    {
        using var fixture = await Fixture.CreateAsync();
        await SqliteMcVaultStore.InitializeAsync(fixture.Root, fixture.Material);

        await using (var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false))
        {
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                "UPDATE vault_meta SET value = '0' WHERE key = 'schema_version';",
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            SqliteMcVaultStore.VerifyAsync(fixture.Root, fixture.Material));
        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public async Task InconsistentSchemaVersionIsRejectedBeforePendingMigration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-inconsistent-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            var migrations = DatabaseMigrationCatalog.Load();
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                "UPDATE vault_meta SET value = '10' WHERE key = 'schema_version';",
                CancellationToken.None);

            var migration10 = new DatabaseMigration(
                10,
                "must_not_apply",
                "CREATE TABLE must_not_apply(id INTEGER PRIMARY KEY) STRICT;",
                new string('a', 64));
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    [.. migrations, migration10],
                    CancellationToken.None));

            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM schema_migrations WHERE version = 10;
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
            command.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name = 'must_not_apply';
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MetadataWithoutMigrationHistoryIsRejected()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-missing-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                """
                CREATE TABLE vault_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                ) STRICT;
                INSERT INTO vault_meta(key, value)
                VALUES ('schema_version', '5');
                """,
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    DatabaseMigrationCatalog.Load(),
                    CancellationToken.None));

            Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name = 'schema_migrations';
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LocalVaultSchemasUpgradeToCurrentWithoutBackupBaseline(
        int initialVersion)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-local-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            var migrations = DatabaseMigrationCatalog.Load();
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations.Take(initialVersion).ToArray(),
                CancellationToken.None);

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(9, upgraded.SchemaVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedMigrationRollsBackAllSchemaChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using var session = await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                root,
                material,
                create: true);
            var badMigration = new DatabaseMigration(
                1,
                "bad",
                "CREATE TABLE vault_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO missing_table VALUES (1);",
                "0".PadLeft(64, '0'));

            await Assert.ThrowsAnyAsync<SqliteException>(() =>
                DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    [badMigration],
                    CancellationToken.None));

            using var command = session.Connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name IN ('vault_meta', 'schema_migrations');
                """;
            Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PendingMigrationIsAppliedAndVersionAdvances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        var migrations = DatabaseMigrationCatalog.Load();
        var initial = migrations.Take(1).ToArray();

        try
        {
            await using var session = await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                root,
                material,
                create: true);
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                initial,
                CancellationToken.None);

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(9, upgraded.SchemaVersion);
            using var command = session.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT count(*)
                FROM sqlite_master
                WHERE name IN (
                    'documents',
                    'document_revisions',
                    'tags',
                    'document_tags',
                    'document_links',
                    'document_fts',
                    'audit_events',
                    'document_drafts',
                    'idempotency_records',
                    'document_relation_suggestions',
                    'document_relations'
                );
                """;
            Assert.Equal(11, Convert.ToInt32(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT count(*) FROM schema_migrations;";
            Assert.Equal(9, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationFiveToSixPreservesDataAndFailedSevenRollsBack()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-five-to-six-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var migrations = DatabaseMigrationCatalog.Load();
        var documentId = Guid.NewGuid().ToString("D");

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations.Take(5).ToArray(),
                CancellationToken.None);
            using (var versionFive = session.Connection.CreateCommand())
            {
                versionFive.CommandText =
                    "SELECT value FROM vault_meta WHERE key = 'schema_version';";
                Assert.Equal(
                    "5",
                    Convert.ToString(await versionFive.ExecuteScalarAsync()));
            }
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                $"""
                INSERT INTO documents(
                    document_id, title, title_key, path, path_key, format,
                    content, frontmatter_json, headings_json, revision,
                    created_at, updated_at)
                VALUES(
                    '{documentId}',
                    'Migration Probe',
                    'migration probe',
                    '/migration-probe.md',
                    '/migration-probe.md',
                    'md',
                    '# Migration Probe',
                    json_object(),
                    '[]',
                    1,
                    '2026-11-03T00:00:00Z',
                    '2026-11-03T00:00:00Z');
                """,
                CancellationToken.None);

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);
            Assert.Equal(9, upgraded.SchemaVersion);

            var failedMigration10 = new DatabaseMigration(
                10,
                "future_migration_failure",
                "CREATE TABLE future_migration_rollback_probe(id INTEGER); INSERT INTO missing_table VALUES(1);",
                new string('b', 64));
            await Assert.ThrowsAnyAsync<SqliteException>(() =>
                DatabaseInitializer.EnsureSchemaAsync(
                    session.Connection,
                    material.VaultId,
                    DateTimeOffset.UtcNow,
                    [.. migrations, failedMigration10],
                    CancellationToken.None));

            using (var verification = session.Connection.CreateCommand())
            {
                verification.CommandText = """
                    SELECT value FROM vault_meta WHERE key = 'schema_version';
                    """;
                Assert.Equal(
                    "9",
                    Convert.ToString(await verification.ExecuteScalarAsync()));
                verification.CommandText =
                    "SELECT count(*) FROM schema_migrations WHERE version = 10;";
                Assert.Equal(0, Convert.ToInt32(await verification.ExecuteScalarAsync()));
                verification.CommandText = """
                    SELECT count(*) FROM sqlite_master
                    WHERE name = 'future_migration_rollback_probe';
                    """;
                Assert.Equal(0, Convert.ToInt32(await verification.ExecuteScalarAsync()));
                verification.CommandText =
                    $"SELECT count(*) FROM documents WHERE document_id = '{documentId}';";
                Assert.Equal(1, Convert.ToInt32(await verification.ExecuteScalarAsync()));
            }

            using var draftTableCheck = session.Connection.CreateCommand();
            draftTableCheck.CommandText = """
                SELECT count(*) FROM sqlite_master
                WHERE name IN ('document_drafts', 'idempotency_records');
                """;
            Assert.Equal(
                2,
                Convert.ToInt32(await draftTableCheck.ExecuteScalarAsync()));

            using var finalCheck = session.Connection.CreateCommand();
            finalCheck.CommandText =
                $"SELECT count(*) FROM documents WHERE document_id = '{documentId}';";
            Assert.Equal(1, Convert.ToInt32(await finalCheck.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationSixToSevenPreservesWikiLinks()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-six-to-seven-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var sourceId = Guid.NewGuid().ToString("D");
        var targetId = Guid.NewGuid().ToString("D");
        var linkId = Guid.NewGuid().ToString("D");
        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            var migrations = DatabaseMigrationCatalog.Load();
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations.Take(6).ToArray(),
                CancellationToken.None);
            await SqliteMcDatabaseFactory.ExecuteNonQueryAsync(
                session.Connection,
                $"""
                INSERT INTO documents(
                    document_id, title, title_key, path, path_key, format,
                    content, frontmatter_json, headings_json, revision,
                    created_at, updated_at)
                VALUES
                    ('{sourceId}', 'Source', 'source', 'source.md', 'source.md',
                     'md', '[[Target]]', json_object(), '[]', 1,
                     '2026-11-04T00:00:00Z', '2026-11-04T00:00:00Z'),
                    ('{targetId}', 'Target', 'target', 'target.md', 'target.md',
                     'md', '# Target', json_object(), '[]', 1,
                     '2026-11-04T00:00:00Z', '2026-11-04T00:00:00Z');
                INSERT INTO document_links(
                    link_id, source_document_id, source_revision,
                    target_document_id, raw_target, raw_target_key,
                    display_text, relation_type, resolution_status,
                    candidate_document_ids_json, start_offset, end_offset)
                VALUES(
                    '{linkId}', '{sourceId}', 1, '{targetId}',
                    'Target', 'TARGET', 'Target', 'references', 'resolved',
                    '[]', 0, 10);
                """,
                CancellationToken.None);

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(9, upgraded.SchemaVersion);
            using var command = session.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT link_kind, canonical_target_key,
                       destination_start_offset, destination_end_offset,
                       resolution_status, target_document_id, raw_target_key
                FROM document_links
                WHERE link_id = $linkId;
                """;
            command.Parameters.AddWithValue("$linkId", linkId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("wiki", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.Equal("resolved", reader.GetString(4));
            Assert.Equal(targetId, reader.GetString(5));
            Assert.Equal("TARGET", reader.GetString(6));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuditMigrationsPreserveRowsIndexesAndAppendOnlyProtection()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-audit-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        VaultFilePermissions.ProtectDirectory(root);
        using var material = new VaultKeyMaterial(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var historicalId = Guid.NewGuid().ToString("D");

        try
        {
            await using var session =
                await SqliteMcDatabaseFactory.OpenUninitializedSessionAsync(
                    root,
                    material,
                    create: true);
            var migrations = DatabaseMigrationCatalog.Load();
            _ = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations.Take(2).ToArray(),
                CancellationToken.None);
            using (var insertHistorical = session.Connection.CreateCommand())
            {
                insertHistorical.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES(
                        $eventId, '2026-11-01T00:00:00Z', 'system', 'link_rebuild',
                        NULL, NULL, NULL, 'success', '{"kept":true}');
                    """;
                insertHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                await insertHistorical.ExecuteNonQueryAsync();
            }

            var upgraded = await DatabaseInitializer.EnsureSchemaAsync(
                session.Connection,
                material.VaultId,
                DateTimeOffset.UtcNow,
                migrations,
                CancellationToken.None);

            Assert.Equal(9, upgraded.SchemaVersion);
            using (var selectHistorical = session.Connection.CreateCommand())
            {
                selectHistorical.CommandText = """
                    SELECT action, details_json
                    FROM audit_events
                    WHERE event_id = $eventId;
                    """;
                selectHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                await using var reader =
                    await selectHistorical.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("link_rebuild", reader.GetString(0));
                Assert.Equal("{\"kept\":true}", reader.GetString(1));
            }

            using (var insertAudits = session.Connection.CreateCommand())
            {
                insertAudits.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES
                        ($createId, '2026-11-01T00:00:01Z', 'user_ui',
                         'backup.create', NULL, NULL, NULL, 'success', '{}'),
                        ($downloadId, '2026-11-01T00:00:02Z', 'user_ui',
                         'backup.download', NULL, NULL, NULL, 'success', '{}'),
                        ($exportId, '2026-11-01T00:00:03Z', 'user_ui',
                         'export.plaintext', NULL, NULL, NULL, 'success', '{}'),
                        ($startedId, '2026-11-01T00:00:04Z', 'user_ui',
                         'export.plaintext', NULL, NULL, NULL, 'started', '{}');
                    """;
                insertAudits.Parameters.AddWithValue(
                    "$createId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$downloadId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$exportId",
                    Guid.NewGuid().ToString("D"));
                insertAudits.Parameters.AddWithValue(
                    "$startedId",
                    Guid.NewGuid().ToString("D"));
                await insertAudits.ExecuteNonQueryAsync();
            }

            using (var unknown = session.Connection.CreateCommand())
            {
                unknown.CommandText = """
                    INSERT INTO audit_events(
                        event_id, occurred_at, actor_kind, action,
                        document_id, revision, correlation_id, result, details_json)
                    VALUES(
                        $eventId, '2026-11-01T00:00:03Z', 'system', 'unknown_action',
                        NULL, NULL, NULL, 'success', '{}');
                    """;
                unknown.Parameters.AddWithValue(
                    "$eventId",
                    Guid.NewGuid().ToString("D"));
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => unknown.ExecuteNonQueryAsync());
                Assert.Contains(
                    "CHECK constraint failed",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var updateHistorical = session.Connection.CreateCommand())
            {
                updateHistorical.CommandText =
                    "UPDATE audit_events SET result = 'failed' WHERE event_id = $eventId;";
                updateHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => updateHistorical.ExecuteNonQueryAsync());
                Assert.Contains(
                    "append-only",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var deleteHistorical = session.Connection.CreateCommand())
            {
                deleteHistorical.CommandText =
                    "DELETE FROM audit_events WHERE event_id = $eventId;";
                deleteHistorical.Parameters.AddWithValue(
                    "$eventId",
                    historicalId);
                var exception = await Assert.ThrowsAsync<SqliteException>(
                    () => deleteHistorical.ExecuteNonQueryAsync());
                Assert.Contains(
                    "append-only",
                    exception.Message,
                    StringComparison.Ordinal);
            }

            using (var objects = session.Connection.CreateCommand())
            {
                objects.CommandText = """
                    SELECT count(*) FROM sqlite_master
                    WHERE name IN (
                        'ix_audit_occurred',
                        'ix_audit_document',
                        'ix_audit_action',
                        'ix_audit_correlation',
                        'trg_audit_no_update',
                        'trg_audit_no_delete'
                    );
                    """;
                Assert.Equal(
                    6,
                    Convert.ToInt32(await objects.ExecuteScalarAsync()));
            }

            using (var foreignKeys = session.Connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                await using var reader =
                    await foreignKeys.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync());
            }

            Assert.Equal(
                "ok",
                await SqliteMcDatabaseFactory.ExecuteScalarStringAsync(
                    session.Connection,
                    "PRAGMA integrity_check;",
                    CancellationToken.None));
        }
        finally
        {
            material.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SemanticIndexSchemaEnforcesProfileConsentJobsAndChunks()
    {
        using var fixture = await Fixture.CreateAsync();
        _ = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);
        await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);

        var documentId = Guid.NewGuid();
        var profileId = new string('a', 64);
        var endpointFingerprint = new string('b', 64);
        const string now = "2026-11-05T00:00:00Z";
        using (var command = session.Connection.CreateCommand())
        {
            command.CommandText =
                $"""
                 INSERT INTO documents(
                     document_id, title, title_key, path, path_key, format,
                     content, frontmatter_json, headings_json, revision,
                     created_at, updated_at)
                 VALUES(
                     '{documentId:D}', 'Semantic', 'semantic', '/semantic.md',
                     '/semantic.md', 'md', '# Semantic', json_object(), '[]', 1,
                     '{now}', '{now}');

                 INSERT INTO semantic_profiles(
                     profile_id, provider_id, model_id, dimension,
                     max_input_tokens, profile_version, chunker_version,
                     endpoint_fingerprint, is_remote, is_active, status,
                     last_error_code, created_at, updated_at, ready_at)
                 VALUES(
                     '{profileId}', 'openai-compatible', 'embedding-test', 3,
                     8192, 1, 1, '{endpointFingerprint}', 0, 1, 'ready',
                     NULL, '{now}', '{now}', '{now}');

                 INSERT INTO semantic_remote_consents(
                     profile_id, consent_version, endpoint_fingerprint,
                     granted_by_actor, granted_at, revoked_at, updated_at)
                 VALUES(
                     '{profileId}', 1, '{endpointFingerprint}', 'user_ui',
                     '{now}', NULL, '{now}');

                 INSERT INTO semantic_index_jobs(
                     document_id, profile_id, job_type, status,
                     requested_revision, document_content_hash, attempt_count,
                     available_at, last_error_code, created_at, updated_at,
                     completed_at)
                 VALUES(
                     '{documentId:D}', '{profileId}', 'index', 'pending', 1,
                     '{new string('c', 64)}', 0, '{now}', NULL, '{now}',
                     '{now}', NULL);
                 """;
            await command.ExecuteNonQueryAsync();
        }

        using (var chunk = session.Connection.CreateCommand())
        {
            chunk.CommandText =
                """
                INSERT INTO semantic_chunks(
                    document_id, profile_id, source_revision, chunk_ordinal,
                    heading_path_json, start_offset, end_offset,
                    chunk_content_hash, dimension, vector_blob, created_at)
                VALUES(
                    $documentId, $profileId, 1, 0, '["Semantic"]', 0, 9,
                    $hash, 3, $vector, $now);
                """;
            chunk.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            chunk.Parameters.AddWithValue("$profileId", profileId);
            chunk.Parameters.AddWithValue("$hash", new string('c', 64));
            chunk.Parameters.AddWithValue("$vector", new byte[12]);
            chunk.Parameters.AddWithValue("$now", now);
            await chunk.ExecuteNonQueryAsync();
        }

        using (var duplicateActive = session.Connection.CreateCommand())
        {
            duplicateActive.CommandText =
                $"""
                 INSERT INTO semantic_profiles(
                     profile_id, provider_id, model_id, dimension,
                     max_input_tokens, profile_version, chunker_version,
                     endpoint_fingerprint, is_remote, is_active, status,
                     created_at, updated_at, ready_at)
                 VALUES(
                     '{new string('d', 64)}', 'openai-compatible',
                     'embedding-test-2', 3, 8192, 1, 1,
                     '{new string('e', 64)}', 0, 1, 'ready',
                     '{now}', '{now}', '{now}');
                 """;
            var exception = await Assert.ThrowsAsync<SqliteException>(
                () => duplicateActive.ExecuteNonQueryAsync());
            Assert.Contains("UNIQUE", exception.Message, StringComparison.Ordinal);
        }

        using (var dimensionMismatch = session.Connection.CreateCommand())
        {
            dimensionMismatch.CommandText =
                """
                INSERT INTO semantic_chunks(
                    document_id, profile_id, source_revision, chunk_ordinal,
                    heading_path_json, start_offset, end_offset,
                    chunk_content_hash, dimension, vector_blob, created_at)
                VALUES(
                    $documentId, $profileId, 1, 1, '[]', 0, 1,
                    $hash, 4, $vector, $now);
                """;
            dimensionMismatch.Parameters.AddWithValue(
                "$documentId",
                documentId.ToString("D"));
            dimensionMismatch.Parameters.AddWithValue("$profileId", profileId);
            dimensionMismatch.Parameters.AddWithValue(
                "$hash",
                new string('f', 64));
            dimensionMismatch.Parameters.AddWithValue(
                "$vector",
                new byte[16]);
            dimensionMismatch.Parameters.AddWithValue("$now", now);
            var exception = await Assert.ThrowsAsync<SqliteException>(
                () => dimensionMismatch.ExecuteNonQueryAsync());
            Assert.Contains(
                "dimension must match",
                exception.Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SemanticAuditAndIdempotencyWhitelistsAreActive()
    {
        using var fixture = await Fixture.CreateAsync();
        _ = await SqliteMcVaultStore.InitializeAsync(
            fixture.Root,
            fixture.Material);
        await using var session = await SqliteMcDatabaseFactory.OpenSessionAsync(
            fixture.Root,
            fixture.Material,
            create: false);
        const string now = "2026-11-05T00:00:00Z";

        using (var command = session.Connection.CreateCommand())
        {
            command.CommandText =
                $"""
                 INSERT INTO idempotency_records(
                     idempotency_key, actor_kind, operation, session_id,
                     request_hash, result_json, created_at, expires_at)
                 VALUES(
                     '{Guid.NewGuid():D}', 'user_ui', 'semantic_index.rebuild',
                     NULL, '{new string('a', 64)}', json_object(), '{now}',
                     '{now}');

                 INSERT INTO audit_events(
                     event_id, occurred_at, actor_kind, action, document_id,
                     revision, correlation_id, result, details_json)
                 VALUES(
                     '{Guid.NewGuid():D}', '{now}', 'user_ui',
                     'embedding.profile_change', NULL, NULL, NULL, 'success',
                     json_object('profileId', 'probe'));
                 """;
            await command.ExecuteNonQueryAsync();
        }

        using (var invalidOperation = session.Connection.CreateCommand())
        {
            invalidOperation.CommandText =
                $"""
                 INSERT INTO idempotency_records(
                     idempotency_key, actor_kind, operation, session_id,
                     request_hash, result_json, created_at, expires_at)
                 VALUES(
                     '{Guid.NewGuid():D}', 'user_ui', 'semantic.invalid',
                     NULL, '{new string('b', 64)}', json_object(), '{now}',
                     '{now}');
                 """;
            await Assert.ThrowsAsync<SqliteException>(
                () => invalidOperation.ExecuteNonQueryAsync());
        }

        using (var invalidAction = session.Connection.CreateCommand())
        {
            invalidAction.CommandText =
                $"""
                 INSERT INTO audit_events(
                     event_id, occurred_at, actor_kind, action, document_id,
                     revision, correlation_id, result, details_json)
                 VALUES(
                     '{Guid.NewGuid():D}', '{now}', 'user_ui',
                     'embedding.invalid', NULL, NULL, NULL, 'success',
                     json_object());
                 """;
            await Assert.ThrowsAsync<SqliteException>(
                () => invalidAction.ExecuteNonQueryAsync());
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, VaultKeyMaterial material)
        {
            Root = root;
            Material = material;
        }

        public string Root { get; }

        public VaultKeyMaterial Material { get; }

        public static Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-schema-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            VaultFilePermissions.ProtectDirectory(root);
            return Task.FromResult(new Fixture(
                root,
                new VaultKeyMaterial(
                    Guid.NewGuid(),
                    RandomNumberGenerator.GetBytes(32),
                    RandomNumberGenerator.GetBytes(32))));
        }

        public void Dispose()
        {
            Material.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
