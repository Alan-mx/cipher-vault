namespace CipherVault.Core.Documents;

public sealed record CursorPageRequest(
    int? PageSize = null,
    string? Cursor = null);
