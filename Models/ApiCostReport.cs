using System.Text.Json.Serialization;

namespace AgentsWinBar.Models;

public sealed record ApiCostReport
{
    [JsonPropertyName("data")]
    public List<ApiCostBucket>? Data { get; init; }
    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }
    [JsonPropertyName("next_page")]
    public string? NextPage { get; init; }
}

public sealed record ApiCostBucket
{
    [JsonPropertyName("results")]
    public List<ApiCostResult>? Results { get; init; }
}

public sealed record ApiCostResult
{
    [JsonPropertyName("cost_type")]
    public string? CostType { get; init; }
    [JsonPropertyName("model")]
    public string? Model { get; init; }
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; init; }
    [JsonPropertyName("context_window")]
    public string? ContextWindow { get; init; }
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed record ApiUsageReport
{
    [JsonPropertyName("data")] public List<ApiUsageBucket?>? Data { get; init; }
    [JsonPropertyName("has_more")] public bool? HasMore { get; init; }
    [JsonPropertyName("next_page")] public string? NextPage { get; init; }
}

public sealed record ApiUsageBucket
{
    [JsonPropertyName("results")] public List<ApiUsageResult?>? Results { get; init; }
}

public sealed record ApiUsageResult
{
    [JsonPropertyName("api_key_id")] public string? ApiKeyId { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("service_tier")] public string? ServiceTier { get; init; }
    [JsonPropertyName("context_window")] public string? ContextWindow { get; init; }
    [JsonPropertyName("uncached_input_tokens")] public long? UncachedInputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; init; }
    [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; init; }
    [JsonPropertyName("cache_creation")] public ApiCacheCreation? CacheCreation { get; init; }
}

public sealed record ApiCacheCreation
{
    [JsonPropertyName("ephemeral_1h_input_tokens")] public long? Ephemeral1hInputTokens { get; init; }
    [JsonPropertyName("ephemeral_5m_input_tokens")] public long? Ephemeral5mInputTokens { get; init; }
}

public sealed record ApiKeysReport
{
    [JsonPropertyName("data")] public List<ApiOrganizationKey?>? Data { get; init; }
    [JsonPropertyName("has_more")] public bool? HasMore { get; init; }
    [JsonPropertyName("last_id")] public string? LastId { get; init; }
}

public sealed record ApiOrganizationKey
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("partial_key_hint")] public string? PartialKeyHint { get; init; }
    [JsonPropertyName("created_by")] public ApiKeyCreator? CreatedBy { get; init; }
}

public sealed record ApiKeyCreator
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

public sealed record ApiUsersReport
{
    [JsonPropertyName("data")] public List<ApiOrganizationUser?>? Data { get; init; }
    [JsonPropertyName("has_more")] public bool? HasMore { get; init; }
    [JsonPropertyName("last_id")] public string? LastId { get; init; }
}

public sealed record ApiOrganizationUser
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}
