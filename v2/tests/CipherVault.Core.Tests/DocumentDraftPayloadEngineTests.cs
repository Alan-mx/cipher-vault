using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class DocumentDraftPayloadEngineTests
{
    [Fact]
    public void CreatePayloadNormalizesAndValidatesDocument()
    {
        var payload = Parse(
            """
            {
              "title": "  Redis 缓存设计  ",
              "virtualPath": "architecture/redis-cache.md",
              "folder": null,
              "format": "md",
              "content": "---\ntags:\n  - Redis\n---\n\n# Redis 缓存设计\n",
              "tags": ["Redis"]
            }
            """);

        var working = DocumentDraftPayloadEngine.ParseCreatePayload(payload);

        Assert.Equal("Redis 缓存设计", working.Title);
        Assert.Equal("architecture/redis-cache.md", working.VirtualPath);
        Assert.Equal(VaultDocumentFormat.Markdown, working.Format);
        Assert.Equal(["Redis"], working.Tags);
    }

    [Fact]
    public void UpdateOperationsApplyInArrayOrder()
    {
        var baseSnapshot = new DocumentDraftSnapshot(
            Guid.NewGuid(),
            4,
            "Cache",
            "notes/cache.md",
            VaultDocumentFormat.Markdown,
            "---\ntags:\n  - Redis\nstatus: raw\n---\n\n# Cache\n",
            ["Redis"]);
        var draft = CreateDraftRecord(DocumentDraftType.Update);
        var payload = Parse(
            """
            {
              "operations": [
                { "type": "append_body", "content": "\nBody\n" },
                { "type": "set_property", "key": "status", "value": "verified" },
                { "type": "add_tag", "tag": "Cache" },
                { "type": "remove_tag", "tag": "Redis" }
              ]
            }
            """);

        var result = DocumentDraftPayloadEngine.ComputePreview(
            draft,
            payload,
            baseSnapshot);

        Assert.Equal(4, result.OperationCount);
        Assert.Contains("status: verified", result.WorkingCopy.Content);
        Assert.Contains("Cache", result.WorkingCopy.Tags);
        Assert.DoesNotContain("Redis", result.WorkingCopy.Tags);
        Assert.Contains("+Body", result.UnifiedDiff);
    }

    [Fact]
    public void DuplicateTagRejectsWholePayload()
    {
        var baseSnapshot = new DocumentDraftSnapshot(
            Guid.NewGuid(),
            1,
            "Cache",
            "notes/cache.md",
            VaultDocumentFormat.Markdown,
            "---\ntags:\n  - Redis\n---\n\n# Cache\n",
            ["Redis"]);
        var payload = Parse(
            """
            {
              "operations": [
                { "type": "add_tag", "tag": "redis" }
              ]
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            DocumentDraftPayloadEngine.ComputePreview(
                CreateDraftRecord(DocumentDraftType.Update),
                payload,
                baseSnapshot));

        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
    }

    [Fact]
    public void RemovingMissingPropertyRejectsWholePayload()
    {
        var baseSnapshot = new DocumentDraftSnapshot(
            Guid.NewGuid(),
            1,
            "Cache",
            "notes/cache.md",
            VaultDocumentFormat.Markdown,
            "# Cache\n",
            []);
        var payload = Parse(
            """
            {
              "operations": [
                { "type": "remove_property", "key": "status" }
              ]
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            DocumentDraftPayloadEngine.ComputePreview(
                CreateDraftRecord(DocumentDraftType.Update),
                payload,
                baseSnapshot));

        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
    }

    [Fact]
    public void MoreThanThirtyTwoOperationsAreRejected()
    {
        var operations = string.Join(
            ",",
            Enumerable.Range(0, 33)
                .Select(index =>
                    $$"""{"type":"append_body","content":"{{index}}" }"""));
        var payload = Parse($$"""{"operations":[{{operations}}]}""");

        var exception = Assert.Throws<VaultException>(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(payload));

        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
    }

    [Fact]
    public void SnapshotRoundTripsAllFields()
    {
        var snapshot = new DocumentDraftSnapshot(
            Guid.NewGuid(),
            7,
            "Title",
            "folder/title.txt",
            VaultDocumentFormat.Text,
            "content",
            ["One", "Two"]);

        var json = DocumentDraftPayloadEngine.SerializeSnapshot(snapshot);
        var parsed = DocumentDraftPayloadEngine.ParseSnapshot(json);

        Assert.Equal(snapshot.DocumentId, parsed.DocumentId);
        Assert.Equal(snapshot.Revision, parsed.Revision);
        Assert.Equal(snapshot.Title, parsed.Title);
        Assert.Equal(snapshot.VirtualPath, parsed.VirtualPath);
        Assert.Equal(snapshot.Format, parsed.Format);
        Assert.Equal(snapshot.Content, parsed.Content);
        Assert.Equal(snapshot.Tags, parsed.Tags);
    }

    [Fact]
    public void PropertyObjectsAreRejected()
    {
        var payload = Parse(
            """
            {
              "operations": [
                {
                  "type": "set_property",
                  "key": "source",
                  "value": { "kind": "web" }
                }
              ]
            }
            """);

        var exception = Assert.Throws<VaultException>(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(payload));

        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
    }

    [Fact]
    public void AppendLargerThanAuthoringLimitIsRejected()
    {
        var payload = Parse(
            $$"""
            {
              "operations": [
                {
                  "type": "append_body",
                  "content": "{{new string('x', (256 * 1024) + 1)}}"
                }
              ]
            }
            """);
        var snapshot = new DocumentDraftSnapshot(
            Guid.NewGuid(),
            1,
            "Title",
            "notes/title.md",
            VaultDocumentFormat.Markdown,
            "# Title\n",
            []);

        var exception = Assert.Throws<VaultException>(() =>
            DocumentDraftPayloadEngine.ComputePreview(
                CreateDraftRecord(DocumentDraftType.Update),
                payload,
                snapshot));

        Assert.Equal(VaultErrorCode.DraftPayloadTooLarge, exception.Code);
    }

    [Fact]
    public void IdempotencyHashIgnoresObjectOrderAndPreservesArrayOrder()
    {
        var first = Parse("""{"a":1,"b":{"x":1,"y":2}}""");
        var reordered = Parse("""{"b":{"y":2,"x":1},"a":1}""");
        var different = Parse("""{"a":1,"b":[1,2]}""");
        var reorderedArray = Parse("""{"a":1,"b":[2,1]}""");

        var baseline = IdempotencyRequestHasher.Compute(
            IdempotencyOperation.DraftCreate,
            VaultActorKind.Mcp,
            null,
            first);
        Assert.Equal(
            baseline,
            IdempotencyRequestHasher.Compute(
                IdempotencyOperation.DraftCreate,
                VaultActorKind.Mcp,
                null,
                reordered));
        Assert.NotEqual(
            baseline,
            IdempotencyRequestHasher.Compute(
                IdempotencyOperation.DraftCreate,
                VaultActorKind.Mcp,
                null,
                different));
        Assert.NotEqual(
            IdempotencyRequestHasher.Compute(
                IdempotencyOperation.DraftCreate,
                VaultActorKind.Mcp,
                null,
                different),
            IdempotencyRequestHasher.Compute(
                IdempotencyOperation.DraftCreate,
                VaultActorKind.Mcp,
                null,
                reorderedArray));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DocumentDraftRecord CreateDraftRecord(
        DocumentDraftType type) =>
        new(
            Guid.NewGuid(),
            1,
            type,
            DocumentDraftStatus.Draft,
            type == DocumentDraftType.Create ? null : Guid.NewGuid(),
            type == DocumentDraftType.Create ? null : 1,
            1,
            "{}",
            type == DocumentDraftType.Create ? null : "{}",
            "mcp",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null);
}
