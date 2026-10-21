using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CipherVault.Core;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Host.Contracts;
using CipherVault.Host.Http;
using CipherVault.Host.Session;
using ModelContextProtocol.Server;

namespace CipherVault.Host.Mcp;

[McpServerToolType]
public sealed class HostVaultTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HostVaultGateway _vault;
    private readonly HostSessionCoordinator _sessions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HostVaultTools(
        HostVaultGateway vault,
        HostSessionCoordinator sessions,
        IHttpContextAccessor httpContextAccessor)
    {
        _vault = vault;
        _sessions = sessions;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "vault_status")]
    [Description("Return the Vault lock state and session deadlines. This tool never returns document content.")]
    public async Task<string> VaultStatus(CancellationToken cancellationToken)
    {
        var client = RequireClient();
        if (client.Error is not null)
        {
            return client.Error;
        }

        try
        {
            var snapshot = await _sessions.GetSnapshotAsync(cancellationToken);
            return Serialize(new
            {
                ok = true,
                vault = new
                {
                    configured = true,
                    clientId = client.ClientId,
                    accessMode = client.AccessMode == McpAccessMode.ReadOnly
                        ? "readOnly"
                        : "readWrite"
                },
                session = snapshot
            });
        }
        catch (HostException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    [McpServerTool(Name = "list_catalog")]
    [Description("List active document titles, virtual folders, headings and tags without returning the real filesystem path.")]
    public async Task<string> ListCatalog(
        string? folder = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteReadAsync(
            (service, token) => service.ListCatalogAsync(
                folder,
                cursor,
                limit,
                token),
            page => new { ok = true, page },
            cancellationToken);
    }

    [McpServerTool(Name = "search_notes")]
    [Description("Search active Markdown/TXT documents with FTS5 or a bounded short-query scan and return snippets.")]
    public async Task<string> SearchNotes(
        string query,
        string? folder = null,
        string? cursor = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteReadAsync(
            (service, token) => service.SearchNotesAsync(
                query,
                folder,
                cursor,
                limit,
                token),
            page => new { ok = true, page },
            cancellationToken);
    }

    [McpServerTool(Name = "read_note")]
    [Description("Read a bounded character range from one active document and verify its application-level integrity HMAC.")]
    public async Task<string> ReadNote(
        string documentId,
        int offset = 0,
        int maxChars = 8_000,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }
        return await ExecuteReadAsync(
            (service, token) => service.ReadNoteAsync(
                parsedDocumentId,
                offset,
                maxChars,
                token),
            document => new { ok = true, document },
            cancellationToken);
    }

    [McpServerTool(Name = "scan_notes")]
    [Description("Page through active document summaries without returning document content.")]
    public async Task<string> ScanNotes(
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteReadAsync(
            (service, token) => service.ScanNotesAsync(cursor, limit, token),
            page => new { ok = true, page },
            cancellationToken);
    }

    [McpServerTool(Name = "create_note")]
    [Description("Create an encrypted Markdown or TXT document in the Vault.")]
    public async Task<string> CreateNote(
        string title,
        string content,
        string? folder = null,
        string format = "md",
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var accessError = EnsureWritable();
        if (accessError is not null)
        {
            return accessError;
        }
        var auditContext = CurrentAuditContext();
        return await ExecuteWriteAsync(
            (service, token) => service.CreateNoteAsync(
                title,
                folder,
                format,
                content,
                tags,
                auditContext,
                token),
            document => new { ok = true, document },
            cancellationToken);
    }

    [McpServerTool(Name = "append_note")]
    [Description("Append content to an existing document using optimistic revision concurrency.")]
    public async Task<string> AppendNote(
        string documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default)
    {
        var accessError = EnsureWritable();
        if (accessError is not null)
        {
            return accessError;
        }
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }
        var auditContext = CurrentAuditContext();
        return await ExecuteWriteAsync(
            (service, token) => service.AppendNoteAsync(
                parsedDocumentId,
                expectedRevision,
                content,
                auditContext,
                token),
            document => new { ok = true, document },
            cancellationToken);
    }

    [McpServerTool(Name = "update_note")]
    [Description("Update title, virtual folder, content or tags using optimistic revision concurrency.")]
    public async Task<string> UpdateNote(
        string documentId,
        int expectedRevision,
        string? title = null,
        string? folder = null,
        string? content = null,
        string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        var accessError = EnsureWritable();
        if (accessError is not null)
        {
            return accessError;
        }
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }
        var auditContext = CurrentAuditContext();
        return await ExecuteWriteAsync(
            (service, token) => service.UpdateNoteAsync(
                parsedDocumentId,
                expectedRevision,
                new VaultNotePatch(title, folder, content, tags),
                auditContext,
                token),
            document => new { ok = true, document },
            cancellationToken);
    }

    [McpServerTool(Name = "delete_note")]
    [Description("Move a document to the encrypted recycle bin using optimistic revision concurrency.")]
    public async Task<string> DeleteNote(
        string documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var accessError = EnsureWritable();
        if (accessError is not null)
        {
            return accessError;
        }
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }
        var auditContext = CurrentAuditContext();
        return await ExecuteWriteAsync(
            (service, token) => service.DeleteNoteAsync(
                parsedDocumentId,
                expectedRevision,
                auditContext,
                token),
            document => new { ok = true, document },
            cancellationToken);
    }

    private async Task<string> ExecuteReadAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        Func<T, object> success,
        CancellationToken cancellationToken)
    {
        var client = RequireClient();
        if (client.Error is not null)
        {
            return client.Error;
        }
        try
        {
            var result = await _vault.ExecuteAsync(operation, cancellationToken);
            return Serialize(success(result));
        }
        catch (HostException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    private Task<string> ExecuteWriteAsync<T>(
        Func<IVaultService, CancellationToken, Task<T>> operation,
        Func<T, object> success,
        CancellationToken cancellationToken)
    {
        return ExecuteReadAsync(operation, success, cancellationToken);
    }

    private (string? ClientId, McpAccessMode AccessMode, string? Error)
        RequireClient()
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        var clientId = principal?.FindFirstValue(
            AgentTokenAuthenticationMiddleware.ClientIdClaim);
        var accessMode = principal?.FindFirstValue(
            AgentTokenAuthenticationMiddleware.AccessModeClaim);
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(accessMode))
        {
            return (
                null,
                McpAccessMode.ReadOnly,
                Serialize(Error(
                    "access_denied",
                    "The agent client identity is unavailable.")));
        }

        return (
            clientId,
            string.Equals(accessMode, "readWrite", StringComparison.OrdinalIgnoreCase)
                ? McpAccessMode.ReadWrite
                : McpAccessMode.ReadOnly,
            null);
    }

    private string? EnsureWritable()
    {
        var client = RequireClient();
        if (client.Error is not null)
        {
            return client.Error;
        }
        return client.AccessMode == McpAccessMode.ReadOnly
            ? Serialize(Error(
                "read_only",
                "This CipherVault MCP client is configured for read-only access."))
            : null;
    }

    private VaultAuditContext CurrentAuditContext()
    {
        var client = RequireClient();
        var requestId = _httpContextAccessor.HttpContext?.Items.TryGetValue(
                HostHttpContextKeys.RequestId,
                out var value) == true
            ? value as string
            : null;
        return new VaultAuditContext(
            VaultActorKind.Mcp,
            client.ClientId,
            requestId);
    }

    private static object Error(string code, string message)
    {
        return new
        {
            ok = false,
            error = new
            {
                code,
                message
            }
        };
    }

    private static string Serialize(object value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static string ToCode(HostErrorCode code)
    {
        return code switch
        {
            HostErrorCode.InvalidInput => "invalid_input",
            HostErrorCode.AlreadyExists => "already_exists",
            HostErrorCode.NotFound => "not_found",
            HostErrorCode.Conflict => "conflict",
            HostErrorCode.SessionLocked => "auth_required",
            HostErrorCode.MaintenanceInProgress => "maintenance_in_progress",
            HostErrorCode.AccessDenied => "access_denied",
            _ => "storage_error"
        };
    }

    private static string ToCode(VaultErrorCode code)
    {
        return code switch
        {
            VaultErrorCode.InvalidInput => "invalid_input",
            VaultErrorCode.AlreadyExists => "already_exists",
            VaultErrorCode.NotFound => "not_found",
            VaultErrorCode.InvalidCredential => "auth_denied",
            VaultErrorCode.Conflict => "conflict",
            VaultErrorCode.CorruptVault => "integrity_error",
            VaultErrorCode.SessionLocked => "auth_required",
            VaultErrorCode.SessionExpired => "session_expired",
            VaultErrorCode.RecoveryVerificationRequired =>
                "recovery_verification_required",
            _ => "storage_error"
        };
    }
}
