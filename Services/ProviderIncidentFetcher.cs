using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Fetches and caches public service status and open incident feeds for Claude, Codex/OpenAI, and Gemini.
/// Limits requests to 1 per provider per 5-minute TTL, bounded by a 5-second timeout and 2 MB cap. Never throws.
/// </summary>
public sealed class ProviderIncidentFetcher : IProviderIncidentFetcher, IDisposable
{
    public static readonly Uri ClaudeFeedUri = new("https://status.claude.com/api/v2/summary.json");
    public static readonly Uri CodexFeedUri = new("https://status.openai.com/api/v2/summary.json");
    public static readonly Uri GeminiFeedUri = GoogleIncidentParser.FeedUri;

    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public const int MaxFeedBytes = 2 * 1024 * 1024;
    private const int MaxPooledBufferBytes = 512 * 1024;
    public const string UserAgent = "CodexBarWin/1.0 (+https://github.com/steipete/CodexBar)";

    private sealed record CachedEntry(
        DateTimeOffset CachedAt,
        ProviderStatus? Status,
        EntityTagHeaderValue? ETag = null,
        string? RawETag = null,
        DateTimeOffset? LastModified = null);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderIncidentFetcher>? _logger;

    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lifetimeLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public ProviderIncidentFetcher(
        ILogger<ProviderIncidentFetcher>? logger = null,
        TimeProvider? timeProvider = null)
        : this((HttpClient?)null, timeProvider, logger)
    {
    }

    public ProviderIncidentFetcher(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        ILogger<ProviderIncidentFetcher>? logger = null)
        : this(new HttpClient(handler), timeProvider, logger)
    {
        _disposeClient = true;
    }

    public ProviderIncidentFetcher(
        HttpClient? httpClient,
        TimeProvider? timeProvider = null,
        ILogger<ProviderIncidentFetcher>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient(CreateDefaultHandler(), disposeHandler: true)
            {
                Timeout = DefaultTimeout
            };
            _disposeClient = true;
        }
    }

    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = DefaultTimeout
    };

    public Task<ProviderStatus?> GetStatusAsync(string provider, CancellationToken ct = default) =>
        GetIncidentAsync(provider, ct);

    public async Task<ProviderStatus?> GetIncidentAsync(string provider, CancellationToken ct = default)
    {
        CancellationTokenSource deadline;
        lock (_lifetimeLock)
        {
            if (_disposed || ct.IsCancellationRequested) return null;
            deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
        using var deadlineScope = deadline;
        deadline.CancelAfter(DefaultTimeout);

        if (!ProviderConstants.IsValidProvider(provider))
        {
            return null;
        }

        var normalized = ProviderConstants.ValidateAndNormalize(provider);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(normalized, out var cached))
        {
            var age = now - cached.CachedAt;
            var currentTtl = cached.Status != null ? CacheTtl : NegativeCacheTtl;
            if (age >= TimeSpan.Zero && age < currentTtl)
            {
                return cached.Status;
            }
        }

        var gate = _gates.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
        var entered = false;
        try
        {
            await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            deadline.Token.ThrowIfCancellationRequested();

            now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(normalized, out cached))
            {
                var age = now - cached.CachedAt;
                var currentTtl = cached.Status != null ? CacheTtl : NegativeCacheTtl;
                if (age >= TimeSpan.Zero && age < currentTtl)
                {
                    return cached.Status;
                }
            }

            CachedEntry entry;
            try
            {
                entry = await FetchHttpCoreAsync(normalized, cached, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to retrieve incident status for {Provider}", normalized);
                entry = new CachedEntry(_timeProvider.GetUtcNow(), null);
            }

            _cache[normalized] = entry;
            return entry.Status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            _cache[normalized] = new CachedEntry(_timeProvider.GetUtcNow(), null);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to retrieve incident status for {Provider}", normalized);
            _cache[normalized] = new CachedEntry(_timeProvider.GetUtcNow(), null);
            return null;
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    private async Task<CachedEntry> FetchHttpCoreAsync(string provider, CachedEntry? previous, CancellationToken ct)
    {
        var uri = provider switch
        {
            "claude" => ClaudeFeedUri,
            "codex" => CodexFeedUri,
            "gemini" => GeminiFeedUri,
            _ => null
        };

        if (uri == null)
        {
            return new CachedEntry(_timeProvider.GetUtcNow(), null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!request.Headers.UserAgent.Any())
            {
                request.Headers.UserAgent.ParseAdd(UserAgent);
            }

            if (previous?.Status != null)
            {
                if (previous.ETag != null)
                {
                    request.Headers.IfNoneMatch.Add(previous.ETag);
                }
                else if (!string.IsNullOrEmpty(previous.RawETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", previous.RawETag);
                }

                if (previous.LastModified.HasValue)
                {
                    request.Headers.IfModifiedSince = previous.LastModified.Value;
                }
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && previous?.Status != null)
            {
                var updatedETag = response.Headers.ETag ?? previous.ETag;
                var updatedRawETag = updatedETag != null ? null : (TryGetRawETag(response) ?? previous.RawETag);
                var updatedLastModified = response.Content?.Headers.LastModified ?? previous.LastModified;
                return new CachedEntry(_timeProvider.GetUtcNow(), previous.Status, updatedETag, updatedRawETag, updatedLastModified);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("{Provider} status feed returned HTTP {StatusCode}", provider, response.StatusCode);
                return new CachedEntry(_timeProvider.GetUtcNow(), null);
            }

            if (response.Content.Headers.ContentLength > MaxFeedBytes)
            {
                return new CachedEntry(_timeProvider.GetUtcNow(), null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var initialCapacity = response.Content.Headers.ContentLength is long contentLength && contentLength > 0
                ? (int)Math.Min(contentLength + 1, MaxFeedBytes + 1)
                : 32 * 1024;

            var buffer = RentBuffer(initialCapacity, out var isPooled);
            var totalRead = 0;
            try
            {
                while (true)
                {
                    if (totalRead == buffer.Length)
                    {
                        if (totalRead > MaxFeedBytes)
                        {
                            return new CachedEntry(_timeProvider.GetUtcNow(), null);
                        }

                        var nextSize = Math.Min(buffer.Length * 2, MaxFeedBytes + 1);
                        if (nextSize <= buffer.Length)
                        {
                            return new CachedEntry(_timeProvider.GetUtcNow(), null);
                        }

                        var next = RentBuffer(nextSize, out var nextPooled);
                        Buffer.BlockCopy(buffer, 0, next, 0, totalRead);
                        if (isPooled)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                        buffer = next;
                        isPooled = nextPooled;
                    }

                    var toRead = Math.Min(buffer.Length - totalRead, MaxFeedBytes + 1 - totalRead);
                    if (toRead <= 0)
                    {
                        return new CachedEntry(_timeProvider.GetUtcNow(), null);
                    }

                    var read = await stream.ReadAsync(buffer.AsMemory(totalRead, toRead), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                    if (totalRead > MaxFeedBytes)
                    {
                        return new CachedEntry(_timeProvider.GetUtcNow(), null);
                    }
                }

                var offset = totalRead >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
                if (totalRead <= offset)
                {
                    return new CachedEntry(_timeProvider.GetUtcNow(), null);
                }

                var utf8Memory = buffer.AsMemory(offset, totalRead - offset);
                var now = _timeProvider.GetUtcNow();
                var status = provider switch
                {
                    "gemini" => GoogleIncidentParser.ParseIncidents(utf8Memory, now),
                    "claude" or "codex" => StatuspageIncidentParser.ParseSummary(utf8Memory, now),
                    _ => null
                };

                if (status == null)
                {
                    return new CachedEntry(now, null);
                }

                var etag = response.Headers.ETag;
                var rawETag = etag == null ? TryGetRawETag(response) : null;
                var lastModified = response.Content.Headers.LastModified;
                return new CachedEntry(now, status, etag, rawETag, lastModified);
            }
            finally
            {
                if (isPooled)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Timeout fetching {Provider} status incidents feed", provider);
            return new CachedEntry(_timeProvider.GetUtcNow(), null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch or parse {Provider} status incidents feed", provider);
            return new CachedEntry(_timeProvider.GetUtcNow(), null);
        }
    }

    private static byte[] RentBuffer(int size, out bool isPooled)
    {
        if (size <= MaxPooledBufferBytes)
        {
            isPooled = true;
            return ArrayPool<byte>.Shared.Rent(size);
        }
        isPooled = false;
        return new byte[size];
    }

    private static string? TryGetRawETag(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
