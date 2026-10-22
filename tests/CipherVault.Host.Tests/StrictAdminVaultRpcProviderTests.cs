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
    }
}
