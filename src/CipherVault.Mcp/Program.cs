using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CipherVault.Core.Configuration;
using CipherVault.Core.Errors;
using CipherVault.Host.Client;
using CipherVault.Host.Contracts;
using CipherVault.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
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
    var tools = new BridgeVaultTools(bridge);
    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    var mcpServer = builder.Services
        .AddMcpServer(BridgeVaultServerMetadata.Configure)
        .WithStdioServerTransport()
        .WithTools<BridgeVaultTools>(tools);
    mcpServer.WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next =>
            async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken)
                    .ConfigureAwait(false);
                var scopes = tools.VisibleScopes;
                result.Tools = result.Tools
                    .Where(tool =>
                        BridgeVaultTools.IsToolAvailable(
                            tool.Name,
                            scopes))
                    .Select(tool =>
                    {
                        tool.Annotations = BridgeVaultTools.AnnotationsFor(
                            tool.Name);
                        return tool;
                    })
                    .ToList();
                return result;
            });
    });

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
    IReadOnlySet<string> RequestedScopes { get; }

    IReadOnlySet<string> GrantedScopes { get; }

    Task<string> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    Task RequestGrantAsync(CancellationToken cancellationToken = default);

    Task RevokeGrantAsync(CancellationToken cancellationToken = default);
}

public static class BridgeVaultServerMetadata
{
    public const string Instructions =
        """
        CipherVault is a local encrypted knowledge vault.
        Prefer search_notes, list_catalog, or scan_notes before reading documents; do not assume full-vault coverage.
        Read the current revision before any mutation. On revision_conflict, re-read and retry with the returned currentRevision.
        Respect truncated, nextCursor, estimatedBytes, and bytesTruncated fields in tool results.
        find_unlinked_mentions only suggests plain-text relationships and never modifies content.
        delete_note moves a document to the encrypted recycle bin.
        """;

    public static void Configure(McpServerOptions options)
    {
        options.ServerInfo = new Implementation
        {
            Name = "CipherVault",
            Version = typeof(BridgeVaultTools).Assembly
                .GetName()
                .Version?
                .ToString() ?? "dev",
            Description = "Local encrypted Markdown and text knowledge vault."
        };
        options.ServerInstructions = Instructions;
    }
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
        if (scopes.Any(scope => !IsValidScopeToken(scope)))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Bridge scope must use read, write, delete, or action:folder.");
        }
        return new StrictBridgeSettings(
            manifest,
            manifestPath,
            pipe,
            GetOption(arguments, "--client-id") ?? "mcp",
            scopes.All(scope =>
                scope == "read" ||
                scope.StartsWith("read:", StringComparison.Ordinal))
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

    private static bool IsValidScopeToken(string value)
    {
        var parts = value.Split(
            ':',
            2,
            StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return parts[0] is "read" or "write" or "delete";
        }
        if (parts[0] is not ("read" or "write" or "delete"))
        {
            return false;
        }
        var folder = parts[1].Replace('\\', '/').Trim('/');
        return folder.Length > 0 &&
            !folder.Split('/').Any(part =>
                part is "" or "." or "..");
    }
}

public sealed class StrictMcpBridgeClient : IMcpBridgeClient
{
    private static Process? _brokerProcess;
    private static readonly SemaphoreSlim BrokerGate = new(1, 1);
    private static readonly IReadOnlySet<string> NoScopes =
        new HashSet<string>(StringComparer.Ordinal);
    private readonly StrictBridgeSettings _settings;
    private readonly Func<
        CancellationToken,
        Task<IBridgePipeClientConnection>> _connectionFactory;
    private readonly Func<CancellationToken, Task<bool>> _ensureBrokerStarted;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IBridgePipeClientConnection? _connection;
    private IReadOnlySet<string> _grantedScopes = NoScopes;
    private bool _disposed;

    internal StrictMcpBridgeClient(
        StrictBridgeSettings settings,
        IBridgePipeClientConnection connection,
        Func<
            CancellationToken,
            Task<IBridgePipeClientConnection>> connectionFactory,
        Func<CancellationToken, Task<bool>> ensureBrokerStarted)
    {
        _settings = settings;
        _connection = connection;
        _connectionFactory = connectionFactory;
        _ensureBrokerStarted = ensureBrokerStarted;
    }

    public static async Task<StrictMcpBridgeClient> ConnectAsync(
        StrictBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        _ = await EnsureBrokerStartedAsync(
            settings,
            cancellationToken).ConfigureAwait(false);
        var connection = await CreateConnectionAsync(
            settings,
            cancellationToken).ConfigureAwait(false);
        return new StrictMcpBridgeClient(
            settings,
            connection,
            token => CreateConnectionAsync(settings, token),
            token => EnsureBrokerStartedAsync(settings, token));
    }

    private static async Task<IBridgePipeClientConnection>
        CreateConnectionAsync(
            StrictBridgeSettings settings,
            CancellationToken cancellationToken)
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
        return await BridgePipeClientConnection.ConnectAsync(
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EnsureBrokerStartedAsync(
        StrictBridgeSettings settings,
        CancellationToken cancellationToken)
    {
        await BrokerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The Broker is a long-lived interactive process, so its handle is
            // kept for the MCP server lifetime instead of being disposed.
            _brokerProcess = StrictServiceBrokerLauncher.EnsureStarted(
                settings.Manifest,
                settings.ManifestPath);
            return true;
        }
        catch (Exception exception) when (
            exception is VaultException or
            Win32Exception or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            NotSupportedException)
        {
            // Starting the Broker is best effort: a missing or unverifiable
            // Broker installation must not mask the Bridge error handling.
            Console.Error.WriteLine(
                $"broker_launch_failed: {exception.Message}");
            return false;
        }
        finally
        {
            BrokerGate.Release();
        }
    }

    public IReadOnlySet<string> RequestedScopes => _settings.RequestedScopes;

    public IReadOnlySet<string> GrantedScopes =>
        Volatile.Read(ref _grantedScopes);

    public async Task<string> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var connection = await GetConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await connection.CallToolAsync(
                name,
                arguments,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            IsTransportFailure(exception))
        {
            var replacement = await ReconnectAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            if (RequiresGrant(name))
            {
                try
                {
                    await RequestGrantAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception grantException)
                {
                    return ErrorResult(
                        "bridge_reauthorization_failed",
                        grantException.Message);
                }
            }
            if (!IsRetrySafeTool(name))
            {
                return ErrorResult(
                    "bridge_connection_reset",
                    "The Bridge connection was re-established. " +
                    "The write operation was not retried automatically.");
            }
            try
            {
                return await replacement.CallToolAsync(
                    name,
                    arguments,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryException) when (
                IsTransportFailure(retryException))
            {
                return ErrorResult(
                    "bridge_unavailable",
                    retryException.Message);
            }
        }
    }

    public async Task RequestGrantAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var brokerRecoveryAttempted = false;
        var reconnectAttempted = false;
        while (true)
        {
            var connection = await GetConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await RequestGrantOnConnectionAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PipeRpcException exception) when (
                string.Equals(
                    exception.Code,
                    "approval_broker_unavailable",
                    StringComparison.Ordinal) &&
                !brokerRecoveryAttempted)
            {
                brokerRecoveryAttempted = true;
                if (!await _ensureBrokerStarted(cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw;
                }
                var recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(12);
                while (DateTimeOffset.UtcNow < recoveryDeadline)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(750),
                        cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await RequestGrantOnConnectionAsync(
                            connection,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (PipeRpcException retryException) when (
                        string.Equals(
                            retryException.Code,
                            "approval_broker_unavailable",
                            StringComparison.Ordinal))
                    {
                    }
                }
                throw;
            }
            catch (Exception exception) when (
                !reconnectAttempted &&
                IsTransportFailure(exception))
            {
                reconnectAttempted = true;
                _ = await ReconnectAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RevokeGrantAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Interlocked.Exchange(ref _grantedScopes, NoScopes);
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        _connectionGate.Dispose();
    }

    private async Task<IBridgePipeClientConnection> GetConnectionAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_connection is { IsUsable: true })
        {
            return _connection;
        }
        await _connectionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (_connection is { IsUsable: true })
            {
                return _connection;
            }
            await ReplaceConnectionLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            return _connection!;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task<IBridgePipeClientConnection> ReconnectAsync(
        IBridgePipeClientConnection failedConnection,
        CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_connection, failedConnection) &&
                _connection is { IsUsable: true })
            {
                return _connection;
            }
            if (ReferenceEquals(_connection, failedConnection))
            {
                _connection = null;
            }
            await failedConnection.DisposeAsync().ConfigureAwait(false);
            await ReplaceConnectionLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            return _connection!;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ReplaceConnectionLockedAsync(
        CancellationToken cancellationToken)
    {
        var previous = Interlocked.Exchange(ref _connection, null);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        Interlocked.Exchange(ref _grantedScopes, NoScopes);
        _connection = await _connectionFactory(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RequestGrantOnConnectionAsync(
        IBridgePipeClientConnection connection,
        CancellationToken cancellationToken)
    {
        var grant = await connection.RequestGrantAsync(cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Exchange(
            ref _grantedScopes,
            grant.Scopes.ToHashSet(StringComparer.Ordinal));
    }

    private static bool IsTransportFailure(Exception exception)
    {
        return exception is IOException or
            ObjectDisposedException or
            TimeoutException ||
            exception is PipeRpcException
            {
                Code: "bridge_session_expired"
            };
    }

    private static bool IsRetrySafeTool(string name)
    {
        return name is
            "vault_status" or
            "list_catalog" or
            "search_notes" or
            "read_note" or
            "scan_notes" or
            "get_document_map" or
            "get_backlinks" or
            "find_unlinked_mentions" or
            "tag_list" or
            "get_local_graph";
    }

    private static bool RequiresGrant(string name)
    {
        return !string.Equals(
            name,
            "vault_status",
            StringComparison.Ordinal);
    }

    private static string ErrorResult(string code, string message)
    {
        return McpToolErrorJson.Serialize(code, message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

[McpServerToolType]
public sealed class BridgeVaultTools
{
    private readonly IMcpBridgeClient _bridge;

    public BridgeVaultTools(IMcpBridgeClient bridge)
    {
        _bridge = bridge;
    }

    public IReadOnlySet<string> VisibleScopes =>
        _bridge.GrantedScopes.Count > 0
            ? _bridge.GrantedScopes
            : _bridge.RequestedScopes;

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
    [Description("Search active Markdown/TXT documents with optional tag, format, updated-time, backlink, or orphan filters. One- and two-character queries scan up to the 10000 most recently updated documents. Coverage includes totalDocuments and isTruncated.")]
    public Task<string> SearchNotes(
        string query,
        string? folder = null,
        string? cursor = null,
        int limit = 10,
        string[]? tags = null,
        string? format = null,
        DateTimeOffset? updatedAfter = null,
        DateTimeOffset? updatedBefore = null,
        bool? hasBacklinks = null,
        bool orphanOnly = false,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "search_notes",
            Arguments(
                ("query", query),
                ("folder", folder),
                ("cursor", cursor),
                ("limit", limit),
                ("tags", tags),
                ("format", format),
                ("updatedAfter", updatedAfter),
                ("updatedBefore", updatedBefore),
                ("hasBacklinks", hasBacklinks),
                ("orphanOnly", orphanOnly)),
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

    [McpServerTool(Name = "tag_list")]
    [Description("List active document tags and usage counts with cursor pagination.")]
    public Task<string> TagList(
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "tag_list",
            Arguments(("cursor", cursor), ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "get_document_map")]
    [Description("Return bounded headings, top-level frontmatter, outgoing Wiki Links and backlink count without document content. Defaults cap each collection at 100 items and the response at 64 KiB; truncation flags identify omitted fields.")]
    public Task<string> GetDocumentMap(
        string documentId,
        int maxHeadings = 100,
        int maxFrontMatterFields = 100,
        int maxLinks = 100,
        int maxResponseBytes = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "get_document_map",
            Arguments(
                ("documentId", documentId),
                ("maxHeadings", maxHeadings),
                ("maxFrontMatterFields", maxFrontMatterFields),
                ("maxLinks", maxLinks),
                ("maxResponseBytes", maxResponseBytes)),
            cancellationToken);
    }

    [McpServerTool(Name = "get_backlinks")]
    [Description("Return active documents that link to the requested document, grouped by source and cursor paginated.")]
    public Task<string> GetBacklinks(
        string documentId,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "get_backlinks",
            Arguments(
                ("documentId", documentId),
                ("cursor", cursor),
                ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "find_unlinked_mentions")]
    [Description("Find plain-text body mentions of a document title that are not Wiki Links. Returns source-grouped contexts with cursor pagination and does not modify content.")]
    public Task<string> FindUnlinkedMentions(
        string documentId,
        string? cursor = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "find_unlinked_mentions",
            Arguments(
                ("documentId", documentId),
                ("cursor", cursor),
                ("limit", limit)),
            cancellationToken);
    }

    [McpServerTool(Name = "get_local_graph")]
    [Description("Return a bounded one- or two-hop directed graph around one document. Tags use AND semantics.")]
    public Task<string> GetLocalGraph(
        string documentId,
        int depth = 1,
        string[]? tags = null,
        string? folder = null,
        int maxNodes = 200,
        int maxEdges = 500,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "get_local_graph",
            Arguments(
                ("documentId", documentId),
                ("depth", depth),
                ("tags", tags),
                ("folder", folder),
                ("maxNodes", maxNodes),
                ("maxEdges", maxEdges)),
            cancellationToken);
    }

    [McpServerTool(Name = "create_note")]
    [Description("Create an encrypted Markdown or TXT document in the Vault. For Markdown, frontmatter tags are authoritative and the tags argument must match them when both are provided.")]
    public Task<string> CreateNote(
        string title,
        string content,
        string? folder = null,
        string format = "md",
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
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
        return CallAsync(
            "append_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision),
                ("content", content)),
            cancellationToken);
    }

    [McpServerTool(Name = "update_note")]
    [Description("Update title, virtual folder, content or tags using optimistic revision concurrency. For Markdown, frontmatter tags are authoritative and the tags argument must match them when both are provided. Set updateBacklinks to rewrite matching title references when renaming.")]
    public Task<string> UpdateNote(
        string documentId,
        int expectedRevision,
        string? title = null,
        string? folder = null,
        string? content = null,
        string[]? tags = null,
        bool updateBacklinks = false,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "update_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision),
                ("title", title),
                ("folder", folder),
                ("content", content),
                ("tags", tags),
                ("updateBacklinks", updateBacklinks)),
            cancellationToken);
    }

    [McpServerTool(Name = "patch_note")]
    [Description("Patch a heading or top-level frontmatter field using optimistic revision concurrency. Set dryRun to preview the diff.")]
    public Task<string> PatchNote(
        string documentId,
        int expectedRevision,
        string targetKind,
        string operation,
        string[]? headingPath = null,
        string? frontmatterKey = null,
        string? content = null,
        string? valueJson = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "patch_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision),
                ("targetKind", targetKind),
                ("operation", operation),
                ("headingPath", headingPath),
                ("frontmatterKey", frontmatterKey),
                ("content", content),
                ("valueJson", valueJson),
                ("dryRun", dryRun)),
            cancellationToken);
    }

    [McpServerTool(Name = "delete_note")]
    [Description("Move a document to the encrypted recycle bin using optimistic revision concurrency.")]
    public Task<string> DeleteNote(
        string documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return CallAsync(
            "delete_note",
            Arguments(
                ("documentId", documentId),
                ("expectedRevision", expectedRevision)),
            cancellationToken);
    }

    private async Task<string> CallAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!CanCall(name))
        {
            return AccessDenied(name);
        }
        var result = await _bridge
            .CallToolAsync(name, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (!HasLostGrant(result))
        {
            return result;
        }
        try
        {
            await _bridge.RequestGrantAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PipeRpcException exception)
        {
            return ErrorMessage(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            return ErrorMessage(
                "bridge_reauthorization_failed",
                exception.Message);
        }
        if (!CanCall(name))
        {
            return AccessDenied(name);
        }
        return await _bridge
            .CallToolAsync(name, arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool CanCall(string name)
    {
        var granted = _bridge.GrantedScopes;
        return granted.Count == 0 || IsToolAvailable(name, granted);
    }

    public static bool IsToolAvailable(
        string name,
        IReadOnlySet<string> scopes)
    {
        var required = name switch
        {
            "create_note" or "append_note" or "update_note" or "patch_note" => "write",
            "delete_note" => "delete",
            _ => "read"
        };
        return scopes.Contains(required) ||
            scopes.Any(scope => scope.StartsWith(
                required + ":",
                StringComparison.Ordinal));
    }

    public static ToolAnnotations AnnotationsFor(string name)
    {
        var readOnly = name is
            "vault_status" or
            "list_catalog" or
            "search_notes" or
            "read_note" or
            "scan_notes" or
            "tag_list" or
            "get_document_map" or
            "get_backlinks" or
            "find_unlinked_mentions" or
            "get_local_graph";
        var destructive = name is
            "append_note" or
            "update_note" or
            "patch_note" or
            "delete_note";
        return new ToolAnnotations
        {
            ReadOnlyHint = readOnly,
            DestructiveHint = destructive,
            IdempotentHint = readOnly,
            OpenWorldHint = false
        };
    }

    private static string AccessDenied(string name)
    {
        return ErrorMessage(
            "access_denied",
            $"The current Access Grant does not allow {name}.");
    }

    private static bool HasLostGrant(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            return code.GetString() switch
            {
                "access_grant_required" => true,
                "access_grant_revoked" => true,
                "access_grant_expired" => true,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
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
        return McpToolErrorJson.Serialize(code, message);
    }
}

public static class McpToolErrorJson
{
    public static string Serialize(string code, string message)
    {
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["retryable"] = McpToolErrorMetadata.IsRetryable(code)
        };
        var hint = McpToolErrorMetadata.HintFor(code);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            error["hint"] = hint;
        }
        return JsonSerializer.Serialize(new { ok = false, error });
    }
}
