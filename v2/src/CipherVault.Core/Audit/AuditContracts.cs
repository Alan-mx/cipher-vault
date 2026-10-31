using CipherVault.Core.Documents;

namespace CipherVault.Core.Audit;

public sealed record AuditQuery(
    VaultActorKind? Actor,
    string? Action,
    Guid? DocumentId,
    DateTimeOffset? OccurredAfter,
    DateTimeOffset? OccurredBefore,
    CursorPageRequest Page);

public sealed record AuditItem(
    Guid EventId,
    DateTimeOffset OccurredAt,
    VaultActorKind Actor,
    string Action,
    Guid? DocumentId,
    int? Revision,
    string Result,
    string DetailsJson);

public sealed record AuditPage(
    IReadOnlyList<AuditItem> Items,
    string? NextCursor);
