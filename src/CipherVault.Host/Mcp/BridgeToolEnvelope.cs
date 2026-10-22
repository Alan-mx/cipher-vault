using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Configuration;
using CipherVault.Host.Session;

namespace CipherVault.Host.Mcp;

public static class BridgeToolEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string VaultStatus(
        string clientId,
        McpAccessMode accessMode,
        HostSessionSnapshot session)
    {
        return Serialize(new
        {
            ok = true,
            vault = new
            {
                configured = true,
                clientId,
                accessMode = accessMode == McpAccessMode.ReadWrite
                    ? "readWrite"
                    : "readOnly"
            },
            session
        });
    }

    public static string Page(JsonElement page)
    {
        return Serialize(new { ok = true, page });
    }

    public static string Document(JsonElement document)
    {
        return Serialize(new { ok = true, document });
    }

    public static string Error(string code, string message)
    {
        return Serialize(new
        {
            ok = false,
            error = new { code, message }
        });
    }

    public static JsonElement SerializeResult(object result)
    {
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }
}
