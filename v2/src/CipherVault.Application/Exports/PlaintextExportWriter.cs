using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Database;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Exports;
using CipherVault.Core.Storage.Exports;

namespace CipherVault.Application.Exports;

internal static class PlaintextExportWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Converters = { new UtcDateTimeOffsetConverter() }
    };

    internal static async Task<PlaintextExportResult> WriteAsync(
        VaultOperationContext context,
        Stream destination,
        PlaintextExportMetadata metadata,
        DateTimeOffset exportedAtUtc,
        CancellationToken cancellationToken)
    {
        var manifestDocuments = new List<PlaintextExportManifestDocument>(
            metadata.DocumentCount);
        long actualBytes = 0;
        long zipBytes;
        using var counting = new AbortableCountingStream(destination);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(
                counting,
                ZipArchiveMode.Create,
                leaveOpen: true);
            PlaintextExportCursor? cursor = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = await PlaintextExportStore.ReadNextAsync(
                    context,
                    cursor,
                    cancellationToken);
                if (document is null)
                {
                    break;
                }

                var content = PlaintextExportContentTransformer.Transform(
                    document.Content,
                    document.VirtualPath,
                    document.Links,
                    document.DocumentId,
                    document.Format);
                var bytes = EncodeContent(content);
                actualBytes = checked(actualBytes + bytes.LongLength);
                if (actualBytes > PlaintextExportLimits.MaxContentBytes)
                {
                    throw new VaultException(
                        Core.Errors.VaultErrorCode.ExportTooLarge,
                        "Plaintext export exceeds the supported content limit.");
                }

                var entry = archive.CreateEntry(
                    document.VirtualPath,
                    CompressionLevel.Optimal);
                entry.LastWriteTime = exportedAtUtc;
                await using (var entryStream = entry.Open())
                {
                    await entryStream.WriteAsync(bytes, cancellationToken);
                }

                manifestDocuments.Add(new PlaintextExportManifestDocument(
                    document.DocumentId.ToString("D"),
                    document.VirtualPath,
                    document.Title,
                    document.Format == VaultDocumentFormat.Markdown
                        ? "md"
                        : "txt",
                    document.Revision,
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes))
                        .ToLowerInvariant()));
                cursor = new PlaintextExportCursor(
                    document.PathKey,
                    document.DocumentId);
            }

            if (manifestDocuments.Count != metadata.DocumentCount
                || actualBytes != metadata.TotalContentBytes)
            {
                throw new VaultException(
                    Core.Errors.VaultErrorCode.DatabaseError,
                    "Plaintext export snapshot changed unexpectedly.");
            }

            var manifest = new PlaintextExportManifest(
                PlaintextExportLimits.ManifestFormat,
                PlaintextExportLimits.ManifestFormatVersion,
                exportedAtUtc.ToUniversalTime(),
                manifestDocuments.Count,
                actualBytes,
                manifestDocuments);
            var manifestEntry = archive.CreateEntry(
                PlaintextExportLimits.ManifestFileName,
                CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = exportedAtUtc;
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                JsonOptions);
            await using (var manifestStream = manifestEntry.Open())
            {
                await manifestStream.WriteAsync(
                    manifestBytes,
                    cancellationToken);
            }

            archive.Dispose();
            archive = null;
            zipBytes = counting.BytesWritten;
        }
        catch
        {
            counting.Fail();
            try
            {
                archive?.Dispose();
            }
            catch
            {
                // Preserve the original export failure.
            }
            throw;
        }

        return new PlaintextExportResult(
            manifestDocuments.Count,
            actualBytes,
            zipBytes);
    }

    private static byte[] EncodeContent(string content)
    {
        try
        {
            return StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException exception)
        {
            throw new VaultException(
                Core.Errors.VaultErrorCode.StorageError,
                "Document content is not valid Unicode.",
                exception);
        }
    }

    private sealed class AbortableCountingStream : Stream
    {
        private readonly Stream inner;
        private bool failed;

        internal AbortableCountingStream(Stream inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        internal long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !failed && inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal void Fail() => failed = true;

        public override void Flush()
        {
            ThrowIfFailed();
            inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            ThrowIfFailed();
            return inner.FlushAsync(cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfFailed();
            inner.Write(buffer, offset, count);
            BytesWritten = checked(BytesWritten + count);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ThrowIfFailed();
            await inner.WriteAsync(
                buffer.AsMemory(offset, count),
                cancellationToken);
            BytesWritten = checked(BytesWritten + count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ThrowIfFailed();
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten = checked(BytesWritten + buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The HTTP response stream is owned by ASP.NET Core.
            }
            base.Dispose(disposing);
        }

        private void ThrowIfFailed()
        {
            if (failed)
            {
                throw new IOException("Plaintext export stream was aborted.");
            }
        }
    }

    private sealed class UtcDateTimeOffsetConverter :
        JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            var formatted = value
                .ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            writer.WriteStringValue(
                formatted.EndsWith("+00:00", StringComparison.Ordinal)
                    ? formatted[..^6] + "Z"
                    : formatted);
        }
    }
}
