using CipherVault.Core.Documents;

namespace CipherVault.Application.Vault;

public sealed record McpWriteModeSnapshot(
    McpWriteMode Mode,
    Guid? SessionId,
    long? SessionGeneration,
    DateTimeOffset? UpdatedAt,
    bool Effective);

internal sealed record McpWriteModeChangeOutcome<T>(
    T Result,
    bool ApplyMode);
