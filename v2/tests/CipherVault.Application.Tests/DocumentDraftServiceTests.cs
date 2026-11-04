using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Drafts;
using CipherVault.Core.Errors;

namespace CipherVault.Application.Tests;

public sealed class DocumentDraftServiceTests
{
    [Fact]
    public async Task CreateDraftCanBeReviewedAndAppliedAsNewDocument()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var payload = Parse(
            """
            {
              "title": "Redis Cache",
              "virtualPath": "notes/redis-cache.md",
              "format": "md",
              "content": "---\ntags:\n  - Redis\n---\n\n# Redis Cache\n",
              "tags": ["Redis"]
            }
            """);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Create,
                null,
                null,
                payload),
            mcp,
            Guid.NewGuid());
        Assert.Equal(DocumentDraftStatus.Draft, created.Status);
        Assert.NotNull(created.FutureDocumentId);

        var detail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal(created.FutureDocumentId, detail.Metadata.FutureDocumentId);
        Assert.False(detail.Payload.TryGetProperty("futureDocumentId", out _));
        Assert.False(detail.Payload.TryGetProperty("payloadVersion", out _));
        Assert.Contains(
            $"id: {created.FutureDocumentId:D}",
            detail.Preview.Content,
            StringComparison.Ordinal);
        Assert.Contains("# Redis Cache", detail.Preview.Content);
        Assert.Contains("# Redis Cache", detail.Preview.UnifiedDiff);

        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());
        Assert.Equal(DocumentDraftStatus.PendingReview, reviewed.Status);

        var applied = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewed.DraftRevision,
            user,
            Guid.NewGuid());
        Assert.Equal(DocumentDraftStatus.Applied, applied.Status);
        Assert.Equal(1, applied.DocumentWriteResult.Revision);
        Assert.Equal(
            created.FutureDocumentId,
            applied.DocumentWriteResult.DocumentId);

        var document = await fixture.Documents.GetAsync(
            applied.DocumentWriteResult.DocumentId);
        var content = await fixture.Documents.ReadContentAsync(
            document.DocumentId,
            0,
            10_000);
        Assert.Contains("# Redis Cache", content.Content);
        Assert.Contains("Redis", document.Tags);
    }

    [Fact]
    public async Task CreateDraftRejectsFrontmatterIdentity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var payload = Parse(
            $$"""
              {
                "title": "Protected",
                "virtualPath": "notes/protected.md",
                "format": "md",
                "content": "---\nid: {{Guid.NewGuid():D}}\n---\nBody",
                "tags": []
              }
              """);

        var exception = await Assert.ThrowsAsync<VaultException>(
            () => fixture.Drafts.CreateAsync(
                new CreateDocumentDraftRequest(
                    DocumentDraftType.Create,
                    null,
                    null,
                    payload),
                mcp,
                Guid.NewGuid()));

        Assert.Equal(VaultErrorCode.DocumentIdProtected, exception.Code);
    }

    [Fact]
    public async Task UpdateCreateDraftPreservesFutureIdentity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Create,
                null,
                null,
                Parse(
                    """
                    {
                      "title": "Draft identity",
                      "virtualPath": "notes/draft-identity.md",
                      "format": "md",
                      "content": "# Draft identity\n",
                      "tags": []
                    }
                    """)),
            mcp,
            Guid.NewGuid());

        var updated = await fixture.Drafts.UpdateAsync(
            created.DraftId,
            created.DraftRevision,
            Parse(
                """
                {
                  "title": "Draft identity",
                  "virtualPath": "notes/draft-identity.md",
                  "format": "md",
                  "content": "# Updated identity\n",
                  "tags": []
                }
                """),
            mcp,
            Guid.NewGuid());

        Assert.Equal(created.FutureDocumentId, updated.FutureDocumentId);
        var detail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Contains(
            $"id: {created.FutureDocumentId:D}",
            detail.Preview.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"id: {created.FutureDocumentId:D}",
            detail.Payload.GetProperty("content").GetString(),
            StringComparison.Ordinal);

        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            updated.DraftRevision,
            mcp,
            Guid.NewGuid());
        var applied = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewed.DraftRevision,
            user,
            Guid.NewGuid());

        Assert.Equal(created.FutureDocumentId, applied.DocumentWriteResult.DocumentId);
    }

    [Fact]
    public async Task UpdateDraftPreviewAndApplyIncludeCurrentIdentity()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Identity update",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "Original",
                null),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Update,
                document.DocumentId,
                1,
                Parse(
                    """
                    {
                      "operations": [
                        { "type": "replace_body", "content": "Replacement" }
                      ]
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var detail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Contains(
            $"id: {document.DocumentId:D}",
            detail.Preview.Content,
            StringComparison.Ordinal);

        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());
        _ = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewed.DraftRevision,
            user,
            Guid.NewGuid());

        var content = await fixture.Documents.ReadContentAsync(
            document.DocumentId,
            0,
            500);
        Assert.Contains(
            $"id: {document.DocumentId:D}",
            content.Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDraftAppliesTypedOperationsAtomically()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Cache",
                "notes/cache.md",
                null,
                VaultDocumentFormat.Markdown,
                "---\ntags:\n  - Redis\nstatus: raw\n---\n\n# Cache\n",
                ["Redis"]),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Update,
                document.DocumentId,
                1,
                Parse(
                    """
                    {
                      "operations": [
                        { "type": "append_body", "content": "\nBody\n" },
                        { "type": "set_property", "key": "status", "value": "verified" },
                        { "type": "add_tag", "tag": "Cache" }
                      ]
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());

        var applied = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewed.DraftRevision,
            user,
            Guid.NewGuid());

        Assert.Equal(2, applied.DocumentWriteResult.Revision);
        var content = await fixture.Documents.ReadContentAsync(
            document.DocumentId,
            0,
            10_000);
        var metadata = await fixture.Documents.GetAsync(document.DocumentId);
        Assert.Contains("status: verified", content.Content);
        Assert.Contains("Body", content.Content);
        Assert.Contains("Cache", metadata.Tags);
        Assert.Contains("Redis", metadata.Tags);
    }

    [Fact]
    public async Task DocumentRevisionConflictMarksDraftConflictAndReplaysConflict()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Conflict",
                "notes/conflict.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Conflict\n",
                []),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Update,
                document.DocumentId,
                1,
                Parse(
                    """
                    {
                      "operations": [
                        { "type": "append_body", "content": "\nDraft\n" }
                      ]
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());
        _ = await fixture.Documents.UpdateAsync(
            document.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Conflict changed",
                "notes/conflict.md",
                "# Conflict changed\n",
                []),
            user);

        var applyKey = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.ApplyAsync(
                created.DraftId,
                reviewed.DraftRevision,
                user,
                applyKey));
        Assert.Equal(VaultErrorCode.RevisionConflict, exception.Code);

        var conflict = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal(DocumentDraftStatus.Conflict, conflict.Metadata.Status);

        var replay = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.ApplyAsync(
                created.DraftId,
                reviewed.DraftRevision,
                user,
                applyKey));
        Assert.Equal(VaultErrorCode.RevisionConflict, replay.Code);
    }

    [Fact]
    public async Task DraftCreateIsIdempotentAndRejectsKeyReuse()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var payload = Parse(
            """
            {
              "title": "Idempotent",
              "virtualPath": "notes/idempotent.md",
              "format": "md",
              "content": "# Idempotent\n",
              "tags": []
            }
            """);
        var key = Guid.NewGuid();
        var request = new CreateDocumentDraftRequest(
            DocumentDraftType.Create,
            null,
            null,
            payload);

        var first = await fixture.Drafts.CreateAsync(request, mcp, key);
        var replay = await fixture.Drafts.CreateAsync(request, mcp, key);

        Assert.Equal(first, replay);
        var changed = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.CreateAsync(
                new CreateDocumentDraftRequest(
                    DocumentDraftType.Create,
                    null,
                    null,
                    Parse(
                        """
                        {
                          "title": "Different",
                          "virtualPath": "notes/different.md",
                          "format": "md",
                          "content": "# Different\n",
                          "tags": []
                        }
                        """)),
                mcp,
                key));
        Assert.Equal(VaultErrorCode.IdempotencyKeyReused, changed.Code);
    }

    [Fact]
    public async Task McpFormalWritesRequireDirectMode()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var request = new CreateDocumentRequest(
            "Mode",
            "notes/mode.md",
            null,
            VaultDocumentFormat.Markdown,
            "# Mode\n",
            []);

        var blocked = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.CreateAsync(request, mcp));
        Assert.Equal(VaultErrorCode.WriteModeDraftRequired, blocked.Code);

        _ = await fixture.EnableDirectWriteAsync();
        var direct = await fixture.Documents.CreateAsync(request, mcp);
        Assert.Equal(1, direct.Revision);

        _ = await fixture.WriteMode.ChangeAsync(
            new McpWriteModeChangeRequest(McpWriteMode.Draft),
            user,
            Guid.NewGuid());
        var blockedAgain = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.UpdateAsync(
                direct.DocumentId,
                1,
                new UpdateDocumentRequest(
                    "Mode",
                    "notes/mode.md",
                    "# Mode changed\n",
                    []),
                mcp));
        Assert.Equal(VaultErrorCode.WriteModeDraftRequired, blockedAgain.Code);
    }

    [Fact]
    public async Task WriteModeResetsAfterRelock()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        _ = await fixture.EnableDirectWriteAsync();
        Assert.Equal(McpWriteMode.Direct, fixture.WriteMode.Get().Mode);

        await fixture.RelockAsync();

        var mode = fixture.WriteMode.Get();
        Assert.Equal(McpWriteMode.Draft, mode.Mode);
        Assert.True(mode.Effective);
    }

    [Fact]
    public async Task ClosingDirectModeWaitsForInFlightMcpWriteAndBlocksNextWrite()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var user = new VaultActorContext(VaultActorKind.UserUi);
        _ = await fixture.EnableDirectWriteAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = fixture.Manager.ExecuteDocumentWriteAsync(
            mcp,
            async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return 1;
            }).AsTask();
        await entered.Task;

        var close = fixture.WriteMode.ChangeAsync(
            new McpWriteModeChangeRequest(McpWriteMode.Draft),
            user,
            Guid.NewGuid());
        await Task.Delay(50);
        Assert.False(close.IsCompleted);

        release.TrySetResult();
        Assert.Equal(1, await inFlight);
        _ = await close;

        var blocked = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Manager.ExecuteDocumentWriteAsync(
                mcp,
                (_, _) => ValueTask.FromResult(1)).AsTask());
        Assert.Equal(VaultErrorCode.WriteModeDraftRequired, blocked.Code);
    }

    [Fact]
    public async Task McpCannotApplyOrRejectDraft()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Create,
                null,
                null,
                Parse(
                    """
                    {
                      "title": "Mcp Draft",
                      "virtualPath": "notes/mcp-draft.md",
                      "format": "md",
                      "content": "# Mcp Draft\n",
                      "tags": []
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());

        var apply = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.ApplyAsync(
                created.DraftId,
                reviewed.DraftRevision,
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.Forbidden, apply.Code);

        var reject = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.RejectAsync(
                created.DraftId,
                reviewed.DraftRevision,
                mcp,
                Guid.NewGuid()));
        Assert.Equal(VaultErrorCode.Forbidden, reject.Code);
    }

    [Fact]
    public async Task CleanupDeletesTerminalDraftsAndExpiredIdempotencyAfterThirtyDays()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Create,
                null,
                null,
                Parse(
                    """
                    {
                      "title": "Cleanup",
                      "virtualPath": "notes/cleanup.md",
                      "format": "md",
                      "content": "# Cleanup\n",
                      "tags": []
                    }
                    """)),
            user,
            Guid.NewGuid());
        _ = await fixture.Drafts.DiscardAsync(
            created.DraftId,
            created.DraftRevision,
            user,
            Guid.NewGuid());
        ((ManualTimeProvider)fixture.TimeProvider).MoveWallClock(
            TimeSpan.FromDays(31));

        var cleanup = await fixture.Drafts.CleanupAsync();

        Assert.Equal(1, cleanup.DeletedDrafts);
        Assert.True(cleanup.DeletedIdempotencyRecords >= 2);
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.GetAsync(created.DraftId));
        Assert.Equal(VaultErrorCode.DraftNotFound, exception.Code);
    }

    [Fact]
    public async Task ActiveDraftLimitRejectsAdditionalCreates()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        await fixture.QueryAsync(
            async context =>
            {
                var now = fixture.TimeProvider.GetUtcNow().ToString("O");
                for (var index = 0; index < 200; index++)
                {
                    using var command = context.CreateCommand();
                    command.Command.CommandText =
                        """
                        INSERT INTO document_drafts(
                            draft_id, draft_revision, draft_type, status,
                            target_document_id, base_document_revision,
                            payload_version, payload_json, base_snapshot_json,
                            source_actor, created_at, updated_at, terminal_at,
                            applied_document_id, applied_document_revision,
                            applied_at)
                        VALUES(
                            $draftId, 1, 'create', 'draft',
                            NULL, NULL, 1, '{}', NULL,
                            'user_ui', $now, $now, NULL,
                            NULL, NULL, NULL);
                        """;
                    command.Command.Parameters.AddWithValue(
                        "$draftId",
                        Guid.NewGuid().ToString("D"));
                    command.Command.Parameters.AddWithValue("$now", now);
                    await command.Command.ExecuteNonQueryAsync();
                }
                return 1;
            });

        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.CreateAsync(
                new CreateDocumentDraftRequest(
                    DocumentDraftType.Create,
                    null,
                    null,
                    Parse(
                        """
                        {
                          "title": "Over limit",
                          "virtualPath": "notes/over-limit.md",
                          "format": "md",
                          "content": "# Over limit\n",
                          "tags": []
                        }
                        """)),
                user,
                Guid.NewGuid()));

        Assert.Equal(VaultErrorCode.DraftLimitExceeded, exception.Code);
    }

    [Fact]
    public async Task ConflictCanBeRebasedAndApplied()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Rebase",
                "notes/rebase.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Rebase\n",
                []),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Update,
                document.DocumentId,
                1,
                Parse(
                    """
                    {
                      "operations": [
                        { "type": "append_body", "content": "\nProposed\n" }
                      ]
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());
        _ = await fixture.Documents.UpdateAsync(
            document.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Rebase",
                "notes/rebase.md",
                "# Rebase\nChanged externally\n",
                []),
            user);
        _ = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Drafts.ApplyAsync(
                created.DraftId,
                reviewed.DraftRevision,
                user,
                Guid.NewGuid()));
        var conflict = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal(DocumentDraftStatus.Conflict, conflict.Metadata.Status);

        var rebased = await fixture.Drafts.RebaseAsync(
            created.DraftId,
            conflict.Metadata.DraftRevision,
            2,
            mcp,
            Guid.NewGuid());
        Assert.Equal(DocumentDraftStatus.Draft, rebased.Status);
        var reviewedAgain = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            rebased.DraftRevision,
            mcp,
            Guid.NewGuid());
        var applied = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewedAgain.DraftRevision,
            user,
            Guid.NewGuid());

        Assert.Equal(3, applied.DocumentWriteResult.Revision);
        var content = await fixture.Documents.ReadContentAsync(
            document.DocumentId,
            0,
            10_000);
        Assert.Contains("Changed externally", content.Content);
        Assert.Contains("Proposed", content.Content);
    }

    [Fact]
    public async Task TrashDraftMovesFormalDocumentToTrash()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Trash draft",
                "notes/trash-draft.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Trash draft\n",
                []),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Trash,
                document.DocumentId,
                1,
                Parse("{}")),
            mcp,
            Guid.NewGuid());
        var reviewed = await fixture.Drafts.SubmitReviewAsync(
            created.DraftId,
            created.DraftRevision,
            mcp,
            Guid.NewGuid());

        var applied = await fixture.Drafts.ApplyAsync(
            created.DraftId,
            reviewed.DraftRevision,
            user,
            Guid.NewGuid());

        Assert.Equal(2, applied.DocumentWriteResult.Revision);
        var exception = await Assert.ThrowsAsync<VaultException>(() =>
            fixture.Documents.GetAsync(document.DocumentId));
        Assert.Equal(VaultErrorCode.DocumentNotFound, exception.Code);
        var trash = await fixture.Trash.ListAsync(new CursorPageRequest());
        Assert.Contains(
            trash.Items,
            item => item.DocumentId == document.DocumentId);
    }

    [Fact]
    public async Task ReviewContextPersistsAcrossDraftUpdatesAndAuditStaysRedacted()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var user = new VaultActorContext(VaultActorKind.UserUi);
        var mcp = new VaultActorContext(VaultActorKind.Mcp);
        var document = await fixture.Documents.CreateAsync(
            new CreateDocumentRequest(
                "Context target",
                "notes/context-target.md",
                null,
                VaultDocumentFormat.Markdown,
                "# Context target\n",
                []),
            user);
        var created = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Create,
                null,
                null,
                Parse(
                    """
                    {
                      "title": "Context draft",
                      "virtualPath": "notes/context-draft.md",
                      "format": "md",
                      "content": "# Context draft\n",
                      "tags": [],
                      "reviewContext": {
                        "summary": "Secret summary",
                        "sources": [
                          {
                            "title": "Source",
                            "excerpt": "Secret excerpt"
                          }
                        ]
                      }
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var createdDetail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal(
            "Secret summary",
            createdDetail.ReviewContext?.Summary);

        var preserved = await fixture.Drafts.UpdateAsync(
            created.DraftId,
            created.DraftRevision,
            Parse(
                """
                {
                  "title": "Context draft",
                  "virtualPath": "notes/context-draft.md",
                  "format": "md",
                  "content": "# Context draft updated\n",
                  "tags": []
                }
                """),
            mcp,
            Guid.NewGuid());
        var preservedDetail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal(
            "Secret summary",
            preservedDetail.ReviewContext?.Summary);

        var replaced = await fixture.Drafts.UpdateAsync(
            created.DraftId,
            preserved.DraftRevision,
            Parse(
                """
                {
                  "title": "Context draft",
                  "virtualPath": "notes/context-draft.md",
                  "format": "md",
                  "content": "# Context draft updated\n",
                  "tags": [],
                  "reviewContext": {
                    "summary": "Replacement",
                    "sources": []
                  }
                }
                """),
            mcp,
            Guid.NewGuid());
        var replacedDetail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Equal("Replacement", replacedDetail.ReviewContext?.Summary);

        _ = await fixture.Drafts.UpdateAsync(
            created.DraftId,
            replaced.DraftRevision,
            Parse(
                """
                {
                  "title": "Context draft",
                  "virtualPath": "notes/context-draft.md",
                  "format": "md",
                  "content": "# Context draft updated\n",
                  "tags": [],
                  "reviewContext": null
                }
                """),
            mcp,
            Guid.NewGuid());
        var clearedDetail = await fixture.Drafts.GetAsync(created.DraftId);
        Assert.Null(clearedDetail.ReviewContext);

        var trash = await fixture.Drafts.CreateAsync(
            new CreateDocumentDraftRequest(
                DocumentDraftType.Trash,
                document.DocumentId,
                document.Revision,
                Parse(
                    """
                    {
                      "reviewContext": {
                        "summary": "Trash context",
                        "sources": []
                      }
                    }
                    """)),
            mcp,
            Guid.NewGuid());
        var trashDetail = await fixture.Drafts.GetAsync(trash.DraftId);
        Assert.Equal("Trash context", trashDetail.ReviewContext?.Summary);
        Assert.Equal(
            ["reviewContext"],
            trashDetail.Payload.EnumerateObject()
                .Select(property => property.Name)
                .ToArray());

        var totalAudit = 0;
        var withContext = 0;
        var withSecret = 0;
        _ = await fixture.QueryAsync(
            async context =>
            {
                using var command = context.CreateCommand();
                command.Command.CommandText =
                    """
                    SELECT
                        count(*),
                        sum(
                            CASE WHEN json_extract(
                                details_json,
                                '$.hasReviewContext'
                            ) = 1 THEN 1 ELSE 0 END
                        ),
                        sum(
                            CASE WHEN details_json LIKE '%Secret%'
                                THEN 1 ELSE 0 END
                        )
                    FROM audit_events
                    WHERE action LIKE 'draft.%';
                    """;
                await using var reader =
                    await command.Command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                totalAudit = reader.GetInt32(0);
                withContext = reader.GetInt32(1);
                withSecret = reader.GetInt32(2);
                return 1;
            });
        Assert.True(totalAudit >= 5);
        Assert.True(withContext >= 3);
        Assert.Equal(0, withSecret);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
