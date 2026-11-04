using CipherVault.Application.Semantic;
using CipherVault.Core.Semantic;

namespace CipherVault.Server.Api.Contracts;

public sealed record SemanticConsentApiRequest(
    string ProfileId,
    string EndpointFingerprint);

public sealed record SemanticRebuildApiRequest(string ProfileId);

public sealed record SemanticProfileApiResponse(
    string ProfileId,
    string ProviderId,
    string ModelId,
    int Dimension,
    int MaxInputTokens,
    string EndpointFingerprint,
    bool IsRemote,
    int ProfileVersion,
    int ChunkerVersion,
    string Status,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadyAt);

public sealed record SemanticIndexStatusApiResponse(
    string State,
    string? EndpointDisplay,
    bool RemoteDeploymentAllowed,
    bool RemoteConsentRequired,
    bool RemoteConsentGranted,
    DateTimeOffset? ConsentGrantedAt,
    DateTimeOffset? ConsentRevokedAt,
    int DocumentCount,
    int ChunkCount,
    int PendingJobCount,
    int FailedJobCount,
    string? LastErrorCode,
    DateTimeOffset? LastUpdatedAt,
    SemanticProfileApiResponse? Profile);

public static class SemanticIndexApiMapper
{
    public static SemanticIndexStatusApiResponse ToApiResponse(
        this SemanticIndexStatus status) =>
        new(
            StateValue(status.State),
            status.EndpointDisplay,
            status.RemoteDeploymentAllowed,
            status.RemoteConsentRequired,
            status.RemoteConsentGranted,
            status.ConsentGrantedAt,
            status.ConsentRevokedAt,
            status.DocumentCount,
            status.ChunkCount,
            status.PendingJobCount,
            status.FailedJobCount,
            status.LastErrorCode,
            status.LastUpdatedAt,
            status.Profile is null
                ? null
                : new SemanticProfileApiResponse(
                    status.Profile.ProfileId,
                    status.Profile.Provider.ProviderId,
                    status.Profile.Provider.ModelId,
                    status.Profile.Provider.Dimension,
                    status.Profile.Provider.MaxInputTokens,
                    status.Profile.Provider.EndpointFingerprint,
                    status.Profile.Provider.IsRemote,
                    status.Profile.ProfileVersion,
                    status.Profile.ChunkerVersion,
                    status.Profile.Status.ToDatabaseValue(),
                    status.Profile.LastErrorCode,
                    status.Profile.CreatedAt,
                    status.Profile.UpdatedAt,
                    status.Profile.ReadyAt));

    public static string ParseProfileId(string? value) =>
        string.IsNullOrEmpty(value) || value.Length != 64
        || value.Any(character => character is not (
            >= '0' and <= '9'
            or >= 'a' and <= 'f'))
            ? throw Invalid("profileId is invalid.")
            : value;

    public static string ParseEndpointFingerprint(string? value) =>
        string.IsNullOrEmpty(value) || value.Length != 64
        || value.Any(character => character is not (
            >= '0' and <= '9'
            or >= 'a' and <= 'f'))
            ? throw Invalid("endpointFingerprint is invalid.")
            : value;

    private static string StateValue(SemanticIndexState state) =>
        state switch
        {
            SemanticIndexState.NotConfigured => "not_configured",
            SemanticIndexState.Disabled => "disabled",
            SemanticIndexState.Building => "building",
            SemanticIndexState.Ready => "ready",
            SemanticIndexState.Degraded => "degraded",
            SemanticIndexState.Failed => "failed",
            _ => "failed"
        };

    private static ApiRequestException Invalid(string message) =>
        new(
            StatusCodes.Status400BadRequest,
            ApiErrorCodes.InvalidInput,
            message);
}
