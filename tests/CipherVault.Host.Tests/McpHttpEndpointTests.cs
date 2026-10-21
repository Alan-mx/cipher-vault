using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Tests;

public sealed class McpHttpEndpointTests
{
    private const string ProtocolVersion = "2026-07-28";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task McpRejectsMissingAgentToken()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateMcpRequest(
            InitializePayload(1),
            agentToken: null,
            method: "initialize");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<HostApiErrorResponse>(
            JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("invalid_token", payload.Error.Code);
    }

    [Fact]
    public async Task ReadOnlyAgentCanSearchButCannotWrite()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await fixture.CreateNoteAsync(
            "MCP search note",
            "read only search marker");
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "readonly-agent",
            "readOnly",
            "staticToken");
        var session = new McpTestSession(fixture.Client, token);
        await session.InitializeAsync();

        using var search = await session.CallToolAsync(
            2,
            "search_notes",
            new { query = "search marker" });
        var searchPayload = GetToolPayload(search);
        Assert.True(searchPayload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(
            searchPayload.RootElement
                .GetProperty("page")
                .GetProperty("hits")
                .EnumerateArray());

        using var create = await session.CallToolAsync(
            3,
            "create_note",
            new
            {
                title = "Blocked agent note",
                content = "must not be written"
            });
        var createPayload = GetToolPayload(create);
        Assert.False(createPayload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "read_only",
            createPayload.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task ReadWriteAgentCanCreateAndReadDocument()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "readwrite-agent",
            "readWrite",
            "accessGrant");
        var grantSecret = await RequestGrantAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read",
            "write");
        var session = new McpTestSession(fixture.Client, token, grantSecret);
        await session.InitializeAsync();

        using var create = await session.CallToolAsync(
            4,
            "create_note",
            new
            {
                title = "Agent created note",
                content = "created through streamable http",
                folder = "agents"
            });
        var created = GetToolPayload(create);
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        var documentId = created.RootElement
            .GetProperty("document")
            .GetProperty("documentId")
            .GetGuid();

        using var read = await session.CallToolAsync(
            5,
            "read_note",
            new { documentId = documentId.ToString("D") });
        var readPayload = GetToolPayload(read);
        Assert.Equal(
            "created through streamable http",
            readPayload.RootElement
                .GetProperty("document")
                .GetProperty("content")
                .GetString());
    }

    [Fact]
    public async Task AdminTokenIsRejectedOnMcpEndpoint()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        using var request = CreateMcpRequest(
            InitializePayload(1),
            HostTestFixture.AdminToken,
            method: "initialize");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WriteAuditRecordsAuthenticatedClientAndRequestId()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "audited-agent",
            "readWrite",
            "accessGrant");
        var grantSecret = await RequestGrantAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read",
            "write");
        var session = new McpTestSession(fixture.Client, token, grantSecret);
        await session.InitializeAsync();

        using var create = await session.CallToolAsync(
            6,
            "create_note",
            new
            {
                title = "Audited agent note",
                content = "audited content"
            },
            requestId: "mcp-audit-request");
        var created = GetToolPayload(create);
        var documentId = created.RootElement
            .GetProperty("document")
            .GetProperty("documentId")
            .GetGuid();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/v1/audit?limit=50");
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<VaultAuditPage>>(JsonOptions);
        Assert.NotNull(payload);

        var audit = Assert.Single(
            payload.Data.Items,
            item => item.Action == "create" &&
                item.DocumentId == documentId);
        Assert.Equal("audited-agent", audit.ClientName);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(
            "mcp-audit-request",
            details.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task LockedVaultReturnsAuthRequiredThroughMcp()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        var token = await CreateClientAsync(
            fixture.Client,
            "locked-agent",
            "readOnly");
        var session = new McpTestSession(fixture.Client, token);

        using var response = await session.CallToolAsync(
            2,
            "list_catalog",
            new { });
        var payload = GetToolPayload(response);
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "auth_required",
            payload.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task AccessGrantAgentMustRequestGrantBeforeReading()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "grant-required-agent",
            "readOnly",
            "accessGrant");
        var session = new McpTestSession(fixture.Client, token);

        using var response = await session.CallToolAsync(
            7,
            "list_catalog",
            new { });
        var payload = GetToolPayload(response);

        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_grant_required",
            payload.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public async Task DeleteRequiresSeparateDeleteScope()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "delete-scope-agent",
            "readWrite",
            "accessGrant");
        var grantSecret = await RequestGrantAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read",
            "write");
        var session = new McpTestSession(fixture.Client, token, grantSecret);
        await session.InitializeAsync();

        using var create = await session.CallToolAsync(
            8,
            "create_note",
            new
            {
                title = "Delete scope note",
                content = "delete scope"
            });
        var created = GetToolPayload(create);
        var documentId = created.RootElement
            .GetProperty("document")
            .GetProperty("documentId")
            .GetGuid();

        using var delete = await session.CallToolAsync(
            9,
            "delete_note",
            new
            {
                documentId = documentId.ToString("D"),
                expectedRevision = 1
            });
        var deleted = GetToolPayload(delete);

        Assert.False(deleted.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_denied",
            deleted.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());

        var deleteGrant = await RequestGrantAsync(
            fixture.Client,
            token,
            Guid.NewGuid(),
            "read",
            "write",
            "delete");
        var deleteSession = new McpTestSession(
            fixture.Client,
            token,
            deleteGrant);
        await deleteSession.InitializeAsync();
        using var allowedDelete = await deleteSession.CallToolAsync(
            11,
            "delete_note",
            new
            {
                documentId = documentId.ToString("D"),
                expectedRevision = 1
            });
        var allowed = GetToolPayload(allowedDelete);
        Assert.True(allowed.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task GrantCannotCrossVaultSession()
    {
        await using var fixture = await HostTestFixture.CreateAsync();
        await UnlockHostAsync(fixture.Client);
        var token = await CreateClientAsync(
            fixture.Client,
            "session-agent",
            "readOnly",
            "accessGrant");
        var clientInstanceId = Guid.NewGuid();
        var grantSecret = await RequestGrantAsync(
            fixture.Client,
            token,
            clientInstanceId,
            "read");
        var session = new McpTestSession(fixture.Client, token, grantSecret);
        await session.InitializeAsync();

        await LockHostAsync(fixture.Client);
        await UnlockHostAsync(fixture.Client);
        using var response = await session.CallToolAsync(
            10,
            "list_catalog",
            new { });
        var payload = GetToolPayload(response);

        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "access_grant_revoked",
            payload.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    private static async Task UnlockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/unlock")
        {
            Content = JsonContent.Create(new HostUnlockRequest("password"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task LockHostAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/session/lock")
        {
            Content = JsonContent.Create(new HostLockRequest("test-lock"))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateClientAsync(
        HttpClient client,
        string clientId,
        string accessMode,
        string authorizationMode = "accessGrant")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/v1/clients")
        {
            Content = JsonContent.Create(
                new HostCreateClientRequest(
                    clientId,
                    accessMode,
                    authorizationMode))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            HostTestFixture.AdminToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostCreateClientData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.Token;
    }

    private static async Task<string> RequestGrantAsync(
        HttpClient client,
        string agentToken,
        Guid clientInstanceId,
        params string[] scopes)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/mcp/access-grants")
        {
            Content = JsonContent.Create(
                new HostRequestAccessGrantRequest(
                    clientInstanceId,
                    scopes))
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            agentToken);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<HostIssuedAccessGrantData>>(JsonOptions);
        Assert.NotNull(payload);
        return payload.Data.Secret;
    }

    private static object InitializePayload(int id)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new Dictionary<string, object?>
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new { },
                ["clientInfo"] = new
                {
                    name = "CipherVault.Host.Tests",
                    version = "1.0.0"
                },
                ["_meta"] = ProtocolMeta()
            }
        };
    }

    private static Dictionary<string, object> ProtocolMeta()
    {
        return new Dictionary<string, object>
        {
            ["io.modelcontextprotocol/protocolVersion"] = ProtocolVersion,
            ["io.modelcontextprotocol/clientCapabilities"] = new { }
        };
    }

    private static HttpRequestMessage CreateMcpRequest(
        object payload,
        string? agentToken,
        string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Host = "127.0.0.1:43127";
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation(
            "MCP-Protocol-Version",
            ProtocolVersion);
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);
        if (agentToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                agentToken);
        }
        return request;
    }

    private static async Task<JsonDocument> ReadJsonRpcAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var json = content;
        if (string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            json = content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].Trim())
                .LastOrDefault();
        }
        Assert.False(string.IsNullOrWhiteSpace(json), content);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument GetToolPayload(JsonDocument response)
    {
        var text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        return JsonDocument.Parse(text);
    }

    private sealed class McpTestSession
    {
        private readonly HttpClient _client;
        private readonly string _token;
        private readonly string? _grantSecret;

        public McpTestSession(
            HttpClient client,
            string token,
            string? grantSecret = null)
        {
            _client = client;
            _token = token;
            _grantSecret = grantSecret;
        }

        public async Task InitializeAsync()
        {
            using var request = CreateMcpRequest(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/list",
                    @params = new Dictionary<string, object?>
                    {
                        ["_meta"] = ProtocolMeta()
                    }
                },
                _token,
                method: "tools/list");
            using var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"MCP tools/list returned {(int)response.StatusCode}: {body}");
            }
            using var document = await ReadJsonRpcAsync(response);
            var toolNames = document.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("vault_status", toolNames);
            Assert.Contains("search_notes", toolNames);
            Assert.Contains("create_note", toolNames);
        }

        public async Task<JsonDocument> CallToolAsync(
            int id,
            string name,
            object arguments,
            string? requestId = null)
        {
            using var request = CreateMcpRequest(
                new
                {
                    jsonrpc = "2.0",
                    id,
                    method = "tools/call",
                    @params = new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["arguments"] = arguments,
                        ["_meta"] = ProtocolMeta()
                    }
                },
                _token,
                method: "tools/call");
            request.Headers.TryAddWithoutValidation("Mcp-Name", name);
            if (_grantSecret is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    AccessGrantHeaders.Grant,
                    _grantSecret);
            }
            if (requestId is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Request-Id",
                    requestId);
            }
            using var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"MCP tools/call '{name}' returned {(int)response.StatusCode}: {body}");
            }
            return await ReadJsonRpcAsync(response);
        }
    }
}
