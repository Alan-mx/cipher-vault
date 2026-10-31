using CipherVault.Core.Documents;

namespace CipherVault.Server.Api;

public static class ServerRequestContext
{
    public static VaultActorContext CreateUserActor(this HttpContext context) =>
        new(VaultActorKind.UserUi, context.GetId());

    public static void SetRevisionETag(this HttpResponse response, int revision)
    {
        response.Headers.ETag = new RevisionETag(revision).ToHeaderValue();
    }
}
