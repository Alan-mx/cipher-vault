using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

BridgeSettings settings;
try
{
    settings = BridgeSettings.Load(args);
}
catch (VaultException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    Environment.ExitCode = 1;
    return;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}

var bridge = new HostMcpBridgeClient(settings);
var tools = new BridgeVaultTools(bridge, settings.ReadOnly);
var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<BridgeVaultTools>(tools);

await builder.Build().RunAsync();

public sealed record BridgeSettings(
    Uri McpEndpoint,
    string AgentToken,
    bool ReadOnly)
{
    public static BridgeSettings Load(string[] arguments)
    {
        var configuration = CipherVaultHostConfiguration.Load(
            new CipherVaultHostConfigOverrides
            {
                VaultPath = GetOption(arguments, "--vault"),
                IdleMinutes = ParseOptionalInteger(
                    GetOption(arguments, "--idle-minutes"),
                    "--idle-minutes"),
                HardHours = ParseOptionalInteger(
                    GetOption(arguments, "--hard-hours"),
                    "--hard-hours")
            },
            GetOption(arguments, "--config"));
        var tokenEnvironmentName = GetOption(arguments, "--token-env")
            ?? "CIPHER_VAULT_MCP_TOKEN";
        var token = Environment.GetEnvironmentVariable(tokenEnvironmentName);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Set {tokenEnvironmentName} to the Agent Bearer Token.");
        }

        if (arguments.Any(argument =>
                string.Equals(
                    argument,
                    "--mcp-url",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "--mcp-url is not supported. The bridge only connects to the configured local Host.");
        }

        var endpoint = new Uri(
            $"http://{configuration.Endpoint.BindAddress}:" +
            $"{configuration.Endpoint.Port}{configuration.Endpoint.McpPath}");
        return new BridgeSettings(
            endpoint,
            token.Trim(),
            ParseReadOnly(arguments));
    }

    private static bool ParseReadOnly(string[] arguments)
    {
        var accessMode = GetOption(arguments, "--access-mode");
        var readOnly = arguments.Any(argument =>
            string.Equals(argument, "--read-only", StringComparison.OrdinalIgnoreCase));
        if (readOnly)
        {
            if (accessMode is not null &&
                !string.Equals(accessMode, "readOnly", StringComparison.OrdinalIgnoreCase))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    "--read-only conflicts with --access-mode readWrite.");
            }
            return true;
        }
        return accessMode?.ToLowerInvariant() switch
        {
            null or "" or "readonly" => true,
            "readwrite" => false,
            _ => throw new VaultException(
                VaultErrorCode.InvalidInput,
                "--access-mode must be readOnly or readWrite.")
        };
    }

    private static string? GetOption(string[] arguments, string option)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    option,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new VaultException(
                    VaultErrorCode.InvalidInput,
                    $"Option {option} requires a value.");
            }
            return arguments[index + 1];
        }
        if (arguments.Any(argument =>
                string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"Option {option} requires a value.");
        }
        return null;
    }

    private static int? ParseOptionalInteger(
        string? value,
        string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, out var parsed))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                $"{option} must be an integer.");
        }
        return parsed;
    }
}

public sealed class HostMcpBridgeClient
{
    private const string ProtocolVersion = "2026-07-28";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly BridgeSettings _settings;
    private readonly HttpClient _http;

    public HostMcpBridgeClient(BridgeSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<string> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.McpEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        jsonrpc = "2.0",
                        id = requestId,
                        method = "tools/call",
                        @params = new Dictionary<string, object?>
                        {
                            ["name"] = name,
                            ["arguments"] = arguments,
                            ["_meta"] = new Dictionary<string, object>
                            {
                                ["io.modelcontextprotocol/protocolVersion"] =
                                    ProtocolVersion,
                                ["io.modelcontextprotocol/clientCapabilities"] =
                                    new { }
                            }
                        }
                    },
                    JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _settings.AgentToken);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation(
            "MCP-Protocol-Version",
            ProtocolVersion);
        request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
        request.Headers.TryAddWithoutValidation("Mcp-Name", name);
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

        try
        {
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Error("invalid_token", "Host rejected the Agent Token.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Error(
                    "host_error",
                    $"Host returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            var json = ExtractJson(body, response.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var rpcError))
            {
                return Error(
                    "mcp_error",
                    rpcError.TryGetProperty("message", out var message)
                        ? message.GetString() ?? "Host MCP request failed."
                        : "Host MCP request failed.");
            }

            var result = document.RootElement.GetProperty("result");
            if (result.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                var parts = content.EnumerateArray()
                    .Where(item =>
                        item.TryGetProperty("type", out var type) &&
                        string.Equals(
                            type.GetString(),
                            "text",
                            StringComparison.Ordinal) &&
                        item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(text => text is not null)
                    .ToList();
                if (parts.Count > 0)
                {
                    return string.Join(Environment.NewLine, parts);
                }
            }
            return result.GetRawText();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                TaskCanceledException or
                JsonException or
                InvalidOperationException or
                KeyNotFoundException)
        {
            return Error(
                "host_unavailable",
                "CipherVault Host is unavailable. Start CipherVault.Host.exe " +
                "or open the desktop app, then try again.");
        }
    }

    private static string ExtractJson(string body, string? mediaType)
    {
        if (!string.Equals(
                mediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }
        return body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].Trim())
            .LastOrDefault()
            ?? throw new JsonException("MCP event stream did not contain a data event.");
    }

    private static string Error(string code, string message)
    {
        return JsonSerializer.Serialize(
            new
            {
                ok = false,
                error = new
                {
                    code,
                    message
                }
            },
            JsonOptions);
    }
}

[McpServerToolType]
public sealed class BridgeVaultTools
{
    private readonly HostMcpBridgeClient _bridge;
    private readonly bool _readOnly;

    public BridgeVaultTools(HostMcpBridgeClient bridge, bool readOnly)
    {
        _bridge = bridge;
        _readOnly = readOnly;
    }

    [McpServerTool(Name = "vault_status")]
    [Description("Return the Vault lock state and session deadlines. This tool never returns document content.")]
    public Task<string> VaultStatus(CancellationToken cancellationToken)
    {
        return CallAsync("vault_status", Empty(), cancellationToken);
    }

    [McpServerTool(Name = "list_catalog")]
    [Description("List active document titles, virtual folders, headings and tags without returning the real filesystem path.")]
    public Task<string> ListCatalog(
        string? folder = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "list_catalog",
            Arguments(
                ("folder", folder),
                ("cursor", cursor),
                ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "search_notes")]
    [Description("Search active Markdown/TXT documents with FTS5 or a bounded short-query scan and return snippets.")]
    public Task<string> SearchNotes(
        string query,
        string? folder = null,
        string? cursor = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "search_notes",
            Arguments(
                ("query", query),
                ("folder", folder),
                ("cursor", cursor),
                ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "read_note")]
    [Description("Read a bounded character range from one active document and verify its application-level integrity HMAC.")]
    public Task<string> ReadNote(
        string documentId,
        int offset = 0,
        int maxChars = 8_000,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "read_note",
            Arguments(
                ("documentId", documentId),
                ("offset", offset),
                ("maxChars", maxChars)),
            cancellationToken);
    }

    [McpServerTool(Name = "scan_notes")]
    [Description("Page through active document summaries without returning document content.")]
    public Task<string> ScanNotes(
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "scan_notes",
            Arguments(("cursor", cursor), ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "create_note")]
    [Description("Create an encrypted Markdown or TXT document in the Vault.")]
    public Task<string> CreateNote(
        string title,
        string content,
        string? folder = null,
        string format = "md",
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        return CallWriteAsync(
            "create_note",
            Arguments(
                ("title", title),
                ("content", content),
                ("folder", folder),
                ("format", format),
                ("tags", tags)),
            cancellationToken);
    }

    [McpServerTool(Name = "append_note")]
    [Description("Append content to an existing document using optimistic revision concurrency.")]
    public Task<string> AppendNote(
        string documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default)
    {
        return CallWriteAsync(
            "append_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision),
                ("content", content)),
            cancellationToken);
    }

    [McpServerTool(Name = "update_note")]
    [Description("Update title, virtual folder, content or tags using optimistic revision concurrency.")]
    public Task<string> UpdateNote(
        string documentId,
        int expectedRevision,
        string? title = null,
        string? folder = null,
        string? content = null,
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        return CallWriteAsync(
            "update_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision),
                ("title", title),
                ("folder", folder),
                ("content", content),
                ("tags", tags)),
            cancellationToken);
    }

    [McpServerTool(Name = "delete_note")]
    [Description("Move a document to the encrypted recycle bin using optimistic revision concurrency.")]
    public Task<string> DeleteNote(
        string documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return CallWriteAsync(
            "delete_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision)),
            cancellationToken);
    }

    private Task<string> CallWriteAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        return _readOnly
            ? Task.FromResult(ErrorMessage(
                "read_only",
                "This CipherVault MCP bridge is configured for read-only access."))
            : CallAsync(name, arguments, cancellationToken);
    }

    private Task<string> CallAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        return _bridge.CallToolAsync(name, arguments, cancellationToken);
    }

    private static IReadOnlyDictionary<string, object?> Empty()
    {
        return new Dictionary<string, object?>();
    }

    private static IReadOnlyDictionary<string, object?> Arguments(
        params (string Name, object? Value)[] values)
    {
        return values
            .Where(value => value.Value is not null)
            .ToDictionary(
                value => value.Name,
                value => value.Value,
                StringComparer.Ordinal);
    }

    private static string ErrorMessage(string code, string message)
    {
        return JsonSerializer.Serialize(
            new
            {
                ok = false,
                error = new
                {
                    code,
                    message
                }
            });
    }
}
