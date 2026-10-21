using CipherVault.Core.Documents;

namespace CipherVault.Host.Http;

internal static class HostAdminContext
{
    public const string ClientKindHeader = "X-CipherVault-Client-Kind";
    public const string ActorItem = "CipherVault.AdminActor";

    public static VaultActorKind GetActor(HttpContext context)
    {
        return context.Items.TryGetValue(ActorItem, out var value) &&
            value is VaultActorKind actor
            ? actor
            : VaultActorKind.UserUi;
    }
}
