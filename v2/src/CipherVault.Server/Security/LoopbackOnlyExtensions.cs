namespace CipherVault.Server.Security;

public static class LoopbackOnlyExtensions
{
    public static RouteHandlerBuilder RequireLoopbackRemote(
        this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<LoopbackOnlyFilter>();
}
