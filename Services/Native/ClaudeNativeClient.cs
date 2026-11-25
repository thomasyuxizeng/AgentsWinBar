using System.Globalization;
using System.Net;
using System.Text.Json;
using CodexBarWin.Models;
using Microsoft.Extensions.Logging;

namespace CodexBarWin.Services.Native;

/// <summary>The fields of Claude Code's OAuth sign-in this reader uses. The access token never leaves this process.</summary>
public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? SubscriptionType, string? RateLimitTier)
{
    /// <summary>The generated ToString would print <see cref="AccessToken"/>; this one never prints a token value.</summary>
    public override string ToString() => $"ClaudeCredentials {{ ExpiresAt = {ExpiresAt:o}, SubscriptionType = {SubscriptionType} }}";
}

public sealed record ClaudeNativeOptions
{
    /// <summary>
    /// Defaults to %USERPROFILE%\.claude\.credentials.json: the default profile only, CLAUDE_CONFIG_DIR is not honored.
    /// A path set here always wins.
    /// </summary>
    public string? CredentialsPath { get; init; }
    /// <summary>Sent as "claude-code/&lt;version&gt;". Upstream detects it by running claude; we never run it.</summary>
    public string ClaudeCodeVersion { get; init; } = "2.1.0";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Serve the last good reading for this long without calling (the WSL shim uses 10 minutes).</summary>
    public TimeSpan CacheLifetime { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>After a 429, do not call again for at least this long (or Retry-After, whichever is later).</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Use the last-good cache for fresh-cache hits. Failures still fall back to it.</summary>
    public bool UseFreshCache { get; init; } = true;
    /// <summary>Time zone for the upstream-style reset description ("Nov 25 at 3:00PM").</summary>
    public TimeZoneInfo? DisplayTimeZone { get; init; }

    public string ResolveCredentialsPath()
    {
        if (!string.IsNullOrWhiteSpace(CredentialsPath)) return CredentialsPath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    }
}

/// <summary>
/// Reads Claude plan usage from Anthropic's OAuth usage endpoint with the Windows Claude Code sign-in, read-only.
/// Port of upstream CodexBar's ClaudeOAuthUsageFetcher plus the WSL shim's 429 cooldown and 10-minute cache.
/// Never refreshes or writes the token: an expired token yields <see cref="NativeUsageStatus.SignInExpired"/>.
/// </summary>
public sealed class ClaudeNativeClient : INativeUsageClient
{
    public static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    public const string BetaHeader = "oauth-2025-04-20";
    private const int SessionMinutes = 5 * 60;
    private const int WeeklyMinutes = 7 * 24 * 60;

    private readonly HttpClient _http;
    private readonly ILogger<ClaudeNativeClient> _logger;
    private readonly ClaudeNativeOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private NativeUsageResult? _lastGood;
    private DateTimeOffset _blockedUntil;

    public ClaudeNativeClient(HttpClient http, ILogger<ClaudeNativeClient> logger, ClaudeNativeOptions? options = null, TimeProvider? clock = null)
    {
        _http = http;
        _logger = logger;
        _options = options ?? new ClaudeNativeOptions();
        _clock = clock ?? TimeProvider.System;
        ProviderId = ProviderConstants.ValidateAndNormalize("claude");
    }

    public string ProviderId { get; }

    /// <summary>When the client will call the endpoint again after a 429 (MinValue when not blocked).</summary>
    public DateTimeOffset BlockedUntil { get { lock (_gate) return _blockedUntil; } }

    public async Task<NativeUsageResult> FetchAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        NativeUsageResult? lastGood;
        DateTimeOffset blockedUntil;
        lock (_gate) { lastGood = _lastGood; blockedUntil = _blockedUntil; }

        // 1. Fresh cache: answer immediately (the shim's 10-minute cache), unless a window has reset since.
        if (_options.UseFreshCache && lastGood?.UpdatedAt is { } updated && now - updated < _options.CacheLifetime &&
            !NativeLastGood.HasPastReset(lastGood, now))
            return lastGood with { Cached = true, CacheUpdatedAt = updated, NetworkCalls = 0 };

        // 2. 429 cooldown: never call; serve the last good reading if any.
        if (blockedUntil > now)
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.RateLimited, 429,
                "Claude usage is rate limited right now; retrying shortly.") with { RetryAfter = blockedUntil });

        // 3. Sign-in (read-only).
        ClaudeCredentials? credentials;
        try
        {
            credentials = await ReadCredentialsAsync(_options.ResolveCredentialsPath(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _logger.LogWarning("Claude sign-in file could not be read ({Type})", ex.GetType().Name);
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.Failed, null, "The Claude sign-in file could not be read."));
        }
        if (credentials == null)
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.SignInMissing, null,
                "Not signed in to Claude on Windows. Sign in to Claude Code on Windows."));
        if (credentials.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            var ago = now - expiresAt;
            _logger.LogInformation("Claude access token expired {Ago} ago; not refreshing (read-only)", NativeParse.Ago(ago));
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.SignInExpired, null,
                $"Claude sign-in expired {NativeParse.Ago(ago)} ago. Open Claude Code on Windows to renew it.") with { ExpiredFor = ago });
        }

        // 4. One GET.
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        NativeHttp.SetBearer(request, credentials.AccessToken);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("anthropic-beta", BetaHeader);
        NativeHttp.SetUserAgent(request, "claude-code/" + _options.ClaudeCodeVersion);
        try
        {
            using var deadline = NativeHttp.Deadline(ct, _options.RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                ClaudeUsageResponse? body;
                await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
                    body = await JsonSerializer.DeserializeAsync(stream, NativeJsonContext.Default.ClaudeUsageResponse, deadline.Token).ConfigureAwait(false);
                var result = body == null ? null : Map(body, credentials, _clock.GetUtcNow(), _options.DisplayTimeZone ?? TimeZoneInfo.Local);
                if (result == null)
                    return FromCacheOr(lastGood, Fail(NativeUsageStatus.Failed, status, "Claude usage response had no usage windows.") with { NetworkCalls = 1 });
                result = result with { NetworkCalls = 1 };
                lock (_gate) { _lastGood = result; _blockedUntil = DateTimeOffset.MinValue; }
                return result;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var callNow = _clock.GetUtcNow();
                var candidate = callNow + _options.RateLimitCooldown;
                var retryAfter = NativeHttp.RetryAfter(response, callNow);
                if (retryAfter > candidate) candidate = retryAfter.Value;
                lock (_gate) { if (candidate > _blockedUntil) _blockedUntil = candidate; blockedUntil = _blockedUntil; }
                _logger.LogWarning("Claude usage endpoint answered 429; cooling down until {Until:o}", blockedUntil);
                return FromCacheOr(lastGood, Fail(NativeUsageStatus.RateLimited, status,
                    "Claude usage is rate limited right now; retrying shortly.") with { RetryAfter = blockedUntil, NetworkCalls = 1 });
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Claude usage endpoint answered {Status}", status);
                return FromCacheOr(lastGood, Fail(NativeUsageStatus.Unauthorized, status,
                    $"Claude rejected the Windows sign-in (HTTP {status}). Sign in to Claude Code on Windows again.") with { NetworkCalls = 1 });
            }
            _logger.LogWarning("Claude usage endpoint answered {Status}", status);
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.Failed, status, $"Claude usage request failed (HTTP {status}).") with { NetworkCalls = 1 });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Claude usage request timed out");
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.Failed, null, "Claude usage request timed out.") with { NetworkCalls = 1 });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            _logger.LogWarning("Claude usage request failed ({Type})", ex.GetType().Name);
            return FromCacheOr(lastGood, Fail(NativeUsageStatus.Failed, null, "Claude usage request failed (network or response error).") with { NetworkCalls = 1 });
        }
    }

    /// <summary>
    /// Reads only the "claudeAiOauth" object of Claude Code's credential file; every other key (other tokens) is
    /// skipped unread. Returns null when the file or the object or its access token is missing.
    /// </summary>
    public static async Task<ClaudeCredentials?> ReadCredentialsAsync(string path, CancellationToken ct)
    {
        using var buffer = await NativeCredentialFile.ReadAsync(path, ct).ConfigureAwait(false);
        return buffer == null ? null : ParseCredentials(buffer.Span);
    }

    public static ClaudeCredentials? ParseCredentials(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(NativeCredentialFile.WithoutBom(json), NativeCredentialFile.ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("claudeAiOauth"u8)) { reader.Read(); reader.Skip(); continue; }
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject) return null;
            string? token = null, subscription = null, tier = null;
            DateTimeOffset? expires = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("accessToken"u8)) { reader.Read(); token = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("expiresAt"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var ms) && double.IsFinite(ms) && ms > 0 && ms < 253402300799000)
                        expires = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
                }
                else if (reader.ValueTextEquals("subscriptionType"u8)) { reader.Read(); subscription = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else if (reader.ValueTextEquals("rateLimitTier"u8)) { reader.Read(); tier = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; }
                else { reader.Read(); reader.Skip(); }
            }
            token = token?.Trim();
            return string.IsNullOrEmpty(token) ? null : new ClaudeCredentials(token, expires, subscription, tier);
        }
        return null;
    }

    /// <summary>Maps a usage response like upstream's mapOAuthUsage. Null when no window is present.</summary>
    public static NativeUsageResult? Map(ClaudeUsageResponse usage, ClaudeCredentials credentials, DateTimeOffset now, TimeZoneInfo zone)
    {
        var primary = Window(usage.FiveHour, SessionMinutes, zone);
        var weekly = Window(usage.SevenDay, WeeklyMinutes, zone) ?? Window(usage.SevenDayOAuthApps, WeeklyMinutes, zone);
        var modelSpecific = Window(usage.SevenDaySonnet, WeeklyMinutes, zone) ?? Window(usage.SevenDayOpus, WeeklyMinutes, zone);

        var extras = new List<NativeExtraWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var limit in usage.Limits ?? [])
        {
            if (limit is not { Group: "weekly", Kind: "weekly_scoped" }) continue;
            if (limit.Percent.ValueKind != JsonValueKind.Number || !limit.Percent.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
            var name = NativeParse.NonEmpty(limit.Scope?.Model?.DisplayName);
            if (name == null) continue;
            var modelId = NativeParse.NonEmpty(limit.Scope?.Model?.Id);
            if (NativeParse.Slug(name) == "all-models" || modelId != null && IsAllModelsSlug(NativeParse.Slug(modelId)))
                continue;
            var slug = NativeParse.Slug(modelId ?? name);
            if (slug.Length == 0 || !seen.Add("claude-weekly-scoped-" + slug)) continue;
            var resets = limit.ResetsAt.ValueKind == JsonValueKind.String ? NativeParse.ParseTimestamp(limit.ResetsAt.GetString()) : null;
            extras.Add(new("claude-weekly-scoped-" + slug, name + " only",
                new(percent, WeeklyMinutes, resets, resets is { } r ? ResetDescription(r, zone) : null)));
        }
        if (Window(usage.AnyRoutines, WeeklyMinutes, zone) is { } routines)
            extras.Add(new("claude-routines", "Daily Routines", routines));

        if (primary == null && weekly == null && modelSpecific == null && !extras.Any(e => e.Id.StartsWith("claude-weekly-scoped-", StringComparison.Ordinal)))
            return null;

        return new NativeUsageResult
        {
            Provider = "claude",
            Source = "oauth",
            Status = NativeUsageStatus.Ok,
            LoginMethod = LoginMethod(credentials.SubscriptionType, credentials.RateLimitTier),
            Primary = primary,
            Secondary = weekly,
            Tertiary = modelSpecific,
            ExtraWindows = extras,
            UpdatedAt = now
        };
    }

    /// <summary>Port of upstream ClaudePlan.oauthLoginMethod: "Claude Max 20x", "Claude Pro", ...</summary>
    public static string? LoginMethod(string? subscriptionType, string? rateLimitTier)
    {
        var plan = PlanFromWords(Words(subscriptionType)) ?? PlanFromTier(rateLimitTier);
        if (plan == null) return null;
        if (plan == "Max")
        {
            var words = Words(rateLimitTier);
            var index = words.IndexOf("max");
            if (index >= 0 && index + 1 < words.Count && words[index + 1] is [.., 'x'] multiplier &&
                int.TryParse(multiplier.AsSpan(0, multiplier.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return $"Claude Max {multiplier}";
        }
        return "Claude " + plan;

        static string? PlanFromWords(List<string> words) =>
            words.Contains("max") ? "Max" : words.Contains("pro") ? "Pro" : words.Contains("team") ? "Team" :
            words.Contains("enterprise") ? "Enterprise" : words.Contains("ultra") ? "Ultra" : null;

        static string? PlanFromTier(string? tier)
        {
            var t = (tier ?? string.Empty).Trim().ToLowerInvariant();
            return t.Contains("max") ? "Max" : t.Contains("pro") ? "Pro" : t.Contains("team") ? "Team" : t.Contains("enterprise") ? "Enterprise" : null;
        }

        static List<string> Words(string? text)
        {
            var words = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (var c in (text ?? string.Empty).Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) current.Append(c);
                else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            if (current.Length > 0) words.Add(current.ToString());
            return words;
        }
    }

    private static bool IsAllModelsSlug(string slug) =>
        slug == "all-models" || slug.EndsWith("-all-models", StringComparison.Ordinal);

    private static NativeUsageWindow? Window(ClaudeUsageWindow? window, int minutes, TimeZoneInfo zone)
    {
        if (window?.Utilization is not { } utilization || !double.IsFinite(utilization)) return null;
        var resets = NativeParse.ParseTimestamp(window.ResetsAt);
        return new(utilization, minutes, resets, resets is { } r ? ResetDescription(r, zone) : null);
    }

    private static string ResetDescription(DateTimeOffset resetsAt, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(resetsAt, zone).ToString("MMM d 'at' h:mmtt", CultureInfo.InvariantCulture);

    private static NativeUsageResult Fail(NativeUsageStatus status, int? http, string message) =>
        new() { Provider = "claude", Source = "oauth", Status = status, HttpStatus = http, Message = message };

    /// <summary>
    /// Like the shim: when a last good reading exists, show it (marked cached, windows that have reset since cleared)
    /// and keep the failure's status and message so the app can flag a sign-in problem; otherwise return the failure alone.
    /// </summary>
    private NativeUsageResult FromCacheOr(NativeUsageResult? lastGood, NativeUsageResult failure) =>
        NativeLastGood.FromCacheOr(lastGood, failure, _clock.GetUtcNow());
}
