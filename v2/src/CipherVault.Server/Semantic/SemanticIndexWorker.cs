using CipherVault.Application.Semantic;
using CipherVault.Application.Vault;
using CipherVault.Core.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CipherVault.Server.Semantic;

public sealed class SemanticIndexWorker : BackgroundService
{
    private readonly SemanticIndexProcessor processor;
    private readonly VaultSessionManager sessionManager;
    private readonly ILogger<SemanticIndexWorker> logger;
    private readonly object sync = new();
    private CancellationTokenSource? sessionCancellation;
    private CancellationToken stoppingToken;
    private bool recovered;

    public SemanticIndexWorker(
        SemanticIndexProcessor processor,
        VaultSessionManager sessionManager,
        ILogger<SemanticIndexWorker> logger)
    {
        this.processor = processor;
        this.sessionManager = sessionManager;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        this.stoppingToken = stoppingToken;
        sessionManager.StateChanged += OnStateChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (sessionManager.State != VaultSessionState.Unlocked)
                {
                    await DelayAsync(stoppingToken);
                    continue;
                }
                var cancellation = GetSessionCancellation(stoppingToken);
                try
                {
                    if (!recovered)
                    {
                        await processor.RecoverInterruptedAsync(cancellation);
                        recovered = true;
                    }
                    var processed = await processor.ProcessOneAsync(
                        cancellation);
                    if (!processed)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(1),
                            cancellation);
                    }
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    recovered = false;
                    await DelayAsync(stoppingToken);
                }
                catch (Exception exception)
                {
                    recovered = false;
                    logger.LogWarning(
                        exception,
                        "Semantic index worker iteration failed.");
                    await DelayAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            sessionManager.StateChanged -= OnStateChanged;
            lock (sync)
            {
                sessionCancellation?.Dispose();
                sessionCancellation = null;
            }
        }
    }

    private void OnStateChanged(VaultStateChangedEvent change)
    {
        lock (sync)
        {
            if (change.Status.State == VaultSessionState.Unlocked)
            {
                recovered = false;
                sessionCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);
            }
            else
            {
                sessionCancellation?.Cancel();
                sessionCancellation?.Dispose();
                sessionCancellation = null;
                recovered = false;
            }
        }
    }

    private CancellationToken GetSessionCancellation(
        CancellationToken stoppingToken)
    {
        lock (sync)
        {
            sessionCancellation ??= CancellationTokenSource.CreateLinkedTokenSource(
                this.stoppingToken);
            return sessionCancellation.Token;
        }
    }

    private static Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
