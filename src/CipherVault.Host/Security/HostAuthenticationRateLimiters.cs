namespace CipherVault.Host.Security;

public sealed class HostAuthenticationRateLimiters
{
    public HostAuthenticationRateLimiters(TimeProvider? timeProvider = null)
    {
        Admin = new HostAuthenticationRateLimiter(timeProvider);
        Agent = new HostAuthenticationRateLimiter(timeProvider);
    }

    public HostAuthenticationRateLimiter Admin { get; }
    public HostAuthenticationRateLimiter Agent { get; }
}
