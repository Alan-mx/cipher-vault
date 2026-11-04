using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Server.Api.Contracts;
using CipherVault.Server.Api;

namespace CipherVault.Server.Tests;

internal static class ServerHttpTestExtensions
{
    private static readonly JsonSerializerOptions JsonOptions =
        ApiJson.CreateOptions();

    internal static async Task<HttpResponseMessage> UnlockAsync(
        this HttpClient client)
    {
        return await client.PostAsJsonAsync(
            "/api/vault/unlock",
            new VaultUnlockApiRequest(ServerTestApplication.Password));
    }

    internal static async Task<string> GetAntiforgeryTokenAsync(
        this HttpClient client)
    {
        using var response = await client.GetAsync("/api/vault/antiforgery");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Antiforgery token was empty.");
    }

    internal static Task<HttpResponseMessage> PostWithAntiforgeryAsync(
        this HttpClient client,
        string path,
        string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    internal static Task<HttpResponseMessage> PostJsonWithAntiforgeryAsync<T>(
        this HttpClient client,
        string path,
        string token,
        T value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(value, options: JsonOptions)
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    internal static Task<HttpResponseMessage> SendWithRevisionAsync<T>(
        this HttpClient client,
        HttpMethod method,
        string path,
        string token,
        int revision,
        T? value = default)
    {
        var request = new HttpRequestMessage(method, path);
        if (value is not null)
        {
            request.Content = JsonContent.Create(value, options: JsonOptions);
        }
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        return client.SendAsync(request);
    }

    internal static async Task SetMcpWriteModeAsync(
        this HttpClient client,
        string mode)
    {
        var token = await client.GetAntiforgeryTokenAsync();
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/vault/session/mcp-write-mode")
        {
            Content = JsonContent.Create(new { mode })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add(
            "Idempotency-Key",
            Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
