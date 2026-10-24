using System.Diagnostics;
using System.Text.Json;
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
    }

    [Fact]
    public async Task ToolCallReauthorizesAfterBrokerKillWithoutDuplicateExecution()
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
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            DocumentId,
            document.RootElement
                .GetProperty("document")
                .GetProperty("documentId")
                .GetString());
        Assert.Equal(2, environment.Handler.ToolCallCount);
        Assert.Equal(1, environment.Handler.ToolExecutionCount);
        Assert.Equal(1, environment.BrokerStarts);
        var secondBroker = Assert.IsType<Process>(
            environment.ReplacementBroker);
        await environment.WaitForRegisteredAsync(secondBroker);
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
