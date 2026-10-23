using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Client;
using CipherVault.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

StrictBridgeSettings settings;
try
{
    settings = StrictBridgeSettings.Load(args);
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

StrictMcpBridgeClient? bridge = null;
try
{
    bridge = await StrictMcpBridgeClient.ConnectAsync(settings);
    var tools = new BridgeVaultTools(bridge, settings.ReadOnly);
    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<BridgeVaultTools>(tools);

    var server = builder.Build();
    await server.RunAsync();
}
catch (VaultException exception)
{
    ReportError(exception.Code.ToString(), exception.Message);
}
catch (ProcessIdentityRejectedException exception)
{
    ReportError("bridge_identity_rejected", exception.Message);
}
catch (Exception exception) when (
    exception is TimeoutException or IOException)
{
    ReportError("bridge_unavailable", exception.Message);
}
catch (Exception exception)
{
    ReportError("mcp_error", exception.Message);
}
finally
{
    if (bridge is not null)
    {
        try
        {
            await bridge.RevokeGrantAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"bridge_revoke_failed: {exception.Message}");
        }
    }
}

static void ReportError(string code, string message)
{
    Console.Error.WriteLine($"{code}: {message}");
    Environment.ExitCode = 1;
}

public interface IMcpBridgeClient
{
    Task<string> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    Task RevokeGrantAsync(CancellationToken cancellationToken = default);
}

public sealed record StrictBridgeSettings(
    CipherVaultInstallPublicManifest Manifest,
    string ManifestPath,
    string PipeName,
    string ClientId,
    McpAccessMode AccessMode,
    IReadOnlySet<string> RequestedScopes)
{
    public bool ReadOnly => AccessMode == McpAccessMode.ReadOnly;

    public static StrictBridgeSettings Load(string[] arguments)
    {
        if (arguments.Any(argument => string.Equals(
                argument,
                "--config",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "--config is not supported. The bridge uses the strict service manifest.");
        }

        var paths = CipherVaultServicePaths.Default;
        var manifestPath = GetOption(arguments, "--manifest") ??
            paths.PublicManifestPath;
        var manifest = CipherVaultServiceConfiguration.LoadInstallManifest(
            manifestPath);
        var pipe = manifest.Pipes?.Bridge
            ?? throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Bridge pipe is missing.");
        var scopesText = GetOption(arguments, "--scopes") ?? "read,write";
        var scopes = scopesText.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return new StrictBridgeSettings(
            manifest,
            manifestPath,
            pipe,
            GetOption(arguments, "--client-id") ?? "mcp",
            scopes.SetEquals(["read"])
                ? McpAccessMode.ReadOnly
                : McpAccessMode.ReadWrite,
            scopes);
    }

    private static string? GetOption(string[] arguments, string option)
    {
        var index = Array.FindIndex(
            arguments,
            value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < arguments.Length
            ? arguments[index + 1]
            : null;
    }
}

public sealed class StrictMcpBridgeClient : IMcpBridgeClient
{
    private readonly BridgePipeClientConnection _connection;

    private StrictMcpBridgeClient(BridgePipeClientConnection connection)
    {
        _connection = connection;
    }

    public static async Task<StrictMcpBridgeClient> ConnectAsync(
        StrictBridgeSettings settings)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "Unable to resolve current user.");
        var options = StrictServiceClientPolicyFactory.CreateBridgeOptions(
            settings.Manifest,
            settings.PipeName,
            settings.ClientId,
            settings.AccessMode,
            settings.RequestedScopes,
            user.Value,
            (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId);
        var connection = await BridgePipeClientConnection.ConnectAsync(options);
        _ = await connection.RequestGrantAsync();
        return new StrictMcpBridgeClient(connection);
    }

    public Task<string> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        return _connection.CallToolAsync(name, arguments, cancellationToken);
    }

    public async Task RevokeGrantAsync(CancellationToken cancellationToken = default)
    {
        await _connection.DisposeAsync();
    }
}

[McpServerToolType]
public sealed class BridgeVaultTools
{
    private readonly IMcpBridgeClient _bridge;
    private readonly bool _readOnly;

    public BridgeVaultTools(IMcpBridgeClient bridge, bool readOnly)
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
    [Description("List active document titles, virtual folders, headings and tags without returning the real filesystem path. Pagination is keyset-based, not a full database snapshot; concurrent modifications may be skipped.")]
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
    [Description("Search active Markdown/TXT documents with FTS5 or a short-query scan limited to the 1000 most recently updated documents. Coverage includes totalDocuments and isTruncated; when truncated, an empty result does not prove the content is absent.")]
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
    [Description("Page through active document summaries without returning document content. Pagination is keyset-based, not a full database snapshot; concurrent modifications may be skipped.")]
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
