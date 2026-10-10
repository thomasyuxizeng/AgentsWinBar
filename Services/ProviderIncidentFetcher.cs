using System.Collections.Concurrent;
using System.Text;
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
    public const string UserAgent = "CodexBarWin/1.0 (+https://github.com/steipete/CodexBar)";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderIncidentFetcher>? _logger;

    private readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, ProviderStatus? Status)> _cache = new(StringComparer.OrdinalIgnoreCase);
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
            _httpClient = new HttpClient { Timeout = DefaultTimeout };
            _disposeClient = true;
        }
    }

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

            ProviderStatus? status = null;
            try
            {
                status = await FetchHttpCoreAsync(normalized, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _disposed)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to retrieve incident status for {Provider}", normalized);
                status = null;
            }

            _cache[normalized] = (_timeProvider.GetUtcNow(), status);
            return status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            _cache[normalized] = (_timeProvider.GetUtcNow(), null);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to retrieve incident status for {Provider}", normalized);
            _cache[normalized] = (_timeProvider.GetUtcNow(), null);
            return null;
        }
        finally
        {
            if (entered) gate.Release();
        }
    }

    private async Task<ProviderStatus?> FetchHttpCoreAsync(string provider, CancellationToken ct)
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
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!request.Headers.UserAgent.Any())
            {
                request.Headers.UserAgent.ParseAdd(UserAgent);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("{Provider} status feed returned HTTP {StatusCode}", provider, response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxFeedBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxFeedBytes + 1 - (int)buffer.Length)), ct).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxFeedBytes) return null;
            }

            var bytes = buffer.GetBuffer();
            var offset = buffer.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var json = Encoding.UTF8.GetString(bytes, offset, (int)buffer.Length - offset);

            var now = _timeProvider.GetUtcNow();
            return provider switch
            {
                "gemini" => GoogleIncidentParser.ParseIncidents(json, now),
                "claude" or "codex" => StatuspageIncidentParser.ParseSummary(json, now),
                _ => null
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Timeout fetching {Provider} status incidents feed", provider);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch or parse {Provider} status incidents feed", provider);
            return null;
        }
    }

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
