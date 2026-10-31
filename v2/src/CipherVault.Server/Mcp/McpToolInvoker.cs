using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Server.Api;
using CipherVault.Server.Mcp.Contracts;
using ModelContextProtocol.Protocol;

namespace CipherVault.Server.Mcp;

public sealed class McpToolInvoker
{
    private static readonly JsonSerializerOptions JsonOptions =
        ApiJson.CreateOptions();
    private readonly IHttpContextAccessor httpContextAccessor;

    public McpToolInvoker(IHttpContextAccessor httpContextAccessor)
    {
        this.httpContextAccessor = httpContextAccessor;
    }

    public McpToolExecutionContext GetContext()
    {
        var correlationId = httpContextAccessor.HttpContext?.GetId()
            ?? Guid.NewGuid().ToString("N");
        return new McpToolExecutionContext(
            correlationId,
            new VaultActorContext(VaultActorKind.Mcp, correlationId));
    }

    public async Task<CallToolResult> ExecuteAsync<T>(
        string toolName,
        Func<McpToolExecutionContext, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var context = GetContext();
        try
        {
            var result = await action(context, cancellationToken);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            return Success(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var error = McpToolErrorMapper.Map(
                exception,
                context.CorrelationId);
            var json = JsonSerializer.Serialize(error, JsonOptions);
            return Error(json);
        }
    }

    private static CallToolResult Success(string json) =>
        new()
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = json
                }
            ]
        };

    private static CallToolResult Error(string json) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = json
                }
            ]
        };
}

public sealed record McpToolExecutionContext(
    string CorrelationId,
    VaultActorContext Actor);
