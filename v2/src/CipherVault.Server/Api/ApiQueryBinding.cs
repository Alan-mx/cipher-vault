using System.Globalization;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api;

public static class ApiQueryBinding
{
    public static Guid ParseGuid(string value, string fieldName) =>
        Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw InvalidQuery($"{fieldName} must be a non-empty GUID.");

    public static DateTimeOffset? ParseTimestamp(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw InvalidQuery($"{fieldName} must be an ISO 8601 timestamp.");
        }

        return parsed.ToUniversalTime();
    }

    public static bool? ParseBoolean(string? value, string fieldName) =>
        value switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => throw InvalidQuery($"{fieldName} must be true or false.")
        };

    public static VaultActorKind? ParseActor(string? value) =>
        value switch
        {
            null => null,
            "user_ui" => VaultActorKind.UserUi,
            "mcp" => VaultActorKind.Mcp,
            "system" => VaultActorKind.System,
            _ => throw InvalidQuery(
                "actor must be user_ui, mcp or system.")
        };

    public static ApiRequestException InvalidQuery(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidRequest,
            message);
}
