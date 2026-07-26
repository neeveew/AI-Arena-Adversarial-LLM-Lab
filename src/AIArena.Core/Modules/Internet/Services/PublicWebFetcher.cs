using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace AIArena.Core.Services;

/// <summary>
/// Fetches model-selected public web pages without allowing requests to reach
/// loopback, private, link-local, or other special-purpose networks.
/// </summary>
internal sealed class PublicWebFetcher : IDisposable
{
    internal const int DefaultMaximumBodyBytes = 4 * 1024 * 1024;
    internal const int DefaultMaximumRedirects = 5;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly Func<Uri, CancellationToken, Task> _validateDestination;
    private readonly int _maximumBodyBytes;
    private readonly int _maximumRedirects;
    private int _disposed;

    internal PublicWebFetcher()
        : this(DefaultTimeout)
    {
    }

    internal PublicWebFetcher(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive or infinite.");
        }

        SocketsHttpHandler? handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectCallback = ConnectToValidatedAddressAsync,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 64,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false
        };

        try
        {
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout
            };
            handler = null;
        }
        finally
        {
            handler?.Dispose();
        }
        _validateDestination = static async (uri, cancellationToken) =>
        {
            await PublicWebDestinationValidator.ValidateAndResolveAsync(uri, cancellationToken).ConfigureAwait(false);
        };
        _maximumBodyBytes = DefaultMaximumBodyBytes;
        _maximumRedirects = DefaultMaximumRedirects;
    }

    internal PublicWebFetcher(
        HttpMessageHandler handler,
        Func<Uri, CancellationToken, Task> validateDestination,
        int maximumBodyBytes = DefaultMaximumBodyBytes,
        int maximumRedirects = DefaultMaximumRedirects,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(validateDestination);
        if (maximumBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        }

        if (maximumRedirects < 0 || maximumRedirects > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRedirects));
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? DefaultTimeout
        };
        _validateDestination = validateDestination;
        _maximumBodyBytes = maximumBodyBytes;
        _maximumRedirects = maximumRedirects;
    }

    internal Task<PublicWebFetchResult> FetchAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        return FetchAsync(uri, _maximumBodyBytes, cancellationToken);
    }

    internal Task<PublicWebFetchResult> FetchAsync(
        string url,
        int maximumBodyBytes,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        }

        return FetchAsync(uri, maximumBodyBytes, cancellationToken);
    }

    internal async Task<PublicWebFetchResult> FetchAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        return await FetchAsync(uri, _maximumBodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublicWebFetchResult> FetchAsync(
        Uri uri,
        int maximumBodyBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(uri);
        if (maximumBodyBytes <= 0 || maximumBodyBytes > _maximumBodyBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        }

        var currentUri = uri;
        for (var redirectCount = 0; ;)
        {
            // Validate and resolve before every request. The connect callback resolves
            // again immediately before opening the socket and pins that connection to
            // one of the newly validated addresses.
            await _validateDestination(currentUri, cancellationToken).ConfigureAwait(false);

            using var request = CreateRequest(currentUri);
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                if (redirectCount >= _maximumRedirects)
                {
                    throw new HttpRequestException($"The response exceeded the limit of {_maximumRedirects} redirects.");
                }

                currentUri = ResolveRedirect(currentUri, response.Headers.Location);
                PublicWebDestinationValidator.ValidateUri(currentUri);
                redirectCount++;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (!BoundedTextContentReader.IsSupportedTextMediaType(response.Content.Headers.ContentType))
            {
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "missing";
                throw new HttpRequestException($"The response media type '{mediaType}' is not supported for public web fetches.");
            }

            var content = await BoundedTextContentReader.ReadAsync(
                    response.Content,
                    maximumBodyBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            return new PublicWebFetchResult(
                currentUri,
                content,
                response.Content.Headers.ContentType?.MediaType ?? "text/plain");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.8));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.8));
        request.Headers.UserAgent.ParseAdd("AI-Arena/1.0");
        return request;
    }

    private static Uri ResolveRedirect(Uri currentUri, Uri location)
    {
        try
        {
            return location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }
        catch (UriFormatException exception)
        {
            throw new HttpRequestException("The server returned an invalid redirect URL.", exception);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static async ValueTask<Stream> ConnectToValidatedAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await PublicWebDestinationValidator.ResolveAndValidateHostAsync(
                endpoint.Host,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Socket? socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return stream;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw new HttpRequestException("A connection could not be established to any validated public address.", lastFailure);
    }
}

internal sealed record PublicWebFetchResult(Uri FinalUri, string Content, string MediaType);

/// <summary>
/// Public-destination validation shared by preflight checks and socket creation.
/// Every DNS response must contain only public addresses; mixed public/private
/// answers are rejected instead of selecting the public subset.
/// </summary>
internal static class PublicWebDestinationValidator
{
    private static readonly (byte[] Prefix, int Length)[] SpecialIpv4Ranges =
    [
        ([0, 0, 0, 0], 8),
        ([10, 0, 0, 0], 8),
        ([100, 64, 0, 0], 10),
        ([127, 0, 0, 0], 8),
        ([169, 254, 0, 0], 16),
        ([172, 16, 0, 0], 12),
        ([192, 0, 0, 0], 24),
        ([192, 0, 2, 0], 24),
        ([192, 31, 196, 0], 24),
        ([192, 52, 193, 0], 24),
        ([192, 88, 99, 0], 24),
        ([192, 168, 0, 0], 16),
        ([192, 175, 48, 0], 24),
        ([198, 18, 0, 0], 15),
        ([198, 51, 100, 0], 24),
        ([203, 0, 113, 0], 24),
        ([224, 0, 0, 0], 4),
        ([240, 0, 0, 0], 4)
    ];

    private static readonly (byte[] Prefix, int Length)[] SpecialIpv6Ranges =
    [
        // IETF protocol assignments, Teredo, benchmarking, ORCHID, and related
        // special-purpose allocations under 2001:0000::/23.
        ([0x20, 0x01, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 23),
        // Documentation and deprecated 6to4 ranges.
        ([0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 32),
        ([0x20, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 16),
        // RFC 9637 documentation prefix.
        ([0x3f, 0xff, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 20),
        // AS112 direct-delegation service prefix.
        ([0x26, 0x20, 0x00, 0x4f, 0x80, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 48)
    ];

    internal static async Task<IPAddress[]> ValidateAndResolveAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ValidateUri(uri);
        return await ResolveAndValidateHostAsync(NormalizeHost(uri.DnsSafeHost), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IPAddress[]> ResolveAndValidateHostAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        host = NormalizeHost(host);
        ValidateHost(host);

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (!IsPublicAddress(literalAddress))
            {
                throw new HttpRequestException("The destination IP address is not public.");
            }

            return [literalAddress];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return ValidateResolvedAddresses(host, addresses);
    }

    internal static IPAddress[] ValidateResolvedAddresses(string host, IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var resolved = addresses.Distinct().ToArray();
        if (resolved.Length == 0)
        {
            throw new HttpRequestException($"The destination host '{host}' did not resolve to an IP address.");
        }

        if (resolved.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException($"The destination host '{host}' resolved to a non-public or special-purpose IP address.");
        }

        return resolved;
    }

    internal static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new HttpRequestException("The destination URL must be absolute.");
        }

        if (uri.AbsoluteUri.Length > 4096)
        {
            throw new HttpRequestException("The destination URL exceeds the 4096-character limit.");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("Only HTTP and HTTPS destination URLs are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new HttpRequestException("Destination URLs containing user information are not allowed.");
        }

        ValidateHost(NormalizeHost(uri.DnsSafeHost));
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !SpecialIpv4Ranges.Any(range => IsInPrefix(bytes, range.Prefix, range.Length));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId != 0)
        {
            return false;
        }

        var ipv6Bytes = address.GetAddressBytes();
        // Publicly routed IPv6 unicast currently comes from 2000::/3. Requiring
        // that allocation also rejects unspecified, loopback, NAT64, ULA,
        // link-local, site-local, and multicast addresses in one conservative rule.
        if (!IsInPrefix(ipv6Bytes, [0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], 3))
        {
            return false;
        }

        return !SpecialIpv6Ranges.Any(range => IsInPrefix(ipv6Bytes, range.Prefix, range.Length));
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new HttpRequestException("The destination URL must contain a host.");
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (!IsPublicAddress(address))
            {
                throw new HttpRequestException("The destination IP address is not public.");
            }

            return;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("Local destination host names are not allowed.");
        }

        if (!host.Contains('.', StringComparison.Ordinal))
        {
            throw new HttpRequestException("Single-label destination host names are not allowed.");
        }
    }

    private static string NormalizeHost(string host)
    {
        return host.Trim().TrimEnd('.');
    }

    private static bool IsInPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int prefixLength)
    {
        if (address.Length != prefix.Length || prefixLength < 0 || prefixLength > address.Length * 8)
        {
            return false;
        }

        var wholeBytes = prefixLength / 8;
        if (!address[..wholeBytes].SequenceEqual(prefix[..wholeBytes]))
        {
            return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[wholeBytes] & mask) == (prefix[wholeBytes] & mask);
    }
}

/// <summary>
/// Reads textual HTTP content through a hard byte ceiling. This helper is
/// intentionally independent of public-destination validation so the local
/// SearXNG JSON client can use the same decompressed-body bound.
/// </summary>
internal static class BoundedTextContentReader
{
    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html",
        "text/plain",
        "application/xhtml+xml",
        "application/json",
        "application/xml",
        "text/xml"
    };

    internal static bool IsSupportedTextMediaType(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || SupportedMediaTypes.Contains(mediaType)
            || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string> ReadAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "The maximum body size must be positive.");
        }

        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new HttpRequestException($"The response body exceeds the {maximumBytes}-byte limit.");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(
            content.Headers.ContentLength is > 0 and <= int.MaxValue
                ? (int)Math.Min(content.Headers.ContentLength.Value, maximumBytes)
                : Math.Min(maximumBytes, 64 * 1024));

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var totalBytes = 0;
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead > maximumBytes - totalBytes)
                {
                    throw new HttpRequestException($"The decompressed response body exceeds the {maximumBytes}-byte limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        destination.Position = 0;
        using var reader = new StreamReader(
            destination,
            ResolveEncoding(content.Headers.ContentType),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Encoding ResolveEncoding(MediaTypeHeaderValue? contentType)
    {
        var charset = contentType?.CharSet?.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
