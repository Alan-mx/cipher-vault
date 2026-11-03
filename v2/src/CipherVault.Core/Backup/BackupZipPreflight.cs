using System.Buffers.Binary;
using System.Text;

namespace CipherVault.Core.Backup;

public sealed class BackupPackageValidationException : Exception
{
    public BackupPackageValidationException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record BackupZipEntryDescriptor(
    string Name,
    long CompressedBytes,
    long UncompressedBytes,
    ushort Flags,
    ushort CompressionMethod,
    uint ExternalAttributes,
    long LocalHeaderOffset);

internal sealed record BackupZipPreflightResult(
    IReadOnlyList<BackupZipEntryDescriptor> Entries,
    long ArchiveBytes,
    long TotalUncompressedBytes)
{
    public BackupZipEntryDescriptor GetRequired(string name) =>
        Entries.Single(entry =>
            string.Equals(entry.Name, name, StringComparison.Ordinal));
}

internal static class BackupZipPreflight
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralDirectoryHeaderSignature = 0x02014b50;
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const int EndOfCentralDirectorySize = 22;
    private const int Zip64LocatorSize = 20;
    private const int Zip64EndOfCentralDirectoryMinimumSize = 56;
    private const int CentralDirectoryHeaderSize = 46;
    private const int LocalFileHeaderSize = 30;
    private const int MaximumCommentSearch = 65_557;
    private static readonly string[] RequiredEntries =
    [
        BackupConstants.DatabaseEntryName,
        BackupConstants.ManifestEntryName,
        BackupConstants.KeyEnvelopeEntryName
    ];

    public static async Task<BackupZipPreflightResult> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await InspectAsync(stream, cancellationToken);
    }

    internal static async Task<BackupZipPreflightResult> InspectAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP stream is not readable.");
        }

        if (stream.Length is <= 0 or > BackupConstants.MaxPackageBytes)
        {
            throw Invalid(
                "backup_too_large",
                "Backup package size is outside the supported range.");
        }

        var eocd = await FindEndOfCentralDirectoryAsync(
            stream,
            cancellationToken);
        var central = await ReadCentralDirectoryLocationAsync(
            stream,
            eocd,
            cancellationToken);
        if (central.Entries != BackupConstants.RequiredEntryCount
            || central.Size > stream.Length
            || central.Offset < 0
            || central.Offset + central.Size > stream.Length)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP directory is invalid.");
        }

        var entries = new List<BackupZipEntryDescriptor>(
            BackupConstants.RequiredEntryCount);
        long totalUncompressed = 0;
        stream.Seek(central.Offset, SeekOrigin.Begin);
        for (var index = 0; index < central.Entries; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixedHeader = new byte[CentralDirectoryHeaderSize];
            await ReadExactlyAsync(stream, fixedHeader, cancellationToken);
            if (ReadUInt32(fixedHeader, 0) != CentralDirectoryHeaderSignature)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP central directory header is invalid.");
            }

            var flags = ReadUInt16(fixedHeader, 8);
            var method = ReadUInt16(fixedHeader, 10);
            var compressed = ReadUInt32(fixedHeader, 20);
            var uncompressed = ReadUInt32(fixedHeader, 24);
            var nameBytes = ReadUInt16(fixedHeader, 28);
            var extraBytes = ReadUInt16(fixedHeader, 30);
            var commentBytes = ReadUInt16(fixedHeader, 32);
            var diskStart = ReadUInt16(fixedHeader, 34);
            var externalAttributes = ReadUInt32(fixedHeader, 38);
            var localOffset = ReadUInt32(fixedHeader, 42);
            if (nameBytes == 0
                || nameBytes > 4096
                || extraBytes > 65_535
                || commentBytes > 65_535)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP entry metadata is invalid.");
            }

            var nameBuffer = new byte[nameBytes];
            var extraBuffer = new byte[extraBytes];
            var commentBuffer = new byte[commentBytes];
            await ReadExactlyAsync(stream, nameBuffer, cancellationToken);
            await ReadExactlyAsync(stream, extraBuffer, cancellationToken);
            await ReadExactlyAsync(stream, commentBuffer, cancellationToken);
            var entryName = DecodeEntryName(nameBuffer, flags);
            ValidateEntryName(entryName);
            if ((flags & 0x2041) != 0)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Encrypted or masked ZIP entries are not supported.");
            }
            if (method is not (0 or 8))
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP compression method is not supported.");
            }

            var zip64 = ParseZip64Extra(
                extraBuffer,
                uncompressed == uint.MaxValue,
                compressed == uint.MaxValue,
                localOffset == uint.MaxValue,
                diskStart == ushort.MaxValue);
            var actualUncompressed = uncompressed == uint.MaxValue
                ? zip64.Uncompressed ?? -1
                : uncompressed;
            var actualCompressed = compressed == uint.MaxValue
                ? zip64.Compressed ?? -1
                : compressed;
            var actualOffset = localOffset == uint.MaxValue
                ? zip64.LocalHeaderOffset ?? -1
                : localOffset;
            var actualDisk = diskStart == ushort.MaxValue
                ? zip64.DiskStart ?? -1
                : diskStart;
            if (actualUncompressed < 0
                || actualCompressed < 0
                || actualOffset < 0
                || actualDisk != 0
                || actualOffset + LocalFileHeaderSize > stream.Length)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP entry sizes or disk fields are invalid.");
            }
            if (actualUncompressed > BackupConstants.MaxUncompressedBytes
                || totalUncompressed
                    > BackupConstants.MaxUncompressedBytes - actualUncompressed)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup uncompressed size exceeds the supported limit.");
            }
            if (!RatioAllowed(actualCompressed, actualUncompressed))
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup compression ratio exceeds the supported limit.");
            }
            ValidateAttributes(externalAttributes);
            var nextCentralPosition = stream.Position;
            await ValidateLocalHeaderAsync(
                stream,
                actualOffset,
                nameBuffer,
                flags,
                method,
                actualCompressed,
                actualUncompressed,
                cancellationToken);
            stream.Seek(nextCentralPosition, SeekOrigin.Begin);
            if (entries.Any(entry =>
                    string.Equals(
                        entry.Name,
                        entryName,
                        StringComparison.Ordinal)))
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP contains duplicate entries.");
            }

            entries.Add(new BackupZipEntryDescriptor(
                entryName,
                actualCompressed,
                actualUncompressed,
                flags,
                method,
                externalAttributes,
                actualOffset));
            totalUncompressed += actualUncompressed;
        }

        var centralEnd = stream.Position;
        if (centralEnd > central.Offset + central.Size
            || entries.Count != BackupConstants.RequiredEntryCount
            || !RequiredEntries.All(required => entries.Any(entry =>
                string.Equals(entry.Name, required, StringComparison.Ordinal))))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP entry set is invalid.");
        }

        var ordered = RequiredEntries
            .Select(name => entries.Single(entry => entry.Name == name))
            .ToArray();
        return new BackupZipPreflightResult(
            ordered,
            stream.Length,
            totalUncompressed);
    }

    private static async Task<EndOfCentralDirectory> FindEndOfCentralDirectoryAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var length = (int)Math.Min(stream.Length, MaximumCommentSearch);
        var tail = new byte[length];
        stream.Seek(
            stream.Length - length,
            SeekOrigin.Begin);
        await ReadExactlyAsync(stream, tail, cancellationToken);
        for (var index = length - EndOfCentralDirectorySize;
             index >= 0;
             index--)
        {
            if (ReadUInt32(tail, index) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = ReadUInt16(tail, index + 20);
            if (commentLength != 0
                || index + EndOfCentralDirectorySize != length)
            {
                continue;
            }

            return new EndOfCentralDirectory(
                stream.Length - length + index,
                ReadUInt16(tail, index + 4),
                ReadUInt16(tail, index + 6),
                ReadUInt16(tail, index + 8),
                ReadUInt16(tail, index + 10),
                ReadUInt32(tail, index + 12),
                ReadUInt32(tail, index + 16));
        }

        throw Invalid(
            "restore_archive_invalid",
            "Backup ZIP end-of-directory record is missing.");
    }

    private static async Task<CentralDirectoryLocation> ReadCentralDirectoryLocationAsync(
        Stream stream,
        EndOfCentralDirectory eocd,
        CancellationToken cancellationToken)
    {
        if (eocd.DiskNumber != 0
            || eocd.CentralDirectoryDisk != 0
            || eocd.EntriesOnDisk != eocd.Entries)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Multi-disk ZIP archives are not supported.");
        }
        if (eocd.Entries == ushort.MaxValue
            || eocd.Size == uint.MaxValue
            || eocd.Offset == uint.MaxValue)
        {
            return await ReadZip64CentralDirectoryLocationAsync(
                stream,
                eocd.OffsetPosition,
                cancellationToken);
        }

        if (eocd.Entries > BackupConstants.RequiredEntryCount)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP contains too many entries.");
        }
        return new CentralDirectoryLocation(
            eocd.Entries,
            eocd.Size,
            eocd.Offset);
    }

    private static async Task<CentralDirectoryLocation> ReadZip64CentralDirectoryLocationAsync(
        Stream stream,
        long eocdOffset,
        CancellationToken cancellationToken)
    {
        if (eocdOffset < Zip64LocatorSize)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 locator is missing.");
        }

        var locator = new byte[Zip64LocatorSize];
        stream.Seek(
            eocdOffset - Zip64LocatorSize,
            SeekOrigin.Begin);
        await ReadExactlyAsync(stream, locator, cancellationToken);
        if (ReadUInt32(locator, 0) != Zip64LocatorSignature
            || ReadUInt32(locator, 4) != 0
            || ReadUInt32(locator, 16) != 1)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 locator is invalid.");
        }

        var zip64OffsetValue = ReadUInt64(locator, 8);
        if (zip64OffsetValue > long.MaxValue)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 end-of-directory offset is invalid.");
        }
        var zip64Offset = (long)zip64OffsetValue;
        if (zip64Offset + Zip64EndOfCentralDirectoryMinimumSize
                > eocdOffset)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 end-of-directory offset is invalid.");
        }

        var header = new byte[Zip64EndOfCentralDirectoryMinimumSize];
        stream.Seek(
            zip64Offset,
            SeekOrigin.Begin);
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (ReadUInt32(header, 0) != Zip64EndOfCentralDirectorySignature
            || ReadUInt64(header, 4) < 44)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 end-of-directory record is invalid.");
        }

        var disk = ReadUInt32(header, 16);
        var centralDisk = ReadUInt32(header, 20);
        var entriesOnDiskValue = ReadUInt64(header, 24);
        var entriesValue = ReadUInt64(header, 32);
        var sizeValue = ReadUInt64(header, 40);
        var offsetValue = ReadUInt64(header, 48);
        if (disk != 0
            || centralDisk != 0
            || entriesOnDiskValue != entriesValue
            || entriesValue > BackupConstants.RequiredEntryCount
            || sizeValue > long.MaxValue
            || offsetValue > long.MaxValue)
        {
            throw Invalid(
                "restore_archive_invalid",
                "ZIP64 directory fields are invalid.");
        }

        return new CentralDirectoryLocation(
            checked((int)entriesValue),
            (long)sizeValue,
            (long)offsetValue);
    }

    private static async Task ValidateLocalHeaderAsync(
        Stream stream,
        long offset,
        byte[] centralName,
        ushort centralFlags,
        ushort centralMethod,
        long compressedBytes,
        long uncompressedBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[LocalFileHeaderSize];
        stream.Seek(offset, SeekOrigin.Begin);
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (ReadUInt32(header, 0) != LocalFileHeaderSignature)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP local file header is invalid.");
        }

        var flags = ReadUInt16(header, 6);
        var method = ReadUInt16(header, 8);
        var compressed = ReadUInt32(header, 18);
        var uncompressed = ReadUInt32(header, 22);
        var nameBytes = ReadUInt16(header, 26);
        var extraBytes = ReadUInt16(header, 28);
        if (flags != centralFlags
            || method != centralMethod
            || nameBytes != centralName.Length)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP local and central headers differ.");
        }

        var localName = new byte[nameBytes];
        await ReadExactlyAsync(stream, localName, cancellationToken);
        if (!localName.AsSpan().SequenceEqual(centralName))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP local entry name differs.");
        }

        if ((flags & 0x0008) == 0
            && (compressed != uint.MaxValue
                && compressed != compressedBytes
                || uncompressed != uint.MaxValue
                && uncompressed != uncompressedBytes))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP local entry sizes are invalid.");
        }

        var dataStart = checked(offset + LocalFileHeaderSize + nameBytes + extraBytes);
        if (dataStart < 0
            || dataStart + compressedBytes > stream.Length)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP local entry data is out of range.");
        }
    }

    private static Zip64Extra ParseZip64Extra(
        ReadOnlySpan<byte> extra,
        bool needUncompressed,
        bool needCompressed,
        bool needOffset,
        bool needDisk)
    {
        if (!needUncompressed && !needCompressed && !needOffset && !needDisk)
        {
            return default;
        }

        var position = 0;
        while (position + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra[position..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(position + 2)..]);
            position += 4;
            if (position + size > extra.Length)
            {
                break;
            }
            if (id == 0x0001)
            {
                var values = extra.Slice(position, size);
                var valuePosition = 0;
                long? uncompressed = null;
                long? compressed = null;
                long? offset = null;
                int? disk = null;
                if (needUncompressed)
                {
                    if (!TryReadUInt64(values, ref valuePosition, out var value)
                        || value > long.MaxValue)
                    {
                        break;
                    }
                    uncompressed = (long)value;
                }
                if (needCompressed)
                {
                    if (!TryReadUInt64(values, ref valuePosition, out var value)
                        || value > long.MaxValue)
                    {
                        break;
                    }
                    compressed = (long)value;
                }
                if (needOffset)
                {
                    if (!TryReadUInt64(values, ref valuePosition, out var value)
                        || value > long.MaxValue)
                    {
                        break;
                    }
                    offset = (long)value;
                }
                if (needDisk)
                {
                    if (valuePosition + 4 > values.Length)
                    {
                        break;
                    }
                    disk = BinaryPrimitives.ReadInt32LittleEndian(values[valuePosition..]);
                }

                return new Zip64Extra(
                    uncompressed,
                    compressed,
                    offset,
                    disk);
            }

            position += size;
        }

        return default;
    }

    private static bool TryReadUInt64(
        ReadOnlySpan<byte> source,
        ref int position,
        out ulong value)
    {
        if (position + sizeof(ulong) > source.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(source[position..]);
        position += sizeof(ulong);
        return true;
    }

    private static void ValidateEntryName(string name)
    {
        if (name.Length == 0
            || name.Contains('\\')
            || name.StartsWith('/')
            || name.EndsWith('/')
            || name.Split('/').Any(segment => segment is "." or ".."))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP entry path is invalid.");
        }
    }

    private static void ValidateAttributes(uint externalAttributes)
    {
        var dosAttributes = externalAttributes & 0xFF;
        if ((dosAttributes & 0x10) != 0)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP directory entries are not supported.");
        }

        var unixType = (externalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x8000))
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP special file entries are not supported.");
        }
    }

    private static bool RatioAllowed(long compressed, long uncompressed)
    {
        if (uncompressed == 0)
        {
            return compressed == 0;
        }

        if (compressed <= 0)
        {
            return false;
        }

        return uncompressed <= compressed * BackupConstants.MaxCompressionRatio;
    }

    private static string DecodeEntryName(byte[] bytes, ushort flags)
    {
        try
        {
            var encoding = (flags & 0x0800) != 0
                ? new UTF8Encoding(false, true)
                : Encoding.ASCII;
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw Invalid(
                "restore_archive_invalid",
                "Backup ZIP entry name is not valid text.",
                exception);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer[read..],
                cancellationToken);
            if (count == 0)
            {
                throw Invalid(
                    "restore_archive_invalid",
                    "Backup ZIP ended unexpectedly.");
            }
            read += count;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

    private static BackupPackageValidationException Invalid(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed record EndOfCentralDirectory(
        long OffsetPosition,
        ushort DiskNumber,
        ushort CentralDirectoryDisk,
        ushort EntriesOnDisk,
        ushort Entries,
        uint Size,
        uint Offset);

    private sealed record CentralDirectoryLocation(
        int Entries,
        long Size,
        long Offset);

    private readonly record struct Zip64Extra(
        long? Uncompressed,
        long? Compressed,
        long? LocalHeaderOffset,
        int? DiskStart);
}
