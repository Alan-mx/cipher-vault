using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CipherVault.Application.Vault;
using CipherVault.Core.Errors;
using CipherVault.Server.Mcp;
using CipherVault.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Tests;

public sealed class McpSecurityAndLifecycleTests
{
    [Fact]
    public async Task McpBearerGateRejectsBeforeReadingBody()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"cipher-vault-mcp-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var tokenPath = Path.Combine(root, "mcp-token.json");
            var store = await McpTokenStore.LoadAsync(tokenPath);
            _ = await store.RotateAsync();
            var context = new DefaultHttpContext();
            context.Request.Path = "/mcp";
            context.Request.Method = HttpMethods.Post;
            context.Request.Headers.Authorization = "Bearer invalid";
            var body = new TrackingReadStream();
            context.Request.Body = body;
            context.Response.Body = new MemoryStream();
            var middleware = new McpBearerGateMiddleware(
                _ => throw new InvalidOperationException(
                    "MCP downstream must not run."),
                store);

            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.False(body.ReadAttempted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TokenQueryAndContentAreAbsentFromMcpLogs()
    {
        using var logs = new CapturingLoggerProvider();
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            configureLogging: logging => logging.ClearProviders().AddProvider(logs));
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await RotateTokenAsync(server.Client);

        const string contentMarker = "mcp-log-content-marker";
        _ = await CallToolAsync(server.Client, token, "create_note", new
        {
            title = "Logs",
            content = contentMarker,
            format = "txt"
        });
        _ = await CallToolAsync(server.Client, token, "search_notes", new
        {
            query = "mcp-log-query-marker"
        });

        var text = string.Join('\n', logs.Messages);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerTestApplication.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(contentMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("mcp-log-query-marker", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusinessToolRefreshesActivityButVaultStatusDoesNot()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            idleTimeout: TimeSpan.FromMinutes(10));
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await RotateTokenAsync(server.Client);
        server.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        _ = await CallToolAsync(server.Client, token, "list_notes", new { });
        var afterBusiness = await ReadIdleExpiryAsync(server.Client);
        Assert.Equal(
            server.TimeProvider.GetUtcNow().AddMinutes(10),
            afterBusiness);

        server.TimeProvider.Advance(TimeSpan.FromMinutes(4));
        _ = await CallToolAsync(server.Client, token, "vault_status", new { });
        var afterStatus = await ReadIdleExpiryAsync(server.Client);
        Assert.Equal(afterBusiness, afterStatus);
    }

    [Fact]
    public async Task TokenAuthenticatesStatusWhileLockedButCannotUnlockOrRunBusinessTools()
    {
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await RotateTokenAsync(server.Client);
        _ = await server.Manager.LockAsync();

        var status = await CallToolAsync(server.Client, token, "vault_status", new { });
        Assert.Equal("locked", status.GetProperty("state").GetString());

        using var response = await PostToolAsync(
            server.Client,
            token,
            "list_notes",
            new { });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("vault_locked", body, StringComparison.Ordinal);
        Assert.Contains("\"isError\":true", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockFailedReturnsStatusButBusinessToolsFailClosed()
    {
        var timing = new VaultSessionTimingOptions(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        await using var server = await ServerTestApplication.CreateAsync(
            initializeVault: true,
            timingOptions: timing);
        using var unlock = await server.Client.UnlockAsync();
        Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
        var token = await RotateTokenAsync(server.Client);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(async () =>
            await server.Manager.ExecuteAsync<int>(async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return 1;
            }));
        await started.Task;

        var failed = await server.Manager.LockAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VaultSessionState.LockFailed, failed.State);

        var status = await CallToolAsync(
            server.Client,
            token,
            "vault_status",
            new { });
        Assert.Equal("lock_failed", status.GetProperty("state").GetString());

        using var response = await PostToolAsync(
            server.Client,
            token,
            "list_notes",
            new { });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("vault_lock_failed", body, StringComparison.Ordinal);
        Assert.Contains("\"isError\":true", body, StringComparison.Ordinal);

        release.SetResult();
        var exception = await Assert.ThrowsAsync<VaultException>(
            async () => await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VaultErrorCode.VaultLockFailed, exception.Code);
        _ = await Task.WhenAny(
            operation,
            Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(operation.IsCompleted);
    }

    private static async Task<string> RotateTokenAsync(HttpClient client)
    {
        var csrf = await client.GetAntiforgeryTokenAsync();
        using var response = await client.PostWithAntiforgeryAsync(
            "/api/mcp/token/rotate",
            csrf);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<DateTimeOffset> ReadIdleExpiryAsync(
        HttpClient client)
    {
        using var response = await client.GetAsync("/api/vault/status");
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("idleExpiresAtUtc")
            .GetDateTimeOffset();
    }

    private static async Task<JsonElement> CallToolAsync(
        HttpClient client,
        string token,
        string name,
        object arguments)
    {
        using var response = await PostToolAsync(client, token, name, arguments);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(ExtractJson(body));
        var result = json.RootElement.GetProperty("result");
        Assert.False(
            result.TryGetProperty("isError", out var isError)
                && isError.ValueKind == JsonValueKind.True);
        var text = result.GetProperty("content")
            .EnumerateArray()
            .First()
            .GetProperty("text")
            .GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostToolAsync(
        HttpClient client,
        string token,
        string name,
        object arguments)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = Random.Shared.Next(1, int.MaxValue),
                    method = "tools/call",
                    @params = new
                    {
                        name,
                        arguments
                    }
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client.SendAsync(request);
    }

    private static string ExtractJson(string body)
    {
        var first = body.IndexOf('{');
        var last = body.LastIndexOf('}');
        Assert.True(
            first >= 0 && last > first,
            $"MCP response did not contain JSON: {body}");
        return body[first..(last + 1)];
    }

    private sealed class TrackingReadStream : Stream
    {
        internal bool ReadAttempted { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 100;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempted = true;
            throw new IOException("Body must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new IOException("Body must not be read.");
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
