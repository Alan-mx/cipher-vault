using CipherVault.Host.Contracts;
using CipherVault.Ipc;

namespace CipherVault.Host.Security;

public sealed class OwnerSessionRegistry
{
    private readonly object _sync = new();
    private OwnerSessionBinding? _binding;
    private int _leaseCount;

    public OwnerSessionLease Enter(VerifiedProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.SessionId == 0)
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "owner_session_required");
        }
        if (string.IsNullOrWhiteSpace(identity.UserSid) ||
            string.IsNullOrWhiteSpace(identity.LogonSid))
        {
            throw new ProcessIdentityRejectedException(
                ProcessVerificationStatus.IdentityMismatch,
                "owner_logon_identity_missing");
        }

        lock (_sync)
        {
            var candidate = new OwnerSessionBinding(
                identity.UserSid,
                identity.LogonSid,
                identity.SessionId);
            if (_leaseCount == 0)
            {
                _binding = candidate;
            }
            else if (_binding is null ||
                     !_binding.Matches(candidate))
            {
                throw new ProcessIdentityRejectedException(
                    ProcessVerificationStatus.IdentityMismatch,
                    "owner_session_mismatch");
            }
            _leaseCount++;
        }
        return new OwnerSessionLease(this);
    }

    internal void Release()
    {
        lock (_sync)
        {
            if (_leaseCount == 0)
            {
                return;
            }
            _leaseCount--;
            if (_leaseCount == 0)
            {
                _binding = null;
            }
        }
    }

    private sealed record OwnerSessionBinding(
        string UserSid,
        string LogonSid,
        uint SessionId)
    {
        public bool Matches(OwnerSessionBinding other)
        {
            return string.Equals(
                    UserSid,
                    other.UserSid,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    LogonSid,
                    other.LogonSid,
                    StringComparison.OrdinalIgnoreCase) &&
                SessionId == other.SessionId;
        }
    }
}

public sealed class OwnerSessionLease : IDisposable
{
    private OwnerSessionRegistry? _registry;

    internal OwnerSessionLease(OwnerSessionRegistry registry)
    {
        _registry = registry;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _registry, null)?.Release();
    }
}
