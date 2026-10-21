namespace CipherVault.Host.Security;

public sealed class HostAuthenticationRateLimiters
{
    private const int GlobalFailureThreshold = 50;

    public HostAuthenticationRateLimiters(TimeProvider? timeProvider = null)
    {
        Admin = new HostAuthenticationRateLimiter(
            timeProvider,
            GlobalFailureThreshold);
        Agent = new HostAuthenticationRateLimiter(
            timeProvider,
            GlobalFailureThreshold);
        AdminKeys = new HostAuthenticationKeyedRateLimiter(timeProvider);
        AgentKeys = new HostAuthenticationKeyedRateLimiter(timeProvider);
    }

    public HostAuthenticationRateLimiter Admin { get; }
    public HostAuthenticationRateLimiter Agent { get; }
    public HostAuthenticationKeyedRateLimiter AdminKeys { get; }
    public HostAuthenticationKeyedRateLimiter AgentKeys { get; }
}
