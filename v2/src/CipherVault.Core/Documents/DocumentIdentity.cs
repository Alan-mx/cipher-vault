using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal enum DocumentIdentityMode
{
    NewDocument,
    ExistingDocument,
    TrustedImport,
    ExportCopy,
    CopyAsNew
}

internal sealed record DocumentFrontMatterIdentity(
    bool HasFrontMatter,
    bool HasId,
    Guid? DocumentId,
    string? RawId,
    int? FieldStartOffset,
    int? FieldEndOffset);

internal static class DocumentIdentity
{
    internal const string FrontMatterKey = "id";

    internal static bool TryParse(string? value, out Guid documentId)
    {
        documentId = Guid.Empty;
        return value is not null
            && Guid.TryParseExact(value, "D", out documentId)
            && documentId != Guid.Empty;
    }

    internal static string Format(Guid documentId) =>
        documentId == Guid.Empty
            ? throw InvalidDocumentId("Document ID cannot be empty.")
            : documentId.ToString("D");

    internal static DocumentFrontMatterIdentity InspectMarkdown(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return new DocumentFrontMatterIdentity(
                HasFrontMatter: false,
                HasId: false,
                DocumentId: null,
                RawId: null,
                FieldStartOffset: null,
                FieldEndOffset: null);
        }

        var idFields = snapshot.Fields
            .Where(field => string.Equals(
                field.Key,
                FrontMatterKey,
                StringComparison.Ordinal))
            .ToArray();
        if (idFields.Length == 0)
        {
            return new DocumentFrontMatterIdentity(
                HasFrontMatter: true,
                HasId: false,
                DocumentId: null,
                RawId: null,
                FieldStartOffset: null,
                FieldEndOffset: null);
        }
        if (idFields.Length != 1)
        {
            throw InvalidDocumentId("Frontmatter contains duplicate id fields.");
        }

        var field = idFields[0];
        if (field.Value.ValueKind != System.Text.Json.JsonValueKind.String
            || !TryParse(field.Value.GetString(), out var documentId))
        {
            throw InvalidDocumentId("Frontmatter id must be a non-empty D-format UUID.");
        }
        return new DocumentFrontMatterIdentity(
            HasFrontMatter: true,
            HasId: true,
            DocumentId: documentId,
            RawId: field.Value.GetString(),
            FieldStartOffset: field.StartOffset,
            FieldEndOffset: field.EndOffset);
    }

    internal static string EnsureMarkdownIdentity(
        string content,
        Guid documentId,
        DocumentIdentityMode mode)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = Format(documentId);
        var inspection = InspectMarkdown(content);
        if (!inspection.HasId)
        {
            return InjectMarkdownIdentity(content, documentId);
        }
        if (mode == DocumentIdentityMode.NewDocument)
        {
            throw DocumentIdProtected(
                "A new document cannot specify Frontmatter id.");
        }
        if (mode == DocumentIdentityMode.CopyAsNew)
        {
            return ReplaceMarkdownIdentity(
                content,
                inspection,
                documentId);
        }
        if (inspection.DocumentId != documentId)
        {
            throw DocumentIdMismatch(documentId, inspection.DocumentId);
        }
        return content;
    }

    internal static string RemoveMarkdownIdentity(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var inspection = InspectMarkdown(content);
        if (!inspection.HasId)
        {
            return content;
        }
        if (inspection.FieldStartOffset is not { } start
            || inspection.FieldEndOffset is not { } end)
        {
            throw InvalidDocumentId("Frontmatter id field offsets are invalid.");
        }
        if (end < content.Length && content[end] == '\r')
        {
            end += content.Length > end + 1 && content[end + 1] == '\n' ? 2 : 1;
        }
        else if (end < content.Length && content[end] == '\n')
        {
            end++;
        }
        return string.Concat(content.AsSpan(0, start), content.AsSpan(end));
    }

    private static string InjectMarkdownIdentity(
        string content,
        Guid documentId)
    {
        var idLine = $"{FrontMatterKey}: {Format(documentId)}";
        if (FrontMatterDocument.TryParse(content, out var snapshot))
        {
            var newline = LineEndingAt(content, snapshot.BodyStart);
            return string.Concat(
                content.AsSpan(0, snapshot.BodyStart),
                idLine,
                newline,
                content.AsSpan(snapshot.BodyStart));
        }

        var documentNewline = FirstLineEnding(content);
        return $"---{documentNewline}{idLine}{documentNewline}---{documentNewline}{content}";
    }

    private static string ReplaceMarkdownIdentity(
        string content,
        DocumentFrontMatterIdentity inspection,
        Guid documentId)
    {
        if (inspection.FieldStartOffset is not { } start
            || inspection.FieldEndOffset is not { } end)
        {
            throw InvalidDocumentId("Frontmatter id field offsets are invalid.");
        }
        var idLine = $"{FrontMatterKey}: {Format(documentId)}";
        return string.Concat(
            content.AsSpan(0, start),
            idLine,
            content.AsSpan(end));
    }

    private static string FirstLineEnding(string content)
    {
        var newline = content.IndexOf('\n');
        return newline > 0 && content[newline - 1] == '\r'
            ? "\r\n"
            : "\n";
    }

    private static string LineEndingAt(string content, int offset)
    {
        if (offset >= 2
            && content[offset - 2] == '\r'
            && content[offset - 1] == '\n')
        {
            return "\r\n";
        }
        return "\n";
    }

    private static VaultException InvalidDocumentId(string message) =>
        new(VaultErrorCode.InvalidDocumentId, message);

    private static VaultException DocumentIdProtected(string message) =>
        new(VaultErrorCode.DocumentIdProtected, message);

    private static VaultException DocumentIdMismatch(
        Guid expected,
        Guid? actual) =>
        new(
            VaultErrorCode.DocumentIdMismatch,
            "Frontmatter id does not match the document identity.",
            details: new Dictionary<string, object?>
            {
                ["documentId"] = Format(expected),
                ["actual"] = actual is null ? null : Format(actual.Value)
            });
}
