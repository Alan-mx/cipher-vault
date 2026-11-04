using CipherVault.Application.Vault;
using CipherVault.Application.Audit;
using CipherVault.Application.Drafts;
using CipherVault.Application.Backup;
using CipherVault.Application.Documents;
using CipherVault.Application.Exports;
using CipherVault.Application.Graph;
using CipherVault.Application.Import;
using CipherVault.Application.Knowledge;
using CipherVault.Application.RelatedKnowledge;
using CipherVault.Application.Search;
using CipherVault.Application.Semantic;
using CipherVault.Core.Semantic;
using CipherVault.Server.Api;
using CipherVault.Server.Api.Endpoints;
using CipherVault.Server.Backup;
using CipherVault.Server.Middleware;
using CipherVault.Server.Mcp;
using CipherVault.Server.Security;
using CipherVault.Server.Semantic;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Hosting;

public static class ServerWebApplication
{
    public static async Task<WebApplication> BuildAsync(
        ServerOptions options,
        VaultSessionManager sessionManager,
        McpTokenStore tokenStore,
        TimeProvider timeProvider,
        Action<ILoggingBuilder>? configureLogging = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ServerWebApplication).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = webRoot
        });
        builder.WebHost.UseUrls(
            options.ListenAddresses.Select(address => address.Url).ToArray());
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            server.AddServerHeader = false;
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        configureLogging?.Invoke(builder.Logging);
        var embeddingOptions = EmbeddingOptions.Load(builder.Configuration);

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(
            ApiJson.Configure);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Paths);
        builder.Services.AddSingleton(new BackupStorageOptions(
            options.Paths.BackupDirectory,
            options.Paths.BackupLockPath));
        builder.Services.AddSingleton(options.GitBackup);
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton(embeddingOptions);
        if (embeddingOptions.IsConfigured)
        {
            builder.Services.AddSingleton<IEmbeddingProvider>(_ =>
                new OpenAiCompatibleEmbeddingProvider(embeddingOptions));
        }
        builder.Services.AddSingleton(sessionManager);
        builder.Services.AddSingleton(tokenStore);
        builder.Services.AddSingleton<VaultLifecycleService>();
        builder.Services.AddSingleton<IdempotencyService>();
        builder.Services.AddSingleton<McpWriteModeService>();
        builder.Services.AddSingleton<DocumentDraftService>();
        builder.Services.AddSingleton<DocumentService>();
        builder.Services.AddSingleton<DocumentAuthoringService>();
        builder.Services.AddSingleton<DocumentRevisionService>();
        builder.Services.AddSingleton<TrashService>();
        builder.Services.AddSingleton<SearchService>();
        builder.Services.AddSingleton(provider =>
            new SemanticIndexService(
                provider.GetRequiredService<VaultSessionManager>(),
                provider.GetService<IEmbeddingProvider>(),
                embeddingOptions.ProfileVersion,
                embeddingOptions.AllowRemote,
                embeddingOptions.IsConfigured
                    ? embeddingOptions.EndpointDisplay
                    : null,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IdempotencyService>()));
        builder.Services.AddSingleton<SemanticIndexProcessor>();
        builder.Services.AddSingleton<SemanticSearchService>();
        builder.Services.AddHostedService<SemanticIndexWorker>();
        builder.Services.AddSingleton<GraphService>();
        builder.Services.AddSingleton<KnowledgeViewService>();
        builder.Services.AddSingleton<RelatedKnowledgeService>();
        builder.Services.AddSingleton<ImportService>();
        builder.Services.AddSingleton<AuditQueryService>();
        builder.Services.AddSingleton<UnlockAttemptLimiter>();
        builder.Services.AddSingleton<ActivityTouchLimiter>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<
            IPlaintextExportAuditFailureSink,
            PlaintextExportAuditFailureSink>();
        builder.Services.AddSingleton(provider =>
            new PlaintextExportService(
                provider.GetRequiredService<VaultSessionManager>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IPlaintextExportAuditFailureSink>()));
        builder.Services.AddSingleton<ExportDownloadTicketStore>();
        builder.Services.AddSingleton<GitBackupStateStore>();
        builder.Services.AddSingleton<IGitCommandRunner, GitCommandRunner>();
        builder.Services.AddSingleton<IGitBackupRepository, GitBackupRepository>();
        builder.Services.AddSingleton<GitBackupCoordinator>();
        builder.Services.AddSingleton<IDocumentMutationObserver>(
            provider => provider.GetRequiredService<GitBackupCoordinator>());
        builder.Services.AddHostedService(
            provider => provider.GetRequiredService<GitBackupCoordinator>());
        builder.Services.AddCipherVaultDataProtection(options.Paths);
        builder.Services.AddVaultCookieAuthentication();
        builder.Services.AddVaultAntiforgery();
        builder.Services.AddCipherVaultMcp();
        builder.Services.AddHostedService<VaultIdleLockService>();
        builder.Services.AddHostedService<DocumentDraftCleanupService>();
        var app = builder.Build();
        app.UseMiddleware<RequestCorrelationMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<ApiStatusCodeMiddleware>();
        app.UseMiddleware<HostOriginValidationMiddleware>();
        app.UseMiddleware<McpBearerGateMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSpaFallback(webRoot);
        app.MapVaultEndpoints();
        app.MapVersionEndpoints();
        app.MapDocumentEndpoints();
        app.MapDraftEndpoints();
        app.MapTrashEndpoints();
        app.MapRevisionEndpoints();
        app.MapKnowledgeEndpoints();
        app.MapSemanticIndexEndpoints();
        app.MapImportEndpoints();
        app.MapAuditEndpoints();
        app.MapBackupEndpoints();
        app.MapExportEndpoints();
        app.MapMcpTokenEndpoints();
        app.MapCipherVaultMcp();
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static void UseSpaFallback(
        this WebApplication app,
        string webRoot)
    {
        var indexPath = Path.Combine(webRoot, "index.html");
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.HasStarted
                || context.Response.StatusCode != StatusCodes.Status404NotFound
                || context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/mcp")
                || (!HttpMethods.IsGet(context.Request.Method)
                    && !HttpMethods.IsHead(context.Request.Method))
                || Path.HasExtension(context.Request.Path.Value)
                || !File.Exists(indexPath))
            {
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(
                indexPath,
                context.RequestAborted);
        });
    }
}
