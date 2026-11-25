namespace CodexBarWin.Services.Native;

/// <summary>Outcome of one native usage read. Only <see cref="Ok"/> means the provider was reached and answered.</summary>
public enum NativeUsageStatus
{
    /// <summary>Fresh reading from the provider, or the in-memory last good reading (see <see cref="NativeUsageResult.Cached"/>).</summary>
    Ok,
    /// <summary>No sign-in was found on Windows for this provider. No network call was made.</summary>
    SignInMissing,
    /// <summary>The stored access token has expired. Nothing was refreshed and no network call was made.</summary>
    SignInExpired,
    /// <summary>The provider rejected the token (HTTP 401/403).</summary>
    Unauthorized,
    /// <summary>The provider rate limited the request (HTTP 429), or the client is in its cooldown after one.</summary>
    RateLimited,
    /// <summary>Any other failure: network, timeout, unexpected HTTP status or an unparseable response.</summary>
    Failed
}

/// <summary>One usage window. <see cref="UsedPercent"/> is 0-100.</summary>
public sealed record NativeUsageWindow(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt, string? ResetDescription);

/// <summary>An additional, separately titled usage window (e.g. "Fable only", "Claude and GPT 5-hour").</summary>
public sealed record NativeExtraWindow(string Id, string Title, NativeUsageWindow Window);

/// <summary>
/// Result of a native usage read. Free of app model types so a console probe can link it; the app maps it to the
/// CLI JSON DTO with <c>NativeUsageDtoMapper</c>. Never carries token values.
/// </summary>
public sealed record NativeUsageResult
{
    /// <summary>Normalized provider id ("claude", "codex", "gemini").</summary>
    public required string Provider { get; init; }
    /// <summary>Data source label, matching the CLI's "source" field ("oauth" for Claude/Codex, "remote" for Antigravity).</summary>
    public string Source { get; init; } = "oauth";
    public NativeUsageStatus Status { get; init; }
    public string? LoginMethod { get; init; }
    public NativeUsageWindow? Primary { get; init; }
    public NativeUsageWindow? Secondary { get; init; }
    public NativeUsageWindow? Tertiary { get; init; }
    public IReadOnlyList<NativeExtraWindow> ExtraWindows { get; init; } = [];
    /// <summary>When the provider's numbers were read.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>The windows above are the last good reading served from memory, not a fresh call.</summary>
    public bool Cached { get; init; }
    public DateTimeOffset? CacheUpdatedAt { get; init; }
    /// <summary>Plain-language explanation for non-OK results. Never contains token values.</summary>
    public string? Message { get; init; }
    public int? HttpStatus { get; init; }
    /// <summary>How long ago the stored access token expired (for <see cref="NativeUsageStatus.SignInExpired"/>).</summary>
    public TimeSpan? ExpiredFor { get; init; }
    /// <summary>When a rate-limited provider may be called again.</summary>
    public DateTimeOffset? RetryAfter { get; init; }
    /// <summary>HTTP requests this read sent (0 when served from cache, cooldown or a local sign-in check).</summary>
    public int NetworkCalls { get; init; }

    public bool HasUsage => Primary != null || Secondary != null || Tertiary != null || ExtraWindows.Count > 0;
}

/// <summary>One provider's native usage reader.</summary>
public interface INativeUsageClient
{
    /// <summary>Normalized provider id this client serves.</summary>
    string ProviderId { get; }

    /// <summary>Reads usage. Never throws for provider, network or sign-in failures; returns a status instead.</summary>
    Task<NativeUsageResult> FetchAsync(CancellationToken ct = default);
}
