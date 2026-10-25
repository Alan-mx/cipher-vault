using System.Text.Json;
using System.Security.Cryptography;
using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;
using CipherVault.Core.Storage;

namespace CipherVault.Core.Tests;

public sealed class KnowledgeGraphTests
{
    [Fact]
    public void WikiLinkParserSkipsCodeCommentsAndEscapes()
    {
        const string content =
            """
            [[目标]]
            [[目标|显示名]]
            [[目标#章节]]
            `[[行内代码]]`
            ```text
            [[代码块]]
            ```
            <!-- [[注释]] -->
            \[[转义]]
            """;

        var links = WikiLinkParser.Parse(content);

        Assert.Equal(3, links.Count);
        Assert.Equal("目标", links[0].RawTarget);
        Assert.Equal("显示名", links[1].DisplayText);
        Assert.Equal("章节", links[2].TargetHeading);
    }

    [Theory]
    [InlineData('`')]
    [InlineData('~')]
    public void WikiLinkParserRequiresWhitespaceOnlyClosingFence(
        char marker)
    {
        var fence = new string(marker, 3);
        var content =
            $"{fence}csharp\n" +
            "var example = 1;\n" +
            $"{fence}text\n" +
            "[[代码示例]]\n" +
            $"{fence}\n" +
            "[[真实链接]]";

        var links = WikiLinkParser.Parse(content);

        var link = Assert.Single(links);
        Assert.Equal("真实链接", link.RawTarget);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void FrontMatterTagsParseBlockListsAcrossLineEndings(
        string newline)
    {
        var content = string.Join(
            newline,
            "---",
            "tags:",
            "- Security",
            "- MCP",
            "---",
            "# 标签同步");

        Assert.True(FrontMatterTags.TryExtract(content, out var tags));
        Assert.Equal(["Security", "MCP"], tags);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void FrontMatterTagsIgnoreIndentedDelimiterInBlockScalar(
        string newline)
    {
        var content = string.Join(
            newline,
            "---",
            "description: |",
            "  第一行说明",
            "  ---",
            "  第二行说明",
            "tags:",
            "  - Security",
            "  - MCP",
            "---",
            "# 正文");

        Assert.True(FrontMatterTags.TryExtract(content, out var tags));
        Assert.Equal(["Security", "MCP"], tags);
    }

    [Theory]
    [InlineData("\n", "tags: [Java, Backend] # programming", "Java|Backend")]
    [InlineData("\r\n", "tags:\n- 'Java'\n- \"machine learning\" # note", "Java|machine learning")]
    [InlineData("\r\n", "tags:\n- Java\n# between items\n- MCP", "Java|MCP")]
    public void FrontMatterTagsParseYamlCommentsAndQuotes(
        string newline,
        string yaml,
        string expected)
    {
        var content = "---" + newline +
            yaml.Replace("\n", newline, StringComparison.Ordinal) +
            newline + "---" + newline + "# body";

        Assert.True(FrontMatterTags.TryExtract(content, out var tags));
        Assert.Equal(expected.Split('|'), tags);
    }

    [Fact]
    public async Task LinksBacklinksTagsAndGraphAreQueryable()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "目标",
            null,
            "md",
            "# 目标\n目标正文",
            ["目标标签"],
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "# 来源\n参见 [[目标]] 和 [[目标#目标]]。",
            ["来源标签"],
            VaultActorKind.UserUi);

        var sourceMap = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(2, sourceMap.OutgoingLinks.Count);
        Assert.All(sourceMap.OutgoingLinks, link =>
        {
            Assert.Equal("resolved", link.ResolutionStatus);
            Assert.Equal(target.DocumentId, link.Target?.DocumentId);
        });

        var targetMap = await fixture.Service.GetDocumentMapAsync(target.DocumentId);
        Assert.Equal(2, targetMap.BacklinkCount);
        var backlinks = await fixture.Service.ListBacklinksAsync(
            target.DocumentId,
            null,
            50);
        var backlink = Assert.Single(backlinks.Items);
        Assert.Equal(source.DocumentId, backlink.SourceDocumentId);
        Assert.Equal(2, backlink.OccurrenceCount);

        var tags = await fixture.Service.ListTagsAsync(null, 50);
        Assert.Contains(tags.Items, tag => tag.Name == "来源标签" && tag.DocumentCount == 1);

        var graph = await fixture.Service.GetLocalGraphAsync(
            new VaultLocalGraphQuery(
                source.DocumentId,
                1,
                [],
                null,
                200,
                500));
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Single(graph.Edges);
        Assert.True(graph.Nodes.Single(node => node.DocumentId == source.DocumentId).IsCenter);
    }

    [Fact]
    public async Task RenameAndReindexResolveThroughHistoricalAlias()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "旧目标",
            null,
            "md",
            "# 旧目标",
            null,
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "[[旧目标]]",
            null,
            VaultActorKind.UserUi);

        await fixture.Service.UpdateNoteAsync(
            target.DocumentId,
            target.Revision,
            new VaultNotePatch(
                Title: "新目标",
                Folder: null,
                Content: null,
                Tags: null),
            VaultActorKind.UserUi);
        var appended = await fixture.Service.AppendNoteAsync(
            source.DocumentId,
            source.Revision,
            "\n[[旧目标]]",
            VaultActorKind.UserUi);

        var map = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(appended.Revision, map.Revision);
        Assert.Equal(2, map.OutgoingLinks.Count);
        Assert.All(map.OutgoingLinks, link =>
            Assert.Equal(target.DocumentId, link.Target?.DocumentId));
    }

    [Fact]
    public async Task RenameWithBacklinkSyncRewritesOnlyMatchingTitleLinks()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "旧目标",
            "folder",
            "md",
            "# 旧目标",
            null,
            VaultActorKind.UserUi);
        var titleSource = await fixture.Service.CreateNoteAsync(
            "标题引用",
            null,
            "md",
            "[[旧目标]]\n[[旧目标#章节|显示名]]",
            null,
            VaultActorKind.UserUi);
        var pathSource = await fixture.Service.CreateNoteAsync(
            "路径引用",
            null,
            "md",
            "[[folder/旧目标]]",
            null,
            VaultActorKind.UserUi);
        var secondTitleSource = await fixture.Service.CreateNoteAsync(
            "第二标题引用",
            null,
            "md",
            "[[旧目标]]",
            null,
            VaultActorKind.UserUi);

        var updated = await fixture.Service.UpdateNoteAsync(
            target.DocumentId,
            target.Revision,
            new VaultNotePatch(
                Title: "新目标",
                UpdateBacklinks: true),
            VaultActorKind.UserUi);
        var rewritten = await fixture.Service.ReadNoteAsync(
            titleSource.DocumentId,
            0,
            16_000);
        var unchanged = await fixture.Service.ReadNoteAsync(
            pathSource.DocumentId,
            0,
            16_000);
        var secondRewritten = await fixture.Service.ReadNoteAsync(
            secondTitleSource.DocumentId,
            0,
            16_000);
        var rewrittenMap = await fixture.Service.GetDocumentMapAsync(
            titleSource.DocumentId);

        Assert.True(updated.Revision > target.Revision);
        Assert.Equal(2, rewritten.Revision);
        Assert.Equal(
            "[[新目标]]\n[[新目标#章节|显示名]]",
            rewritten.Content);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal("[[folder/旧目标]]", unchanged.Content);
        Assert.Equal(2, secondRewritten.Revision);
        Assert.Equal("[[新目标]]", secondRewritten.Content);
        Assert.Equal(2, rewrittenMap.OutgoingLinks.Count);
        Assert.All(rewrittenMap.OutgoingLinks, link =>
            Assert.Equal(target.DocumentId, link.Target?.DocumentId));
        var audit = await fixture.Service.GetAuditPageAsync(
            new VaultAuditQuery(Limit: 100));
        var updateEvent = Assert.Single(
            audit.Items,
            item => item.Action == "update" &&
                item.DocumentId == target.DocumentId);
        using var details = JsonDocument.Parse(updateEvent.DetailsJson);
        Assert.Equal(
            2,
            details.RootElement.GetProperty("rewrittenDocuments").GetInt32());
        Assert.Equal(
            3,
            details.RootElement.GetProperty("rewrittenLinks").GetInt32());
        var correlationId = details.RootElement
            .GetProperty("correlationId")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        var sourceEvents = audit.Items
            .Where(item => item.Action == "backlink_rewrite")
            .ToArray();
        Assert.Equal(2, sourceEvents.Length);
        Assert.DoesNotContain(
            sourceEvents,
            item => item.DocumentId == pathSource.DocumentId);
        foreach (var sourceEvent in sourceEvents)
        {
            using var sourceDetails = JsonDocument.Parse(
                sourceEvent.DetailsJson);
            Assert.Equal(
                target.DocumentId.ToString("D").ToLowerInvariant(),
                sourceDetails.RootElement
                    .GetProperty("targetDocumentId")
                    .GetString());
            Assert.Equal(
                correlationId,
                sourceDetails.RootElement
                    .GetProperty("correlationId")
                    .GetString());
        }
        var firstSourceEvent = Assert.Single(
            sourceEvents,
            item => item.DocumentId == titleSource.DocumentId);
        using var firstSourceDetails = JsonDocument.Parse(
            firstSourceEvent.DetailsJson);
        Assert.Equal(
            2,
            firstSourceDetails.RootElement
                .GetProperty("rewrittenLinks")
                .GetInt32());
        var secondSourceEvent = Assert.Single(
            sourceEvents,
            item => item.DocumentId == secondTitleSource.DocumentId);
        using var secondSourceDetails = JsonDocument.Parse(
            secondSourceEvent.DetailsJson);
        Assert.Equal(
            1,
            secondSourceDetails.RootElement
                .GetProperty("rewrittenLinks")
                .GetInt32());

        var correlationKey = Guid.Parse(
            Assert.IsType<string>(correlationId));
        var correlated = await fixture.Service.GetAuditPageAsync(
            new VaultAuditQuery(
                CorrelationId: correlationKey,
                Limit: 100));
        Assert.Equal(3, correlated.Items.Count);
        Assert.All(
            correlated.Items,
            item => Assert.Equal(correlationKey, item.CorrelationId));

        var filtered = await fixture.Service.GetAuditPageAsync(
            new VaultAuditQuery(
                DocumentId: titleSource.DocumentId,
                Action: "backlink_rewrite",
                Limit: 100));
        Assert.Single(filtered.Items);
        Assert.Equal(correlationKey, filtered.Items[0].CorrelationId);
    }

    [Fact]
    public async Task RenameWithoutBacklinkSyncKeepsReferences()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "旧目标",
            null,
            "md",
            "# 旧目标",
            null,
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "[[旧目标]]",
            null,
            VaultActorKind.UserUi);

        await fixture.Service.UpdateNoteAsync(
            target.DocumentId,
            target.Revision,
            new VaultNotePatch(Title: "新目标"),
            VaultActorKind.UserUi);
        var unchanged = await fixture.Service.ReadNoteAsync(
            source.DocumentId,
            0,
            16_000);
        var map = await fixture.Service.GetDocumentMapAsync(source.DocumentId);

        Assert.Equal(1, unchanged.Revision);
        Assert.Equal("[[旧目标]]", unchanged.Content);
        Assert.Equal(target.DocumentId, Assert.Single(map.OutgoingLinks).Target?.DocumentId);
    }

    [Fact]
    public async Task SynchronizedRenameRejectsUnsafeWikiTitle()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "安全标题",
            null,
            "md",
            "# 安全标题",
            null,
            VaultActorKind.UserUi);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UpdateNoteAsync(
                target.DocumentId,
                target.Revision,
                new VaultNotePatch(
                    Title: "bad#title",
                    UpdateBacklinks: true),
                VaultActorKind.UserUi));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        var read = await fixture.Service.ReadNoteAsync(target.DocumentId, 0, 16_000);
        Assert.Equal("安全标题", read.Title);
        Assert.Equal(1, read.Revision);
    }

    [Fact]
    public async Task DuplicateTitlesProduceAmbiguousCandidates()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        await fixture.Service.CreateNoteAsync(
            "重复",
            "a",
            "md",
            "# A",
            null,
            VaultActorKind.UserUi);
        await fixture.Service.CreateNoteAsync(
            "重复",
            "b",
            "md",
            "# B",
            null,
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "[[重复]]",
            null,
            VaultActorKind.UserUi);

        var map = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        var link = Assert.Single(map.OutgoingLinks);
        Assert.Equal("ambiguous", link.ResolutionStatus);
        Assert.Null(link.Target);
        Assert.Equal(2, link.Candidates.Count);
    }

    [Fact]
    public async Task PurgeMakesLinksUnresolvedAndCreatingAgainRebinds()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "目标",
            null,
            "md",
            "# 目标",
            null,
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "[[目标]]",
            null,
            VaultActorKind.UserUi);

        await fixture.Service.DeleteNoteAsync(
            target.DocumentId,
            target.Revision,
            VaultActorKind.UserUi);
        var trashed = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal("unresolved", Assert.Single(trashed.OutgoingLinks).ResolutionStatus);

        var restored = await fixture.Service.RestoreTrashAsync(
            target.DocumentId,
            target.Revision,
            null,
            VaultActorKind.UserUi);
        var restoredMap = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(
            target.DocumentId,
            Assert.Single(restoredMap.OutgoingLinks).Target?.DocumentId);

        await fixture.Service.DeleteNoteAsync(
            target.DocumentId,
            restored.Revision,
            VaultActorKind.UserUi);
        await fixture.Service.PurgeNoteAsync(
            target.DocumentId,
            VaultActorKind.UserUi);

        var unresolved = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal("unresolved", Assert.Single(unresolved.OutgoingLinks).ResolutionStatus);

        var replacement = await fixture.Service.CreateNoteAsync(
            "目标",
            null,
            "md",
            "# 新目标",
            null,
            VaultActorKind.UserUi);
        var rebound = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(replacement.DocumentId, Assert.Single(rebound.OutgoingLinks).Target?.DocumentId);
    }

    [Fact]
    public async Task PatchSupportsDryRunHeadingsAndFrontmatter()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "项目",
            null,
            "md",
            """
            ---
            status: open # current
            items:
            - one # first
            ---
            # 项目
            正文

            ## 日志
            旧内容
            """,
            null,
            VaultActorKind.UserUi);

        var dryRun = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "heading",
                ["项目", "日志"],
                null,
                "append",
                "- 新内容",
                null,
                true),
            VaultActorKind.UserUi);
        Assert.True(dryRun.Changed);
        Assert.Equal(created.Revision, dryRun.Revision);

        var appended = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "heading",
                ["项目", "日志"],
                null,
                "append",
                "- 新内容",
                null,
                false),
            VaultActorKind.UserUi);
        Assert.Equal(created.Revision + 1, appended.Revision);

        var status = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            appended.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "status",
                "replace",
                null,
                JsonSerializer.SerializeToElement("done"),
                false),
            VaultActorKind.UserUi);

        var items = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            status.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "items",
                "append",
                null,
                JsonSerializer.SerializeToElement("two"),
                false),
            VaultActorKind.UserUi);

        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Contains("- 新内容", read.Content, StringComparison.Ordinal);
        Assert.Contains("status: done", read.Content, StringComparison.Ordinal);
        Assert.Contains("- two", read.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("# current", read.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("# first", read.Content, StringComparison.Ordinal);
        Assert.Equal(items.Revision, read.Revision);
        var map = await fixture.Service.GetDocumentMapAsync(created.DocumentId);
        Assert.Contains(
            map.FrontMatterFields,
            field => field.Key == "status" && field.ValueJson == "\"done\"");
        Assert.Contains(
            map.Headings,
            heading => heading.Path.SequenceEqual(["项目", "日志"]));

        var conflict = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.PatchNoteAsync(
                created.DocumentId,
                created.Revision,
                new VaultNotePatchCommand(
                    "heading",
                    ["项目", "日志"],
                    null,
                    "append",
                    "- stale",
                    null,
                    false),
                VaultActorKind.UserUi));
        Assert.Equal(VaultErrorCode.Conflict, conflict.Code);
    }

    [Fact]
    public async Task PatchingFrontMatterPreservesIndependentComment()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "注释保留",
            null,
            "md",
            """
            ---
            title: CipherVault
            # 这个注释用于说明下面的 tags
            tags: [Security, MCP]
            ---
            # 注释保留
            """,
            null,
            VaultActorKind.UserUi);

        await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "title",
                "replace",
                null,
                JsonSerializer.SerializeToElement("Updated"),
                false),
            VaultActorKind.UserUi);

        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Equal(
            "---\n" +
            "title: Updated\n" +
            "# 这个注释用于说明下面的 tags\n" +
            "tags: [Security, MCP]\n" +
            "---\n" +
            "# 注释保留",
            read.Content);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 10);
        var item = Assert.Single(catalog.Items);
        Assert.Contains("Security", item.Tags);
        Assert.Contains("MCP", item.Tags);
    }

    [Fact]
    public async Task PatchingBlockScalarPreservesFollowingComment()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "块标量注释保留",
            null,
            "md",
            """
            ---
            description: |
              第一行说明
              ---
              第二行说明
            # 这个注释用于说明下面的 tags
            tags: [Security]
            ---
            # 块标量注释保留
            """,
            null,
            VaultActorKind.UserUi);

        await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "description",
                "replace",
                null,
                JsonSerializer.SerializeToElement("Updated"),
                false),
            VaultActorKind.UserUi);

        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Equal(
            "---\n" +
            "description: Updated\n" +
            "# 这个注释用于说明下面的 tags\n" +
            "tags: [Security]\n" +
            "---\n" +
            "# 块标量注释保留",
            read.Content);
    }

    [Fact]
    public async Task PatchingMultilineFlowArrayKeepsClosingBracket()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "多行数组",
            null,
            "md",
            """
            ---
            items: [
              one,
              two
            ]
            # 这个注释用于说明下面的 tags
            tags: [Security]
            ---
            # 多行数组
            """,
            null,
            VaultActorKind.UserUi);

        await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "items",
                "replace",
                null,
                JsonSerializer.SerializeToElement(new[] { "three" }),
                false),
            VaultActorKind.UserUi);

        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Equal(
            "---\n" +
            "items:\n" +
            "- three\n" +
            "# 这个注释用于说明下面的 tags\n" +
            "tags: [Security]\n" +
            "---\n" +
            "# 多行数组",
            read.Content);
    }

    [Fact]
    public async Task HeadingPatchIgnoresLanguageLikeFenceClosingLine()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "围栏标题",
            null,
            "md",
            """
            # 主标题
            ```csharp
            # 代码内一级标题
            ```text
            ## 代码内二级标题
            ```
            ## 真实标题
            正文
            """,
            null,
            VaultActorKind.UserUi);

        var notFound = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.PatchNoteAsync(
                created.DocumentId,
                created.Revision,
                new VaultNotePatchCommand(
                    "heading",
                    ["主标题", "代码内二级标题"],
                    null,
                    "append",
                    "- 不应写入",
                    null,
                    false),
                VaultActorKind.UserUi));
        Assert.Equal(VaultErrorCode.PatchTargetNotFound, notFound.Code);

        var patched = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "heading",
                ["主标题", "真实标题"],
                null,
                "append",
                "- 已写入",
                null,
                false),
            VaultActorKind.UserUi);
        Assert.Equal(created.Revision + 1, patched.Revision);
        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Contains("- 已写入", read.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrontMatterTagsDriveTagIndexAcrossCreateAndUpdate()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "标签同步",
            null,
            "md",
            """
            ---
            tags: [Java, Backend] # original
            ---
            # 标签同步
            """,
            null,
            VaultActorKind.UserUi);

        var initialTags = await fixture.Service.ListTagsAsync(null, 50);
        Assert.Contains(initialTags.Items, tag => tag.Name == "Java");
        Assert.Contains(initialTags.Items, tag => tag.Name == "Backend");

        await fixture.Service.UpdateNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatch(
                Content: "---\r\n" +
                    "tags:\r\n" +
                    "- Security\r\n" +
                    "- MCP\r\n" +
                    "---\r\n" +
                    "# 标签同步"),
            VaultActorKind.UserUi);

        var updatedTags = await fixture.Service.ListTagsAsync(null, 50);
        Assert.DoesNotContain(updatedTags.Items, tag => tag.Name == "Java");
        Assert.DoesNotContain(updatedTags.Items, tag => tag.Name == "Backend");
        Assert.Contains(updatedTags.Items, tag => tag.Name == "Security");
        Assert.Contains(updatedTags.Items, tag => tag.Name == "MCP");
        Assert.True(
            (await fixture.Service.VerifyVaultAsync()).Valid);
    }

    [Fact]
    public async Task FrontMatterBlockScalarTagsDriveIndexAndIntegrity()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "块标量标签",
            null,
            "md",
            """
            ---
            description: |
              第一行说明
              ---
              第二行说明
            tags:
              - Security
              - MCP
            ---
            # 块标量标签
            """,
            null,
            VaultActorKind.UserUi);

        var tags = await fixture.Service.ListTagsAsync(null, 50);
        Assert.Contains(tags.Items, tag => tag.Name == "Security");
        Assert.Contains(tags.Items, tag => tag.Name == "MCP");
        var map = await fixture.Service.GetDocumentMapAsync(created.DocumentId);
        Assert.Contains(
            map.FrontMatterFields,
            field => field.Key == "description");
        Assert.Contains(
            map.FrontMatterFields,
            field => field.Key == "tags");
        Assert.True(
            (await fixture.Service.VerifyVaultAsync()).Valid);
    }

    [Fact]
    public async Task PatchingFrontMatterTagsReplacesAndClearsIndex()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "标签修改",
            null,
            "md",
            """
            ---
            tags: [Java, Backend]
            ---
            # 标签修改
            """,
            null,
            VaultActorKind.UserUi);

        var replaced = await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            created.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "tags",
                "replace",
                null,
                JsonSerializer.SerializeToElement(new[] { "Security", "MCP" }),
                false),
            VaultActorKind.UserUi);
        var catalogAfterReplace = await fixture.Service.ListCatalogAsync(
            null,
            null,
            10);
        var replacedItem = Assert.Single(
            catalogAfterReplace.Items,
            item => item.DocumentId == created.DocumentId);
        Assert.Contains("Security", replacedItem.Tags);
        Assert.Contains("MCP", replacedItem.Tags);
        Assert.DoesNotContain("Java", replacedItem.Tags);
        Assert.DoesNotContain("Backend", replacedItem.Tags);
        var replacedContent = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.DoesNotContain(
            "# original",
            replacedContent.Content,
            StringComparison.Ordinal);

        await fixture.Service.PatchNoteAsync(
            created.DocumentId,
            replaced.Revision,
            new VaultNotePatchCommand(
                "frontmatter",
                null,
                "tags",
                "delete",
                null,
                null,
                false),
            VaultActorKind.UserUi);
        var catalogAfterDelete = await fixture.Service.ListCatalogAsync(
            null,
            null,
            10);
        var deletedItem = Assert.Single(
            catalogAfterDelete.Items,
            item => item.DocumentId == created.DocumentId);
        Assert.Empty(deletedItem.Tags);
        Assert.Empty((await fixture.Service.ListTagsAsync(null, 50)).Items);
        Assert.True(
            (await fixture.Service.VerifyVaultAsync()).Valid);
    }

    [Fact]
    public async Task PatchingFrontMatterRejectsNestedArrayValues()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "嵌套字段",
            null,
            "md",
            """
            ---
            items: [one]
            ---
            # 嵌套字段
            """,
            null,
            VaultActorKind.UserUi);
        var attempts = new (string Operation, JsonElement Value)[]
        {
            (
                "replace",
                JsonSerializer.SerializeToElement(
                    new[] { new { name = "first", enabled = true } })),
            (
                "replace",
                JsonSerializer.SerializeToElement(
                    new[] { new[] { "nested" } })),
            (
                "append",
                JsonSerializer.SerializeToElement(
                    new { name = "appended", enabled = false }))
        };

        foreach (var (operation, value) in attempts)
        {
            var exception = await Assert.ThrowsAsync<VaultException>(() =>
                fixture.Service.PatchNoteAsync(
                    created.DocumentId,
                    created.Revision,
                    new VaultNotePatchCommand(
                        "frontmatter",
                        null,
                        "items",
                        operation,
                        null,
                        value,
                        false),
                    VaultActorKind.UserUi));
            Assert.Equal(
                VaultErrorCode.FrontMatterInvalid,
                exception.Code);
        }

        var read = await fixture.Service.ReadNoteAsync(
            created.DocumentId,
            0,
            16_000);
        Assert.Equal(created.Revision, read.Revision);
        Assert.Equal(
            "---\nitems: [one]\n---\n# 嵌套字段",
            read.Content);
    }

    [Fact]
    public async Task ExplicitTagsMustMatchMarkdownFrontMatterTags()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.CreateNoteAsync(
                "冲突标签",
                null,
                "md",
                """
                ---
                tags: [Java]
                ---
                # 冲突标签
                """,
                ["Security"],
                VaultActorKind.UserUi));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        Assert.Empty((await fixture.Service.ListCatalogAsync(
            null,
            null,
            10)).Items);
    }

    [Fact]
    public async Task AppendingFrontMatterToEmptyDocumentUpdatesTags()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "追加标签",
            null,
            "md",
            string.Empty,
            null,
            VaultActorKind.UserUi);

        await fixture.Service.AppendNoteAsync(
            created.DocumentId,
            created.Revision,
            """
            ---
            tags: [Appended]
            ---
            # 追加标签
            """,
            VaultActorKind.UserUi);

        var catalog = await fixture.Service.ListCatalogAsync(
            null,
            null,
            10);
        var item = Assert.Single(
            catalog.Items,
            candidate => candidate.DocumentId == created.DocumentId);
        Assert.Contains("Appended", item.Tags);
        Assert.True(
            (await fixture.Service.VerifyVaultAsync()).Valid);
    }

    [Fact]
    public async Task IntegrityCheckDetectsTagDerivedDataDrift()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "标签完整性",
            null,
            "md",
            """
            ---
            tags: [Java]
            ---
            # 标签完整性
            """,
            null,
            VaultActorKind.UserUi);
        var documentId = created.DocumentId.ToString("D").ToLowerInvariant();
        var baseline = await fixture.Service.VerifyVaultAsync();
        Assert.True(
            baseline.Valid,
            string.Join("; ", baseline.Problems));

        await fixture.ExecuteDatabaseSqlAsync(
            $"""
             UPDATE documents
             SET tags_json = '["Security"]'
             WHERE document_id = '{documentId}';
             UPDATE document_revisions
             SET tags_json = '["Backend"]'
             WHERE document_id = '{documentId}';
             UPDATE documents_fts
             SET tags = '["Other"]'
             WHERE document_id = '{documentId}';
             """);

        var damaged = await fixture.Service.VerifyVaultAsync();
        Assert.False(damaged.Valid);
        Assert.Contains(
            $"document_frontmatter_tags_mismatch={documentId}",
            damaged.Problems);
        Assert.Contains(
            $"document_tag_relation_mismatch={documentId}",
            damaged.Problems);
        Assert.Contains(
            $"fts_tags_mismatch={documentId}",
            damaged.Problems);
        Assert.Contains(
            damaged.Problems,
            problem => problem.StartsWith(
                "revision_frontmatter_tags_mismatch=",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntegrityCheckDetectsMissingTagRelation()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "标签关系",
            null,
            "md",
            "# 标签关系",
            ["Java"],
            VaultActorKind.UserUi);
        var documentId = created.DocumentId.ToString("D").ToLowerInvariant();
        var baseline = await fixture.Service.VerifyVaultAsync();
        Assert.True(
            baseline.Valid,
            string.Join("; ", baseline.Problems));

        await fixture.ExecuteDatabaseSqlAsync(
            $"""
             DELETE FROM document_tags
             WHERE document_id = '{documentId}';
             """);

        var damaged = await fixture.Service.VerifyVaultAsync();
        Assert.False(damaged.Valid);
        Assert.Contains(
            $"document_tag_relation_mismatch={documentId}",
            damaged.Problems);
    }

    [Fact]
    public async Task IntegrityCheckDetectsFtsFieldAndRowDrift()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var created = await fixture.Service.CreateNoteAsync(
            "FTS 完整性",
            null,
            "md",
            "# FTS 完整性\noriginal searchable content",
            null,
            VaultActorKind.UserUi);
        var documentId = created.DocumentId.ToString("D").ToLowerInvariant();
        const string unexpectedId = "99999999-9999-9999-9999-999999999999";
        var baseline = await fixture.Service.VerifyVaultAsync();
        Assert.True(
            baseline.Valid,
            string.Join("; ", baseline.Problems));

        await fixture.ExecuteDatabaseSqlAsync(
            $"""
             UPDATE documents_fts
             SET content = 'stale searchable content'
             WHERE document_id = '{documentId}';
             INSERT INTO documents_fts(
                 document_id, title, source_path, headings, tags, content)
             VALUES(
                 '{unexpectedId}', 'unexpected', 'unexpected.md',
                 '[]', '[]', 'unexpected content');
             """);

        var damaged = await fixture.Service.VerifyVaultAsync();
        Assert.False(damaged.Valid);
        Assert.Contains(
            $"fts_content_mismatch={documentId}",
            damaged.Problems);
        Assert.Contains(
            $"fts_row_unexpected={unexpectedId}",
            damaged.Problems);
    }

    [Fact]
    public async Task LocalGraphAppliesAndTagFilter()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "目标",
            null,
            "md",
            "# 目标",
            ["b"],
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "来源",
            null,
            "md",
            "[[目标]]",
            ["a"],
            VaultActorKind.UserUi);

        var graph = await fixture.Service.GetLocalGraphAsync(
            new VaultLocalGraphQuery(
                source.DocumentId,
                2,
                ["a"],
                null,
                200,
                500));

        var node = Assert.Single(graph.Nodes);
        Assert.Equal(source.DocumentId, node.DocumentId);
        Assert.Empty(graph.Edges);
        Assert.NotEqual(target.DocumentId, node.DocumentId);
    }

    [Fact]
    public async Task ExcessiveLinkIndexRollsBackDocumentCreation()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var content = string.Join(
            ' ',
            Enumerable.Repeat("[[target]]", 4_000));

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.CreateNoteAsync(
                "oversized-links",
                null,
                "md",
                content,
                null,
                VaultActorKind.UserUi));

        Assert.Equal(VaultErrorCode.LinkIndexTooLarge, exception.Code);
        var catalog = await fixture.Service.ListCatalogAsync(null, null, 100);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task InvalidBacklinkCursorReturnsDomainError()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var document = await fixture.Service.CreateNoteAsync(
            "cursor-target",
            null,
            "md",
            "# cursor-target",
            null,
            VaultActorKind.UserUi);

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ListBacklinksAsync(
                document.DocumentId,
                "not-a-valid-cursor",
                50));

        Assert.Equal(VaultErrorCode.InvalidCursor, exception.Code);
    }

    [Fact]
    public async Task TagPaginationAndGraphTruncationAreDeterministic()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var third = await fixture.Service.CreateNoteAsync(
            "third",
            "other",
            "md",
            "# third",
            ["beta"],
            VaultActorKind.UserUi);
        var second = await fixture.Service.CreateNoteAsync(
            "second",
            null,
            "md",
            "[[third]]",
            ["beta"],
            VaultActorKind.UserUi);
        var first = await fixture.Service.CreateNoteAsync(
            "first",
            "team",
            "md",
            "[[second]]",
            ["alpha"],
            VaultActorKind.UserUi);

        var firstTagPage = await fixture.Service.ListTagsAsync(null, 1);
        var secondTagPage = await fixture.Service.ListTagsAsync(
            firstTagPage.NextCursor,
            1);
        Assert.Single(firstTagPage.Items);
        Assert.Single(secondTagPage.Items);
        Assert.NotEqual(
            firstTagPage.Items[0].Name,
            secondTagPage.Items[0].Name);

        var truncated = await fixture.Service.GetLocalGraphAsync(
            new VaultLocalGraphQuery(
                first.DocumentId,
                2,
                [],
                null,
                1,
                500));
        var onlyNode = Assert.Single(truncated.Nodes);
        Assert.Equal(first.DocumentId, onlyNode.DocumentId);
        Assert.True(truncated.Truncated);

        var folderFiltered = await fixture.Service.GetLocalGraphAsync(
            new VaultLocalGraphQuery(
                first.DocumentId,
                2,
                [],
                "team",
                200,
                500));
        Assert.Equal(
            first.DocumentId,
            Assert.Single(folderFiltered.Nodes).DocumentId);
        Assert.Empty(folderFiltered.Edges);
        Assert.NotEqual(second.DocumentId, third.DocumentId);
    }

    [Fact]
    public async Task LinkIndexRebuildRepairsStaleAndMissingRows()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var target = await fixture.Service.CreateNoteAsync(
            "rebuild-target",
            null,
            "md",
            "# rebuild-target",
            null,
            VaultActorKind.UserUi);
        var source = await fixture.Service.CreateNoteAsync(
            "rebuild-source",
            null,
            "md",
            "[[rebuild-target]]",
            null,
            VaultActorKind.UserUi);

        await fixture.ExecuteDatabaseSqlAsync(
            """
            DELETE FROM document_links;
            UPDATE documents SET links_indexed_revision = 0;
            """);
        var damaged = await fixture.Service.VerifyVaultAsync();
        Assert.False(damaged.Valid);
        Assert.Contains(
            damaged.Problems,
            problem => problem.StartsWith(
                "link_index_stale=",
                StringComparison.Ordinal));

        var rebuilt = await fixture.Service.RebuildLinkIndexAsync(
            VaultActorKind.UserUi);
        Assert.Equal(2, rebuilt.DocumentsIndexed);
        Assert.Equal(1, rebuilt.LinksIndexed);
        Assert.True(rebuilt.AuditRecorded);

        var verified = await fixture.Service.VerifyVaultAsync();
        Assert.True(verified.Valid, string.Join("; ", verified.Problems));
        var map = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(
            target.DocumentId,
            Assert.Single(map.OutgoingLinks).Target?.DocumentId);
    }

    [Fact]
    public async Task BatchResolverHandlesMoreThanOneQueryChunk()
    {
        await using var fixture = await KnowledgeFixture.CreateAsync();
        var content = string.Join(
            ' ',
            Enumerable.Range(0, 401)
                .Select(index => $"[[missing-{index:D3}]]"));
        var source = await fixture.Service.CreateNoteAsync(
            "chunked-links",
            null,
            "md",
            content,
            null,
            VaultActorKind.UserUi);

        var map = await fixture.Service.GetDocumentMapAsync(source.DocumentId);
        Assert.Equal(401, map.OutgoingLinks.Count);
        Assert.All(
            map.OutgoingLinks,
            link => Assert.Equal("unresolved", link.ResolutionStatus));
    }

    private sealed class KnowledgeFixture : IAsyncDisposable
    {
        private const string Password = "knowledge-graph-password";
        private readonly string _root;

        private KnowledgeFixture(
            string root,
            string vaultPath,
            VaultService service)
        {
            _root = root;
            VaultPath = vaultPath;
            Service = service;
        }

        public string VaultPath { get; }
        public VaultService Service { get; }

        public async Task ExecuteDatabaseSqlAsync(string sql)
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
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material.MasterKey);
                CryptographicOperations.ZeroMemory(material.DbKey);
            }
        }

        public static async Task<KnowledgeFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-knowledge-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var vaultPath = Path.Combine(root, "vault");
            var service = new VaultService(new AlwaysActiveActivityProvider());
            try
            {
                var initialized = await service.InitializeAsync(
                    vaultPath,
                    Password);
                await service.ConfirmRecoveryKeyAsync(
                    vaultPath,
                    initialized.RecoveryKey,
                    VaultActorKind.UserUi);
                await service.UnlockAsync(
                    vaultPath,
                    Password,
                    VaultCredentialType.Password);
                return new KnowledgeFixture(root, vaultPath, service);
            }
            catch
            {
                await service.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class AlwaysActiveActivityProvider : ISystemActivityProvider
    {
        public SystemActivitySnapshot GetSnapshot()
        {
            return new SystemActivitySnapshot(TimeSpan.Zero, false);
        }
    }
}
