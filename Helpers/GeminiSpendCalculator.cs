using System.Globalization;
using System.Text.Json;
using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

/// <summary>
/// Pure, deterministic calculator for monthly Gemini API spend from local spend logs.
/// </summary>
public static class GeminiSpendCalculator
{
    /// <summary>
    /// Parses a JSON-lines log string into validated spend entries.
    /// Skips blank lines, malformed JSON, lines missing time or model, unparseable times,
    /// negative amounts, and null amounts.
    /// </summary>
    public static IReadOnlyList<GeminiSpendEntry> ParseEntries(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<GeminiSpendEntry>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var row = JsonSerializer.Deserialize(line, AppJsonSerializerContext.Default.GeminiSpendLogRowDto);
                if (row == null) continue;
                if (string.IsNullOrWhiteSpace(row.Model) || string.IsNullOrWhiteSpace(row.Time)) continue;
                // The cache-aware estimate is closer to the bill; older rows only carry the upper bound.
                var usd = row.UsdCacheAware ?? row.Usd;
                if (!usd.HasValue || usd.Value < 0) continue;
                if (!DateTime.TryParse(row.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naiveTime))
                    continue;

                naiveTime = DateTime.SpecifyKind(naiveTime, DateTimeKind.Unspecified);
                list.Add(new GeminiSpendEntry
                {
                    Time = naiveTime,
                    Model = row.Model.Trim(),
                    Backend = row.Backend?.Trim(),
                    Usd = usd.Value
                });
            }
            catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
            {
                // Ignore malformed rows
            }
        }
        return list;
    }

    /// <summary>
    /// Computes the calendar month spend info for the month containing <paramref name="nowUtc"/> converted to <paramref name="zone"/>.
    /// </summary>
    public static GeminiApiSpendInfo Build(IReadOnlyList<GeminiSpendEntry> entries, DateTime nowUtc, TimeZoneInfo zone, DateTime? fetchedAtUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), zone);
        var monthStart = new DateOnly(localNow.Year, localNow.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var localDate = DateOnly.FromDateTime(localNow);
        var daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);

        var startLocal = new DateTime(monthStart.Year, monthStart.Month, monthStart.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var endLocal = new DateTime(monthEnd.Year, monthEnd.Month, monthEnd.Day, 0, 0, 0, DateTimeKind.Unspecified);

        var counted = (entries ?? []).Where(e => e.Time >= startLocal && e.Time < endLocal).ToList();

        decimal vertexUsd = 0;
        decimal aiStudioUsd = 0;
        decimal unrecordedUsd = 0;

        foreach (var entry in counted)
        {
            if (string.Equals(entry.Backend, "vertex", StringComparison.OrdinalIgnoreCase))
                vertexUsd += entry.Usd;
            else if (string.Equals(entry.Backend, "aistudio", StringComparison.OrdinalIgnoreCase))
                aiStudioUsd += entry.Usd;
            else
                unrecordedUsd += entry.Usd;
        }

        var totalUsd = vertexUsd + aiStudioUsd + unrecordedUsd;

        var models = counted
            .GroupBy(r => r.Model, StringComparer.Ordinal)
            .Select(g => new GeminiApiModelSpend
            {
                Model = g.Key,
                Usd = g.Sum(r => r.Usd),
                Calls = g.Count()
            })
            .OrderByDescending(m => m.Usd)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();

        DateTime? lastCallUtc = null;
        if (counted.Count > 0)
        {
            var newest = counted.MaxBy(r => r.Time);
            if (newest != null)
                lastCallUtc = ConvertNaiveToUtc(newest.Time, zone);
        }

        return new GeminiApiSpendInfo
        {
            State = GeminiApiSpendState.Ok,
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            DaysLeft = daysLeft,
            TotalUsd = totalUsd,
            AiStudioUsd = aiStudioUsd,
            VertexUsd = vertexUsd,
            UnrecordedRouteUsd = unrecordedUsd,
            Calls = counted.Count,
            Models = models,
            LastCallUtc = lastCallUtc,
            FetchedAtUtc = fetchedAtUtc.HasValue ? Utc(fetchedAtUtc.Value) : null,
            Message = null
        };
    }

    /// <summary>
    /// Overload that parses log text into entries before building spend info.
    /// </summary>
    public static GeminiApiSpendInfo Build(string? logText, DateTime nowUtc, TimeZoneInfo zone, DateTime? fetchedAtUtc) =>
        Build(ParseEntries(logText), nowUtc, zone, fetchedAtUtc);

    /// <summary>
    /// Creates a GeminiApiSpendInfo indicating no spend log exists.
    /// </summary>
    public static GeminiApiSpendInfo BuildNoLog(DateTime nowUtc, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), zone);
        var monthStart = new DateOnly(localNow.Year, localNow.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var localDate = DateOnly.FromDateTime(localNow);
        var daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);

        return new GeminiApiSpendInfo
        {
            State = GeminiApiSpendState.NoLog,
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            DaysLeft = daysLeft,
            TotalUsd = 0,
            AiStudioUsd = 0,
            VertexUsd = 0,
            UnrecordedRouteUsd = 0,
            Calls = 0,
            Models = [],
            LastCallUtc = null,
            FetchedAtUtc = null,
            Message = null
        };
    }

    /// <summary>
    /// Creates a GeminiApiSpendInfo indicating the spend log could not be read.
    /// </summary>
    public static GeminiApiSpendInfo BuildUnavailable(DateTime nowUtc, TimeZoneInfo zone, string? message = "Gemini spend log could not be read")
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(Utc(nowUtc), zone);
        var monthStart = new DateOnly(localNow.Year, localNow.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var localDate = DateOnly.FromDateTime(localNow);
        var daysLeft = Math.Max(1, monthEnd.DayNumber - localDate.DayNumber);

        return new GeminiApiSpendInfo
        {
            State = GeminiApiSpendState.Unavailable,
            MonthStart = monthStart,
            MonthEnd = monthEnd,
            DaysLeft = daysLeft,
            TotalUsd = 0,
            AiStudioUsd = 0,
            VertexUsd = 0,
            UnrecordedRouteUsd = 0,
            Calls = 0,
            Models = [],
            LastCallUtc = null,
            FetchedAtUtc = null,
            Message = message
        };
    }

    /// <summary>
    /// Converts a naive local timestamp to UTC in <paramref name="zone"/>, handling DST gaps and ambiguity safely.
    /// </summary>
    public static DateTime ConvertNaiveToUtc(DateTime naiveLocal, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(naiveLocal, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Treat an invalid local time (DST gap) as the next valid instant.
            for (var minutes = 1; minutes <= 120; minutes++)
            {
                var adjusted = unspecified.AddMinutes(minutes);
                if (!zone.IsInvalidTime(adjusted))
                {
                    unspecified = adjusted;
                    break;
                }
            }
        }
        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            var offset = offsets.Length > 0 ? offsets[0] : zone.BaseUtcOffset;
            return new DateTimeOffset(unspecified, offset).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();
}
