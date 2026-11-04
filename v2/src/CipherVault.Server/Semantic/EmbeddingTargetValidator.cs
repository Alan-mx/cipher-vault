using System.Net;
using System.Net.Sockets;
using CipherVault.Core.Semantic;

namespace CipherVault.Server.Semantic;

public static class EmbeddingTargetValidator
{
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        Uri endpoint,
        bool allowRemote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var isLoopbackHost = EmbeddingEndpointPolicy.IsLoopbackHost(
            endpoint.DnsSafeHost);
        var addresses = IPAddress.TryParse(
            endpoint.DnsSafeHost,
            out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(
                endpoint.DnsSafeHost,
                cancellationToken);
        if (addresses.Length == 0)
        {
            throw ProviderFailure(
                "embedding_endpoint_invalid",
                "Embedding endpoint did not resolve to an address.");
        }
        if (isLoopbackHost || !addresses.Any(address => !IsForbidden(address)))
        {
            if (addresses.Any(address => !IPAddress.IsLoopback(address)))
            {
                throw ProviderFailure(
                    "embedding_endpoint_invalid",
                    "Local embedding endpoints must resolve only to loopback.");
            }
            return addresses;
        }
        if (!allowRemote)
        {
            throw ProviderFailure(
                "semantic_remote_not_allowed",
                "Remote embedding is not enabled for this deployment.");
        }
        if (!string.Equals(
                endpoint.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw ProviderFailure(
                "embedding_endpoint_invalid",
                "Remote embedding endpoints must use HTTPS.");
        }
        if (addresses.Any(IsForbidden))
        {
            throw ProviderFailure(
                "embedding_endpoint_invalid",
                "Remote embedding endpoint resolved to a forbidden address.");
        }
        return addresses;
    }

    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127
                || bytes[0] >= 224
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 198 && bytes[1] is 18 or 19;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.Equals(IPAddress.IPv6Loopback)
                || address.Equals(IPAddress.IPv6Any)
                || (bytes[0] & 0xFE) == 0xFC
                || bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80
                || bytes[0] == 0xFF;
        }
        return true;
    }

    private static EmbeddingProviderException ProviderFailure(
        string code,
        string message) =>
        new(code, message);
}

public static class EmbeddingHttpClientFactory
{
    public static HttpClient Create(EmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BaseUri is null)
        {
            throw new InvalidOperationException(
                "Embedding provider is not configured.");
        }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await EmbeddingTargetValidator.ResolveAsync(
                    options.BaseUri,
                    options.AllowRemote,
                    cancellationToken);
                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(
                        address.AddressFamily,
                        SocketType.Stream,
                        ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(
                            new IPEndPoint(address, context.DnsEndPoint.Port),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception)
                        when (exception is not OperationCanceledException)
                    {
                        socket.Dispose();
                        lastError = exception;
                    }
                }
                throw new EmbeddingProviderException(
                    "embedding_provider_unavailable",
                    "Unable to connect to the embedding provider.",
                    lastError);
            }
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
