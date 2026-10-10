using CodexBarWin.Models;

namespace CodexBarWin.Services;

/// <summary>
/// Fetches provider service status and open incident details from public feeds (Claude, OpenAI/Codex, Gemini).
/// </summary>
public interface IProviderIncidentFetcher
{
    /// <summary>
    /// Fetches the latest provider status and open incident details from the provider's public feed.
    /// Returns null on any failure, network error, or timeout. Never throws.
    /// </summary>
    Task<ProviderStatus?> GetIncidentAsync(string provider, CancellationToken ct = default);
}
