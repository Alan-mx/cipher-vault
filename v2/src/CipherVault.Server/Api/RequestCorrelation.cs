using Microsoft.AspNetCore.Http;

namespace CipherVault.Server.Api;

public static class RequestCorrelation
{
    private const string ItemKey = "CipherVault.CorrelationId";

    public static string GetId(this HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var value)
            && value is string id)
        {
            return id;
        }

        var created = Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = created;
        return created;
    }

    public static void SetId(this HttpContext context, string id) =>
        context.Items[ItemKey] = id;
}
