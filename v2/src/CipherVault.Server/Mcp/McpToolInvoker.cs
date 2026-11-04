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
            var structured = JsonSerializer.SerializeToElement(
                result,
                JsonOptions);
            return Success(structured);
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
            var structured = JsonSerializer.SerializeToElement(
                error,
                JsonOptions);
            return Error(structured);
        }
    }

    private static CallToolResult Success(JsonElement structured) =>
        new()
        {
            IsError = false,
            StructuredContent = structured,
            Content =
            [
                new TextContentBlock
                {
                    Text = structured.GetRawText()
                }
            ]
        };

    private static CallToolResult Error(JsonElement structured) =>
        new()
        {
            IsError = true,
            StructuredContent = structured,
            Content =
            [
                new TextContentBlock
                {
                    Text = structured.GetRawText()
                }
            ]
        };
}

public sealed record McpToolExecutionContext(
    string CorrelationId,
    VaultActorContext Actor);
