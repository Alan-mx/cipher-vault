using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Application.Exports;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;

namespace CipherVault.Application.Tests;

public sealed class PlaintextExportServiceTests
{
    [Fact]
    public async Task ExportsActiveDocumentsManifestAndSuccessAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var markdown = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Alpha",
                "folder/Alpha.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [mcp-test]\n---\n# Alpha\n正文",
                ["mcp-test"]),
            actor);
        var text = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Plain",
                "Plain.txt",
                null,
                VaultDocumentFormat.Text,
                "TXT 正文",
                null),
            actor);
        var deleted = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Deleted",
                "Deleted.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Deleted",
                null),
            actor);
        _ = await fixture.Documents.DeleteAsync(
            deleted.DocumentId,
            1,
            actor);

        var service = new PlaintextExportService(
            fixture.Manager,
            fixture.TimeProvider);
        using var destination = new MemoryStream();
        PlaintextExportMetadata? prepared = null;
        var result = await service.WriteAsync(
            destination,
            metadata => prepared = metadata,
            actor);

        Assert.NotNull(prepared);
        Assert.Equal(2, result.DocumentCount);
        Assert.True(result.TotalContentBytes > 0);
        Assert.True(result.ZipBytes > 0);
        Assert.Matches(
            "^cipher-vault-export-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\\.zip$",
            prepared!.FileName);

        destination.Position = 0;
        using var archive = new ZipArchive(
            destination,
            ZipArchiveMode.Read,
            leaveOpen: true);
        Assert.Equal(
            [
                "Plain.txt",
                "folder/Alpha.md",
                PlaintextExportLimits.ManifestFileName
            ],
            archive.Entries.Select(entry => entry.FullName));
        Assert.Equal(
            "TXT 正文",
            ReadEntry(archive, "Plain.txt"));
        Assert.Equal(
            "---\ntags: [mcp-test]\n---\n# Alpha\n正文",
            ReadEntry(archive, "folder/Alpha.md"));

        var manifestText = ReadEntry(
            archive,
            PlaintextExportLimits.ManifestFileName);
        Assert.Contains(
            "\"exportedAtUtc\":\"2026-10-31T00:00:00",
            manifestText,
            StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(manifestText);
        Assert.Equal(
            "cipher-vault-plaintext-export",
            manifest.RootElement.GetProperty("format").GetString());
        Assert.EndsWith(
            "Z",
            manifest.RootElement.GetProperty("exportedAtUtc").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            manifest.RootElement.GetProperty("documentCount").GetInt32());
        var documents = manifest.RootElement
            .GetProperty("documents")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, documents.Length);
        Assert.False(documents[0].TryGetProperty("documentId", out _));
        Assert.Equal(
            Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes("TXT 正文")))
                .ToLowerInvariant(),
            documents[0].GetProperty("sha256").GetString());
        Assert.False(documents[1].TryGetProperty("documentId", out _));
        Assert.Equal(
            "md",
            documents[1].GetProperty("format").GetString());

        var audit = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT result, details_json
                    FROM audit_events
                    WHERE action = 'export.plaintext'
                    ORDER BY occurred_at DESC
                    LIMIT 1;
                    """;
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    return string.Empty;
                }
                return $"{reader.GetString(0)}:{reader.GetString(1)}";
            });
        Assert.StartsWith("success:", audit, StringComparison.Ordinal);
        Assert.Contains(
            $"\"documentCount\":2",
            audit,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyVaultProducesManifestOnlyZip()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var service = new PlaintextExportService(
            fixture.Manager,
            fixture.TimeProvider);
        using var destination = new MemoryStream();

        var result = await service.WriteAsync(
            destination,
            _ => { },
            new VaultActorContext(VaultActorKind.UserUi));

        Assert.Equal(0, result.DocumentCount);
        Assert.Equal(0, result.TotalContentBytes);
        destination.Position = 0;
        using var archive = new ZipArchive(
            destination,
            ZipArchiveMode.Read,
            leaveOpen: true);
        Assert.Single(archive.Entries);
        using var manifest = JsonDocument.Parse(
            ReadEntry(
                archive,
                PlaintextExportLimits.ManifestFileName));
        Assert.Equal(
            0,
            manifest.RootElement.GetProperty("documentCount").GetInt32());
        Assert.Empty(
            manifest.RootElement.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task StreamCancellationRecordsCancelledAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Cancel",
                null,
                null,
                VaultDocumentFormat.Text,
                "cancel body",
                null),
            actor);
        var service = new PlaintextExportService(
            fixture.Manager,
            fixture.TimeProvider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.WriteAsync(
                new CancellingStream(),
                _ => { },
                actor));

        var auditResult = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT result
                    FROM audit_events
                    WHERE action = 'export.plaintext'
                    ORDER BY occurred_at DESC
                    LIMIT 1;
                    """;
                return Convert.ToString(
                    await command.Command.ExecuteScalarAsync());
            });
        Assert.Equal("cancelled", auditResult);
    }

    [Fact]
    public async Task RejectsMoreThanDocumentLimitBeforeWritingZip()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    WITH digits(n) AS (
                        VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)
                    ),
                    numbers(n) AS (
                        SELECT 1 + d0.n + d1.n * 10 + d2.n * 100 + d3.n * 1000
                        FROM digits AS d0
                        CROSS JOIN digits AS d1
                        CROSS JOIN digits AS d2
                        CROSS JOIN digits AS d3
                        WHERE 1 + d0.n + d1.n * 10 + d2.n * 100 + d3.n * 1000
                            <= 10001
                        UNION ALL
                        SELECT 10001
                    )
                    INSERT INTO documents(
                        document_id, title, title_key, path, path_key, format,
                        content, frontmatter_json, headings_json, revision,
                        created_at, updated_at)
                    SELECT
                        printf(
                            '%s-%s-4%s-a%s-%s',
                            substr(h, 1, 8),
                            substr(h, 9, 4),
                            substr(h, 14, 3),
                            substr(h, 18, 3),
                            substr(h, 21, 12)),
                        'Doc ' || n,
                        upper('Doc ' || n),
                        'Doc ' || n || '.md',
                        'Doc ' || n || '.md',
                        'md',
                        'x',
                        '{}',
                        '[]',
                        1,
                        '2026-11-02T00:00:00Z',
                        '2026-11-02T00:00:00Z'
                    FROM (
                        SELECT n, lower(hex(randomblob(16))) AS h
                        FROM numbers
                    );
                    """;
                return await command.Command.ExecuteNonQueryAsync();
            });
        var service = new PlaintextExportService(
            fixture.Manager,
            fixture.TimeProvider);
        using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => service.WriteAsync(
                destination,
                _ => { },
                new VaultActorContext(VaultActorKind.UserUi)));

        Assert.Equal(VaultErrorCode.ExportTooLarge, exception.Code);
        Assert.Equal(0, destination.Length);
    }

    private static string ReadEntry(
        ZipArchive archive,
        string name)
    {
        var entry = archive.GetEntry(name)
            ?? throw new InvalidOperationException($"Missing entry: {name}");
        using var reader = new StreamReader(
            entry.Open(),
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private sealed class CancellingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new OperationCanceledException();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromException(new OperationCanceledException());

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new OperationCanceledException());
    }
}
