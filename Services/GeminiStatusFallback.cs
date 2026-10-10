using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services;

/// <summary>
/// Fetches and caches the public Google Workspace Status incidents feed for Gemini (product npdyhgECDJ6tB66MxXyo)
/// when the CLI does not provide a status object. Never throws; returns null on any failure.
/// </summary>
public sealed class GeminiStatusFallback : IGeminiStatusFallback, IDisposable
{
    public static readonly Uri FeedUri = new("https://www.google.com/appsstatus/dashboard/incidents.json");
    public const string GeminiProductId = "npdyhgECDJ6tB66MxXyo";
    public const string HistoryUrl = "https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public const int MaxFeedBytes = 2 * 1024 * 1024;
    public const string UserAgent = "CodexBarWin/1.0 (+https://github.com/steipete/CodexBar)";

    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GeminiStatusFallback>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private ProviderStatus? _cachedStatus;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public GeminiStatusFallback(
        ILogger<GeminiStatusFallback>? logger = null,
        TimeProvider? timeProvider = null)
        : this((HttpClient?)null, timeProvider, logger)
    {
    }

    public GeminiStatusFallback(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        ILogger<GeminiStatusFallback>? logger = null)
        : this(new HttpClient(handler), timeProvider, logger)
    {
        _disposeClient = true;
    }

    public GeminiStatusFallback(
        HttpClient? httpClient,
        TimeProvider? timeProvider = null,
        ILogger<GeminiStatusFallback>? logger = null)
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

    public async Task<ProviderStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        CancellationTokenSource deadline;
        lock (_lifetimeLock)
        {
            if (_disposed || ct.IsCancellationRequested) return null;
            deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        }
        using var deadlineScope = deadline;
        deadline.CancelAfter(DefaultTimeout); // Includes waiting for another caller.
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            deadline.Token.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            var age = now - _cachedAt;
            // Cache failures too, so a burst of callers cannot hammer a failing feed.
            if (age >= TimeSpan.Zero && age < CacheTtl)
            {
                return _cachedStatus;
            }

            var status = await FetchStatusCoreAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            _cachedStatus = status;
            _cachedAt = _timeProvider.GetUtcNow();
            return status;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to retrieve Gemini status fallback");
            return null;
        }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private async Task<ProviderStatus?> FetchStatusCoreAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUri);
            if (!request.Headers.UserAgent.Any())
            {
                request.Headers.UserAgent.ParseAdd(UserAgent);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Google status feed returned HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxFeedBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                // Read at most one byte over the cap, even without a Content-Length header.
                var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length,
                    MaxFeedBytes + 1 - (int)buffer.Length)), ct).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxFeedBytes) return null;
            }
            var bytes = buffer.GetBuffer();
            var offset = buffer.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return ParseIncidents(Encoding.UTF8.GetString(bytes, offset, (int)buffer.Length - offset), _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Timeout fetching Google status incidents feed");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to fetch or parse Google status incidents feed");
            return null;
        }
    }

    internal static ProviderStatus? ParseIncidents(string json, DateTimeOffset now) =>
        GoogleIncidentParser.ParseIncidents(json, now);

    internal static ProviderStatusIndicator MapIndicator(string? impact, string? severity) =>
        GoogleIncidentParser.MapIndicator(impact, severity);

    internal static string CleanDescription(string? desc, ProviderStatusIndicator indicator) =>
        GoogleIncidentParser.CleanDescription(desc, indicator);

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
        // Do not dispose the semaphore while in-flight callers may still release it.
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
