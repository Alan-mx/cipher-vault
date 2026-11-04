using CipherVault.Application.Relations;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Relations;

namespace CipherVault.Application.Tests;

public sealed class RelationSuggestionServiceTests
{
    [Fact]
    public async Task CreateUpdateListAndDiscardUseRevisionAndIdempotency()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var createKey = Guid.NewGuid();

        var created = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            createKey);
        Assert.Equal(
            RelationSuggestionStatus.PendingReview,
            created.Metadata.Status);
        Assert.Equal(1, created.Metadata.SuggestionRevision);
        var replay = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            createKey);
        Assert.Equal(created.Metadata, replay.Metadata);
        Assert.Equal(created.Reason, replay.Reason);
        Assert.Equal(created.ModelName, replay.ModelName);
        Assert.Equal(
            created.Evidence.Select(item => (
                item.DocumentId,
                item.DocumentRevision,
                item.HeadingPath,
                item.Excerpt)),
            replay.Evidence.Select(item => (
                item.DocumentId,
                item.DocumentRevision,
                item.HeadingPath,
                item.Excerpt)));
        var reusedKey = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                Request(source, target) with
                {
                    Reason = "A different request using the same key."
                },
                mcp,
                createKey));
        Assert.Equal(
            VaultErrorCode.IdempotencyKeyReused,
            reusedKey.Code);
        var auditDetails = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT details_json
                    FROM audit_events
                    WHERE action = 'relation_suggestion.create';
                    """;
                return Convert.ToString(
                    await command.Command.ExecuteScalarAsync()) ?? "{}";
            });
        Assert.DoesNotContain(
            "The source depends on the target.",
            auditDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Source evidence",
            auditDetails,
            StringComparison.Ordinal);

        var duplicate = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                Request(source, target),
                mcp,
                Guid.NewGuid()));
        Assert.Equal(
            VaultErrorCode.RelationSuggestionExists,
            duplicate.Code);

        var list = await fixture.RelationSuggestions.ListAsync(
            new RelationSuggestionQuery(
                RelationSuggestionStatus.PendingReview,
                null,
                null,
                null,
                null,
                null,
                new CursorPageRequest(50)),
            mcp);
        Assert.Single(list.Items);
        Assert.Equal(source.DocumentId, list.Items[0].SourceDocument.DocumentId);

        var updateKey = createKey;
        var updated = await fixture.RelationSuggestions.UpdateAsync(
            created.Metadata.SuggestionId,
            1,
            new UpdateRelationSuggestionRequest(
                "Updated reason",
                "model-2",
                null,
                null,
                [
                    Evidence(
                        source.DocumentId,
                        source.Revision,
                        "Updated evidence")
                ]),
            mcp,
            updateKey);
        Assert.Equal(2, updated.Metadata.SuggestionRevision);
        Assert.Equal("Updated reason", updated.Reason);

        var discardKey = createKey;
        var discarded = await fixture.RelationSuggestions.DiscardAsync(
            updated.Metadata.SuggestionId,
            2,
            mcp,
            discardKey);
        Assert.Equal(
            updated.Metadata.SuggestionId,
            discarded.SuggestionId);
        var discardReplay =
            await fixture.RelationSuggestions.DiscardAsync(
                updated.Metadata.SuggestionId,
                2,
                mcp,
                discardKey);
        Assert.Equal(discarded, discardReplay);
        var missing = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.GetAsync(
                updated.Metadata.SuggestionId,
                mcp));
        Assert.Equal(
            VaultErrorCode.RelationSuggestionNotFound,
            missing.Code);
    }

    [Fact]
    public async Task ApplyAlreadyExistsAndRetractMaintainFormalRelations()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");

        var first = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        var applied = await fixture.RelationReview.ApplyAsync(
            first.Metadata.SuggestionId,
            1,
            user,
            Guid.NewGuid());
        Assert.Equal(RelationSuggestionStatus.Applied, applied.Status);
        Assert.Equal(
            RelationSuggestionApplyResult.Inserted,
            applied.ApplyResult);
        Assert.NotNull(applied.RelationId);

        var second = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        var alreadyExists = await fixture.RelationReview.ApplyAsync(
            second.Metadata.SuggestionId,
            1,
            user,
            Guid.NewGuid());
        Assert.Equal(
            RelationSuggestionApplyResult.AlreadyExists,
            alreadyExists.ApplyResult);
        Assert.Equal(applied.RelationId, alreadyExists.RelationId);

        var relations =
            await fixture.RelationReview.ListDocumentRelationsAsync(
                source.DocumentId,
                user);
        var relation = Assert.Single(relations.Items);
        Assert.Equal(
            DocumentRelationDirection.Outgoing,
            relation.Direction);
        Assert.False(relations.Truncated);

        _ = await fixture.RelationReview.RetractAsync(
            relation.RelationId,
            user,
            Guid.NewGuid());
        relations = await fixture.RelationReview.ListDocumentRelationsAsync(
            source.DocumentId,
            user);
        Assert.Empty(relations.Items);

        var third = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        var reapplied = await fixture.RelationReview.ApplyAsync(
            third.Metadata.SuggestionId,
            1,
            user,
            Guid.NewGuid());
        Assert.Equal(
            RelationSuggestionApplyResult.Inserted,
            reapplied.ApplyResult);
        Assert.NotEqual(applied.RelationId, reapplied.RelationId);

        _ = await fixture.EnableDirectWriteAsync();
        var forbidden = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationReview.ApplyAsync(
                third.Metadata.SuggestionId,
                2,
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.Forbidden, forbidden.Code);
    }

    [Fact]
    public async Task DocumentUpdateMarksPendingSuggestionStaleAndUpdateRebuildsIt()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var created = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());

        _ = await fixture.Documents.UpdateAsync(
            target.DocumentId,
            target.Revision,
            new UpdateDocumentRequest(
                "Target",
                "target.md",
                "# Target\n\nUpdated.",
                []),
            user);
        var stale = await fixture.RelationSuggestions.GetAsync(
            created.Metadata.SuggestionId,
            user);
        Assert.Equal(RelationSuggestionStatus.Stale, stale.Metadata.Status);
        Assert.Equal(
            RelationStaleReason.TargetRevisionChanged,
            stale.Metadata.StaleReasonCode);

        var rebuilt = await fixture.RelationSuggestions.UpdateAsync(
            created.Metadata.SuggestionId,
            2,
            new UpdateRelationSuggestionRequest(
                "Rebuilt reason",
                "model-2",
                null,
                null,
                [
                    Evidence(
                        target.DocumentId,
                        target.Revision + 1,
                        "Updated evidence")
                ]),
            mcp,
            Guid.NewGuid());
        Assert.Equal(
            RelationSuggestionStatus.PendingReview,
            rebuilt.Metadata.Status);
        Assert.Equal(3, rebuilt.Metadata.SuggestionRevision);
        Assert.Equal(target.Revision + 1, rebuilt.Metadata.TargetDocumentRevision);

        var deleted = await fixture.Documents.DeleteAsync(
            target.DocumentId,
            target.Revision + 1,
            user);
        stale = await fixture.RelationSuggestions.GetAsync(
            created.Metadata.SuggestionId,
            user);
        Assert.Equal(RelationSuggestionStatus.Stale, stale.Metadata.Status);
        Assert.Equal(
            RelationStaleReason.TargetTrashed,
            stale.Metadata.StaleReasonCode);

        var restored = await fixture.Trash.RestoreAsync(
            target.DocumentId,
            deleted.Revision,
            replacementPath: null,
            user);
        stale = await fixture.RelationSuggestions.GetAsync(
            created.Metadata.SuggestionId,
            user);
        Assert.Equal(RelationSuggestionStatus.Stale, stale.Metadata.Status);

        var trashed = await fixture.Documents.DeleteAsync(
            target.DocumentId,
            restored.Revision,
            user);
        ((ManualTimeProvider)fixture.TimeProvider).MoveWallClock(
            TimeSpan.FromDays(31));
        _ = await fixture.Trash.PurgeAsync(
            target.DocumentId,
            trashed.Revision,
            user);
        var missing = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.GetAsync(
                created.Metadata.SuggestionId,
                user));
        Assert.Equal(
            VaultErrorCode.RelationSuggestionNotFound,
            missing.Code);
    }

    [Fact]
    public async Task ApplyRevisionMismatchCommitsStaleOutcomeAndReplaysIt()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var created = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        _ = await fixture.QueryAsync(
            async context =>
            {
                await context.BeginTransactionAsync();
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    UPDATE documents
                    SET revision = revision + 1
                    WHERE document_id = $documentId;
                    """;
                command.Command.Parameters.AddWithValue(
                    "$documentId",
                    target.DocumentId.ToString("D"));
                await command.Command.ExecuteNonQueryAsync();
                await context.CommitTransactionAsync();
                return 1;
            });
        var key = Guid.NewGuid();

        var outcome = await fixture.RelationReview.ApplyAsync(
            created.Metadata.SuggestionId,
            1,
            user,
            key);
        Assert.Equal(RelationSuggestionStatus.Stale, outcome.Status);
        Assert.Equal(
            RelationStaleReason.TargetRevisionChanged,
            outcome.StaleReason);
        var replay = await fixture.RelationReview.ApplyAsync(
            created.Metadata.SuggestionId,
            1,
            user,
            key);
        Assert.Equal(outcome, replay);
        var stale = await fixture.RelationSuggestions.GetAsync(
            created.Metadata.SuggestionId,
            user);
        Assert.Equal(RelationSuggestionStatus.Stale, stale.Metadata.Status);
    }

    [Fact]
    public async Task RejectAndTerminalCleanupRetainAuditOnly()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var created = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        var rejected = await fixture.RelationReview.RejectAsync(
            created.Metadata.SuggestionId,
            1,
            user,
            Guid.NewGuid());
        Assert.Equal(RelationSuggestionStatus.Rejected, rejected.Status);

        ((ManualTimeProvider)fixture.TimeProvider).MoveWallClock(
            TimeSpan.FromDays(31));
        var cleanup = await fixture.RelationMaintenance.CleanupAsync();
        Assert.Equal(1, cleanup.DeletedSuggestions);
        Assert.True(cleanup.DeletedIdempotencyRecords >= 1);
        var missing = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.GetAsync(
                created.Metadata.SuggestionId,
                user));
        Assert.Equal(
            VaultErrorCode.RelationSuggestionNotFound,
            missing.Code);
    }

    [Fact]
    public async Task ListUsesStableCursorAndRejectsFilterChanges()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        foreach (var relationType in new[]
                 {
                     RelationType.DependsOn,
                     RelationType.Implements,
                     RelationType.Related
                 })
        {
            _ = await fixture.RelationSuggestions.CreateAsync(
                Request(source, target) with
                {
                    RelationType = relationType
                },
                mcp,
                Guid.NewGuid());
        }

        var first = await fixture.RelationSuggestions.ListAsync(
            new RelationSuggestionQuery(
                RelationSuggestionStatus.PendingReview,
                null,
                null,
                null,
                null,
                null,
                new CursorPageRequest(1)),
            mcp);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);

        var second = await fixture.RelationSuggestions.ListAsync(
            new RelationSuggestionQuery(
                RelationSuggestionStatus.PendingReview,
                null,
                null,
                null,
                null,
                null,
                new CursorPageRequest(1, first.NextCursor)),
            mcp);
        Assert.Single(second.Items);
        Assert.NotEqual(
            first.Items[0].SuggestionId,
            second.Items[0].SuggestionId);

        var invalid = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.ListAsync(
                new RelationSuggestionQuery(
                    RelationSuggestionStatus.PendingReview,
                    RelationType.DependsOn,
                    null,
                    null,
                    null,
                    null,
                    new CursorPageRequest(1, first.NextCursor)),
                mcp));
        Assert.Equal(VaultErrorCode.InvalidCursor, invalid.Code);
    }

    [Fact]
    public async Task ConfirmedRelationDoesNotCreateBacklinkOrChangeOrphanState()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var suggestion = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        _ = await fixture.RelationReview.ApplyAsync(
            suggestion.Metadata.SuggestionId,
            1,
            user,
            Guid.NewGuid());

        var backlinks = await fixture.Documents.ListBacklinksAsync(
            target.DocumentId,
            new CursorPageRequest(20));
        Assert.Empty(backlinks.Items);
        var orphans = await fixture.Documents.ListAsync(
            new CatalogQuery(
                null,
                null,
                new CursorPageRequest(20),
                OrphanOnly: true));
        Assert.Equal(
            new[] { source.DocumentId, target.DocumentId }.Order(),
            orphans.Items.Select(item => item.DocumentId).Order());
    }

    [Fact]
    public async Task InvalidIdentityEvidenceAndSubmitterAreRejectedAtomically()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");

        var noSubmitter = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                Request(source, target) with
                {
                    ModelName = null,
                    AgentName = null,
                    ClientName = null
                },
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, noSubmitter.Code);

        var sameDocument = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                Request(source, source),
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, sameDocument.Code);

        var badEvidence = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.RelationSuggestions.CreateAsync(
                Request(source, target) with
                {
                    Evidence =
                    [
                        Evidence(
                            target.DocumentId,
                            source.Revision + 1,
                            "Wrong revision")
                    ]
                },
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.InvalidInput, badEvidence.Code);
        var list = await fixture.RelationSuggestions.ListAsync(
            new RelationSuggestionQuery(
                RelationSuggestionStatus.PendingReview,
                null,
                null,
                null,
                null,
                null,
                new CursorPageRequest(50)),
            mcp);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task ConcurrentApplyAllowsOneMutationAndReturnsRevisionConflict()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var source = await CreateAsync(fixture, user, "Source", "source.md");
        var target = await CreateAsync(fixture, user, "Target", "target.md");
        var created = await fixture.RelationSuggestions.CreateAsync(
            Request(source, target),
            mcp,
            Guid.NewGuid());
        var gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object> ApplyAsync()
        {
            await gate.Task;
            try
            {
                return await fixture.RelationReview.ApplyAsync(
                    created.Metadata.SuggestionId,
                    1,
                    user,
                    Guid.NewGuid());
            }
            catch (VaultException exception)
            {
                return exception;
            }
        }

        var first = ApplyAsync();
        var second = ApplyAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        var applied = Assert.Single(
            results.OfType<RelationSuggestionApplyOutcome>());
        Assert.Equal(
            RelationSuggestionApplyResult.Inserted,
            applied.ApplyResult);
        var conflict = Assert.Single(results.OfType<VaultException>());
        Assert.Equal(
            VaultErrorCode.RelationSuggestionRevisionConflict,
            conflict.Code);

        var relations =
            await fixture.RelationReview.ListDocumentRelationsAsync(
                source.DocumentId,
                user);
        Assert.Single(relations.Items);
    }

    private static async Task<DocumentWriteResult> CreateAsync(
        VaultApplicationFixture fixture,
        VaultActorContext actor,
        string title,
        string path) =>
        await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                title,
                path,
                null,
                VaultDocumentFormat.Markdown,
                $"---\nstatus: active\n---\n\n# {title}",
                []),
            actor);

    private static CreateRelationSuggestionRequest Request(
        DocumentWriteResult source,
        DocumentWriteResult target) =>
        new(
            RelationType.DependsOn,
            source.DocumentId,
            source.Revision,
            target.DocumentId,
            target.Revision,
            "The source depends on the target.",
            "test-model",
            "test-agent",
            "test-client",
            [
                Evidence(
                    source.DocumentId,
                    source.Revision,
                    "Source evidence")
            ]);

    private static RelationEvidence Evidence(
        Guid documentId,
        int revision,
        string excerpt) =>
        new(documentId, revision, ["Section"], excerpt);
}
