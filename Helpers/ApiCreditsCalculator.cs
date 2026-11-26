using System.Globalization;
using System.Text.Json;
using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

public static class ApiCreditsCalculator
{
    public static (DateTime StartUtc, DateTime EndUtc) GetCycleWindow(int cycleStartDay, DateTime nowUtc)
    {
        var now = Utc(nowUtc);
        var day = cycleStartDay is >= 1 and <= 31 ? cycleStartDay : 1;
        static DateTime Start(int year, int month, int day) =>
            new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);
        var start = Start(now.Year, now.Month, day);
        if (start > now)
        {
            var previous = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
            start = Start(previous.Year, previous.Month, day);
        }
        var next = new DateTime(start.Year, start.Month, 1).AddMonths(1);
        return (start, Start(next.Year, next.Month, day));
    }

    public static ApiCreditsInfo BuildOk(decimal monthlyUsd, decimal spentUsd, int cycleStartDay,
        DateTime nowUtc, DateTime fetchedAtUtc)
    {
        var now = Utc(nowUtc);
        var (start, end) = GetCycleWindow(cycleStartDay, now);
        monthlyUsd = NormalizeMonthlyUsd(monthlyUsd);
        var remaining = monthlyUsd - spentUsd;
        var days = Math.Max(1, (int)Math.Ceiling((end - now).TotalDays));
        return new()
        {
            State = ApiCreditsState.Ok, MonthlyUsd = monthlyUsd, SpentUsd = spentUsd,
            RemainingUsd = remaining, PercentUsed = Math.Clamp((double)spentUsd / (double)monthlyUsd * 100, 0, 100),
            IsOverBudget = spentUsd > monthlyUsd, CycleStart = DateOnly.FromDateTime(start),
            CycleEnd = DateOnly.FromDateTime(end), DaysLeft = days, PaceUsdPerDay = Math.Max(remaining, 0) / days,
            FetchedAtUtc = Utc(fetchedAtUtc), IsStale = now - Utc(fetchedAtUtc) > TimeSpan.FromMinutes(15)
        };
    }

    public static ApiCreditsInfo BuildNoKey(decimal monthlyUsd, int cycleStartDay, DateTime nowUtc) =>
        BuildState(ApiCreditsState.NoKey, monthlyUsd, cycleStartDay, nowUtc,
            "Set ANTHROPIC_ADMIN_KEY to show API spend");

    public static ApiCreditsInfo BuildRejected(decimal monthlyUsd, int cycleStartDay, DateTime nowUtc, int status) =>
        BuildState(ApiCreditsState.Rejected, monthlyUsd, cycleStartDay, nowUtc,
            $"Admin key rejected (HTTP {status})");

    public static ApiCreditsInfo BuildUnavailable(decimal monthlyUsd, int cycleStartDay, DateTime nowUtc) =>
        BuildState(ApiCreditsState.Unavailable, monthlyUsd, cycleStartDay, nowUtc,
            "Anthropic API spend is unavailable right now");

    private static ApiCreditsInfo BuildState(ApiCreditsState state, decimal monthlyUsd, int day, DateTime now, string message) =>
        BuildOk(monthlyUsd, 0, day, now, now) with { State = state, FetchedAtUtc = null, Message = message };

    public static decimal NormalizeMonthlyUsd(decimal value) => value is > 0 and <= 100000 ? value : 200;

    public static bool TryParseCostReport(string json, out decimal totalCents, out string? nextPage, out bool hasMore) =>
        TryParseCostReport(json, out totalCents, out nextPage, out hasMore, out _);

    internal static bool TryParseCostReport(string json, out decimal totalCents, out string? nextPage,
        out bool hasMore, out int ignoredCurrencies)
    {
        totalCents = 0; nextPage = null; hasMore = false; ignoredCurrencies = 0;
        try
        {
            var report = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.ApiCostReport);
            if (report?.Data == null || report.HasMore == null) return false;
            decimal total = 0;
            foreach (var bucket in report.Data)
            {
                if (bucket?.Results == null) return false;
                foreach (var result in bucket.Results)
                {
                    if (result == null) return false;
                    if (!string.Equals(result.Currency, "USD", StringComparison.Ordinal))
                    { ignoredCurrencies++; continue; }
                    if (!decimal.TryParse(result.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var cents)) return false;
                    total = checked(total + cents);
                }
            }
            totalCents = total; nextPage = report.NextPage; hasMore = report.HasMore.Value;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
