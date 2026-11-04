using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Core.Drafts;

public static class IdempotencyRequestHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public static string Compute(
        IdempotencyOperation operation,
        VaultActorKind actorKind,
        Guid? sessionId,
        JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "The idempotent request must be a JSON object.");
        }

        var actor = actorKind switch
        {
            VaultActorKind.UserUi => "user_ui",
            VaultActorKind.Mcp => "mcp",
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Idempotent operations require a UserUi or Mcp actor.")
        };
        var envelope = JsonSerializer.SerializeToElement(
            new
            {
                operation = operation.ToDatabaseValue(),
                actor,
                sessionId = sessionId?.ToString("D"),
                request
            },
            JsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, envelope);
        }

        return Convert.ToHexString(
            SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                    .EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "The idempotent request contains an unsupported JSON value.");
        }
    }
}
