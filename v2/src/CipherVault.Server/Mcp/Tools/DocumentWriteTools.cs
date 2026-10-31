using System.ComponentModel;
using System.Text.Json;
using CipherVault.Application.Documents;
using CipherVault.Core.Documents;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp.Tools;

[McpServerToolType]
public sealed class DocumentWriteTools
{
    private readonly DocumentService documents;
    private readonly DocumentAuthoringService authoring;
    private readonly McpToolInvoker invoker;

    public DocumentWriteTools(
        DocumentService documents,
        DocumentAuthoringService authoring,
        McpToolInvoker invoker)
    {
        this.documents = documents;
        this.authoring = authoring;
        this.invoker = invoker;
    }

    [McpServerTool(Name = McpToolNames.CreateNote)]
    [Description("Create an encrypted Markdown or TXT document. Markdown Frontmatter tags are authoritative.")]
    public Task<CallToolResult> CreateNote(
        string title,
        string content,
        string? folder = null,
        string? virtualPath = null,
        string format = "md",
        string[]? tags = null,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.CreateNote,
            async (context, token) =>
            {
                if (content is null)
                {
                    throw Invalid("content is required.");
                }
                if (folder is not null && virtualPath is not null)
                {
                    throw Invalid("folder and virtualPath cannot both be provided.");
                }
                return await documents.CreateAsync(
                    new CreateDocumentRequest(
                        title,
                        virtualPath,
                        folder,
                        ParseFormat(format)!.Value,
                        content,
                        tags),
                    context.Actor,
                    token);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.AppendNote)]
    [Description("Append content exactly to an existing document using expectedRevision.")]
    public Task<CallToolResult> AppendNote(
        string documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.AppendNote,
            async (context, token) =>
            {
                if (content is null)
                {
                    throw Invalid("content is required.");
                }
                return await authoring.AppendAsync(
                    DocumentReadTools.ParseDocumentId(documentId),
                    expectedRevision,
                    content,
                    context.Actor,
                    token);
            },
            cancellationToken);

    [McpServerTool(Name = McpToolNames.UpdateNote)]
    [Description("Partially update a document using expectedRevision. Omitted fields are preserved; explicit null is invalid.")]
    public Task<CallToolResult> UpdateNote(
        string documentId,
        int expectedRevision,
        JsonElement changes,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.UpdateNote,
            async (context, token) => await authoring.UpdatePartialAsync(
                DocumentReadTools.ParseDocumentId(documentId),
                expectedRevision,
                McpPartialUpdateParser.Parse(changes),
                context.Actor,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.PatchNote)]
    [Description("Patch a Markdown heading or Frontmatter field using expectedRevision. Set dryRun to preview.")]
    public Task<CallToolResult> PatchNote(
        string documentId,
        int expectedRevision,
        string targetKind,
        string operation,
        string[]? headingPath = null,
        string? frontmatterKey = null,
        string? content = null,
        JsonElement? value = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.PatchNote,
            async (context, token) => await documents.PatchAsync(
                DocumentReadTools.ParseDocumentId(documentId),
                expectedRevision,
                new DocumentPatchCommand(
                    ParseTargetKind(targetKind),
                    headingPath,
                    frontmatterKey,
                    ParseOperation(operation),
                    content,
                    value,
                    dryRun),
                context.Actor,
                token),
            cancellationToken);

    [McpServerTool(Name = McpToolNames.DeleteNote)]
    [Description("Move a document to the encrypted trash using expectedRevision. Permanent purge is not available.")]
    public Task<CallToolResult> DeleteNote(
        string documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        invoker.ExecuteAsync(
            McpToolNames.DeleteNote,
            async (context, token) => await documents.DeleteAsync(
                DocumentReadTools.ParseDocumentId(documentId),
                expectedRevision,
                context.Actor,
                token),
            cancellationToken);

    private static VaultDocumentFormat? ParseFormat(string format) =>
        format switch
        {
            "md" => VaultDocumentFormat.Markdown,
            "txt" => VaultDocumentFormat.Text,
            _ => throw Invalid("format must be md or txt.")
        };

    private static VaultPatchTargetKind ParseTargetKind(string value) =>
        value switch
        {
            "heading" => VaultPatchTargetKind.Heading,
            "front_matter" => VaultPatchTargetKind.FrontMatter,
            _ => throw Invalid(
                "targetKind must be heading or front_matter.")
        };

    private static VaultPatchOperation ParseOperation(string value) =>
        value switch
        {
            "append" => VaultPatchOperation.Append,
            "prepend" => VaultPatchOperation.Prepend,
            "replace" => VaultPatchOperation.Replace,
            "delete" => VaultPatchOperation.Delete,
            _ => throw Invalid(
                "operation must be append, prepend, replace or delete.")
        };

    private static McpToolRequestException Invalid(string message) =>
        new("invalid_input", message);
}
