using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexBarWin.Helpers;
using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Parser for Atlassian Statuspage API v2 feeds (summary.json) used by Claude and OpenAI/Codex.
/// </summary>
public static class StatuspageIncidentParser
{
    public const int MaxFeedBytes = 2 * 1024 * 1024;

    public static ProviderStatus? ParseSummary(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFeedBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseSummaryCore(doc, now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ProviderStatus? ParseSummary(ReadOnlyMemory<byte> utf8Json, DateTimeOffset now)
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
            return ParseSummaryCore(doc, now);
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

    private static ProviderStatus? ParseSummaryCore(JsonDocument doc, DateTimeOffset now)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? pageUrl = null;
        DateTime? pageUpdatedAt = null;

        if (doc.RootElement.TryGetProperty("page", out var pageEl) && pageEl.ValueKind == JsonValueKind.Object)
        {
            if (pageEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                pageUrl = urlEl.GetString();
            }

            if (pageEl.TryGetProperty("updated_at", out var upEl) &&
                upEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(upEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var upUtc))
            {
                pageUpdatedAt = upUtc.UtcDateTime;
            }
        }

        string? statusIndicator = null;
        string? statusDesc = null;

        if (doc.RootElement.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Object)
        {
            if (statusEl.TryGetProperty("indicator", out var indEl) && indEl.ValueKind == JsonValueKind.String)
            {
                statusIndicator = indEl.GetString();
            }

            if (statusEl.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            {
                statusDesc = IncidentTextSanitizer.Sanitize(descEl.GetString());
                if (string.IsNullOrWhiteSpace(statusDesc)) statusDesc = null;
            }
        }

        var indicator = ProviderStatusPresentation.ParseIndicator(statusIndicator);

        // 1. Check unresolved incidents
        if (doc.RootElement.TryGetProperty("incidents", out var incidentsEl) && incidentsEl.ValueKind == JsonValueKind.Array)
        {
            var candidates = new List<(JsonElement Element, int ImpactRank, DateTimeOffset UpdatedAt)>();

            foreach (var incident in incidentsEl.EnumerateArray())
            {
                if (incident.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (incident.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                {
                    var st = stEl.GetString()?.Trim().ToLowerInvariant();
                    if (st is "resolved" or "postmortem")
                    {
                        continue;
                    }
                }

                if (incident.TryGetProperty("resolved_at", out var resAtEl) &&
                    resAtEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(resAtEl.GetString()))
                {
                    continue;
                }

                string? impact = incident.TryGetProperty("impact", out var impEl) && impEl.ValueKind == JsonValueKind.String
                    ? impEl.GetString()
                    : null;

                var impactRank = GetImpactRank(impact);
                var updatedAt = ExtractIncidentTime(incident, now);

                candidates.Add((incident, impactRank, updatedAt));
            }

            if (candidates.Count > 0)
            {
                var winningIncident = candidates
                    .OrderByDescending(c => c.ImpactRank)
                    .ThenByDescending(c => c.UpdatedAt)
                    .First().Element;

                string? incidentTitle = winningIncident.TryGetProperty("name", out var nameEl) &&
                                       nameEl.ValueKind == JsonValueKind.String
                    ? IncidentTextSanitizer.Sanitize(nameEl.GetString())
                    : null;
                if (string.IsNullOrWhiteSpace(incidentTitle)) incidentTitle = null;

                var (updateText, updateAt) = ExtractNewestUpdate(winningIncident, now);

                if (indicator is ProviderStatusIndicator.None or ProviderStatusIndicator.Unknown)
                {
                    string? impact = winningIncident.TryGetProperty("impact", out var impEl) && impEl.ValueKind == JsonValueKind.String
                        ? impEl.GetString()
                        : null;
                    indicator = MapImpactToIndicator(impact);
                }

                string? incidentUrl = pageUrl;
                if (winningIncident.TryGetProperty("shortlink", out var linkEl) &&
                    linkEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(linkEl.GetString()))
                {
                    incidentUrl = linkEl.GetString();
                }

                return new ProviderStatus
                {
                    Indicator = indicator,
                    Description = !string.IsNullOrWhiteSpace(statusDesc) ? statusDesc : ProviderStatusPresentation.Description(new ProviderStatus { Indicator = indicator }),
                    Url = incidentUrl ?? pageUrl,
                    UpdatedAt = updateAt ?? pageUpdatedAt ?? now.UtcDateTime,
                    IncidentTitle = incidentTitle,
                    LatestUpdateText = updateText,
                    LatestUpdateAt = updateAt
                };
            }
        }

        // 2. Check scheduled maintenance if no open incident
        if (doc.RootElement.TryGetProperty("scheduled_maintenances", out var smEl) && smEl.ValueKind == JsonValueKind.Array)
        {
            var activeMaintenances = new List<(JsonElement Element, int Priority, DateTimeOffset UpdatedAt)>();

            foreach (var maint in smEl.EnumerateArray())
            {
                if (maint.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (maint.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                {
                    var st = stEl.GetString()?.Trim().ToLowerInvariant();
                    if (st is "in_progress" or "scheduled" or "verifying")
                    {
                        var priority = st == "in_progress" ? 2 : 1;
                        var updatedAt = ExtractIncidentTime(maint, now);
                        activeMaintenances.Add((maint, priority, updatedAt));
                    }
                }
            }

            if (activeMaintenances.Count > 0)
            {
                var winningMaint = activeMaintenances
                    .OrderByDescending(m => m.Priority)
                    .ThenByDescending(m => m.UpdatedAt)
                    .First().Element;

                string? maintTitle = winningMaint.TryGetProperty("name", out var nameEl) &&
                                     nameEl.ValueKind == JsonValueKind.String
                    ? IncidentTextSanitizer.Sanitize(nameEl.GetString())
                    : null;
                if (string.IsNullOrWhiteSpace(maintTitle)) maintTitle = null;

                var (updateText, updateAt) = ExtractNewestUpdate(winningMaint, now);

                if (indicator is ProviderStatusIndicator.None or ProviderStatusIndicator.Unknown)
                {
                    indicator = ProviderStatusIndicator.Maintenance;
                }

                return new ProviderStatus
                {
                    Indicator = indicator,
                    Description = !string.IsNullOrWhiteSpace(statusDesc) ? statusDesc : "Scheduled maintenance",
                    Url = pageUrl,
                    UpdatedAt = updateAt ?? pageUpdatedAt ?? now.UtcDateTime,
                    IncidentTitle = maintTitle,
                    LatestUpdateText = updateText,
                    LatestUpdateAt = updateAt
                };
            }
        }

        // 3. Clean operational or no open incident
        return new ProviderStatus
        {
            Indicator = indicator == ProviderStatusIndicator.Unknown ? ProviderStatusIndicator.None : indicator,
            Description = !string.IsNullOrWhiteSpace(statusDesc) ? statusDesc : "All systems operational",
            Url = pageUrl,
            UpdatedAt = pageUpdatedAt ?? now.UtcDateTime,
            IncidentTitle = null,
            LatestUpdateText = null,
            LatestUpdateAt = null
        };
    }

    private static (string? Body, DateTime? Timestamp) ExtractNewestUpdate(JsonElement incident, DateTimeOffset now)
    {
        if (incident.TryGetProperty("incident_updates", out var updatesEl) && updatesEl.ValueKind == JsonValueKind.Array)
        {
            JsonElement? newestUpdate = null;
            DateTimeOffset newestTime = DateTimeOffset.MinValue;

            foreach (var update in updatesEl.EnumerateArray())
            {
                if (update.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var time = ExtractUpdateTime(update);
                if (newestUpdate == null || time > newestTime)
                {
                    newestUpdate = update;
                    newestTime = time;
                }
            }

            if (newestUpdate.HasValue)
            {
                string? rawBody = null;
                if (newestUpdate.Value.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
                {
                    rawBody = bodyEl.GetString();
                }

                var sanitized = IncidentTextSanitizer.Sanitize(rawBody);
                var body = string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
                var timestamp = newestTime > DateTimeOffset.MinValue ? newestTime.UtcDateTime : (DateTime?)null;

                return (body, timestamp ?? ExtractIncidentTime(incident, now).UtcDateTime);
            }
        }

        var fallbackTime = ExtractIncidentTime(incident, now).UtcDateTime;
        return (null, fallbackTime);
    }

    private static DateTimeOffset ExtractUpdateTime(JsonElement update)
    {
        if (update.TryGetProperty("display_at", out var dispEl) &&
            dispEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(dispEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dispUtc))
        {
            return dispUtc;
        }

        if (update.TryGetProperty("updated_at", out var upEl) &&
            upEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(upEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var upUtc))
        {
            return upUtc;
        }

        if (update.TryGetProperty("created_at", out var crEl) &&
            crEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(crEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var crUtc))
        {
            return crUtc;
        }

        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset ExtractIncidentTime(JsonElement incident, DateTimeOffset now)
    {
        if (incident.TryGetProperty("updated_at", out var upEl) &&
            upEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(upEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var upUtc))
        {
            return upUtc;
        }

        if (incident.TryGetProperty("created_at", out var crEl) &&
            crEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(crEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var crUtc))
        {
            return crUtc;
        }

        if (incident.TryGetProperty("scheduled_for", out var schEl) &&
            schEl.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(schEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var schUtc))
        {
            return schUtc;
        }

        return now;
    }

    private static int GetImpactRank(string? impact) => impact?.Trim().ToLowerInvariant() switch
    {
        "critical" => 4,
        "major" => 3,
        "minor" => 2,
        "maintenance" => 1,
        _ => 0
    };

    private static ProviderStatusIndicator MapImpactToIndicator(string? impact) => impact?.Trim().ToLowerInvariant() switch
    {
        "critical" => ProviderStatusIndicator.Critical,
        "major" => ProviderStatusIndicator.Major,
        "minor" => ProviderStatusIndicator.Minor,
        "maintenance" => ProviderStatusIndicator.Maintenance,
        _ => ProviderStatusIndicator.Minor
    };
}
