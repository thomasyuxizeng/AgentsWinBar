namespace CodexBarWin.Helpers;

public sealed record ResetRefreshSchedule(TimeSpan Delay, DateTime? TargetResetAtUtc = null, bool IsRetry = false);
public sealed record ResetRefreshAttempt(DateTime TriggeredAtUtc, bool RetryTriggered = false);

/// <summary>Pure scheduling policy; attempts are recorded only when a scheduled refresh actually starts.</summary>
public static class ResetRefreshScheduler
{
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    public static ResetRefreshSchedule GetSchedule(IEnumerable<DateTime?> resetTimes, DateTime nowUtc,
        TimeSpan normalDelay, IReadOnlyDictionary<DateTime, ResetRefreshAttempt>? attempts = null)
    {
        var now = DashboardValues.Utc(nowUtc);
        var result = new ResetRefreshSchedule(normalDelay < MinimumDelay ? MinimumDelay : normalDelay);
        foreach (var reset in resetTimes.Where(t => t.HasValue).Select(t => DashboardValues.Utc(t!.Value)).Distinct())
        {
            bool retry = attempts != null && attempts.TryGetValue(reset, out _);
            TimeSpan delay;
            if (retry)
            {
                var attempt = attempts![reset];
                if (attempt.RetryTriggered) continue;
                delay = DashboardValues.Utc(attempt.TriggeredAtUtc) - now + RetryDelay;
            }
            else
            {
                if (reset <= now) continue;
                delay = reset - now + MinimumDelay;
            }
            if (delay < MinimumDelay) delay = MinimumDelay;
            // Equal/later resets do not change the ordinary schedule or consume an attempt.
            if (delay < result.Delay) result = new(delay, reset, retry);
        }
        return result;
    }
}
