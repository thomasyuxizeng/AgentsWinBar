using System.Security.Cryptography;
using System.Text;

namespace AgentsWinBar.Services.Native;

/// <summary>
/// Remembers a rate limit between fetches without scheduling any work. A hold belongs to the credential it was set for:
/// a request made with a different credential (another sign-in, or a renewed token) ends it. Only a SHA-256 hash of the
/// credential is kept, in memory; the credential itself is neither stored nor logged.
/// </summary>
internal sealed class NativeRateLimitHold
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(10);
    private readonly Lock _gate = new();
    private NativeUsageResult? _failure;
    private DateTimeOffset _started;
    private byte[]? _credentialHash;

    /// <summary>The held failure while a hold set for <paramref name="credential"/> lasts; otherwise null.</summary>
    public NativeUsageResult? Get(DateTimeOffset now, string credential) => Get(now, Hash(credential));

    /// <summary>
    /// The end of the hold at <paramref name="now"/>, whatever credential set it (a schedule hint only: a request with
    /// another credential still ends the hold), or null when none lasts.
    /// </summary>
    public DateTimeOffset? Until(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_failure?.RetryAfter is not { } until) return null;
            // The clock was set back since the hold began: keep its length, as Get does.
            if (now < _started) until = now + (until - _started);
            return now < until ? until : null;
        }
    }

    public NativeUsageResult Remember(NativeUsageResult failure, DateTimeOffset? retryAfter, DateTimeOffset now, string credential)
    {
        var until = retryAfter ?? now + DefaultDelay;
        var hash = Hash(credential);
        lock (_gate)
        {
            if (Get(now, hash)?.RetryAfter is not { } existing || until > existing)
            {
                _started = now;
                _credentialHash = hash;
                _failure = failure with { RetryAfter = until, NetworkCalls = 0 };
            }
            return failure with { RetryAfter = _failure!.RetryAfter };
        }
    }

    private NativeUsageResult? Get(DateTimeOffset now, byte[] hash)
    {
        lock (_gate)
        {
            if (_failure?.RetryAfter is not { } until) return null;
            if (_credentialHash == null || !CryptographicOperations.FixedTimeEquals(_credentialHash, hash))
            {
                // The sign-in changed since the hold was set: the new credential gets its own answer.
                Clear();
                return null;
            }
            if (now < _started)
            {
                until = now + (until - _started);
                _started = now;
                _failure = _failure with { RetryAfter = until };
            }
            if (now < until) return _failure;
            Clear();
            return null;
        }
    }

    private void Clear()
    {
        _failure = null;
        _credentialHash = null;
    }

    private static byte[] Hash(string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
