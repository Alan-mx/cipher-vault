using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using CipherVault.Core.Errors;
using CipherVault.Core.KnowledgeBase;

namespace CipherVault.Application.Tests;

public sealed class KnowledgePayloadValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Guid NoteTypeId =
        Guid.Parse("00000000-0000-4000-8000-000000001001");
    private static readonly Guid SourceRuleId =
        Guid.Parse("00000000-0000-4000-8000-000000003001");

    [Fact]
    public void KnowledgeRejectsEnumValueOutsideModelOptions()
    {
        var payload = KnowledgePayload(
            "---\ntype: note\n---\n# Body",
            JsonSerializer.SerializeToElement(new { status = "green" }));

        var exception = Assert.Throws<VaultException>(() =>
            KnowledgeBasePayloadParser.ParseKnowledge(
                payload,
                Model()));

        Assert.Equal(VaultErrorCode.DraftOperationInvalid, exception.Code);
        Assert.Equal(
            "properties.status",
            exception.Details["fieldPath"]);
    }

    [Fact]
    public void KnowledgeValidatesMatchingFrontmatterAndRemovesIt()
    {
        var result = KnowledgeBasePayloadParser.ParseKnowledge(
            KnowledgePayload(
                "---\ntype: note\nstatus: red\n---\n# Body",
                JsonSerializer.SerializeToElement(new { status = "red" })),
            Model());

        Assert.Equal("# Body", result.ContentMarkdown);
    }

    [Fact]
    public void KnowledgeRejectsFrontmatterThatConflictsWithStructuredFields()
    {
        var exception = Assert.Throws<VaultException>(() =>
            KnowledgeBasePayloadParser.ParseKnowledge(
                KnowledgePayload(
                    "---\ntype: note\nstatus: blue\n---\n# Body",
                    JsonSerializer.SerializeToElement(new { status = "red" })),
                Model()));

        Assert.Equal(
            VaultErrorCode.FrontMatterModelMismatch,
            exception.Code);
        Assert.Equal("status", exception.Details["fieldPath"]);
    }

    [Fact]
    public void SourceMetadataRejectsUnknownAndMistypedFields()
    {
        var unknown = SourcePayload(
            JsonSerializer.SerializeToElement(
                new { url = "https://example.com", extra = true }));
        var unknownException = Assert.Throws<VaultException>(() =>
            KnowledgeBasePayloadParser.ParseSource(unknown, Model()));
        Assert.Equal(
            VaultErrorCode.SourceMetadataInvalid,
            unknownException.Code);
        Assert.Equal(
            "metadata.extra",
            unknownException.Details["fieldPath"]);

        var wrongType = SourcePayload(
            JsonSerializer.SerializeToElement(
                new { url = 42, author = "Ada" }));
        var typeException = Assert.Throws<VaultException>(() =>
            KnowledgeBasePayloadParser.ParseSource(wrongType, Model()));
        Assert.Equal(
            VaultErrorCode.SourceMetadataInvalid,
            typeException.Code);
        Assert.Equal("metadata.url", typeException.Details["fieldPath"]);
    }

    private static JsonElement KnowledgePayload(
        string content,
        JsonElement properties) =>
        JsonSerializer.SerializeToElement(
            new
            {
                title = "Knowledge",
                summary = "",
                knowledgeTypeId = NoteTypeId,
                templateId = (string?)null,
                contentMarkdown = content,
                properties,
                tags = Array.Empty<string>(),
                sourceReferences = Array.Empty<object>()
            },
            JsonOptions);

    private static JsonElement SourcePayload(JsonElement metadata) =>
        JsonSerializer.SerializeToElement(
            new
            {
                sourceKind = "web_page",
                title = "Source",
                metadata,
                contentFormat = "markdown",
                contentText = "# Source"
            },
            JsonOptions);

    private static KnowledgeModelSnapshot Model()
    {
        var note = new KnowledgeTypeDefinition(
            NoteTypeId,
            "note",
            "Note",
            null,
            true,
            10,
            [
                new KnowledgePropertyDefinition(
                    Guid.NewGuid(),
                    "status",
                    "Status",
                    "enum",
                    false,
                    null,
                    JsonSerializer.SerializeToElement(
                        new { options = new[] { "red", "blue" } }),
                    true,
                    10)
            ]);
        var sourceRule = new SourceRuleDefinition(
            SourceRuleId,
            SourceKind.WebPage,
            JsonSerializer.SerializeToElement(
                new
                {
                    required = new[] { "url" },
                    properties = new
                    {
                        url = new { type = "string" },
                        author = new { type = "string" }
                    },
                    additionalProperties = false
                }),
            [SourceLocatorType.Section],
            true);
        return new KnowledgeModelSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            [note],
            [],
            [],
            [sourceRule],
            []);
    }
}
