using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Parser for Google Workspace Status incidents feed (incidents.json) targeting Gemini.
/// </summary>
public static class GoogleIncidentParser
{
    public static readonly Uri FeedUri = new("https://www.google.com/appsstatus/dashboard/incidents.json");
    public const string GeminiProductId = "npdyhgECDJ6tB66MxXyo";
    public const string HistoryUrl = "https://www.google.com/appsstatus/dashboard/products/npdyhgECDJ6tB66MxXyo/history";
    public const int MaxFeedBytes = 2 * 1024 * 1024;

    public static ProviderStatus? ParseIncidents(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFeedBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseIncidentsCore(doc, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ProviderStatus? ParseIncidents(ReadOnlyMemory<byte> utf8Json, DateTimeOffset now)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaxFeedBytes)
        {
            return null;
        }

        var trimmed = TrimBomAndLeadingWhitespace(utf8Json);
        if (trimmed.IsEmpty)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return ParseIncidentsCore(doc, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReadOnlyMemory<byte> TrimBomAndLeadingWhitespace(ReadOnlyMemory<byte> utf8Json)
    {
        var span = utf8Json.Span;
        var start = span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF ? 3 : 0;
        while (start < span.Length && span[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }
        return start >= span.Length ? ReadOnlyMemory<byte>.Empty : utf8Json[start..];
    }

    private static ProviderStatus? ParseIncidentsCore(JsonDocument doc, DateTimeOffset now)
    {
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

            if (IsEnded(incident, now) || IsResolved(incident))
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
                    UpdatedAt = ExtractUpdatedAt(incident, now),
                    IncidentTitle = ExtractTitle(incident, indicator),
                    LatestUpdateText = ExtractUpdateText(incident),
                    LatestUpdateAt = ExtractLatestUpdateAt(incident, now)
                };
            }
        }

        return winningStatus ?? new ProviderStatus
        {
            Indicator = ProviderStatusIndicator.None,
            Description = "All systems operational",
            Url = HistoryUrl,
            UpdatedAt = now.UtcDateTime,
            IncidentTitle = null,
            LatestUpdateText = null,
            LatestUpdateAt = null
        };
    }

    public static bool IsGeminiIncident(JsonElement incident)
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

    public static bool IsEnded(JsonElement incident, DateTimeOffset now)
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

    public static bool IsResolved(JsonElement incident)
    {
        if (incident.TryGetProperty("most_recent_update", out var mru) &&
            mru.ValueKind == JsonValueKind.Object &&
            mru.TryGetProperty("status", out var mruStatusEl) &&
            mruStatusEl.ValueKind == JsonValueKind.String)
        {
            var status = mruStatusEl.GetString()?.Trim();
            if (string.Equals(status, "AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (incident.TryGetProperty("updates", out var updatesEl) &&
            updatesEl.ValueKind == JsonValueKind.Array)
        {
            var length = updatesEl.GetArrayLength();
            if (length > 0)
            {
                var last = updatesEl[length - 1];
                if (last.ValueKind == JsonValueKind.Object &&
                    last.TryGetProperty("status", out var uStatusEl) &&
                    uStatusEl.ValueKind == JsonValueKind.String)
                {
                    var uStatus = uStatusEl.GetString()?.Trim();
                    if (string.Equals(uStatus, "AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(uStatus, "resolved", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static (ProviderStatusIndicator Indicator, int Rank) DetermineIndicatorAndRank(JsonElement incident)
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

    public static ProviderStatusIndicator MapIndicator(string? impact, string? severity)
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

    public static int GetSeverityRank(ProviderStatusIndicator indicator) => indicator switch
    {
        ProviderStatusIndicator.Critical => 4,
        ProviderStatusIndicator.Major => 3,
        ProviderStatusIndicator.Minor => 2,
        ProviderStatusIndicator.Maintenance => 1,
        _ => 0
    };

    public static string ExtractDescription(JsonElement incident, ProviderStatusIndicator indicator)
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

    public static string CleanDescription(string? desc, ProviderStatusIndicator indicator)
    {
        var fallback = indicator switch
        {
            ProviderStatusIndicator.Critical => "Critical service disruption",
            ProviderStatusIndicator.Major => "Major service disruption",
            ProviderStatusIndicator.Minor => "Minor service disruption",
            ProviderStatusIndicator.Maintenance => "Scheduled maintenance",
            _ => "All systems operational"
        };

        if (string.IsNullOrWhiteSpace(desc))
        {
            return fallback;
        }

        string rawText = desc;
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
                    rawText = lines[i + 1];
                    break;
                }
            }

            if (line.StartsWith("**Summary:**", StringComparison.OrdinalIgnoreCase))
            {
                rawText = line["**Summary:**".Length..];
                break;
            }

            if (line.StartsWith("**Title:**", StringComparison.OrdinalIgnoreCase))
            {
                rawText = line["**Title:**".Length..];
                break;
            }
        }

        if (rawText == desc && lines.Length > 0)
        {
            rawText = lines[0];
        }

        var sanitized = IncidentTextSanitizer.Sanitize(rawText);
        return !string.IsNullOrWhiteSpace(sanitized) ? sanitized : fallback;
    }

    public static string ExtractTitle(JsonElement incident, ProviderStatusIndicator indicator)
    {
        if (incident.TryGetProperty("name", out var nameEl) &&
            nameEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            var sanitized = IncidentTextSanitizer.Sanitize(nameEl.GetString());
            if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
        }

        if (incident.TryGetProperty("title", out var titleEl) &&
            titleEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(titleEl.GetString()))
        {
            var sanitized = IncidentTextSanitizer.Sanitize(titleEl.GetString());
            if (!string.IsNullOrWhiteSpace(sanitized)) return sanitized;
        }

        return ExtractDescription(incident, indicator);
    }

    public static string? ExtractUpdateText(JsonElement incident)
    {
        string? raw = null;
        if (incident.TryGetProperty("most_recent_update", out var updateEl) &&
            updateEl.ValueKind == JsonValueKind.Object &&
            updateEl.TryGetProperty("text", out var textEl) &&
            textEl.ValueKind == JsonValueKind.String)
        {
            raw = textEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(raw) &&
            incident.TryGetProperty("external_desc", out var descEl) &&
            descEl.ValueKind == JsonValueKind.String)
        {
            raw = descEl.GetString();
        }

        var sanitized = IncidentTextSanitizer.Sanitize(raw);
        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    public static DateTime? ExtractLatestUpdateAt(JsonElement incident, DateTimeOffset now)
    {
        if (incident.TryGetProperty("most_recent_update", out var updateEl) &&
            updateEl.ValueKind == JsonValueKind.Object)
        {
            if (updateEl.TryGetProperty("when", out var whenEl) &&
                whenEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(whenEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var whenUtc))
            {
                return whenUtc.UtcDateTime;
            }

            if (updateEl.TryGetProperty("modified", out var modEl) &&
                modEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(modEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modUtc))
            {
                return modUtc.UtcDateTime;
            }

            if (updateEl.TryGetProperty("created", out var createdEl) &&
                createdEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(createdEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdUtc))
            {
                return createdUtc.UtcDateTime;
            }
        }

        return ExtractUpdatedAt(incident, now);
    }

    public static DateTime ExtractUpdatedAt(JsonElement incident, DateTimeOffset now)
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
}
