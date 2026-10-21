namespace CipherVault.Host.Contracts;

public sealed class HostException : Exception
{
    public HostException(HostErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public HostException(HostErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public HostErrorCode Code { get; }
}
