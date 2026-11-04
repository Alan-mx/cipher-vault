using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CipherVault.Core.Semantic;
using CipherVault.Server.Semantic;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Tests;

public sealed class EmbeddingProviderTests
{
    [Fact]
    public void EmbeddingOptionsLoadLocalProfileAndRejectIncompleteConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_EMBEDDING_PROVIDER"] = "openai-compatible",
                ["CIPHERVAULT_EMBEDDING_BASE_URL"] =
                    "http://127.0.0.1:1234/v1/",
                ["CIPHERVAULT_EMBEDDING_MODEL"] = "local-embed",
                ["CIPHERVAULT_EMBEDDING_DIMENSION"] = "4",
                ["CIPHERVAULT_EMBEDDING_MAX_INPUT_TOKENS"] = "2048"
            })
            .Build();

        var options = EmbeddingOptions.Load(configuration);

        Assert.True(options.IsConfigured);
        Assert.False(options.IsRemote);
        Assert.True(options.RemoteAllowed);
        Assert.Equal(4, options.Dimension);
        Assert.Equal(2048, options.MaxInputTokens);
        Assert.Equal(
            EmbeddingEndpointPolicy.Fingerprint(
                new Uri("http://127.0.0.1:1234/v1")),
            options.EndpointFingerprint);

        var incomplete = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_EMBEDDING_PROVIDER"] = "openai-compatible",
                ["CIPHERVAULT_EMBEDDING_BASE_URL"] = "http://127.0.0.1:1234/v1"
            })
            .Build();
        Assert.Throws<InvalidOperationException>(
            () => EmbeddingOptions.Load(incomplete));
    }

    [Fact]
    public void RemoteHttpEndpointAndForbiddenAddressesAreRejected()
    {
        var httpConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_EMBEDDING_PROVIDER"] = "openai-compatible",
                ["CIPHERVAULT_EMBEDDING_BASE_URL"] = "http://example.com/v1",
                ["CIPHERVAULT_EMBEDDING_MODEL"] = "remote-embed",
                ["CIPHERVAULT_EMBEDDING_DIMENSION"] = "4",
                ["CIPHERVAULT_EMBEDDING_MAX_INPUT_TOKENS"] = "2048",
                ["CIPHERVAULT_EMBEDDING_ALLOW_REMOTE"] = "true"
            })
            .Build();
        Assert.Throws<InvalidOperationException>(
            () => EmbeddingOptions.Load(httpConfig));

        Assert.True(EmbeddingTargetValidator.IsForbidden(IPAddress.Loopback));
        Assert.True(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("10.0.0.1")));
        Assert.True(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("172.16.0.1")));
        Assert.True(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("192.168.1.1")));
        Assert.True(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("100.64.0.1")));
        Assert.True(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("169.254.1.1")));
        Assert.False(
            EmbeddingTargetValidator.IsForbidden(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void RemoteDefaultsOffAndProfileIdentityChangesWithEndpoint()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CIPHERVAULT_EMBEDDING_PROVIDER"] = "openai-compatible",
                ["CIPHERVAULT_EMBEDDING_BASE_URL"] =
                    "https://embedding.example.com/v1",
                ["CIPHERVAULT_EMBEDDING_MODEL"] = "remote-embed",
                ["CIPHERVAULT_EMBEDDING_DIMENSION"] = "4",
                ["CIPHERVAULT_EMBEDDING_MAX_INPUT_TOKENS"] = "2048",
                ["CIPHERVAULT_EMBEDDING_API_KEY"] = "must-not-leak"
            })
            .Build();

        var options = EmbeddingOptions.Load(configuration);
        var firstProfile = SemanticProfileIdentity.CreateProfileId(
            options.ToDescriptor(),
            options.ProfileVersion);
        var changedEndpoint = options with
        {
            BaseUri = new Uri("https://embedding2.example.com/v1")
        };
        var secondProfile = SemanticProfileIdentity.CreateProfileId(
            changedEndpoint.ToDescriptor(),
            options.ProfileVersion);

        Assert.True(options.IsRemote);
        Assert.False(options.AllowRemote);
        Assert.False(options.RemoteAllowed);
        Assert.NotEqual(firstProfile, secondProfile);
        Assert.DoesNotContain(
            "must-not-leak",
            options.EndpointDisplay,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must-not-leak",
            options.ToDescriptor().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAICompatibleProviderParsesVectorsAndSendsApiKey()
    {
        var handler = new StubHandler(async request =>
        {
            Assert.Equal(
                "Bearer test-key",
                request.Headers.Authorization?.ToString());
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync());
            Assert.Equal(
                "local-embed",
                body.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                2,
                body.RootElement.GetProperty("input").GetArrayLength());
            return JsonResponse(
                """
                {
                  "model": "local-embed",
                  "data": [
                    { "embedding": [1, 2, 3, 4] },
                    { "embedding": [0.5, 0.25, 0.125, 0.0625] }
                  ]
                }
                """);
        });
        using var httpClient = new HttpClient(handler);
        using var provider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(apiKey: "test-key"),
            httpClient);

        var result = await provider.EmbedAsync(
            new EmbeddingRequest(
                EmbeddingInputKind.DocumentChunk,
                ["alpha", "beta"]),
            CancellationToken.None);

        Assert.Equal("local-embed", result.ModelId);
        Assert.Equal(4, result.Dimension);
        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal([1f, 2f, 3f, 4f], result.Vectors[0]);
    }

    [Fact]
    public async Task ProviderRejectsOversizedInputAndMismatchedResponses()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """
            {
              "model": "different-model",
              "data": [{ "embedding": [1, 2, 3, 4] }]
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var provider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(maxInputTokens: 4),
            httpClient);

        var tooLarge = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    ["abcdef"]),
                CancellationToken.None));
        Assert.Equal("embedding_input_too_large", tooLarge.Code);

        var invalid = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    ["abc"]),
                CancellationToken.None));
        Assert.Equal("embedding_provider_invalid_response", invalid.Code);
    }

    [Fact]
    public async Task ProviderRejectsZeroVectorsAndMapsRateLimits()
    {
        var zeroHandler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """
            {
              "model": "local-embed",
              "data": [{ "embedding": [0, 0, 0, 0] }]
            }
            """)));
        using var zeroClient = new HttpClient(zeroHandler);
        using var zeroProvider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(),
            zeroClient);

        var zero = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => zeroProvider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    ["abc"]),
                CancellationToken.None));
        Assert.Equal("embedding_provider_invalid_response", zero.Code);

        var limitedHandler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using var limitedClient = new HttpClient(limitedHandler);
        using var limitedProvider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(),
            limitedClient);

        var limited = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => limitedProvider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    ["abc"]),
                CancellationToken.None));
        Assert.Equal("embedding_provider_rate_limited", limited.Code);
    }

    [Fact]
    public async Task ProviderMapsCancellationTimeoutWithoutLeakingRequestData()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var httpClient = new HttpClient(handler);
        using var provider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(timeout: TimeSpan.FromMilliseconds(25)),
            httpClient);

        var failure = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => provider.EmbedAsync(
                new EmbeddingRequest(
                    EmbeddingInputKind.SearchQuery,
                    ["private query"]),
                CancellationToken.None));

        Assert.Equal("embedding_provider_timeout", failure.Code);
        Assert.DoesNotContain("private query", failure.ToString());
    }

    [Fact]
    public async Task ProviderInteroperatesWithLoopbackHttpEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var requestLine = await reader.ReadLineAsync();
            Assert.Equal(
                "POST /v1/embeddings HTTP/1.1",
                requestLine);
            var contentLength = 0;
            while (await reader.ReadLineAsync() is { Length: > 0 } line)
            {
                if (line.StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            var body = new char[contentLength];
            var offset = 0;
            while (offset < body.Length)
            {
                var read = await reader.ReadAsync(
                    body.AsMemory(offset, body.Length - offset));
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }
            using var requestJson = JsonDocument.Parse(
                new string(body, 0, offset));
            Assert.Equal(
                "local-embed",
                requestJson.RootElement.GetProperty("model").GetString());
            Assert.Equal(
                "alpha",
                requestJson.RootElement
                    .GetProperty("input")[0]
                    .GetString());

            var payload = Encoding.UTF8.GetBytes(
                """
                {
                  "model": "local-embed",
                  "data": [{ "embedding": [1, 2, 3, 4] }]
                }
                """);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {payload.Length}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(payload);
        });
        using var provider = new OpenAiCompatibleEmbeddingProvider(
            CreateOptions(
                baseUri: new Uri(
                    $"http://127.0.0.1:{port}/v1")));

        var result = await provider.EmbedAsync(
            new EmbeddingRequest(
                EmbeddingInputKind.SearchQuery,
                ["alpha"]),
            CancellationToken.None);

        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, result.Dimension);
        Assert.Single(result.Vectors);
    }

    private static EmbeddingOptions CreateOptions(
        string? apiKey = null,
        int maxInputTokens = 2048,
        TimeSpan? timeout = null,
        Uri? baseUri = null) =>
        new(
            true,
            "openai-compatible",
            baseUri ?? new Uri("http://127.0.0.1:1234/v1"),
            "local-embed",
            4,
            maxInputTokens,
            1,
            false,
            apiKey,
            timeout ?? TimeSpan.FromSeconds(30),
            16);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> handler;

        public StubHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            this.handler = (request, _) => handler(request);
        }

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
                handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
