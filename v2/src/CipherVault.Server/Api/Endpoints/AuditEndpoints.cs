using CipherVault.Application.Audit;
using CipherVault.Core.Audit;
using CipherVault.Core.Documents;

namespace CipherVault.Server.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/audit",
                async (
                    string? actor,
                    string? action,
                    string? documentId,
                    string? occurredAfter,
                    string? occurredBefore,
                    int? pageSize,
                    string? cursor,
                    AuditQueryService audit,
                    CancellationToken cancellationToken) =>
                {
                    var request = new AuditQuery(
                        ApiQueryBinding.ParseActor(actor),
                        action,
                        string.IsNullOrWhiteSpace(documentId)
                            ? null
                            : ApiQueryBinding.ParseGuid(
                                documentId,
                                nameof(documentId)),
                        ApiQueryBinding.ParseTimestamp(
                            occurredAfter,
                            nameof(occurredAfter)),
                        ApiQueryBinding.ParseTimestamp(
                            occurredBefore,
                            nameof(occurredBefore)),
                        new CursorPageRequest(pageSize, cursor));
                    return Results.Ok(await audit.ListAsync(
                        request,
                        cancellationToken));
                })
            .RequireAuthorization()
            .WithName("ListAuditEvents");
        return endpoints;
    }
}
