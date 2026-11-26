using System.Text.Json.Serialization;

namespace AgentsWinBar.Models;

public enum GeminiApiSpendState
{
    /// <summary>The local spend log was read; totals may still be zero for a quiet month.</summary>
    Ok,
    /// <summary>No spend log exists on this PC. The dashboard hides the section entirely.</summary>
    NoLog,
    /// <summary>The log exists but could not be read.</summary>
    Unavailable
}

/// <summary>One model's share of the month's Gemini API spend.</summary>
public sealed record GeminiApiModelSpend
{
    public string Model { get; init; } = string.Empty;
    public decimal Usd { get; init; }
    public int Calls { get; init; }
}

/// <summary>
/// This calendar month's Gemini API spend (local time), read from the delegate workflow's local spend log.
/// The amounts are estimates from token counts, so they are an upper bound on the bill (cached input is priced in full).
/// </summary>
public sealed record GeminiApiSpendInfo
{
    public GeminiApiSpendState State { get; init; }
    public DateOnly MonthStart { get; init; }
    public DateOnly MonthEnd { get; init; }
    public int DaysLeft { get; init; }
    /// <summary>All spend this month: the sum of the three route amounts below.</summary>
    public decimal TotalUsd { get; init; }
    /// <summary>Calls recorded as going through the AI Studio key (<c>backend</c> = "aistudio").</summary>
    public decimal AiStudioUsd { get; init; }
    /// <summary>Calls recorded as going through Vertex (<c>backend</c> = "vertex").</summary>
    public decimal VertexUsd { get; init; }
    /// <summary>Calls whose route was not recorded (every Gemini CLI job row, and early API rows).</summary>
    public decimal UnrecordedRouteUsd { get; init; }
    public int Calls { get; init; }
    /// <summary>Largest spend first; every model seen this month.</summary>
    public IReadOnlyList<GeminiApiModelSpend> Models { get; init; } = [];
    /// <summary>Time of the newest call this month, when there is one.</summary>
    public DateTime? LastCallUtc { get; init; }
    public DateTime? FetchedAtUtc { get; init; }
    public string? Message { get; init; }
}

/// <summary>Raw JSON-lines DTO for a single Gemini spend log row.</summary>
public sealed record GeminiSpendLogRowDto
{
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    /// <summary>Upper bound: cached input priced at the full input rate.</summary>
    [JsonPropertyName("usd")]
    public decimal? Usd { get; init; }

    /// <summary>Cached input priced at the cache rate; newer rows only. Preferred over <see cref="Usd"/> when present.</summary>
    [JsonPropertyName("usd_cache_aware")]
    public decimal? UsdCacheAware { get; init; }
}

/// <summary>Parsed and validated spend entry with a naive local timestamp.</summary>
public sealed record GeminiSpendEntry
{
    public DateTime Time { get; init; }
    public string Model { get; init; } = string.Empty;
    public string? Backend { get; init; }
    public decimal Usd { get; init; }
}
