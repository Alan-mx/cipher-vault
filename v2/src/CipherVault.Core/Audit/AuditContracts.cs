using CipherVault.Core.Documents;

namespace CipherVault.Core.Audit;

public sealed record AuditQuery(
    VaultActorKind? Actor,
    string? Action,
    string? TargetObjectType,
    Guid? TargetId,
    DateTimeOffset? OccurredAfter,
    DateTimeOffset? OccurredBefore,
    CursorPageRequest Page);

public sealed record AuditItem(
    Guid EventId,
    DateTimeOffset OccurredAt,
    VaultActorKind Actor,
    string Action,
    string? TargetObjectType,
    Guid? TargetId,
    int? TargetRevision,
    string Result,
    string DetailsJson);

public sealed record AuditPage(
    IReadOnlyList<AuditItem> Items,
    string? NextCursor);
