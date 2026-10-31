using System.Text.Json;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api.Contracts;

public sealed record CreateDocumentApiRequest(
    string? Title,
    string? VirtualPath,
    string? Folder,
    VaultDocumentFormat? Format,
    string? Content,
    IReadOnlyList<string>? Tags)
{
    public CreateDocumentRequest ToApplicationRequest()
    {
        if (Format is null)
        {
            throw Missing("format");
        }
        if (Content is null)
        {
            throw Missing("content");
        }
        if (Title is null)
        {
            throw Missing("title");
        }

        return new CreateDocumentRequest(
            Title,
            VirtualPath,
            Folder,
            Format.Value,
            Content,
            Tags);
    }

    private static ApiRequestException Missing(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is required.");
}

public sealed record UpdateDocumentApiRequest(
    string? Title,
    string? VirtualPath,
    string? Content,
    IReadOnlyList<string>? Tags,
    bool UpdateBacklinks = false)
{
    public UpdateDocumentRequest ToApplicationRequest()
    {
        if (Title is null)
        {
            throw Missing("title");
        }
        if (Content is null)
        {
            throw Missing("content");
        }
        if (Tags is null)
        {
            throw Missing("tags");
        }

        return new UpdateDocumentRequest(
            Title,
            VirtualPath,
            Content,
            Tags,
            UpdateBacklinks);
    }

    private static ApiRequestException Missing(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is required.");
}

public sealed record PatchDocumentApiRequest(
    VaultPatchTargetKind? TargetKind,
    IReadOnlyList<string>? HeadingPath,
    string? FrontMatterKey,
    VaultPatchOperation? Operation,
    string? ContentMarkdown,
    JsonElement? Value,
    bool DryRun)
{
    public DocumentPatchCommand ToApplicationCommand()
    {
        if (TargetKind is null)
        {
            throw Missing("targetKind");
        }
        if (Operation is null)
        {
            throw Missing("operation");
        }

        return new DocumentPatchCommand(
            TargetKind.Value,
            HeadingPath,
            FrontMatterKey,
            Operation.Value,
            ContentMarkdown,
            Value,
            DryRun);
    }

    private static ApiRequestException Missing(string field) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            $"{field} is required.");
}
