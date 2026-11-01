using CipherVault.Application.Backup;
using CipherVault.Core.Documents;

namespace CipherVault.Application.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task CreateBackupProducesVerifiedPackage()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var service = new BackupService(
            new BackupStorageOptions(
                Path.Combine(fixture.Parent, "backups")),
            fixture.Manager,
            fixture.TimeProvider);

        var result = await service.CreateAsync(
            new VaultActorContext(VaultActorKind.UserUi));

        Assert.True(result.AuditRecorded);
        Assert.Equal(
            Core.Backup.BackupFileStatus.Verified,
            result.Item.Status);
        Assert.True(File.Exists(Path.Combine(
            fixture.Parent,
            "backups",
            result.Item.FileName)));
    }
}
