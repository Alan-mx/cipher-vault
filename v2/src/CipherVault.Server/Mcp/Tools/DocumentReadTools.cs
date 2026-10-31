using System.ComponentModel;
using CipherVault.Application.Documents;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Server.Api;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class DocumentReadTools
{
    private readonly DocumentService documents;
    private readonly McpToolInvoker invoker;

    public DocumentReadTools(
        DocumentService documents,
        McpToolInvoker invoker)
    {
        this.documents = documents;
        this.invoker = invoker;
    }

    [McpServerTool(Name = McpToolNames.ListNotes)]
    [Description("List active document metadata without content. Pagination is opaque and concurrent changes may be skipped.")]
    public Task<CallToolResult> ListNotes(
        string? folder = null,
        string? format = null,
        string? cursor = null,
        int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ListNotes,
            async (_, token) =>
                await documents.ListAsync(
                    new CatalogQuery(
                        folder,
                        ParseFormat(format),
                        new CursorPageRequest(pageSize, cursor)),
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ScanNotes)]
    [Description("Scan all active document summaries ordered by virtual path. Pagination is opaque and concurrent changes may be skipped.")]
    public Task<CallToolResult> ScanNotes(
        string? cursor = null,
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.ScanNotes,
            async (_, token) =>
                await documents.ListAsync(
                    new CatalogQuery(
                        null,
                        null,
                        new CursorPageRequest(pageSize, cursor)),
                    token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.ReadNote)]
    [Description("Read a bounded UTF-16 character range from one active document.")]
    public Task<CallToolResult> ReadNote(
        string documentId,
        int offset = 0,
        int maxChars = 8000,
        CancellationToken cancellationToken = default)
    {
        return invoker.ExecuteAsync(
            McpToolNames.ReadNote,
            async (_, token) =>
            {
                var id = ParseDocumentId(documentId);
                if (offset < 0)
                {
                    throw OffsetInvalid();
                }
                if (maxChars is < 2 or > 65_536)
                {
                    throw InputInvalid(
                        "maxChars must be between 2 and 65536.");
                }

                try
                {
                    return await documents.ReadContentAsync(
                        id,
                        offset,
                        maxChars,
                        token);
                }
                catch (VaultException exception)
                    when (exception.Code == VaultErrorCode.InvalidInput)
                {
                    throw OffsetInvalid();
                }
            },
            cancellationToken);
    }

    internal static VaultDocumentFormat? ParseFormat(string? format) =>
        format switch
        {
            null => null,
            "md" => VaultDocumentFormat.Markdown,
            "txt" => VaultDocumentFormat.Text,
            _ => throw InputInvalid("format must be md or txt.")
        };

    internal static Guid ParseDocumentId(string value) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw InputInvalid("documentId must be a non-empty GUID.");

    private static McpToolRequestException OffsetInvalid() =>
        new(
            "invalid_offset",
            "Document content offset is invalid.");

    private static McpToolRequestException InputInvalid(string message) =>
        new("invalid_input", message);
}
