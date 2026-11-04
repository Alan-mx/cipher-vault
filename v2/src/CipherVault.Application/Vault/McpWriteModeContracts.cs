using CipherVault.Core.Documents;

namespace CipherVault.Application.Vault;

public sealed record McpWriteModeStatus(
    McpWriteMode Mode,
    Guid? SessionId,
    long? SessionGeneration,
    DateTimeOffset? UpdatedAt,
    bool Effective);

public sealed record McpWriteModeChangeRequest(McpWriteMode Mode);
