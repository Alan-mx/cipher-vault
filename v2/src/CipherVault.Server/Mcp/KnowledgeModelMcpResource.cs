using System.Text.Json;
using CipherVault.Application.KnowledgeBase;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CipherVault.Server.Mcp;

[McpServerResourceType]
public sealed class KnowledgeModelMcpResource(
    KnowledgeModelService models)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [McpServerResource(
        UriTemplate = "cipher-vault://knowledge-model",
        Name = "knowledge_model",
        Title = "CipherVault Knowledge Model",
        MimeType = "application/json")]
    public async Task<TextResourceContents> GetAsync(
        CancellationToken cancellationToken)
    {
        var model = await models.GetAsync(cancellationToken);
        return new TextResourceContents
        {
            Uri = "cipher-vault://knowledge-model",
            MimeType = "application/json",
            Text = JsonSerializer.Serialize(model, JsonOptions)
        };
    }
}
