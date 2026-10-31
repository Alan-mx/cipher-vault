using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentServicePatchTests
{
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
