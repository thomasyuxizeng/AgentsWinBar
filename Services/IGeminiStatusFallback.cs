using AgentsWinBar.Models;

namespace AgentsWinBar.Services;

/// <summary>
/// Fallback status fetcher for Gemini / Antigravity when the CLI outputs no status object.
/// </summary>
public interface IGeminiStatusFallback
{
    /// <summary>
    /// Fetches and returns the current provider status for Gemini asynchronously, using cached data if within TTL.
    /// Returns null on any failure, cancellation, or disposal.
    /// </summary>
    Task<ProviderStatus?> GetStatusAsync(CancellationToken ct = default);
}
