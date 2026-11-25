using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace CodexBarWin.Services.Native;

/// <summary>
/// Shared HTTP plumbing for the native usage clients: one pooled handler, no cookies, no redirects (a redirect could
/// carry the bearer token to another host) and an explicit host allow list.
/// </summary>
public static class NativeHttp
{
    /// <summary>Every host the native clients may contact.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.anthropic.com",
        "chatgpt.com",
        "cloudcode-pa.googleapis.com",
        "daily-cloudcode-pa.googleapis.com"
    };

    /// <summary>
    /// Creates the single HttpClient the app should reuse for all three providers. Idle connections close after
    /// <paramref name="idleTimeout"/> (default 90 s), so a 5-minute refresh does not keep sockets open in between.
    /// </summary>
    public static HttpClient CreateClient(TimeSpan? idleTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(90),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        return new HttpClient(new HostAllowListHandler(handler), disposeHandler: true)
        {
            // Per-request deadlines come from the callers' linked cancellation tokens.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Parses a Retry-After header (delta seconds or HTTP date).</summary>
    public static DateTimeOffset? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta >= TimeSpan.Zero) return now + delta;
        if (header?.Date is { } date) return date;
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    return now.AddSeconds(seconds);
            }
        }
        return null;
    }

    internal static void SetBearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    internal static void SetUserAgent(HttpRequestMessage request, string userAgent) =>
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

    /// <summary>Linked token that also fires after <paramref name="timeout"/>.</summary>
    internal static CancellationTokenSource Deadline(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>Refuses any request to a host outside <see cref="AllowedHosts"/> or over plain HTTP.</summary>
    public sealed class HostAllowListHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host))
                throw new InvalidOperationException($"Native usage request to a host outside the allow list: {uri?.Host}");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
