using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Documents;

namespace CipherVault.Core.Semantic;

public enum SemanticProfileStatus
{
    Disabled,
    Building,
    Ready,
    Degraded,
    Failed,
    Invalid
}

public enum SemanticIndexState
{
    NotConfigured,
    Disabled,
    Building,
    Ready,
    Degraded,
    Failed
}

public enum SemanticIndexJobType
{
    Index,
    Delete,
    Purge
}

public enum SemanticIndexJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Superseded
}

public enum SearchMode
{
    Keyword,
    Semantic,
    Hybrid
}

public sealed record SemanticProfile(
    string ProfileId,
    EmbeddingProviderDescriptor Provider,
    int ProfileVersion,
    int ChunkerVersion,
    bool IsActive,
    SemanticProfileStatus Status,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ReadyAt);

public sealed record SemanticRemoteConsent(
    string ProfileId,
    int ConsentVersion,
    string EndpointFingerprint,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset UpdatedAt);

public sealed record SemanticIndexJob(
    Guid DocumentId,
    string ProfileId,
    SemanticIndexJobType Type,
    SemanticIndexJobStatus Status,
    int RequestedRevision,
    string? DocumentContentHash,
    int AttemptCount,
    DateTimeOffset AvailableAt,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record SemanticChunkRecord(
    Guid DocumentId,
    string ProfileId,
    int SourceRevision,
    int ChunkOrdinal,
    IReadOnlyList<string> HeadingPath,
    int StartOffset,
    int EndOffset,
    string ContentHash,
    int Dimension,
    byte[] VectorBlob,
    DateTimeOffset CreatedAt);

public sealed record SemanticDocumentSnapshot(
    Guid DocumentId,
    int Revision,
    string Title,
    VaultDocumentFormat Format,
    string Content,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> ContextFields,
    string ContentHash);

public sealed record SemanticSearchHit(
    Guid DocumentId,
    string Title,
    string VirtualPath,
    int Revision,
    int SourceRevision,
    int ChunkOrdinal,
    IReadOnlyList<string> HeadingPath,
    int StartOffset,
    int EndOffset,
    string Excerpt,
    double CosineScore,
    DateTimeOffset UpdatedAt);

public sealed record SemanticSearchResult(
    IReadOnlyList<SemanticSearchHit> Hits,
    int ScannedDocuments,
    int MatchedDocuments,
    bool Truncated);

public sealed record SemanticIndexStatus(
    SemanticIndexState State,
    SemanticProfile? Profile,
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
    DateTimeOffset? LastUpdatedAt);

public static class SemanticProfileIdentity
{
    public const int ChunkerVersion = 1;

    public static string CreateProfileId(
        EmbeddingProviderDescriptor descriptor,
        int profileVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (profileVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profileVersion));
        }
        var canonical = string.Join(
            '\n',
            "cipher-vault-embedding-profile-v1",
            descriptor.ProviderId,
            descriptor.ModelId,
            descriptor.Dimension,
            descriptor.MaxInputTokens,
            descriptor.EndpointFingerprint,
            profileVersion,
            ChunkerVersion);
        return Sha256Hex(canonical);
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

public static class SemanticValueMapping
{
    public static string ToDatabaseValue(this SemanticProfileStatus value) =>
        value switch
        {
            SemanticProfileStatus.Disabled => "disabled",
            SemanticProfileStatus.Building => "building",
            SemanticProfileStatus.Ready => "ready",
            SemanticProfileStatus.Degraded => "degraded",
            SemanticProfileStatus.Failed => "failed",
            SemanticProfileStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this SemanticIndexJobType value) =>
        value switch
        {
            SemanticIndexJobType.Index => "index",
            SemanticIndexJobType.Delete => "delete",
            SemanticIndexJobType.Purge => "purge",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static string ToDatabaseValue(this SemanticIndexJobStatus value) =>
        value switch
        {
            SemanticIndexJobStatus.Pending => "pending",
            SemanticIndexJobStatus.Running => "running",
            SemanticIndexJobStatus.Completed => "completed",
            SemanticIndexJobStatus.Failed => "failed",
            SemanticIndexJobStatus.Cancelled => "cancelled",
            SemanticIndexJobStatus.Superseded => "superseded",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SemanticProfileStatus ParseProfileStatus(string value) =>
        value switch
        {
            "disabled" => SemanticProfileStatus.Disabled,
            "building" => SemanticProfileStatus.Building,
            "ready" => SemanticProfileStatus.Ready,
            "degraded" => SemanticProfileStatus.Degraded,
            "failed" => SemanticProfileStatus.Failed,
            "invalid" => SemanticProfileStatus.Invalid,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SemanticIndexJobType ParseJobType(string value) =>
        value switch
        {
            "index" => SemanticIndexJobType.Index,
            "delete" => SemanticIndexJobType.Delete,
            "purge" => SemanticIndexJobType.Purge,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };

    public static SemanticIndexJobStatus ParseJobStatus(string value) =>
        value switch
        {
            "pending" => SemanticIndexJobStatus.Pending,
            "running" => SemanticIndexJobStatus.Running,
            "completed" => SemanticIndexJobStatus.Completed,
            "failed" => SemanticIndexJobStatus.Failed,
            "cancelled" => SemanticIndexJobStatus.Cancelled,
            "superseded" => SemanticIndexJobStatus.Superseded,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
}
