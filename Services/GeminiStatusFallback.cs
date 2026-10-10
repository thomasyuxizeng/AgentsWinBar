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

    internal static ProviderStatus? ParseIncidents(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFeedBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ProviderStatus? winningStatus = null;
            int highestRank = -1;

            foreach (var incident in doc.RootElement.EnumerateArray())
            {
                if (incident.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!IsGeminiIncident(incident))
                {
                    continue;
                }

                if (IsEnded(incident, now))
                {
                    continue;
                }

                var (indicator, rank) = DetermineIndicatorAndRank(incident);
                if (indicator == ProviderStatusIndicator.None)
                {
                    continue;
                }

                if (rank > highestRank)
                {
                    highestRank = rank;
                    winningStatus = new ProviderStatus
                    {
                        Indicator = indicator,
                        Description = ExtractDescription(incident, indicator),
                        Url = HistoryUrl,
                        UpdatedAt = ExtractUpdatedAt(incident, now)
                    };
                }
            }

            return winningStatus ?? new ProviderStatus
            {
                Indicator = ProviderStatusIndicator.None,
                Description = "All systems operational",
                Url = HistoryUrl,
                UpdatedAt = now.UtcDateTime
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGeminiIncident(JsonElement incident)
    {
        if (incident.TryGetProperty("service_key", out var serviceKeyEl) &&
            serviceKeyEl.ValueKind == JsonValueKind.String &&
            string.Equals(serviceKeyEl.GetString(), GeminiProductId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (incident.TryGetProperty("affected_products", out var affectedEl) &&
            affectedEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var prod in affectedEl.EnumerateArray())
            {
                if (prod.ValueKind == JsonValueKind.Object &&
                    prod.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String &&
                    string.Equals(idEl.GetString(), GeminiProductId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEnded(JsonElement incident, DateTimeOffset now)
    {
        if (incident.TryGetProperty("end", out var endEl) &&
            endEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(endEl.GetString()))
        {
            if (DateTimeOffset.TryParse(endEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var endUtc))
                return endUtc <= now;
        }

        // Google occasionally leaves old incidents open. Prefer modified, then begin;
        // missing dates alone do not prove an incident has ended.
        var updatedAt = ExtractUpdatedAt(incident, now);
        return now.UtcDateTime - updatedAt > CacheRetention.MaxAge;
    }

    private static (ProviderStatusIndicator Indicator, int Rank) DetermineIndicatorAndRank(JsonElement incident)
    {
        string? impact = incident.TryGetProperty("status_impact", out var impactEl) && impactEl.ValueKind == JsonValueKind.String
            ? impactEl.GetString()
            : null;

        string? severity = incident.TryGetProperty("severity", out var severityEl) && severityEl.ValueKind == JsonValueKind.String
            ? severityEl.GetString()
            : null;

        var indicator = MapIndicator(impact, severity);
        var rank = GetSeverityRank(indicator);
        return (indicator, rank);
    }

    internal static ProviderStatusIndicator MapIndicator(string? impact, string? severity)
    {
        var normalizedImpact = impact?.Trim().ToUpperInvariant();
        var normalizedSeverity = severity?.Trim().ToLowerInvariant();

        return normalizedImpact switch
        {
            "SERVICE_OUTAGE" or "OUTAGE" or "CRITICAL" => ProviderStatusIndicator.Critical,
            "SERVICE_DISRUPTION" or "DISRUPTION" => normalizedSeverity == "high"
                ? ProviderStatusIndicator.Major
                : ProviderStatusIndicator.Minor,
            "MAJOR" => ProviderStatusIndicator.Major,
            "SERVICE_INFORMATION" or "INFORMATION" or "MINOR" => ProviderStatusIndicator.Minor,
            "SCHEDULED_MAINTENANCE" or "MAINTENANCE" => ProviderStatusIndicator.Maintenance,
            "AVAILABLE" or "NONE" => ProviderStatusIndicator.None,
            _ => ProviderStatusIndicator.Minor
        };
    }

    private static int GetSeverityRank(ProviderStatusIndicator indicator) => indicator switch
    {
        ProviderStatusIndicator.Critical => 4,
        ProviderStatusIndicator.Major => 3,
        ProviderStatusIndicator.Minor => 2,
        ProviderStatusIndicator.Maintenance => 1,
        _ => 0
    };

    private static string ExtractDescription(JsonElement incident, ProviderStatusIndicator indicator)
    {
        string? raw = null;
        if (incident.TryGetProperty("external_desc", out var descEl) && descEl.ValueKind == JsonValueKind.String)
        {
            raw = descEl.GetString();
        }
        else if (incident.TryGetProperty("most_recent_update", out var updateEl) &&
                 updateEl.ValueKind == JsonValueKind.Object &&
                 updateEl.TryGetProperty("text", out var textEl) &&
                 textEl.ValueKind == JsonValueKind.String)
        {
            raw = textEl.GetString();
        }

        return CleanDescription(raw, indicator);
    }

    internal static string CleanDescription(string? desc, ProviderStatusIndicator indicator)
    {
        if (string.IsNullOrWhiteSpace(desc))
        {
            return indicator switch
            {
                ProviderStatusIndicator.Critical => "Critical service disruption",
                ProviderStatusIndicator.Major => "Major service disruption",
                ProviderStatusIndicator.Minor => "Minor service disruption",
                ProviderStatusIndicator.Maintenance => "Scheduled maintenance",
                _ => "All systems operational"
            };
        }

        var lines = desc.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Equals("**Summary:**", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("**Title:**", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("**Summary**", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("**Title**", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < lines.Length)
                {
                    return lines[i + 1].Trim('*', ' ');
                }
            }

            if (line.StartsWith("**Summary:**", StringComparison.OrdinalIgnoreCase))
            {
                return line["**Summary:**".Length..].Trim('*', ' ');
            }

            if (line.StartsWith("**Title:**", StringComparison.OrdinalIgnoreCase))
            {
                return line["**Title:**".Length..].Trim('*', ' ');
            }
        }

        return lines[0].Trim('*', ' ');
    }

    private static DateTime ExtractUpdatedAt(JsonElement incident, DateTimeOffset now)
    {
        if (incident.TryGetProperty("modified", out var modEl) &&
            modEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(modEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modUtc))
        {
            return modUtc.UtcDateTime;
        }

        if (incident.TryGetProperty("begin", out var beginEl) &&
            beginEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(beginEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var beginUtc))
        {
            return beginUtc.UtcDateTime;
        }

        return now.UtcDateTime;
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
        // Do not dispose the semaphore while in-flight callers may still release it.
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}
