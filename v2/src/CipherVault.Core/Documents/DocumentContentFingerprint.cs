using System.Security.Cryptography;
using System.Text;

namespace CipherVault.Core.Documents;

internal static class DocumentContentFingerprint
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Compute(
        string content,
        VaultDocumentFormat format)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = Normalize(content);
        if (format == VaultDocumentFormat.Markdown)
        {
            normalized = RemoveFrontMatterId(normalized);
        }
        return Convert.ToHexString(
                SHA256.HashData(StrictUtf8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static string Normalize(string content)
    {
        var withoutBom = content.StartsWith('\uFEFF')
            ? content[1..]
            : content;
        return withoutBom
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
    }

    private static string RemoveFrontMatterId(string content)
    {
        if (!FrontMatterDocument.TryParse(content, out var snapshot))
        {
            return content;
        }
        var id = snapshot.Fields
            .Where(field => string.Equals(
                field.Key,
                DocumentIdentity.FrontMatterKey,
                StringComparison.Ordinal))
            .ToArray();
        if (id.Length == 0)
        {
            return content;
        }
        if (id.Length != 1)
        {
            throw new CipherVault.Core.Errors.VaultException(
                CipherVault.Core.Errors.VaultErrorCode.InvalidDocumentId,
                "Frontmatter contains duplicate id fields.");
        }

        var field = id[0];
        var end = field.EndOffset;
        if (end < content.Length && content[end] == '\n')
        {
            end++;
        }
        return string.Concat(
            content.AsSpan(0, field.StartOffset),
            content.AsSpan(end));
    }
}
