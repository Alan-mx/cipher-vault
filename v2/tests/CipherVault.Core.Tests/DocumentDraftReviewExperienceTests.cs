using System.Text.Json;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class DocumentDraftReviewExperienceTests
{
    [Fact]
    public void ReviewContextIsPreservedReplacedAndCleared()
    {
        var create = Parse(
            """
            {
              "title": "Context Draft",
              "virtualPath": "notes/context.md",
              "format": "md",
              "content": "# Context",
              "tags": [],
              "reviewContext": {
                "summary": "Initial source",
                "sources": [
                  {
                    "documentId": "11111111-1111-4111-8111-111111111111",
                    "title": "Source",
                    "excerpt": "Source excerpt"
                  }
                ]
              }
            }
            """);
        var prepared = DocumentDraftPayloadEngine.PrepareCreatePayload(
            create,
            Guid.NewGuid());
        Assert.Equal(
            "Initial source",
            prepared.PersistedPayload
                .GetProperty("reviewContext")
                .GetProperty("summary")
                .GetString());

        var existing = prepared.PersistedPayload;
        var preserve = DocumentDraftPayloadEngine.PrepareUpdatePayload(
            Parse("""{"operations":[{"type":"append_body","content":"+more"}]}"""),
            existing);
        Assert.Equal(
            "Initial source",
            preserve.GetProperty("reviewContext").GetProperty("summary").GetString());

        var replace = DocumentDraftPayloadEngine.PrepareUpdatePayload(
            Parse(
                """
                {
                  "operations":[{"type":"append_body","content":"+more"}],
                  "reviewContext":{"summary":"Replacement","sources":[]}
                }
                """),
            existing);
        Assert.Equal(
            "Replacement",
            replace.GetProperty("reviewContext").GetProperty("summary").GetString());

        var clear = DocumentDraftPayloadEngine.PrepareUpdatePayload(
            Parse(
                """
                {
                  "operations":[{"type":"append_body","content":"+more"}],
                  "reviewContext":null
                }
                """),
            existing);
        Assert.False(clear.TryGetProperty("reviewContext", out _));

        var trash = DocumentDraftPayloadEngine.NormalizeTrashPayload(
            Parse("""{"reviewContext":{"summary":"Trash source"}}"""));
        Assert.Equal(
            "Trash source",
            trash.GetProperty("reviewContext").GetProperty("summary").GetString());
    }

    [Fact]
    public void PayloadValidationRejectsUnknownFieldsAndLimits()
    {
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.PrepareCreatePayload(
                Parse(
                    """
                    {
                      "title":"T",
                      "virtualPath":"t.md",
                      "format":"md",
                      "content":"body",
                      "tags":[],
                      "unknown":true
                    }
                    """),
                Guid.NewGuid()));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(
                Parse(
                    """
                    {
                      "operations":[
                        {"type":"append_body","content":"x","unknown":true}
                      ]
                    }
                    """)));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(
                Parse(
                    """
                    {
                      "operations":[{"type":"append_body","content":"x"}],
                      "reviewContext":{"summary":"x","unknown":true}
                    }
                    """)));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(
                Parse(
                    """
                    {
                      "operations":[{"type":"append_body","content":"x"}],
                      "reviewContext":{
                        "sources":[{"excerpt":"x","unknown":true}]
                      }
                    }
                    """)));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(
                Parse(
                    """
                    {
                      "operations":[{"type":"append_body","content":"x"}],
                      "reviewContext":{"sources":null}
                    }
                    """)));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.ValidateUpdatePayload(
                Parse(
                    $$"""
                    {
                      "operations":[{"type":"append_body","content":"x"}],
                      "reviewContext":{
                        "summary":"{{new string('x', 2_001)}}"
                      }
                    }
                    """)));
        AssertDraftOperationInvalid(() =>
            DocumentDraftPayloadEngine.NormalizeTrashPayload(
                Parse("""{"unknown":true}""")));
    }

    [Fact]
    public void StructuredDiffSeparatesFieldsAndDistinguishesMissingFromNull()
    {
        var baseSnapshot = Snapshot(
            """
            ---
            tags: [Redis, Work]
            status: raw
            nullable: null
            removed: null
            ---

            # Title
            Before
            """,
            ["Redis", "Work"]);
        var preview = new DocumentDraftWorkingCopy(
            baseSnapshot.DocumentId,
            "Title",
            baseSnapshot.VirtualPath,
            VaultDocumentFormat.Markdown,
            """
            ---
            tags: [work, redis]
            status: verified
            nullable: null
            newNull: null
            meta: {z: 1, a: 2}
            ---

            # Title
            After
            """,
            ["work", "redis"]);

        var changes = DocumentDraftStructuredDiffBuilder.Build(
            DocumentDraftType.Update,
            baseSnapshot,
            preview);

        Assert.DoesNotContain(
            changes.Changes,
            change => change.FieldKind == "tags");
        var meta = Assert.Single(
            changes.Changes,
            change => change.FieldKind == "frontmatter"
                && change.Key == "meta");
        Assert.False(meta.BeforeExists);
        Assert.Equal("object", meta.AfterValueType);
        Assert.Equal("""{"a":2,"z":1}""", meta.After);
        var removed = Assert.Single(
            changes.Changes,
            change => change.FieldKind == "frontmatter"
                && change.Key == "removed");
        Assert.True(removed.BeforeExists);
        Assert.Equal("null", removed.BeforeValueType);
        Assert.False(removed.AfterExists);
        Assert.DoesNotContain(
            changes.Changes,
            change => change.FieldKind == "frontmatter"
                && change.Key == "nullable");
        var newNull = Assert.Single(
            changes.Changes,
            change => change.FieldKind == "frontmatter"
                && change.Key == "newNull");
        Assert.False(newNull.BeforeExists);
        Assert.True(newNull.AfterExists);
        Assert.Equal("null", newNull.AfterValueType);
        var body = Assert.Single(
            changes.Changes,
            change => change.FieldKind == "body");
        Assert.DoesNotContain("status:", body.After, StringComparison.Ordinal);
        Assert.Contains("After", body.After, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredDiffCoversCreateTrashTruncationAndOrdering()
    {
        var create = new DocumentDraftWorkingCopy(
            Guid.NewGuid(),
            "Created",
            "notes/created.md",
            VaultDocumentFormat.Markdown,
            "---\nempty: []\n---\nBody",
            []);
        var created = DocumentDraftStructuredDiffBuilder.Build(
            DocumentDraftType.Create,
            null,
            create);
        Assert.Contains(
            created.Changes,
            change => change.FieldKind == "title" && !change.BeforeExists);
        Assert.Contains(
            created.Changes,
            change => change.FieldKind == "tags"
                && change.After == "[]");

        var trash = DocumentDraftStructuredDiffBuilder.Build(
            DocumentDraftType.Trash,
            Snapshot("Body"),
            new DocumentDraftWorkingCopy(
                Guid.NewGuid(),
                "Title",
                "notes/title.md",
                VaultDocumentFormat.Markdown,
                "Body",
                []));
        var trashChange = Assert.Single(trash.Changes);
        Assert.Equal("trash", trashChange.FieldKind);
        Assert.True(trashChange.BeforeExists);
        Assert.False(trashChange.AfterExists);

        var baseSnapshot = Snapshot("---\n---\nBody");
        var properties = new System.Text.StringBuilder();
        properties.AppendLine("---");
        properties.AppendLine($"aaa: \"{new string('x', 2_001)}\"");
        for (var index = 1; index <= 100; index++)
        {
            properties.AppendLine($"key{index:D3}: {index}");
        }
        properties.AppendLine("---");
        properties.Append("Body");
        var truncated = DocumentDraftStructuredDiffBuilder.Build(
            DocumentDraftType.Update,
            baseSnapshot,
            new DocumentDraftWorkingCopy(
                baseSnapshot.DocumentId,
                baseSnapshot.Title,
                baseSnapshot.VirtualPath,
                VaultDocumentFormat.Markdown,
                properties.ToString(),
                []));

        Assert.True(truncated.Truncated);
        Assert.Equal(100, truncated.Changes.Count);
        Assert.True(truncated.TotalChangeCount > 100);
        var longValue = Assert.Single(
            truncated.Changes,
            change => change.Key == "aaa");
        Assert.True(longValue.AfterTruncated);
        Assert.Equal(2_000, longValue.After!.EnumerateRunes().Count());
    }

    [Fact]
    public void OperationSummaryAndBodyEditCapabilityAreDeterministic()
    {
        var snapshot = Snapshot(
            "---\ntags: [Redis]\nstatus: raw\n---\n\n# Title\n");
        var payload = Parse(
            """
            {
              "operations": [
                {"type":"append_body","content":"Body"},
                {"type":"set_property","key":"status","value":"verified"},
                {"type":"add_tag","tag":"Cache"},
                {"type":"append_body","content":"Again"}
              ]
            }
            """);
        var summary = DocumentDraftPayloadEngine.GetOperationSummary(
            DocumentDraftType.Update,
            payload);
        Assert.Equal(4, summary.Total);
        Assert.Equal(
            [
                new DocumentDraftOperationCount("append_body", 2),
                new DocumentDraftOperationCount("set_property", 1),
                new DocumentDraftOperationCount("add_tag", 1)
            ],
            summary.Items);

        var draft = Record(DocumentDraftType.Update);
        var preview = DocumentDraftPayloadEngine.ComputePreview(
            draft,
            payload,
            snapshot);
        var capability = DocumentDraftPayloadEngine.GetBodyEditCapability(
            draft,
            payload,
            snapshot,
            preview.WorkingCopy);
        Assert.True(capability.CanSave);

        var unsafeSnapshot = Snapshot("---\n---\nBody");
        var unsafePayload = Parse(
            """
            {
              "operations": [
                {
                  "type":"replace_body",
                  "content":"---\ntags: [Redis]\n---\nBody"
                },
                {"type":"remove_tag","tag":"Redis"}
              ]
            }
            """);
        var unsafePreview = DocumentDraftPayloadEngine.ComputePreview(
            draft,
            unsafePayload,
            unsafeSnapshot);
        var unsafeCapability =
            DocumentDraftPayloadEngine.GetBodyEditCapability(
                draft,
                unsafePayload,
                unsafeSnapshot,
                unsafePreview.WorkingCopy);
        Assert.False(unsafeCapability.CanSave);
        Assert.Equal(
            "mixed_body_operation_unsafe",
            unsafeCapability.Reason);
    }

    [Fact]
    public void TrashDetailResultPassesDatabaseBoundaryGuard()
    {
        var now = DateTimeOffset.UtcNow;
        var detail = new DocumentDraftDetail(
            new DocumentDraftMetadata(
                Guid.NewGuid(),
                1,
                DocumentDraftType.Trash,
                DocumentDraftStatus.Draft,
                Guid.NewGuid(),
                1,
                null,
                "mcp",
                "Title",
                "notes/title.md",
                VaultDocumentFormat.Markdown,
                now,
                now,
                null,
                null,
                null),
            Parse("""{"reviewContext":{"summary":"x","sources":[]}}"""),
            new DocumentDraftReviewContext("x", []),
            new DocumentDraftPreview(
                "Body",
                [],
                string.Empty,
                false,
                0,
                new DocumentDraftStructuredChanges([], 0, false),
                new DocumentDraftOperationSummary(0, []),
                new DocumentDraftBodyEditCapability(false, "trash_not_editable")));

        VaultOperationResultGuard.ValidateResult(detail);
    }

    private static void AssertDraftOperationInvalid(Action action)
    {
        var exception = Assert.Throws<VaultException>(action);
        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
    }

    private static DocumentDraftSnapshot Snapshot(
        string content,
        IReadOnlyList<string>? tags = null) =>
        new(
            Guid.NewGuid(),
            1,
            "Title",
            "notes/title.md",
            VaultDocumentFormat.Markdown,
            content,
            tags ?? []);

    private static DocumentDraftRecord Record(DocumentDraftType type) =>
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

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
