using System.Text.Json;
using CipherVault.Host.Contracts;

namespace CipherVault.Ipc;

public static class PipeRpcCodec
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static PipeRpcRequest CreateRequest<T>(
        string method,
        T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return new PipeRpcRequest(
            PipeProtocol.Version,
            Guid.NewGuid(),
            method,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
    }

    public static T ReadPayload<T>(PipeRpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Payload.Deserialize<T>(JsonOptions)
            ?? throw new InvalidDataException("RPC payload was empty.");
    }

    public static PipeRpcResponse Success<T>(
        Guid requestId,
        T data)
    {
        return new PipeRpcResponse(
            requestId,
            Ok: true,
            JsonSerializer.SerializeToElement(data, JsonOptions),
            Error: null);
    }

    public static PipeRpcResponse Failure(
        Guid requestId,
        string code,
        string message)
    {
        return new PipeRpcResponse(
            requestId,
            Ok: false,
            Data: null,
            new PipeError(code, message));
    }
}
