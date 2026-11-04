using CipherVault.Application.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Semantic;
using CipherVault.Core.Storage.Semantic;
using System.Text.Json;

namespace CipherVault.Application.Tests;

public sealed class SemanticIndexManagementTests
{
    [Fact]
    public async Task RemoteConsentAndRebuildAreIdempotentAndAudited()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var provider = new ManagementProvider();
        var service = new SemanticIndexService(
            fixture.Manager,
            provider,
            1,
            remoteDeploymentAllowed: true,
            endpointDisplay: "https://embedding.example.com/v1",
            fixture.TimeProvider,
            fixture.Idempotency);
        var initial = await service.GetStatusAsync(user);
        Assert.NotNull(initial.Profile);
        Assert.Equal(SemanticIndexState.Disabled, initial.State);
        Assert.True(initial.RemoteConsentRequired);
        var profile = initial.Profile;
        var key = Guid.NewGuid();

        var granted = await service.GrantRemoteConsentAsync(
            key,
            profile.ProfileId,
            profile.Provider.EndpointFingerprint,
            user);
        Assert.True(granted.RemoteConsentGranted);
        Assert.Equal(SemanticIndexState.Building, granted.State);
        var replay = await service.GrantRemoteConsentAsync(
            key,
            profile.ProfileId,
            profile.Provider.EndpointFingerprint,
            user);
        Assert.Equal(granted, replay);

        var reused = await Assert.ThrowsAsync<VaultException>(() =>
            service.GrantRemoteConsentAsync(
                key,
                profile.ProfileId,
                new string('b', 64),
                user));
        Assert.Equal(VaultErrorCode.IdempotencyKeyReused, reused.Code);

        _ = await service.RevokeRemoteConsentAsync(
            Guid.NewGuid(),
            profile.ProfileId,
            profile.Provider.EndpointFingerprint,
            user);
        var revoked = await service.GetStatusAsync(user);
        Assert.False(revoked.RemoteConsentGranted);
        Assert.Equal(SemanticIndexState.Disabled, revoked.State);

        _ = await service.GrantRemoteConsentAsync(
            Guid.NewGuid(),
            profile.ProfileId,
            profile.Provider.EndpointFingerprint,
            user);
        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.UpdateStatusAsync(
                    context,
                    profile.ProfileId,
                    SemanticProfileStatus.Ready,
                    null,
                    fixture.TimeProvider.GetUtcNow(),
                    CancellationToken.None);
                return 1;
            });
        var rebuilding = await service.RebuildAsync(
            Guid.NewGuid(),
            profile.ProfileId,
            user);
        Assert.Equal(SemanticIndexState.Building, rebuilding.State);

        var auditCount = 0;
        var auditDetails = string.Empty;
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT count(*)
                    FROM audit_events
                    WHERE action IN (
                        'embedding.profile_change',
                        'embedding.remote_consent',
                        'embedding.rebuild'
                    );
                    """;
                auditCount = Convert.ToInt32(
                    await command.Command.ExecuteScalarAsync());
                command.Command.CommandText =
                    """
                    SELECT details_json
                    FROM audit_events
                    WHERE action = 'embedding.remote_consent'
                    ORDER BY occurred_at DESC
                    LIMIT 1;
                    """;
                auditDetails = Convert.ToString(
                    await command.Command.ExecuteScalarAsync()) ?? "{}";
                return 1;
            });
        Assert.True(auditCount >= 4);
        using var details = JsonDocument.Parse(auditDetails);
        Assert.True(
            details.RootElement
                .GetProperty("sessionGeneration")
                .GetInt64() > 0);
        Assert.False(details.RootElement.TryGetProperty("apiKey", out _));
    }

    private sealed class ManagementProvider : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(
            "openai-compatible",
            "remote-embed",
            3,
            2048,
            new string('a', 64),
            IsRemote: true);

        public int CountInputTokens(string input) => input.Length;

        public Task<EmbeddingResponse> EmbedAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse(
                Descriptor.ProviderId,
                Descriptor.ModelId,
                Descriptor.Dimension,
                request.Inputs.Select(_ => new[] { 1f, 0f, 0f }).ToArray()));
    }
}
