namespace AgentsWinBar.Helpers;

/// <summary>Normalizes external values before dashboard calculations and formatting.</summary>
public static class DashboardValues
{
    public static double Percent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
