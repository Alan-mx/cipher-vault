using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;

namespace CipherVault.Server.Api.Contracts;

public sealed record McpWriteModeApiRequest(string Mode);

public sealed record McpWriteModeApiResponse(
    string Mode,
    Guid? SessionId,
    long? SessionGeneration,
    DateTimeOffset? UpdatedAt,
    bool Effective);

public static class McpWriteModeApiMapper
{
    public static McpWriteModeChangeRequest ToApplicationRequest(
        this McpWriteModeApiRequest request) =>
        new(ParseMode(request.Mode));

    public static McpWriteModeApiResponse ToApiResponse(
        this McpWriteModeStatus status) =>
        new(
            ToApiValue(status.Mode),
            status.SessionId,
            status.SessionGeneration,
            status.UpdatedAt,
            status.Effective);

    private static McpWriteMode ParseMode(string value) =>
        value switch
        {
            "draft" => McpWriteMode.Draft,
            "direct" => McpWriteMode.Direct,
            _ => throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.McpWriteModeInvalid,
                "mode must be draft or direct.")
        };

    private static string ToApiValue(McpWriteMode mode) =>
        mode switch
        {
            McpWriteMode.Draft => "draft",
            McpWriteMode.Direct => "direct",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
}
