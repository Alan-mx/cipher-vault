using CipherVault.Core.Documents;
using CipherVault.Core.Knowledge;

namespace CipherVault.Server.Api.Contracts;

public static class KnowledgeMaturityQueueApiBinding
{
    public static KnowledgeMaturityQueueBucket ParseBucket(
        HttpRequest request)
    {
        KnowledgeViewApiBinding.EnsureOnlyQueryKeys(
            request,
            "bucket",
            "pageSize",
            "cursor");
        var value = KnowledgeViewApiBinding.SingleQueryValue(
            request,
            "bucket");
        return value switch
        {
            "pending_organization" =>
                KnowledgeMaturityQueueBucket.PendingOrganization,
            "pending_verification" =>
                KnowledgeMaturityQueueBucket.PendingVerification,
            "completed" => KnowledgeMaturityQueueBucket.Completed,
            "other" => KnowledgeMaturityQueueBucket.Other,
            _ => throw KnowledgeViewApiBinding.InvalidInput(
                "bucket is invalid.")
        };
    }

    public static CursorPageRequest ParsePage(HttpRequest request) =>
        new(
            KnowledgeViewApiBinding.ParseOptionalPageSize(request),
            KnowledgeViewApiBinding.ParseOptionalCursor(request));
}
