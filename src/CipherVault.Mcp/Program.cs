using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CipherVault.Auth.Win;
using CipherVault.Core;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.Session;

var vaultPath = GetOption(args, "--vault")
    ?? Environment.GetEnvironmentVariable("CIPHER_VAULT_PATH");
await using var vault = new VaultService(new WindowsSystemActivityProvider());
var tools = new VaultTools(vault, vaultPath, new WindowsCredentialPrompt());

var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<VaultTools>(tools);

await builder.Build().RunAsync();

static string? GetOption(string[] arguments, string option)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }
    return null;
}

public interface ICredentialPrompt
{
    Task<PasswordPromptResult> RequestAsync(
        string title,
        string message,
        CancellationToken cancellationToken);
}

public sealed class WindowsCredentialPrompt : ICredentialPrompt
{
    public Task<PasswordPromptResult> RequestAsync(
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        return WindowsPasswordPrompt.RequestAsync(title, message, cancellationToken);
    }
}

[McpServerToolType]
public sealed class VaultTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IVaultService _vault;
    private readonly string? _vaultPath;
    private readonly ICredentialPrompt _credentialPrompt;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private DateTimeOffset _deniedUntil = DateTimeOffset.MinValue;

    public VaultTools(
        IVaultService vault,
        string? vaultPath,
        ICredentialPrompt credentialPrompt)
    {
        _vault = vault;
        _vaultPath = vaultPath;
        _credentialPrompt = credentialPrompt;
    }

    [McpServerTool(Name = "vault_status")]
    [Description("Return the Vault lock state and session deadlines. This tool never returns document content.")]
    public async Task<string> VaultStatus(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_vaultPath))
        {
            return Serialize(Error("not_configured", "Set --vault or CIPHER_VAULT_PATH."));
        }

        var status = await _vault.GetStatusAsync(cancellationToken);
        return Serialize(new
        {
            ok = true,
            vault = new { configured = true },
            session = status
        });
    }

    [McpServerTool(Name = "list_catalog")]
    [Description("List active document titles, virtual folders, headings and tags without returning the real filesystem path.")]
    public async Task<string> ListCatalog(
        string? folder = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        try
        {
            var page = await _vault.ListCatalogAsync(
                folder,
                cursor,
                limit,
                cancellationToken);
            return Serialize(new { ok = true, page });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
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
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        try
        {
            var page = await _vault.SearchNotesAsync(
                query,
                folder,
                cursor,
                limit,
                cancellationToken);
            return Serialize(new { ok = true, page });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    [McpServerTool(Name = "read_note")]
    [Description("Read a bounded character range from one active document and verify its application-level integrity HMAC.")]
    public async Task<string> ReadNote(
        string documentId,
        int offset = 0,
        int maxChars = 8_000,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }

        try
        {
            var result = await _vault.ReadNoteAsync(
                parsedDocumentId,
                offset,
                maxChars,
                cancellationToken);
            return Serialize(new { ok = true, document = result });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    [McpServerTool(Name = "scan_notes")]
    [Description("Page through active document summaries without returning document content.")]
    public async Task<string> ScanNotes(
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        try
        {
            var page = await _vault.ScanNotesAsync(cursor, limit, cancellationToken);
            return Serialize(new { ok = true, page });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
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
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        try
        {
            var result = await _vault.CreateNoteAsync(
                title,
                folder,
                format,
                content,
                tags,
                VaultActorKind.Mcp,
                cancellationToken);
            return Serialize(new { ok = true, document = result });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    [McpServerTool(Name = "append_note")]
    [Description("Append content to an existing document using optimistic revision concurrency.")]
    public async Task<string> AppendNote(
        string documentId,
        int expectedRevision,
        string content,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }

        try
        {
            var result = await _vault.AppendNoteAsync(
                parsedDocumentId,
                expectedRevision,
                content,
                VaultActorKind.Mcp,
                cancellationToken);
            return Serialize(new { ok = true, document = result });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
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
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }

        try
        {
            var result = await _vault.UpdateNoteAsync(
                parsedDocumentId,
                expectedRevision,
                new VaultNotePatch(title, folder, content, tags),
                VaultActorKind.Mcp,
                cancellationToken);
            return Serialize(new { ok = true, document = result });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    [McpServerTool(Name = "delete_note")]
    [Description("Move a document to the encrypted recycle bin using optimistic revision concurrency.")]
    public async Task<string> DeleteNote(
        string documentId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var authorizationError = await EnsureUnlockedAsync(cancellationToken);
        if (authorizationError is not null)
        {
            return authorizationError;
        }

        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return Serialize(Error("invalid_input", "documentId must be a UUID."));
        }

        try
        {
            var result = await _vault.DeleteNoteAsync(
                parsedDocumentId,
                expectedRevision,
                VaultActorKind.Mcp,
                cancellationToken);
            return Serialize(new { ok = true, document = result });
        }
        catch (VaultException exception)
        {
            return Serialize(Error(ToCode(exception.Code), exception.Message));
        }
    }

    private async Task<string?> EnsureUnlockedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_vaultPath))
        {
            return Serialize(Error("not_configured", "Set --vault or CIPHER_VAULT_PATH."));
        }

        await _authGate.WaitAsync(cancellationToken);
        try
        {
            var status = await _vault.GetStatusAsync(cancellationToken);
            if (status.State is VaultSessionState.Unlocked or VaultSessionState.Expiring)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (now < _deniedUntil)
            {
                return Serialize(new
                {
                    ok = false,
                    error = new
                    {
                        code = "auth_denied",
                        message = "Vault authorization is cooling down after a denied request.",
                        retryAfterMs = (int)(_deniedUntil - now).TotalMilliseconds
                    }
                });
            }

            var prompt = await _credentialPrompt.RequestAsync(
                "CipherVault 授权",
                "AI 请求访问你的 CipherVault 加密资料库。",
                cancellationToken);
            if (!prompt.Confirmed || string.IsNullOrEmpty(prompt.Password))
            {
                _deniedUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                return Serialize(Error("auth_denied", "Vault authorization was cancelled."));
            }

            try
            {
                await _vault.UnlockAsync(
                    _vaultPath,
                    prompt.Password,
                    VaultCredentialType.Password,
                    cancellationToken);
                return null;
            }
            catch (VaultException exception)
            {
                _deniedUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                return Serialize(Error(ToCode(exception.Code), exception.Message));
            }
        }
        finally
        {
            _authGate.Release();
        }
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
            VaultErrorCode.RecoveryVerificationRequired => "recovery_verification_required",
            VaultErrorCode.UnsupportedPlatform => "unsupported_platform",
            VaultErrorCode.StorageError => "storage_error",
            _ => "storage_error"
        };
    }
}
