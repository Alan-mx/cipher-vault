namespace CipherVault.Core.Documents;

public enum VaultActorKind
{
    UserUi,
    UserCli,
    Mcp,
    System
}

public sealed record VaultAuditContext(
    VaultActorKind Actor,
    string? ClientName = null,
    string? RequestId = null,
    Guid? ClientInstanceId = null,
    Guid? GrantId = null,
    string? GrantScopes = null);

public sealed record VaultWriteResult(
    Guid DocumentId,
    int Revision,
    DateTimeOffset CommittedAt,
    Guid? AuditEventId = null);

public sealed record VaultNotePatch(
    string? Title = null,
    string? Folder = null,
    string? Content = null,
    IReadOnlyList<string>? Tags = null,
    bool UpdateBacklinks = false);

public sealed record VaultDeleteResult(
    Guid DocumentId,
    int Revision,
    DateTimeOffset PurgeAfter,
    Guid? AuditEventId = null);

public sealed record VaultRestoreResult(
    Guid DocumentId,
    int Revision,
    string VirtualPath);

public sealed record VaultPurgeResult(
    Guid DocumentId,
    bool Purged);

public sealed record VaultTrashItem(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgeAfter);

public sealed record VaultTrashPage(
    IReadOnlyList<VaultTrashItem> Items,
    string? NextCursor);

public sealed record VaultRevisionItem(
    Guid RevisionId,
    Guid DocumentId,
    int Revision,
    string Title,
    string VirtualPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset RetainUntil);

public sealed record VaultRevisionPage(
    IReadOnlyList<VaultRevisionItem> Items,
    string? NextCursor);
