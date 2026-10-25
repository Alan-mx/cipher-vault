using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class ImportPerformanceTests
{
    [Fact]
    public async Task RunImportBaselineWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_IMPORT_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var documentCount = ReadInt("CIPHER_VAULT_IMPORT_PERF_DOCS", 1_000, 10, 20_000);
        var linksPerDocument = ReadInt("CIPHER_VAULT_IMPORT_PERF_LINKS", 10, 0, 50);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-import-perf-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "vault");
        var sourcePath = Path.Combine(root, "import-source");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sourcePath);
        var service = new VaultService(
            new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await service.InitializeAsync(
                vaultPath,
                "import-performance-password");
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await service.UnlockAsync(
                vaultPath,
                "import-performance-password",
                VaultCredentialType.Password);

            var prepareWatch = Stopwatch.StartNew();
            for (var index = 0; index < documentCount; index++)
            {
                var title = $"perf-import-{index:D5}";
                var links = CreateLinks(index, linksPerDocument);
                await File.WriteAllTextAsync(
                    Path.Combine(sourcePath, $"{title}.md"),
                    $"---\ntags: [performance, import]\n---\n# {title}\n\n{links}");
            }
            prepareWatch.Stop();

            var previewWatch = Stopwatch.StartNew();
            var preview = await service.PreviewImportAsync(sourcePath);
            previewWatch.Stop();

            var commitWatch = Stopwatch.StartNew();
            var result = await service.CommitImportAsync(preview.JobId);
            commitWatch.Stop();

            var output = JsonSerializer.Serialize(new
            {
                documents = documentCount,
                requestedLinks = Enumerable.Range(1, documentCount - 1)
                    .Sum(index => Math.Min(index, linksPerDocument)),
                supported = preview.SupportedFiles,
                prepareMs = prepareWatch.ElapsedMilliseconds,
                previewMs = previewWatch.ElapsedMilliseconds,
                commitMs = commitWatch.ElapsedMilliseconds,
                imported = result.Imported,
                failed = result.Failed,
                state = result.State
            });
            Console.WriteLine($"IMPORT_BENCHMARK={output}");
            Assert.Equal(documentCount, result.Imported);
            Assert.Equal(0, result.Failed);
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

    private static string CreateLinks(int index, int count)
    {
        if (index == 0 || count == 0)
        {
            return string.Empty;
        }
        var targetCount = Math.Min(count, index);
        return string.Join(
            '\n',
            Enumerable.Range(0, targetCount)
                .Select(offset =>
                    $"[[perf-import-{(index - 1 - offset):D5}]]"));
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
