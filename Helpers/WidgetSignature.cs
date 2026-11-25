using System.Text;
using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

/// <summary>
/// Pure-C# signature helpers for compact widgets (<see cref="TaskbarWindow"/> and <see cref="OverlayWindow"/>)
/// so unchanged refreshes skip control rebuilds and layout passes.
/// </summary>
public static class WidgetSignature
{
    public static int RoundTimePercent(double? elapsed)
    {
        if (elapsed is not { } value || !double.IsFinite(value) || value < 0)
        {
            return -1;
        }
        return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
    }

    public static int WarningBrushKind(UsageWindow? window) => window?.Percent switch
    {
        >= UsageThresholds.Critical => 2,
        >= UsageThresholds.Warning => 1,
        _ => 0
    };

    public static string CellText(UsageWindow? window, bool isLoading) =>
        window?.PercentText ?? (isLoading ? "…" : "–");

    public static string BarLabel(UsageData data, WidgetBar? bar) =>
        bar == null ? string.Empty
        : bar.Title ?? (ReferenceEquals(bar.Window, data.Session) ? data.SessionLabel : data.WeeklyLabel);

    public static string ComputeTaskbarSignature(IEnumerable<UsageData> providers, bool isLight, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null)
    {
        DateTime? Stamp(string bar, DateTime? detectedAtUtc) => resetStamp == null ? detectedAtUtc : resetStamp(bar, detectedAtUtc);
        var sb = new StringBuilder();
        sb.Append(isLight ? 'L' : 'D');
        foreach (var data in providers)
        {
            var bars = WidgetBarSelector.Pick(data, nowUtc);
            var first = bars.Count > 0 ? bars[0] : null;
            var second = bars.Count > 1 ? bars[1] : null;
            var firstText = CellText(first?.Window, data.IsLoading);
            var secondText = CellText(second?.Window, data.IsLoading);
            // Omit data.IsLoading when both bars already have windows: CellText does not depend on it.
            var showLoading = data.IsLoading && (first?.Window == null || second?.Window == null);

            sb.Append('|').Append(data.Provider)
              .Append(';').Append(showLoading)
              .Append(';').Append(data.ServiceStatus?.Indicator)
              .Append(';').Append(StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc))
              .Append(';').Append(first?.Window.Percent ?? 0)
              .Append(';').Append(firstText)
              .Append(';').Append(RoundTimePercent(first?.Window.ElapsedPercent))
              .Append(';').Append(first?.TooltipText)
              .Append(';').Append(first?.Accent)
              .Append(';').Append(first?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|first", first?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(BarLabel(data, first))
              .Append(';').Append(WarningBrushKind(first?.Window))
              .Append(';').Append(second?.Window.Percent ?? 0)
              .Append(';').Append(secondText)
              .Append(';').Append(RoundTimePercent(second?.Window.ElapsedPercent))
              .Append(';').Append(second?.TooltipText)
              .Append(';').Append(second?.Accent)
              .Append(';').Append(second?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|second", second?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(BarLabel(data, second))
              .Append(';').Append(WarningBrushKind(second?.Window));
        }
        return sb.ToString();
    }

    public static string ComputeOverlaySignature(IEnumerable<UsageData> providers, DateTime nowUtc,
        Func<string, DateTime?, DateTime?>? resetStamp = null)
    {
        DateTime? Stamp(string bar, DateTime? detectedAtUtc) => resetStamp == null ? detectedAtUtc : resetStamp(bar, detectedAtUtc);
        var sb = new StringBuilder();
        foreach (var data in providers)
        {
            var bars = WidgetBarSelector.Pick(data, nowUtc);
            var first = bars.Count > 0 ? bars[0] : null;
            var second = bars.Count > 1 ? bars[1] : null;
            var firstText = CellText(first?.Window, data.IsLoading);
            var secondText = CellText(second?.Window, data.IsLoading);
            var showLoading = data.IsLoading && (first?.Window == null || second?.Window == null);

            sb.Append('|').Append(data.Provider)
              .Append(';').Append(showLoading)
              .Append(';').Append(data.ServiceStatus?.Indicator)
              .Append(';').Append(StatusDetailCaption.Tooltip(data.ServiceStatus, nowUtc))
              .Append(';').Append(first?.Window.Percent ?? 0)
              .Append(';').Append(firstText)
              .Append(';').Append(RoundTimePercent(first?.Window.ElapsedPercent))
              .Append(';').Append(first?.TooltipText)
              .Append(';').Append(first?.Accent)
              .Append(';').Append(first?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|first", first?.Window.ResetDetectedAtUtc)?.Ticks)
              .Append(';').Append(second?.Window.Percent ?? 0)
              .Append(';').Append(secondText)
              .Append(';').Append(RoundTimePercent(second?.Window.ElapsedPercent))
              .Append(';').Append(second?.TooltipText)
              .Append(';').Append(second?.Accent)
              .Append(';').Append(second?.Window.ResetFromUsedPercent)
              .Append(';').Append(Stamp(data.Provider + "|second", second?.Window.ResetDetectedAtUtc)?.Ticks);
        }
        return sb.ToString();
    }
}
