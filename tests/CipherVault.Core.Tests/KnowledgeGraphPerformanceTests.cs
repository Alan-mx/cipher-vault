using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Session;

namespace CipherVault.Core.Tests;

public sealed class KnowledgeGraphPerformanceTests
{
    [Fact]
    public async Task RunKnowledgeGraphBaselineWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CIPHER_VAULT_RUN_PERF"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var documentCount = ReadInt("CIPHER_VAULT_PERF_DOCS", 1_000, 10, 20_000);
        var linksPerDocument = ReadInt("CIPHER_VAULT_PERF_LINKS", 10, 0, 50);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-perf-{Guid.NewGuid():N}");
        var vaultPath = Path.Combine(root, "vault");
        Directory.CreateDirectory(root);
        var service = new VaultService(
            new AlwaysActiveActivityProvider());
        try
        {
            var initialized = await service.InitializeAsync(
                vaultPath,
                "knowledge-performance-password");
            await service.ConfirmRecoveryKeyAsync(
                vaultPath,
                initialized.RecoveryKey,
                VaultActorKind.UserUi);
            await service.UnlockAsync(
                vaultPath,
                "knowledge-performance-password",
                VaultCredentialType.Password);

            var documentIds = new List<Guid>(documentCount);
            var createWatch = Stopwatch.StartNew();
            for (var index = 0; index < documentCount; index++)
            {
                var title = $"perf-{index:D5}";
                var links = CreateLinks(index, linksPerDocument);
                var created = await service.CreateNoteAsync(
                    title,
                    null,
                    "md",
                    $"# {title}\n\n{links}",
                    null,
                    VaultActorKind.UserUi);
                documentIds.Add(created.DocumentId);
            }
            createWatch.Stop();

            var rebuildWatch = Stopwatch.StartNew();
            var rebuilt = await service.RebuildLinkIndexAsync(
                VaultActorKind.UserUi);
            rebuildWatch.Stop();

            var mapWatch = Stopwatch.StartNew();
            _ = await service.GetDocumentMapAsync(documentIds[^1]);
            mapWatch.Stop();

            var backlinkWatch = Stopwatch.StartNew();
            _ = await service.ListBacklinksAsync(documentIds[0], null, 100);
            backlinkWatch.Stop();

            var graphWatch = Stopwatch.StartNew();
            _ = await service.GetLocalGraphAsync(
                new VaultLocalGraphQuery(
                    documentIds[documentIds.Count / 2],
                    2,
                    [],
                    null,
                    200,
                    500));
            graphWatch.Stop();

            var output = JsonSerializer.Serialize(new
            {
                documents = documentCount,
                requestedLinks = Enumerable.Range(1, documentCount - 1)
                    .Sum(index => Math.Min(index, linksPerDocument)),
                indexedLinks = rebuilt.LinksIndexed,
                createMs = createWatch.ElapsedMilliseconds,
                indexRebuildMs = rebuildWatch.ElapsedMilliseconds,
                documentMapMs = mapWatch.ElapsedMilliseconds,
                backlinksMs = backlinkWatch.ElapsedMilliseconds,
                localGraphMs = graphWatch.ElapsedMilliseconds
            });
            Console.WriteLine($"KNOWLEDGE_GRAPH_BENCHMARK={output}");
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
                    $"[[perf-{(index - 1 - offset):D5}]]"));
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
