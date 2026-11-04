using CipherVault.Core.Semantic;
using CipherVault.Core.Documents;
using CipherVault.Core.Storage.Semantic;

namespace CipherVault.Application.Tests;

public sealed class SemanticProfileStoreTests
{
    [Fact]
    public async Task ActiveProfileAndConsentRoundTrip()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var now = fixture.TimeProvider.GetUtcNow();
        var descriptor = new EmbeddingProviderDescriptor(
            "openai-compatible",
            "local-embed",
            4,
            2048,
            new string('b', 64),
            IsRemote: true);
        var profileId = SemanticProfileIdentity.CreateProfileId(
            descriptor,
            profileVersion: 1);
        var profile = new SemanticProfile(
            profileId,
            descriptor,
            ProfileVersion: 1,
            ChunkerVersion: SemanticProfileIdentity.ChunkerVersion,
            IsActive: true,
            SemanticProfileStatus.Disabled,
            LastErrorCode: null,
            now,
            now,
            ReadyAt: null);
        SemanticProfile? active = null;
        SemanticRemoteConsent? consent = null;

        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.ActivateAsync(
                    context,
                    profile,
                    now,
                    CancellationToken.None);
                active = await SemanticProfileStore.GetActiveAsync(
                    context,
                    CancellationToken.None);
                await SemanticConsentStore.GrantAsync(
                    context,
                    profileId,
                    descriptor.EndpointFingerprint,
                    now,
                    CancellationToken.None);
                consent = await SemanticConsentStore.GetAsync(
                    context,
                    profileId,
                    CancellationToken.None);
                return 1;
            });

        Assert.NotNull(active);
        Assert.Equal(profileId, active.ProfileId);
        Assert.True(active.IsActive);
        Assert.Equal(SemanticProfileStatus.Disabled, active.Status);
        Assert.NotNull(consent);
        Assert.Null(consent.RevokedAt);

        var revokedAt = now.AddMinutes(1);
        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticConsentStore.RevokeAsync(
                    context,
                    profileId,
                    revokedAt,
                    CancellationToken.None);
                consent = await SemanticConsentStore.GetAsync(
                    context,
                    profileId,
                    CancellationToken.None);
                return 1;
            });
        Assert.Equal(revokedAt, consent?.RevokedAt);
    }

    [Fact]
    public async Task ActivatingNewProfileInvalidatesPreviousProfile()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var now = fixture.TimeProvider.GetUtcNow();
        var firstDescriptor = Descriptor('b', "first");
        var secondDescriptor = Descriptor('c', "second");
        var first = Profile(
            firstDescriptor,
            now,
            SemanticProfileStatus.Ready);
        var second = Profile(
            secondDescriptor,
            now.AddMinutes(1),
            SemanticProfileStatus.Building);

        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.ActivateAsync(
                    context,
                    first,
                    now,
                    CancellationToken.None);
                await SemanticProfileStore.ActivateAsync(
                    context,
                    second,
                    now.AddMinutes(1),
                    CancellationToken.None);
                return 1;
            });

        SemanticProfile? active = null;
        SemanticProfile? previous = null;
        _ = await fixture.QueryAsync(
            async context =>
            {
                active = await SemanticProfileStore.GetActiveAsync(
                    context,
                    CancellationToken.None);
                previous = await SemanticProfileStore.FindAsync(
                    context,
                    first.ProfileId,
                    CancellationToken.None);
                return 1;
            });

        Assert.Equal(second.ProfileId, active?.ProfileId);
        Assert.False(previous?.IsActive);
        Assert.Equal(SemanticProfileStatus.Invalid, previous?.Status);
    }

    [Fact]
    public async Task ChunkStoreReplacesDocumentVectorsAndReadsMetadata()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Vector",
                "notes/vector.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Vector\n\nBody",
                []),
            user);
        var now = fixture.TimeProvider.GetUtcNow();
        var descriptor = Descriptor('d', "vector-model");
        var profile = Profile(
            descriptor,
            now,
            SemanticProfileStatus.Ready);
        var vector = SemanticVectorCodec.NormalizeAndEncode([1f, 2f, 3f, 4f]);
        var first = new SemanticChunkRecord(
            document.DocumentId,
            profile.ProfileId,
            document.Revision,
            0,
            ["Vector"],
            0,
            8,
            SemanticProfileIdentity.Sha256Hex("# Vector"),
            4,
            vector,
            now);
        var second = first with
        {
            ChunkOrdinal = 1,
            StartOffset = 8,
            EndOffset = 14,
            ContentHash = SemanticProfileIdentity.Sha256Hex("\n\nBody")
        };
        var countAfterInsert = 0;
        var countAfterReplace = 0;
        var readOrdinal = -1;
        var readDimension = 0;

        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.ActivateAsync(
                    context,
                    profile,
                    now,
                    CancellationToken.None);
                await SemanticChunkStore.ReplaceDocumentAsync(
                    context,
                    document.DocumentId,
                    profile.ProfileId,
                    document.Revision,
                    4,
                    [first, second],
                    CancellationToken.None);
                countAfterInsert = await SemanticChunkStore.CountAsync(
                    context,
                    profile.ProfileId,
                    CancellationToken.None);
                await SemanticChunkStore.ReplaceDocumentAsync(
                    context,
                    document.DocumentId,
                    profile.ProfileId,
                    document.Revision,
                    4,
                    [first],
                    CancellationToken.None);
                countAfterReplace = await SemanticChunkStore.CountAsync(
                    context,
                    profile.ProfileId,
                    CancellationToken.None);
                var chunks = await SemanticChunkStore.ReadDocumentAsync(
                    context,
                    document.DocumentId,
                    profile.ProfileId,
                    CancellationToken.None);
                readOrdinal = chunks.Single().ChunkOrdinal;
                readDimension = chunks.Single().Dimension;
                return 1;
            });

        Assert.Equal(2, countAfterInsert);
        Assert.Equal(1, countAfterReplace);
        Assert.Equal(0, readOrdinal);
        Assert.Equal(4, readDimension);
    }

    [Fact]
    public async Task EveryDocumentMutationPathQueuesSemanticOutbox()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var now = fixture.TimeProvider.GetUtcNow();
        var descriptor = Descriptor('e', "outbox-model");
        var profile = Profile(
            descriptor,
            now,
            SemanticProfileStatus.Ready);
        _ = await fixture.QueryAsync(
            async context =>
            {
                await SemanticProfileStore.ActivateAsync(
                    context,
                    profile,
                    now,
                    CancellationToken.None);
                return 1;
            });

        var created = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Outbox",
                "notes/outbox.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Outbox\n",
                []),
            user);
        var createdJob = await ReadJobAsync(
            fixture,
            created.DocumentId,
            profile.ProfileId);
        Assert.Equal(("index", 1, "pending"), createdJob);

        var updated = await fixture.Documents.UpdateAsync(
            created.DocumentId,
            created.Revision,
            new UpdateDocumentRequest(
                "Outbox",
                "notes/outbox.md",
                "# Outbox changed\n",
                []),
            user);
        var updatedJob = await ReadJobAsync(
            fixture,
            created.DocumentId,
            profile.ProfileId);
        Assert.Equal(("index", 2, "pending"), updatedJob);

        var deleted = await fixture.Documents.DeleteAsync(
            created.DocumentId,
            updated.Revision,
            user);
        var deletedJob = await ReadJobAsync(
            fixture,
            created.DocumentId,
            profile.ProfileId);
        Assert.Equal(("delete", 3, "pending"), deletedJob);

        var restored = await fixture.Trash.RestoreAsync(
            created.DocumentId,
            deleted.Revision,
            null,
            user);
        var restoredJob = await ReadJobAsync(
            fixture,
            created.DocumentId,
            profile.ProfileId);
        Assert.Equal(("index", restored.Revision, "pending"), restoredJob);

        var deletedAgain = await fixture.Documents.DeleteAsync(
            created.DocumentId,
            restored.Revision,
            user);
        ((ManualTimeProvider)fixture.TimeProvider).MoveWallClock(
            TimeSpan.FromDays(31));
        _ = await fixture.Trash.PurgeAsync(
            created.DocumentId,
            deletedAgain.Revision,
            user);
        var purgedJob = await ReadJobAsync(
            fixture,
            created.DocumentId,
            profile.ProfileId);
        Assert.Equal(("purge", deletedAgain.Revision, "pending"), purgedJob);
    }

    private static async Task<(string Type, int Revision, string Status)>
        ReadJobAsync(
            VaultApplicationFixture fixture,
            Guid documentId,
            string profileId)
    {
        var type = string.Empty;
        var revision = 0;
        var status = string.Empty;
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT job_type, requested_revision, status
                    FROM semantic_index_jobs
                    WHERE document_id = $documentId
                      AND profile_id = $profileId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    documentId.ToString("D"));
                command.Command.Parameters.AddWithValue(
                    "$profileId",
                    profileId);
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                type = reader.GetString(0);
                revision = reader.GetInt32(1);
                status = reader.GetString(2);
                return 1;
            });
        return (type, revision, status);
    }

    private static EmbeddingProviderDescriptor Descriptor(
        char fingerprint,
        string model) =>
        new(
            "openai-compatible",
            model,
            4,
            2048,
            new string(fingerprint, 64),
            IsRemote: false);

    private static SemanticProfile Profile(
        EmbeddingProviderDescriptor descriptor,
        DateTimeOffset now,
        SemanticProfileStatus status) =>
        new(
            SemanticProfileIdentity.CreateProfileId(descriptor, 1),
            descriptor,
            1,
            SemanticProfileIdentity.ChunkerVersion,
            IsActive: true,
            status,
            LastErrorCode: null,
            now,
            now,
            status == SemanticProfileStatus.Ready ? now : null);
}
