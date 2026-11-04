using System.Net;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Semantic;

namespace CipherVault.Server.Semantic;

public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly EmbeddingOptions options;
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public OpenAiCompatibleEmbeddingProvider(EmbeddingOptions options)
        : this(options, EmbeddingHttpClientFactory.Create(options), true)
    {
    }

    public OpenAiCompatibleEmbeddingProvider(
        EmbeddingOptions options,
        HttpClient client,
        bool ownsClient = false)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
        if (!options.IsConfigured)
        {
            throw new InvalidOperationException(
                "Embedding provider is not configured.");
        }
    }

    public EmbeddingProviderDescriptor Descriptor => options.ToDescriptor();

    public int CountInputTokens(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        // The first adapter accepts byte-tokenizing models only. UTF-8 bytes
        // are a conservative upper bound for their token count.
        return Encoding.UTF8.GetByteCount(input);
    }

    public async Task<EmbeddingResponse> EmbedAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.BaseUri is null || options.ModelId is null)
        {
            throw Unavailable("Embedding provider is not configured.");
        }
        if (request.Inputs.Count == 0
            || request.Inputs.Count > options.BatchSize)
        {
            throw new EmbeddingProviderException(
                "embedding_input_invalid",
                "Embedding batch size is invalid.");
        }
        foreach (var input in request.Inputs)
        {
            if (string.IsNullOrEmpty(input))
            {
                throw new EmbeddingProviderException(
                    "embedding_input_invalid",
                    "Embedding input must not be empty.");
            }
            if (CountInputTokens(input) > options.MaxInputTokens)
            {
                throw new EmbeddingProviderException(
                    "embedding_input_too_large",
                    "Embedding input exceeds the configured token limit.");
            }
        }

        var endpoint = new Uri(
            $"{options.BaseUri.AbsoluteUri.TrimEnd('/')}/embeddings",
            UriKind.Absolute);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(options.Timeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        model = options.ModelId,
                        input = request.Inputs
                    },
                    JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            message.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingProviderException(
                "embedding_provider_timeout",
                "Embedding provider timed out.");
        }
        catch (EmbeddingProviderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EmbeddingProviderException(
                "embedding_provider_unavailable",
                "Embedding provider request failed.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw FailureForStatus(response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw InvalidResponse();
            }
            try
            {
                await response.Content.LoadIntoBufferAsync(
                    MaxResponseBytes,
                    timeout.Token);
                await using var stream = await response.Content
                    .ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token);
                return ParseResponse(document.RootElement, request.Inputs.Count);
            }
            catch (EmbeddingProviderException)
            {
                throw;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new EmbeddingProviderException(
                    "embedding_provider_timeout",
                    "Embedding provider timed out.");
            }
            catch (Exception exception)
            {
                throw new EmbeddingProviderException(
                    "embedding_provider_invalid_response",
                    "Embedding provider returned an invalid response.",
                    exception);
            }
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }

    private EmbeddingResponse ParseResponse(
        JsonElement root,
        int expectedCount)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || !string.Equals(
                model.GetString(),
                options.ModelId,
                StringComparison.Ordinal)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() != expectedCount)
        {
            throw InvalidResponse();
        }

        var vectors = new List<float[]>(expectedCount);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array
                || embedding.GetArrayLength() != options.Dimension)
            {
                throw InvalidResponse();
            }
            var vector = new float[options.Dimension];
            var index = 0;
            foreach (var value in embedding.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetSingle(out var number)
                    || !float.IsFinite(number))
                {
                    throw InvalidResponse();
                }
                vector[index++] = number;
            }
            if (!ContainsNonZero(vector))
            {
                throw InvalidResponse();
            }
            vectors.Add(vector);
        }
        return new EmbeddingResponse(
            options.ProviderId,
            options.ModelId!,
            options.Dimension,
            vectors);
    }

    private static EmbeddingProviderException FailureForStatus(
        HttpStatusCode status) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new(
                    "embedding_provider_auth_failed",
                    "Embedding provider rejected credentials."),
            HttpStatusCode.TooManyRequests =>
                new(
                    "embedding_provider_rate_limited",
                    "Embedding provider rate limit was reached."),
            HttpStatusCode.BadRequest or
            HttpStatusCode.NotFound or
            HttpStatusCode.UnprocessableEntity =>
                new(
                    "embedding_provider_invalid_response",
                    "Embedding provider rejected the request."),
            _ => new(
                "embedding_provider_unavailable",
                "Embedding provider is unavailable.")
        };

    private static EmbeddingProviderException InvalidResponse() =>
        new(
            "embedding_provider_invalid_response",
            "Embedding provider returned an invalid response.");

    private static bool ContainsNonZero(IReadOnlyList<float> vector) =>
        vector.Any(value => value != 0);

    private static EmbeddingProviderException Unavailable(string message) =>
        new("embedding_provider_unavailable", message);
}
