namespace AgentsWinBar.Services.Native;

/// <summary>Remembers a rate limit between fetches without scheduling any work.</summary>
internal sealed class NativeRateLimitHold
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(10);
    private readonly Lock _gate = new();
    private NativeUsageResult? _failure;
    private DateTimeOffset _started;

    public NativeUsageResult? Get(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_failure?.RetryAfter is not { } until) return null;
            if (now < _started)
            {
                until = now + (until - _started);
                _started = now;
                _failure = _failure with { RetryAfter = until };
            }
            if (now < until) return _failure;
            _failure = null;
            return null;
        }
    }

    public NativeUsageResult Remember(NativeUsageResult failure, DateTimeOffset? retryAfter, DateTimeOffset now)
    {
        var until = retryAfter ?? now + DefaultDelay;
        lock (_gate)
        {
            if (Get(now)?.RetryAfter is not { } existing || until > existing)
            {
                _started = now;
                _failure = failure with { RetryAfter = until, NetworkCalls = 0 };
            }
            return failure with { RetryAfter = _failure!.RetryAfter };
        }
    }
}
