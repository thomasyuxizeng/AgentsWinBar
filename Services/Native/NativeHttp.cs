using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AgentsWinBar.Services.Native;

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

    /// <summary>The longest wait a Retry-After header can ask for; anything longer (or unreadable) is cut to this.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    /// <summary>The largest usage response body read (the real ones are a few KB).</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Creates the single HttpClient the app should reuse for all three providers. Idle connections close after
    /// <paramref name="idleTimeout"/> (default 90 s), so a 5-minute refresh does not keep sockets open in between.
    /// </summary>
    public static HttpClient CreateClient(TimeSpan? idleTimeout = null) =>
        Wrap(new HostAllowListHandler(CreateSocketsHandler(idleTimeout)));

#if DEBUG
    /// <summary>
    /// Debug test hook: the same client, also allowed to reach the given http://127.0.0.1:&lt;port&gt;/ origins (the fake
    /// provider server). Release builds have no such overload.
    /// </summary>
    internal static HttpClient CreateClient(IReadOnlyCollection<Uri> loopbackOrigins, TimeSpan? idleTimeout = null) =>
        Wrap(new HostAllowListHandler(CreateSocketsHandler(idleTimeout), loopbackOrigins));
#endif

    private static SocketsHttpHandler CreateSocketsHandler(TimeSpan? idleTimeout) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionIdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(90),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };

    private static HttpClient Wrap(HostAllowListHandler handler) => new(handler, disposeHandler: true)
    {
        // Per-request deadlines come from the callers' linked cancellation tokens.
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// Parses a Retry-After header (delta seconds or HTTP date). Never throws: a wait longer than
    /// <see cref="MaxRetryAfter"/> (including absurd or infinite values) is cut to it, and NaN or negative values are ignored.
    /// </summary>
    public static DateTimeOffset? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta && delta >= TimeSpan.Zero) return Bounded(delta, now);
        if (header?.Date is { } date) return date - now > MaxRetryAfter ? now + MaxRetryAfter : date;
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            foreach (var raw in values)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                    return seconds >= MaxRetryAfter.TotalSeconds ? now + MaxRetryAfter : now.AddSeconds(seconds);
            }
        }
        return null;

        static DateTimeOffset Bounded(TimeSpan delta, DateTimeOffset now) => now + (delta > MaxRetryAfter ? MaxRetryAfter : delta);
    }

    /// <summary>
    /// Deserializes a response body, reading at most <see cref="MaxResponseBytes"/> (after decompression). A larger body
    /// fails with <see cref="IOException"/>, which the clients report as a failed read.
    /// </summary>
    internal static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new IOException("Usage response is larger than expected.");
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var limited = new LimitedReadStream(body, MaxResponseBytes);
        return await JsonSerializer.DeserializeAsync(limited, type, ct).ConfigureAwait(false);
    }

    internal static void ValidateBearerToken(string token)
    {
        if (token.AsSpan().ContainsAnyExceptInRange('!', '~'))
            throw new InvalidDataException("The sign-in contains a malformed access token.");
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

    /// <summary>
    /// Refuses any request that is not HTTPS to a host in <see cref="AllowedHosts"/> on the default port without user
    /// info, on both the asynchronous and the synchronous send path.
    /// </summary>
    public sealed class HostAllowListHandler : DelegatingHandler
    {
        public HostAllowListHandler(HttpMessageHandler inner) : base(inner)
        {
        }

#if DEBUG
        private readonly HashSet<(string Host, int Port)> _loopback = [];

        /// <summary>
        /// Debug test hook: also lets plain-http requests through to exactly these http://127.0.0.1:&lt;port&gt;/ origins.
        /// Any other origin in the list is refused here, so the exception can never widen beyond the loopback address.
        /// </summary>
        internal HostAllowListHandler(HttpMessageHandler inner, IReadOnlyCollection<Uri> loopbackOrigins) : base(inner)
        {
            foreach (var origin in loopbackOrigins)
            {
                if (origin is not { IsAbsoluteUri: true } || origin.Scheme != Uri.UriSchemeHttp || origin.Host != "127.0.0.1" ||
                    !string.IsNullOrEmpty(origin.UserInfo))
                    throw new ArgumentException("Only http://127.0.0.1:<port>/ origins can be allowed.", nameof(loopbackOrigins));
                _loopback.Add((origin.Host, origin.Port));
            }
        }
#endif

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Validate(request);
            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Validate(request);
            return base.Send(request, cancellationToken);
        }

        private void Validate(HttpRequestMessage request)
        {
            var uri = request.RequestUri;
            if (uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                string.IsNullOrEmpty(uri.UserInfo) && AllowedHosts.Contains(uri.Host))
                return;
#if DEBUG
            if (uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttp && string.IsNullOrEmpty(uri.UserInfo) &&
                _loopback.Contains((uri.Host, uri.Port)))
                return;
#endif
            throw new InvalidOperationException($"Native usage request to a host outside the allow list: {uri?.Host}");
        }
    }

    /// <summary>Fails a read once more than <c>limit</c> bytes came through; never hands out more than one byte past it.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allowed(buffer.Length)]));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // One byte more than the limit is enough to tell an oversized body from one that fits exactly.
        private int Allowed(int requested) => (int)Math.Min(requested, Math.Max(0, limit - _read + 1));

        private int Count(int read)
        {
            _read += read;
            if (_read > limit) throw new IOException("Usage response is larger than expected.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
