using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core.Documents;
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
        options.Converters.Add(new DocumentFormatJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower));
    }

    private sealed class DocumentFormatJsonConverter :
        JsonConverter<VaultDocumentFormat>
    {
        public override VaultDocumentFormat Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value switch
            {
                "md" => VaultDocumentFormat.Markdown,
                "txt" => VaultDocumentFormat.Text,
                _ => throw new JsonException("Document format must be md or txt.")
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            VaultDocumentFormat value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                VaultDocumentFormat.Markdown => "md",
                VaultDocumentFormat.Text => "txt",
                _ => throw new JsonException("Document format is invalid.")
            });
        }
    }
}
