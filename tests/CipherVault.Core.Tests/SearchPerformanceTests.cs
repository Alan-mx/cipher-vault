using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class SearchPerformanceTests
{
    [Fact]
    public async Task RunShortSearchBaselineWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_SEARCH_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var documentCount = ReadInt(
            "CIPHER_VAULT_SEARCH_PERF_DOCS",
            10_005,
            100,
            20_000);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-search-perf-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "vault");
        var sourcePath = Path.Combine(root, "import-source");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sourcePath);
        var service = new VaultService(new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await service.InitializeAsync(
                vaultPath,
                "search-performance-password");
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await service.UnlockAsync(
                vaultPath,
                "search-performance-password",
                VaultCredentialType.Password);

            for (var index = 0; index < documentCount; index++)
            {
                var title = $"perf-search-{index:D5}";
                await File.WriteAllTextAsync(
                    Path.Combine(sourcePath, $"{title}.md"),
                    $"# {title}\n针 benchmark content {index}");
            }

            var preview = await service.PreviewImportAsync(sourcePath);
            var imported = await service.CommitImportAsync(preview.JobId);
            Assert.Equal(documentCount, imported.Imported);
            Assert.Equal(0, imported.Failed);

            var watch = Stopwatch.StartNew();
            var page = await service.SearchNotesAsync(
                "针",
                null,
                null,
                20);
            watch.Stop();

            var output = JsonSerializer.Serialize(new
            {
                documents = documentCount,
                searchMs = watch.ElapsedMilliseconds,
                totalDocuments = page.Coverage.TotalDocuments,
                scannedDocuments = page.Coverage.ScannedDocuments,
                matchedDocuments = page.Coverage.MatchedDocuments,
                returnedDocuments = page.Coverage.ReturnedDocuments,
                isTruncated = page.Coverage.IsTruncated
            });
            Console.WriteLine($"SEARCH_BENCHMARK={output}");
            Assert.Equal(documentCount, page.Coverage.TotalDocuments);
            Assert.Equal(
                Math.Min(documentCount, 10_000),
                page.Coverage.ScannedDocuments);
            Assert.Equal(documentCount > 10_000, page.Coverage.IsTruncated);
        }
        finally
        {
            await service.DisposeAsync();
            if (Directory.Exists(root))
            {
                await DeleteDirectoryWithRetryAsync(root);
            }
        }
    }

    private static int ReadInt(
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(100);
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
