using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;
using CipherVault.Ipc;

namespace CipherVault.Host.Session;

internal sealed class BridgePipeSession : IAsyncDisposable
{
    private readonly BridgePipeServerOptions _options;
    private readonly BridgeProcessSession _session;
    private readonly PipeJsonConnection _connection;
    private readonly ChannelReader<PipeRpcRequest> _requests;
    private readonly ChannelWriter<PipeRpcRequest> _requestWriter;
    private bool _closeRequested;
    private AccessGrantContext? _grant;
    private bool _disposed;

    public BridgePipeSession(
        BridgePipeServerOptions options,
        BridgeProcessSession session,
        PipeJsonConnection connection,
        ChannelReader<PipeRpcRequest> requests,
        ChannelWriter<PipeRpcRequest> requestWriter,
        CancellationToken sessionToken,
        CancellationToken handlerToken)
    {
        _options = options;
        _session = session;
        _connection = connection;
        _requests = requests;
        _requestWriter = requestWriter;
        SessionToken = sessionToken;
        HandlerToken = handlerToken;
    }

    private CancellationToken SessionToken { get; }
    private CancellationToken HandlerToken { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _requests.ReadAllAsync(
                               cancellationToken).ConfigureAwait(false))
            {
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
                else if (string.Equals(
                        request.Method,
                        "bridge.status",
                        StringComparison.Ordinal))
                {
                    response = await HandleStatusAsync(request)
                        .ConfigureAwait(false);
                }
                else if (string.Equals(
                        request.Method,
                        PipeProtocol.PingMethod,
                        StringComparison.Ordinal))
                {
                    response = PipeRpcCodec.Success(
                        request.RequestId,
                        new PipePingResponse(DateTimeOffset.UtcNow));
                }
                else if (string.Equals(
                        request.Method,
                        "bridge.session.close",
                        StringComparison.Ordinal))
                {
                    response = await HandleCloseAsync(request)
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
                if (_closeRequested)
                {
                    _requestWriter.TryComplete();
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested &&
            _requests.Completion.IsCompleted)
        {
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
                HandlerToken).ConfigureAwait(false);
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
                HandlerToken).ConfigureAwait(false);
            if (HasLostGrant(result))
            {
                _grant = null;
            }
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

    private static bool HasLostGrant(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            return code.GetString() switch
            {
                "access_grant_required" => true,
                "access_grant_revoked" => true,
                "access_grant_expired" => true,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<PipeRpcResponse> HandleStatusAsync(PipeRpcRequest request)
    {
        try
        {
            var status = await _options.Handler.GetStatusAsync(
                _session,
                _grant,
                HandlerToken).ConfigureAwait(false);
            return PipeRpcCodec.Success(request.RequestId, status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not IOException)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                exception is AdminPipeRpcException rpcException
                    ? rpcException.Code
                    : "bridge_error",
                exception.Message);
        }
    }

    private async Task<PipeRpcResponse> HandleCloseAsync(PipeRpcRequest request)
    {
        try
        {
            var closeRequest = PipeRpcCodec.ReadPayload<BridgeSessionCloseRequest>(
                request);
            var reason = string.IsNullOrWhiteSpace(closeRequest.Reason)
                ? "client-requested"
                : closeRequest.Reason.Trim();
            if (reason.Length > 200)
            {
                return PipeRpcCodec.Failure(
                    request.RequestId,
                    "invalid_input",
                    "Bridge close reason is too long.");
            }
            await _options.Handler.CloseSessionAsync(
                _session.ProcessSessionId,
                reason,
                HandlerToken).ConfigureAwait(false);
            _grant = null;
            _closeRequested = true;
            return PipeRpcCodec.Success(
                request.RequestId,
                new BridgeSessionCloseData(reason));
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
                "Bridge close request payload is invalid.");
        }
        catch (Exception exception) when (exception is not IOException)
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

