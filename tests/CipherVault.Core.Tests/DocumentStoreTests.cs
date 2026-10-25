using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using CipherVault.Core;
using CipherVault.Core.Crypto;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Core.Storage;

namespace CipherVault.Core.Tests;

public sealed class DocumentStoreTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task CatalogFiltersByVirtualFolder()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var page = await fixture.Service.ListCatalogAsync(
            folder: "健康",
            cursor: null,
            limit: 20);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.StartsWith("健康/", item.VirtualPath));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task FtsSearchFindsChineseAndEnglish()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var chinese = await fixture.Service.SearchNotesAsync(
            "睡眠质量",
            folder: null,
            cursor: null,
            limit: 10);
        var english = await fixture.Service.SearchNotesAsync(
            "sleep",
            folder: null,
            cursor: null,
            limit: 10);

        Assert.Single(chinese.Hits);
        Assert.Equal("睡眠记录", chinese.Hits[0].Title);
        Assert.Equal(2, english.Hits.Count);
        Assert.All(english.Hits, hit => Assert.Contains("sleep", hit.Snippet, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShortQueryFallsBackToBoundedScan()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var page = await fixture.Service.SearchNotesAsync(
            "睡眠",
            folder: null,
            cursor: null,
            limit: 10);

        Assert.Single(page.Hits);
        Assert.True(page.Coverage.ScannedDocuments <= 1_000);
        Assert.False(page.Coverage.IsTruncated);
        Assert.Contains("睡眠", page.Hits[0].Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortQueryDoesNotScanBeyondOneThousandDocuments()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await DocumentFixture.SeedShortQueryWindowAsync(fixture.VaultPath, 1_000);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var page = await fixture.Service.SearchNotesAsync(
            "针",
            folder: null,
            cursor: null,
            limit: 10);

        Assert.Empty(page.Hits);
        Assert.Equal(1_005, page.Coverage.TotalDocuments);
        Assert.Equal(1_000, page.Coverage.ScannedDocuments);
        Assert.Equal(0, page.Coverage.MatchedDocuments);
        Assert.True(page.Coverage.IsTruncated);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task CursorCannotBeReusedAfterSessionChange()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.SearchNotesAsync(
            "sleep",
            folder: null,
            cursor: null,
            limit: 1);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.LockAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.SearchNotesAsync(
                "sleep",
                folder: null,
                cursor: first.NextCursor,
                limit: 1));

        Assert.Equal(VaultErrorCode.SessionExpired, exception.Code);
    }

    [Fact]
    public async Task ReadNoteReturnsBoundedChunks()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var first = await fixture.Service.ReadNoteAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            offset: 0,
            maxChars: 4);
        var second = await fixture.Service.ReadNoteAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            offset: first.NextOffset,
            maxChars: 4);

        Assert.False(first.Done);
        Assert.Equal(4, first.Content.Length);
        Assert.Equal(first.NextOffset, second.Offset);
        Assert.NotEqual(first.Content, second.Content);
    }

    [Fact]
    public async Task ReadNoteRejectsTamperedHmac()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.TamperHmacAsync("11111111-1111-1111-1111-111111111111");
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ReadNoteAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                offset: 0,
                maxChars: 100));

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public async Task ScanNotesPagesActiveSummaries()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var first = await fixture.Service.ScanNotesAsync(cursor: null, limit: 2);
        var second = await fixture.Service.ScanNotesAsync(first.NextCursor, limit: 2);

        Assert.Equal(2, first.Items.Count);
        Assert.False(first.Done);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.Done);
    }

    [Fact]
    public async Task CatalogCursorDoesNotDuplicateAfterInsertionBeforeCursor()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.ListCatalogAsync(null, null, 2);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.CreateNoteAsync(
            "000-new",
            null,
            "md",
            "inserted before the cursor",
            [],
            VaultActorKind.Mcp);
        var second = await fixture.Service.ListCatalogAsync(
            null,
            first.NextCursor,
            2);

        var firstIds = first.Items.Select(item => item.DocumentId).ToHashSet();
        Assert.DoesNotContain(second.Items, item => firstIds.Contains(item.DocumentId));
        Assert.Equal(
            4,
            first.Items.Concat(second.Items)
                .Select(item => item.DocumentId)
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task CatalogCursorDoesNotDuplicateAfterRenamePastCursor()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.ListCatalogAsync(null, null, 2);
        Assert.NotNull(first.NextCursor);
        var moved = first.Items[1];

        await fixture.Service.UpdateNoteAsync(
            moved.DocumentId,
            moved.Revision,
            new VaultNotePatch(
                "zzzz-renamed",
                string.Empty,
                null,
                null),
            VaultActorKind.Mcp);
        var second = await fixture.Service.ListCatalogAsync(
            null,
            first.NextCursor,
            2);

        Assert.DoesNotContain(
            second.Items,
            item => item.DocumentId == moved.DocumentId);
    }

    [Fact]
    public async Task CatalogSnapshotSkipsDocumentUpdatedAfterFirstPage()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var seeded = await fixture.Service.ListCatalogAsync(null, null, 10);
        var first = await fixture.Service.ListCatalogAsync(null, null, 2);
        Assert.NotNull(first.NextCursor);
        var unseen = seeded.Items[2];

        await fixture.Service.UpdateNoteAsync(
            unseen.DocumentId,
            unseen.Revision,
            new VaultNotePatch(
                "zzzz-concurrent-update",
                string.Empty,
                null,
                null),
            VaultActorKind.Mcp);
        var second = await fixture.Service.ListCatalogAsync(
            null,
            first.NextCursor,
            10);

        Assert.DoesNotContain(
            second.Items,
            item => item.DocumentId == unseen.DocumentId);
        Assert.DoesNotContain(
            second.Items,
            item => first.Items.Any(firstItem =>
                firstItem.DocumentId == item.DocumentId));
    }

    [Fact]
    public async Task ScanCursorDoesNotDuplicateAfterInsertionBeforeCursor()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.ScanNotesAsync(cursor: null, limit: 2);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.CreateNoteAsync(
            "000-new",
            null,
            "md",
            "inserted before the cursor",
            [],
            VaultActorKind.Mcp);
        var second = await fixture.Service.ScanNotesAsync(first.NextCursor, limit: 2);

        var firstIds = first.Items.Select(item => item.DocumentId).ToHashSet();
        Assert.DoesNotContain(second.Items, item => firstIds.Contains(item.DocumentId));
    }

    [Fact]
    public async Task FtsSearchCursorDoesNotDuplicateAfterInsertion()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.SearchNotesAsync(
            "sleep",
            folder: null,
            cursor: null,
            limit: 1);
        Assert.Single(first.Hits);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.CreateNoteAsync(
            "New sleep",
            null,
            "md",
            "new sleep result",
            [],
            VaultActorKind.Mcp);
        var second = await fixture.Service.SearchNotesAsync(
            "sleep",
            folder: null,
            cursor: first.NextCursor,
            limit: 1);

        Assert.DoesNotContain(
            second.Hits,
            hit => hit.DocumentId == first.Hits[0].DocumentId);
    }

    [Fact]
    public async Task ShortSearchCursorDoesNotDuplicateAfterInsertion()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        await fixture.Service.CreateNoteAsync(
            "第二个睡眠记录",
            "健康",
            "md",
            "第二次睡眠记录",
            [],
            VaultActorKind.Mcp);
        var first = await fixture.Service.SearchNotesAsync(
            "睡眠",
            folder: null,
            cursor: null,
            limit: 1);
        Assert.Single(first.Hits);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.CreateNoteAsync(
            "第三个睡眠记录",
            "健康",
            "md",
            "第三次睡眠记录",
            [],
            VaultActorKind.Mcp);
        var second = await fixture.Service.SearchNotesAsync(
            "睡眠",
            folder: null,
            cursor: first.NextCursor,
            limit: 1);

        Assert.DoesNotContain(
            second.Hits,
            hit => hit.DocumentId == first.Hits[0].DocumentId);
    }

    [Fact]
    public async Task TrashCursorDoesNotDuplicateAfterNewDeletion()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 10);
        await fixture.Service.DeleteNoteAsync(
            catalog.Items[0].DocumentId,
            catalog.Items[0].Revision);
        await fixture.Service.DeleteNoteAsync(
            catalog.Items[1].DocumentId,
            catalog.Items[1].Revision);
        var first = await fixture.Service.ListTrashAsync(null, 1);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.DeleteNoteAsync(
            catalog.Items[2].DocumentId,
            catalog.Items[2].Revision);
        var second = await fixture.Service.ListTrashAsync(first.NextCursor, 1);

        Assert.DoesNotContain(
            second.Items,
            item => item.DocumentId == first.Items[0].DocumentId);
    }

    [Fact]
    public async Task RevisionCursorDoesNotDuplicateAfterNewRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var documentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await fixture.Service.AppendNoteAsync(documentId, 1, " revision 2");
        await fixture.Service.AppendNoteAsync(documentId, 2, " revision 3");
        var first = await fixture.Service.ListRevisionsAsync(
            documentId,
            cursor: null,
            limit: 1);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.AppendNoteAsync(documentId, 3, " revision 4");
        var second = await fixture.Service.ListRevisionsAsync(
            documentId,
            first.NextCursor,
            1);

        Assert.DoesNotContain(
            second.Items,
            item => item.RevisionId == first.Items[0].RevisionId);
        Assert.Equal(2, second.Items[0].Revision);
    }

    [Fact]
    public async Task AuditCursorDoesNotDuplicateAfterNewEvent()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        await fixture.Service.CreateNoteAsync(
            "audit-one",
            null,
            "md",
            "one",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.CreateNoteAsync(
            "audit-two",
            null,
            "md",
            "two",
            [],
            VaultActorKind.Mcp);
        var first = await fixture.Service.GetAuditPageAsync(null, 1);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        await fixture.Service.CreateNoteAsync(
            "audit-three",
            null,
            "md",
            "three",
            [],
            VaultActorKind.Mcp);
        var second = await fixture.Service.GetAuditPageAsync(first.NextCursor, 1);

        Assert.DoesNotContain(
            second.Items,
            item => item.EventId == first.Items[0].EventId);
    }

    [Fact]
    public async Task CreateNoteBuildsRevisionFtsAndReadableHmac()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var created = await fixture.Service.CreateNoteAsync(
            "治疗记录",
            "健康",
            "md",
            "# 治疗记录\n新的睡眠治疗方案。",
            [" 睡眠 ", "治疗", "睡眠"],
            VaultActorKind.Mcp);
        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            offset: 0,
            maxChars: 100);
        var search = await fixture.Service.SearchNotesAsync(
            "治疗方案",
            folder: "健康",
            cursor: null,
            limit: 10);
        var catalog = await fixture.Service.ListCatalogAsync(
            "健康",
            cursor: null,
            limit: 20);

        Assert.Equal(1, created.Revision);
        Assert.Contains("新的睡眠治疗方案", read.Content, StringComparison.Ordinal);
        Assert.Contains("H1: 治疗记录", read.Headings);
        Assert.Contains(search.Hits, hit => hit.DocumentId == created.DocumentId);
        Assert.Contains(catalog.Items, item => item.DocumentId == created.DocumentId);
    }

    [Fact]
    public async Task CreateNoteRejectsDuplicateActivePath()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        await fixture.Service.CreateNoteAsync(
            "重复路径",
            "工作",
            "md",
            "first",
            [],
            VaultActorKind.Mcp);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.CreateNoteAsync(
                "重复路径",
                "工作",
                "md",
                "second",
                [],
                VaultActorKind.Mcp));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task AppendCreatesNewRevisionAndSearchableContent()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "持续记录",
            "健康",
            "md",
            "初始记录。",
            [],
            VaultActorKind.Mcp);

        var appended = await fixture.Service.AppendNoteAsync(
            created.DocumentId,
            expectedRevision: 1,
            "追加的独特内容：康复进展。",
            VaultActorKind.Mcp);
        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            offset: 0,
            maxChars: 1_000);
        var search = await fixture.Service.SearchNotesAsync(
            "康复进展",
            folder: null,
            cursor: null,
            limit: 10);

        Assert.Equal(2, appended.Revision);
        Assert.Equal(2, read.Revision);
        Assert.Contains("康复进展", read.Content, StringComparison.Ordinal);
        Assert.Contains(search.Hits, hit => hit.DocumentId == created.DocumentId);
    }

    [Fact]
    public async Task AppendRejectsStaleRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "冲突记录",
            "工作",
            "md",
            "initial",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.AppendNoteAsync(
            created.DocumentId,
            1,
            "first append",
            VaultActorKind.Mcp);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.AppendNoteAsync(
                created.DocumentId,
                1,
                "stale append",
                VaultActorKind.Mcp));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task UpdateChangesTitleFolderContentTagsAndIndex()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "旧标题",
            "旧目录",
            "md",
            "# 旧标题\n旧内容",
            ["旧标签"],
            VaultActorKind.Mcp);

        var updated = await fixture.Service.UpdateNoteAsync(
            created.DocumentId,
            expectedRevision: 1,
            new VaultNotePatch(
                Title: "新标题",
                Folder: "新目录",
                Content: "# 新标题\n这是新的独特内容。",
                Tags: ["新标签"]),
            VaultActorKind.Mcp);
        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            offset: 0,
            maxChars: 1_000);
        var catalog = await fixture.Service.ListCatalogAsync(
            "新目录",
            cursor: null,
            limit: 10);
        var search = await fixture.Service.SearchNotesAsync(
            "独特内容",
            folder: null,
            cursor: null,
            limit: 10);

        Assert.Equal(2, updated.Revision);
        Assert.Equal("新标题", read.Title);
        Assert.StartsWith("新目录/", read.VirtualPath);
        Assert.Contains("H1: 新标题", read.Headings);
        var updatedItem = Assert.Single(
            catalog.Items,
            item => item.DocumentId == created.DocumentId);
        Assert.Contains("新标签", updatedItem.Tags);
        Assert.DoesNotContain("旧标签", updatedItem.Tags);
        Assert.Contains(search.Hits, hit => hit.DocumentId == created.DocumentId);
    }

    [Fact]
    public async Task UpdateRejectsStaleRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "更新冲突",
            "工作",
            "md",
            "content",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.UpdateNoteAsync(
            created.DocumentId,
            1,
            new VaultNotePatch(Content: "new content"),
            VaultActorKind.Mcp);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UpdateNoteAsync(
                created.DocumentId,
                1,
                new VaultNotePatch(Content: "stale content"),
                VaultActorKind.Mcp));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task SoftDeleteRemovesDocumentFromReadCatalogAndSearch()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "待删除",
            "工作",
            "md",
            "独特删除内容",
            [],
            VaultActorKind.Mcp);

        var deleted = await fixture.Service.DeleteNoteAsync(
            created.DocumentId,
            expectedRevision: 1,
            VaultActorKind.Mcp);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 100);
        var search = await fixture.Service.SearchNotesAsync(
            "独特删除内容",
            null,
            null,
            10);

        Assert.True(deleted.PurgeAfter > DateTimeOffset.UtcNow);
        Assert.DoesNotContain(catalog.Items, item => item.DocumentId == created.DocumentId);
        Assert.DoesNotContain(search.Hits, hit => hit.DocumentId == created.DocumentId);
        await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ReadNoteAsync(created.DocumentId, 0, 100));
    }

    [Fact]
    public async Task SoftDeleteRejectsStaleRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "删除冲突",
            "工作",
            "md",
            "content",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.AppendNoteAsync(
            created.DocumentId,
            1,
            "append",
            VaultActorKind.Mcp);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.DeleteNoteAsync(
                created.DocumentId,
                expectedRevision: 1,
                VaultActorKind.Mcp));

        Assert.Equal(VaultErrorCode.Conflict, exception.Code);
    }

    [Fact]
    public async Task TrashListAndRestoreCreateNewRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "可恢复文档",
            "工作",
            "md",
            "restore me",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.DeleteNoteAsync(created.DocumentId, 1, VaultActorKind.Mcp);

        var trash = await fixture.Service.ListTrashAsync(null, 20);
        var restored = await fixture.Service.RestoreTrashAsync(
            created.DocumentId,
            expectedRevision: 1,
            renamePath: null);
        var catalog = await fixture.Service.ListCatalogAsync("工作", null, 20);

        Assert.Contains(trash.Items, item => item.DocumentId == created.DocumentId);
        Assert.Equal(2, restored.Revision);
        Assert.Contains(catalog.Items, item => item.DocumentId == created.DocumentId);
    }

    [Fact]
    public async Task RestoreTrashRequiresRenameWhenPathIsOccupied()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var first = await fixture.Service.CreateNoteAsync(
            "同名文件",
            "工作",
            "md",
            "first",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.DeleteNoteAsync(first.DocumentId, 1, VaultActorKind.Mcp);
        _ = await fixture.Service.CreateNoteAsync(
            "同名文件",
            "工作",
            "md",
            "second",
            [],
            VaultActorKind.Mcp);

        var conflict = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.RestoreTrashAsync(first.DocumentId, 1, null));
        Assert.Equal(VaultErrorCode.Conflict, conflict.Code);

        var renamed = await fixture.Service.RestoreTrashAsync(
            first.DocumentId,
            1,
            "工作/恢复版本.md");
        Assert.EndsWith("恢复版本.md", renamed.VirtualPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgeRemovesDocumentRevisionsAndFts()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "彻底删除",
            "工作",
            "md",
            "permanent marker",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.DeleteNoteAsync(created.DocumentId, 1, VaultActorKind.Mcp);

        var purged = await fixture.Service.PurgeNoteAsync(created.DocumentId);
        var trash = await fixture.Service.ListTrashAsync(null, 20);
        var search = await fixture.Service.SearchNotesAsync(
            "permanent marker",
            null,
            null,
            10);

        Assert.True(purged.Purged);
        Assert.DoesNotContain(trash.Items, item => item.DocumentId == created.DocumentId);
        Assert.DoesNotContain(search.Hits, hit => hit.DocumentId == created.DocumentId);
        await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ListRevisionsAsync(created.DocumentId, null, 20));
    }

    [Fact]
    public async Task RevisionListAndRestoreProduceNewCurrentRevision()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "版本恢复",
            "工作",
            "md",
            "version one",
            [],
            VaultActorKind.Mcp);
        await fixture.Service.AppendNoteAsync(
            created.DocumentId,
            1,
            " version two",
            VaultActorKind.Mcp);
        await fixture.Service.UpdateNoteAsync(
            created.DocumentId,
            2,
            new VaultNotePatch(Content: "version three"),
            VaultActorKind.Mcp);

        var revisions = await fixture.Service.ListRevisionsAsync(
            created.DocumentId,
            null,
            20);
        var oldest = revisions.Items.Single(item => item.Revision == 1);
        var restored = await fixture.Service.RestoreRevisionAsync(
            created.DocumentId,
            oldest.RevisionId,
            expectedRevision: 3);
        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            offset: 0,
            maxChars: 100);

        Assert.Equal(3, revisions.Items.Count);
        Assert.Equal(4, restored.Revision);
        Assert.Equal("version one", read.Content);
        Assert.Equal(4, read.Revision);
    }

    [Fact]
    public async Task BackupCanBeRestoredAndReadInNewDirectory()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "备份文档",
            "工作",
            "md",
            "backup marker",
            ["备份"],
            VaultActorKind.Mcp);
        var backupPath = Path.Combine(fixture.RootPath, "vault.cvbackup");
        var targetPath = Path.Combine(fixture.RootPath, "restored");

        var backup = await fixture.Service.CreateBackupAsync(backupPath);
        var sourceAudit = await fixture.Service.GetAuditPageAsync(null, 100);
        var backupEvent = Assert.Single(
            sourceAudit.Items,
            item => item.Action == "backup_create");
        var restored = await fixture.Service.RestoreBackupAsync(
            backupPath,
            targetPath,
            Password,
            VaultCredentialType.Password);

        await using var restoredService = new VaultService(new ManualActivityProvider());
        await restoredService.UnlockAsync(
            restored.VaultPath,
            Password,
            VaultCredentialType.Password);
        var read = await restoredService.ReadNoteAsync(
            created.DocumentId,
            0,
            100);
        var restoredAudit = await restoredService.GetAuditPageAsync(null, 100);
        var restoreEvent = Assert.Single(
            restoredAudit.Items,
            item => item.Action == "backup_restore");

        Assert.True(File.Exists(backup.BackupPath));
        Assert.Equal(created.DocumentId, read.DocumentId);
        Assert.Contains("backup marker", read.Content, StringComparison.Ordinal);
        Assert.Equal("success", backupEvent.Result);
        Assert.DoesNotContain(
            backupPath,
            backupEvent.DetailsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("success", restoreEvent.Result);
        Assert.DoesNotContain(
            targetPath,
            restoreEvent.DetailsJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditVerifyAndRebuildDetectAndRepairFtsDamage()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "审计文档",
            "工作",
            "md",
            "audit marker",
            [],
            VaultActorKind.Mcp);

        var audit = await fixture.Service.GetAuditPageAsync(null, 100);
        var before = await fixture.Service.VerifyVaultAsync();
        await fixture.DeleteAllFtsRowsAsync();
        var damaged = await fixture.Service.VerifyVaultAsync();
        var rebuilt = await fixture.Service.RebuildSearchIndexAsync();
        var after = await fixture.Service.VerifyVaultAsync();
        var search = await fixture.Service.SearchNotesAsync(
            "audit marker",
            null,
            null,
            10);

        Assert.Contains(audit.Items, item => item.Action == "create");
        Assert.True(before.Valid, string.Join("; ", before.Problems));
        Assert.False(damaged.Valid);
        Assert.Equal(before.ActiveDocuments, rebuilt);
        Assert.True(after.Valid);
        Assert.Contains(search.Hits, hit => hit.DocumentId == created.DocumentId);
    }

    [Fact]
    public async Task ImportPreviewAndCommitCreateSearchableDocuments()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var sourceRoot = Path.Combine(fixture.RootPath, "import-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "健康"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "健康", "导入睡眠.md"),
            "---\ntags: [健康, 睡眠]\n---\n# 导入睡眠\n独特的导入睡眠内容。");
        await File.WriteAllTextAsync(
            Path.Combine(sourceRoot, "说明.txt"),
            "纯文本导入内容");
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "image.png"),
            [1, 2, 3]);

        var preview = await fixture.Service.PreviewImportAsync(sourceRoot);
        var result = await fixture.Service.CommitImportAsync(preview.JobId);
        var catalog = await fixture.Service.ListCatalogAsync("健康", null, 100);
        var search = await fixture.Service.SearchNotesAsync(
            "导入睡眠",
            null,
            null,
            20);

        Assert.Equal(2, preview.SupportedFiles);
        Assert.Equal(1, preview.UnsupportedFiles);
        Assert.Equal(2, result.Imported);
        Assert.Contains(catalog.Items, item =>
            item.Title == "导入睡眠" &&
            item.Tags.Contains("健康") &&
            item.Tags.Contains("睡眠"));
        Assert.Contains(search.Hits, hit => hit.Title == "导入睡眠");
    }

    [Fact]
    public async Task ExportWritesMarkdownManifestAndChecksums()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        await fixture.Service.CreateNoteAsync(
            "导出文档",
            "导出",
            "md",
            "# 导出文档\n导出内容。",
            ["导出"],
            VaultActorKind.Mcp);
        var destination = Path.Combine(fixture.RootPath, "export-snapshot");

        var result = await fixture.Service.ExportSnapshotAsync(destination);

        Assert.True(File.Exists(Path.Combine(destination, "vault-export-manifest.json")));
        Assert.True(File.Exists(Path.Combine(destination, "vault-export-checksums.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "导出", "导出文档.md")));
        Assert.True(result.ExportedDocuments > 0);
        var audit = await fixture.Service.GetAuditPageAsync(null, 100);
        var exportEvent = Assert.Single(
            audit.Items,
            item => item.Action == "export_snapshot");
        Assert.Equal("success", exportEvent.Result);
        Assert.DoesNotContain(
            destination,
            exportEvent.DetailsJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentAppendWithSameRevisionAllowsOnlyOneWriter()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var created = await fixture.Service.CreateNoteAsync(
            "并发文档",
            "工作",
            "md",
            "initial",
            [],
            VaultActorKind.Mcp);

        var results = await Task.WhenAll(
            CaptureAppendResultAsync(fixture.Service.AppendNoteAsync(
                created.DocumentId, 1, " first", VaultActorKind.Mcp)),
            CaptureAppendResultAsync(fixture.Service.AppendNoteAsync(
                created.DocumentId, 1, " second", VaultActorKind.Mcp)));

        Assert.Equal(1, results.Count(result => result.Success));
        Assert.Equal(
            1,
            results.Count(result => result.ErrorCode == VaultErrorCode.Conflict));
    }

    [Fact]
    public async Task CorruptedBackupIsRejected()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var backupPath = Path.Combine(fixture.RootPath, "broken.cvbackup");
        await fixture.Service.CreateBackupAsync(backupPath);
        var bytes = await File.ReadAllBytesAsync(backupPath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(backupPath, bytes);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.RestoreBackupAsync(
                backupPath,
                Path.Combine(fixture.RootPath, "broken-restore"),
                Password,
                VaultCredentialType.Password));

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public async Task UncommittedTransactionDoesNotBecomeVisibleAfterCrashLikeDispose()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var envelope = await VaultKeyring.LoadEnvelopeAsync(
            Path.Combine(fixture.VaultPath, "vault.keys"));
        var material = await VaultKeyring.UnwrapAsync(
            envelope,
            Password,
            VaultCredentialType.Password);
        var id = "99999999-9999-9999-9999-999999999999";
        try
        {
            await using var connection = await SqlCipherVaultStore.OpenAsync(
                fixture.VaultPath,
                material);
            await using var transaction = await connection.BeginTransactionAsync();
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO documents(
                    document_id, title, source_path, source_path_norm, format,
                    content, headings_json, tags_json, payload_hmac, revision,
                    created_at_ms, updated_at_ms, deleted_at_ms, purge_after_ms)
                VALUES($id, 'crash-only', 'crash.md', 'crash.md', 'md',
                       'uncommitted', '[]', '[]', $hmac, 1,
                       1, 1, NULL, NULL);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$hmac", new byte[32]);
            await command.ExecuteNonQueryAsync();
            // No commit: dispose the connection/transaction like a process crash.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.MasterKey);
            CryptographicOperations.ZeroMemory(material.DbKey);
        }

        var catalog = await fixture.Service.ListCatalogAsync(null, null, 100);
        Assert.DoesNotContain(catalog.Items, item => item.DocumentId == Guid.Parse(id));
    }

    [Fact]
    public async Task ImportCommitRejectsSourceChangedAfterPreview()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var source = Path.Combine(fixture.RootPath, "changed-source");
        Directory.CreateDirectory(source);
        var sourceFile = Path.Combine(source, "changed.md");
        await File.WriteAllTextAsync(sourceFile, "preview version");

        var preview = await fixture.Service.PreviewImportAsync(source);
        await File.WriteAllTextAsync(sourceFile, "changed after preview");
        var result = await fixture.Service.CommitImportAsync(preview.JobId);

        Assert.Equal(1, result.Failed);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 100);
        Assert.DoesNotContain(catalog.Items, item => item.Title == "changed");
    }

    [Fact]
    public async Task ImportCommitKeepsOtherFilesWhenOneChangesAfterPreview()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var source = Path.Combine(fixture.RootPath, "partially-changed-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "a-first.md"),
            "# 第一篇\n保持可导入。");
        var changedSource = Path.Combine(source, "b-changed.md");
        await File.WriteAllTextAsync(changedSource, "# 第二篇\n预览版本。");
        await File.WriteAllTextAsync(
            Path.Combine(source, "c-last.md"),
            "# 第三篇\n也应成功导入。");

        var preview = await fixture.Service.PreviewImportAsync(source);
        await File.WriteAllTextAsync(changedSource, "# 第二篇\n预览后变化。");
        var result = await fixture.Service.CommitImportAsync(preview.JobId);
        var retry = await fixture.Service.CommitImportAsync(preview.JobId);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 100);

        Assert.Equal(3, preview.SupportedFiles);
        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Failed);
        Assert.Equal("failed", result.State);
        Assert.Equal(2, retry.Imported);
        Assert.Equal(1, retry.Failed);
        Assert.Contains(catalog.Items, item => item.Title == "a-first");
        Assert.DoesNotContain(catalog.Items, item => item.Title == "b-changed");
        Assert.Contains(catalog.Items, item => item.Title == "c-last");
    }

    [Fact]
    public async Task ImportReconcilesForwardLinksCreatedInLaterBatch()
    {
        await using var fixture = await DocumentFixture.CreateAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        var source = Path.Combine(fixture.RootPath, "cross-batch-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "a-source.md"),
            "# 来源\n参见 [[z-target]]。");
        for (var index = 0; index < 99; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(source, $"f-{index:D3}.md"),
                $"# 填充 {index}\n批次填充内容。");
        }
        await File.WriteAllTextAsync(
            Path.Combine(source, "z-target.md"),
            "# 目标\n第二批创建的链接目标。");

        var preview = await fixture.Service.PreviewImportAsync(source);
        var result = await fixture.Service.CommitImportAsync(preview.JobId);
        var catalogItems = new List<VaultCatalogItem>();
        string? cursor = null;
        do
        {
            var page = await fixture.Service.ListCatalogAsync(null, cursor, 100);
            catalogItems.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        var sourceItem = Assert.Single(catalogItems, item => item.Title == "a-source");
        var targetItem = Assert.Single(catalogItems, item => item.Title == "z-target");
        var sourceMap = await fixture.Service.GetDocumentMapAsync(sourceItem.DocumentId);
        var targetMap = await fixture.Service.GetDocumentMapAsync(targetItem.DocumentId);
        var link = Assert.Single(sourceMap.OutgoingLinks);

        Assert.Equal(101, preview.SupportedFiles);
        Assert.Equal(101, result.Imported);
        Assert.Equal("completed", result.State);
        Assert.Equal("resolved", link.ResolutionStatus);
        Assert.Equal(targetItem.DocumentId, link.Target?.DocumentId);
        Assert.Equal(1, targetMap.BacklinkCount);
    }

    private static async Task<(bool Success, VaultErrorCode? ErrorCode)> CaptureAppendResultAsync(
        Task<VaultWriteResult> operation)
    {
        try
        {
            await operation;
            return (true, null);
        }
        catch (VaultException exception)
        {
            return (false, exception.Code);
        }
    }

    private sealed class DocumentFixture : IAsyncDisposable
    {
        private DocumentFixture(
            string rootPath,
            string vaultPath,
            ManualActivityProvider activity,
            ManualTimeProvider time,
            VaultService service)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            Activity = activity;
            Time = time;
            Service = service;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public ManualActivityProvider Activity { get; }
        public ManualTimeProvider Time { get; }
        public VaultService Service { get; }

        public static async Task<DocumentFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-docs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var activity = new ManualActivityProvider();
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
            var service = new VaultService(activity, time);
            var initialized = await service.InitializeAsync(vaultPath, Password);
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await SeedAsync(vaultPath, time.GetUtcNow());
            return new DocumentFixture(root, vaultPath, activity, time, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        public async Task TamperHmacAsync(string documentId)
        {
            var envelope = await VaultKeyring.LoadEnvelopeAsync(
                Path.Combine(VaultPath, "vault.keys"));
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                Password,
                VaultCredentialType.Password);
            try
            {
                await using var connection = await SqlCipherVaultStore.OpenAsync(
                    VaultPath,
                    material);
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE documents SET payload_hmac = $hmac WHERE document_id = $id;";
                command.Parameters.AddWithValue("$hmac", new byte[32]);
                command.Parameters.AddWithValue("$id", documentId);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }

        public async Task DeleteAllFtsRowsAsync()
        {
            var envelope = await VaultKeyring.LoadEnvelopeAsync(
                Path.Combine(VaultPath, "vault.keys"));
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                Password,
                VaultCredentialType.Password);
            try
            {
                await using var connection = await SqlCipherVaultStore.OpenAsync(
                    VaultPath,
                    material);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM documents_fts;";
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }

        public static async Task SeedShortQueryWindowAsync(
            string vaultPath,
            int decoyCount)
        {
            var envelope = await VaultKeyring.LoadEnvelopeAsync(
                Path.Combine(vaultPath, "vault.keys"));
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                Password,
                VaultCredentialType.Password);
            try
            {
                var integrityKey = VaultKeyring.DeriveIntegrityKey(material.MasterKey);
                await using var connection = await SqlCipherVaultStore.OpenAsync(
                    vaultPath,
                    material);
                using var transaction = connection.BeginTransaction();
                var baseTime = DateTimeOffset.UtcNow;
                for (var index = 0; index < decoyCount; index++)
                {
                    var documentId =
                        $"90000000-0000-0000-0000-{index + 1:D12}";
                    await InsertSearchWindowDocumentAsync(
                        connection,
                        transaction,
                        integrityKey,
                        documentId,
                        $"decoy-{index:D4}",
                        $"decoy/{index:D4}.md",
                        "decoy content",
                        baseTime.AddDays(1).AddMilliseconds(index));
                }

                await InsertSearchWindowDocumentAsync(
                    connection,
                    transaction,
                    integrityKey,
                    "99999999-9999-9999-9999-999999999999",
                    "outside-window",
                    "zzzz-outside-window.md",
                    "针",
                    baseTime.AddDays(-1));
                transaction.Commit();
                CryptographicOperations.ZeroMemory(integrityKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }

        private static async Task InsertSearchWindowDocumentAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            byte[] integrityKey,
            string documentId,
            string title,
            string sourcePath,
            string content,
            DateTimeOffset updatedAt)
        {
            const string headingsJson = "[]";
            const string tagsJson = "[]";
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentId,
                1,
                title,
                sourcePath,
                "md",
                headingsJson,
                tagsJson,
                content);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO documents(
                    document_id, title, source_path, source_path_norm, format,
                    content, headings_json, tags_json, payload_hmac, revision,
                    created_at_ms, updated_at_ms, deleted_at_ms, purge_after_ms)
                VALUES(
                    $id, $title, $path, $pathNorm, 'md',
                    $content, $headings, $tags, $hmac, 1,
                    $created, $updated, NULL, NULL);
                INSERT INTO documents_fts(
                    document_id, title, source_path, headings, tags, content)
                VALUES($id, $title, $path, $headings, $tags, $content);
                """;
            command.Parameters.AddWithValue("$id", documentId);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$path", sourcePath);
            command.Parameters.AddWithValue("$pathNorm", sourcePath.ToLowerInvariant());
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$headings", headingsJson);
            command.Parameters.AddWithValue("$tags", tagsJson);
            command.Parameters.AddWithValue("$hmac", hmac);
            command.Parameters.AddWithValue(
                "$created",
                updatedAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue(
                "$updated",
                updatedAt.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        private static async Task SeedAsync(string vaultPath, DateTimeOffset now)
        {
            var envelope = await VaultKeyring.LoadEnvelopeAsync(
                Path.Combine(vaultPath, "vault.keys"));
            var material = await VaultKeyring.UnwrapAsync(
                envelope,
                Password,
                VaultCredentialType.Password);
            try
            {
                var integrityKey = VaultKeyring.DeriveIntegrityKey(material.MasterKey);
                await using var connection = await SqlCipherVaultStore.OpenAsync(
                    vaultPath,
                    material);
                var timestamp = now.ToUnixTimeMilliseconds();
                await InsertDocumentAsync(
                    connection,
                    "11111111-1111-1111-1111-111111111111",
                    "睡眠记录",
                    "健康/睡眠.md",
                    "健康/睡眠.md",
                    "最近睡眠质量下降，半夜经常醒来。",
                    "[]",
                    "[\"健康\",\"睡眠\"]",
                    timestamp,
                    integrityKey);
                await InsertDocumentAsync(
                    connection,
                    "22222222-2222-2222-2222-222222222222",
                    "Sleep routine",
                    "工作/sleep-routine.md",
                    "工作/sleep-routine.md",
                    "sleep routine and evening review",
                    "[]",
                    "[\"工作\"]",
                    timestamp + 1,
                    integrityKey);
                await InsertDocumentAsync(
                    connection,
                    "33333333-3333-3333-3333-333333333333",
                    "Sleep notes",
                    "工作/sleep-notes.md",
                    "工作/sleep-notes.md",
                    "second sleep note for pagination",
                    "[]",
                    "[\"工作\"]",
                    timestamp + 2,
                    integrityKey);
                await InsertDocumentAsync(
                    connection,
                    "44444444-4444-4444-4444-444444444444",
                    "体检记录",
                    "健康/体检.md",
                    "健康/体检.md",
                    "年度体检结果",
                    "[]",
                    "[\"健康\"]",
                    timestamp + 3,
                    integrityKey);
                CryptographicOperations.ZeroMemory(integrityKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }

        private static async Task InsertDocumentAsync(
            SqliteConnection connection,
            string documentId,
            string title,
            string sourcePath,
            string sourcePathNorm,
            string content,
            string headingsJson,
            string tagsJson,
            long timestamp,
            byte[] integrityKey)
        {
            var hmac = DocumentHmac.Compute(
                integrityKey,
                documentId,
                1,
                title,
                sourcePath,
                "md",
                headingsJson,
                tagsJson,
                content);
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO documents(
                        document_id, title, title_norm, source_path, source_path_norm,
                        link_path_norm, format, content, headings_json, tags_json,
                        payload_hmac, revision, links_indexed_revision,
                        created_at_ms, updated_at_ms, deleted_at_ms, purge_after_ms)
                    VALUES(
                        $id, $title, $titleNorm, $path, $pathNorm, $linkPathNorm,
                        'md', $content, $headings, $tags, $hmac, 1, 1,
                        $created, $updated, NULL, NULL);
                    INSERT INTO documents_fts(document_id, title, source_path, headings, tags, content)
                    VALUES($id, $title, $path, $headings, $tags, $content);
                    """;
                command.Parameters.AddWithValue("$id", documentId);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue(
                    "$titleNorm",
                    DocumentLinkKeys.NormalizeTitle(title));
                command.Parameters.AddWithValue("$path", sourcePath);
                command.Parameters.AddWithValue("$pathNorm", sourcePathNorm);
                command.Parameters.AddWithValue(
                    "$linkPathNorm",
                    DocumentLinkKeys.NormalizeDocumentPath(sourcePath));
                command.Parameters.AddWithValue("$content", content);
                command.Parameters.AddWithValue("$headings", headingsJson);
                command.Parameters.AddWithValue("$tags", tagsJson);
                command.Parameters.AddWithValue("$hmac", hmac);
                command.Parameters.AddWithValue("$created", timestamp);
                command.Parameters.AddWithValue("$updated", timestamp);
                await command.ExecuteNonQueryAsync();
            }
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson) ?? [];
            foreach (var tag in tags)
            {
                var normalized = VirtualPathPolicy
                    .NormalizeTag(tag)
                    .ToLowerInvariant();
                using (var insertTag = connection.CreateCommand())
                {
                    insertTag.Transaction = transaction;
                    insertTag.CommandText =
                        """
                        INSERT INTO tags(tag_id, name, normalized_name)
                        VALUES($id, $name, $normalized)
                        ON CONFLICT(normalized_name) DO NOTHING;
                        """;
                    insertTag.Parameters.AddWithValue(
                        "$id",
                        Guid.NewGuid().ToString("D"));
                    insertTag.Parameters.AddWithValue("$name", tag);
                    insertTag.Parameters.AddWithValue("$normalized", normalized);
                    await insertTag.ExecuteNonQueryAsync();
                }
                using var insertRelation = connection.CreateCommand();
                insertRelation.Transaction = transaction;
                insertRelation.CommandText =
                    """
                    INSERT OR IGNORE INTO document_tags(document_id, tag_id)
                    SELECT $documentId, tag_id
                    FROM tags
                    WHERE normalized_name = $normalized;
                    """;
                insertRelation.Parameters.AddWithValue(
                    "$documentId",
                    documentId);
                insertRelation.Parameters.AddWithValue("$normalized", normalized);
                await insertRelation.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }
    }

    private sealed class ManualActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
