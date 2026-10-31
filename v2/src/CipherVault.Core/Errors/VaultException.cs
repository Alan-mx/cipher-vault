using System.Collections.ObjectModel;

namespace CipherVault.Core.Errors;

public sealed class VaultException : Exception
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyDetails =
        new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));

    public VaultException(
        VaultErrorCode code,
        string message,
        Exception? innerException = null,
        IReadOnlyDictionary<string, object?>? details = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details is null
            ? EmptyDetails
            : new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(details, StringComparer.Ordinal));
    }

    public VaultErrorCode Code { get; }

    public IReadOnlyDictionary<string, object?> Details { get; }
}
