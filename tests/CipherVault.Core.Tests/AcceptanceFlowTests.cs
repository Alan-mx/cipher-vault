using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class AcceptanceFlowTests
{
    [Fact]
    public async Task CompleteCipherVaultLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cipher-vault-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var vaultPath = Path.Combine(root, "vault");
        var sourcePath = Path.Combine(root, "source");
        var exportPath = Path.Combine(root, "export");
        var backupPath = Path.Combine(root, "vault.cvbackup");
        var restoredPath = Path.Combine(root, "restored");
        Directory.CreateDirectory(Path.Combine(sourcePath, "健康"));
        Directory.CreateDirectory(Path.Combine(sourcePath, "工作"));
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, "健康", "睡眠.md"),
            "---\ntags: [健康, 睡眠]\n---\n# 睡眠\n[[记录]]\n最近睡眠质量一般。");
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath, "工作", "记录.txt"),
            "工作需要持续记录。");
        await File.WriteAllBytesAsync(
            Path.Combine(sourcePath, "忽略.png"),
            [1, 2, 3]);

        var activity = new AlwaysActiveActivityProvider();
        await using var service = new VaultService(activity);
        try
        {
            var initialized = await service.InitializeAsync(vaultPath, "acceptance-password-2026");
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserCli);
            await service.UnlockAsync(
                vaultPath,
                "acceptance-password-2026",
                VaultCredentialType.Password);

            var preview = await service.PreviewImportAsync(sourcePath, VaultActorKind.UserCli);
            Assert.Equal(2, preview.SupportedFiles);
            Assert.Equal(1, preview.UnsupportedFiles);
            var import = await service.CommitImportAsync(preview.JobId, VaultActorKind.UserCli);
            Assert.Equal(2, import.Imported);
            Assert.Equal(0, import.Failed);

            var catalog = await service.ListCatalogAsync(null, null, 100);
            Assert.Contains(catalog.Items, item => item.Title == "睡眠");
            Assert.Contains(catalog.Items, item => item.Title == "记录");

            var search = await service.SearchNotesAsync("睡眠质量", null, null, 20);
            var sleep = Assert.Single(search.Hits);
            var read = await service.ReadNoteAsync(sleep.DocumentId, 0, 10_000);
            Assert.Contains("最近睡眠质量一般", read.Content, StringComparison.Ordinal);

            var updated = await service.UpdateNoteAsync(
                sleep.DocumentId,
                read.Revision,
                new VaultNotePatch(
                    Content: read.Content
                        .Replace(
                            "tags: [健康, 睡眠]",
                            "tags: [健康, 睡眠, 恢复]",
                            StringComparison.Ordinal) +
                        "\n新增恢复记录。",
                    Tags: null),
                VaultActorKind.Mcp);
            Assert.Contains(
                (await service.ListTagsAsync(null, 50)).Items,
                tag => tag.Name == "恢复");
            var appended = await service.AppendNoteAsync(
                sleep.DocumentId,
                updated.Revision,
                "\n持续观察。",
                VaultActorKind.Mcp);
            Assert.Equal(3, appended.Revision);

            var revisions = await service.ListRevisionsAsync(sleep.DocumentId, null, 100);
            Assert.Equal(3, revisions.Items.Count);

            var deleted = await service.DeleteNoteAsync(
                sleep.DocumentId,
                appended.Revision,
                VaultActorKind.Mcp);
            Assert.True(deleted.PurgeAfter > DateTimeOffset.UtcNow);
            var trash = await service.ListTrashAsync(null, 100);
            Assert.Contains(trash.Items, item => item.DocumentId == sleep.DocumentId);
            await service.RestoreTrashAsync(
                sleep.DocumentId,
                appended.Revision,
                null,
                VaultActorKind.UserUi);

            await service.ChangePasswordAsync(
                vaultPath,
                "acceptance-password-2026",
                "acceptance-password-2027",
                VaultActorKind.UserUi);
            var recoveryKey = await service.RotateRecoveryKeyAsync(
                vaultPath,
                "acceptance-password-2027",
                VaultActorKind.UserUi);
            Assert.NotEqual(initialized.RecoveryKey, recoveryKey);

            var exported = await service.ExportSnapshotAsync(exportPath, VaultActorKind.UserUi);
            Assert.True(exported.ExportedDocuments >= 2);
            Assert.True(File.Exists(Path.Combine(exportPath, "health", "睡眠.md")) is false);
            Assert.True(File.Exists(Path.Combine(exportPath, "健康", "睡眠.md")));

            var backup = await service.CreateBackupAsync(backupPath, VaultActorKind.UserUi);
            Assert.True(File.Exists(backup.BackupPath));
            var verification = await service.VerifyVaultAsync();
            Assert.True(verification.Valid);

            var restored = await service.RestoreBackupAsync(
                backupPath,
                restoredPath,
                "acceptance-password-2027",
                VaultCredentialType.Password,
                VaultActorKind.UserUi);

            await using var restoredService = new VaultService(new AlwaysActiveActivityProvider());
            await restoredService.UnlockAsync(
                restored.VaultPath,
                "acceptance-password-2027",
                VaultCredentialType.Password);
            var restoredSearch = await restoredService.SearchNotesAsync(
                "睡眠质量",
                null,
                null,
                20);
            var restoredHit = Assert.Single(restoredSearch.Hits);
            Assert.Equal(sleep.DocumentId, restoredHit.DocumentId);
            var restoredRead = await restoredService.ReadNoteAsync(
                restoredHit.DocumentId,
                0,
                10_000);
            Assert.Contains("持续观察。", restoredRead.Content);
            var restoredMap = await restoredService.GetDocumentMapAsync(
                restoredHit.DocumentId);
            Assert.Contains(
                restoredMap.FrontMatterFields,
                field => field.Key == "tags");
            Assert.Contains(
                restoredMap.OutgoingLinks,
                link => link.RawTarget == "记录" &&
                    link.ResolutionStatus == "resolved");
            var restoredRevisions = await restoredService.ListRevisionsAsync(
                restoredHit.DocumentId,
                null,
                100);
            Assert.True(restoredRevisions.Items.Count >= 3);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
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
