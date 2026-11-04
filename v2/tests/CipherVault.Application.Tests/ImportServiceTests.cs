using System.Text;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Application.Import;

namespace CipherVault.Application.Tests;

public sealed class ImportServiceTests
{
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
        Assert.Equal("original", content.Content);
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
}
