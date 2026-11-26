using System.Globalization;
using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

public static class CostFormatter
{
    public static string Tokens(long tokens)
    {
        var value = Math.Max(0, tokens);
        var (divisor, suffix) = value switch
        {
            >= 1_000_000_000 => (1_000_000_000m, "B"),
            >= 1_000_000 => (1_000_000m, "M"),
            >= 1_000 => (1_000m, "K"),
            _ => (1m, "")
        };
        return (value / divisor).ToString(divisor == 1 ? "0" : "0.#", CultureInfo.InvariantCulture) + suffix;
    }

    public static string Money(decimal value, string currency = "USD") =>
        (currency == "USD" ? "$" : currency + " ") + Math.Max(0, value).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Summary(CostData data) =>
        $"Est. today {Money(data.TodayCostUSD, data.CurrencyCode)}, {Tokens(data.TodayTokens)} tokens · 30 days {Money(data.Last30DaysCostUSD, data.CurrencyCode)}, {Tokens(data.Last30DaysTokens)} tokens";

    public static string CardSummary(CostData data) =>
        $"Est. today {Money(data.TodayCostUSD, data.CurrencyCode)} · {Tokens(data.TodayTokens)} tokens\n" +
        $"Est. 30 days {Money(data.Last30DaysCostUSD, data.CurrencyCode)} · {Tokens(data.Last30DaysTokens)} tokens";
}
