using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class KnowledgeMaturityPatchTests
{
    [Fact]
    public async Task SetAddsMissingMaturityAndWritesRedactedAudit()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Markdown,
            "status: active");

        var result = await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            SetCommand("raw"),
            actor);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Revision);
        Assert.NotNull(result.AuditEventId);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        var maturity = Assert.Single(
            map.FrontMatterFields,
            field => field.Key == "maturity");
        Assert.Equal("string", maturity.ValueType);
        Assert.Equal("\"raw\"", maturity.ValueJson);

        var auditDetails = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT details_json
                    FROM audit_events
                    WHERE event_id = $eventId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$eventId",
                    result.AuditEventId!.Value.ToString("D"));
                return Convert.ToString(
                    await command.Command.ExecuteScalarAsync()) ?? "{}";
            });
        using var json = JsonDocument.Parse(auditDetails);
        Assert.Equal(
            "frontmatter",
            json.RootElement.GetProperty("patchTargetKind").GetString());
        Assert.Equal(
            "maturity",
            json.RootElement.GetProperty("frontMatterKey").GetString());
        Assert.Equal(
            "set",
            json.RootElement.GetProperty("patchOperation").GetString());
        Assert.DoesNotContain("raw", auditDetails, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetReplacesNonStringAndDeleteRemovesMaturity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Markdown,
            "maturity: 7");

        var set = await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            SetCommand("verified"),
            actor);
        Assert.True(set.Changed);
        Assert.Equal(2, set.Revision);
        var map = await fixture.Documents.GetMapAsync(created.DocumentId);
        var maturity = Assert.Single(
            map.FrontMatterFields,
            field => field.Key == "maturity");
        Assert.Equal("string", maturity.ValueType);
        Assert.Equal("\"verified\"", maturity.ValueJson);

        var deleted = await fixture.Documents.PatchAsync(
            created.DocumentId,
            2,
            new DocumentPatchCommand(
                VaultPatchTargetKind.FrontMatter,
                null,
                "maturity",
                VaultPatchOperation.Delete,
                null,
                null,
                DryRun: false),
            actor);
        Assert.True(deleted.Changed);
        Assert.Equal(3, deleted.Revision);
        map = await fixture.Documents.GetMapAsync(created.DocumentId);
        Assert.DoesNotContain(
            map.FrontMatterFields,
            field => field.Key == "maturity");
    }

    [Fact]
    public async Task SetIsNoOpWhenValueIsIdentical()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Markdown,
            "maturity: raw");

        var result = await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            SetCommand("raw"),
            actor);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Revision);
        Assert.Null(result.AuditEventId);
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("")]
    public async Task SetRejectsNonStandardValues(string value)
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Markdown,
            "status: active");

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.PatchAsync(
                created.DocumentId,
                1,
                SetCommand(value),
                actor));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
    }

    [Fact]
    public async Task SetRejectsTextOtherKeyAndStaleRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var text = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Text,
            null);
        var markdown = await CreateAsync(
            fixture,
            actor,
            VaultDocumentFormat.Markdown,
            "status: active");

        var textError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.PatchAsync(
                text.DocumentId,
                1,
                SetCommand("raw"),
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, textError.Code);

        var keyError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.PatchAsync(
                markdown.DocumentId,
                1,
                SetCommand("raw") with
                {
                    FrontMatterKey = "status"
                },
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, keyError.Code);

        var contentError = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.PatchAsync(
                markdown.DocumentId,
                1,
                SetCommand("raw") with
                {
                    ContentMarkdown = "not allowed"
                },
                actor));
        Assert.Equal(VaultErrorCode.InvalidInput, contentError.Code);

        var stale = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.PatchAsync(
                markdown.DocumentId,
                999,
                SetCommand("raw"),
                actor));
        Assert.Equal(VaultErrorCode.RevisionConflict, stale.Code);
    }

    private static DocumentPatchCommand SetCommand(string value) =>
        new(
            VaultPatchTargetKind.FrontMatter,
            null,
            "maturity",
            VaultPatchOperation.Set,
            null,
            JsonSerializer.SerializeToElement(value),
            DryRun: false);

    private static async Task<DocumentWriteResult> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        VaultDocumentFormat format,
        string? frontMatter)
    {
        var content = format == VaultDocumentFormat.Text
            ? "Plain text."
            : frontMatter is null
                ? "# Markdown body"
                : $"""
                   ---
                   {frontMatter}
                   ---

                   # Markdown body
                   """;
        return await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Maturity Patch",
                null,
                null,
                format,
                content,
                null),
            actor);
    }
}
