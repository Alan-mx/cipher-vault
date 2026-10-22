using System.Text;
using CipherVault.Core.Configuration;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class AccessGrantServiceTests
{
    [Fact]
    public async Task ApprovedGrantStoresHashAndReturnsPlaintextOnce()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(timeProvider: time);

        var outcome = await service.RequestAsync(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadWrite,
            AccessGrantScope.Read | AccessGrantScope.Write,
            Generation(now, TimeSpan.FromHours(1)),
            (_, _) => Task.FromResult(
                new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read | AccessGrantScope.Write)));

        Assert.Equal(AccessGrantRequestStatus.Approved, outcome.Status);
        var issued = Assert.IsType<IssuedAccessGrant>(outcome.IssuedGrant);
        Assert.NotEmpty(issued.PlaintextSecret);
        Assert.NotNull(issued.Record.SecretHash);
        Assert.Equal(AccessGrantState.Active, issued.Record.State);
        Assert.Equal(
            AccessGrantScope.Read | AccessGrantScope.Write,
            issued.Record.Scopes);
        Assert.NotEqual(
            issued.PlaintextSecret,
            Encoding.UTF8.GetString(issued.Record.SecretHash!));
    }

    [Fact]
    public async Task ConcurrentRequestsForSameInstanceShareApproval()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));
        var clientInstanceId = Guid.NewGuid();
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<AccessGrantApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<AccessGrantApprovalResult> Handler(
            AccessGrantApprovalRequest _,
            CancellationToken __)
        {
            Interlocked.Increment(ref calls);
            handlerStarted.TrySetResult();
            return await approval.Task;
        }

        var first = service.RequestAsync(
            "agent",
            clientInstanceId,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            Generation(now, TimeSpan.FromHours(1)),
            Handler);
        await handlerStarted.Task;
        var second = service.RequestAsync(
            "agent",
            clientInstanceId,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            Generation(now, TimeSpan.FromHours(1)),
            Handler);

        approval.SetResult(new AccessGrantApprovalResult(
            true,
            AccessGrantScope.Read));
        var firstOutcome = await first;
        var secondOutcome = await second;

        Assert.Equal(1, calls);
        Assert.Same(firstOutcome.IssuedGrant, secondOutcome.IssuedGrant);
    }

    [Fact]
    public async Task DenialStartsCooldownForClient()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(timeProvider: time);
        var generation = Generation(now, TimeSpan.FromHours(1));

        var denied = await service.RequestAsync(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(false)));
        Assert.Equal(AccessGrantRequestStatus.Denied, denied.Status);

        var cooled = await service.RequestAsync(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            (_, _) => throw new InvalidOperationException(
                "Approval must not run during cooldown."));
        Assert.Equal(AccessGrantRequestStatus.Cooldown, cooled.Status);

        time.Advance(TimeSpan.FromSeconds(31));
        var approved = await service.RequestAsync(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                AccessGrantScope.Read)));
        Assert.Equal(AccessGrantRequestStatus.Approved, approved.Status);
    }

    [Fact]
    public async Task ValidationRejectsDifferentVaultSession()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(timeProvider: time);
        var generation = Generation(now, TimeSpan.FromHours(1));
        var issued = await ApproveAsync(
            service,
            now,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation);

        var valid = await service.ValidateAsync(
            "agent",
            issued.PlaintextSecret,
            generation);
        Assert.Equal(AccessGrantValidationStatus.Valid, valid.Status);

        var mismatch = await service.ValidateAsync(
            "agent",
            issued.PlaintextSecret,
            new AccessGrantVaultGeneration(
                Guid.NewGuid(),
                now + TimeSpan.FromHours(1)));
        Assert.Equal(AccessGrantValidationStatus.Revoked, mismatch.Status);
    }

    [Fact]
    public async Task VerifiedBridgeGrantBindsProcessSession()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));
        var generation = Generation(now, TimeSpan.FromHours(1));
        var processSessionId = Guid.NewGuid();

        var outcome = await service.RequestAsync(
            "bridge",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                AccessGrantScope.Read)),
            AgentClientIdentityKind.VerifiedBridgeProcess,
            processSessionId);
        var issued = Assert.IsType<IssuedAccessGrant>(outcome.IssuedGrant);
        Assert.Equal(AgentClientIdentityKind.VerifiedBridgeProcess, issued.Record.IdentityKind);
        Assert.Equal(processSessionId, issued.Record.VerifiedProcessSessionId);

        var validation = await service.ValidateAsync(
            "bridge",
            issued.PlaintextSecret,
            generation);

        Assert.Equal(AccessGrantValidationStatus.Valid, validation.Status);
        Assert.Equal(AgentClientIdentityKind.VerifiedBridgeProcess, validation.Grant!.IdentityKind);
        Assert.Equal(processSessionId, validation.Grant.VerifiedProcessSessionId);
        Assert.True(service.RecordUse(validation.Grant));
        Assert.False(service.RecordUse(validation.Grant with
        {
            VerifiedProcessSessionId = Guid.NewGuid()
        }));
    }

    [Fact]
    public async Task VerifiedBridgeGrantRequiresProcessSession()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RequestAsync(
                "bridge",
                Guid.NewGuid(),
                McpAccessMode.ReadOnly,
                AccessGrantScope.Read,
                Generation(now, TimeSpan.FromHours(1)),
                (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read)),
                AgentClientIdentityKind.VerifiedBridgeProcess,
                verifiedProcessSessionId: null));

        Assert.Contains("process session", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationExpiresAtIdleAndHardLimits()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(
            timeProvider: time,
            idleLifetime: TimeSpan.FromSeconds(10),
            hardLifetime: TimeSpan.FromSeconds(20));
        var generation = Generation(now, TimeSpan.FromHours(1));
        var issued = await ApproveAsync(
            service,
            now,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation);

        time.Advance(TimeSpan.FromSeconds(8));
        var validation = await service.ValidateAsync(
            "agent",
            issued.PlaintextSecret,
            generation);
        Assert.Equal(AccessGrantValidationStatus.Valid, validation.Status);
        Assert.True(service.RecordUse(validation.Grant!));

        time.Advance(TimeSpan.FromSeconds(8));
        var beforeHard = await service.ValidateAsync(
            "agent",
            issued.PlaintextSecret,
            generation);
        Assert.Equal(AccessGrantValidationStatus.Valid, beforeHard.Status);

        time.Advance(TimeSpan.FromSeconds(5));
        var expired = await service.ValidateAsync(
            "agent",
            issued.PlaintextSecret,
            generation);
        Assert.Equal(AccessGrantValidationStatus.Expired, expired.Status);
        Assert.False(service.RecordUse(new AccessGrantContext(
            issued.Record.GrantId,
            "agent",
            issued.Record.ClientInstanceId,
            AccessGrantScope.Read,
            generation.SessionId)));
        Assert.Equal(
            AccessGrantValidationStatus.Expired,
            (await service.ValidateAsync(
                "agent",
                issued.PlaintextSecret,
                generation)).Status);

        service.SweepExpired();
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task RevokeClientRemovesOnlyThatClientsGrants()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(timeProvider: time);
        var generation = Generation(now, TimeSpan.FromHours(1));
        var revoked = await ApproveAsync(
            service,
            now,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            "agent-a");
        var other = await ApproveAsync(
            service,
            now,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            "agent-b");

        Assert.Equal(1, await service.RevokeClientAsync("agent-a", "token-revoked"));
        Assert.Equal(
            AccessGrantValidationStatus.Revoked,
            (await service.ValidateAsync(
                "agent-a",
                revoked.PlaintextSecret,
                generation)).Status);
        Assert.Equal(
            AccessGrantValidationStatus.Invalid,
            (await service.ValidateAsync(
                "agent-b",
                "wrong-secret",
                generation)).Status);
        Assert.Equal(
            AccessGrantValidationStatus.Valid,
            (await service.ValidateAsync(
                "agent-b",
                other.PlaintextSecret,
                generation)).Status);
    }

    [Fact]
    public async Task RevokeAllCancelsPendingApproval()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<AccessGrantApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var request = service.RequestAsync(
            "agent",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            Generation(now, TimeSpan.FromHours(1)),
            async (_, _) =>
            {
                handlerStarted.TrySetResult();
                return await approval.Task;
            });
        await handlerStarted.Task;

        Assert.Equal(1, await service.RevokeAllAsync("vault-locked"));
        approval.SetResult(new AccessGrantApprovalResult(
            true,
            AccessGrantScope.Read));
        var outcome = await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AccessGrantRequestStatus.Denied, outcome.Status);
        Assert.Equal("access_grant_revoked", outcome.ReasonCode);
        Assert.Null(outcome.IssuedGrant);
    }

    [Fact]
    public async Task RevokeRequiresMatchingClientAndSecret()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));
        var generation = Generation(now, TimeSpan.FromHours(1));
        var issued = await ApproveAsync(
            service,
            now,
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation);

        Assert.Equal(
            AccessGrantRevokeStatus.Invalid,
            await service.RevokeAsync(
                "other-agent",
                issued.Record.GrantId,
                issued.PlaintextSecret));
        Assert.Equal(
            AccessGrantRevokeStatus.Invalid,
            await service.RevokeAsync(
                "agent",
                issued.Record.GrantId,
                "wrong-secret"));
        Assert.Equal(
            AccessGrantRevokeStatus.Revoked,
            await service.RevokeAsync(
                "agent",
                issued.Record.GrantId,
                issued.PlaintextSecret));
        Assert.Equal(
            AccessGrantRevokeStatus.AlreadyRemoved,
            await service.RevokeAsync(
                "agent",
                issued.Record.GrantId,
                issued.PlaintextSecret));
    }

    [Fact]
    public void DeleteScopeImpliesReadAndWrite()
    {
        Assert.Equal(
            AccessGrantScope.Read |
            AccessGrantScope.Write |
            AccessGrantScope.Delete,
            AccessGrantScopes.Normalize(AccessGrantScope.Delete));
        Assert.Equal(
            AccessGrantScope.Read,
            AccessGrantScopes.Ceiling(McpAccessMode.ReadOnly));
        Assert.Equal(
            AccessGrantScope.Read |
            AccessGrantScope.Write |
            AccessGrantScope.Delete,
            AccessGrantScopes.Ceiling(McpAccessMode.ReadWrite));
        Assert.Equal(
            ["delete", "read", "write"],
            AccessGrantScopes.ToNames(AccessGrantScope.Delete)
                .OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RevokeByIdUsesCustomReason()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var securityLog = new RecordingSecurityLog();
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now),
            securityLog: securityLog);
        var generation = Generation(now, TimeSpan.FromHours(1));
        var issued = await ApproveBridgeAsync(
            service,
            now,
            AccessGrantScope.Read,
            generation,
            Guid.NewGuid());

        Assert.Equal(
            AccessGrantRevokeStatus.Revoked,
            await service.RevokeByIdAsync(
                issued.Record.GrantId,
                "bridge-disconnected"));

        var revoked = Assert.Single(
            securityLog.Events,
            item => string.Equals(item.EventType, "grant_revoked"));
        Assert.Equal("bridge-disconnected", revoked.ReasonCode);
        Assert.Equal("bridge", revoked.ClientId);
        Assert.Equal(issued.Record.GrantId, revoked.GrantId);
    }

    [Fact]
    public async Task UseBridgeGrantRejectsProcessAndReportsExpired()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var time = new MutableTimeProvider(now);
        var service = new AccessGrantService(
            timeProvider: time,
            idleLifetime: TimeSpan.FromSeconds(10));
        var generation = Generation(now, TimeSpan.FromHours(1));
        var issued = await ApproveBridgeAsync(
            service,
            now,
            AccessGrantScope.Read,
            generation,
            Guid.NewGuid());
        var context = Context(issued.Record);

        Assert.Equal(
            AccessGrantValidationStatus.Revoked,
            service.UseBridgeGrant(context with
            {
                VerifiedProcessSessionId = Guid.NewGuid()
            }).Status);
        Assert.Equal(
            AccessGrantValidationStatus.Valid,
            service.UseBridgeGrant(context).Status);

        time.Advance(TimeSpan.FromSeconds(10));
        var expired = service.UseBridgeGrant(context);
        Assert.Equal(AccessGrantValidationStatus.Expired, expired.Status);
        Assert.Equal("access_grant_expired", expired.ReasonCode);
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task RevokeByProcessSessionCancelsOnlyThatBridge()
    {
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        var service = new AccessGrantService(
            timeProvider: new MutableTimeProvider(now));
        var generation = Generation(now, TimeSpan.FromHours(1));
        var processA = Guid.NewGuid();
        var processB = Guid.NewGuid();
        var activeA = await ApproveBridgeAsync(
            service,
            now,
            AccessGrantScope.Read,
            generation,
            processA,
            "bridge-a");
        var activeB = await ApproveBridgeAsync(
            service,
            now,
            AccessGrantScope.Read,
            generation,
            processB,
            "bridge-b");
        var handlerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingRequest = service.RequestAsync(
            "bridge-a",
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            AccessGrantScope.Read,
            generation,
            async (_, token) =>
            {
                handlerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new AccessGrantApprovalResult(
                    true,
                    AccessGrantScope.Read);
            },
            AgentClientIdentityKind.VerifiedBridgeProcess,
            processA);
        await handlerStarted.Task;

        Assert.Equal(
            2,
            await service.RevokeByProcessSessionAsync(
                processA,
                "bridge-disconnected"));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pendingRequest.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            AccessGrantValidationStatus.Revoked,
            service.UseBridgeGrant(Context(activeA.Record)).Status);
        Assert.Equal(
            AccessGrantValidationStatus.Valid,
            service.UseBridgeGrant(Context(activeB.Record)).Status);
        Assert.DoesNotContain(
            service.List(),
            item => item.ClientId == "bridge-a");
    }

    private static async Task<IssuedAccessGrant> ApproveAsync(
        AccessGrantService service,
        DateTimeOffset now,
        McpAccessMode accessMode,
        AccessGrantScope scopes,
        AccessGrantVaultGeneration generation,
        string clientId = "agent")
    {
        var outcome = await service.RequestAsync(
            clientId,
            Guid.NewGuid(),
            accessMode,
            scopes,
            generation,
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                scopes)));
        Assert.Equal(AccessGrantRequestStatus.Approved, outcome.Status);
        return Assert.IsType<IssuedAccessGrant>(outcome.IssuedGrant);
    }

    private static AccessGrantVaultGeneration Generation(
        DateTimeOffset now,
        TimeSpan hardLifetime)
    {
        return new AccessGrantVaultGeneration(
            Guid.NewGuid(),
            now + hardLifetime);
    }

    private static async Task<IssuedAccessGrant> ApproveBridgeAsync(
        AccessGrantService service,
        DateTimeOffset now,
        AccessGrantScope scopes,
        AccessGrantVaultGeneration generation,
        Guid processSessionId,
        string clientId = "bridge")
    {
        var outcome = await service.RequestAsync(
            clientId,
            Guid.NewGuid(),
            McpAccessMode.ReadOnly,
            scopes,
            generation,
            (_, _) => Task.FromResult(new AccessGrantApprovalResult(
                true,
                scopes)),
            AgentClientIdentityKind.VerifiedBridgeProcess,
            processSessionId);
        Assert.Equal(AccessGrantRequestStatus.Approved, outcome.Status);
        return Assert.IsType<IssuedAccessGrant>(outcome.IssuedGrant);
    }

    private static AccessGrantContext Context(AccessGrantRecord record)
    {
        return new AccessGrantContext(
            record.GrantId,
            record.ClientId,
            record.ClientInstanceId,
            record.Scopes,
            record.VaultSessionId!.Value,
            record.IdentityKind,
            record.VerifiedProcessSessionId);
    }

    private sealed class RecordingSecurityLog : ISecurityEventLog
    {
        public IReadOnlyList<HostSecurityLogEvent> Events => _events;
        private readonly List<HostSecurityLogEvent> _events = [];

        public Task WriteAsync(
            HostSecurityLogEvent securityEvent,
            CancellationToken cancellationToken = default)
        {
            _events.Add(securityEvent);
            return Task.CompletedTask;
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
