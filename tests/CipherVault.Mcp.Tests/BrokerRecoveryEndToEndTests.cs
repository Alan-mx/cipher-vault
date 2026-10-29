using System.Diagnostics;
using System.Text.Json;
using CipherVault.Host.Client;
using CipherVault.Host.Security;

namespace CipherVault.Mcp.Tests;

public sealed class BrokerRecoveryEndToEndTests
{
    private const string DocumentId =
        "d80c2e83-636f-47a8-a3f2-e473beec9922";

    [Fact]
    public async Task McpRecoversGrantAfterBrokerProcessKilled()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var firstBroker = environment.StartInitialBroker();
        await environment.WaitForRegisteredAsync(firstBroker);

        firstBroker.Kill(entireProcessTree: true);
        await firstBroker.WaitForExitAsync(environment.Token);
        await environment.WaitForUnavailableAsync();

        var recovery = Stopwatch.StartNew();
        await environment.Mcp.RequestGrantAsync(environment.Token);
        recovery.Stop();

        Assert.Contains("read", environment.Mcp.GrantedScopes);
        var secondBroker = Assert.IsType<Process>(
            environment.ReplacementBroker);
        Assert.Equal(1, environment.BrokerStarts);
        Assert.True(
            recovery.Elapsed < TimeSpan.FromSeconds(12),
            $"MCP recovery took {recovery.Elapsed}.");
        await environment.WaitForRegisteredAsync(secondBroker);

        var lifecycleEvents =
            await environment.ReadLifecycleEventsAsync();
        Assert.Contains(
            lifecycleEvents,
            item => item.EventType == "broker_accepted" &&
                item.ProcessId == (uint?)firstBroker.Id);
        Assert.Contains(
            lifecycleEvents,
            item => item.EventType == "broker_unavailable");
        Assert.Contains(
            lifecycleEvents,
            item => item.EventType == "broker_accepted" &&
                item.ProcessId == (uint?)secondBroker.Id);
        Assert.Contains(
            lifecycleEvents,
            item => item.EventType == "broker_released" &&
                item.ProcessId == (uint?)firstBroker.Id);
    }

    [Fact]
    public async Task ConcurrentMcpClientsRecoverWithSingleReplacementBroker()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var firstBroker = environment.StartInitialBroker();
        await environment.WaitForRegisteredAsync(firstBroker);
        await using var firstClient =
            await environment.CreateAdditionalMcpClientAsync();
        await using var secondClient =
            await environment.CreateAdditionalMcpClientAsync();

        firstBroker.Kill(entireProcessTree: true);
        await firstBroker.WaitForExitAsync(environment.Token);
        await environment.WaitForUnavailableAsync();

        await Task.WhenAll(
            firstClient.Mcp.RequestGrantAsync(environment.Token),
            secondClient.Mcp.RequestGrantAsync(environment.Token));

        Assert.Contains("read", firstClient.Mcp.GrantedScopes);
        Assert.Contains("read", secondClient.Mcp.GrantedScopes);
        Assert.Equal(1, environment.BrokerStarts);
        var replacement = Assert.IsType<Process>(
            environment.ReplacementBroker);
        await environment.WaitForRegisteredAsync(replacement);
    }

    [Fact]
    public async Task McpRecoversGrantWhenBrokerKilledDuringApproval()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var approvalStartedPath = environment.CreateTemporaryFilePath(
            "approval-started");
        var firstBroker = environment.StartInitialBroker(
            approvalDelay: TimeSpan.FromSeconds(30),
            approvalStartedPath: approvalStartedPath);
        await environment.WaitForRegisteredAsync(firstBroker);

        var grantTask = environment.Mcp.RequestGrantAsync(
            environment.Token);
        await WaitUntilAsync(
            () => File.Exists(approvalStartedPath),
            TimeSpan.FromSeconds(10));
        firstBroker.Kill(entireProcessTree: true);
        await firstBroker.WaitForExitAsync(environment.Token);

        await grantTask.WaitAsync(
            TimeSpan.FromSeconds(20),
            environment.Token);

        Assert.Contains("read", environment.Mcp.GrantedScopes);
        var secondBroker = Assert.IsType<Process>(
            environment.ReplacementBroker);
        Assert.Equal(1, environment.BrokerStarts);
        await environment.WaitForRegisteredAsync(secondBroker);

        var lifecycleEvents =
            await environment.WaitForLifecycleEventsAsync(events =>
                events.Any(item =>
                    item.EventType == "broker_accepted" &&
                    item.ProcessId == (uint?)firstBroker.Id) &&
                events.Any(item =>
                    item.EventType == "broker_unavailable") &&
                events.Any(item =>
                    item.EventType == "broker_released" &&
                    item.ProcessId == (uint?)firstBroker.Id) &&
                events.Any(item =>
                    item.EventType == "broker_accepted" &&
                    item.ProcessId == (uint?)secondBroker.Id));
        Assert.Contains(
            lifecycleEvents,
            item => item.EventType == "access_grant_approval_failed" &&
                item.Stage == "broker.approval.request" &&
                item.Result == "error" &&
                item.ErrorType is not null);
    }

    [Fact]
    public async Task MidApprovalBrokerKillReturnsStableRecoveryErrorAndLogsCause()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var approvalStartedPath = environment.CreateTemporaryFilePath(
            "direct-approval-started");
        var firstBroker = environment.StartInitialBroker(
            approvalDelay: TimeSpan.FromSeconds(30),
            approvalStartedPath: approvalStartedPath);
        await environment.WaitForRegisteredAsync(firstBroker);

        var requestTask = environment.BridgeConnection.RequestGrantAsync(
            environment.Token);
        await WaitUntilAsync(
            () => File.Exists(approvalStartedPath),
            TimeSpan.FromSeconds(10));
        var approvalId = (await File.ReadAllTextAsync(
            approvalStartedPath,
            environment.Token)).Trim();
        firstBroker.Kill(entireProcessTree: true);
        await firstBroker.WaitForExitAsync(environment.Token);

        var exception = await Assert.ThrowsAsync<PipeRpcException>(
            async () => await requestTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                environment.Token));
        Assert.Equal("approval_broker_unavailable", exception.Code);

        var lifecycleEvents =
            await environment.WaitForLifecycleEventsAsync(events =>
                events.Any(item =>
                    item.EventType == "access_grant_approval_failed" &&
                    item.Message?.Contains(
                        $"approvalId={approvalId}",
                        StringComparison.Ordinal) == true) &&
                events.Any(item =>
                    item.EventType == "broker_released" &&
                    item.ProcessId == (uint?)firstBroker.Id));
        var failureEvent = Assert.Single(
            lifecycleEvents,
            item => item.EventType == "access_grant_approval_failed");
        Assert.Equal("broker.approval.request", failureEvent.Stage);
        Assert.Equal("error", failureEvent.Result);
        Assert.Contains(
            failureEvent.ErrorType,
            new[]
            {
                typeof(IOException).FullName,
                typeof(EndOfStreamException).FullName,
                typeof(ObjectDisposedException).FullName,
                typeof(TimeoutException).FullName,
                typeof(InvalidDataException).FullName
            });
        Assert.Equal(0, environment.BrokerStarts);
        Assert.Null(environment.ReplacementBroker);
    }

    [Fact]
    public async Task RequestCancellationDoesNotStartBrokerRecovery()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var approvalStartedPath = environment.CreateTemporaryFilePath(
            "cancelled-approval-started");
        var firstBroker = environment.StartInitialBroker(
            approvalDelay: TimeSpan.FromSeconds(30),
            approvalStartedPath: approvalStartedPath);
        await environment.WaitForRegisteredAsync(firstBroker);
        using var cancellation = CancellationTokenSource
            .CreateLinkedTokenSource(environment.Token);

        var requestTask = environment.Mcp.RequestGrantAsync(
            cancellation.Token);
        await WaitUntilAsync(
            () => File.Exists(approvalStartedPath),
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await requestTask.WaitAsync(
                TimeSpan.FromSeconds(10),
                environment.Token));
        await Task.Delay(500, environment.Token);

        Assert.Empty(environment.Mcp.GrantedScopes);
        Assert.Equal(0, environment.BrokerStarts);
        Assert.Null(environment.ReplacementBroker);
        Assert.True(environment.Registry.IsAvailable);
        var lifecycleEvents = await environment.ReadLifecycleEventsAsync();
        Assert.DoesNotContain(
            lifecycleEvents,
            item => item.EventType == "access_grant_approval_failed");
    }

    [Fact]
    public async Task ToolCallDoesNotReauthorizeAfterExplicitRevokeAndBrokerKill()
    {
        await using var environment =
            await BrokerRecoveryTestEnvironment.CreateAsync();
        var firstBroker = environment.StartInitialBroker();
        await environment.WaitForRegisteredAsync(firstBroker);
        await environment.Mcp.RequestGrantAsync(environment.Token);
        var grantId = Assert.IsType<Guid>(
            environment.Handler.LastGrantId);
        Assert.Equal(
            AccessGrantRevokeStatus.Revoked,
            await environment.Grants.RevokeByIdAsync(
                grantId,
                "recovery-test"));

        firstBroker.Kill(entireProcessTree: true);
        await firstBroker.WaitForExitAsync(environment.Token);
        await environment.WaitForUnavailableAsync();

        var tools = new BridgeVaultTools(environment.Mcp);
        var result = await tools.ReadNote(
            DocumentId,
            offset: 0,
            maxChars: 100,
            environment.Token);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_grant_revoked",
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(1, environment.Handler.ToolCallCount);
        Assert.Equal(0, environment.Handler.ToolExecutionCount);
        Assert.Equal(0, environment.BrokerStarts);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Broker recovery test condition was not reached.");
            }
            await Task.Delay(25);
        }
    }
}
