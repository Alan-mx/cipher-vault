using System.IO;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Ipc;

namespace CipherVault.Host.Session;

internal sealed class BridgePipeSession : IAsyncDisposable
{
    private readonly BridgePipeServerOptions _options;
    private readonly BridgeProcessSession _session;
    private readonly PipeJsonConnection _connection;
    private AccessGrantContext? _grant;
    private bool _disposed;

    public BridgePipeSession(
        BridgePipeServerOptions options,
        BridgeProcessSession session,
        PipeJsonConnection connection,
        CancellationToken cancellationToken)
    {
        _options = options;
        _session = session;
        _connection = connection;
        CancellationToken = cancellationToken;
    }

    private CancellationToken CancellationToken { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PipeRpcRequest request;
            try
            {
                request = await _connection.ReadAsync<PipeRpcRequest>(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            PipeRpcResponse response;
            if (string.Equals(
                    request.Method,
                    "bridge.grant.request",
                    StringComparison.Ordinal))
            {
                response = await HandleGrantAsync(request)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(
                    request.Method,
                    "bridge.tools.call",
                    StringComparison.Ordinal))
            {
                response = await HandleToolCallAsync(request)
                    .ConfigureAwait(false);
            }
            else
            {
                response = PipeRpcCodec.Failure(
                    request.RequestId,
                    "unsupported_method",
                    $"Unsupported bridge method: {request.Method}");
            }

            await _connection.WriteAsync(response, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _grant = null;
        return ValueTask.CompletedTask;
    }

    private async Task<PipeRpcResponse> HandleGrantAsync(PipeRpcRequest request)
    {
        if (_grant is not null)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                "conflict",
                "A Bridge Access Grant is already active.");
        }

        try
        {
            var grantRequest = PipeRpcCodec.ReadPayload<BridgeGrantRequest>(
                request);
            var issued = await _options.Handler.RequestGrantAsync(
                _session,
                grantRequest,
                CancellationToken).ConfigureAwait(false);
            _grant = issued.Context;
            return PipeRpcCodec.Success(request.RequestId, issued.Data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                "invalid_input",
                "Bridge Grant request payload is invalid.");
        }
        catch (Exception exception) when (
            exception is not IOException)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                exception is AdminPipeRpcException rpcException
                    ? rpcException.Code
                    : "bridge_error",
                exception.Message);
        }
    }

    private async Task<PipeRpcResponse> HandleToolCallAsync(
        PipeRpcRequest request)
    {
        try
        {
            var call = PipeRpcCodec.ReadPayload<BridgeToolCallRequest>(request);
            var result = await _options.Handler.CallToolAsync(
                _session,
                _grant,
                call,
                CancellationToken).ConfigureAwait(false);
            return PipeRpcCodec.Success(request.RequestId, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                "invalid_input",
                "Bridge tool request payload is invalid.");
        }
        catch (Exception exception) when (
            exception is not IOException)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                exception is AdminPipeRpcException rpcException
                    ? rpcException.Code
                    : "bridge_error",
                exception.Message);
        }
    }
}
