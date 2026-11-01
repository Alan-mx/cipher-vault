using CipherVault.Application.Backup;
using CipherVault.Application.Documents;
using CipherVault.Application.Import;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;

namespace CipherVault.Application.Tests;

public sealed class DocumentMutationObserverTests
{
    [Fact]
    public async Task MutatingServicesNotifyOnlyAfterSuccessfulWrites()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var observer = new RecordingObserver();
        var documents = new DocumentService(
            fixture.Manager,
            fixture.TimeProvider,
            observer);
        var authoring = new DocumentAuthoringService(
            fixture.Manager,
            fixture.TimeProvider,
            observer);
        var revisions = new DocumentRevisionService(
            fixture.Manager,
            fixture.TimeProvider,
            observer);
        var trash = new TrashService(
            fixture.Manager,
            fixture.TimeProvider,
            observer);
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var created = await documents.CreateAsync(
            new CreateDocumentRequest(
                "Observed",
                null,
                null,
                VaultDocumentFormat.Markdown,
                "# Observed\nold",
                null),
            actor);
        var noOp = await documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Observed",
                null,
                "# Observed\nold",
                []),
            actor);
        Assert.Equal(1, noOp.Revision);
        var updated = await documents.UpdateAsync(
            created.DocumentId,
            1,
            new UpdateDocumentRequest(
                "Observed",
                null,
                "# Observed\nnew",
                []),
            actor);
        var command = new DocumentPatchCommand(
            VaultPatchTargetKind.Heading,
            ["Observed"],
            null,
            VaultPatchOperation.Append,
            "patched",
            null,
            DryRun: true);
        _ = await documents.PatchAsync(
            created.DocumentId,
            updated.Revision,
            command,
            actor);
        var patched = await documents.PatchAsync(
            created.DocumentId,
            updated.Revision,
            command with { DryRun = false },
            actor);
        var appended = await authoring.AppendAsync(
            created.DocumentId,
            patched.Revision,
            "\nappended",
            actor);
        var history = await revisions.ListAsync(
            created.DocumentId,
            new CursorPageRequest());
        var revisionOne = history.Items.Single(item => item.Revision == 1);
        var restoredRevision = await revisions.RestoreAsync(
            created.DocumentId,
            revisionOne.RevisionId,
            appended.Revision,
            actor);
        var deleted = await documents.DeleteAsync(
            created.DocumentId,
            restoredRevision.Revision,
            actor);
        var restored = await trash.RestoreAsync(
            created.DocumentId,
            deleted.Revision,
            replacementPath: null,
            actor);
        var deletedAgain = await documents.DeleteAsync(
            created.DocumentId,
            restored.Revision,
            actor);
        ((ManualTimeProvider)fixture.TimeProvider).Advance(
            TimeSpan.FromDays(31));
        await fixture.RelockAsync();
        _ = await trash.PurgeAsync(
            created.DocumentId,
            deletedAgain.Revision,
            actor);

        Assert.Equal(
            [
                DocumentMutationKind.Create,
                DocumentMutationKind.Update,
                DocumentMutationKind.Patch,
                DocumentMutationKind.Update,
                DocumentMutationKind.RevisionRestore,
                DocumentMutationKind.Delete,
                DocumentMutationKind.Restore,
                DocumentMutationKind.Delete,
                DocumentMutationKind.Purge
            ],
            observer.Kinds);
    }

    [Fact]
    public async Task ImportNotifiesOnceWhenDocumentsWereImported()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var observer = new RecordingObserver();
        var imports = new ImportService(
            fixture.Manager,
            fixture.TimeProvider,
            observer);
        var source = Path.Combine(fixture.Parent, "import-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "Imported.md"),
            "# Imported");
        var actor = new VaultActorContext(VaultActorKind.UserUi);

        var preview = await imports.PreviewAsync(
            new ImportPreviewRequest(source),
            actor);
        _ = await imports.CommitAsync(
            new ImportCommitRequest(preview.Token),
            actor);

        Assert.Equal([DocumentMutationKind.Import], observer.Kinds);
    }

    [Fact]
    public async Task ObserverFailureDoesNotFailCommittedDocumentWrite()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var documents = new DocumentService(
            fixture.Manager,
            fixture.TimeProvider,
            new FailingObserver());

        var created = await documents.CreateAsync(
            new CreateDocumentRequest(
                "Still saved",
                null,
                null,
                VaultDocumentFormat.Text,
                "body",
                null),
            new VaultActorContext(VaultActorKind.UserUi));

        Assert.Equal(1, created.Revision);
    }

    [Fact]
    public async Task VaultLifecycleRaisesStateChangedWithoutAffectingLock()
    {
        await using var fixture = await VaultApplicationFixture.CreateAsync();
        var states = new List<VaultSessionState>();
        fixture.Manager.StateChanged += change =>
            states.Add(change.Status.State);

        await fixture.RelockAsync();

        Assert.Contains(VaultSessionState.Locked, states);
        Assert.Contains(VaultSessionState.Unlocked, states);
    }

    private sealed class RecordingObserver : IDocumentMutationObserver
    {
        public List<DocumentMutationKind> Kinds { get; } = [];

        public ValueTask NotifyAsync(
            DocumentMutationKind kind,
            CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingObserver : IDocumentMutationObserver
    {
        public ValueTask NotifyAsync(
            DocumentMutationKind kind,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(
                new InvalidOperationException("Expected test failure."));
    }
}
