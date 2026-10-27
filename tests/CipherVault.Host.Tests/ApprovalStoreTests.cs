using System.Text.Json;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class ApprovalStoreTests
{
    [Fact]
    public void CanonicalJsonIgnoresPropertyOrderAndHashesStable()
    {
        using var first = JsonDocument.Parse(
            """{"path":"C:\\Vault","count":2,"nested":{"b":true,"a":null}}""");
        using var second = JsonDocument.Parse(
            """{"nested":{"a":null,"b":true},"count":2,"path":"C:\\Vault"}""");

        var firstJson = CanonicalJson.Normalize(first.RootElement);
        var secondJson = CanonicalJson.Normalize(second.RootElement);

        Assert.Equal(firstJson, secondJson);
        Assert.Equal(
            CanonicalJson.ComputeSha256(first.RootElement),
            CanonicalJson.ComputeSha256(second.RootElement));
    }

    [Fact]
    public void ApprovalIsBoundAndCanOnlyBeConsumedOnce()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ApprovalStore(time);
        using var parameters = JsonDocument.Parse("""{"documentId":"abc"}""");
        var processSessionId = Guid.NewGuid();
        var vaultSessionId = Guid.NewGuid();
        var operation = store.Prepare(
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            parameters.RootElement);
        var request = store.CreateRequest(operation, "永久删除", "删除 abc");
        var decision = store.Decide(
            request,
            approved: true,
            brokerProcessSessionId: Guid.NewGuid());
        Assert.True(decision.Approved);

        var mismatch = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            new string('0', 64));
        Assert.Equal(ApprovalConsumeStatus.ParameterMismatch, mismatch.Status);

        var consumed = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.Consumed, consumed.Status);

        var replay = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.NotFound, replay.Status);
        Assert.Equal("approval_consumed", replay.ReasonCode);
    }

    [Fact]
    public void ApprovalRejectsDifferentProcessAndVaultSessions()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ApprovalStore(time);
        using var parameters = JsonDocument.Parse("""{"documentId":"abc"}""");
        var processSessionId = Guid.NewGuid();
        var vaultSessionId = Guid.NewGuid();
        var operation = store.Prepare(
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            parameters.RootElement);
        var request = store.CreateRequest(operation, "永久删除", "删除 abc");
        store.Decide(
            request,
            approved: true,
            brokerProcessSessionId: Guid.NewGuid());

        var wrongProcess = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            Guid.NewGuid(),
            vaultSessionId,
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.IdentityMismatch, wrongProcess.Status);

        var wrongVault = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            Guid.NewGuid(),
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.IdentityMismatch, wrongVault.Status);

        var consumed = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            operation.ParametersHash);
        Assert.Equal(ApprovalConsumeStatus.Consumed, consumed.Status);
    }

    [Fact]
    public void ExpiredApprovalCannotBeConsumed()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new ApprovalStore(time);
        using var parameters = JsonDocument.Parse("""{"id":"abc"}""");
        var processSessionId = Guid.NewGuid();
        var operation = store.Prepare(
            ApprovalOperationKind.CreateBackup,
            processSessionId,
            vaultSessionId: null,
            parameters.RootElement);
        var request = store.CreateRequest(operation, "备份", "创建备份");
        store.Decide(
            request,
            approved: true,
            brokerProcessSessionId: Guid.NewGuid());
        time.Advance(TimeSpan.FromMinutes(3));

        var consumed = store.Consume(
            operation.OperationId,
            ApprovalOperationKind.CreateBackup,
            processSessionId,
            vaultSessionId: null,
            operation.ParametersHash);

        Assert.Equal(ApprovalConsumeStatus.Expired, consumed.Status);
        Assert.Equal("approval_expired", consumed.ReasonCode);
    }

    [Fact]
    public void RevokeByScopeInvalidatesOnlyMatchingApprovals()
    {
        var store = new ApprovalStore();
        using var parameters = JsonDocument.Parse("""{"documentId":"abc"}""");
        var processA = Guid.NewGuid();
        var processB = Guid.NewGuid();
        var vaultSession = Guid.NewGuid();
        var operationA = PrepareApproved(
            store,
            processA,
            vaultSession,
            parameters.RootElement);
        var operationB = PrepareApproved(
            store,
            processB,
            vaultSession,
            parameters.RootElement);

        Assert.Equal(1, store.RevokeByScope(processA, vaultSession));
        Assert.Equal(
            ApprovalConsumeStatus.NotApproved,
            store.Consume(
                operationA.OperationId,
                ApprovalOperationKind.PurgeNote,
                processA,
                vaultSession,
                operationA.ParametersHash).Status);
        Assert.Equal(
            ApprovalConsumeStatus.Consumed,
            store.Consume(
                operationB.OperationId,
                ApprovalOperationKind.PurgeNote,
                processB,
                vaultSession,
                operationB.ParametersHash).Status);
    }

    private static PreparedOperation PrepareApproved(
        ApprovalStore store,
        Guid processSessionId,
        Guid vaultSessionId,
        JsonElement parameters)
    {
        var operation = store.Prepare(
            ApprovalOperationKind.PurgeNote,
            processSessionId,
            vaultSessionId,
            parameters);
        var request = store.CreateRequest(
            operation,
            "永久删除",
            "删除 abc");
        store.Decide(
            request,
            approved: true,
            brokerProcessSessionId: Guid.NewGuid());
        return operation;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
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
