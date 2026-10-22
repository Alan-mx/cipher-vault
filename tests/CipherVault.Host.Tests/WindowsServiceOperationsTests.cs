using CipherVault.Core.Errors;
using CipherVault.Host.ServiceManagement;

namespace CipherVault.Host.Tests;

public sealed class WindowsServiceOperationsTests
{
    [Fact]
    public async Task StartStopsAndDeletesServiceInOrder()
    {
        var query = new SequenceServiceQuery(
            new WindowsServiceSnapshot(true, "stopped", "Automatic"),
            new WindowsServiceSnapshot(true, "running", "Automatic"),
            new WindowsServiceSnapshot(true, "running", "Automatic"),
            new WindowsServiceSnapshot(true, "stopped", "Automatic"),
            new WindowsServiceSnapshot(true, "stopped", "Automatic"),
            new WindowsServiceSnapshot(false, "notInstalled", null));
        var runner = new RecordingCommandRunner();
        var operations = new WindowsServiceOperations(query, runner);

        var started = await operations.StartAsync("CipherVaultHost");
        var stopped = await operations.StopAsync("CipherVaultHost");
        var removed = await operations.UninstallAsync("CipherVaultHost");

        Assert.Equal("running", started.State);
        Assert.Equal("stopped", stopped.State);
        Assert.False(removed.Installed);
        Assert.Equal(
            [
                "start CipherVaultHost",
                "stop CipherVaultHost",
                "delete CipherVaultHost"
            ],
            runner.Commands);
    }

    [Fact]
    public async Task UninstallStopsRunningServiceBeforeDelete()
    {
        var query = new SequenceServiceQuery(
            new WindowsServiceSnapshot(true, "running", "Automatic"),
            new WindowsServiceSnapshot(true, "running", "Automatic"),
            new WindowsServiceSnapshot(true, "stopped", "Automatic"),
            new WindowsServiceSnapshot(false, "notInstalled", null));
        var runner = new RecordingCommandRunner();
        var operations = new WindowsServiceOperations(query, runner);

        var removed = await operations.UninstallAsync("CipherVaultHost");

        Assert.False(removed.Installed);
        Assert.Equal(
            [
                "stop CipherVaultHost",
                "delete CipherVaultHost"
            ],
            runner.Commands);
    }

    [Fact]
    public async Task StartRejectsMissingService()
    {
        var operations = new WindowsServiceOperations(
            new SequenceServiceQuery(
                new WindowsServiceSnapshot(
                    false,
                    "notInstalled",
                    null)),
            new RecordingCommandRunner());

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => operations.StartAsync("CipherVaultHost"));

        Assert.Equal(VaultErrorCode.NotFound, exception.Code);
    }

    private sealed class SequenceServiceQuery(
        params WindowsServiceSnapshot[] snapshots) : IWindowsServiceQuery
    {
        private readonly Queue<WindowsServiceSnapshot> _snapshots =
            new(snapshots);

        public WindowsServiceSnapshot GetStatus(string serviceName)
        {
            Assert.Equal("CipherVaultHost", serviceName);
            return _snapshots.Count > 0
                ? _snapshots.Dequeue()
                : throw new InvalidOperationException(
                    "No service status snapshot was queued.");
        }
    }

    private sealed class RecordingCommandRunner : IWindowsServiceCommandRunner
    {
        public List<string> Commands { get; } = [];

        public Task<WindowsServiceCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(string.Join(' ', arguments));
            return Task.FromResult(new WindowsServiceCommandResult(
                0,
                string.Empty,
                string.Empty));
        }
    }
}
