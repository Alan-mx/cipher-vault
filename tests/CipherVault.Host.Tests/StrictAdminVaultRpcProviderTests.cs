using System.Security.Principal;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Host.Service;
using CipherVault.Host.Session;
using Microsoft.Extensions.DependencyInjection;

namespace CipherVault.Host.Tests;

public sealed class StrictAdminVaultRpcProviderTests
{
    [Fact]
    public async Task CreateNoteExecutesThroughVaultGateway()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var sessions = fixture.App.Services.GetRequiredService<
            HostSessionCoordinator>();
        await sessions.UnlockAsync(HostCredentialPreference.Password);
        var provider = new StrictAdminVaultRpcProvider(
            fixture.App.Services.GetRequiredService<HostVaultGateway>());
        var now = DateTimeOffset.UtcNow;
        var session = new AdminSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new VerifiedProcessIdentity(
                VerifiedProcessKind.AdminUi,
                (uint)Environment.ProcessId,
                now.AddMinutes(-1),
                WindowsIdentity.GetCurrent().User!.Value,
                "S-1-5-5-0-1",
                1,
                Environment.ProcessPath!,
                new string('a', 64),
                SignerThumbprint: null,
                SignerSubject: null,
                IntegrityLevel: 0x2000),
            WindowsIdentity.GetCurrent().User!.Value,
            "S-1-5-80-1",
            now,
            now,
            now.AddMinutes(15),
            now.AddHours(4),
            VaultSessionId: null,
            State: AdminSessionState.Active,
            RevocationReason: null);

        var result = await provider.HandleAsync(
            session,
            "admin.notes.create",
            JsonSerializer.SerializeToElement(
                new HostCreateNoteRequest(
                    "Strict pipe note",
                    Folder: null,
                    Format: "md",
                    Content: "Created through the strict Admin pipe.",
                    Tags: ["strict"]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);

        var write = Assert.IsType<VaultWriteResult>(result);
        Assert.NotEqual(Guid.Empty, write.DocumentId);
        Assert.Equal(1, write.Revision);
        var appendResult = await provider.HandleAsync(
            session,
            "admin.notes.append",
            JsonSerializer.SerializeToElement(
                new AdminAppendNoteRequest(
                    write.DocumentId,
                    new HostAppendNoteRequest(
                        write.Revision,
                        "\nAppended through strict pipe.")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var appended = Assert.IsType<VaultWriteResult>(appendResult);
        Assert.Equal(write.DocumentId, appended.DocumentId);
        Assert.Equal(2, appended.Revision);
        var updateResult = await provider.HandleAsync(
            session,
            "admin.notes.update",
            JsonSerializer.SerializeToElement(
                new AdminUpdateNoteRequest(
                    appended.DocumentId,
                    new HostUpdateNoteRequest(
                        appended.Revision,
                        Title: "Updated strict pipe note",
                        Folder: null,
                        Content: "Updated through strict pipe.",
                        Tags: ["strict", "updated"])),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var updated = Assert.IsType<VaultWriteResult>(updateResult);
        Assert.Equal(appended.DocumentId, updated.DocumentId);
        Assert.Equal(3, updated.Revision);
        var readResult = await provider.HandleAsync(
            session,
            "admin.notes.read",
            JsonSerializer.SerializeToElement(
                new AdminReadNoteRequest(
                    updated.DocumentId,
                    Offset: 0,
                    MaxChars: 1000),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var read = Assert.IsType<VaultReadResult>(readResult);
        Assert.Equal("Updated strict pipe note", read.Title);
        Assert.Equal("Updated through strict pipe.", read.Content);
        var deleteResult = await provider.HandleAsync(
            session,
            "admin.notes.delete",
            JsonSerializer.SerializeToElement(
                new AdminTrashNoteRequest(
                    updated.DocumentId,
                    new HostTrashNoteRequest(updated.Revision)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var deleted = Assert.IsType<VaultDeleteResult>(deleteResult);
        Assert.Equal(updated.DocumentId, deleted.DocumentId);
        Assert.Equal(3, deleted.Revision);
        var catalogResult = await provider.HandleAsync(
            session,
            "admin.catalog.list",
            JsonSerializer.SerializeToElement(
                new AdminCatalogListRequest(
                    Folder: null,
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var catalog = Assert.IsType<VaultCatalogPage>(catalogResult);
        Assert.Empty(catalog.Items);
        var trashResult = await provider.HandleAsync(
            session,
            "admin.trash.list",
            JsonSerializer.SerializeToElement(
                new AdminTrashListRequest(
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var trash = Assert.IsType<VaultTrashPage>(trashResult);
        var trashed = Assert.Single(trash.Items);
        Assert.Equal(updated.DocumentId, trashed.DocumentId);
        var restoreResult = await provider.HandleAsync(
            session,
            "admin.trash.restore",
            JsonSerializer.SerializeToElement(
                new AdminRestoreTrashRequest(
                    trashed.DocumentId,
                    new HostRestoreTrashRequest(
                        trashed.Revision,
                        RenamePath: null)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var restored = Assert.IsType<VaultRestoreResult>(restoreResult);
        Assert.Equal(trashed.DocumentId, restored.DocumentId);
        Assert.EndsWith(".md", restored.VirtualPath, StringComparison.Ordinal);
        var revisionsResult = await provider.HandleAsync(
            session,
            "admin.revisions.list",
            JsonSerializer.SerializeToElement(
                new AdminRevisionListRequest(
                    restored.DocumentId,
                    Cursor: null,
                    Limit: 100),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);
        var revisions = Assert.IsType<VaultRevisionPage>(revisionsResult);
        Assert.NotEmpty(revisions.Items);
        Assert.All(
            revisions.Items,
            revision => Assert.Equal(restored.DocumentId, revision.DocumentId));
    }
}
