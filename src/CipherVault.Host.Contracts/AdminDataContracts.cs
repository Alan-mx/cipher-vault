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

public sealed record AdminAppendNoteRequest(
    Guid DocumentId,
    HostAppendNoteRequest Request);

public sealed record AdminUpdateNoteRequest(
    Guid DocumentId,
    HostUpdateNoteRequest Request);

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
