using System.IO;
using CipherVault.Core.Configuration;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class ImportWorkspaceRegistryTests
{
    [Fact]
    public void TryTakeActiveWorkspaceTransfersOwnership()
    {
        using var fixture = new RegistryFixture();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var registry = new ImportWorkspaceRegistry(
            time,
            TimeSpan.FromHours(1));
        var workspace = fixture.CreateWorkspace();
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryAdd(jobId, workspace));
        Assert.True(registry.Contains(jobId));

        Assert.True(registry.TryTake(jobId, out var taken));

        Assert.Same(workspace, taken);
        Assert.False(registry.Contains(jobId));
        taken!.Dispose();
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public void TryTakeExpiredWorkspaceRemovesDirectory()
    {
        using var fixture = new RegistryFixture();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var registry = new ImportWorkspaceRegistry(
            time,
            TimeSpan.FromHours(1));
        var workspace = fixture.CreateWorkspace();
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryAdd(jobId, workspace));

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(registry.TryTake(jobId, out _));
        Assert.Equal(0, registry.Count);
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public void ContainsExpiredWorkspaceRemovesAndDisposesIt()
    {
        using var fixture = new RegistryFixture();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var registry = new ImportWorkspaceRegistry(
            time,
            TimeSpan.FromHours(1));
        var workspace = fixture.CreateWorkspace();
        var jobId = Guid.NewGuid();
        Assert.True(registry.TryAdd(jobId, workspace));

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(registry.Contains(jobId));
        Assert.Equal(0, registry.Count);
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    [Fact]
    public void SweepExpiredRemovesWorkspace()
    {
        using var fixture = new RegistryFixture();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        using var registry = new ImportWorkspaceRegistry(
            time,
            TimeSpan.FromHours(1));
        var workspace = fixture.CreateWorkspace();
        Assert.True(registry.TryAdd(Guid.NewGuid(), workspace));

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(1, registry.SweepExpired());
        Assert.Equal(0, registry.Count);
        Assert.False(Directory.Exists(workspace.RootPath));
    }

    private sealed class RegistryFixture : IDisposable
    {
        public RegistryFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"cipher-vault-import-registry-{Guid.NewGuid():N}");
            Paths = CipherVaultServicePaths.Create(
                Path.Combine(Root, "ProgramFiles"),
                Path.Combine(Root, "ProgramData"),
                Path.Combine(Root, "LocalAppData"));
            Directory.CreateDirectory(Paths.TransferRoot);
            Coordinator = new TransferCoordinator(
                Paths,
                TimeSpan.FromMinutes(30));
        }

        public string Root { get; }
        public CipherVaultServicePaths Paths { get; }
        public TransferCoordinator Coordinator { get; }

        public TransferWorkspace CreateWorkspace()
        {
            return Coordinator.CreateWorkspace(
                HostTransferDirection.Import,
                HostTransferPayloadKind.Directory,
                "S-1-5-1",
                logonSidValue: null,
                "input");
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
