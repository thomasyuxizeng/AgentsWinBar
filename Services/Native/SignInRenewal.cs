namespace AgentsWinBar.Services.Native;

/// <summary>Shares one in-flight renewal and backs off repeated failures for the same stored expiry.</summary>
internal sealed class SignInRenewal<T>(TimeProvider clock, TimeSpan firstDelay, Func<T, DateTimeOffset?> getExpiry)
    where T : class
{
    private readonly Lock _gate = new();
    private Task<T?>? _running;
    private DateTimeOffset? _expiry;
    private DateTimeOffset _lastFailureStart;
    private int _failures;

    public void ObserveExpiry(DateTimeOffset? expiry)
    {
        lock (_gate)
        {
            if (_expiry == expiry) return;
            _expiry = expiry;
            _failures = 0;
        }
    }

    public Task<T?> TryRenewAsync(DateTimeOffset expiry, Func<Task<T?>> renew, CancellationToken callerToken)
    {
        Task<T?> attempt;
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running)
            {
                attempt = running;
            }
            else
            {
                var now = clock.GetUtcNow();
                var delay = _failures switch
                {
                    0 => TimeSpan.Zero,
                    1 => firstDelay,
                    2 => TimeSpan.FromHours(2),
                    _ => TimeSpan.FromHours(6)
                };
                if (_expiry != expiry || (_failures > 0 && now >= _lastFailureStart && now - _lastFailureStart < delay))
                    return Task.FromResult<T?>(null);

                // The attempt owns its timeout and backoff even if the caller stops waiting.
                attempt = _running = Task.Run(() => RunAsync(expiry, now, renew));
            }
        }
        return attempt.WaitAsync(callerToken);
    }

    private async Task<T?> RunAsync(DateTimeOffset expiry, DateTimeOffset started, Func<Task<T?>> renew)
    {
        var credential = await renew().ConfigureAwait(false);
        var storedExpiry = credential == null ? null : getExpiry(credential);
        var renewed = storedExpiry > expiry && storedExpiry > clock.GetUtcNow();

        lock (_gate)
        {
            // A fetch may have seen a different credential while the CLI was running.
            if (_expiry == expiry)
            {
                _expiry = storedExpiry ?? expiry;
                if (renewed)
                    _failures = 0;
                else if (expiry <= started)
                {
                    _failures = Math.Min(_failures + 1, 3);
                    _lastFailureStart = started;
                }
            }
        }
        return renewed ? credential : null;
    }
}
