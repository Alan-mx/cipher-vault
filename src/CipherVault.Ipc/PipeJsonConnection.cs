using System.Buffers.Binary;
using System.Text.Json;

namespace CipherVault.Ipc;

public sealed class PipeJsonConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public PipeJsonConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async ValueTask WriteAsync<T>(
        T value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length == 0 || payload.Length > PipeProtocol.MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"Pipe message size must be between 1 and {PipeProtocol.MaxMessageBytes} bytes.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            await _stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<T> ReadAsync<T>(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var length = new byte[4];
        await _stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (payloadLength <= 0 ||
            payloadLength > PipeProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"Pipe message size is outside the allowed range: {payloadLength}.");
        }
        var payload = new byte[payloadLength];
        await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("Pipe message payload was empty.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
