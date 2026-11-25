using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexBarWin.Services.Native;

/// <summary>
/// Source-generated (reflection-free) deserializers for the three provider responses. Unknown members are skipped,
/// and members are tolerant of null.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ClaudeUsageResponse))]
[JsonSerializable(typeof(CodexUsageResponse))]
[JsonSerializable(typeof(CodeAssistResponse))]
[JsonSerializable(typeof(QuotaSummaryResponse))]
[JsonSerializable(typeof(AvailableModelsResponse))]
[JsonSerializable(typeof(GoogleTokenResponse))]
public sealed partial class NativeJsonContext : JsonSerializerContext
{
}

// ---- Claude: GET https://api.anthropic.com/api/oauth/usage ----

public sealed class ClaudeUsageResponse
{
    [JsonPropertyName("five_hour")] public ClaudeUsageWindow? FiveHour { get; set; }
    [JsonPropertyName("seven_day")] public ClaudeUsageWindow? SevenDay { get; set; }
    [JsonPropertyName("seven_day_oauth_apps")] public ClaudeUsageWindow? SevenDayOAuthApps { get; set; }
    [JsonPropertyName("seven_day_sonnet")] public ClaudeUsageWindow? SevenDaySonnet { get; set; }
    [JsonPropertyName("seven_day_opus")] public ClaudeUsageWindow? SevenDayOpus { get; set; }
    // Upstream accepts several names for the Daily Routines window; the first present one wins.
    [JsonPropertyName("seven_day_routines")] public ClaudeUsageWindow? SevenDayRoutines { get; set; }
    [JsonPropertyName("seven_day_claude_routines")] public ClaudeUsageWindow? SevenDayClaudeRoutines { get; set; }
    [JsonPropertyName("claude_routines")] public ClaudeUsageWindow? ClaudeRoutines { get; set; }
    [JsonPropertyName("routines")] public ClaudeUsageWindow? Routines { get; set; }
    [JsonPropertyName("routine")] public ClaudeUsageWindow? Routine { get; set; }
    [JsonPropertyName("seven_day_cowork")] public ClaudeUsageWindow? SevenDayCowork { get; set; }
    [JsonPropertyName("cowork")] public ClaudeUsageWindow? Cowork { get; set; }
    [JsonPropertyName("limits")] public List<ClaudeScopedLimit?>? Limits { get; set; }
    [JsonPropertyName("extra_usage")] public ClaudeExtraUsage? ExtraUsage { get; set; }

    [JsonIgnore]
    public ClaudeUsageWindow? AnyRoutines =>
        SevenDayRoutines ?? SevenDayClaudeRoutines ?? ClaudeRoutines ?? Routines ?? Routine ?? SevenDayCowork ?? Cowork;
}

public sealed class ClaudeUsageWindow
{
    [JsonPropertyName("utilization")] public double? Utilization { get; set; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
}

public sealed class ClaudeScopedLimit
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
    // JsonElement so a value of an unexpected type reads as missing instead of failing the whole response.
    [JsonPropertyName("percent")] public JsonElement Percent { get; set; }
    [JsonPropertyName("resets_at")] public JsonElement ResetsAt { get; set; }
    [JsonPropertyName("scope")] public ClaudeLimitScope? Scope { get; set; }
}

public sealed class ClaudeLimitScope
{
    [JsonPropertyName("model")] public ClaudeLimitModel? Model { get; set; }
}

public sealed class ClaudeLimitModel
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
}

public sealed class ClaudeExtraUsage
{
    [JsonPropertyName("is_enabled")] public bool? IsEnabled { get; set; }
}

// ---- Codex: GET https://chatgpt.com/backend-api/wham/usage ----

public sealed class CodexUsageResponse
{
    [JsonPropertyName("plan_type")] public string? PlanType { get; set; }
    [JsonPropertyName("rate_limit")] public CodexRateLimit? RateLimit { get; set; }
    [JsonPropertyName("additional_rate_limits")] public List<CodexAdditionalRateLimit?>? AdditionalRateLimits { get; set; }
}

public sealed class CodexRateLimit
{
    [JsonPropertyName("primary_window")] public CodexWindow? PrimaryWindow { get; set; }
    [JsonPropertyName("secondary_window")] public CodexWindow? SecondaryWindow { get; set; }
}

public sealed class CodexWindow
{
    [JsonPropertyName("used_percent")] public double? UsedPercent { get; set; }
    [JsonPropertyName("reset_at")] public long? ResetAt { get; set; }
    [JsonPropertyName("limit_window_seconds")] public long? LimitWindowSeconds { get; set; }
}

public sealed class CodexAdditionalRateLimit
{
    [JsonPropertyName("limit_name")] public string? LimitName { get; set; }
    [JsonPropertyName("metered_feature")] public string? MeteredFeature { get; set; }
    [JsonPropertyName("rate_limit")] public CodexRateLimit? RateLimit { get; set; }
}

// ---- Antigravity: POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist ----

public sealed class CodeAssistResponse
{
    [JsonPropertyName("planInfo")] public CodeAssistPlanInfo? PlanInfo { get; set; }
    [JsonPropertyName("currentTier")] public CodeAssistTier? CurrentTier { get; set; }
    [JsonPropertyName("paidTier")] public CodeAssistTier? PaidTier { get; set; }
    [JsonPropertyName("ineligibleTiers")] public List<CodeAssistIneligibleTier?>? IneligibleTiers { get; set; }
    /// <summary>Either a project id string or an object with "id"/"projectId".</summary>
    [JsonPropertyName("cloudaicompanionProject")] public JsonElement CloudAICompanionProject { get; set; }

    [JsonIgnore]
    public string? ProjectId => CloudAICompanionProject.ValueKind switch
    {
        JsonValueKind.String => NativeParse.NonEmpty(CloudAICompanionProject.GetString()),
        JsonValueKind.Object => NativeParse.NonEmpty(
            CloudAICompanionProject.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() :
            CloudAICompanionProject.TryGetProperty("projectId", out var pid) && pid.ValueKind == JsonValueKind.String ? pid.GetString() : null),
        _ => null
    };

    /// <summary>Consumer accounts look unonboarded when the token's OAuth client is not the consumer sign-in client.</summary>
    [JsonIgnore]
    public bool RejectsClientForConsumerTier =>
        CurrentTier == null && ProjectId == null &&
        IneligibleTiers?.Any(t => t?.ReasonCode == "GOOGLE_TOS_NOT_SUPPORTED_BY_CLIENT") == true;
}

public sealed class CodeAssistPlanInfo
{
    [JsonPropertyName("planType")] public string? PlanType { get; set; }
}

public sealed class CodeAssistTier
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class CodeAssistIneligibleTier
{
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
}

// ---- Antigravity: POST https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary ----

public sealed class QuotaSummaryResponse
{
    /// <summary>Int or string; anything other than 0/"ok"/"success"/"0" is an error.</summary>
    [JsonPropertyName("code")] public JsonElement Code { get; set; }
    [JsonPropertyName("response")] public QuotaSummaryPayload? Response { get; set; }
    [JsonPropertyName("summary")] public QuotaSummaryPayload? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("groups")] public List<QuotaSummaryGroup?>? Groups { get; set; }

    [JsonIgnore]
    public List<QuotaSummaryGroup?>? ResolvedGroups => Response?.Groups ?? Summary?.Groups ?? Groups;

    [JsonIgnore]
    public bool CodeIsError => Code.ValueKind switch
    {
        JsonValueKind.Number => !Code.TryGetInt64(out var n) || n != 0,
        JsonValueKind.String => Code.GetString()?.ToLowerInvariant() is not ("ok" or "success" or "0"),
        _ => false
    };
}

public sealed class QuotaSummaryPayload
{
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("groups")] public List<QuotaSummaryGroup?>? Groups { get; set; }
}

public sealed class QuotaSummaryGroup
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("buckets")] public List<QuotaSummaryBucket?>? Buckets { get; set; }
}

public sealed class QuotaSummaryBucket
{
    [JsonPropertyName("bucketId")] public string? BucketId { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("disabled")] public bool? Disabled { get; set; }
    [JsonPropertyName("remainingFraction")] public double? RemainingFraction { get; set; }
    [JsonPropertyName("remaining")] public QuotaSummaryRemaining? Remaining { get; set; }
    [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
    [JsonPropertyName("window")] public string? Window { get; set; }
}

public sealed class QuotaSummaryRemaining
{
    [JsonPropertyName("remainingFraction")] public double? RemainingFraction { get; set; }
    [JsonPropertyName("case")] public string? Case { get; set; }
    [JsonPropertyName("value")] public double? Value { get; set; }

    [JsonIgnore]
    public double? Fraction => RemainingFraction ?? (Case == "remainingFraction" ? Value : null);
}

// ---- Antigravity fallback: POST https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels ----

public sealed class AvailableModelsResponse
{
    [JsonPropertyName("models")] public Dictionary<string, AvailableModel?>? Models { get; set; }
}

public sealed class AvailableModel
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("quotaInfo")] public AvailableModelQuota? QuotaInfo { get; set; }
}

public sealed class AvailableModelQuota
{
    [JsonPropertyName("remainingFraction")] public double? RemainingFraction { get; set; }
    [JsonPropertyName("resetTime")] public string? ResetTime { get; set; }
}

// ---- Google OAuth token refresh (only with the opt-in refresh flag) ----

public sealed class GoogleTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("expires_in")] public double? ExpiresIn { get; set; }
}
