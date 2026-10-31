using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

internal static class TitleNormalizer
{
    internal const int MaxScalars = 200;

    internal static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Document title is required.");
        }

        var normalized = UnicodeText.CollapseWhitespace(title);
        int scalarCount;
        try
        {
            scalarCount = UnicodeText.CountScalars(normalized);
        }
        catch (ArgumentException)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Document title contains invalid Unicode data.");
        }

        if (scalarCount is < 1 or > MaxScalars)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Document title must contain 1 to {MaxScalars} Unicode characters.");
        }

        return normalized;
    }

    internal static string NormalizeKey(string title) =>
        NormalizeTitle(title).ToUpperInvariant();
}
