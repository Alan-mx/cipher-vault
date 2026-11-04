using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CipherVault.Core.Semantic;
using Microsoft.Extensions.Configuration;

namespace CipherVault.Server.Semantic;

public sealed record EmbeddingOptions(
    bool IsConfigured,
    string ProviderId,
    Uri? BaseUri,
    string? ModelId,
    int Dimension,
    int MaxInputTokens,
    int ProfileVersion,
    bool AllowRemote,
    string? ApiKey,
    TimeSpan Timeout,
    int BatchSize)
{
    public const string OpenAiCompatibleProviderId = "openai-compatible";
    public const string ApiKeyEnvironmentVariable =
        "CIPHERVAULT_EMBEDDING_API_KEY";
    public const int DefaultBatchSize = 16;
    public const int DefaultProfileVersion = 1;
    public const int DefaultTimeoutSeconds = 30;

    public static EmbeddingOptions Disabled { get; } = new(
        false,
        OpenAiCompatibleProviderId,
        null,
        null,
        0,
        0,
        DefaultProfileVersion,
        false,
        null,
        TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        DefaultBatchSize);

    public bool IsRemote =>
        BaseUri is not null
        && !EmbeddingEndpointPolicy.IsLoopbackHost(BaseUri.DnsSafeHost);

    public bool RemoteAllowed => !IsRemote || AllowRemote;

    public string EndpointFingerprint =>
        BaseUri is null
            ? string.Empty
            : EmbeddingEndpointPolicy.Fingerprint(BaseUri);

    public string EndpointDisplay =>
        BaseUri is null
            ? string.Empty
            : BaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public EmbeddingProviderDescriptor ToDescriptor() =>
        new(
            ProviderId,
            ModelId ?? string.Empty,
            Dimension,
            MaxInputTokens,
            EndpointFingerprint,
            IsRemote)
        {
            MaxBatchSize = BatchSize
        };

    public static EmbeddingOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var provider = configuration["CIPHERVAULT_EMBEDDING_PROVIDER"];
        var baseUrl = configuration["CIPHERVAULT_EMBEDDING_BASE_URL"];
        var modelId = configuration["CIPHERVAULT_EMBEDDING_MODEL"];
        var dimensionText = configuration["CIPHERVAULT_EMBEDDING_DIMENSION"];
        var maxTokensText =
            configuration["CIPHERVAULT_EMBEDDING_MAX_INPUT_TOKENS"];
        var anyProviderSetting = new[]
        {
            provider,
            baseUrl,
            modelId,
            dimensionText,
            maxTokensText
        }.Any(value => !string.IsNullOrWhiteSpace(value));
        if (!anyProviderSetting)
        {
            return Disabled;
        }
        if (!string.Equals(
                provider?.Trim(),
                OpenAiCompatibleProviderId,
                StringComparison.Ordinal))
        {
            throw Invalid("Embedding provider must be openai-compatible.");
        }
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var endpoint)
            || string.IsNullOrEmpty(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw Invalid("Embedding base URL is invalid.");
        }
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw Invalid("Embedding model is required.");
        }
        var dimension = ParsePositive(
            dimensionText,
            "CIPHERVAULT_EMBEDDING_DIMENSION",
            1,
            65_536);
        var maxTokens = ParsePositive(
            maxTokensText,
            "CIPHERVAULT_EMBEDDING_MAX_INPUT_TOKENS",
            1,
            1_048_576);
        var profileVersion = ParsePositive(
            configuration["CIPHERVAULT_EMBEDDING_PROFILE_VERSION"],
            "CIPHERVAULT_EMBEDDING_PROFILE_VERSION",
            1,
            1_000_000,
            DefaultProfileVersion);
        var timeoutSeconds = ParsePositive(
            configuration["CIPHERVAULT_EMBEDDING_TIMEOUT_SECONDS"],
            "CIPHERVAULT_EMBEDDING_TIMEOUT_SECONDS",
            1,
            120,
            DefaultTimeoutSeconds);
        var batchSize = ParsePositive(
            configuration["CIPHERVAULT_EMBEDDING_BATCH_SIZE"],
            "CIPHERVAULT_EMBEDDING_BATCH_SIZE",
            1,
            32,
            DefaultBatchSize);
        var allowRemote = ParseBoolean(
            configuration["CIPHERVAULT_EMBEDDING_ALLOW_REMOTE"],
            "CIPHERVAULT_EMBEDDING_ALLOW_REMOTE");
        var isRemote = !EmbeddingEndpointPolicy.IsLoopbackHost(
            endpoint.DnsSafeHost);
        if (isRemote
            && !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Remote embedding endpoints must use HTTPS.");
        }
        if (!isRemote
            && !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Local embedding endpoints must use HTTP or HTTPS.");
        }

        return new EmbeddingOptions(
            true,
            OpenAiCompatibleProviderId,
            endpoint,
            modelId.Trim(),
            dimension,
            maxTokens,
            profileVersion,
            allowRemote,
            Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable),
            TimeSpan.FromSeconds(timeoutSeconds),
            batchSize);
    }

    private static int ParsePositive(
        string? value,
        string name,
        int minimum,
        int maximum,
        int? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (defaultValue is not null)
            {
                return defaultValue.Value;
            }
            throw Invalid($"{name} is required.");
        }
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw Invalid($"{name} is invalid.");
        }
        return parsed;
    }

    private static bool ParseBoolean(string? value, string name) =>
        value switch
        {
            null or "" => false,
            "true" => true,
            "false" => false,
            _ => throw Invalid($"{name} must be true or false.")
        };

    private static InvalidOperationException Invalid(string message) =>
        new(message);
}

public static class EmbeddingEndpointPolicy
{
    public static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var address)
            && System.Net.IPAddress.IsLoopback(address));

    public static string Fingerprint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException(
                "Endpoint must not contain userinfo, query or fragment.",
                nameof(endpoint));
        }
        var host = endpoint.Host.ToLowerInvariant();
        var defaultPort =
            string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            && endpoint.Port == 443
            || string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            && endpoint.Port == 80;
        var authority = defaultPort ? host : $"{host}:{endpoint.Port}";
        var path = endpoint.AbsolutePath.TrimEnd('/');
        var canonical =
            $"{endpoint.Scheme.ToLowerInvariant()}://{authority}{path}";
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
