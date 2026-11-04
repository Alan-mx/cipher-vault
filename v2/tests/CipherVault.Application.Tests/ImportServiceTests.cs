using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Application.Import;

namespace CipherVault.Application.Tests;

public sealed class ImportServiceTests
{
    [Fact]
    public async Task PreviewClassifiesFrontmatterAndGeneratedIdentity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"identity-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        var frontmatterId = Guid.NewGuid();
        WriteFile(
            sourceRoot,
            "frontmatter.md",
            $"---\nid: {frontmatterId:D}\n---\nBody");
        WriteFile(sourceRoot, "generated.txt", "plain");
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        Assert.Null(preview.ManifestVersion);
        var frontmatter = Assert.Single(
            preview.Items,
            item => item.RelativePath == "frontmatter.md");
        Assert.Equal(frontmatterId, frontmatter.DocumentId);
        Assert.Equal(ImportIdentitySource.Frontmatter, frontmatter.IdentitySource);
        var generated = Assert.Single(
            preview.Items,
            item => item.RelativePath == "generated.txt");
        Assert.NotNull(generated.DocumentId);
        Assert.Equal(ImportIdentitySource.Generated, generated.IdentitySource);
        Assert.NotNull(generated.ContentHash);
        _ = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);
        Assert.Equal(
            frontmatterId,
            (await fixture.Documents.GetAsync(frontmatterId)).DocumentId);
        Assert.Equal(
            generated.DocumentId,
            (await fixture.Documents.GetAsync(generated.DocumentId!.Value)).DocumentId);
    }

    [Fact]
    public async Task PreviewUsesManifestForMarkdownAndTextIdentity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"manifest-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        var markdownId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        const string markdownContent =
            "---\nid: 7b29a2e1-1111-4222-8333-123456789abc\n---\nBody";
        var actualMarkdown = markdownContent.Replace(
            "7b29a2e1-1111-4222-8333-123456789abc",
            markdownId.ToString("D"),
            StringComparison.Ordinal);
        const string textContent = "plain";
        WriteFile(sourceRoot, "notes/a.md", actualMarkdown);
        WriteFile(sourceRoot, "notes/b.txt", textContent);
        await WriteManifestAsync(
            sourceRoot,
            [
                (markdownId, "notes/a.md", "Manifest title", "md", actualMarkdown),
                (textId, "notes/b.txt", "Text title", "txt", textContent)
            ]);
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        Assert.Equal(2, preview.ManifestVersion);
        Assert.NotNull(preview.ManifestSha256);
        var markdown = Assert.Single(
            preview.Items,
            item => item.RelativePath == "notes/a.md");
        Assert.Equal(markdownId, markdown.DocumentId);
        Assert.Equal(ImportIdentitySource.Frontmatter, markdown.IdentitySource);
        Assert.Equal("Manifest title", markdown.Title);
        var text = Assert.Single(
            preview.Items,
            item => item.RelativePath == "notes/b.txt");
        Assert.Equal(textId, text.DocumentId);
        Assert.Equal(ImportIdentitySource.Manifest, text.IdentitySource);
        Assert.Equal("Text title", text.Title);

        _ = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);
        Assert.Equal(
            "Manifest title",
            (await fixture.Documents.GetAsync(markdownId)).Title);
        Assert.Equal(
            "Text title",
            (await fixture.Documents.GetAsync(textId)).Title);
    }

    [Fact]
    public async Task PreviewRejectsManifestMarkdownWithoutMatchingId()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"manifest-missing-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        const string content = "Body";
        WriteFile(sourceRoot, "note.md", content);
        await WriteManifestAsync(
            sourceRoot,
            [(Guid.NewGuid(), "note.md", "Note", "md", content)]);
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewItemState.Rejected, item.State);
        Assert.Equal("invalid_manifest", item.Reason);
    }

    [Fact]
    public async Task PreviewRejectsManifestAndFrontmatterIdMismatch()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"manifest-id-mismatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        var frontmatterId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var content = $"---\nid: {frontmatterId:D}\n---\nBody";
        WriteFile(sourceRoot, "note.md", content);
        await WriteManifestAsync(
            sourceRoot,
            [(manifestId, "note.md", "Note", "md", content)]);
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewItemState.Rejected, item.State);
        Assert.Equal("document_id_mismatch", item.Reason);
    }

    [Fact]
    public async Task PreviewRejectsAllDuplicateIdsInBatch()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"duplicate-id-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        var id = Guid.NewGuid();
        WriteFile(
            sourceRoot,
            "a.md",
            $"---\nid: {id:D}\n---\nA");
        WriteFile(
            sourceRoot,
            "b.md",
            $"---\nid: {id:D}\n---\nB");
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        Assert.All(
            preview.Items,
            item =>
            {
                Assert.Equal(ImportPreviewItemState.Rejected, item.State);
                Assert.Equal("duplicate_document_id_in_import", item.Reason);
            });
    }

    [Fact]
    public async Task PreviewRejectsInvalidMarkdownIdentityPerFile()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"invalid-id-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        WriteFile(
            sourceRoot,
            "invalid.md",
            "---\nid: not-a-guid\n---\nBody");
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        var item = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewItemState.Rejected, item.State);
        Assert.Equal("invalid_document_id", item.Reason);
    }

    [Fact]
    public async Task PreviewClassifiesExistingAndTrashedIdConflicts()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Existing",
                "notes/existing.md",
                null,
                VaultDocumentFormat.Markdown,
                "Body",
                null),
            actor);
        var existingContent = (
            await fixture.Documents.ReadContentAsync(
                created.DocumentId,
                0,
                500)).Content;
        var sameRoot = Path.Combine(
            fixture.Parent,
            $"same-id-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(sameRoot, "notes"));
        WriteFile(sameRoot, "notes/existing.md", existingContent);

        var same = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sameRoot),
            actor);

        var sameItem = Assert.Single(same.Items);
        Assert.Equal(ImportPreviewItemState.SkippedConflict, sameItem.State);
        Assert.Equal("duplicate_document_id", sameItem.Reason);

        var differentRoot = Path.Combine(
            fixture.Parent,
            $"different-path-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(differentRoot);
        WriteFile(differentRoot, "other.md", existingContent);
        var different = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(differentRoot),
            actor);
        var differentItem = Assert.Single(different.Items);
        Assert.Equal(ImportPreviewItemState.Rejected, differentItem.State);
        Assert.Equal("document_id_conflict", differentItem.Reason);

        _ = await fixture.Documents.DeleteAsync(
            created.DocumentId,
            1,
            actor);
        var trashed = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sameRoot),
            actor);
        var trashedItem = Assert.Single(trashed.Items);
        Assert.Equal(ImportPreviewItemState.Rejected, trashedItem.State);
        Assert.Equal("trashed_document_id_conflict", trashedItem.Reason);
    }

    [Fact]
    public async Task CommitRejectsManifestChangedAfterPreview()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"changed-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        WriteFile(sourceRoot, "note.txt", "body");
        await WriteManifestAsync(
            sourceRoot,
            [(Guid.NewGuid(), "note.txt", "Note", "txt", "body")]);
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);
        await WriteManifestAsync(
            sourceRoot,
            [(Guid.NewGuid(), "note.txt", "Changed", "txt", "body")]);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor));

        Assert.Equal(VaultErrorCode.InvalidManifest, exception.Code);
    }

    [Fact]
    public async Task CommitRejectsIdentityOccupiedAfterPreview()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            fixture.Parent,
            $"id-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        var id = Guid.NewGuid();
        WriteFile(
            sourceRoot,
            "note.md",
            $"---\nid: {id:D}\n---\nBody");
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);
        await fixture.QueryAsync(
            async context =>
                await CipherVault.Core.Storage.Documents.DocumentMutationWriter
                    .CreateTrustedAsync(
                        context,
                        id,
                        new CreateDocumentRequest(
                            "Occupied",
                            "occupied.md",
                            null,
                            VaultDocumentFormat.Markdown,
                            "Other body",
                            null),
                        actor,
                        fixture.TimeProvider.GetUtcNow(),
                        DocumentContentLimitMode.StandardWrite,
                        CancellationToken.None));

        var result = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);

        var item = Assert.Single(result.Items);
        Assert.Equal("failed", item.Result);
        Assert.Equal("DocumentIdConflict", item.ErrorCode);
    }

    [Theory]
    [InlineData("a/Source.md", "z/Target.md", "../z/Target.md")]
    [InlineData("z/Source.md", "a/Target.md", "../a/Target.md")]
    public async Task MarkdownLinksResolveRegardlessOfImportOrder(
        string sourcePath,
        string targetPath,
        string markdownTarget)
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-import-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            WriteFile(
                sourceRoot,
                sourcePath,
                $"[Target]({markdownTarget})");
            WriteFile(sourceRoot, targetPath, "# Target");
            var actor = new VaultActorContext(VaultActorKind.UserUi);
            var preview = await fixture.Import.PreviewAsync(
                new ImportPreviewRequest(sourceRoot),
                actor);
            _ = await fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor);
            var catalog = await fixture.Documents.ListAsync(
                new CatalogQuery(
                    null,
                    VaultDocumentFormat.Markdown,
                    new CursorPageRequest(100, null)));
            var source = Assert.Single(
                catalog.Items,
                item => item.Title == "Source");
            var map = await fixture.Documents.GetMapAsync(source.DocumentId);
            var link = Assert.Single(map.OutgoingLinks);
            Assert.Equal(DocumentLinkKind.Markdown, link.LinkKind);
            Assert.Equal("resolved", link.ResolutionStatus);
            Assert.Equal("Target", link.Target?.Title);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewAndCommitImportUtf8FilesRecursively()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Notes"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Notes", "Alpha.md"),
            "---\ntags: [Work]\n---\n# Alpha\nSee [[Beta]].",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Beta.txt"),
            "Beta body");
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "ignored.json"),
            "{}");

        var actor = new VaultActorContext(VaultActorKind.UserUi, "import-1");
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        Assert.Equal(2, preview.ReadyCount);
        Assert.Equal(0, preview.SkippedCount);
        Assert.Equal(0, preview.RejectedCount);
        Assert.Equal(2, preview.Items.Count);
        Assert.All(preview.Items, item =>
            Assert.Equal(ImportPreviewItemState.Ready, item.State));

        var committed = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);

        Assert.Equal(2, committed.Imported);
        Assert.Equal(0, committed.Skipped);
        Assert.Equal(0, committed.Failed);
        Assert.NotNull(committed.AuditEventId);
        var catalog = await fixture.Documents.ListAsync(
            new CatalogQuery(null, null, new CursorPageRequest()));
        Assert.Equal(2, catalog.Items.Count);
        Assert.Contains(catalog.Items, item => item.VirtualPath == "Beta.txt");
        Assert.Contains(catalog.Items, item => item.VirtualPath == "Notes/Alpha.md");

        var audit = await fixture.Audit.ListAsync(
            new AuditQuery(
                null,
                null,
                null,
                null,
                null,
                new CursorPageRequest(200)));
        Assert.Contains(audit.Items, item => item.Action == "import_start");
        Assert.Contains(audit.Items, item => item.Action == "import_complete");
        Assert.All(
            audit.Items.Where(item =>
                item.Action is "import_start" or "import_complete"),
            item => Assert.DoesNotContain(
                sourceRoot,
                item.DetailsJson,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreviewSkipsActivePathAndRejectsInvalidFiles()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        _ = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Existing",
                "Notes/Existing.md",
                null,
                VaultDocumentFormat.Markdown,
                "original",
                null),
            actor);

        var sourceRoot = Path.Combine(fixture.Parent, "mixed-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Notes"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Notes", "Existing.md"),
            "replacement");
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "invalid.md"),
            [0xFF, 0xFE, 0xFD]);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "too-large.txt"),
            new byte[(256 * 1024) + 4]);

        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);
        Assert.Equal(0, preview.ReadyCount);
        Assert.Equal(1, preview.SkippedCount);
        Assert.Equal(2, preview.RejectedCount);
        Assert.Contains(
            preview.Items,
            item => item.RelativePath == "invalid.md"
                && item.Reason == "invalid_utf8");
        Assert.Contains(
            preview.Items,
            item => item.RelativePath == "too-large.txt"
                && item.Reason == "document_too_large");

        var committed = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);
        Assert.Equal(0, committed.Imported);
        Assert.Equal(1, committed.Skipped);
        Assert.Equal(2, committed.Failed);
        var existing = await fixture.Documents.GetAsync(
            (await fixture.Documents.ListAsync(
                new CatalogQuery(
                    "Notes",
                    null,
                    new CursorPageRequest()))).Items.Single().DocumentId);
        var content = await fixture.Documents.ReadContentAsync(
            existing.DocumentId,
            offset: 0);
        Assert.Contains(
            $"id: {existing.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
        Assert.EndsWith("original", content.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitConsumesTokenAndRejectsChangedSource()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "changed-source");
        Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "Changed.md");
        await File.WriteAllTextAsync(sourcePath, "first");
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        await File.WriteAllTextAsync(sourcePath, "other");
        var committed = await fixture.Import.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);
        Assert.Equal(0, committed.Imported);
        Assert.Single(committed.Items);
        Assert.Equal("failed", committed.Items[0].Result);
        Assert.Equal("ImportSourceChanged", committed.Items[0].ErrorCode);

        var replay = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor));
        Assert.Equal(VaultErrorCode.ImportPreviewInvalid, replay.Code);
    }

    [Fact]
    public async Task PreviewRequiresUserUiAndExpiredTokenCannotCommit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "expiry-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Expiry.md"),
            "body");

        var forbidden = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.PreviewAsync(
                new ImportPreviewRequest(sourceRoot),
                new VaultActorContext(VaultActorKind.Mcp)));
        Assert.Equal(VaultErrorCode.Forbidden, forbidden.Code);

        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);
        ((ManualTimeProvider)fixture.TimeProvider).Advance(TimeSpan.FromMinutes(10));

        var expired = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor));
        Assert.Equal(VaultErrorCode.ImportPreviewInvalid, expired.Code);
    }

    [Fact]
    public async Task CommitRejectsTokenAfterVaultSessionChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "session-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Session.md"),
            "body");
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        await fixture.RelockAsync();
        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor));

        Assert.Equal(VaultErrorCode.ImportPreviewInvalid, exception.Code);
    }

    [Fact]
    public async Task CommitRejectsTokenAfterSourceRootMoves()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "bound-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Bound.md"),
            "body");
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var preview = await fixture.Import.PreviewAsync(
            new ImportPreviewRequest(sourceRoot),
            actor);

        Directory.Move(
            sourceRoot,
            Path.Combine(fixture.Parent, "moved-source"));
        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.CommitAsync(
                new ImportCommitRequest(preview.Token),
                actor));

        Assert.Equal(VaultErrorCode.ImportSourceInvalid, exception.Code);
    }

    [Fact]
    public async Task PreviewStoreRejectsSeventeenthPendingToken()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var sourceRoot = Path.Combine(fixture.Parent, "capacity-source");
        Directory.CreateDirectory(sourceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "Capacity.md"),
            "body");
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        for (var index = 0; index < 16; index++)
        {
            _ = await fixture.Import.PreviewAsync(
                new ImportPreviewRequest(sourceRoot),
                actor);
        }

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Import.PreviewAsync(
                new ImportPreviewRequest(sourceRoot),
                actor));
        Assert.Equal(VaultErrorCode.ImportPreviewInvalid, exception.Code);
    }

    private static void WriteFile(
        string sourceRoot,
        string relativePath,
        string content)
    {
        var path = Path.Combine(
            sourceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static Task WriteManifestAsync(
        string sourceRoot,
        IReadOnlyList<(
            Guid DocumentId,
            string Path,
            string Title,
            string Format,
            string Content)> documents)
    {
        var entries = documents.Select(document =>
        {
            var bytes = Encoding.UTF8.GetBytes(document.Content);
            return new
            {
                documentId = document.DocumentId.ToString("D"),
                virtualPath = document.Path,
                title = document.Title,
                format = document.Format,
                revision = 1,
                bytes = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                    .ToLowerInvariant()
            };
        }).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            format = "cipher-vault-plaintext-export",
            formatVersion = 2,
            exportedAtUtc = "2026-11-04T00:00:00Z",
            documentCount = entries.Length,
            totalContentBytes = entries.Sum(entry => entry.bytes),
            documents = entries
        });
        return File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "_cipher-vault-export.json"),
            json,
            new UTF8Encoding(false));
    }
}
