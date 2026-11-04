using System.Text;

namespace CipherVault.Core.Knowledge;

internal static class KnowledgeViewKey
{
    internal static string? NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Normalize(System.Text.NormalizationForm.FormC)
            .Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    internal static string? NormalizeKey(string? value) =>
        NormalizeLabel(value)?.ToUpperInvariant();

    internal static byte[] OrdinalSortKey(string value) =>
        Encoding.BigEndianUnicode.GetBytes(value);
}
