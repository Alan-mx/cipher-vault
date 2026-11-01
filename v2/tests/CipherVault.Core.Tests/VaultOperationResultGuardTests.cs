using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CipherVault.Core.Crypto;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Tests;

public sealed class VaultOperationResultGuardTests
{
    [Fact]
    public void AllowsScalarsAndMaterializedDtos()
    {
        var result = new AllowedResult(
            42,
            "safe",
            new[] { Guid.NewGuid() },
            new ReadOnlyCollection<int>(new[] { 1, 2, 3 }));

        VaultOperationResultGuard.ValidateResult(result);
    }

    [Fact]
    public async Task RejectsDatabaseAndTaskObjects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = fixture.Runtime.CreateOperationContext();
        using var lease = context.CreateCommand();

        AssertRejected(context);
        AssertRejected(fixture.Runtime);
        AssertRejected(fixture.Material);
        AssertRejected(lease);
        AssertRejected(lease.Command);
        AssertRejected(context.Connection.Handle!);
        AssertRejected(Task.FromResult(1));

        await context.DisposeAsync();
    }

    [Fact]
    public void RejectsLazySequencesAndCycles()
    {
        AssertRejected(new LazySequence());
        var node = new CyclicNode();
        node.Next = node;
        AssertRejected(node);
    }

    [Fact]
    public void AllowsSharedEmptyCollectionsInSiblingLinks()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new LinkTargetView(
            Guid.NewGuid(),
            "Target",
            "Target.md");
        var sharedCandidates = Array.Empty<LinkTargetView>();
        var result = new DocumentMap(
            new DocumentMetadata(
                Guid.NewGuid(),
                "Source",
                "Source.md",
                VaultDocumentFormat.Markdown,
                1,
                Array.Empty<string>(),
                now,
                now),
            Array.Empty<DocumentHeading>(),
            Array.Empty<FrontMatterFieldView>(),
            new[]
            {
                new OutgoingLinkView(
                    "Target",
                    "Target",
                    null,
                    "resolved",
                    target,
                    sharedCandidates,
                    0,
                    8),
                new OutgoingLinkView(
                    "Target",
                    "Target",
                    null,
                    "resolved",
                    target,
                    sharedCandidates,
                    9,
                    17)
            },
            0,
            false,
            false,
            false);

        VaultOperationResultGuard.ValidateResult(result);
    }

    private static void AssertRejected(object value)
    {
        var exception = Assert.Throws<VaultException>(
            () => VaultOperationResultGuard.ValidateResult(value));
        Assert.Equal(VaultErrorCode.DatabaseError, exception.Code);
    }

    private sealed record AllowedResult(
        int Number,
        string Text,
        Guid[] Ids,
        ReadOnlyCollection<int> Values);

    private sealed class LazySequence : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator()
        {
            yield return 1;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class CyclicNode
    {
        public CyclicNode? Next { get; set; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            VaultKeyMaterial material,
            VaultDatabaseRuntime runtime)
        {
            Root = root;
            Material = material;
            Runtime = runtime;
        }

        internal VaultDatabaseRuntime Runtime { get; }

        private string Root { get; }

        internal VaultKeyMaterial Material { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-result-guard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var material = new VaultKeyMaterial(
                Guid.NewGuid(),
                RandomNumberGenerator.GetBytes(32),
                RandomNumberGenerator.GetBytes(32));
            try
            {
                await SqliteMcVaultStore.InitializeAsync(root, material);
                var runtime = await VaultDatabaseRuntime.OpenAsync(
                    root,
                    material,
                    Guid.NewGuid(),
                    generation: 1);
                return new Fixture(root, material, runtime);
            }
            catch
            {
                material.Dispose();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Material.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
