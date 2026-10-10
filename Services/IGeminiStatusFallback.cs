using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Fallback status fetcher for Gemini / Antigravity when the CLI outputs no status object.
/// </summary>
public interface IGeminiStatusFallback
{
    /// <summary>
    /// Fetches and returns the current provider status for Gemini asynchronously, using cached data if within TTL.
    /// Returns null on any failure.
    /// </summary>
    Task<ProviderStatus?> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Synchronous accessor for the Gemini provider status, using cached data or executing synchronously.
    /// Returns null on any failure.
    /// </summary>
    ProviderStatus? GetStatus(CancellationToken ct = default);
}
