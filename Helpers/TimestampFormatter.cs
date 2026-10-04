using System.Globalization;

namespace CodexBarWin.Helpers;

public static class TimestampFormatter
{
    /// <summary>Displays stored UTC timestamps in the user's timezone and culture.</summary>
    public static string FormatLocal(DateTime timestamp, TimeZoneInfo? timeZone = null, CultureInfo? culture = null)
    {
        var utc = timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? TimeZoneInfo.Local)
            .ToString("g", culture ?? CultureInfo.CurrentCulture);
    }
}
