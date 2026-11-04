namespace CipherVault.Core.Semantic;

public enum EmbeddingInputKind
{
    DocumentChunk,
    SearchQuery
}

public sealed record EmbeddingProviderDescriptor(
    string ProviderId,
    string ModelId,
    int Dimension,
    int MaxInputTokens,
    string EndpointFingerprint,
    bool IsRemote)
{
    public int MaxBatchSize { get; init; } = 16;
}

public sealed record EmbeddingRequest(
    EmbeddingInputKind Kind,
    IReadOnlyList<string> Inputs);

public sealed record EmbeddingResponse(
    string ProviderId,
    string ModelId,
    int Dimension,
    IReadOnlyList<float[]> Vectors);

public interface IEmbeddingProvider
{
    EmbeddingProviderDescriptor Descriptor { get; }

    int CountInputTokens(string input);

    Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken);
}

public sealed class EmbeddingProviderException : Exception
{
    public EmbeddingProviderException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
