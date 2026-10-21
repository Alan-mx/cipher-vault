using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CipherVault.Core.Backup;
using CipherVault.Core.Configuration;
using CipherVault.Core.Documents;
using CipherVault.Core.Errors;
using CipherVault.Core.ImportExport;
using CipherVault.Core.Session;
using CipherVault.Host.Contracts;
using CipherVault.Host.Security;

namespace CipherVault.Host.Client;

public sealed class HttpHostVaultClient : IHostVaultClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string _adminPath;
    private readonly string _adminToken;
    private readonly HostAdminClientKind _clientKind;
    private readonly string _expectedConfigFingerprint;

    private HttpHostVaultClient(
        HttpClient http,
        string adminPath,
        string adminToken,
        HostAdminClientKind clientKind,
        string expectedConfigFingerprint)
    {
        _http = http;
        _adminPath = adminPath;
        _adminToken = adminToken;
        _clientKind = clientKind;
        _expectedConfigFingerprint = expectedConfigFingerprint;
    }

    public bool PromptsForCredentials => true;

    public static async Task<HttpHostVaultClient> CreateAsync(
        ResolvedCipherVaultHostConfig configuration,
        HostAdminClientKind clientKind,
        CancellationToken cancellationToken = default)
    {
        var token = await new DpapiAdminTokenStore().GetTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var baseAddress = new Uri(
            $"http://{configuration.Endpoint.BindAddress}:{configuration.Endpoint.Port}");
        var http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromMinutes(2)
        };
        var expectedFingerprint = HostConfigurationFingerprint.Compute(
            configuration.VaultPath,
            configuration.ConfigPath,
            configuration.Endpoint.BindAddress,
            configuration.Endpoint.Port,
            configuration.Endpoint.McpPath,
            configuration.Endpoint.AdminPath);
        var client = new HttpHostVaultClient(
            http,
            configuration.Endpoint.AdminPath,
            token,
            clientKind,
            expectedFingerprint);
        try
        {
            var health = await client.ProbeHealthAsync(cancellationToken)
                .ConfigureAwait(false);
            if (health is null)
            {
                HostProcessLauncher.Start(configuration.ConfigPath);
                await client.WaitUntilHealthyAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                client.ValidateFingerprint(health);
            }
            return client;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public async Task<VaultInitializeResult> InitializeAsync(
        string vaultPath,
        string? typedPassword = null,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<HostInitializeData>(
            HttpMethod.Post,
            "/vault/initialize",
            new HostInitializeRequest(vaultPath),
            cancellationToken).ConfigureAwait(false);
        return new VaultInitializeResult(
            data.VaultId,
            data.VaultPath,
            data.RecoveryKey,
            data.CreatedAt);
    }

    public async Task<VaultSessionStatus> UnlockAsync(
        string vaultPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<HostSessionStatusData>(
            HttpMethod.Post,
            "/session/unlock",
            new HostUnlockRequest("any"),
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(data);
    }

    public async Task<VaultSessionStatus> LockAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<HostSessionStatusData>(
            HttpMethod.Post,
            "/session/lock",
            new HostLockRequest(reason),
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(data);
    }

    public async Task<VaultSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await SendAsync<HostStatusData>(
            HttpMethod.Get,
            "/status",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return ToSessionStatus(status.Session);
    }

    public async Task<VaultCatalogPage> ListCatalogAsync(
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultCatalogPage>(
            HttpMethod.Get,
            BuildPath(
                "/catalog",
                ("folder", folder),
                ("cursor", cursor),
                ("limit", limit.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultSearchPage> SearchNotesAsync(
        string query,
        string? folder,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultSearchPage>(
            HttpMethod.Get,
            BuildPath(
                "/search",
                ("q", query),
                ("folder", folder),
                ("cursor", cursor),
                ("limit", limit.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultReadResult> ReadNoteAsync(
        Guid documentId,
        int offset,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultReadResult>(
            HttpMethod.Get,
            BuildPath(
                $"/notes/{documentId:D}",
                ("offset", offset.ToString()),
                ("maxChars", maxChars.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultTrashPage> ListTrashAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultTrashPage>(
            HttpMethod.Get,
            BuildPath(
                "/trash",
                ("cursor", cursor),
                ("limit", limit.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultRestoreResult> RestoreTrashAsync(
        Guid documentId,
        int expectedRevision,
        string? renamePath,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultRestoreResult>(
            HttpMethod.Post,
            $"/trash/{documentId:D}/restore",
            new HostRestoreTrashRequest(expectedRevision, renamePath),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultPurgeResult> PurgeNoteAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultPurgeResult>(
            HttpMethod.Delete,
            $"/trash/{documentId:D}",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultRevisionPage> ListRevisionsAsync(
        Guid documentId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultRevisionPage>(
            HttpMethod.Get,
            BuildPath(
                $"/notes/{documentId:D}/revisions",
                ("cursor", cursor),
                ("limit", limit.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultWriteResult> RestoreRevisionAsync(
        Guid documentId,
        Guid revisionId,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultWriteResult>(
            HttpMethod.Post,
            $"/notes/{documentId:D}/revisions/{revisionId:D}/restore",
            new HostRestoreRevisionRequest(expectedRevision),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultImportPreview> PreviewImportAsync(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultImportPreview>(
            HttpMethod.Post,
            "/import/preview",
            new HostImportPreviewRequest(sourceRoot),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultImportResult> CommitImportAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultImportResult>(
            HttpMethod.Post,
            $"/import/{jobId:D}/apply",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultExportResult> ExportSnapshotAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultExportResult>(
            HttpMethod.Post,
            "/export",
            new HostExportRequest(destination),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultBackupResult> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultBackupResult>(
            HttpMethod.Post,
            "/maintenance/backup",
            new HostBackupRequest(backupPath),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultRestoreBackupResult> RestoreBackupAsync(
        string backupPath,
        string targetPath,
        string credential,
        VaultCredentialType credentialType,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultRestoreBackupResult>(
            HttpMethod.Post,
            "/maintenance/restore-new",
            new HostRestoreNewRequest(backupPath, targetPath),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultIntegrityReport> VerifyVaultAsync(
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultIntegrityReport>(
            HttpMethod.Post,
            "/maintenance/verify",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RebuildSearchIndexAsync(
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<int>(
            HttpMethod.Post,
            "/maintenance/index/rebuild",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VaultAuditPage> GetAuditPageAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<VaultAuditPage>(
            HttpMethod.Get,
            BuildPath(
                "/audit",
                ("cursor", cursor),
                ("limit", limit.ToString())),
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task ChangePasswordAsync(
        string vaultPath,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutDataAsync(
            HttpMethod.Post,
            "/maintenance/password/change",
            body: null,
            cancellationToken);
    }

    public async Task<string> RotateRecoveryKeyAsync(
        string vaultPath,
        string currentPassword,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<HostRecoveryRotationData>(
            HttpMethod.Post,
            "/maintenance/recovery/rotate",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return data.RecoveryKey;
    }

    public Task ConfirmRecoveryKeyAsync(
        string vaultPath,
        string recoveryKey,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutDataAsync(
            HttpMethod.Post,
            "/maintenance/recovery/confirm",
            body: null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<HostClientData>> ListAgentClientsAsync(
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<IReadOnlyList<HostClientData>>(
            HttpMethod.Get,
            "/clients",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostCreateClientData> CreateAgentClientAsync(
        string clientId,
        string accessMode,
        bool allowLock,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<HostCreateClientData>(
            HttpMethod.Post,
            "/clients",
            new HostCreateClientRequest(clientId, accessMode, allowLock),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HostCreateClientData> RotateAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<HostCreateClientData>(
            HttpMethod.Post,
            $"/clients/{Uri.EscapeDataString(clientId)}/rotate",
            body: null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAgentClientAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        return SendWithoutDataAsync(
            HttpMethod.Delete,
            $"/clients/{Uri.EscapeDataString(clientId)}",
            body: null,
            cancellationToken);
    }

    public Task StopHostAsync(
        string reason = "user-requested",
        CancellationToken cancellationToken = default)
    {
        return SendWithoutDataAsync(
            HttpMethod.Post,
            "/host/stop",
            new HostStopRequest(reason),
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<HostHealthData?> ProbeHealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                "/health",
                body: null);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "A process is listening on the configured Host port, " +
                    "but it rejected the CipherVault health request.");
            }
            var payload = await response.Content.ReadFromJsonAsync<
                HostApiResponse<HostHealthData>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return payload?.Data ?? throw new VaultException(
                VaultErrorCode.StorageError,
                "CipherVault Host returned an invalid health response.");
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var health = await ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
            if (health is not null)
            {
                ValidateFingerprint(health);
                return;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new VaultException(
            VaultErrorCode.StorageError,
            "CipherVault Host did not become ready.");
    }

    private void ValidateFingerprint(HostHealthData health)
    {
        if (!string.Equals(
                health.ConfigFingerprint,
                _expectedConfigFingerprint,
                StringComparison.Ordinal))
        {
            throw new VaultException(
                VaultErrorCode.Conflict,
                "The configured Host port is owned by a different CipherVault " +
                "configuration. Stop that Host before continuing.");
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<
            HostApiResponse<T>>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            throw new VaultException(
                VaultErrorCode.StorageError,
                "Host returned an empty response.");
        }
        return payload.Data;
    }

    private async Task SendWithoutDataAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        HostApiErrorResponse? payload = null;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<
                HostApiErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }
        var message = payload?.Error.Message
            ?? $"Host request failed with HTTP {(int)response.StatusCode}.";
        throw new VaultException(
            ToVaultErrorCode(payload?.Error.Code, response.StatusCode),
            message);
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        object? body)
    {
        var request = new HttpRequestMessage(
            method,
            _adminPath + path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            _adminToken);
        request.Headers.TryAddWithoutValidation(
            "X-CipherVault-Client-Kind",
            _clientKind == HostAdminClientKind.Cli ? "cli" : "ui");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return request;
    }

    private static string BuildPath(
        string path,
        params (string Key, string? Value)[] query)
    {
        var values = query
            .Where(item => item.Value is not null)
            .Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}")
            .ToList();
        return values.Count == 0 ? path : path + "?" + string.Join("&", values);
    }

    private static VaultSessionStatus ToSessionStatus(
        HostSessionStatusData status)
    {
        var state = Enum.Parse<VaultSessionState>(
            status.State,
            ignoreCase: true);
        return new VaultSessionStatus(
            state,
            status.SessionId,
            status.StartedAt,
            status.LastUserActivityAt,
            status.IdleExpiresAt,
            status.HardExpiresAt,
            status.LockReason,
            status.RecoveryVerified);
    }

    private static VaultErrorCode ToVaultErrorCode(
        string? code,
        HttpStatusCode statusCode)
    {
        return code switch
        {
            "invalid_input" => VaultErrorCode.InvalidInput,
            "already_exists" => VaultErrorCode.AlreadyExists,
            "not_found" => VaultErrorCode.NotFound,
            "invalid_credential" or "invalid_token" =>
                VaultErrorCode.InvalidCredential,
            "conflict" => VaultErrorCode.Conflict,
            "session_locked" => VaultErrorCode.SessionLocked,
            "maintenance_in_progress" => VaultErrorCode.Conflict,
            _ => statusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    VaultErrorCode.InvalidCredential,
                _ => VaultErrorCode.StorageError
            }
        };
    }

    private static class HostProcessLauncher
    {
        public static void Start(string configPath)
        {
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                "CipherVault.Host.exe");
            if (!File.Exists(executable))
            {
                throw new VaultException(
                    VaultErrorCode.StorageError,
                    "CipherVault.Host.exe was not found.");
            }

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(configPath)
                    ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(configPath);
            _ = Process.Start(startInfo)
                ?? throw new VaultException(
                    VaultErrorCode.StorageError,
                    "CipherVault Host could not be started.");
        }
    }
}
