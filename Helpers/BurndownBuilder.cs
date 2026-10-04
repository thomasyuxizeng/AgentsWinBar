using CodexBarWin.Models;

namespace CodexBarWin.Helpers;

public record BurndownPoint(double ElapsedFraction, double UsedPercent);

public record BurndownSeries
{
    public IReadOnlyList<BurndownPoint> Usage { get; init; } = [];
    public IReadOnlyList<BurndownPoint> Guide { get; init; } = [];
    public IReadOnlyList<BurndownPoint> Projection { get; init; } = [];
    public double? ProjectedAtReset { get; init; }
    public DateTime? CapturedAt { get; init; }
    public int SampleCount => Usage.Count;
    public bool IsReady => Usage.Count >= 2;
    public string Message { get; init; } = "Collecting data";
}

/// <summary>Builds only the current reset window. Rates come from observed samples, not assumed history.</summary>
public static class BurndownBuilder
{
    public static BurndownSeries Build(IEnumerable<UsageHistorySample> samples, UsageWindow window, DateTime nowUtc)
    {
        if (!window.ResetAt.HasValue || window.WindowMinutes <= 0)
            return new() { Message = "Window timing unavailable" };
        var end = Utc(window.ResetAt.Value);
        DateTime start;
        try { start = end.AddMinutes(-window.WindowMinutes); }
        catch (ArgumentOutOfRangeException) { return new() { Message = "Window timing unavailable" }; }
        nowUtc = Utc(nowUtc);
        if (nowUtc < start) return new() { Message = "Window has not started" };
        if (nowUtc >= end) return new() { Message = "Waiting for window reset" };
        var current = samples.Where(s => s.ResetsAt.HasValue && Utc(s.ResetsAt.Value) == end &&
                Utc(s.Timestamp) >= start && Utc(s.Timestamp) <= nowUtc &&
                double.IsFinite(s.UsedPercent) && s.UsedPercent is >= 0 and <= 100)
            .OrderBy(s => s.Timestamp).GroupBy(s => Utc(s.Timestamp)).Select(g => g.Last()).ToList();
        var duration = (end - start).TotalSeconds;
        var points = current.Select(s => new BurndownPoint((Utc(s.Timestamp) - start).TotalSeconds / duration, s.UsedPercent)).ToList();
        var series = new BurndownSeries { Usage = points, Guide = [new(0, 0), new(1, 100)],
            CapturedAt = current.LastOrDefault()?.Timestamp };
        if (points.Count < 2) return series;
        var first = points[0]; var last = points[^1];
        var rate = Math.Max(0, (last.UsedPercent - first.UsedPercent) / (last.ElapsedFraction - first.ElapsedFraction));
        var projected = last.UsedPercent + rate * (1 - last.ElapsedFraction);
        return series with { Projection = [last, new(1, projected)], ProjectedAtReset = projected, Message = string.Empty };
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
