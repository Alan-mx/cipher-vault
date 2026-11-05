using CipherVault.Core.Documents;

namespace CipherVault.Core.Audit;

public sealed record AuditQuery(
    VaultActorKind? Actor,
    string? Action,
    string? TargetObjectType,
    Guid? TargetId,
    DateTimeOffset? OccurredAfter,
    DateTimeOffset? OccurredBefore,
    CursorPageRequest Page)
{
    public AuditQuery(
        VaultActorKind? actor,
        string? action,
        Guid? documentId,
        DateTimeOffset? occurredAfter,
        DateTimeOffset? occurredBefore,
        CursorPageRequest page)
        : this(
            actor,
            action,
            documentId is null ? null : "knowledge",
            documentId,
            occurredAfter,
            occurredBefore,
            page)
    {
    }
}

public sealed record AuditItem(
    Guid EventId,
    DateTimeOffset OccurredAt,
    VaultActorKind Actor,
    string Action,
    string? TargetObjectType,
    Guid? TargetId,
    int? TargetRevision,
    string Result,
    string DetailsJson)
{
    public Guid? DocumentId => TargetId;

    public int? Revision => TargetRevision;
}

public sealed record AuditPage(
    IReadOnlyList<AuditItem> Items,
    string? NextCursor);
