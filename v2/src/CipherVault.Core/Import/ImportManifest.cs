using System.Security.Cryptography;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;

namespace CipherVault.Core.Import;

internal sealed record ImportManifest(
    string SourceRoot,
    string ManifestPath,
    string ManifestSha256,
    IReadOnlyDictionary<string, ImportManifestDocument> DocumentsByPath);

internal sealed record ImportManifestDocument(
    Guid DocumentId,
    string VirtualPath,
    string Title,
    VaultDocumentFormat Format,
    int Revision,
    long Bytes,
    string Sha256);

internal static class ImportManifestReader
{
    private const int MaxManifestBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    internal static async Task<ImportManifest?> ReadAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            sourceRoot,
            PlaintextExportLimits.ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxManifestBytes)
            {
                throw InvalidManifest("Manifest exceeds the supported size.");
            }
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            throw new VaultException(
                VaultErrorCode.ImportSourceInvalid,
                "Import Manifest could not be read.",
                exception);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            RequireObject(root);
            var format = RequireString(root, "format");
            if (!string.Equals(
                    format,
                    PlaintextExportLimits.ManifestFormat,
                    StringComparison.Ordinal))
            {
                throw InvalidManifest("Manifest format is invalid.");
            }
            var version = RequireInt32(root, "formatVersion");
            if (version != PlaintextExportLimits.ManifestFormatVersion)
            {
                throw new VaultException(
                    VaultErrorCode.UnsupportedManifestVersion,
                    "Manifest version is not supported.");
            }
            var documentCount = RequireInt32(root, "documentCount");
            var totalContentBytes = RequireInt64(root, "totalContentBytes");
            if (documentCount < 0
                || documentCount > PlaintextExportLimits.MaxDocuments
                || totalContentBytes < 0
                || totalContentBytes > PlaintextExportLimits.MaxContentBytes)
            {
                throw InvalidManifest(
                    "Manifest limits are invalid.");
            }
            if (!root.TryGetProperty("documents", out var documentsElement)
                || documentsElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidManifest("Manifest documents must be an array.");
            }

            var documents = new Dictionary<string, ImportManifestDocument>(
                StringComparer.Ordinal);
            var documentIds = new HashSet<Guid>();
            long actualBytes = 0;
            foreach (var item in documentsElement.EnumerateArray())
            {
                RequireObject(item);
                if (!DocumentIdentity.TryParse(
                        RequireString(item, "documentId"),
                        out var documentId))
                {
                    throw InvalidManifest(
                        "Manifest contains an invalid documentId.");
                }
                if (!documentIds.Add(documentId))
                {
                    throw InvalidManifest(
                        "Manifest contains a duplicate documentId.");
                }

                var formatValue = RequireString(item, "format");
                var documentFormat = formatValue switch
                {
                    "md" => VaultDocumentFormat.Markdown,
                    "txt" => VaultDocumentFormat.Text,
                    _ => throw InvalidManifest(
                        "Manifest document format is invalid.")
                };
                var pathValue = RequireString(item, "virtualPath");
                NormalizedVirtualPath normalizedPath;
                try
                {
                    normalizedPath = VirtualPath.Normalize(
                        pathValue,
                        documentFormat);
                }
                catch (VaultException exception)
                {
                    throw InvalidManifest(
                        "Manifest document path is invalid.",
                        exception);
                }
                if (documents.ContainsKey(normalizedPath.Path))
                {
                    throw InvalidManifest(
                        "Manifest contains a duplicate virtualPath.");
                }

                string title;
                try
                {
                    title = TitleNormalizer.NormalizeTitle(
                        RequireString(item, "title"));
                }
                catch (VaultException exception)
                {
                    throw InvalidManifest(
                        "Manifest document title is invalid.",
                        exception);
                }
                var revision = RequireInt32(item, "revision");
                var bytesValue = RequireInt64(item, "bytes");
                var sha256 = RequireString(item, "sha256").ToLowerInvariant();
                if (revision < 1
                    || bytesValue < 0
                    || sha256.Length != 64
                    || !sha256.All(char.IsAsciiHexDigit))
                {
                    throw InvalidManifest(
                        "Manifest document metadata is invalid.");
                }
                actualBytes = checked(actualBytes + bytesValue);
                documents.Add(
                    normalizedPath.Path,
                    new ImportManifestDocument(
                        documentId,
                        normalizedPath.Path,
                        title,
                        documentFormat,
                        revision,
                        bytesValue,
                        sha256));
            }

            if (documentCount != documents.Count
                || totalContentBytes != actualBytes)
            {
                throw InvalidManifest(
                    "Manifest document count or total size is inconsistent.");
            }
            return new ImportManifest(
                sourceRoot,
                path,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                documents);
        }
        catch (VaultException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or OverflowException)
        {
            throw InvalidManifest(
                "Manifest JSON is invalid.",
                exception);
        }
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw InvalidManifest(
                $"Manifest field {name} must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static int RequireInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw InvalidManifest(
                $"Manifest field {name} must be an integer.");
        }
        return result;
    }

    private static long RequireInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            throw InvalidManifest(
                $"Manifest field {name} must be an integer.");
        }
        return result;
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest("Manifest value must be an object.");
        }
    }

    private static VaultException InvalidManifest(
        string message,
        Exception? innerException = null) =>
        new(
            VaultErrorCode.InvalidManifest,
            message,
            innerException);
}
