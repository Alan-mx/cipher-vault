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
                if (DateTimeOffset.UtcNow >= _session.ExpiresAt)
                {
                    await _connection.WriteAsync(
                        PipeRpcCodec.Failure(
                            request.RequestId,
                            "bridge_session_expired",
                            "The Bridge session has expired."),
                        cancellationToken).ConfigureAwait(false);
                    _requestWriter.TryComplete();
                    return;
                }

                PipeRpcResponse response;
                BridgeToolCallResult? delivery = null;
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
                    var execution = await HandleToolCallAsync(request)
                        .ConfigureAwait(false);
                    response = execution.Response;
                    delivery = execution.Delivery;
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

                try
                {
                    await _connection.WriteAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (delivery is not null)
                    {
                        await delivery.DisposeAsync().ConfigureAwait(false);
                    }
                }
                if (_closeRequested)
                {
                    _requestWriter.TryComplete();
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
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
        if (DateTimeOffset.UtcNow >= _session.ExpiresAt)
        {
            return PipeRpcCodec.Failure(
                request.RequestId,
                "bridge_session_expired",
                "The Bridge session has expired.");
        }
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

    private async Task<ToolCallResponse> HandleToolCallAsync(
        PipeRpcRequest request)
    {
        if (DateTimeOffset.UtcNow >= _session.ExpiresAt)
        {
            return Failure(
                request,
                "bridge_session_expired",
                "The Bridge session has expired.");
        }
        BridgeToolCallResult? execution = null;
        try
        {
            var call = PipeRpcCodec.ReadPayload<BridgeToolCallRequest>(request);
            execution = await _options.Handler.CallToolWithDeliveryAsync(
                _session,
                _grant,
                call,
                HandlerToken).ConfigureAwait(false);
            if (HasLostGrant(execution.Payload))
            {
                _grant = null;
            }
            return new ToolCallResponse(
                PipeRpcCodec.Success(request.RequestId, execution.Payload),
                execution);
        }
        catch (OperationCanceledException)
        {
            if (execution is not null)
            {
                await execution.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (IOException)
        {
            if (execution is not null)
            {
                await execution.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or System.Text.Json.JsonException)
        {
            if (execution is not null)
            {
                await execution.DisposeAsync().ConfigureAwait(false);
            }
            return Failure(
                request,
                "invalid_input",
                "Bridge tool request payload is invalid.");
        }
        catch (Exception exception) when (
            exception is not IOException)
        {
            if (execution is not null)
            {
                await execution.DisposeAsync().ConfigureAwait(false);
            }
            return Failure(
                request,
                exception is AdminPipeRpcException rpcException
                    ? rpcException.Code
                    : "bridge_error",
                exception.Message);
        }
    }

    private static ToolCallResponse Failure(
        PipeRpcRequest request,
        string code,
        string message)
    {
        return new ToolCallResponse(
            PipeRpcCodec.Failure(request.RequestId, code, message),
            Delivery: null);
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

    private sealed record ToolCallResponse(
        PipeRpcResponse Response,
        BridgeToolCallResult? Delivery);
}

