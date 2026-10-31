namespace CipherVault.Core.Documents;

public enum VaultDocumentFormat
{
    Markdown,
    Text
}

internal static class VaultDocumentFormatExtensions
{
    internal static string ToDatabaseValue(this VaultDocumentFormat format) =>
        format switch
        {
            VaultDocumentFormat.Markdown => "md",
            VaultDocumentFormat.Text => "txt",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    internal static string ToExtension(this VaultDocumentFormat format) =>
        format switch
        {
            VaultDocumentFormat.Markdown => ".md",
            VaultDocumentFormat.Text => ".txt",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    internal static VaultDocumentFormat ParseDatabaseValue(string value) =>
        value switch
        {
            "md" => VaultDocumentFormat.Markdown,
            "txt" => VaultDocumentFormat.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
