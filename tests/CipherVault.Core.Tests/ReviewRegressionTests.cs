using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using CipherVault.Core.Backup;
using CipherVault.Core.Crypto;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Security;
using CipherVault.Core.Session;
using CipherVault.Core.Storage;

namespace CipherVault.Core.Tests;

public sealed class ReviewRegressionTests
{
    private const string Password = "review regression password";

    [Fact]
    public async Task UnconfirmedRecoveryBlocksContentUntilConfirmed()
    {
        await using var fixture = await Fixture.CreateAsync();

        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var blocked = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ListCatalogAsync(null, null, 10));
        Assert.Equal(VaultErrorCode.RecoveryVerificationRequired, blocked.Code);

        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);

        var page = await fixture.Service.ListCatalogAsync(null, null, 10);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task RecoveryRotationRemainsPendingUntilConfirmed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);

        var newRecoveryKey = await fixture.Service.RotateRecoveryKeyAsync(
            fixture.VaultPath,
            Password,
            VaultActorKind.UserUi);

        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultCredentialType.RecoveryKey);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            newRecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.LockAsync();

        await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.UnlockAsync(
                fixture.VaultPath,
                initialized.RecoveryKey,
                VaultCredentialType.RecoveryKey));
        var unlocked = await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            newRecoveryKey,
            VaultCredentialType.RecoveryKey);
        Assert.Equal(VaultSessionState.Unlocked, unlocked.State);
        Assert.True(unlocked.RecoveryVerified);
    }

    [Fact]
    public async Task ExportRejectsDestinationInsideVault()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var destination = Path.Combine(fixture.VaultPath, "export");
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Service.ExportSnapshotAsync(destination, VaultActorKind.UserUi));

        Assert.Equal(VaultErrorCode.InvalidInput, exception.Code);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExpiredTrashAndRevisionsAreCleanedOnUnlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var created = await fixture.Service.CreateNoteAsync(
            "expired-note",
            null,
            "md",
            "expired content",
            null,
            VaultActorKind.UserUi);
        await fixture.Service.DeleteNoteAsync(
            created.DocumentId,
            created.Revision,
            VaultActorKind.UserUi);

        var envelope = await VaultKeyring.LoadEnvelopeAsync(
            Path.Combine(fixture.VaultPath, "vault.keys"));
        var material = await VaultKeyring.UnwrapAsync(
            envelope,
            Password,
            VaultCredentialType.Password);
        try
        {
            await using var connection = await SqlCipherVaultStore.OpenAsync(
                fixture.VaultPath,
                material);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE documents SET purge_after_ms = 0
                WHERE document_id = $id;
                UPDATE document_revisions SET retain_until_ms = 0
                WHERE document_id = $id;
                """;
            command.Parameters.AddWithValue("$id", created.DocumentId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.MasterKey);
            CryptographicOperations.ZeroMemory(material.DbKey);
        }

        await fixture.Service.LockAsync();
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var trash = await fixture.Service.ListTrashAsync(null, 10);
        Assert.Empty(trash.Items);
    }

    [Fact]
    public async Task ProcessLockSerializesConcurrentAcquisition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cipher-vault-lock-{Guid.NewGuid():N}");
        using var first = await VaultProcessLock.AcquireAsync(path);
        var secondTask = VaultProcessLock.AcquireAsync(path);

        await Task.Delay(150);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask;
        Assert.NotNull(second);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InitializedVaultHasProtectedWindowsAcl()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.InitializeAsync(fixture.VaultPath, Password);

        var information = new DirectoryInfo(fixture.VaultPath);
        var security = information.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();
        Assert.NotEmpty(rules);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("iterations")]
    [InlineData("parallelism")]
    public async Task PasswordKdfParametersAboveLimitsAreRejected(string parameter)
    {
        var created = await VaultKeyring.CreateAsync(Password, Guid.NewGuid());
        var slot = created.Envelope.PasswordSlot;
        var tamperedSlot = parameter switch
        {
            "memory" => slot with { MemoryCostKiB = VaultKeyring.MaxArgon2MemoryKiB + 1 },
            "iterations" => slot with { Iterations = VaultKeyring.MaxArgon2Iterations + 1 },
            "parallelism" => slot with { Parallelism = VaultKeyring.MaxArgon2Parallelism + 1 },
            _ => throw new InvalidOperationException("Unknown KDF parameter.")
        };
        var tamperedEnvelope = created.Envelope with { PasswordSlot = tamperedSlot };

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            VaultKeyring.UnwrapAsync(
                tamperedEnvelope,
                Password,
                VaultCredentialType.Password));

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public async Task UnsupportedKeyEnvelopeVersionIsRejected()
    {
        var created = await VaultKeyring.CreateAsync(Password, Guid.NewGuid());
        var tamperedEnvelope = created.Envelope with
        {
            FormatVersion = created.Envelope.FormatVersion + 1
        };

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            VaultKeyring.UnwrapAsync(
                tamperedEnvelope,
                Password,
                VaultCredentialType.Password));

        Assert.Equal(VaultErrorCode.CorruptVault, exception.Code);
    }

    [Fact]
    public void BackupArchiveAndEntryLimitsRejectOversizedInput()
    {
        Assert.Throws<VaultException>(() =>
            BackupService.ValidateArchiveLength(BackupService.MaxArchiveBytes + 1));
        Assert.Throws<VaultException>(() =>
            BackupService.ValidateEntryLength(
                "manifest.json",
                BackupService.MaxMetadataBytes + 1,
                0));

        var total = BackupService.ValidateEntryLength(
            "vault.db",
            BackupService.MaxDatabaseBytes,
            0);
        Assert.Equal(BackupService.MaxDatabaseBytes, total);
    }

    [Fact]
    public async Task ImportPreviewMarksOversizedDocumentUnsupported()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var source = Path.Combine(fixture.RootPath, "import-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "valid.md"), "valid");
        await using (var oversized = new FileStream(
            Path.Combine(source, "oversized.md"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            oversized.SetLength(DocumentWriter.MaxContentBytes + 1);
        }

        var preview = await fixture.Service.PreviewImportAsync(source);
        var oversizedItem = Assert.Single(
            preview.Items,
            item => item.RelativePath == "oversized.md");

        Assert.Equal(1, preview.SupportedFiles);
        Assert.Equal(1, preview.UnsupportedFiles);
        Assert.Equal("unsupported", oversizedItem.State);
        Assert.Equal("content_too_large", oversizedItem.Reason);

        var result = await fixture.Service.CommitImportAsync(preview.JobId);
        Assert.Equal(1, result.Imported);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ExportedDirectoryKeepsProtectedAclAndReturnsFinalManifestPath()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);
        await fixture.Service.CreateNoteAsync(
            "export",
            null,
            "md",
            "protected export",
            null,
            VaultActorKind.UserUi);

        var destination = Path.Combine(fixture.RootPath, "export");
        var result = await fixture.Service.ExportSnapshotAsync(destination);
        var security = new DirectoryInfo(destination).GetAccessControl();

        Assert.Equal(
            Path.Combine(destination, "vault-export-manifest.json"),
            result.ManifestPath);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public async Task ExportRemovesStaleStagingDirectory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var initialized = await fixture.Service.InitializeAsync(fixture.VaultPath, Password);
        await fixture.Service.ConfirmRecoveryKeyAsync(
            fixture.VaultPath,
            initialized.RecoveryKey,
            VaultActorKind.UserUi);
        await fixture.Service.UnlockAsync(
            fixture.VaultPath,
            Password,
            VaultCredentialType.Password);

        var destination = Path.Combine(fixture.RootPath, "export");
        var stale = Path.Combine(fixture.RootPath, ".export.export-stale");
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        await fixture.Service.ExportSnapshotAsync(destination);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(destination));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string rootPath, string vaultPath, VaultService service)
        {
            RootPath = rootPath;
            VaultPath = vaultPath;
            Service = service;
        }

        public string RootPath { get; }
        public string VaultPath { get; }
        public VaultService Service { get; }

        public static Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-review-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var service = new VaultService(new AlwaysActiveActivityProvider());
            return Task.FromResult(new Fixture(root, Path.Combine(root, "vault"), service));
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
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
