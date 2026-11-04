using System.Text.Json;
using CipherVault.Application.Documents;
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
    string Title,
    OptionalField<string> VirtualPath,
    string Content,
    OptionalField<IReadOnlyList<string>> Tags)
{
    public static UpdateDocumentApiRequest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Request body must be a JSON object.");
        }
        if (root.TryGetProperty("updateBacklinks", out _))
        {
            throw Invalid("updateBacklinks is no longer supported.");
        }

        return new UpdateDocumentApiRequest(
            RequireString(root, "title"),
            ReadOptionalString(root, "virtualPath"),
            RequireString(root, "content"),
            ReadOptionalTags(root));
    }

    public DocumentPartialUpdate ToApplicationUpdate() =>
        new(
            OptionalField<string>.Of(Title),
            VirtualPath,
            OptionalField<string>.Of(Content),
            Tags);
    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string.");
        }
        return value.GetString()!;
    }

    private static OptionalField<string> ReadOptionalString(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return OptionalField<string>.Missing;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{name} must be a string or null.");
        }
        return OptionalField<string>.Of(value.GetString()!);
    }

    private static OptionalField<IReadOnlyList<string>> ReadOptionalTags(
        JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags))
        {
            return OptionalField<IReadOnlyList<string>>.Missing;
        }
        if (tags.ValueKind == JsonValueKind.Null)
        {
            throw Invalid("tags cannot be null.");
        }
        if (tags.ValueKind != JsonValueKind.Array
            || tags.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String))
        {
            throw Invalid("tags must be an array of strings when provided.");
        }
        return OptionalField<IReadOnlyList<string>>.Of(
            tags.EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray());
    }

    private static ApiRequestException Invalid(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
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
