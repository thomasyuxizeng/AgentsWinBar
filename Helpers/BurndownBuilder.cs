using AgentsWinBar.Models;

namespace AgentsWinBar.Helpers;

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
        // Same window = reset time within a minute: providers can report it with sub-second jitter, and samples
        // saved before the reader rounded it keep that jitter.
        var current = new List<UsageHistorySample>();
        var prevTicks = long.MinValue;
        var isSorted = true;
        foreach (var s in samples)
        {
            if (!s.ResetsAt.HasValue || !SameReset(Utc(s.ResetsAt.Value), end)) continue;
            var ts = Utc(s.Timestamp);
            if (ts < start || ts > nowUtc || !double.IsFinite(s.UsedPercent) || s.UsedPercent < 0) continue;
            if (ts.Ticks < prevTicks) isSorted = false;
            prevTicks = ts.Ticks;
            current.Add(s);
        }
        if (current.Count > 1)
        {
            if (!isSorted) current = current.OrderBy(s => Utc(s.Timestamp)).ToList();
            var write = 0;
            for (var i = 0; i < current.Count; i++)
            {
                if (i + 1 < current.Count && Utc(current[i].Timestamp) == Utc(current[i + 1].Timestamp))
                    continue;
                current[write++] = current[i];
            }
            if (write < current.Count) current.RemoveRange(write, current.Count - write);
        }
        var duration = (end - start).TotalSeconds;
        var points = current.Select(s => new BurndownPoint((Utc(s.Timestamp) - start).TotalSeconds / duration, s.UsedPercent)).ToList();
        var series = new BurndownSeries { Usage = points, Guide = [new(0, 0), new(1, 100)],
            CapturedAt = current.LastOrDefault() is { } latest ? Utc(latest.Timestamp) : null };
        if (points.Count < 2) return series;
        var first = points[0]; var last = points[^1];
        // Subtract timestamps before converting to fractions: adjacent ticks can round to the same fraction.
        var observedSeconds = (Utc(current[^1].Timestamp) - Utc(current[0].Timestamp)).TotalSeconds;
        var rate = Math.Max(0, (last.UsedPercent - first.UsedPercent) * duration / observedSeconds);
        var projected = last.UsedPercent + rate * (1 - last.ElapsedFraction);
        return series with { Projection = [last, new(1, projected)], ProjectedAtReset = projected, Message = string.Empty };
    }

    /// <summary>How far apart two reported reset times may be and still mean the same window.</summary>
    public static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(1);

    private static bool SameReset(DateTime a, DateTime b) => (a > b ? a - b : b - a) <= ResetTolerance;

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
