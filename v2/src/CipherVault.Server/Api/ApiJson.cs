using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;

namespace CipherVault.Server.Api;

public static class ApiJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        return options;
    }

    public static void Configure(
        Microsoft.AspNetCore.Http.Json.JsonOptions options)
    {
        Apply(options.SerializerOptions);
    }

    private static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNameCaseInsensitive = false;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.WriteIndented = false;
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower));
    }
}
