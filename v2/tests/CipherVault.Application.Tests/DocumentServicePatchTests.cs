using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServicePatchTests
{
    [Theory]
    [InlineData("replace", true)]
    [InlineData("replace", false)]
    [InlineData("delete", true)]
    [InlineData("delete", false)]
    public async Task IdentityPatchIsAlwaysProtected(
        string operation,
        bool dryRun)
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Identity",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "Body",
                null),
            actor);
        var command = new DocumentPatchCommand(
            VaultPatchTargetKind.FrontMatter,
            null,
            "id",
            operation == "delete"
                ? VaultPatchOperation.Delete
                : VaultPatchOperation.Replace,
            null,
            operation == "delete"
                ? null
                : System.Text.Json.JsonSerializer.SerializeToElement(
                    Guid.NewGuid().ToString("D")),
            dryRun);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.PatchAsync(
                created.DocumentId,
                1,
                command,
                actor));

        Assert.Equal(VaultErrorCode.DocumentIdProtected, exception.Code);
        Assert.Equal(
            1,
            (await fixture.Documents.GetAsync(created.DocumentId)).Revision);
    }

    [Fact]
    public async Task DryRunDoesNotWriteAndRealPatchAdvancesRevision()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Patch",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Root\nOld",
                null),
            actor);
        var command = new DocumentPatchCommand(
            VaultPatchTargetKind.Heading,
            ["Root"],
            null,
            VaultPatchOperation.Replace,
            "New",
            null,
            DryRun: true);

        var dryRun = await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            command,
            actor);
        Assert.True(dryRun.DryRun);
        Assert.True(dryRun.Changed);
        Assert.Equal(1, (await fixture.Documents.GetAsync(created.DocumentId)).Revision);

        var real = await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            command with { DryRun = false },
            actor);
        Assert.Equal(2, real.Revision);
        Assert.NotNull(real.AuditEventId);
        var content = await fixture.Documents.ReadContentAsync(created.DocumentId, 0, 100);
        Assert.Contains("New", content.Content, StringComparison.Ordinal);

        var conflict = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Documents.PatchAsync(
                created.DocumentId,
                1,
                command with { DryRun = false },
                actor));
        Assert.Equal(VaultErrorCode.RevisionConflict, conflict.Code);
    }

    [Fact]
    public async Task FrontMatterPatchUpdatesAuthoritativeTags()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var actor = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Tags",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags: [Old]\n---\nbody",
                null),
            actor);
        var command = new DocumentPatchCommand(
            VaultPatchTargetKind.FrontMatter,
            null,
            "tags",
            VaultPatchOperation.Replace,
            null,
            JsonSerializer.SerializeToElement(new[] { "New" }),
            DryRun: false);

        await fixture.Documents.PatchAsync(
            created.DocumentId,
            1,
            command,
            actor);

        var metadata = await fixture.Documents.GetAsync(created.DocumentId);
        Assert.Equal(["New"], metadata.Tags);
    }
}
