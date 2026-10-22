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
