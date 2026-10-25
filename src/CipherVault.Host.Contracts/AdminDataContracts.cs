using System.Text.Json;

namespace CipherVault.Host.Contracts;

public sealed record AdminCatalogListRequest(
    string? Folder,
    string? Cursor,
    int Limit);

public sealed record AdminSearchNotesRequest(
    string Query,
    string? Folder,
    string? Cursor,
    int Limit);

public sealed record AdminReadNoteRequest(
    Guid DocumentId,
    int Offset,
    int MaxChars);

public sealed record AdminScanNotesRequest(
    string? Cursor,
    int Limit);

public sealed record AdminDocumentMapRequest(
    Guid DocumentId);

public sealed record AdminBacklinksRequest(
    Guid DocumentId,
    string? Cursor,
    int Limit);

public sealed record AdminUnlinkedMentionsRequest(
    Guid DocumentId,
    string? Cursor,
    int Limit);

public sealed record AdminTagListRequest(
    string? Cursor,
    int Limit);

public sealed record AdminLocalGraphRequest(
    Guid DocumentId,
    int Depth,
    IReadOnlyList<string>? Tags,
    string? Folder,
    int MaxNodes,
    int MaxEdges);

public sealed record AdminAppendNoteRequest(
    Guid DocumentId,
    HostAppendNoteRequest Request);

public sealed record AdminUpdateNoteRequest(
    Guid DocumentId,
    HostUpdateNoteRequest Request);

public sealed record HostPatchNoteRequest(
    int ExpectedRevision,
    string TargetKind,
    IReadOnlyList<string>? HeadingPath,
    string? FrontMatterKey,
    string Operation,
    string? ContentMarkdown,
    JsonElement? Value,
    bool DryRun);

public sealed record AdminPatchNoteRequest(
    Guid DocumentId,
    HostPatchNoteRequest Request);

public sealed record AdminTrashNoteRequest(
    Guid DocumentId,
    HostTrashNoteRequest Request);

public sealed record AdminTrashListRequest(
    string? Cursor,
    int Limit);

public sealed record AdminRestoreTrashRequest(
    Guid DocumentId,
    HostRestoreTrashRequest Request);

public sealed record AdminRevisionListRequest(
    Guid DocumentId,
    string? Cursor,
    int Limit);

public sealed record AdminRestoreRevisionRequest(
    Guid DocumentId,
    Guid RevisionId,
    HostRestoreRevisionRequest Request);

public sealed record AdminAuditListRequest(
    Guid? DocumentId,
    string? Action,
    Guid? CorrelationId,
    string? Cursor,
    int Limit);

public sealed record AdminPurgeNoteRequest(
    Guid DocumentId);

public sealed record AdminImportCommitRequest(
    Guid JobId);
