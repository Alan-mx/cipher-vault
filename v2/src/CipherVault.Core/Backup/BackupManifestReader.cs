using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CipherVault.Core.Backup;

internal static class BackupManifestReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> TopLevelFields =
    [
        "format",
        "formatVersion",
        "vaultId",
        "createdAtUtc",
        "files"
    ];
    private static readonly HashSet<string> FileFields =
    [
        "path",
        "bytes",
        "sha256"
    ];

    public static async Task<BackupManifest> ReadAsync(
        string packagePath,
        BackupZipPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ReadAsync(stream, preflight, cancellationToken);
    }

    internal static async Task<BackupManifest> ReadAsync(
        Stream stream,
        BackupZipPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw Invalid("Manifest stream is not readable.");
        }

        using var archive = new ZipArchive(
            stream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        var entry = archive.GetEntry(BackupConstants.ManifestEntryName)
            ?? throw Invalid("Manifest entry is missing.");
        var descriptor = preflight.GetRequired(
            BackupConstants.ManifestEntryName);
        if (descriptor.UncompressedBytes > BackupConstants.MaxManifestBytes)
        {
            throw Invalid("Manifest exceeds the supported size.");
        }

        await using var entryStream = entry.Open();
        var content = await ReadBoundedAsync(
            entryStream,
            BackupConstants.MaxManifestBytes,
            cancellationToken);
        if (content.Length != descriptor.UncompressedBytes)
        {
            throw Invalid("Manifest size differs from the ZIP directory.");
        }
        if (content.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            throw Invalid("Manifest must not include a UTF-8 BOM.");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw Invalid("Manifest is not valid UTF-8.", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw Invalid("Manifest JSON is invalid.", exception);
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    private static BackupManifest Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Manifest root must be an object.");
        }
        var topLevel = ReadObject(root, TopLevelFields);
        var formatElement = topLevel["format"];
        if (formatElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                formatElement.GetString(),
                BackupConstants.Format,
                StringComparison.Ordinal))
        {
            throw Invalid("Manifest format is not supported.");
        }

        var versionElement = topLevel["formatVersion"];
        if (versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version)
            || version != BackupConstants.FormatVersion)
        {
            throw Invalid("Manifest format version is not supported.");
        }

        var vaultElement = topLevel["vaultId"];
        if (vaultElement.ValueKind != JsonValueKind.String
            || !TryParseLowercaseGuid(
                vaultElement.GetString(),
                out var vaultId))
        {
            throw Invalid("Manifest VaultId is invalid.");
        }

        var createdElement = topLevel["createdAtUtc"];
        if (createdElement.ValueKind != JsonValueKind.String
            || !TryParseUtc(createdElement.GetString(), out var createdAt))
        {
            throw Invalid("Manifest creation time is invalid.");
        }

        var filesElement = topLevel["files"];
        if (filesElement.ValueKind != JsonValueKind.Array
            || filesElement.GetArrayLength() != 2)
        {
            throw Invalid("Manifest files must contain exactly two entries.");
        }

        var files = new List<BackupManifestFile>(2);
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Manifest file entry must be an object.");
            }
            var fields = ReadObject(fileElement, FileFields);
            var pathElement = fields["path"];
            var bytesElement = fields["bytes"];
            var hashElement = fields["sha256"];
            if (pathElement.ValueKind != JsonValueKind.String
                || bytesElement.ValueKind != JsonValueKind.Number
                || !bytesElement.TryGetInt64(out var bytes)
                || bytes <= 0
                || hashElement.ValueKind != JsonValueKind.String
                || !Sha256Pattern.IsMatch(hashElement.GetString()!))
            {
                throw Invalid("Manifest file metadata is invalid.");
            }

            files.Add(new BackupManifestFile(
                pathElement.GetString()!,
                bytes,
                hashElement.GetString()!));
        }

        if (!string.Equals(
                files[0].Path,
                BackupConstants.DatabaseEntryName,
                StringComparison.Ordinal)
            || !string.Equals(
                files[1].Path,
                BackupConstants.KeyEnvelopeEntryName,
                StringComparison.Ordinal)
            || files[0].Bytes > BackupConstants.MaxUncompressedBytes
            || files[1].Bytes > BackupConstants.MaxKeyEnvelopeBytes
            || files[0].Bytes
                > BackupConstants.MaxUncompressedBytes - files[1].Bytes)
        {
            throw Invalid("Manifest file set is invalid.");
        }

        return new BackupManifest(
            BackupConstants.Format,
            version,
            vaultId,
            createdAt,
            files);
    }

    private static Dictionary<string, JsonElement> ReadObject(
        JsonElement element,
        IReadOnlySet<string> expected)
    {
        var result = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)
                || !result.TryAdd(property.Name, property.Value))
            {
                throw Invalid("Manifest contains unknown or duplicate fields.");
            }
        }
        if (result.Count != expected.Count)
        {
            throw Invalid("Manifest is missing required fields.");
        }

        return result;
    }

    private static bool TryParseLowercaseGuid(
        string? value,
        out Guid result)
    {
        result = Guid.Empty;
        if (!Guid.TryParseExact(value, "D", out result)
            || result == Guid.Empty)
        {
            return false;
        }

        return string.Equals(
            value,
            result.ToString("D"),
            StringComparison.Ordinal);
    }

    private static bool TryParseUtc(
        string? value,
        out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrEmpty(value)
            || !value.EndsWith('Z')
            || !DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out result))
        {
            return false;
        }

        result = result.ToUniversalTime();
        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maximum,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(
            capacity: checked((int)Math.Min(maximum, 4096)));
        var buffer = new byte[4096];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return memory.ToArray();
            }
            total += read;
            if (total > maximum)
            {
                throw Invalid("Manifest exceeds the supported size.");
            }
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static BackupPackageValidationException Invalid(
        string message,
        Exception? innerException = null) =>
        new(
            "restore_manifest_invalid",
            message,
            innerException);
}
