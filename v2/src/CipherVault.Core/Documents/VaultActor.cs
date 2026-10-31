using CipherVault.Core.Errors;

namespace CipherVault.Core.Documents;

public enum VaultActorKind
{
    UserUi,
    Mcp,
    System
}

public sealed record VaultActorContext
{
    public VaultActorContext(
        VaultActorKind kind,
        string? correlationId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Actor kind is invalid.");
        }

        if (correlationId?.Length > 128)
        {
            throw new VaultException(
                VaultErrorCode.InvalidInput,
                "Correlation ID must not exceed 128 characters.");
        }

        Kind = kind;
        CorrelationId = correlationId;
    }

    public VaultActorKind Kind { get; }

    public string? CorrelationId { get; }
}
