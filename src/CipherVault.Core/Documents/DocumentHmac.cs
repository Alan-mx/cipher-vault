using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CipherVault.Core.Documents;

internal static class DocumentHmac
{
    private static readonly byte[] Protocol = Encoding.ASCII.GetBytes("PVHMAC1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static byte[] Compute(
        byte[] integrityKey,
        string documentId,
        long revision,
        string title,
        string sourcePath,
        string format,
        string headingsJson,
        string tagsJson,
        string content)
    {
        var headings = ParseHeadings(headingsJson);
        var tags = ParseTags(tagsJson);

        using var stream = new MemoryStream();
        stream.Write(Protocol);
        WriteString(stream, documentId);
        WriteInt64(stream, revision);
        WriteString(stream, title);
        WriteString(stream, sourcePath);
        WriteString(stream, format);
        WriteInt32(stream, headings.Count);
        foreach (var heading in headings)
        {
            stream.WriteByte(checked((byte)heading.Level));
            WriteString(stream, heading.Text);
        }
        WriteInt32(stream, tags.Count);
        foreach (var tag in tags)
        {
            WriteString(stream, tag);
        }
        WriteInt64(stream, Encoding.UTF8.GetByteCount(content));
        stream.Write(Encoding.UTF8.GetBytes(content));

        return HMACSHA256.HashData(integrityKey, stream.ToArray());
    }

    public static bool Verify(
        byte[] integrityKey,
        byte[] expected,
        string documentId,
        long revision,
        string title,
        string sourcePath,
        string format,
        string headingsJson,
        string tagsJson,
        string content)
    {
        var actual = Compute(
            integrityKey,
            documentId,
            revision,
            title,
            sourcePath,
            format,
            headingsJson,
            tagsJson,
            content);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static List<MarkdownHeading> ParseHeadings(string headingsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<MarkdownHeading>>(
                headingsJson,
                JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored headings JSON is invalid.", exception);
        }
    }

    private static List<string> ParseTags(string tagsJson)
    {
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(tagsJson, JsonOptions) ?? [];
            return values
                .Select(NormalizeTag)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored tags JSON is invalid.", exception);
        }
    }

    private static string NormalizeTag(string value)
    {
        return string.Join(
            ' ',
            value
                .Normalize(NormalizationForm.FormC)
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)value));
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, checked((ulong)value));
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    internal sealed record MarkdownHeading(int Level, string Text);
}
